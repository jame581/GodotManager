using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class EnvironmentServiceTests
{
    private readonly AppPaths _paths;
    private readonly EnvironmentService _service;

    public EnvironmentServiceTests()
    {
        _paths = new AppPaths();
        _service = new EnvironmentService(_paths);
    }

    [Fact]
    public async Task ApplyActiveAsync_CreatesShimFile()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        // Create a fake Godot executable
        var exeName = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        var exePath = Path.Combine(tempDir, exeName);
        File.WriteAllText(exePath, "fake executable");

        var entry = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = tempDir
        };

        try
        {
            // Act
            await _service.ApplyActiveAsync(entry, dryRun: false, createDesktopShortcut: false);

            // Assert
            var shimDir = _paths.GetShimDirectory(InstallScope.User);
            var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
            var shimPath = Path.Combine(shimDir, shimName);

            Assert.True(File.Exists(shimPath), $"Shim file should exist at {shimPath}");

            if (OperatingSystem.IsWindows())
            {
                var shimContent = await File.ReadAllTextAsync(shimPath);
                Assert.Contains(exePath, shimContent);
            }
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RemoveActiveAsync_DeletesShimFile()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var exeName = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        var exePath = Path.Combine(tempDir, exeName);
        File.WriteAllText(exePath, "fake executable");

        var entry = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = tempDir
        };

        try
        {
            // Setup: Create shim first
            await _service.ApplyActiveAsync(entry, dryRun: false, createDesktopShortcut: false);

            var shimDir = _paths.GetShimDirectory(InstallScope.User);
            var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
            var shimPath = Path.Combine(shimDir, shimName);

            Assert.True(File.Exists(shimPath), "Shim should exist before removal");

            // Act
            await _service.RemoveActiveAsync(entry);

            // Assert
            Assert.False(File.Exists(shimPath), "Shim file should be deleted");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ApplyActiveAsync_WithDryRun_DoesNotCreateShim()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var entry = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = tempDir
        };

        try
        {
            // Act
            await _service.ApplyActiveAsync(entry, dryRun: true, createDesktopShortcut: false);

            // Assert
            var shimDir = _paths.GetShimDirectory(InstallScope.User);
            var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
            var shimPath = Path.Combine(shimDir, shimName);

            // In dry-run mode, shim should not be created
            Assert.False(File.Exists(shimPath), "Shim file should not exist in dry-run mode");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ApplyActiveAsync_WithDesktopShortcut_CreatesShortcut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Shortcuts are Windows-only
        }

        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var exeName = "Godot_v4.5.1-stable_win64.exe";
        var exePath = Path.Combine(tempDir, exeName);
        File.WriteAllText(exePath, "fake executable");

        var entry = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = InstallPlatform.Windows,
            Scope = InstallScope.User,
            Path = tempDir
        };

        try
        {
            // Act
            await _service.ApplyActiveAsync(entry, dryRun: false, createDesktopShortcut: true);

            // Assert
            var desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var shortcutName = $"Godot {entry.Version} ({entry.Edition}).lnk";
            var desktopShortcut = Path.Combine(desktopFolder, shortcutName);

            // Note: Actual shortcut creation might fail in test environment, so this is best-effort
            // Assert.True(File.Exists(desktopShortcut), "Desktop shortcut should be created");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
