using GodotManager.Config;
using GodotManager.Services;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class GodotVersionFetcherCacheTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;
    private readonly string? _savedHome;

    private const string GitHubReleaseJson = """
        [{
          "tag_name": "4.5.1-stable",
          "name": "Godot 4.5.1",
          "draft": false,
          "prerelease": false,
          "published_at": "2025-01-15T00:00:00Z",
          "assets": [
            {"name": "Godot_v4.5.1-stable_win64.exe.zip", "browser_download_url": "http://example.com/godot.zip"}
          ]
        }]
        """;

    public GodotVersionFetcherCacheTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "godman-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _savedHome = Environment.GetEnvironmentVariable("GODMAN_HOME");
        Environment.SetEnvironmentVariable("GODMAN_HOME", _tempRoot);

        _paths = new AppPaths();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GODMAN_HOME", _savedHome);
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task FetchReleasesAsync_FirstCall_FetchesFromGitHubAndCachesResult()
    {
        // Arrange
        var handler = new TrackingHttpMessageHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_paths, httpClient);
        var cacheFile = Path.Combine(_paths.ConfigDirectory, "releases-cache.json");

        // Act
        var releases = await fetcher.FetchReleasesAsync();

        // Assert
        Assert.Single(releases);
        Assert.Equal("4.5.1", releases[0].Version);
        Assert.True(releases[0].IsStable);
        Assert.True(releases[0].HasStandard);
        Assert.True(File.Exists(cacheFile), "Cache file should be created after first fetch");
        Assert.True(handler.CallCount >= 1, "HTTP handler should have been called at least once");
    }

    [Fact]
    public async Task FetchReleasesAsync_SecondCall_UsesCachedData()
    {
        // Arrange
        var handler = new TrackingHttpMessageHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_paths, httpClient);

        // Act
        var firstResult = await fetcher.FetchReleasesAsync();
        var callsAfterFirst = handler.CallCount;

        var secondResult = await fetcher.FetchReleasesAsync();
        var callsAfterSecond = handler.CallCount;

        // Assert
        Assert.Equal(firstResult.Count, secondResult.Count);
        Assert.Equal(firstResult[0].Version, secondResult[0].Version);
        Assert.Equal(callsAfterFirst, callsAfterSecond);
    }

    [Fact]
    public async Task FetchReleasesAsync_WithSkipCache_AlwaysFetchesFromGitHub()
    {
        // Arrange
        var handler = new TrackingHttpMessageHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_paths, httpClient);

        // Act - first call populates cache
        await fetcher.FetchReleasesAsync();
        var callsAfterFirst = handler.CallCount;

        // Act - second call with skipCache should hit HTTP again
        var releases = await fetcher.FetchReleasesAsync(skipCache: true);
        var callsAfterSecond = handler.CallCount;

        // Assert
        Assert.Single(releases);
        Assert.True(callsAfterSecond > callsAfterFirst, "HTTP handler should be called again when skipCache is true");
    }

    [Fact]
    public async Task FetchReleasesAsync_WithExpiredCache_FetchesFromGitHub()
    {
        // Arrange
        var handler = new TrackingHttpMessageHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_paths, httpClient);

        // Populate cache via a first fetch
        await fetcher.FetchReleasesAsync();
        var callsAfterFirst = handler.CallCount;

        // Expire the cache by setting LastWriteTime to 25 hours ago
        var cacheFile = Path.Combine(_paths.ConfigDirectory, "releases-cache.json");
        File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow.AddHours(-25));

        // Act - this should detect expired cache and fetch from HTTP
        var releases = await fetcher.FetchReleasesAsync();
        var callsAfterExpired = handler.CallCount;

        // Assert
        Assert.Single(releases);
        Assert.True(callsAfterExpired > callsAfterFirst, "HTTP handler should be called again when cache is expired (>24h)");
    }

    [Fact]
    public void GetCacheAge_WithNoCache_ReturnsNull()
    {
        // Arrange
        var handler = new TrackingHttpMessageHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_paths, httpClient);

        // Act
        var cacheAge = fetcher.GetCacheAge();

        // Assert
        Assert.Null(cacheAge);
    }

    [Fact]
    public async Task GetCacheAge_WithCache_ReturnsTimestamp()
    {
        // Arrange
        var handler = new TrackingHttpMessageHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_paths, httpClient);

        // Populate cache
        await fetcher.FetchReleasesAsync();

        // Act
        var cacheAge = fetcher.GetCacheAge();

        // Assert
        Assert.NotNull(cacheAge);
        var elapsed = DateTimeOffset.UtcNow - cacheAge.Value;
        Assert.True(elapsed.TotalSeconds < 30, "Cache timestamp should be very recent");
    }
}

/// <summary>
/// Mock HTTP handler that returns a fixed JSON response and tracks the number of calls made.
/// </summary>
internal class TrackingHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseJson;
    private int _callCount;

    public int CallCount => _callCount;

    public TrackingHttpMessageHandler(string responseJson)
    {
        _responseJson = responseJson;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json")
        };

        return Task.FromResult(response);
    }
}
