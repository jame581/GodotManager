using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
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
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var urlBuilder = new GodotDownloadUrlBuilder();
        var command = new InstallCommand(installer, urlBuilder);

        var settings = new InstallCommand.Settings
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Url = "https://example.com/godot.zip",
            DryRun = true
        };

        // Act
        var result = await command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithLocalArchive_InstallsAndReturnsZero()
    {
        // Arrange
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var urlBuilder = new GodotDownloadUrlBuilder();
        var command = new InstallCommand(installer, urlBuilder);

        var settings = new InstallCommand.Settings
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            ArchivePath = mockArchive,
            Activate = false,
            Force = false,
            DryRun = false
        };

        // Act
        var result = await command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);

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
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, mockHttpClient);
        var urlBuilder = new GodotDownloadUrlBuilder();
        var command = new InstallCommand(installer, urlBuilder);

        var settings = new InstallCommand.Settings
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Url = "http://test.com/godot.zip",
            Activate = false,
            Force = false,
            DryRun = false
        };

        // Act
        var result = await command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.Single(registry.Installs);
        Assert.Equal("4.5.1", registry.Installs[0].Version);
        Assert.True(Directory.Exists(registry.Installs[0].Path));

        // Cleanup
        File.Delete(mockArchive);
    }
}
