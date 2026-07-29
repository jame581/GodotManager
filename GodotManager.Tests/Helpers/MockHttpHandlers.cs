using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GodotManager.Tests.Helpers;

/// <summary>
/// Mock HTTP handler that serves file bytes from a local archive path.
/// Optionally sets a Content-Disposition filename.
/// </summary>
internal class MockFileHttpHandler : HttpMessageHandler
{
    private readonly string _archivePath;
    private readonly string? _downloadedFileName;

    public MockFileHttpHandler(string archivePath, string? downloadedFileName = null)
    {
        _archivePath = archivePath;
        _downloadedFileName = downloadedFileName;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var fileBytes = await File.ReadAllBytesAsync(_archivePath, cancellationToken);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(fileBytes)
        };
        response.Content.Headers.ContentLength = fileBytes.Length;

        if (!string.IsNullOrWhiteSpace(_downloadedFileName))
        {
            response.Content.Headers.ContentDisposition =
                new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                {
                    FileName = _downloadedFileName
                };
        }

        return response;
    }
}

/// <summary>
/// Mock HTTP handler that serves a fixed JSON string response.
/// </summary>
internal class MockJsonHttpHandler : HttpMessageHandler
{
    private readonly string _jsonResponse;

    public MockJsonHttpHandler(string jsonResponse)
    {
        _jsonResponse = jsonResponse;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_jsonResponse, Encoding.UTF8, "application/json")
        };

        return Task.FromResult(response);
    }
}

/// <summary>
/// Mock HTTP handler that serves a fixed JSON string and tracks call count.
/// Useful for verifying caching behavior.
/// </summary>
internal class TrackingJsonHttpHandler : HttpMessageHandler
{
    private readonly string _jsonResponse;
    private int _callCount;

    public int CallCount => _callCount;

    public TrackingJsonHttpHandler(string jsonResponse)
    {
        _jsonResponse = jsonResponse;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_jsonResponse, Encoding.UTF8, "application/json")
        };

        return Task.FromResult(response);
    }
}

/// <summary>
/// Shared HTTP Range semantics for the mock handlers below. A single
/// implementation is used everywhere a mock needs to serve a byte payload
/// with Range support, so Range/If-Range correctness only has to be gotten
/// right (and tested) once.
/// </summary>
internal static class RangeAwareContent
{
    /// <summary>
    /// Builds a response for <paramref name="content"/> honoring the request's
    /// Range and If-Range headers:
    /// - No Range header, or <paramref name="honorRange"/> is false: 200 with the full body.
    /// - Range present but If-Range names an ETag that doesn't match <paramref name="etag"/>: 200 with the full body
    ///   (the client's cached range would be spliced onto a different resource otherwise).
    /// - Range present and satisfiable: 206 with Content-Range and the requested slice.
    /// - Range present but starts at or past the end of the content: 416.
    ///
    /// <paramref name="misalignedRangeStart"/> models a range-rewriting proxy: when set,
    /// the 206 is served from that offset instead of the requested one, with a matching
    /// Content-Range. Body and Content-Range stay consistent with each other and only
    /// disagree with the request, which is what a client must detect. Defaults to null,
    /// i.e. the correct, aligned answer.
    /// </summary>
    public static HttpResponseMessage BuildResponse(
        byte[] content,
        HttpRequestMessage request,
        string? etag,
        bool honorRange = true,
        int? misalignedRangeStart = null)
    {
        var range = request.Headers.Range;

        if (range is null || !honorRange || !IfRangeIsSatisfied(request, etag))
        {
            return FullResponse(content, etag);
        }

        var requested = range.Ranges.First();
        var from = (int)(requested.From ?? 0);

        if (from >= content.Length)
        {
            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        }

        var to = requested.To.HasValue ? Math.Min((int)requested.To.Value, content.Length - 1) : content.Length - 1;
        var servedFrom = Math.Clamp(misalignedRangeStart ?? from, 0, to);
        var slice = content.Skip(servedFrom).Take(to - servedFrom + 1).ToArray();

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice)
        };
        response.Content.Headers.ContentLength = slice.Length;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(servedFrom, to, content.Length);
        ApplyETag(response, etag);

        return response;
    }

    private static HttpResponseMessage FullResponse(byte[] content, string? etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };
        response.Content.Headers.ContentLength = content.Length;
        ApplyETag(response, etag);

        return response;
    }

    private static void ApplyETag(HttpResponseMessage response, string? etag)
    {
        if (!string.IsNullOrWhiteSpace(etag))
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }
    }

    /// <summary>
    /// If-Range is absent: the Range request is unconditional, so it's satisfied.
    /// If-Range names an ETag: satisfied only when it matches the resource's current ETag.
    /// If-Range names a date (last-modified based validation) or the resource has no ETag
    /// to compare against: treated conservatively as not satisfied, falling back to a full 200 —
    /// these mocks only model ETag-based validation.
    /// </summary>
    private static bool IfRangeIsSatisfied(HttpRequestMessage request, string? etag)
    {
        var ifRange = request.Headers.IfRange;
        if (ifRange is null)
        {
            return true;
        }

        if (ifRange.EntityTag is null || string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        return ifRange.EntityTag.Equals(new EntityTagHeaderValue(etag));
    }
}

