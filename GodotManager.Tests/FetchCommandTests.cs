using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Services;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class FetchCommandTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;
    private readonly string? _savedHome;
    private readonly string? _savedGlobal;

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
        _tempRoot = Path.Combine(Path.GetTempPath(), "godman-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _savedHome = Environment.GetEnvironmentVariable("GODMAN_HOME");
        _savedGlobal = Environment.GetEnvironmentVariable("GODMAN_GLOBAL_ROOT");
        Environment.SetEnvironmentVariable("GODMAN_HOME", _tempRoot);
        Environment.SetEnvironmentVariable("GODMAN_GLOBAL_ROOT", Path.Combine(_tempRoot, "global"));

        _paths = new AppPaths();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GODMAN_HOME", _savedHome);
        Environment.SetEnvironmentVariable("GODMAN_GLOBAL_ROOT", _savedGlobal);

        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithMockReleases_ReturnsZero()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(MockReleasesJson);
        var fetcher = new GodotVersionFetcher(_paths, httpClient);
        var command = new FetchCommand(fetcher);
        var settings = new FetchCommand.Settings { Limit = 20 };

        // Act
        var result = await command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithStableFilter_ReturnsZero()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(MockReleasesJson);
        var fetcher = new GodotVersionFetcher(_paths, httpClient);
        var command = new FetchCommand(fetcher);
        var settings = new FetchCommand.Settings { StableOnly = true, Limit = 20 };

        // Act
        var result = await command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithVersionFilter_ReturnsZero()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(MockReleasesJson);
        var fetcher = new GodotVersionFetcher(_paths, httpClient);
        var command = new FetchCommand(fetcher);
        var settings = new FetchCommand.Settings { VersionFilter = "4.5", Limit = 20 };

        // Act
        var result = await command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoCache_ReturnsZero()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(MockReleasesJson);
        var fetcher = new GodotVersionFetcher(_paths, httpClient);
        var command = new FetchCommand(fetcher);
        var settings = new FetchCommand.Settings { NoCache = true, Limit = 20 };

        // Act
        var result = await command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyReleases_ReturnsZero()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(EmptyReleasesJson);
        var fetcher = new GodotVersionFetcher(_paths, httpClient);
        var command = new FetchCommand(fetcher);
        var settings = new FetchCommand.Settings { Limit = 20 };

        // Act
        var result = await command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);
    }

    private static HttpClient CreateMockHttpClient(string jsonResponse)
    {
        var handler = new MockJsonHttpMessageHandler(jsonResponse);
        return new HttpClient(handler);
    }
}

internal class MockJsonHttpMessageHandler : HttpMessageHandler
{
    private readonly string _jsonResponse;

    public MockJsonHttpMessageHandler(string jsonResponse)
    {
        _jsonResponse = jsonResponse;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_jsonResponse, System.Text.Encoding.UTF8, "application/json")
        };

        return Task.FromResult(response);
    }
}
