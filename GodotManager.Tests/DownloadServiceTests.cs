using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using GodotManager.Infrastructure;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using Xunit;

namespace GodotManager.Tests;

public class DownloadServiceTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    // Two delays => three attempts.
    private static readonly TimeSpan[] NoBackoff = [TimeSpan.Zero, TimeSpan.Zero];

    // Larger than the 80 KB read buffer, so a resumed transfer takes more than
    // one read and the progress assertions below are not vacuous.
    private static readonly byte[] Payload = CreatePayload(200_000);

    private static readonly Uri TestUri = new("https://test.invalid/Godot_v4.5.1-stable_linux.x86_64.zip");

    public DownloadServiceTests() => _fixture = new GodmanTestFixture();

    public void Dispose() => _fixture.Dispose();

    private static byte[] CreatePayload(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }
        return bytes;
    }

    private DownloadService CreateService(HttpMessageHandler handler) =>
        new(_fixture.Paths, new HttpClient(handler), diagnostics: null, backoff: NoBackoff);

    private string CacheFile(string extension) =>
        Path.Combine(_fixture.Paths.DownloadCacheDirectory, DownloadService.ComputeCacheKey(TestUri) + extension);

    private async Task SeedPartialAsync(
        int bytes,
        string archiveName = "Godot_v4.5.1-stable_linux.x86_64.zip",
        string? etag = null,
        bool staleContent = false)
    {
        // staleContent seeds bytes that are NOT a prefix of Payload, standing in for
        // a partial captured from a different version of the resource. Appending to
        // it therefore produces a corrupt archive that assertions can detect.
        var partial = staleContent
            ? Enumerable.Repeat((byte)0xEE, bytes).ToArray()
            : Payload.Take(bytes).ToArray();

        await File.WriteAllBytesAsync(CacheFile(".part"), partial);
        var etagJson = etag is null ? "null" : JsonSerializer.Serialize(etag);
        await File.WriteAllTextAsync(
            CacheFile(".json"),
            $$"""{"Url":"{{TestUri.AbsoluteUri}}","ArchiveName":"{{archiveName}}","ETag":{{etagJson}}}""");
    }

    [Fact]
    public async Task DownloadAsync_FreshDownload_WritesCompleteArchive()
    {
        var service = CreateService(new MockRangeHttpHandler(Payload, fileName: "Godot_v4.5.1-stable_linux.x86_64.zip"));

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        Assert.Equal("Godot_v4.5.1-stable_linux.x86_64.zip", outcome.ArchiveName);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(outcome.FilePath));
        Assert.EndsWith(".archive", outcome.FilePath);
        Assert.Empty(Directory.GetFiles(_fixture.Paths.DownloadCacheDirectory, "*.part"));
        // The sidecar describes an in-flight transfer and must not outlive it.
        Assert.Empty(Directory.GetFiles(_fixture.Paths.DownloadCacheDirectory, "*.json"));
    }

    [Fact]
    public async Task DownloadAsync_WithExistingPartial_SendsRangeAndCompletesFile()
    {
        await SeedPartialAsync(50_000);
        var handler = new MockRangeHttpHandler(Payload);
        var service = CreateService(handler);

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        Assert.Equal("bytes=50000-", handler.ReceivedRangeHeaders.Single());
        Assert.Equal(Payload, await File.ReadAllBytesAsync(outcome.FilePath));
    }

    [Fact]
    public async Task DownloadAsync_AfterResume_HashCoversTheWholePayload()
    {
        // The subtlest code in the release: SHA-512 state cannot cross a process
        // boundary, so resumed bytes are re-read from disk to rebuild it. Without
        // this assertion a rehash bug ships green and surfaces later as a spurious
        // checksum mismatch on every real resumed download.
        await SeedPartialAsync(50_000);
        var service = CreateService(new MockRangeHttpHandler(Payload));

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(Payload)), outcome.Sha512);
    }

    [Fact]
    public async Task DownloadAsync_OnResumeWithoutContentDisposition_KeepsTheRecordedArchiveName()
    {
        // A resumed 206 need not carry Content-Disposition, and the name the server
        // gave on the first leg is the one install folders are built from. Deriving
        // it from the request URI here would silently rename every resumed install,
        // so the sidecar's recorded name is read back. The seeded name deliberately
        // differs from TestUri's filename, or this assertion would be vacuous.
        await SeedPartialAsync(50_000, archiveName: "Godot_v4.5.1-stable_mono_linux_x86_64.zip");
        var service = CreateService(new MockRangeHttpHandler(Payload));

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        Assert.Equal("Godot_v4.5.1-stable_mono_linux_x86_64.zip", outcome.ArchiveName);
    }

    [Fact]
    public async Task DownloadAsync_WhenETagMatches_ResumesFromThePartial()
    {
        await SeedPartialAsync(50_000, etag: "\"v1\"");
        var handler = new MockRangeHttpHandler(Payload, etag: "\"v1\"");
        var service = CreateService(handler);

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        // A malformed If-Range would make the mock answer 200 instead of 206.
        Assert.Equal("bytes=50000-", handler.ReceivedRangeHeaders.Single());
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(Payload)), outcome.Sha512);
    }

    [Fact]
    public async Task DownloadAsync_WhenETagChanged_DiscardsPartialAndRestarts()
    {
        // The mock only answers 200 to a Range request when If-Range names a stale
        // ETag; without If-Range it would answer 206 and the 0xEE bytes from the
        // superseded resource would be spliced under a 150 KB tail. The payload and
        // hash assertions are what catch that splice.
        await SeedPartialAsync(50_000, etag: "\"old\"", staleContent: true);
        var handler = new MockRangeHttpHandler(Payload, etag: "\"new\"");
        var service = CreateService(handler);

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        Assert.Equal("bytes=50000-", handler.ReceivedRangeHeaders.Single());
        Assert.Equal(Payload, await File.ReadAllBytesAsync(outcome.FilePath));
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(Payload)), outcome.Sha512);
    }

    [Fact]
    public async Task DownloadAsync_When206StartsAtADifferentOffsetThanRequested_RestartsFromZero()
    {
        // A range-rewriting proxy answers "bytes=50000-" with the whole body and
        // "Content-Range: bytes 0-199999/200000". Inferring the body's start from the
        // 206 status alone would append 200 KB onto the 50 KB prefix and hand back a
        // 250 KB archive, hashed and renamed, with no error anywhere. The stale prefix
        // is 0xEE, so the payload and hash assertions detect the splice; equality with
        // Payload is also what proves the .part was truncated, not appended to.
        await SeedPartialAsync(50_000, staleContent: true);
        var handler = new MockRangeHttpHandler(Payload, misalignedRangeStart: 0);
        var service = CreateService(handler);

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        Assert.Equal("bytes=50000-", handler.ReceivedRangeHeaders.Single());
        Assert.Equal(Payload, await File.ReadAllBytesAsync(outcome.FilePath));
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(Payload)), outcome.Sha512);
    }

    [Fact]
    public async Task DownloadAsync_WhenServerIgnoresRange_TruncatesAndStillSucceeds()
    {
        await SeedPartialAsync(50_000, staleContent: true);
        var service = CreateService(new MockRangeHttpHandler(Payload, honorRange: false));

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        // Not 250_000 bytes: the stale partial must be discarded, not appended to.
        Assert.Equal(Payload, await File.ReadAllBytesAsync(outcome.FilePath));
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(Payload)), outcome.Sha512);
    }

    [Fact]
    public async Task DownloadAsync_WhenPartialLongerThanResource_RestartsFromZero()
    {
        await File.WriteAllBytesAsync(CacheFile(".part"), new byte[Payload.Length + 5_000]);
        await File.WriteAllTextAsync(
            CacheFile(".json"),
            $$"""{"Url":"{{TestUri.AbsoluteUri}}","ArchiveName":"x.zip","ETag":null}""");

        var service = CreateService(new MockRangeHttpHandler(Payload));

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(outcome.FilePath));
    }

    [Fact]
    public async Task DownloadAsync_WithPartialButNoSidecar_RestartsAndSendsNoRange()
    {
        // Without a sidecar there is no ETag, so a Range request would have no
        // If-Range guard and could splice two different resources together.
        await File.WriteAllBytesAsync(CacheFile(".part"), Payload.Take(50_000).ToArray());
        var handler = new MockRangeHttpHandler(Payload);
        var service = CreateService(handler);

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        Assert.Null(handler.ReceivedRangeHeaders.Single());
        Assert.Equal(Payload, await File.ReadAllBytesAsync(outcome.FilePath));
    }

    [Fact]
    public async Task DownloadAsync_WithSidecarForDifferentUrl_RestartsAndSendsNoRange()
    {
        await File.WriteAllBytesAsync(CacheFile(".part"), Payload.Take(50_000).ToArray());
        await File.WriteAllTextAsync(
            CacheFile(".json"),
            """{"Url":"https://test.invalid/something-else.zip","ArchiveName":"x.zip","ETag":null}""");

        var handler = new MockRangeHttpHandler(Payload);
        var service = CreateService(handler);

        await service.DownloadAsync(TestUri, progress: null);

        Assert.Null(handler.ReceivedRangeHeaders.Single());
    }

    [Fact]
    public async Task DownloadAsync_WithUnsolicited206AndNoPartial_TreatsItAsFreshTransfer()
    {
        // FileMode.Open on a nonexistent .part raises FileNotFoundException, which
        // is an IOException and therefore classified retryable — burning every
        // attempt on a guaranteed failure. The Content-Range is well formed and
        // starts at 0, so the offset check cannot stand in for the "is there
        // anything on disk to append to" check: only the latter rejects this.
        var handler = new SequencedHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Payload)
            };
            response.Content.Headers.ContentLength = Payload.Length;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, Payload.Length - 1, Payload.Length);
            return response;
        });

        var service = CreateService(handler);

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(outcome.FilePath));
    }

    [Fact]
    public async Task DownloadAsync_RetriesTransientFailuresThenSucceeds()
    {
        var handler = new SequencedHttpHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            _ =>
            {
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
                ok.Content.Headers.ContentLength = Payload.Length;
                return ok;
            });

        var service = CreateService(handler);

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        Assert.Equal(3, handler.CallCount);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(outcome.FilePath));
    }

    [Fact]
    public async Task DownloadAsync_WhenTransientFailurePersists_StopsAfterThreeAttempts()
    {
        // Two backoff delays are the gaps BETWEEN attempts, so they buy three
        // attempts, not two. An off-by-one here silently halves or doubles the
        // work done before a user sees a failure.
        var handler = new SequencedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var service = CreateService(handler);

        await Assert.ThrowsAsync<TransientDownloadException>(() => service.DownloadAsync(TestUri, progress: null));

        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task DownloadAsync_DoesNotRetryNotFound()
    {
        // Must surface as GodmanException, not HttpRequestException: the latter is
        // in the retryable set, so EnsureSuccessStatusCode would burn all attempts.
        var handler = new SequencedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = CreateService(handler);

        await Assert.ThrowsAsync<GodmanException>(() => service.DownloadAsync(TestUri, progress: null));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DownloadAsync_ReportsProgressAccountingForResumedBytes()
    {
        await SeedPartialAsync(100_000);   // half of the 200 KB payload
        var service = CreateService(new MockRangeHttpHandler(Payload));
        var reported = new List<double>();

        await service.DownloadAsync(TestUri, reported.Add);

        // Multiple reads happen, so an implementation that counted only newly
        // received bytes would report values well below 50 early on.
        Assert.True(reported.Count > 2, $"expected several progress reports, got {reported.Count}");
        Assert.All(reported, p => Assert.True(p >= 50d, $"progress {p} ignores the 100000 resumed bytes"));
        Assert.Equal(100d, reported.Last());
    }

    [Fact]
    public async Task DownloadAsync_DoesNotReuseAStaleArchiveFromAPreviousRun()
    {
        // This is not a content cache: an archive left behind by an earlier run is
        // re-fetched, never handed back as though this run had produced it.
        await File.WriteAllTextAsync(CacheFile(".archive"), "stale content from a failed install");
        var service = CreateService(new MockRangeHttpHandler(Payload));

        var outcome = await service.DownloadAsync(TestUri, progress: null);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(outcome.FilePath));
    }

    [Fact]
    public async Task DownloadAsync_DeletesTheStaleArchiveEvenWhenTheRunFails()
    {
        // The discard happens up front, so stale bytes cannot survive a failed run
        // and be mistaken for a good archive later. Relying on the final rename to
        // overwrite them would leave them in place on every failure path.
        await File.WriteAllTextAsync(CacheFile(".archive"), "stale content from a failed install");
        var service = CreateService(new SequencedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<GodmanException>(() => service.DownloadAsync(TestUri, progress: null));

        Assert.False(File.Exists(CacheFile(".archive")));
    }

    [Fact]
    public async Task DeleteCacheEntry_RemovesTheArchive()
    {
        var service = CreateService(new MockRangeHttpHandler(Payload));
        var outcome = await service.DownloadAsync(TestUri, progress: null);

        service.DeleteCacheEntry(outcome.FilePath);

        Assert.False(File.Exists(outcome.FilePath));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.DownloadCacheDirectory));
    }
}
