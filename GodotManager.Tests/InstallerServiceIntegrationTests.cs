using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
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
        Assert.Equal(128, result.Checksum.Length); // SHA512 hex string

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
        Assert.Equal(128, result.Checksum.Length);

        // Verify checksum matches independently computed hash
        var expectedHash = ComputeSha512(mockArchive);
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

        // The archive also contains README.txt, so this stale copy must be replaced.
        var readme = Path.Combine(customPath, "README.txt");
        File.WriteAllText(readme, "old content");

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
        Assert.Equal("Mock Godot Engine", (await File.ReadAllTextAsync(readme)).Trim());

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

    [Fact]
    public async Task InstallAsync_RecordsSha512AndAlgorithm()
    {
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

        var result = await installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            InstallScope.User,
            null, mockArchive, null, false, false, false));

        Assert.NotNull(result.Checksum);
        Assert.Equal(128, result.Checksum!.Length);
        Assert.Equal("sha512", result.ChecksumAlgorithm);
        Assert.False(result.ChecksumVerified);   // a local archive has nothing to verify against

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WhenExtractionFails_LeavesNoTargetOrStagingDirectory()
    {
        var corrupt = Path.Combine(_fixture.TempRoot, "corrupt.zip");
        await File.WriteAllTextAsync(corrupt, "this is not a zip file");

        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

        await Assert.ThrowsAnyAsync<Exception>(() => installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            InstallScope.User,
            null, corrupt, null, false, false, false)));

        Assert.Empty(Directory.GetDirectories(_fixture.Paths.GetInstallRoot(InstallScope.User)));
        Assert.Empty((await _fixture.Registry.LoadAsync()).Installs);
    }

    [Fact]
    public async Task InstallAsync_WhenCancelledDuringExtraction_LeavesNoTargetOrStagingDirectory()
    {
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

        using var cts = new CancellationTokenSource();

        // Extraction reports progress per entry and then honours the token, so
        // cancelling from the callback aborts mid-extraction.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(
            new InstallRequest(
                "4.5.1",
                InstallEdition.Standard,
                OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
                InstallScope.User,
                null, mockArchive, null, false, false, false),
            _ => cts.Cancel(),
            cts.Token));

        Assert.Empty(Directory.GetDirectories(_fixture.Paths.GetInstallRoot(InstallScope.User)));

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithForce_PreservesUnrelatedFilesInTheTarget()
    {
        // --path accepts any directory. Replacing rather than merging would delete
        // whatever else lives there.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;

        var customPath = Path.Combine(_fixture.TempRoot, "shared-dir");
        Directory.CreateDirectory(customPath);
        var unrelated = Path.Combine(customPath, "my-notes.txt");
        await File.WriteAllTextAsync(unrelated, "do not delete me");

        var nestedUnrelated = Path.Combine(customPath, "projects", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedUnrelated)!);
        await File.WriteAllTextAsync(nestedUnrelated, "do not delete me either");

        await installer.InstallAsync(new InstallRequest(
            "4.5.1", InstallEdition.Standard, platform, InstallScope.User,
            null, mockArchive, customPath, false, Force: true, DryRun: false));

        Assert.True(File.Exists(unrelated), "--force must merge, not replace");
        Assert.Equal("do not delete me", (await File.ReadAllTextAsync(unrelated)).Trim());
        Assert.True(File.Exists(nestedUnrelated), "--force must not remove unrelated subdirectories");
        Assert.Equal("do not delete me either", (await File.ReadAllTextAsync(nestedUnrelated)).Trim());

        // ...and the archive still landed.
        Assert.True(File.Exists(Path.Combine(customPath, "README.txt")));

        // The merge must not leave its staging directory beside the target.
        Assert.Empty(Directory.GetDirectories(_fixture.TempRoot, ".staging-*"));

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithForce_MergesNestedArchiveEntries()
    {
        // Godot's .NET builds ship a GodotSharp/ tree. A merge that only walked the
        // archive's top level would silently produce a broken --force refresh on
        // exactly the builds most likely to be refreshed.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchiveWithNestedEntry();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;

        // Pre-creating the target is what selects the merge branch over the atomic swap.
        var customPath = Path.Combine(_fixture.TempRoot, "dotnet-install");
        Directory.CreateDirectory(customPath);

        await installer.InstallAsync(new InstallRequest(
            "4.5.1", InstallEdition.DotNet, platform, InstallScope.User,
            null, mockArchive, customPath, false, Force: true, DryRun: false));

        var nested = Path.Combine(customPath, "GodotSharp", "Api", "GodotSharp.dll");
        Assert.True(File.Exists(nested), "--force must merge entries nested inside the archive");
        Assert.Equal("Mock GodotSharp assembly", (await File.ReadAllTextAsync(nested)).Trim());

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WhenMergeFailsPartWay_ReportsThatTheTargetWasPartiallyUpdated()
    {
        // The merge is the one non-atomic step. Telling the user the install was
        // "left in place" here would point them at a half-overwritten directory.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;

        var customPath = Path.Combine(_fixture.TempRoot, "half-merged");
        Directory.CreateDirectory(customPath);

        // A directory where the archive carries a file makes File.Copy fail mid-merge.
        Directory.CreateDirectory(Path.Combine(customPath, "README.txt"));

        var ex = await Assert.ThrowsAsync<GodmanException>(() => installer.InstallAsync(new InstallRequest(
            "4.5.1", InstallEdition.Standard, platform, InstallScope.User,
            null, mockArchive, customPath, false, Force: true, DryRun: false)));

        Assert.NotNull(ex.Hint);
        Assert.Contains("partially updated", ex.Hint);
        Assert.DoesNotContain("left in place", ex.Hint);

        // Staging is still cleaned up on this path.
        Assert.Empty(Directory.GetDirectories(_fixture.TempRoot, ".staging-*"));

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithRelativePath_ResolvesAgainstCurrentDirectory()
    {
        // A bare relative --path has no directory component, so the target's parent
        // can only be derived once the path has been made absolute.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_fixture.TempRoot);

            var result = await installer.InstallAsync(new InstallRequest(
                "4.5.1",
                InstallEdition.Standard,
                OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
                InstallScope.User,
                null, mockArchive, "relative-target", false, false, false));

            Assert.Equal(Path.GetFullPath("relative-target"), result.Path);
            Assert.True(Path.IsPathRooted(result.Path), "the registry must record an absolute path");
            Assert.True(File.Exists(Path.Combine(result.Path, "README.txt")));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithTrailingSeparatorPath_StillSwapsAtomically()
    {
        // A trailing separator used to make the target its own parent, which quietly
        // took the merge branch and put staging *inside* the directory being installed to.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

        var target = Path.Combine(_fixture.TempRoot, "trailing-sep");

        var result = await installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            InstallScope.User,
            null, mockArchive, target + Path.DirectorySeparatorChar, false, false, false));

        Assert.Equal(target, result.Path);
        Assert.True(File.Exists(Path.Combine(target, "README.txt")));

        // The atomic swap leaves nothing beside or inside the target.
        Assert.Empty(Directory.GetDirectories(target));

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithForcedExtractionFailure_LeavesExistingInstallIntact()
    {
        var goodArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;

        var first = await installer.InstallAsync(new InstallRequest(
            "4.5.1", InstallEdition.Standard, platform, InstallScope.User,
            null, goodArchive, null, false, false, false));

        var sentinel = Path.Combine(first.Path, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "original install");

        var corrupt = Path.Combine(_fixture.TempRoot, "corrupt2.zip");
        await File.WriteAllTextAsync(corrupt, "not a zip");

        await Assert.ThrowsAnyAsync<Exception>(() => installer.InstallAsync(new InstallRequest(
            "4.5.1", InstallEdition.Standard, platform, InstallScope.User,
            null, corrupt, first.Path, false, Force: true, DryRun: false)));

        Assert.True(File.Exists(sentinel), "a failed forced install must not damage the existing one");
        Assert.Equal("original install", (await File.ReadAllTextAsync(sentinel)).Trim());

        // The failed attempt must not leave staging behind next to the install either.
        Assert.Single(Directory.GetDirectories(_fixture.Paths.GetInstallRoot(InstallScope.User)));

        File.Delete(goodArchive);
    }

    [Fact]
    public async Task InstallAsync_WithPathWhoseParentDoesNotExist_Succeeds()
    {
        // Directory.CreateDirectory used to build the whole chain; Directory.Move
        // does not, so the parent must be created explicitly.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var nested = Path.Combine(_fixture.TempRoot, "tools", "godot", "4.5.1");

        var result = await installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            InstallScope.User,
            null, mockArchive, nested, false, false, false));

        Assert.Equal(nested, result.Path);
        Assert.NotEmpty(Directory.GetFiles(nested, "*", SearchOption.AllDirectories));

        // A fresh install swaps staging into place; nothing may be left beside it.
        Assert.Equal(
            new[] { nested },
            Directory.GetDirectories(Path.GetDirectoryName(nested)!));

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_AfterSuccessfulDownload_EmptiesTheDownloadCache()
    {
        // Downloads now land in the managed cache rather than a temp file, so the
        // install has to clean up after itself or every install leaks an archive.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var mockHttpClient = new HttpClient(new MockFileHttpHandler(mockArchive, "Godot_v4.5.1-stable_linux.x86_64.zip"));
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, mockHttpClient);

        await installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            InstallScope.User,
            new Uri("https://test.invalid/godot.zip"),
            null, null, false, false, false));

        Assert.Empty(Directory.GetFiles(_fixture.Paths.DownloadCacheDirectory));

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithLocalArchive_DoesNotDeleteTheUsersFile()
    {
        // The cache cleanup keys off the plan's CacheFilePath, which is null for a
        // local archive. Cleaning up the resolved ArchivePath instead would delete
        // the file the user pointed at.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

        await installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            InstallScope.User,
            null, mockArchive, null, false, false, false));

        Assert.True(File.Exists(mockArchive), "a user-supplied --archive must never be deleted");

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_FromDownload_KeepsTheExistingFolderNamingScheme()
    {
        // The sums lookup needs the post-redirect filename, but folder naming must
        // keep using the request URI or every existing install directory is orphaned.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var mockHttpClient = new HttpClient(new MockFileHttpHandler(mockArchive));
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, mockHttpClient);

        var result = await installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            InstallPlatform.Linux,
            InstallScope.User,
            new Uri("https://downloads.godotengine.org/?version=4.5.1&flavor=stable&slug=linux.x86_64.zip"),
            null, null, false, false, false));

        // No filename in the request URI and no Content-Disposition => deterministic fallback.
        Assert.EndsWith("4.5.1-standard-linux-user", result.Path);

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithPublishedSums_RecordsAVerifiedChecksum()
    {
        // The registry's ChecksumVerified flag is only worth anything if it reflects
        // a real comparison against SHA512-SUMS.txt rather than defaulting.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var archiveBytes = await File.ReadAllBytesAsync(mockArchive);
        var handler = new MockSumsHttpHandler(archiveBytes, "Godot_v4.5.1-stable_linux.x86_64.zip", "SHA512-SUMS.txt");
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, new HttpClient(handler));

        var result = await installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            InstallPlatform.Linux,
            InstallScope.User,
            new Uri("https://test.invalid/godot.zip"),
            null, null, false, false, false,
            Checksums: new ChecksumSource("4.5.1")));

        Assert.True(result.ChecksumVerified, "a download matching the published sums must be recorded as verified");
        Assert.Equal("sha512", result.ChecksumAlgorithm);
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(archiveBytes)), result.Checksum);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.True(Assert.Single(registry.Installs).ChecksumVerified);

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_FromACustomUrl_RecordsTheChecksumAsUnverified()
    {
        // No ChecksumSource means no upstream sums exist to compare against, so the
        // hash is recorded but must not claim to have been verified.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var mockHttpClient = new HttpClient(new MockFileHttpHandler(mockArchive));
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, mockHttpClient);

        var result = await installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            InstallPlatform.Linux,
            InstallScope.User,
            new Uri("https://test.invalid/godot.zip"),
            null, null, false, false, false));

        Assert.NotNull(result.Checksum);
        Assert.Equal("sha512", result.ChecksumAlgorithm);
        Assert.False(result.ChecksumVerified);

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithLocalArchive_InvokesOnVerifiedWithNotApplicable()
    {
        // A local --archive never runs verification at all -- InstallPlan's default
        // ChecksumStatus (NotApplicable) is what onVerified should report here, not
        // the fail-closed Unverified default that would wrongly tell a caller
        // something was attempted and failed.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);

        ChecksumStatus? observedStatus = null;
        string? observedReason = "not yet set";

        await installer.InstallAsync(
            new InstallRequest(
                "4.5.1", InstallEdition.Standard, InstallPlatform.Linux, InstallScope.User,
                DownloadUri: null, ArchivePath: mockArchive, InstallPath: null,
                Activate: false, Force: false),
            onVerified: (status, reason) =>
            {
                observedStatus = status;
                observedReason = reason;
            });

        Assert.Equal(ChecksumStatus.NotApplicable, observedStatus);
        Assert.Null(observedReason);

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithNoPublishedSums_InvokesOnVerifiedWithNotApplicable()
    {
        // The plumbing Important 2 adds: InstallerService must hand the command
        // layer enough to tell "nothing to check" apart from "checked and failed"
        // without the command layer re-deriving it from ChecksumVerified alone.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var archiveBytes = await File.ReadAllBytesAsync(mockArchive);
        var handler = new SequencedHttpHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("SHA512-SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archiveBytes) };
            ok.Content.Headers.ContentLength = archiveBytes.Length;
            return ok;
        });
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, new HttpClient(handler));

        ChecksumStatus? observedStatus = null;
        string? observedReason = null;

        await installer.InstallAsync(
            new InstallRequest(
                "4.5.1", InstallEdition.Standard, InstallPlatform.Linux, InstallScope.User,
                new Uri("https://test.invalid/godot.zip"),
                null, null, false, false, false,
                Checksums: new ChecksumSource("4.5.1")),
            onVerified: (status, reason) =>
            {
                observedStatus = status;
                observedReason = reason;
            });

        Assert.Equal(ChecksumStatus.NotApplicable, observedStatus);
        Assert.NotNull(observedReason);

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WhenSumsFetchGenuinelyFails_InvokesOnVerifiedWithUnverifiedAndReason()
    {
        // The counterpart to the NotApplicable test above: an actual failure (HTTP
        // 500, not 404) must come back as Unverified with a reason naming what
        // happened, so the command layer knows to warn and what to say.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var archiveBytes = await File.ReadAllBytesAsync(mockArchive);
        var handler = new SequencedHttpHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("SHA512-SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archiveBytes) };
            ok.Content.Headers.ContentLength = archiveBytes.Length;
            return ok;
        });
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, new HttpClient(handler));

        ChecksumStatus? observedStatus = null;
        string? observedReason = null;

        await installer.InstallAsync(
            new InstallRequest(
                "4.5.1", InstallEdition.Standard, InstallPlatform.Linux, InstallScope.User,
                new Uri("https://test.invalid/godot.zip"),
                null, null, false, false, false,
                Checksums: new ChecksumSource("4.5.1")),
            onVerified: (status, reason) =>
            {
                observedStatus = status;
                observedReason = reason;
            });

        Assert.Equal(ChecksumStatus.Unverified, observedStatus);
        Assert.Contains("HTTP 500", observedReason);

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WhenTheResolvedNameDiffers_UsesEachNameForItsOwnPurpose()
    {
        // DownloadOutcome carries two names for two jobs: ArchiveName (pre-redirect)
        // decides the install folder, ResolvedFileName (post-redirect) is the sums
        // lookup key. The mock deliberately makes them disagree — the response has no
        // Content-Disposition and reports a CDN blob as its final URI, while the sums
        // file is keyed on that blob. Swapping the two fails both assertions: the
        // folder would be named after the blob, and the lookup would miss.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var archiveBytes = await File.ReadAllBytesAsync(mockArchive);
        var sums = $"{Convert.ToHexStringLower(SHA512.HashData(archiveBytes))}  9f2c1ab4.bin\n";

        var handler = new SequencedHttpHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("SHA512-SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) };
            }

            var ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archiveBytes),
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get, new Uri("https://cdn.invalid/objects/9f2c1ab4.bin"))
            };
            ok.Content.Headers.ContentLength = archiveBytes.Length;
            return ok;
        });

        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment, new HttpClient(handler));

        var result = await installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            InstallPlatform.Linux,
            InstallScope.User,
            new Uri("https://downloads.godotengine.org/Godot_v4.5.1-stable_linux.x86_64.zip"),
            null, null, false, false, false,
            Checksums: new ChecksumSource("4.5.1")));

        Assert.Equal(
            Path.Combine(_fixture.Paths.GetInstallRoot(InstallScope.User), "Godot_v4.5.1-stable_linux"),
            result.Path);
        Assert.True(result.ChecksumVerified, "the sums lookup must use the post-redirect name");

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task ElevatedPayload_CarriesTheParentsVerification_IntoTheChildsRegistryEntry()
    {
        // The Windows global install downloads in one process and installs in another.
        // Everything below is that boundary except Process.Start itself: the parent's
        // resolved plan is encoded exactly as RunElevatedInstallAsync encodes it, and
        // decoded exactly as the elevated child decodes it. Left uncarried, the child
        // re-hashes and writes ChecksumVerified = false over a download the parent did
        // verify — and the install command then tells the user so.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var parentHash = ComputeSha512(mockArchive);
        var target = Path.Combine(_fixture.TempRoot, "elevated-target");

        var parentRequest = new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            InstallScope.Global,
            null, mockArchive, target, false, false, false,
            Known: new KnownChecksum(parentHash, "sha512", Verified: true));

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(InstallerService.BuildElevatedPayload(parentRequest))));

        var payload = JsonSerializer.Deserialize<ElevatedInstallPayload>(
            Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Assert.NotNull(payload);

        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var result = await installer.InstallAsync(ElevatedInstallCommand.BuildRequest(payload));

        Assert.Equal(parentHash, result.Checksum);
        Assert.Equal("sha512", result.ChecksumAlgorithm);
        Assert.True(result.ChecksumVerified, "the parent verified this archive; the child must not record otherwise");

        var registry = await _fixture.Registry.LoadAsync();
        Assert.True(Assert.Single(registry.Installs).ChecksumVerified);

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WhenACarriedChecksumDoesNotMatchTheArchive_RefusesToInstall()
    {
        // The elevated child extracts an archive out of a directory the unelevated
        // parent can write to. If those bytes changed after the parent hashed them,
        // writing them into Program Files as administrator is precisely what must not
        // happen — so the claim is re-checked against the file rather than believed.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var target = Path.Combine(_fixture.TempRoot, "swapped-target");

        var ex = await Assert.ThrowsAsync<GodmanException>(() => installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            InstallScope.User,
            null, mockArchive, target, false, false, false,
            Known: new KnownChecksum(new string('c', 128), "sha512", Verified: true))));

        Assert.Contains("no longer the file that was checked", ex.Message);
        Assert.False(Directory.Exists(target));
        Assert.Empty((await _fixture.Registry.LoadAsync()).Installs);

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithACarriedChecksumItCannotRecompute_RecordsItAsUnverified()
    {
        // A payload naming an algorithm this build does not compute cannot be checked
        // against the archive, so its Verified claim is worth nothing and must not
        // reach the registry. The install still proceeds — it is an unverified local
        // archive, which is an ordinary thing to install.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var target = Path.Combine(_fixture.TempRoot, "unknown-algorithm-target");

        var result = await installer.InstallAsync(new InstallRequest(
            "4.5.1",
            InstallEdition.Standard,
            OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            InstallScope.User,
            null, mockArchive, target, false, false, false,
            Known: new KnownChecksum(new string('c', 32), "md5", Verified: true)));

        Assert.False(result.ChecksumVerified);
        Assert.Equal(ComputeSha512(mockArchive), result.Checksum);
        Assert.Equal("sha512", result.ChecksumAlgorithm);

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WhenTargetExists_ThrowsActionableGodmanException()
    {
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;

        var first = await installer.InstallAsync(new InstallRequest(
            "4.5.1", InstallEdition.Standard, platform, InstallScope.User,
            null, mockArchive, null, false, false, false));

        var ex = await Assert.ThrowsAsync<GodmanException>(() => installer.InstallAsync(new InstallRequest(
            "4.5.1", InstallEdition.Standard, platform, InstallScope.User,
            null, mockArchive, first.Path, false, false, false)));

        Assert.Contains(first.Path, ex.Message);
        Assert.NotNull(ex.Hint);
        Assert.Contains("--force", ex.Hint!);
        // A registry entry owns this directory, so removal must be offered too.
        Assert.Contains("remove", ex.Hint!, StringComparison.OrdinalIgnoreCase);

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WhenTargetExistsButIsNotRegistered_OffersOnlyForceOrManualDelete()
    {
        // A stray directory not owned by any registry entry has no "godman remove"
        // to offer. The hint must not claim otherwise.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;

        var target = Path.Combine(_fixture.TempRoot, "unregistered-dir");
        Directory.CreateDirectory(target);

        var ex = await Assert.ThrowsAsync<GodmanException>(() => installer.InstallAsync(new InstallRequest(
            "4.5.1", InstallEdition.Standard, platform, InstallScope.User,
            null, mockArchive, target, false, false, false)));

        Assert.Contains(target, ex.Message);
        Assert.NotNull(ex.Hint);
        Assert.Contains("--force", ex.Hint!);
        Assert.DoesNotContain("godman remove", ex.Hint!);

        File.Delete(mockArchive);
    }

    [Fact]
    public async Task InstallAsync_WithEmptyPath_ThrowsActionableGodmanExceptionRatherThanArgumentException()
    {
        // Path.GetFullPath("") throws a raw ArgumentException. Before staging
        // extraction normalized targetDir up front, an empty --path fell through to
        // the "cannot determine parent directory" guard and got an actionable
        // message instead; this pins that behaviour back.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var installer = new InstallerService(_fixture.Paths, _fixture.Registry, _fixture.Environment);
        var platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;

        var ex = await Assert.ThrowsAsync<GodmanException>(() => installer.InstallAsync(new InstallRequest(
            "4.5.1", InstallEdition.Standard, platform, InstallScope.User,
            null, mockArchive, "", false, false, false)));

        Assert.NotNull(ex.Hint);
        Assert.Contains("--path", ex.Hint!);

        File.Delete(mockArchive);
    }

    private static string ComputeSha512(string filePath)
    {
        using var sha512 = SHA512.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(sha512.ComputeHash(stream));
    }
}