/// <summary>
/// Serves a fixed byte payload with HTTP Range support. Set honorRange: false
/// to simulate a server that ignores Range and returns 200 with the full body,
/// or misalignedRangeStart to simulate a proxy that answers a 206 for a range
/// other than the one requested.
/// </summary>
internal sealed class MockRangeHttpHandler : HttpMessageHandler
{
    private readonly byte[] _content;
    private readonly string? _fileName;
    private readonly string? _etag;
    private readonly bool _honorRange;
    private readonly int? _misalignedRangeStart;

    public List<string?> ReceivedRangeHeaders { get; } = new();

    public MockRangeHttpHandler(
        byte[] content,
        string? fileName = null,
        string? etag = null,
        bool honorRange = true,
        int? misalignedRangeStart = null)
    {
        _content = content;
        _fileName = fileName;
        _etag = etag;
        _honorRange = honorRange;
        _misalignedRangeStart = misalignedRangeStart;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ReceivedRangeHeaders.Add(request.Headers.Range?.ToString());

        var response = RangeAwareContent.BuildResponse(_content, request, _etag, _honorRange, _misalignedRangeStart);

        if (!string.IsNullOrWhiteSpace(_fileName) && response.Content is not null)
        {
            response.Content.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("attachment") { FileName = _fileName };
        }

        return Task.FromResult(response);
    }
}

/// <summary>
/// Returns a scripted sequence of responses, one per call. The final entry is
/// reused if more calls arrive than were scripted.
/// </summary>
internal sealed class SequencedHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _responses;
    private int _callCount;

    public int CallCount => _callCount;

    public SequencedHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        _responses = responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _callCount) - 1;
        return Task.FromResult(_responses[Math.Min(index, _responses.Length - 1)](request));
    }
}

/// <summary>
/// Serves an archive payload plus a SHA512-SUMS.txt listing it. Requests whose
/// URI contains sumsUrlFragment get the sums file; everything else gets the
/// archive. Pass overrideHash to simulate a mismatch.
///
/// Both the archive and the sums file are served through the same Range-aware
/// logic as <see cref="MockRangeHttpHandler"/> (via <see cref="RangeAwareContent"/>),
/// each with its own content-derived ETag, so a resumed archive download
/// followed by verification against the sums file exercises real Range/If-Range
/// semantics rather than always getting a full 200 body.
/// </summary>
internal sealed class MockSumsHttpHandler : HttpMessageHandler
{
    private readonly byte[] _archiveContent;
    private readonly string _archiveFileName;
    private readonly string _sumsUrlFragment;
    private readonly byte[] _sumsContent;
    private readonly string _archiveETag;
    private readonly string _sumsETag;
    private int _sumsRequestCount;

    public int SumsRequestCount => _sumsRequestCount;

    /// <summary>
    /// Range headers seen on archive requests only, so a test can assert that a
    /// transfer genuinely resumed rather than quietly restarting from zero.
    /// </summary>
    public List<string?> ReceivedArchiveRangeHeaders { get; } = new();

    public MockSumsHttpHandler(byte[] archiveContent, string archiveFileName, string sumsUrlFragment, string? overrideHash = null)
    {
        _archiveContent = archiveContent;
        _archiveFileName = archiveFileName;
        _sumsUrlFragment = sumsUrlFragment;

        var actualHash = Convert.ToHexStringLower(SHA512.HashData(archiveContent));
        var publishedHash = overrideHash ?? actualHash;
        _sumsContent = Encoding.UTF8.GetBytes($"{publishedHash}  {_archiveFileName}\n");

        // ETags identify each resource's current bytes, independent of whatever
        // hash the (possibly tampered) sums file claims.
        _archiveETag = $"\"{actualHash}\"";
        _sumsETag = $"\"{Convert.ToHexStringLower(SHA512.HashData(_sumsContent))}\"";
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.ToString().Contains(_sumsUrlFragment, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _sumsRequestCount);
            return Task.FromResult(RangeAwareContent.BuildResponse(_sumsContent, request, _sumsETag));
        }

        ReceivedArchiveRangeHeaders.Add(request.Headers.Range?.ToString());

        var response = RangeAwareContent.BuildResponse(_archiveContent, request, _archiveETag);

        if (response.Content is not null)
        {
            response.Content.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("attachment") { FileName = _archiveFileName };
        }

        return Task.FromResult(response);
    }
}
