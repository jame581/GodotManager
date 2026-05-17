using GodotManager.Commands;
using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class InstallCommandTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public InstallCommandTests()
    {
        _fixture = new GodmanTestFixture();
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_DryRun_ReturnsZero()
    {
        // Arrange
        var app = CliTestHarness.Create(_fixture);

        var platform = OperatingSystem.IsWindows() ? "windows" : "linux";

        // Act
        var result = await app.RunAsync([
            "install",
            "--version", "4.5.1",
            "--edition", "Standard",
            "--platform", platform,
            "--scope", "User",
            "--url", "https://example.com/godot.zip",
            "--dry-run"
        ]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithLocalArchive_InstallsAndReturnsZero()
    {
        // Arrange
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var app = CliTestHarness.Create(_fixture);

        var platform = OperatingSystem.IsWindows() ? "windows" : "linux";

        // Act
        var result = await app.RunAsync([
            "install",
            "--version", "4.5.1",
            "--edition", "Standard",
            "--platform", platform,
            "--scope", "User",
            "--archive", mockArchive
        ]);

        // Assert
        Assert.Equal(0, result.ExitCode);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.Single(registry.Installs);
        Assert.Equal("4.5.1", registry.Installs[0].Version);

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public void ExecuteAsync_WithMissingVersion_ReturnsError()
    {
        // Arrange
        var settings = new InstallCommand.Settings
        {
            Version = string.Empty,
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User
        };

        // Act
        var validation = settings.Validate();

        // Assert
        Assert.False(validation.Successful);
        Assert.Contains("Version is required", validation.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithMockedDownload_InstallsAndReturnsZero()
    {
        // Arrange
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var mockHttpClient = new HttpClient(new MockFileHttpHandler(mockArchive));
        var app = CliTestHarness.Create(_fixture, mockHttpClient);

        var platform = OperatingSystem.IsWindows() ? "windows" : "linux";

        // Act
        var result = await app.RunAsync([
            "install",
            "--version", "4.5.1",
            "--edition", "Standard",
            "--platform", platform,
            "--scope", "User",
            "--url", "http://test.com/godot.zip"
        ]);

        // Assert
        Assert.Equal(0, result.ExitCode);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.Single(registry.Installs);
        Assert.Equal("4.5.1", registry.Installs[0].Version);
        Assert.True(Directory.Exists(registry.Installs[0].Path));

        // Cleanup
        File.Delete(mockArchive);
    }
}
