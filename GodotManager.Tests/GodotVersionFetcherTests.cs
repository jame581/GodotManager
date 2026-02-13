using GodotManager.Services;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class GodotVersionFetcherTests
{
    [Fact(Skip = "Integration test - requires network access")]
    public async Task FetchReleases_ReturnsValidReleases()
    {
        var fetcher = new GodotVersionFetcher();
        var releases = await fetcher.FetchReleasesAsync();

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
        var fetcher = new GodotVersionFetcher();
        var releases = await fetcher.FetchReleasesAsync();

        var stableReleases = releases.Where(r => r.IsStable).ToList();
        Assert.NotEmpty(stableReleases);

        // Should include well-known stable versions
        Assert.Contains(stableReleases, r => r.Version.StartsWith("4."));
        Assert.Contains(stableReleases, r => r.Version.StartsWith("3."));
    }

    [Fact(Skip = "Integration test - requires network access")]
    public async Task FetchReleases_OrderedByPublishedDate()
    {
        var fetcher = new GodotVersionFetcher();
        var releases = await fetcher.FetchReleasesAsync();

        var dates = releases.Select(r => r.PublishedAt).ToList();
        var sortedDates = dates.OrderByDescending(d => d).ToList();

        Assert.Equal(sortedDates, dates);
    }
}
