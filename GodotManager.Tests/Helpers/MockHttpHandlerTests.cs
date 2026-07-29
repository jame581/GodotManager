using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.Helpers;

public class MockHttpHandlerTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("0123456789abcdef");

    [Fact]
    public async Task MockRangeHttpHandler_WithoutRange_ReturnsWholeBody()
    {
        var handler = new MockRangeHttpHandler(Payload, fileName: "thing.zip", etag: "\"v1\"");
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://test.invalid/thing.zip");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Payload, bytes);
        Assert.Equal("thing.zip", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("\"v1\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task MockRangeHttpHandler_WithRange_ReturnsPartialContent()
    {
        var handler = new MockRangeHttpHandler(Payload);
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.invalid/thing.zip");
        request.Headers.Range = new RangeHeaderValue(10, null);

        var response = await client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(Payload.Skip(10).ToArray(), bytes);
        Assert.Equal("bytes=10-", handler.ReceivedRangeHeaders.Single());
    }

    [Fact]
    public async Task MockRangeHttpHandler_WhenNotHonoringRange_ReturnsOkWithWholeBody()
    {
        var handler = new MockRangeHttpHandler(Payload, honorRange: false);
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.invalid/thing.zip");
        request.Headers.Range = new RangeHeaderValue(10, null);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Payload, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MockRangeHttpHandler_WithRangePastEnd_Returns416()
    {
        var handler = new MockRangeHttpHandler(Payload);
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.invalid/thing.zip");
        request.Headers.Range = new RangeHeaderValue(999, null);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    [Fact]
    public async Task MockRangeHttpHandler_WithMismatchedIfRange_ReturnsFullBody()
    {
        var handler = new MockRangeHttpHandler(Payload, etag: "\"v1\"");
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.invalid/thing.zip");
        request.Headers.Range = new RangeHeaderValue(10, null);
        request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"stale\""));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Payload, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MockRangeHttpHandler_WithMatchingIfRange_ReturnsPartialContent()
    {
        var handler = new MockRangeHttpHandler(Payload, etag: "\"v1\"");
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.invalid/thing.zip");
        request.Headers.Range = new RangeHeaderValue(10, null);
        request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"v1\""));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(Payload.Skip(10).ToArray(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SequencedHttpHandler_ReturnsResponsesInOrder()
    {
        var handler = new SequencedHttpHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        using var client = new HttpClient(handler);

        var first = await client.GetAsync("https://test.invalid/thing.zip");
        var second = await client.GetAsync("https://test.invalid/thing.zip");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task MockSumsHttpHandler_ServesArchiveAndMatchingSums()
    {
        var handler = new MockSumsHttpHandler(Payload, "thing.zip", "SHA512-SUMS.txt");
        using var client = new HttpClient(handler);

        var archive = await client.GetAsync("https://test.invalid/thing.zip");
        var sums = await client.GetStringAsync("https://test.invalid/SHA512-SUMS.txt");

        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Contains("thing.zip", sums);
        Assert.Equal(1, handler.SumsRequestCount);
    }

    [Fact]
    public async Task MockSumsHttpHandler_ArchiveRequest_HonorsRange()
    {
        var handler = new MockSumsHttpHandler(Payload, "thing.zip", "SHA512-SUMS.txt");
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.invalid/thing.zip");
        request.Headers.Range = new RangeHeaderValue(10, null);

        var response = await client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(Payload.Skip(10).ToArray(), bytes);
        Assert.NotNull(response.Content.Headers.ContentRange);
        Assert.Equal(Payload.Length, response.Content.Headers.ContentRange!.Length);
    }

    [Fact]
    public async Task MockSumsHttpHandler_ArchiveRequest_WithRangePastEnd_Returns416()
    {
        var handler = new MockSumsHttpHandler(Payload, "thing.zip", "SHA512-SUMS.txt");
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.invalid/thing.zip");
        request.Headers.Range = new RangeHeaderValue(Payload.Length + 1, null);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    [Fact]
    public async Task MockSumsHttpHandler_ArchiveRequest_WithStaleIfRange_ReturnsFullBodyNot206()
    {
        var handler = new MockSumsHttpHandler(Payload, "thing.zip", "SHA512-SUMS.txt");
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.invalid/thing.zip");
        request.Headers.Range = new RangeHeaderValue(10, null);
        request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"not-the-real-etag\""));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Payload, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MockSumsHttpHandler_ArchiveRequest_WithMatchingIfRange_ReturnsPartialContent()
    {
        var handler = new MockSumsHttpHandler(Payload, "thing.zip", "SHA512-SUMS.txt");
        using var client = new HttpClient(handler);

        // Discover the handler's real ETag the same way a well-behaved client would:
        // from a prior, unconditional response.
        var initial = await client.GetAsync("https://test.invalid/thing.zip");
        var etag = initial.Headers.ETag;
        Assert.NotNull(etag);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.invalid/thing.zip");
        request.Headers.Range = new RangeHeaderValue(10, null);
        request.Headers.IfRange = new RangeConditionHeaderValue(etag!);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(Payload.Skip(10).ToArray(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MockSumsHttpHandler_WithOverrideHash_SumsFileReflectsOverride_NotActualContentHash()
    {
        const string bogusHash = "deadbeef";
        var handler = new MockSumsHttpHandler(Payload, "thing.zip", "SHA512-SUMS.txt", overrideHash: bogusHash);
        using var client = new HttpClient(handler);

        var sums = await client.GetStringAsync("https://test.invalid/SHA512-SUMS.txt");

        Assert.Contains(bogusHash, sums);
    }
}
