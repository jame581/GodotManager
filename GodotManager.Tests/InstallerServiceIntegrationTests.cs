using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class InstallerServiceIntegrationTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public InstallerServiceIntegrationTests()
    {
        _fixture = new GodmanTestFixture();
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public async Task InstallAsync_WithMockedDownload_ExtractsAndRegisters()
    {
        // Arrange
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var mockHttpClient = new HttpClient(new MockFileHttpHandler(mockArchive));
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, mockHttpClient);

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

        // Verify checksum was computed from download
        Assert.NotNull(result.Checksum);
        Assert.Equal(64, result.Checksum.Length); // SHA256 hex string

        // Verify registry
        var registry = await _fixture.Registry.LoadAsync();
        Assert.Single(registry.Installs);
        Assert.Equal(result.Id, registry.Installs[0].Id);
        Assert.Equal(result.Checksum, registry.Installs[0].Checksum);

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithLocalArchive_ExtractsAndRegisters()
    {
        // Arrange
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

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

        // Verify checksum was computed from local archive
        Assert.NotNull(result.Checksum);
        Assert.Equal(64, result.Checksum.Length);

        // Verify checksum matches independently computed hash
        var expectedHash = ComputeSha256(mockArchive);
        Assert.Equal(expectedHash, result.Checksum);

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
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

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
        var registry = await _fixture.Registry.LoadAsync();
        Assert.Equal(result.Id, registry.ActiveId);
        Assert.True(registry.GetActive()?.IsActive);

        // Verify shim exists
        var shimPath = OperatingSystem.IsWindows()
            ? Path.Combine(_fixture.Paths.GetShimDirectory(InstallScope.User), "godot.cmd")
            : Path.Combine(_fixture.Paths.GetShimDirectory(InstallScope.User), "godot");

        Assert.True(File.Exists(shimPath));

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithForce_OverwritesExistingDirectory()
    {
        // Arrange
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

        var customPath = Path.Combine(_fixture.TempRoot, "custom-install");
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
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

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
        var registry = await _fixture.Registry.LoadAsync();
        Assert.Empty(registry.Installs);

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_ProgressCallback_ReportsProgress()
    {
        // Arrange
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

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
        var mockArchive1 = MockArchiveFactory.CreateMockGodotArchive();
        var mockArchive2 = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

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
        var registry = await _fixture.Registry.LoadAsync();
        Assert.Equal(2, registry.Installs.Count);
        Assert.Equal(result2.Id, registry.ActiveId);
        Assert.Contains(registry.Installs, i => i.Version == "4.5.0");
        Assert.Contains(registry.Installs, i => i.Version == "4.5.1");

        // Cleanup
        File.Delete(mockArchive1);
        File.Delete(mockArchive2);
    }

    [Fact]
    public async Task InstallAsync_WithQueryDownloadUrlWithoutFilename_UsesDeterministicFolderName()
    {
        // Arrange
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var mockHttpClient = new HttpClient(new MockFileHttpHandler(mockArchive));
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, mockHttpClient);
        var platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;

        var request = new InstallRequest(
            Version: "4.5.2",
            Edition: InstallEdition.Standard,
            Platform: platform,
            Scope: InstallScope.User,
            DownloadUri: new Uri("https://github.com/godotengine/godot-builds/releases/download/4.5-stable/download?platform=windows"),
            ArchivePath: null,
            InstallPath: null,
            Activate: false,
            Force: false,
            DryRun: false);

        // Act
        var result = await installer.InstallAsync(request);

        // Assert
        var expectedFolder = platform == InstallPlatform.Windows
            ? "4.5.2-standard-windows-user"
            : "4.5.2-standard-linux-user";
        var expectedPath = Path.Combine(_fixture.Paths.GetInstallRoot(InstallScope.User), expectedFolder);

        Assert.Equal(expectedPath, result.Path);
        Assert.True(Directory.Exists(result.Path));

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithDownloadedArchiveName_UsesArchiveBasedFolderName()
    {
        // Arrange
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var mockHttpClient = new HttpClient(new MockFileHttpHandler(mockArchive, "Godot_v4.5.2-stable_linux.x86_64.zip"));
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, mockHttpClient);

        var request = new InstallRequest(
            Version: "4.5.2",
            Edition: InstallEdition.Standard,
            Platform: InstallPlatform.Linux,
            Scope: InstallScope.User,
            DownloadUri: new Uri("http://test.com/download?platform=linux"),
            ArchivePath: null,
            InstallPath: null,
            Activate: false,
            Force: false,
            DryRun: false);

        // Act
        var result = await installer.InstallAsync(request);

        // Assert
        var expectedPath = Path.Combine(_fixture.Paths.GetInstallRoot(InstallScope.User), "Godot_v4.5.2-stable_linux");
        Assert.Equal(expectedPath, result.Path);
        Assert.True(Directory.Exists(result.Path));

        // Cleanup
        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_MultipleQueryEndpointDownloads_DoNotOverwriteRegistryEntries()
    {
        // Arrange
        var mockArchive1 = MockArchiveFactory.CreateMockGodotArchive();
        var mockArchive2 = MockArchiveFactory.CreateMockGodotArchive();
        var platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;
        var requestUri = new Uri("https://example.com/download?platform=windows");

        var installer1 = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, new HttpClient(new MockFileHttpHandler(mockArchive1)));
        var installer2 = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, new HttpClient(new MockFileHttpHandler(mockArchive2)));

        var request1 = new InstallRequest(
            Version: "4.5.0",
            Edition: InstallEdition.Standard,
            Platform: platform,
            Scope: InstallScope.User,
            DownloadUri: requestUri,
            ArchivePath: null,
            InstallPath: null,
            Activate: false,
            Force: false,
            DryRun: false);

        var request2 = new InstallRequest(
            Version: "4.5.1",
            Edition: InstallEdition.DotNet,
            Platform: platform,
            Scope: InstallScope.User,
            DownloadUri: requestUri,
            ArchivePath: null,
            InstallPath: null,
            Activate: false,
            Force: false,
            DryRun: false);

        // Act
        var result1 = await installer1.InstallAsync(request1);
        var result2 = await installer2.InstallAsync(request2);

        // Assert
        Assert.NotEqual(result1.Path, result2.Path);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.Equal(2, registry.Installs.Count);
        Assert.Contains(registry.Installs, i => i.Version == "4.5.0");
        Assert.Contains(registry.Installs, i => i.Version == "4.5.1");

        // Cleanup
        File.Delete(mockArchive1);
        File.Delete(mockArchive2);
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }
}
