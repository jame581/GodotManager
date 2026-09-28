using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using Spectre.Console;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

/// <summary>
/// The 1.3.0 → 1.4.0 upgrade of an active global install: <c>&lt;shim&gt;/godman</c>
/// moves to <c>&lt;prefix&gt;/lib/godman</c> and the shim that hard-codes the old root has
/// to follow. The real migration only runs without overrides (on the real /usr/local), so
/// the move and repair are driven through the same internal
/// <see cref="AppPaths.MigrateAndRepair"/> the constructor calls, against the fixture's
/// prefix.
/// </summary>
public class GlobalRootUpgradeE2ETests : IDisposable
{
    private readonly GodmanTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private sealed record Layout(string OldRoot, string NewRoot, string Shim, string Binary, string InstallDir, Guid ActiveId);

    /// <summary>
    /// A 1.3.0 machine: global install and its registry inside <c>&lt;shim&gt;/godman</c>,
    /// the active entry recorded in the user file, and a shim plus env.sh naming the old
    /// root.
    /// </summary>
    private async Task<Layout> Create130LayoutAsync()
    {
        var globalShimDir = _fixture.Paths.GetShimDirectory(InstallScope.Global);
        var oldRoot = Path.Combine(globalShimDir, "godman");
        var newRoot = _fixture.Paths.GetInstallRoot(InstallScope.Global);
        var installDir = Path.Combine(oldRoot, "4.5.1-standard-linux-global");
        var binary = Path.Combine(installDir, "Godot_v4.5.1-stable_linux.x86_64");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(binary, "fake godot");

        var entry = InstallEntryFactory.Create(version: "4.5.1", scope: InstallScope.Global, path: installDir);
        var registry = new InstallRegistry { Installs = [entry] };
        registry.MarkActive(entry.Id);
        // SaveAsync writes the global file at the current path; a 1.3.0 machine keeps it in
        // the old root, so move it there and drop the (otherwise empty) new root that the
        // fixture's AppPaths best-effort created -- the migration no-ops onto an existing one.
        await _fixture.Registry.SaveAsync(registry);
        File.Move(_fixture.Paths.GlobalRegistryFile, Path.Combine(oldRoot, "installs.json"));
        Directory.Delete(newRoot, recursive: true);

        var shim = Path.Combine(globalShimDir, "godot");
        File.WriteAllText(shim, $"#!/usr/bin/env bash\nsource \"{_fixture.Paths.EnvScriptPath}\" 2>/dev/null\nexec \"{binary}\" \"$@\"\n");
        File.WriteAllText(_fixture.Paths.EnvScriptPath, $"export GODOT_HOME=\"{installDir}\"\n");

        return new Layout(oldRoot, newRoot, shim, binary, installDir, entry.Id);
    }

    private string[] RepairTargets() =>
    [
        Path.Combine(_fixture.Paths.GetShimDirectory(InstallScope.User), "godot"),
        Path.Combine(_fixture.Paths.GetShimDirectory(InstallScope.Global), "godot"),
        _fixture.Paths.EnvScriptPath
    ];

    private async Task<string> RunDoctorAsync()
    {
        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);
            Assert.Equal(0, result.ExitCode);
            return result.Output.Replace("\r", "").Replace("\n", "");
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task CompletedGlobalMigration_RepairsTheShimAndEnv_AndDoctorIsSatisfied()
    {
        if (OperatingSystem.IsWindows()) return; // Linux layout: <prefix>/bin/godman -> <prefix>/lib/godman
        var layout = await Create130LayoutAsync();
        var prefix = Path.GetDirectoryName(_fixture.Paths.GetShimDirectory(InstallScope.Global))!;

        var moves = AppPaths.MigrateAndRepair(
            AppPaths.PlanLinuxMigrations(_fixture.TempRoot, prefix, migrateUser: false, migrateGlobal: true),
            RepairTargets());

        Assert.Contains((layout.OldRoot, layout.NewRoot), moves);
        var newBinary = Path.Combine(layout.NewRoot, "4.5.1-standard-linux-global", Path.GetFileName(layout.Binary));
        Assert.True(File.Exists(newBinary), "precondition: the move itself happened");

        var shim = File.ReadAllText(layout.Shim);
        Assert.Contains($"exec \"{newBinary}\"", shim);
        Assert.DoesNotContain(layout.OldRoot + "/", shim);
        // The source line names the env script, which did not move -- left alone.
        Assert.Contains($"source \"{_fixture.Paths.EnvScriptPath}\"", shim);
        Assert.Contains(Path.Combine(layout.NewRoot, "4.5.1-standard-linux-global"), File.ReadAllText(_fixture.Paths.EnvScriptPath));

        var output = await RunDoctorAsync();

        Assert.Contains("Shim present", output);
        Assert.DoesNotContain("Shim points at a missing binary", output);
        Assert.DoesNotContain("Active install directory missing", output);
    }

    [Fact]
    public async Task CompletedGlobalMigration_WithoutTheRepair_DoctorReportsTheStaleShim()
    {
        // What 1.4.0 did before the repair existed: the move succeeds, the registry rebases
        // the active entry onto the new root (which exists), and the shim keeps exec'ing a
        // path that is gone. The active-directory check is satisfied, so only the shim-target
        // check can see it.
        if (OperatingSystem.IsWindows()) return;
        var layout = await Create130LayoutAsync();

        Assert.True(AppPaths.TryMigrateDirectory(layout.OldRoot, layout.NewRoot));

        var output = await RunDoctorAsync();

        Assert.Contains("Shim present", output);
        Assert.Contains($"Shim points at a missing binary: {layout.Binary}", output);
        Assert.DoesNotContain("Active install directory missing", output);
        // A global shim needs root to rewrite: the remedy names a command sudo can find.
        Assert.Contains($"Run `{GodotManager.Infrastructure.ElevatedCommandLine.Render($"activate {layout.ActiveId}")}` to rewrite the shim", output);
    }
}
