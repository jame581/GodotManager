using GodotManager.Commands;
using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using Spectre.Console;
using Spectre.Console.Testing;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class CleanCommandTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public CleanCommandTests()
    {
        _fixture = new GodmanTestFixture();
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Clean_Prompt_SaysLauncherEntriesLeaveTheRealApplicationMenu()
    {
        // Launcher paths ignore GODMAN_HOME / GODMAN_GLOBAL_ROOT by design, so a clean run
        // against a sandbox GODMAN_HOME still deletes the entries in the real menu.
        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            app.Console.Interactive();
            app.Console.Input.PushTextWithEnter("n");

            var result = await app.RunAsync(["clean"]);

            Assert.Equal(0, result.ExitCode);
            var output = result.Output.Replace("\r", "").Replace("\n", "");
            Assert.Contains("launcher entries in your real application menu", output);
            Assert.Contains("GODMAN_HOME", output);
            Assert.Contains("Aborted", output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Clean_RemovesUserPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Path overrides aimed at Linux layout; behavior on Windows is trivial.
        }

        var userInstall = _fixture.Paths.GetInstallRoot(InstallScope.User);
        var userShim = _fixture.Paths.GetShimDirectory(InstallScope.User);
        var config = _fixture.Paths.ConfigDirectory;

        Directory.CreateDirectory(userInstall);
        Directory.CreateDirectory(userShim);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(userInstall, "dummy"), "x");
        File.WriteAllText(Path.Combine(config, "cfg"), "x");

        // Place a godot shim in the shim directory
        File.WriteAllText(Path.Combine(userShim, "godot"), "shim");

        var app = CliTestHarness.Create(_fixture);
        var result = await app.RunAsync(["clean", "--yes"]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(userInstall));
        Assert.False(Directory.Exists(config));

        // On Linux, shim directory should still exist (it's shared, e.g. ~/.local/bin),
        // but the godot shim file inside should be removed.
        Assert.False(File.Exists(Path.Combine(userShim, "godot")));
    }

    [Fact]
    public async Task Clean_OnLinux_OnlyRemovesShimFile_NotEntireDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // This test targets Linux behavior where shim dir is shared.
        }

        var userShim = _fixture.Paths.GetShimDirectory(InstallScope.User);

        Directory.CreateDirectory(userShim);

        // Place the godot shim and an unrelated binary in the same directory
        File.WriteAllText(Path.Combine(userShim, "godot"), "shim");
        File.WriteAllText(Path.Combine(userShim, "other-tool"), "keep me");

        var app = CliTestHarness.Create(_fixture);
        await app.RunAsync(["clean", "--yes"]);

        // The godot shim should be removed
        Assert.False(File.Exists(Path.Combine(userShim, "godot")));

        // The unrelated binary and directory must survive
        Assert.True(Directory.Exists(userShim), "Shim directory should not be deleted on Linux");
        Assert.True(File.Exists(Path.Combine(userShim, "other-tool")), "Other files in shim directory must not be deleted");
    }

    [Fact]
    public async Task Clean_RemovesGlobalPaths_WhenOverridesSet()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Global concept not supported on Windows.
        }

        var globalInstall = _fixture.Paths.GetInstallRoot(InstallScope.Global);
        var globalShim = _fixture.Paths.GetShimDirectory(InstallScope.Global);

        Directory.CreateDirectory(globalInstall);
        Directory.CreateDirectory(globalShim);
        File.WriteAllText(Path.Combine(globalInstall, "dummy"), "x");
        File.WriteAllText(Path.Combine(globalShim, "godot"), "shim");

        var app = CliTestHarness.Create(_fixture);
        var result = await app.RunAsync(["clean", "--yes"]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(globalInstall));
        // On Linux, only the shim file is removed, not the directory
        Assert.False(File.Exists(Path.Combine(globalShim, "godot")));
    }

    [Fact]
    public void CleanupAll_RemovesTheGlobalRegistryFile()
    {
        // On Windows GlobalRegistryFile sits one level above GetInstallRoot(Global)
        // (beside installs\ and bin\, not inside installs\), so the recursive delete
        // of the install root alone would never reach it. This runs on both CI
        // platforms (ubuntu-latest + windows-latest) and pins the fix regardless of
        // which directory shape is under test.
        var globalRegistryPath = _fixture.Paths.GlobalRegistryFile;
        Directory.CreateDirectory(Path.GetDirectoryName(globalRegistryPath)!);
        File.WriteAllText(globalRegistryPath, "{}");

        CleanCommand.CleanupAll(_fixture.Paths);

        Assert.False(File.Exists(globalRegistryPath));
    }

    [Fact]
    public void CleanupAll_RemovesTheDownloadCache()
    {
        File.WriteAllText(
            Path.Combine(_fixture.Paths.DownloadCacheDirectory, "abc123.archive"),
            "cached archive");

        CleanCommand.CleanupAll(_fixture.Paths);

        Assert.False(Directory.Exists(_fixture.Paths.DownloadCacheDirectory));
    }

    [Fact]
    public void Clean_RemovesOnlyGodmanLauncherEntries()
    {
        if (OperatingSystem.IsWindows()) return;
        var installPath = Path.Combine(_fixture.TempRoot, "i");
        Directory.CreateDirectory(installPath);
        var user = InstallEntryFactory.Create(path: installPath);
        var global = InstallEntryFactory.Create(scope: InstallScope.Global, path: installPath);
        _fixture.Launcher.Create(user);
        _fixture.Launcher.Create(global);
        var foreign = Path.Combine(_fixture.Paths.GetLauncherDirectory(InstallScope.User), "org.gnome.Foo.desktop");
        File.WriteAllText(foreign, "[Desktop Entry]\n");

        CleanCommand.CleanupAll(_fixture.Paths, [user, global]);

        Assert.False(_fixture.Launcher.Exists(user));
        Assert.False(_fixture.Launcher.Exists(global));
        Assert.False(File.Exists(_fixture.Paths.GetLauncherIconPath(InstallScope.User)));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void CleanupAll_ReportsGlobalLauncherEntriesItCouldNotRemove()
    {
        // Parity-review F3: an unprivileged clean printed "Removed" for each launcher
        // file it deleted and nothing at all for the global ones it could not, while
        // neighbouring global items print "Failed to remove".
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return; // POSIX permission simulation
        var installPath = Path.Combine(_fixture.TempRoot, "i");
        Directory.CreateDirectory(installPath);
        var global = InstallEntryFactory.Create(scope: InstallScope.Global, path: installPath);
        _fixture.Launcher.Create(global);
        var globalLauncherDir = _fixture.Paths.GetLauncherDirectory(InstallScope.Global);
        var desktopFile = Directory.GetFiles(globalLauncherDir, "godman-godot-*.desktop")[0];

        var console = new TestConsole();
        console.Profile.Width = 1000;
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = console;
        File.SetUnixFileMode(globalLauncherDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            CleanCommand.CleanupAll(_fixture.Paths, [global]);

            Assert.True(File.Exists(desktopFile));
            Assert.Contains($"Failed to remove launcher entry: {desktopFile}", console.Output);
        }
        finally
        {
            File.SetUnixFileMode(globalLauncherDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Clean_WithCorruptRegistry_StillSucceeds()
    {
        // Unelevated on Windows, any global target sends clean through UAC (RunElevatedCleanup),
        // which would relaunch the test host. The fixture creates the global root and shim
        // directory, so remove them: the corrupt user registry is what this test is about.
        if (OperatingSystem.IsWindows() && !GodotManager.Services.WindowsElevationHelper.IsElevated())
        {
            Directory.Delete(_fixture.Paths.GetInstallRoot(InstallScope.Global), recursive: true);
            Directory.Delete(_fixture.Paths.GetShimDirectory(InstallScope.Global), recursive: true);
        }

        File.WriteAllText(_fixture.Paths.RegistryFile, "{ not json");
        var result = await CliTestHarness.Create(_fixture).RunAsync(["clean", "--yes"]);
        Assert.Equal(0, result.ExitCode);
    }
}
