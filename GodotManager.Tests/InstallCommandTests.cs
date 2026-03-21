using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class InstallCommandTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;
    private readonly string? _savedHome;
    private readonly string? _savedGlobal;
    private readonly string? _savedLegacyHome;
    private readonly string? _savedLegacyGlobal;

    public InstallCommandTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "godman-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _savedHome = Environment.GetEnvironmentVariable("GODMAN_HOME");
        _savedGlobal = Environment.GetEnvironmentVariable("GODMAN_GLOBAL_ROOT");
        _savedLegacyHome = Environment.GetEnvironmentVariable("GODOT_MANAGER_HOME");
        _savedLegacyGlobal = Environment.GetEnvironmentVariable("GODOT_MANAGER_GLOBAL_ROOT");
        Environment.SetEnvironmentVariable("GODMAN_HOME", _tempRoot);
        Environment.SetEnvironmentVariable("GODMAN_GLOBAL_ROOT", Path.Combine(_tempRoot, "global"));

        _paths = new AppPaths();
        _registry = new RegistryService(_paths);
        _environment = new EnvironmentService(_paths);
    }

    public void Dispose()
    {
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

        Environment.SetEnvironmentVariable("GODMAN_HOME", _savedHome);
        Environment.SetEnvironmentVariable("GODMAN_GLOBAL_ROOT", _savedGlobal);
        Environment.SetEnvironmentVariable("GODOT_MANAGER_HOME", _savedLegacyHome);
        Environment.SetEnvironmentVariable("GODOT_MANAGER_GLOBAL_ROOT", _savedLegacyGlobal);
    }

    [Fact]
    public async Task ExecuteAsync_DryRun_ReturnsZero()
    {
        // Arrange
        var installer = new InstallerService(_paths, _registry, _environment);
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
        var mockArchive = CreateMockGodotArchive();
        var installer = new InstallerService(_paths, _registry, _environment);
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

        var registry = await _registry.LoadAsync();
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
        var mockArchive = CreateMockGodotArchive();
        var mockHttpClient = CreateMockHttpClient(mockArchive);
        var installer = new InstallerService(_paths, _registry, _environment, mockHttpClient);
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

        var registry = await _registry.LoadAsync();
        Assert.Single(registry.Installs);
        Assert.Equal("4.5.1", registry.Installs[0].Version);
        Assert.True(Directory.Exists(registry.Installs[0].Path));

        // Cleanup
        File.Delete(mockArchive);
    }

    private static string CreateMockGodotArchive()
    {
        var tempFile = Path.GetTempFileName();
        var zipPath = Path.ChangeExtension(tempFile, ".zip");

        File.Delete(tempFile);

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var exeName = OperatingSystem.IsWindows() ? "Godot.exe" : "godot";
            var entry = archive.CreateEntry(exeName);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine("Mock Godot binary");
            }

            var readmeEntry = archive.CreateEntry("README.txt");
            using (var stream = readmeEntry.Open())
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine("Mock Godot Engine");
            }
        }

        return zipPath;
    }

    private static HttpClient CreateMockHttpClient(string archivePath, string? downloadedFileName = null)
    {
        var handler = new MockHttpMessageHandler(archivePath, downloadedFileName);
        return new HttpClient(handler);
    }
}
