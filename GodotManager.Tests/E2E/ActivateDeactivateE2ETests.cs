using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using Spectre.Console;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

public class ActivateDeactivateE2ETests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public ActivateDeactivateE2ETests() => _fixture = new GodmanTestFixture();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Activate_ValidId_ExitsZeroAndSetsActive()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);

        var exeName = OperatingSystem.IsWindows()
            ? "Godot_v4.5.1-stable_win64.exe"
            : "Godot_v4.5.1-stable_linux.x86_64";
        File.WriteAllText(Path.Combine(installPath, exeName), "fake");

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync(["activate", entry.Id.ToString()]);

        Assert.Equal(0, result.ExitCode);

        var updated = await _fixture.Registry.LoadAsync();
        Assert.Equal(entry.Id, updated.ActiveId);
    }

    [Fact]
    public async Task Activate_UserInstall_WithASurvivingGlobalShim_WarnsOnLinux()
    {
        // Parity-review F1: an unprivileged RemoveUnix cannot delete the global shim, so
        // it silently outranked the user shim. The activation still succeeds; the user
        // has to be told which file wins and how to remove it.
        if (OperatingSystem.IsWindows()) return;

        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "Godot_v4.5.1-stable_linux.x86_64"), "fake");

        var globalShimDir = _fixture.Paths.GetShimDirectory(InstallScope.Global);
        Directory.CreateDirectory(globalShimDir);
        var globalShim = Path.Combine(globalShimDir, "godot");
        File.WriteAllText(globalShim, "#!/bin/sh\n");

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["activate", entry.Id.ToString()]);

            Assert.Equal(0, result.ExitCode);
            // Spectre wraps at the test console width; join lines before matching.
            var output = result.Output.Replace("\r", "").Replace("\n", "");
            Assert.Contains("leftover from an earlier global activation", output);
            Assert.Contains("sudo rm", output);
            Assert.Contains("godot", output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Activate_InvalidId_ExitsNonZero()
    {
        var app = CliTestHarness.Create(_fixture);
        await _fixture.Registry.SaveAsync(new InstallRegistry());

        var result = await app.RunAsync(["activate", Guid.NewGuid().ToString()]);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Activate_DryRun_DoesNotModifyRegistry()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync(["activate", entry.Id.ToString(), "--dry-run"]);

        Assert.Equal(0, result.ExitCode);

        var updated = await _fixture.Registry.LoadAsync();
        Assert.Null(updated.ActiveId);
    }

    [Fact]
    public async Task Deactivate_WithActiveInstall_ExitsZeroAndClears()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync(["deactivate"]);

        Assert.Equal(0, result.ExitCode);

        var updated = await _fixture.Registry.LoadAsync();
        Assert.Null(updated.ActiveId);
    }

    [Fact]
    public async Task Deactivate_WithNoActive_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture);
        await _fixture.Registry.SaveAsync(new InstallRegistry());

        var result = await app.RunAsync(["deactivate"]);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Deactivate_WithCorruptRegistry_RendersOwnPrefixInsteadOfCrashing()
    {
        // DeactivateCommand.ExecuteAsync had no top-level catch at all (task-13-brief.md
        // Bug A groups it with RemoveCommand for this): any exception out of
        // _registry.LoadAsync/SaveAsync -- not just a GodmanException -- escaped to
        // Spectre.Console.Cli's own default handler, which renders a bare
        // "Error: <message>" with no command-specific prefix and, for a
        // GodmanException specifically, no hint line.
        //
        // Unlike RemoveCommand, DeactivateCommand never mutates registry.Installs,
        // so it can't be driven down RegistryService.SaveAsync's *global*-write
        // GodmanException+Hint branch the way Remove_GlobalScopeEntry_WithoutPrivilege_
        // RendersHint does above: SaveAsync only attempts that write when the set of
        // global-scope entries actually changed, and Deactivate never changes it (it
        // only clears ActiveId). A malformed user registry file is what remains
        // reachable: it makes LoadAsync throw before any of that, exercising the
        // same missing-top-level-catch defect through the plain-Exception branch.
        // GodmanExceptionRendererTests pins the Hint-carrying branch directly against
        // the same shared GodmanExceptionRenderer.Render this command now calls.
        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Paths.RegistryFile)!);
        await File.WriteAllTextAsync(_fixture.Paths.RegistryFile, "{ not valid json");

        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["deactivate"]);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Deactivate failed:", result.Output);
            Assert.DoesNotContain("Error:", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Activate_GlobalScopeEntry_WithoutPrivilege_RendersHint()
    {
        // Reproduces Jan's manual-test observation (task-13-brief.md Bug B):
        // ActivateCommand.cs:75 used to hardcode "Access denied while updating
        // environment for this scope." with no hint at all when
        // EnvironmentService.ApplyActiveAsync throws UnauthorizedAccessException for
        // an unprivileged global-scope activation. On Linux that throw site is
        // ApplyUnix's File.WriteAllText(shimPath, ...) into the global shim
        // directory (AppPaths.GetShimDirectory(Global)), which this test locks down
        // the same POSIX way RegistryServiceTests locks down the global registry
        // directory.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return; // Permission simulation below is POSIX-specific and root bypasses it.
        }

        var installPath = Path.Combine(_fixture.TempRoot, "global-install");
        Directory.CreateDirectory(installPath);
        var exeName = "Godot_v4.5.1-stable_linux.x86_64";
        File.WriteAllText(Path.Combine(installPath, exeName), "fake");

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(scope: InstallScope.Global, version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry); // Global dir is still writable here.

        var globalShimDir = _fixture.Paths.GetShimDirectory(InstallScope.Global);
        Directory.CreateDirectory(globalShimDir);
        File.SetUnixFileMode(globalShimDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var app = CliTestHarness.Create(_fixture);
            var originalConsole = AnsiConsole.Console;
            AnsiConsole.Console = app.Console;
            try
            {
                var result = await app.RunAsync(["activate", entry.Id.ToString()]);

                Assert.NotEqual(0, result.ExitCode);

                // What this originally pinned is the hint line: the old code never named a
                // remedy at all. Since the Linux pre-check (LinuxElevation.Check) the
                // unwritable shim directory is caught before ApplyActiveAsync writes
                // anything, so the message names the directory instead of the
                // UnauthorizedAccessException it used to hit -- the hint must survive that
                // move, now naming the real command rather than a placeholder.
                var output = result.Output.Replace("\r", "").Replace("\n", "");
                Assert.Contains("Activation failed:", output);
                Assert.Contains($"{globalShimDir} is not writable", output);
                Assert.Contains("hint:", output);
                Assert.Contains("sudo", output, StringComparison.OrdinalIgnoreCase);
                Assert.Contains($"activate {entry.Id}", output);
                Assert.False(File.Exists(Path.Combine(globalShimDir, "godot")));
            }
            finally
            {
                AnsiConsole.Console = originalConsole;
            }
        }
        finally
        {
            // Restore write access so GodmanTestFixture.Dispose can clean up TempRoot.
            File.SetUnixFileMode(globalShimDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
