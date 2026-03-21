using System.IO;
using System.Net;
using System.Net.Http;
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
