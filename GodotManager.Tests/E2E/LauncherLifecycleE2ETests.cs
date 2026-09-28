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

    [Fact]
    public async Task InstallWithActivate_OverAnActiveInstall_CleansUpThePreviousShim()
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
            Assert.Contains("needs root to remove", output);
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
    public async Task Remove_NonActiveInstall_DeletesItsLauncherEntry()
    {
        if (OperatingSystem.IsWindows()) return;
        var entry = await InstallAsync("4.5.1");

        var result = await CliTestHarness.Create(_fixture).RunAsync(["remove", entry.Id.ToString()]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(_fixture.Launcher.Exists(entry));
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
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return; // POSIX permission simulation
        var installPath = Path.Combine(_fixture.TempRoot, "global-install");
        Directory.CreateDirectory(installPath);
        var entry = InstallEntryFactory.Create(scope: InstallScope.Global, path: installPath);
        entry.LauncherEntry = true;
        await _fixture.Registry.SaveAsync(new InstallRegistry { Installs = [entry] });
        _fixture.Launcher.Create(entry);
        File.SetUnixFileMode(_fixture.Paths.GlobalRegistryFile, UnixFileMode.UserRead);

        try
        {
            var result = await CliTestHarness.Create(_fixture).RunAsync(["remove", entry.Id.ToString()]);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(_fixture.Launcher.Exists(entry));
        }
        finally
        {
            File.SetUnixFileMode(_fixture.Paths.GlobalRegistryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
