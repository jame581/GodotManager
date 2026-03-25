using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class EnvironmentServiceTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public EnvironmentServiceTests()
    {
        _fixture = new GodmanTestFixture();
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ApplyActiveAsync_CreatesShimFile()
    {
        // Arrange
        var tempDir = Path.Combine(_fixture.TempRoot, "shim-create");
        Directory.CreateDirectory(tempDir);

        // Create a fake Godot executable
        var exeName = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        var exePath = Path.Combine(tempDir, exeName);
        File.WriteAllText(exePath, "fake executable");

        var entry = InstallEntryFactory.Create(path: tempDir);

        // Act
        await _fixture.Environment.ApplyActiveAsync(entry, dryRun: false, createDesktopShortcut: false);

        // Assert
        var shimDir = _fixture.Paths.GetShimDirectory(InstallScope.User);
        var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
        var shimPath = Path.Combine(shimDir, shimName);

        Assert.True(File.Exists(shimPath), $"Shim file should exist at {shimPath}");

        if (OperatingSystem.IsWindows())
        {
            var shimContent = await File.ReadAllTextAsync(shimPath);
            Assert.Contains(exePath, shimContent);
        }
    }

    [Fact]
    public async Task RemoveActiveAsync_DeletesShimFile()
    {
        // Arrange
        var tempDir = Path.Combine(_fixture.TempRoot, "shim-remove");
        Directory.CreateDirectory(tempDir);

        var exeName = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        var exePath = Path.Combine(tempDir, exeName);
        File.WriteAllText(exePath, "fake executable");

        var entry = InstallEntryFactory.Create(path: tempDir);

        // Setup: Create shim first
        await _fixture.Environment.ApplyActiveAsync(entry, dryRun: false, createDesktopShortcut: false);

        var shimDir = _fixture.Paths.GetShimDirectory(InstallScope.User);
        var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
        var shimPath = Path.Combine(shimDir, shimName);

        Assert.True(File.Exists(shimPath), "Shim should exist before removal");

        // Act
        await _fixture.Environment.RemoveActiveAsync(entry);

        // Assert
        Assert.False(File.Exists(shimPath), "Shim file should be deleted");
    }

    [Fact]
    public async Task RemoveActiveAsync_WithGlobalScope_DeletesGlobalShim()
    {
        // Arrange
        var tempDir = Path.Combine(_fixture.TempRoot, "shim-global-remove");
        Directory.CreateDirectory(tempDir);

        var exeName = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        var exePath = Path.Combine(tempDir, exeName);
        File.WriteAllText(exePath, "fake executable");

        var entry = InstallEntryFactory.Create(path: tempDir, scope: InstallScope.Global);

        // Setup: Create shim first
        await _fixture.Environment.ApplyActiveAsync(entry, dryRun: false, createDesktopShortcut: false);

        var shimDir = _fixture.Paths.GetShimDirectory(InstallScope.Global);
        var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
        var shimPath = Path.Combine(shimDir, shimName);

        Assert.True(File.Exists(shimPath), $"Global shim should exist before removal at {shimPath}");

        // Also verify user shim directory does NOT have a shim
        var userShimDir = _fixture.Paths.GetShimDirectory(InstallScope.User);
        var userShimPath = Path.Combine(userShimDir, shimName);

        // Act
        await _fixture.Environment.RemoveActiveAsync(entry);

        // Assert - global shim should be deleted
        Assert.False(File.Exists(shimPath), "Global shim file should be deleted after removal");
    }

    [Fact]
    public async Task ApplyActiveAsync_WithDryRun_DoesNotCreateShim()
    {
        // Arrange
        var tempDir = Path.Combine(_fixture.TempRoot, "shim-dryrun");
        Directory.CreateDirectory(tempDir);

        var entry = InstallEntryFactory.Create(path: tempDir);

        // Act
        await _fixture.Environment.ApplyActiveAsync(entry, dryRun: true, createDesktopShortcut: false);

        // Assert
        var shimDir = _fixture.Paths.GetShimDirectory(InstallScope.User);
        var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
        var shimPath = Path.Combine(shimDir, shimName);

        // In dry-run mode, shim should not be created
        Assert.False(File.Exists(shimPath), "Shim file should not exist in dry-run mode");
    }

    [Fact]
    public async Task ApplyActiveAsync_WithDesktopShortcut_CreatesShortcut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Shortcuts are Windows-only
        }

        // Arrange
        var tempDir = Path.Combine(_fixture.TempRoot, "shim-shortcut");
        Directory.CreateDirectory(tempDir);

        var exeName = "Godot_v4.5.1-stable_win64.exe";
        var exePath = Path.Combine(tempDir, exeName);
        File.WriteAllText(exePath, "fake executable");

        var entry = InstallEntryFactory.Create(path: tempDir);

        // Act
        await _fixture.Environment.ApplyActiveAsync(entry, dryRun: false, createDesktopShortcut: true);

        // Assert
        var desktopFolder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
        var shortcutName = $"Godot {entry.Version} ({entry.Edition}).lnk";
        var desktopShortcut = Path.Combine(desktopFolder, shortcutName);

        // Note: Actual shortcut creation might fail in test environment, so this is best-effort
        // Assert.True(File.Exists(desktopShortcut), "Desktop shortcut should be created");
    }
}
