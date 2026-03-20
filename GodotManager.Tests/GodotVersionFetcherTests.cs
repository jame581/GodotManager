using GodotManager.Config;
using GodotManager.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class GodotVersionFetcherTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;

    public GodotVersionFetcherTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "godman-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempRoot);
        Environment.SetEnvironmentVariable("GODMAN_HOME", _tempRoot);
        _paths = new AppPaths();
    }

    [Fact(Skip = "Integration test - requires network access")]
    public async Task FetchReleases_ReturnsValidReleases()
    {
        var fetcher = new GodotVersionFetcher(_paths);
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
        var fetcher = new GodotVersionFetcher(_paths);
        var releases = await fetcher.FetchReleasesAsync(skipCache: true);

        var stableReleases = releases.Where(r => r.IsStable).ToList();
        Assert.NotEmpty(stableReleases);

        // Should include well-known stable versions
        Assert.Contains(stableReleases, r => r.Version.StartsWith("4."));
        Assert.Contains(stableReleases, r => r.Version.StartsWith("3."));
    }

    [Fact(Skip = "Integration test - requires network access")]
    public async Task FetchReleases_OrderedByPublishedDate()
    {
        var fetcher = new GodotVersionFetcher(_paths);
        var releases = await fetcher.FetchReleasesAsync(skipCache: true);

        var dates = releases.Select(r => r.PublishedAt).ToList();
        var sortedDates = dates.OrderByDescending(d => d).ToList();

        Assert.Equal(sortedDates, dates);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GODMAN_HOME", null);
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
