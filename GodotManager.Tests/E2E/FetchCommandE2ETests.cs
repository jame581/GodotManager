using GodotManager.Tests.Helpers;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

public class FetchCommandE2ETests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    private const string MockReleasesJson = """
        [
          {
            "tag_name": "4.5.1-stable",
            "name": "Godot 4.5.1",
            "draft": false,
            "prerelease": false,
            "published_at": "2025-01-15T00:00:00Z",
            "assets": [
              {"name": "Godot_v4.5.1-stable_win64.exe.zip", "browser_download_url": "http://example.com/a.zip"},
              {"name": "Godot_v4.5.1-stable_mono_win64.zip", "browser_download_url": "http://example.com/b.zip"}
            ]
          },
          {
            "tag_name": "4.4.0-beta1",
            "name": "Godot 4.4.0 Beta 1",
            "draft": false,
            "prerelease": true,
            "published_at": "2024-12-01T00:00:00Z",
            "assets": [
              {"name": "Godot_v4.4.0-beta1_win64.exe.zip", "browser_download_url": "http://example.com/c.zip"}
            ]
          }
        ]
        """;

    public FetchCommandE2ETests() => _fixture = new GodmanTestFixture();
    public void Dispose() => _fixture.Dispose();

    private HttpClient MockHttp() => new(new MockJsonHttpHandler(MockReleasesJson));

    [Fact]
    public async Task Fetch_Default_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture, MockHttp());

        var result = await app.RunAsync("fetch");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Fetch_StableOnly_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture, MockHttp());

        var result = await app.RunAsync("fetch", "--stable");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Fetch_WithFilter_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture, MockHttp());

        var result = await app.RunAsync("fetch", "--filter", "4.5");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Fetch_WithLimit_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture, MockHttp());

        var result = await app.RunAsync("fetch", "--limit", "1");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Fetch_NoCache_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture, MockHttp());

        var result = await app.RunAsync("fetch", "--no-cache");

        Assert.Equal(0, result.ExitCode);
    }
}
