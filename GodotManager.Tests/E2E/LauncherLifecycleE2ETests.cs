using GodotManager.Domain;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

public class LauncherLifecycleE2ETests : IDisposable
{
    private readonly GodmanTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private static string Platform => OperatingSystem.IsWindows() ? "windows" : "linux";

    private async Task<InstallEntry> InstallAsync(string version, params string[] extra)
    {
        var archive = MockArchiveFactory.CreateMockGodotArchive();
        try
        {
            var app = CliTestHarness.Create(_fixture);
            var result = await app.RunAsync(["install", "--version", version, "--archive", archive, "--platform", Platform, .. extra]);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            File.Delete(archive);
        }

        var registry = await _fixture.Registry.LoadAsync();
        return registry.Installs.Single(x => x.Version == version);
    }

    [Fact]
    public async Task Install_CreatesLauncherEntry_AndRecordsIt()
    {
        if (OperatingSystem.IsWindows()) return; // .lnk creation is manual-test territory
        var entry = await InstallAsync("4.5.1");

        Assert.True(_fixture.Launcher.Exists(entry));
        Assert.True(entry.LauncherEntry);
    }

    [Fact]
    public async Task Install_NoShortcut_CreatesNothing_AndRecordsTheOptOut()
    {
        var entry = await InstallAsync("4.5.1", "--no-shortcut");

        Assert.False(_fixture.Launcher.Exists(entry));
        Assert.False(entry.LauncherEntry);
    }

