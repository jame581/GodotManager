using GodotManager.Services;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class GodotVersionFetcherCacheTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

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
        _fixture = new GodmanTestFixture();
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public async Task FetchReleasesAsync_FirstCall_FetchesFromGitHubAndCachesResult()
    {
        // Arrange
        var handler = new TrackingJsonHttpHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_fixture.Paths, httpClient);
        var cacheFile = Path.Combine(_fixture.Paths.ConfigDirectory, "releases-cache.json");

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
        var handler = new TrackingJsonHttpHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_fixture.Paths, httpClient);

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
        var handler = new TrackingJsonHttpHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_fixture.Paths, httpClient);

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
        var handler = new TrackingJsonHttpHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_fixture.Paths, httpClient);

        // Populate cache via a first fetch
        await fetcher.FetchReleasesAsync();
        var callsAfterFirst = handler.CallCount;

        // Expire the cache by setting LastWriteTime to 25 hours ago
        var cacheFile = Path.Combine(_fixture.Paths.ConfigDirectory, "releases-cache.json");
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
        var handler = new TrackingJsonHttpHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_fixture.Paths, httpClient);

        // Act
        var cacheAge = fetcher.GetCacheAge();

        // Assert
        Assert.Null(cacheAge);
    }

    [Fact]
    public async Task GetCacheAge_WithCache_ReturnsTimestamp()
    {
        // Arrange
        var handler = new TrackingJsonHttpHandler(GitHubReleaseJson);
        var httpClient = new HttpClient(handler);
        var fetcher = new GodotVersionFetcher(_fixture.Paths, httpClient);

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
