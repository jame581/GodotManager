using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using Xunit;

namespace GodotManager.Tests;

public class InstallerServiceIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;
    private readonly string? _savedHome;
    private readonly string? _savedGlobal;
    private readonly string? _savedLegacyHome;
    private readonly string? _savedLegacyGlobal;

    public InstallerServiceIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "godman-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        // Override paths to use temp directory
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
    public async Task InstallAsync_WithMockedDownload_ExtractsAndRegisters()
    {
        // Arrange
        var mockArchive = CreateMockGodotArchive();
        var mockHttpClient = CreateMockHttpClient(mockArchive);
        var installer = new InstallerService(_paths, _registry, _environment, mockHttpClient);

        var request = new InstallRequest(
            Version: "4.5.1",
            Edition: InstallEdition.Standard,
            Platform: OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope: InstallScope.User,
            DownloadUri: new Uri("http://test.com/godot.zip"),
            ArchivePath: null,
            InstallPath: null,
            Activate: false,
            Force: false,
            DryRun: false);

        // Act
        var result = await installer.InstallAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("4.5.1", result.Version);
        Assert.Equal(InstallEdition.Standard, result.Edition);
        Assert.True(Directory.Exists(result.Path));

        // Verify registry
        var registry = await _registry.LoadAsync();
        Assert.Single(registry.Installs);
        Assert.Equal(result.Id, registry.Installs[0].Id);

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithLocalArchive_ExtractsAndRegisters()
    {
        // Arrange
        var mockArchive = CreateMockGodotArchive();
        var installer = new InstallerService(_paths, _registry, _environment);

        var request = new InstallRequest(
            Version: "4.5.1",
            Edition: InstallEdition.DotNet,
            Platform: OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope: InstallScope.User,
            DownloadUri: null,
            ArchivePath: mockArchive,
            InstallPath: null,
            Activate: false,
            Force: false,
            DryRun: false);

        // Act
        var result = await installer.InstallAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("4.5.1", result.Version);
        Assert.Equal(InstallEdition.DotNet, result.Edition);
        Assert.True(Directory.Exists(result.Path));

        // Verify extracted files exist
        var files = Directory.GetFiles(result.Path, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithActivate_SetsActiveAndCreatesShim()
    {
        // Arrange
        var mockArchive = CreateMockGodotArchive();
        var installer = new InstallerService(_paths, _registry, _environment);

        var request = new InstallRequest(
            Version: "4.5.1",
            Edition: InstallEdition.Standard,
            Platform: OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope: InstallScope.User,
            DownloadUri: null,
            ArchivePath: mockArchive,
            InstallPath: null,
            Activate: true,
            Force: false,
            DryRun: false);

        // Act
        var result = await installer.InstallAsync(request);

        // Assert
        Assert.NotNull(result);

        // Verify active in registry
        var registry = await _registry.LoadAsync();
        Assert.Equal(result.Id, registry.ActiveId);
        Assert.True(registry.GetActive()?.IsActive);

        // Verify shim exists
        var shimPath = OperatingSystem.IsWindows()
            ? Path.Combine(_paths.GetShimDirectory(InstallScope.User), "godot.cmd")
            : Path.Combine(_paths.GetShimDirectory(InstallScope.User), "godot");
        
        Assert.True(File.Exists(shimPath));

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithForce_OverwritesExistingDirectory()
    {
        // Arrange
        var mockArchive = CreateMockGodotArchive();
        var installer = new InstallerService(_paths, _registry, _environment);

        var customPath = Path.Combine(_tempRoot, "custom-install");
        Directory.CreateDirectory(customPath);
        File.WriteAllText(Path.Combine(customPath, "existing.txt"), "old content");

        var request = new InstallRequest(
            Version: "4.5.1",
            Edition: InstallEdition.Standard,
            Platform: OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope: InstallScope.User,
            DownloadUri: null,
            ArchivePath: mockArchive,
            InstallPath: customPath,
            Activate: false,
            Force: true,
            DryRun: false);

        // Act
        var result = await installer.InstallAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(customPath, result.Path);
        Assert.True(Directory.Exists(result.Path));

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithDryRun_DoesNotCreateFiles()
    {
        // Arrange
        var mockArchive = CreateMockGodotArchive();
        var installer = new InstallerService(_paths, _registry, _environment);

        var request = new InstallRequest(
            Version: "4.5.1",
            Edition: InstallEdition.Standard,
            Platform: OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope: InstallScope.User,
            DownloadUri: null,
            ArchivePath: mockArchive,
            InstallPath: null,
            Activate: false,
            Force: false,
            DryRun: true);

        // Act
        var result = await installer.InstallAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.False(Directory.Exists(result.Path)); // Directory should NOT exist in dry-run

        // Verify registry was NOT modified
        var registry = await _registry.LoadAsync();
        Assert.Empty(registry.Installs);

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_ProgressCallback_ReportsProgress()
    {
        // Arrange
        var mockArchive = CreateMockGodotArchive();
        var installer = new InstallerService(_paths, _registry, _environment);

        var progressValues = new System.Collections.Generic.List<double>();
        var request = new InstallRequest(
            Version: "4.5.1",
            Edition: InstallEdition.Standard,
            Platform: OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope: InstallScope.User,
            DownloadUri: null,
            ArchivePath: mockArchive,
            InstallPath: null,
            Activate: false,
            Force: false,
            DryRun: false);

        // Act
        await installer.InstallAsync(request, progress =>
        {
            progressValues.Add(progress);
        });

        // Assert
        Assert.NotEmpty(progressValues);
        Assert.Contains(progressValues, p => p >= 0 && p <= 100);
        Assert.Equal(100, progressValues.Last()); // Should end at 100%

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_MultipleInstalls_MaintainsRegistry()
    {
        // Arrange
        var mockArchive1 = CreateMockGodotArchive();
        var mockArchive2 = CreateMockGodotArchive();
        var installer = new InstallerService(_paths, _registry, _environment);

        var request1 = new InstallRequest(
            Version: "4.5.0",
            Edition: InstallEdition.Standard,
            Platform: OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope: InstallScope.User,
            DownloadUri: null,
            ArchivePath: mockArchive1,
            InstallPath: null,
            Activate: false,
            Force: false,
            DryRun: false);

        var request2 = new InstallRequest(
            Version: "4.5.1",
            Edition: InstallEdition.DotNet,
            Platform: OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope: InstallScope.User,
            DownloadUri: null,
            ArchivePath: mockArchive2,
            InstallPath: null,
            Activate: true,
            Force: false,
            DryRun: false);

        // Act
        var result1 = await installer.InstallAsync(request1);
        var result2 = await installer.InstallAsync(request2);

        // Assert
        var registry = await _registry.LoadAsync();
        Assert.Equal(2, registry.Installs.Count);
        Assert.Equal(result2.Id, registry.ActiveId);
        Assert.Contains(registry.Installs, i => i.Version == "4.5.0");
        Assert.Contains(registry.Installs, i => i.Version == "4.5.1");

        // Cleanup
        File.Delete(mockArchive1);
        File.Delete(mockArchive2);
    }

    private static string CreateMockGodotArchive()
    {
        var tempFile = Path.GetTempFileName();
        var zipPath = Path.ChangeExtension(tempFile, ".zip");
        
        // Delete the temp file before moving
        File.Delete(tempFile);
        
        // Create the zip with the new name
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            // Create mock Godot executable
            var exeName = OperatingSystem.IsWindows() ? "Godot.exe" : "godot";
            var entry = archive.CreateEntry(exeName);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine("Mock Godot binary");
            }

            // Add some additional files to simulate real archive
            var readmeEntry = archive.CreateEntry("README.txt");
            using (var stream = readmeEntry.Open())
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine("Mock Godot Engine");
            }
        }

        return zipPath;
    }

    private static HttpClient CreateMockHttpClient(string archivePath)
    {
        var handler = new MockHttpMessageHandler(archivePath);
        return new HttpClient(handler);
    }
}

// Mock HTTP handler for testing
internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly string _archivePath;

    public MockHttpMessageHandler(string archivePath)
    {
        _archivePath = archivePath;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var fileBytes = await File.ReadAllBytesAsync(_archivePath, cancellationToken);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(fileBytes)
        };
        response.Content.Headers.ContentLength = fileBytes.Length;
        return response;
    }
}
