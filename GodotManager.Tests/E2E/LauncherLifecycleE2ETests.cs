using GodotManager.Domain;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
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
}
