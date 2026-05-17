using GodotManager.Tests.Helpers;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class FetchCommandTests : IDisposable
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
              {"name": "Godot_v4.5.1-stable_win64.exe.zip", "browser_download_url": "http://example.com/godot.zip"},
              {"name": "Godot_v4.5.1-stable_mono_win64.zip", "browser_download_url": "http://example.com/godot-mono.zip"}
            ]
          },
          {
            "tag_name": "4.4.0-beta1",
            "name": "Godot 4.4.0 Beta 1",
            "draft": false,
            "prerelease": true,
            "published_at": "2024-12-01T00:00:00Z",
            "assets": [
              {"name": "Godot_v4.4.0-beta1_win64.exe.zip", "browser_download_url": "http://example.com/godot-beta.zip"},
              {"name": "Godot_v4.4.0-beta1_mono_win64.zip", "browser_download_url": "http://example.com/godot-beta-mono.zip"}
            ]
          },
          {
            "tag_name": "4.3.0-stable",
            "name": "Godot 4.3.0",
            "draft": false,
            "prerelease": false,
            "published_at": "2024-10-01T00:00:00Z",
            "assets": [
              {"name": "Godot_v4.3.0-stable_win64.exe.zip", "browser_download_url": "http://example.com/godot-430.zip"},
              {"name": "Godot_v4.3.0-stable_mono_win64.zip", "browser_download_url": "http://example.com/godot-430-mono.zip"}
            ]
          }
        ]
        """;

    private const string EmptyReleasesJson = "[]";

    public FetchCommandTests()
    {
        _fixture = new GodmanTestFixture();
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WithMockReleases_ReturnsZero()
    {
        // Arrange
        var app = CliTestHarness.Create(_fixture, CreateMockHttpClient(MockReleasesJson));

        // Act
        var result = await app.RunAsync(["fetch", "--limit", "20"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithStableFilter_ReturnsZero()
    {
        // Arrange
        var app = CliTestHarness.Create(_fixture, CreateMockHttpClient(MockReleasesJson));

        // Act
        var result = await app.RunAsync(["fetch", "--stable", "--limit", "20"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithVersionFilter_ReturnsZero()
    {
        // Arrange
        var app = CliTestHarness.Create(_fixture, CreateMockHttpClient(MockReleasesJson));

        // Act
        var result = await app.RunAsync(["fetch", "--filter", "4.5", "--limit", "20"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoCache_ReturnsZero()
    {
        // Arrange
        var app = CliTestHarness.Create(_fixture, CreateMockHttpClient(MockReleasesJson));

        // Act
        var result = await app.RunAsync(["fetch", "--no-cache", "--limit", "20"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyReleases_ReturnsZero()
    {
        // Arrange
        var app = CliTestHarness.Create(_fixture, CreateMockHttpClient(EmptyReleasesJson));

        // Act
        var result = await app.RunAsync(["fetch", "--limit", "20"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    private static HttpClient CreateMockHttpClient(string jsonResponse)
    {
        var handler = new MockJsonHttpHandler(jsonResponse);
        return new HttpClient(handler);
    }
}
