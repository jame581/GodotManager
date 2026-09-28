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
    public async Task ApplyActiveAsync_SetsProcessEnvironmentVariable()
    {
        // Arrange
        var tempDir = Path.Combine(_fixture.TempRoot, "env-process");
        Directory.CreateDirectory(tempDir);

        var exeName = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        File.WriteAllText(Path.Combine(tempDir, exeName), "fake executable");

        var entry = InstallEntryFactory.Create(path: tempDir);

        // Clear any existing value
        System.Environment.SetEnvironmentVariable("GODOT_HOME", null, EnvironmentVariableTarget.Process);

        // Act
        await _fixture.Environment.ApplyActiveAsync(entry, dryRun: false, createDesktopShortcut: false);

        // Assert
        var envValue = System.Environment.GetEnvironmentVariable("GODOT_HOME", EnvironmentVariableTarget.Process);
        Assert.Equal(tempDir, envValue);

        // Cleanup
        System.Environment.SetEnvironmentVariable("GODOT_HOME", null, EnvironmentVariableTarget.Process);
    }

    [Fact]
    public async Task Fixture_DoesNotLeakItsShimDirectoryIntoThePersistedUserPath()
    {
        // Windows-only by construction: ApplyWindows is the branch that appends the
        // shim directory to the persisted User PATH. Guarded rather than asserted
        // cross-platform so this makes no claim about how the User target behaves
        // on Unix.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // GODMAN_HOME redirects where files land, but not where EnvironmentService
        // writes PATH -- that always went to the real registry. Every activating test
        // therefore appended its own temp shim directory permanently, which is how a
        // real machine ended up with 49 dead godman-test entries in its User PATH.
        var before = System.Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);

        using (var fixture = new GodmanTestFixture())
        {
            var installDir = Path.Combine(fixture.TempRoot, "path-leak");
            Directory.CreateDirectory(installDir);
            File.WriteAllText(Path.Combine(installDir, "Godot_v4.5.1-stable_win64.exe"), "fake executable");

            await fixture.Environment.ApplyActiveAsync(
                InstallEntryFactory.Create(path: installDir), dryRun: false, createDesktopShortcut: false);

            Assert.Contains(
                fixture.Paths.GetShimDirectory(InstallScope.User),
                System.Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "");
        }

        Assert.Equal(before, System.Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
    }

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
    public async Task ParseShimTarget_ReadsBackTheExecutableTheWriterPutThere()
    {
        // Round-trips through the real writer, so doctor's parser cannot drift away from
        // the shim format without this failing -- on either OS's shim.
        var tempDir = Path.Combine(_fixture.TempRoot, "shim-parse");
        Directory.CreateDirectory(tempDir);
        var exeName = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        var exePath = Path.Combine(tempDir, exeName);
        File.WriteAllText(exePath, "fake executable");
        var entry = InstallEntryFactory.Create(path: tempDir);
        entry.LauncherEntry = false;

        await _fixture.Environment.ApplyActiveAsync(entry, dryRun: false, createDesktopShortcut: false);

        var shimPath = Path.Combine(
            _fixture.Paths.GetShimDirectory(InstallScope.User),
            OperatingSystem.IsWindows() ? "godot.cmd" : "godot");
        Assert.Equal(exePath, GodotManager.Services.EnvironmentService.ParseShimTarget(File.ReadAllText(shimPath)));
    }

    [Fact]
    public void InspectShim_ReportsAbsentStaleHealthyAndForeignShims()
    {
        var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
        var shimPath = Path.Combine(_fixture.Paths.GetShimDirectory(InstallScope.User), shimName);
        var liveTarget = Path.Combine(_fixture.TempRoot, "live", "Godot");
        var goneTarget = Path.Combine(_fixture.TempRoot, "gone", "Godot");
        Directory.CreateDirectory(Path.GetDirectoryName(liveTarget)!);
        File.WriteAllText(liveTarget, "fake");
        string Shim(string target) => OperatingSystem.IsWindows()
            ? $"@echo off\r\n\"{target}\" %*\r\n"
            : $"#!/usr/bin/env bash\nexec \"{target}\" \"$@\"\n";

        if (File.Exists(shimPath)) File.Delete(shimPath);
        Assert.Equal((shimPath, false, (string?)null),
            GodotManager.Services.EnvironmentService.InspectShim(_fixture.Paths, InstallScope.User));

        Directory.CreateDirectory(Path.GetDirectoryName(shimPath)!);
        File.WriteAllText(shimPath, Shim(goneTarget));
        Assert.Equal((shimPath, true, goneTarget),
            GodotManager.Services.EnvironmentService.InspectShim(_fixture.Paths, InstallScope.User));

        File.WriteAllText(shimPath, Shim(liveTarget));
        Assert.Equal((shimPath, true, (string?)null),
            GodotManager.Services.EnvironmentService.InspectShim(_fixture.Paths, InstallScope.User));

        File.WriteAllText(shimPath, "#!/bin/sh\necho hand-written\n");
        Assert.Equal((shimPath, true, (string?)null),
            GodotManager.Services.EnvironmentService.InspectShim(_fixture.Paths, InstallScope.User));
    }

    [Fact]
    public void InspectShim_LooksInTheRequestedScope()
    {
        var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
        var inspected = GodotManager.Services.EnvironmentService.InspectShim(_fixture.Paths, InstallScope.Global);
        Assert.Equal(Path.Combine(_fixture.Paths.GetShimDirectory(InstallScope.Global), shimName), inspected.ShimPath);
    }

    [Fact]
    public void ParseShimTarget_ParsesBothShimFormats_AndIgnoresForeignFiles()
    {
        Assert.Equal("/opt/g/Godot", GodotManager.Services.EnvironmentService.ParseShimTarget(
            "#!/usr/bin/env bash\nsource \"/h/.config/godman/env.sh\" 2>/dev/null\nexec \"/opt/g/Godot\" \"$@\"\n"));
        Assert.Equal(@"C:\g\Godot.exe", GodotManager.Services.EnvironmentService.ParseShimTarget(
            "@echo off\r\n\"C:\\g\\Godot.exe\" %*\r\n"));
        Assert.Null(GodotManager.Services.EnvironmentService.ParseShimTarget("#!/bin/sh\n"));
    }

    [Fact]
    public void ParseShimSourcedScript_ReadsTheSourceLine_AndIgnoresEverythingElse()
    {
        Assert.Equal("/root/.config/godman/env.sh", GodotManager.Services.EnvironmentService.ParseShimSourcedScript(
            "#!/usr/bin/env bash\nsource \"/root/.config/godman/env.sh\" 2>/dev/null\nexec \"/opt/g/Godot\" \"$@\"\n"));
        Assert.Null(GodotManager.Services.EnvironmentService.ParseShimSourcedScript("@echo off\r\n\"C:\\g\\Godot.exe\" %*\r\n"));
        Assert.Null(GodotManager.Services.EnvironmentService.ParseShimSourcedScript("#!/bin/sh\nexec \"/opt/g/Godot\"\n"));
    }

    [Fact]
    public async Task ParseShimSourcedScript_ReadsBackTheEnvScriptTheWriterPutThere()
    {
        if (OperatingSystem.IsWindows()) return; // godot.cmd sources nothing
        var tempDir = Path.Combine(_fixture.TempRoot, "shim-source");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Godot_v4.5.1-stable_linux.x86_64"), "fake executable");
        var entry = InstallEntryFactory.Create(path: tempDir);
        entry.LauncherEntry = false;

        await _fixture.Environment.ApplyActiveAsync(entry, dryRun: false, createDesktopShortcut: false);

        var shimPath = Path.Combine(_fixture.Paths.GetShimDirectory(InstallScope.User), "godot");
        Assert.Equal(_fixture.Paths.EnvScriptPath,
            GodotManager.Services.EnvironmentService.ParseShimSourcedScript(File.ReadAllText(shimPath)));
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
        if (OperatingSystem.IsWindows() && !GodotManager.Services.WindowsElevationHelper.IsElevated())
        {
            return; // Global scope writes to HKLM; CI runners have admin, local devs typically don't
        }

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
        // The desktop resolves under the fixture's GODMAN_LAUNCHER_ROOT, so this never
        // touches the real user's desktop and the assertion can be live.
        var desktopFolder = _fixture.Paths.DesktopDirectory;
        var shortcutName = $"Godot {entry.Version} ({entry.Edition}).lnk";
        var desktopShortcut = Path.Combine(desktopFolder, shortcutName);

        Assert.True(File.Exists(desktopShortcut), "Desktop shortcut should be created");
    }
}