    [Fact]
    public async Task Activate_LegacyEntry_BackfillsLauncherEntry()
    {
        if (OperatingSystem.IsWindows()) return;
        var entry = await InstallAsync("4.5.1", "--no-shortcut");
        var registry = await _fixture.Registry.LoadAsync();
        registry.Installs.Single().LauncherEntry = null;   // what a pre-1.4.0 registry holds
        await _fixture.Registry.SaveAsync(registry);

        var result = await CliTestHarness.Create(_fixture).RunAsync(["activate", entry.Id.ToString()]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(_fixture.Launcher.Exists(entry));
    }

    [Fact]
    public async Task Activate_OptedOutEntry_DoesNotCreateLauncherEntry()
    {
        var entry = await InstallAsync("4.5.1", "--no-shortcut");

        var result = await CliTestHarness.Create(_fixture).RunAsync(["activate", entry.Id.ToString()]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(_fixture.Launcher.Exists(entry));
    }

    // Regression guard only: on Linux deactivate never touched launcher files, and the
    // one real change (RemoveWindows now deletes just the desktop shortcut, not the Start
    // Menu entry) is Windows-only and covered by the manual checklist.
    [Fact]
    public async Task Deactivate_KeepsTheLauncherEntry()
    {
        if (OperatingSystem.IsWindows()) return;
        var entry = await InstallAsync("4.5.1", "--activate");

        var result = await CliTestHarness.Create(_fixture).RunAsync(["deactivate"]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(_fixture.Launcher.Exists(entry));
    }

    // Pins the end state only: user -> user overwrites the same shim file, so this passes
    // without the previous-activation cleanup. The real guard is
    // InstallerServiceIntegrationTests.InstallAsync_WithActivate_RemovesThePreviouslyActiveInstallsShim.
    [Fact]
    public async Task InstallWithActivate_OverAnActiveInstall_EndsWithTheShimOnTheNewInstall()
    {
        if (OperatingSystem.IsWindows()) return; // Unix shim content is what we can inspect here
        var first = await InstallAsync("4.5.1", "--activate");
        var second = await InstallAsync("4.6.0", "--activate");

        var shim = File.ReadAllText(Path.Combine(_fixture.Paths.GetShimDirectory(InstallScope.User), "godot"));
        Assert.Contains(second.Path, shim);
        Assert.DoesNotContain(first.Path, shim);
        var registry = await _fixture.Registry.LoadAsync();
        Assert.Equal(second.Id, registry.ActiveId);
    }

    [Fact]
    public async Task InstallWithActivate_WithASurvivingGlobalShim_WarnsOnLinux()
    {
        // Parity-review F1 through `install --activate`: same shared check as `activate`.
        if (OperatingSystem.IsWindows()) return;
        var globalShimDir = _fixture.Paths.GetShimDirectory(InstallScope.Global);
        Directory.CreateDirectory(globalShimDir);
        File.WriteAllText(Path.Combine(globalShimDir, "godot"), "#!/bin/sh\n");

        var archive = MockArchiveFactory.CreateMockGodotArchive();
        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["install", "--version", "4.5.1", "--archive", archive, "--platform", Platform, "--activate"]);

            Assert.Equal(0, result.ExitCode);
            var output = result.Output.Replace("\r", "").Replace("\n", "");
            Assert.Contains("leftover from an earlier global activation", output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task Install_Force_OverTheSamePath_LeavesOneLauncherEntry()
    {
        if (OperatingSystem.IsWindows()) return;
        // Pinned --path: each mock archive has a random temp-file name, and the install
        // folder is derived from it, so without this the two installs never share a
        // target and --force never replaces anything.
        var target = Path.Combine(_fixture.Paths.GetInstallRoot(InstallScope.User), "godot-4.5.1-force");
        await InstallAsync("4.5.1", "--path", target);
        await InstallAsync("4.5.1", "--path", target, "--force");

        var files = Directory.GetFiles(_fixture.Paths.GetLauncherDirectory(InstallScope.User), "godman-godot-*.desktop");
        Assert.Single(files);
    }

    [Fact]
    public async Task Install_Force_WhenRegistrySaveFails_KeepsTheReplacedEntrysLauncher()
    {
        // Parity-review F5: the replaced entry's launcher was deleted before the save, so a
        // failed save left the still-registered old install with no app-menu entry.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return; // POSIX permission simulation
        var target = Path.Combine(_fixture.Paths.GetInstallRoot(InstallScope.User), "godot-4.5.1-force");
        var original = await InstallAsync("4.5.1", "--path", target);
        Assert.True(_fixture.Launcher.Exists(original));

        File.SetUnixFileMode(_fixture.Paths.RegistryFile, UnixFileMode.UserRead);
        var archive = MockArchiveFactory.CreateMockGodotArchive();
        try
        {
            var result = await CliTestHarness.Create(_fixture).RunAsync(
                ["install", "--version", "4.5.1", "--archive", archive, "--platform", Platform, "--path", target, "--force"]);

            Assert.NotEqual(0, result.ExitCode);
            var registry = await _fixture.Registry.LoadAsync();
            Assert.Equal(original.Id, registry.Installs.Single().Id);
            Assert.True(_fixture.Launcher.Exists(original));
        }
        finally
        {
            File.SetUnixFileMode(_fixture.Paths.RegistryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task InstallWithActivate_WhenRegistrySaveFails_WritesNoLauncherEntry()
    {
        // Activation inside InstallAsync used to write the launcher entry before the
        // registry save, so a failed save left an app-menu entry for an install the
        // registry never recorded -- on every `install --activate` and TUI install.
        // The *user* registry file is locked, not the global one: LinuxElevation.Check
        // probes the global file up front, so locking that stops the install before any
        // launcher write and would no longer pin the save-then-launcher ordering.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return; // POSIX permission simulation
        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Paths.RegistryFile)!);
        File.WriteAllText(_fixture.Paths.RegistryFile, "{\"installs\":[]}");
        File.SetUnixFileMode(_fixture.Paths.RegistryFile, UnixFileMode.UserRead);
        var launcherDir = _fixture.Paths.GetLauncherDirectory(InstallScope.User);
        var archive = MockArchiveFactory.CreateMockGodotArchive();
        try
        {
            var result = await CliTestHarness.Create(_fixture).RunAsync(
                ["install", "--version", "4.5.1", "--archive", archive, "--platform", Platform, "--activate"]);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Empty((await _fixture.Registry.LoadAsync()).Installs);
            Assert.True(
                !Directory.Exists(launcherDir) || Directory.GetFiles(launcherDir, "godman-godot-*.desktop").Length == 0,
                "no launcher entry may exist for an install whose registry save failed");
        }
        finally
        {
            File.SetUnixFileMode(_fixture.Paths.RegistryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task Remove_NonActiveInstall_DeletesItsLauncherEntry()
    {
        if (OperatingSystem.IsWindows()) return;
        var entry = await InstallAsync("4.5.1");

        var result = await CliTestHarness.Create(_fixture).RunAsync(["remove", entry.Id.ToString()]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(_fixture.Launcher.Exists(entry));
    }

    [Fact]
    public async Task Remove_NonActiveSibling_KeepsTheActiveInstallsDesktopShortcut()
    {
        // Windows names the desktop shortcut after version + edition only, so two installs
        // of the same version share it. The shortcut belongs to the activation: removing
        // the non-active sibling must not take the active install's shortcut with it.
        if (!OperatingSystem.IsWindows()) return; // desktop shortcuts are Windows-only
        var active = InstallEntryFactory.Create(path: Path.Combine(_fixture.TempRoot, "a"));
        var sibling = InstallEntryFactory.Create(path: Path.Combine(_fixture.TempRoot, "b"));
        var registry = await _fixture.Registry.LoadAsync();
        registry.Installs.Add(active);
        registry.Installs.Add(sibling);
        registry.ActiveId = active.Id;
        await _fixture.Registry.SaveAsync(registry);

        Directory.CreateDirectory(_fixture.Paths.DesktopDirectory);
        var shortcut = Path.Combine(_fixture.Paths.DesktopDirectory, $"Godot {active.Version} ({active.Edition}).lnk");
        File.WriteAllText(shortcut, "shortcut");

        var result = await CliTestHarness.Create(_fixture).RunAsync(["remove", sibling.Id.ToString()]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(shortcut), "the active install's desktop shortcut was deleted");
    }

    [Fact]
    public async Task Remove_ActiveInstall_DeletesItsDesktopShortcut()
    {
        // The other half: the shortcut still goes when its own (active) install is removed,
        // through deactivation rather than through the launcher entry's delete.
        if (!OperatingSystem.IsWindows()) return;
        var active = InstallEntryFactory.Create(path: Path.Combine(_fixture.TempRoot, "a"));
        var registry = await _fixture.Registry.LoadAsync();
        registry.Installs.Add(active);
        registry.ActiveId = active.Id;
        await _fixture.Registry.SaveAsync(registry);

        Directory.CreateDirectory(_fixture.Paths.DesktopDirectory);
        var shortcut = Path.Combine(_fixture.Paths.DesktopDirectory, $"Godot {active.Version} ({active.Edition}).lnk");
        File.WriteAllText(shortcut, "shortcut");

        var result = await CliTestHarness.Create(_fixture).RunAsync(["remove", active.Id.ToString()]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(shortcut), "the removed active install's desktop shortcut was left behind");
    }

    [Fact]
    public async Task Remove_ActiveInstall_DeletesItsLauncherEntry()
    {
        if (OperatingSystem.IsWindows()) return;
        var entry = await InstallAsync("4.5.1", "--activate");

        var result = await CliTestHarness.Create(_fixture).RunAsync(["remove", entry.Id.ToString()]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(_fixture.Launcher.Exists(entry));
    }

    [Fact]
    public async Task Remove_WhenRegistrySaveFails_KeepsLauncherEntry()
    {
        // A user-scope entry with the *user* registry file locked: the global file would be
        // caught by LinuxElevation.Check before the launcher is touched, so it would no
        // longer pin that the launcher entry is deleted only after the save succeeds.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return; // POSIX permission simulation
        var installPath = Path.Combine(_fixture.TempRoot, "user-install");
        Directory.CreateDirectory(installPath);
        var entry = InstallEntryFactory.Create(scope: InstallScope.User, path: installPath);
        entry.LauncherEntry = true;
        await _fixture.Registry.SaveAsync(new InstallRegistry { Installs = [entry] });
        _fixture.Launcher.Create(entry);
        File.SetUnixFileMode(_fixture.Paths.RegistryFile, UnixFileMode.UserRead);

        try
        {
            var result = await CliTestHarness.Create(_fixture).RunAsync(["remove", entry.Id.ToString()]);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(_fixture.Launcher.Exists(entry));
        }
        finally
        {
            File.SetUnixFileMode(_fixture.Paths.RegistryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
