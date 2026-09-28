using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

/// <summary>
/// Linux decides elevation before the first machine-wide write (CLAUDE.md). Reproduces
/// Jan's manual test: an unprivileged <c>remove --delete</c> of a global entry tried to
/// delete the install's files before the registry write failed, and the hint printed a
/// literal <c>&lt;same arguments&gt;</c>. Each test locks the global install root the way
/// a root-owned <c>/usr/local/lib/godman</c> is locked for an ordinary user, then checks
/// that the command stops with the real command in the hint and has written nothing.
/// </summary>
public class LinuxElevationPrecheckE2ETests : IDisposable
{
    private static readonly UnixFileMode ReadOnlyDir = (UnixFileMode)Convert.ToInt32("555", 8);
    private static readonly UnixFileMode WritableDir = (UnixFileMode)Convert.ToInt32("755", 8);

    private readonly GodmanTestFixture _fixture;

    public LinuxElevationPrecheckE2ETests() => _fixture = new GodmanTestFixture();
    public void Dispose() => _fixture.Dispose();

    private string GlobalRoot => _fixture.Paths.GetInstallRoot(InstallScope.Global);

    private async Task<InstallEntry> SeedGlobalEntryAsync(bool active = false)
    {
        var installPath = Path.Combine(GlobalRoot, "4.5.1-standard-linux-global");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "Godot_v4.5.1-stable_linux.x86_64"), "fake");

        var entry = InstallEntryFactory.Create(scope: InstallScope.Global, version: "4.5.1", path: installPath);
        var registry = new InstallRegistry();
        registry.Installs.Add(entry);
        if (active)
        {
            registry.MarkActive(entry.Id);
        }

        await _fixture.Registry.SaveAsync(registry);
        return entry;
    }

    private async Task<(int ExitCode, string Output)> RunLockedAsync(params string[] args)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        File.SetUnixFileMode(GlobalRoot, ReadOnlyDir);
        try
        {
            var app = CliTestHarness.Create(_fixture);
            app.Console.Profile.Width = 1000;
            var originalConsole = AnsiConsole.Console;
            AnsiConsole.Console = app.Console;
            try
            {
                var result = await app.RunAsync(args);
                return (result.ExitCode, result.Output.Replace("\r", "").Replace("\n", ""));
            }
            finally
            {
                AnsiConsole.Console = originalConsole;
            }
        }
        finally
        {
            File.SetUnixFileMode(GlobalRoot, WritableDir);
        }
    }

    [Fact]
    public async Task Remove_Delete_GlobalEntry_Unwritable_StopsBeforeDeletingAnything()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        var entry = await SeedGlobalEntryAsync();
        var id = entry.Id.ToString("N");

        var (exitCode, output) = await RunLockedAsync("remove", id, "--delete");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Remove failed:", output);
        Assert.DoesNotContain("Failed to delete files", output); // no delete was attempted
        Assert.Contains($"remove {id} --delete", output);        // the real command, not a placeholder
        Assert.DoesNotContain("<same arguments>", output);

        Assert.True(File.Exists(Path.Combine(entry.Path, "Godot_v4.5.1-stable_linux.x86_64")));
        Assert.Contains((await _fixture.Registry.LoadAsync()).Installs, x => x.Id == entry.Id);
    }

    [Fact]
    public async Task Remove_Delete_DeletableFilesButUnwritableRegistry_KeepsTheFiles()
    {
        // The dangerous half of Jan's report: a global install whose files *are* deletable
        // (a --path outside the locked root) while the registry is not. Without the
        // pre-check the files went first and the registry write failed after, leaving a
        // registered install with nothing on disk.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        var installPath = Path.Combine(_fixture.TempRoot, "opt-godot");
        Directory.CreateDirectory(installPath);
        var exe = Path.Combine(installPath, "Godot_v4.5.1-stable_linux.x86_64");
        File.WriteAllText(exe, "fake");
        var entry = InstallEntryFactory.Create(scope: InstallScope.Global, version: "4.5.1", path: installPath);
        await _fixture.Registry.SaveAsync(new InstallRegistry { Installs = [entry] });
        var registryFile = _fixture.Paths.GlobalRegistryFile;
        File.SetUnixFileMode(registryFile, UnixFileMode.UserRead);
        try
        {
            var (exitCode, output) = await RunLockedAsync("remove", entry.Id.ToString("N"), "--delete");

            Assert.True(File.Exists(exe), "the install's files must not be deleted when the removal is refused");
            Assert.NotEqual(0, exitCode);
            Assert.Contains($"remove {entry.Id:N} --delete", output);
        }
        finally
        {
            File.SetUnixFileMode(registryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Contains((await _fixture.Registry.LoadAsync()).Installs, x => x.Id == entry.Id);
    }

    [Fact]
    public async Task Activate_GlobalEntry_Unwritable_StopsBeforeWritingTheShim()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        var entry = await SeedGlobalEntryAsync();
        var id = entry.Id.ToString("N");
        var shim = Path.Combine(_fixture.Paths.GetShimDirectory(InstallScope.Global), "godot");

        var (exitCode, output) = await RunLockedAsync("activate", id);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Activation failed:", output);
        Assert.Contains($"activate {id}", output);
        Assert.False(File.Exists(shim));
        Assert.Null((await _fixture.Registry.LoadAsync()).ActiveId);
    }

    [Fact]
    public async Task Activate_UserEntry_OverActiveGlobal_Unwritable_StopsAndKeepsTheGlobalActive()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        var global = await SeedGlobalEntryAsync(active: true);
        var userPath = Path.Combine(_fixture.TempRoot, "user-install");
        Directory.CreateDirectory(userPath);
        File.WriteAllText(Path.Combine(userPath, "Godot_v4.5.1-stable_linux.x86_64"), "fake");
        var user = InstallEntryFactory.Create(version: "4.5.1", path: userPath);
        var registry = await _fixture.Registry.LoadAsync();
        registry.Installs.Add(user);
        await _fixture.Registry.SaveAsync(registry);

        var (exitCode, output) = await RunLockedAsync("activate", user.Id.ToString("N"));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Activation failed:", output);
        Assert.Equal(global.Id, (await _fixture.Registry.LoadAsync()).ActiveId);
    }

    [Fact]
    public async Task Deactivate_ActiveGlobal_Unwritable_StopsAndKeepsItActive()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        var entry = await SeedGlobalEntryAsync(active: true);

        var (exitCode, output) = await RunLockedAsync("deactivate");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Deactivate failed:", output);
        Assert.Contains("sudo", output);
        Assert.EndsWith(" deactivate", output.TrimEnd());
        Assert.Equal(entry.Id, (await _fixture.Registry.LoadAsync()).ActiveId);
    }

    [Fact]
    public async Task Install_GlobalScope_Unwritable_StopsBeforeExtracting()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        Directory.CreateDirectory(GlobalRoot);
        var archive = MockArchiveFactory.CreateMockGodotArchive();
        try
        {
            var (exitCode, output) = await RunLockedAsync(
                "install", "--version", "4.5.1", "--archive", archive, "--platform", "linux", "--scope", "Global");

            Assert.NotEqual(0, exitCode);
            Assert.Contains("Install failed:", output);
            Assert.Contains("--scope Global", output);
            Assert.Empty(Directory.GetFileSystemEntries(GlobalRoot));
            Assert.Empty((await _fixture.Registry.LoadAsync()).Installs);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task Clean_GlobalTargets_Unwritable_StopsBeforeDeletingUserPaths()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        await SeedGlobalEntryAsync();
        var userInstallRoot = _fixture.Paths.GetInstallRoot(InstallScope.User);
        Directory.CreateDirectory(userInstallRoot);
        File.WriteAllText(Path.Combine(userInstallRoot, "marker"), "x");

        var (exitCode, output) = await RunLockedAsync("clean", "--yes");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Clean failed:", output);
        Assert.Contains("clean --yes", output);
        Assert.True(File.Exists(Path.Combine(userInstallRoot, "marker")));
        Assert.True(File.Exists(_fixture.Paths.GlobalRegistryFile));
    }

    [Fact]
    public async Task Clean_NoGlobalTargets_RunsUnprivilegedEvenWithAnUnwritablePrefix()
    {
        // The shared /usr/local/bin always exists and is never writable by a user, so a
        // directory's mere existence must not count as a global target: an ordinary user
        // cleaning only their own installs would be told to use sudo.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        var shimDir = _fixture.Paths.GetShimDirectory(InstallScope.Global);
        Directory.CreateDirectory(shimDir);
        if (Directory.Exists(GlobalRoot)) Directory.Delete(GlobalRoot, recursive: true);
        File.SetUnixFileMode(shimDir, ReadOnlyDir);
        try
        {
            var app = CliTestHarness.Create(_fixture);
            var originalConsole = AnsiConsole.Console;
            AnsiConsole.Console = app.Console;
            try
            {
                var result = await app.RunAsync(["clean", "--yes"]);
                Assert.Equal(0, result.ExitCode);
                Assert.DoesNotContain("Clean failed:", result.Output);
            }
            finally
            {
                AnsiConsole.Console = originalConsole;
            }
        }
        finally
        {
            File.SetUnixFileMode(shimDir, WritableDir);
        }
    }
}
