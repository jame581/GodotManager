using GodotManager.Infrastructure;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class GodotVersionFetcherTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public GodotVersionFetcherTests()
    {
        _fixture = new GodmanTestFixture();
    }

    public void Dispose() => _fixture.Dispose();

    [Fact(Skip = "Integration test - requires network access")]
    public async Task FetchReleases_ReturnsValidReleases()
    {
        var fetcher = new GodotVersionFetcher(_fixture.Paths);
        var releases = await fetcher.FetchReleasesAsync(skipCache: true);

        Assert.NotEmpty(releases);
        Assert.All(releases, r =>
        {
            Assert.NotEmpty(r.Version);
            Assert.True(r.HasStandard || r.HasDotNet);
        });
    }

    [Fact(Skip = "Integration test - requires network access")]
    public async Task FetchReleases_IncludesStableVersions()
    {
        var fetcher = new GodotVersionFetcher(_fixture.Paths);
        var releases = await fetcher.FetchReleasesAsync(skipCache: true);

        var stableReleases = releases.Where(r => r.IsStable).ToList();
        Assert.NotEmpty(stableReleases);

        // Should include well-known stable versions
        Assert.Contains(stableReleases, r => r.Version.StartsWith("4."));
        Assert.Contains(stableReleases, r => r.Version.StartsWith("3."));
    }

    [Fact]
    public async Task FetchReleasesAsync_WhenGitHubReturnsForbidden_ThrowsWithRateLimitHint()
    {
        // 403 from the GitHub releases API is almost always rate limiting, not an
        // outage, so the hint must say so rather than pointing at the network.
        var handler = new SequencedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var fetcher = new GodotVersionFetcher(_fixture.Paths, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<GodmanException>(() => fetcher.FetchReleasesAsync(skipCache: true));

        Assert.NotNull(ex.Hint);
        Assert.Contains("rate limiting", ex.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchReleasesAsync_WhenGitHubIsUnreachable_ThrowsWithGenericNetworkHint()
    {
        // Anything other than a 403 falls back to the generic "check your
        // connection" hint, not the rate-limit-specific one.
        var handler = new SequencedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var fetcher = new GodotVersionFetcher(_fixture.Paths, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<GodmanException>(() => fetcher.FetchReleasesAsync(skipCache: true));

        Assert.NotNull(ex.Hint);
        Assert.Contains("network connection", ex.Hint!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rate limiting", ex.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Skip = "Integration test - requires network access")]
    public async Task FetchReleases_OrderedByPublishedDate()
    {
        var fetcher = new GodotVersionFetcher(_fixture.Paths);
        var releases = await fetcher.FetchReleasesAsync(skipCache: true);

        var dates = releases.Select(r => r.PublishedAt).ToList();
        var sortedDates = dates.OrderByDescending(d => d).ToList();

        Assert.Equal(sortedDates, dates);
    }
}
