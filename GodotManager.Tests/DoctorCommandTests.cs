using GodotManager.Domain;
using GodotManager.Infrastructure;
using GodotManager.Tests.Helpers;
using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class DoctorCommandTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public DoctorCommandTests()
    {
        _fixture = new GodmanTestFixture();
    }

    private static async Task<string> RunDoctorAsync(GodmanTestFixture fixture)
    {
        var app = CliTestHarness.Create(fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);
            Assert.Equal(0, result.ExitCode);
            return result.Output;
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_ReportsAnInstallWhoseLauncherEntryWentMissing()
    {
        using var fixture = new GodmanTestFixture();
        var path = Path.Combine(fixture.TempRoot, "i");
        Directory.CreateDirectory(path);
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: path);
        entry.LauncherEntry = true;   // godman wrote one; it has since been deleted
        await fixture.Registry.SaveAsync(new InstallRegistry { Installs = [entry] });

        var output = await RunDoctorAsync(fixture);

        Assert.Contains("Launcher entry missing", output);
        Assert.Contains(entry.Id.ToString(), output);
        Assert.DoesNotContain("predate launcher entries", output);
    }

    [Fact]
    public async Task Doctor_SummarisesPreLauncherInstallsInOneLine()
    {
        // Every install on a machine upgraded from 1.3.x has LauncherEntry == null. One
        // two-line block per install buried the rest of the report.
        using var fixture = new GodmanTestFixture();
        var installs = new[] { "a", "b" }.Select(name =>
        {
            var path = Path.Combine(fixture.TempRoot, name);
            Directory.CreateDirectory(path);
            return InstallEntryFactory.Create(version: "4.5.1", path: path);   // LauncherEntry null: pre-1.4.0
        }).ToList();
        await fixture.Registry.SaveAsync(new InstallRegistry { Installs = installs });

        var output = await RunDoctorAsync(fixture);

        Assert.Contains("2 install(s) predate launcher entries", output);
        Assert.Contains("`godman activate <id>` adds one (and makes it active).", output);
        Assert.DoesNotContain("for a global install", output);   // both are user installs
        Assert.DoesNotContain("Launcher entry missing", output);
        Assert.All(installs, x => Assert.DoesNotContain(x.Id.ToString(), output));
    }

    private static InstallEntry PreLauncherInstall(GodmanTestFixture fixture, string name, InstallScope scope)
    {
        var path = Path.Combine(fixture.TempRoot, name);
        Directory.CreateDirectory(path);
        return InstallEntryFactory.Create(version: "4.5.1", scope: scope, path: path);   // LauncherEntry null: pre-1.4.0
    }

    [Fact]
    public async Task Doctor_PreLauncherSummary_ForGlobalInstallsOnly_NamesTheElevatedCommand()
    {
        // Activating a global install needs root: a bare `godman activate` is the wrong advice.
        using var fixture = new GodmanTestFixture();
        await fixture.Registry.SaveAsync(new InstallRegistry
        {
            Installs = [PreLauncherInstall(fixture, "g", InstallScope.Global)]
        });

        var output = (await RunDoctorAsync(fixture)).Replace("\r", "").Replace("\n", "");

        Assert.Contains("1 install(s) predate launcher entries", output);
        Assert.Contains($"`{ElevatedCommandLine.Render("activate <id>")}` adds one (and makes it active).", output);
    }

    [Fact]
    public async Task Doctor_PreLauncherSummary_ForMixedScopes_NamesBothCommands()
    {
        if (OperatingSystem.IsWindows()) return; // one command there: UAC elevates activate itself
        using var fixture = new GodmanTestFixture();
        await fixture.Registry.SaveAsync(new InstallRegistry
        {
            Installs = [PreLauncherInstall(fixture, "u", InstallScope.User), PreLauncherInstall(fixture, "g", InstallScope.Global)]
        });

        var output = (await RunDoctorAsync(fixture)).Replace("\r", "").Replace("\n", "");

        Assert.Contains("2 install(s) predate launcher entries", output);
        Assert.Contains(
            $"`godman activate <id>` adds one (and makes it active); for a global install use `{ElevatedCommandLine.Render("activate <id>")}`.",
            output);
    }

    [Fact]
    public async Task Doctor_DoesNotReportAnOptedOutInstall()
    {
        using var fixture = new GodmanTestFixture();
        var path = Path.Combine(fixture.TempRoot, "i");
        Directory.CreateDirectory(path);
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: path);
        entry.LauncherEntry = false;
        await fixture.Registry.SaveAsync(new InstallRegistry { Installs = [entry] });

        var output = await RunDoctorAsync(fixture);

        Assert.Contains("Registry", output);
        Assert.DoesNotContain("Launcher entry missing", output);
        Assert.DoesNotContain("predate launcher entries", output);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoInstalls_ReturnsZero()
    {
        // Arrange
        var registry = new InstallRegistry();
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["doctor"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithActiveInstall_ReturnsZero()
    {
        // Arrange
        var installPath = Path.Combine(_fixture.TempRoot, "Godot_v4.5.1-stable");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(
            version: "4.5.1",
            edition: InstallEdition.Standard,
            path: installPath);

        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["doctor"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoActiveInstall_ReturnsZero()
    {
        // Arrange
        var installPath = Path.Combine(_fixture.TempRoot, "Godot_v4.5.1-stable");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(
            version: "4.5.1",
            edition: InstallEdition.Standard,
            path: installPath);

        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["doctor"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Doctor_WithActiveInstallDirectoryMissing_SaysSo()
    {
        // An active entry whose directory has vanished (deleted by hand). This is not
        // the migration check: a blocked migration leaves the directory -- and the shim
        // -- where they were, and a completed one gets its entry rebased onto the new
        // root. The shim-target check covers a moved root; see GlobalRootUpgradeE2ETests.
        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(
            version: "4.5.1", path: Path.Combine(_fixture.TempRoot, "moved-away"));
        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Active install directory missing", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithGlobalActiveInstallDirectoryMissing_NamesAnElevatedActivate()
    {
        var entry = InstallEntryFactory.Create(
            version: "4.5.1", scope: InstallScope.Global, path: Path.Combine(_fixture.TempRoot, "gone-global"));
        entry.LauncherEntry = false;
        var registry = new InstallRegistry { Installs = [entry] };
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

        var output = Flatten(await RunDoctorAsync(_fixture));

        Assert.Contains("Active install directory missing", output);
        Assert.Contains($"Run `{ElevatedCommandLine.Render($"activate {entry.Id}")}` again to rewrite the shim", output);
    }

    [Fact]
    public async Task Doctor_WithActiveInstallDirectoryPresent_DoesNotReportItMissing()
    {
        var registry = new InstallRegistry();
        var installPath = Path.Combine(_fixture.TempRoot, "still-there");
        Directory.CreateDirectory(installPath);
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            // Paired with a positive assertion so this cannot pass on empty output.
            Assert.Contains("Registry", result.Output);
            Assert.DoesNotContain("Active install directory missing", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithUnmigratedInstallRoot_WarnsAgainstDeletingIt()
    {
        // The stock legacy-directory advice is "this can be removed". For a root whose
        // destination does not exist yet that advice is actively destructive: the
        // directory still holds the only copy of those installs, and the machine-wide
        // one needs privileges to move. Getting this wrong deletes a user's installs.
        var pending = FirstPendingRelocation();
        Directory.CreateDirectory(pending.OldRoot);
        // The fixture's AppPaths created both install roots before this old root existed,
        // so "not migrated" shows up here as an empty destination, not a missing one.
        Assert.False(
            Directory.Exists(pending.NewRoot) && Directory.EnumerateFileSystemEntries(pending.NewRoot).Any(),
            "precondition: nothing may have landed in the destination yet");

        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Still in use", result.Output);
            Assert.DoesNotContain("can be removed", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithLeftoverRootAfterMigration_SaysItCanBeRemoved()
    {
        // Once the destination exists the old directory really is a leftover, and the
        // original advice applies again.
        var pending = FirstPendingRelocation();
        Directory.CreateDirectory(pending.OldRoot);
        Directory.CreateDirectory(pending.NewRoot);
        File.WriteAllText(Path.Combine(pending.NewRoot, "4.6.2-standard-linux-global.marker"), "migrated");

        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Legacy directory found", result.Output);
            Assert.Contains("can be removed", result.Output);
            Assert.DoesNotContain("Still in use", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithARegisteredInstallStillInTheLegacyRoot_DoesNotSayItCanBeRemoved()
    {
        // A destination with content is not proof this root moved: on a machine with two
        // old roots only the first planned one migrates, and a blocked or partial move
        // leaves entries behind. A registry entry pointing into the directory makes it live.
        // And because TryMigrateDirectory no-ops onto a destination that exists, "run
        // godman to complete the move" can never work here -- it must not be offered.
        var pending = PendingRelocation(InstallScope.Global);
        var installDir = Path.Combine(pending.OldRoot, "4.5.1-standard-linux-global");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(pending.NewRoot);
        File.WriteAllText(Path.Combine(pending.NewRoot, "4.6.2-standard-linux-global.marker"), "migrated");
        var entry = InstallEntryFactory.Create(version: "4.5.1", scope: InstallScope.Global, path: installDir);
        entry.LauncherEntry = false;
        await _fixture.Registry.SaveAsync(new InstallRegistry { Installs = [entry] });

        var output = Flatten(await RunDoctorAsync(_fixture));

        Assert.Contains("Legacy directory found", output);
        Assert.Contains("Still in use", output);
        Assert.Contains("already exists", output);
        Assert.Contains("Reinstall or remove those installs", output);
        Assert.DoesNotContain("can be removed", output);
        Assert.DoesNotContain("to complete the move", output);
        Assert.DoesNotContain(ElevatedCommandLine.Render("list"), output);
    }

    // The four tests below follow TryMigrateDirectory's real rule: it no-ops whenever the
    // destination EXISTS, even empty. The fixture's AppPaths already created the install
    // roots (as every run does), so the "absent" cases delete the destination first. The
    // destination is derived from AppPaths exactly as doctor derives it.

    [Fact]
    public async Task Doctor_WithAnUnmovedGlobalRootAndAnAbsentDestination_OffersTheElevatedMove()
    {
        var (legacy, destination) = UnmovedRoot(InstallScope.Global);
        Directory.Delete(destination, recursive: true);

        var output = Flatten(await RunDoctorAsync(_fixture));

        Assert.Contains("Still in use", output);
        Assert.Contains(
            OperatingSystem.IsWindows()
                ? "Run an elevated godman command to complete the move"
                : $"Run `{ElevatedCommandLine.Render("list")}` to complete the move",
            output);
        Assert.DoesNotContain("rmdir", output);
        Assert.DoesNotContain("can be removed", output);
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public async Task Doctor_WithAnUnmovedGlobalRootAndAnEmptyDestination_SaysTheEmptyDirectoryBlocksTheMove()
    {
        // e.g. after a failed privileged move: `sudo godman list` alone would no-op forever.
        var (_, destination) = UnmovedRoot(InstallScope.Global);
        Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);

        var output = Flatten(await RunDoctorAsync(_fixture));

        Assert.Contains("Still in use", output);
        Assert.Contains($"blocked only by the empty {destination}", output);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains($"`sudo rmdir {destination}`, then: run `{ElevatedCommandLine.Render("list")}`", output);
        }
        Assert.Contains("Do not delete this directory", output);
        Assert.DoesNotContain("can be removed", output);
    }

    [Fact]
    public async Task Doctor_WithAnUnmovedUserRootAndAnAbsentDestination_DoesNotSendTheUserToSudo()
    {
        // User roots move on every ordinary run; under a HOME-resetting sudo an elevated
        // run would migrate root's home instead. (Linux: on Windows the user migration's
        // destination is the config root itself, which always exists.)
        if (OperatingSystem.IsWindows()) return;
        var (_, destination) = UnmovedRoot(InstallScope.User);
        Directory.Delete(destination, recursive: true);

        var output = Flatten(await RunDoctorAsync(_fixture));

        Assert.Contains("Still in use", output);
        Assert.Contains("next ordinary (non-sudo) run", output);
        Assert.DoesNotContain("sudo ", output);
        Assert.DoesNotContain("can be removed", output);
    }

    [Fact]
    public async Task Doctor_WithAnUnmovedUserRootAndAnEmptyDestination_SaysToRemoveItWithoutSudo()
    {
        // The normal state: doctor's own run already attempted the user migration and then
        // created the (empty) user install root, so "the next ordinary run" alone would
        // never move anything.
        if (OperatingSystem.IsWindows()) return;
        var (_, destination) = UnmovedRoot(InstallScope.User);
        Assert.True(Directory.Exists(destination) && !HasAnything(destination), "precondition: exists but empty");

        var output = Flatten(await RunDoctorAsync(_fixture));

        Assert.Contains($"blocked only by the empty {destination}", output);
        Assert.Contains($"`rmdir {destination}`, then: godman moves them on its next ordinary (non-sudo) run", output);
        Assert.DoesNotContain("sudo rmdir", output);
        Assert.DoesNotContain("can be removed", output);
    }

    /// <summary>
    /// An old root of <paramref name="scope"/> holding an install, plus the directory whose
    /// existence decides whether its migration can run (derived, as doctor does).
    /// </summary>
    private (string Legacy, string Destination) UnmovedRoot(InstallScope scope)
    {
        var pending = PendingRelocation(scope);
        Directory.CreateDirectory(Path.Combine(pending.OldRoot, "4.5.1-standard"));
        var relocation = _fixture.Paths.GetInstallRootRelocations().First(r =>
            r.NewRoot == pending.NewRoot
            && (r.OldRoot == pending.OldRoot || r.OldRoot.StartsWith(pending.OldRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
        var destination = _fixture.Paths.GetMigrationDestination(relocation.OldRoot)!;
        Assert.StartsWith(_fixture.TempRoot, destination);
        return (pending.OldRoot, destination);
    }

    private static string Flatten(string output) => output.Replace("\r", "").Replace("\n", "");

    private static bool HasAnything(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();

    /// <summary>A legacy root doctor reports whose relocation lands in <paramref name="scope"/>'s install root.</summary>
    private (string OldRoot, string NewRoot) PendingRelocation(InstallScope scope)
    {
        var target = _fixture.Paths.GetInstallRoot(scope);
        foreach (var (legacyPath, _) in _fixture.Paths.GetLegacyPaths())
        {
            foreach (var relocation in _fixture.Paths.GetInstallRootRelocations())
            {
                if (relocation.NewRoot == target
                    && (relocation.OldRoot == legacyPath || relocation.OldRoot.StartsWith(legacyPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                {
                    return (legacyPath, relocation.NewRoot);
                }
            }
        }

        throw new InvalidOperationException($"no {scope} legacy path is covered by the relocation map");
    }

    /// <summary>
    /// An old root that doctor reports as legacy AND that the relocation map knows how
    /// to move -- the pairing the two messages are chosen from.
    /// </summary>
    private (string OldRoot, string NewRoot) FirstPendingRelocation()
    {
        foreach (var (legacyPath, _) in _fixture.Paths.GetLegacyPaths())
        {
            foreach (var relocation in _fixture.Paths.GetInstallRootRelocations())
            {
                if (relocation.OldRoot == legacyPath || relocation.OldRoot.StartsWith(legacyPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return (legacyPath, relocation.NewRoot);
                }
            }
        }

        throw new InvalidOperationException("no legacy path is covered by the relocation map");
    }

    [Fact]
    public async Task Doctor_WithOrphanedPartial_ReportsItAsNotResumable()
    {
        // No .json sidecar: per DownloadService's cache-lifecycle invariant, a .part
        // without a sidecar has no ETag to guard a Range request with, so DownloadAsync
        // does not resume it -- it truncates and restarts from zero. Doctor must not
        // claim this one "will resume".
        await File.WriteAllBytesAsync(
            Path.Combine(_fixture.Paths.DownloadCacheDirectory, "deadbeefdeadbeef.part"),
            new byte[2048]);

        var app = CliTestHarness.Create(_fixture);

        // DoctorCommand writes through the static AnsiConsole (per project convention),
        // not the CommandAppTester's own console, so it must be redirected here to
        // capture the rendered output. See ListCommandTests for the same pattern.
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            // Three positive assertions on the redirected channel, ending with the
            // specific "0 resumable" count: together they prove doctor actually
            // rendered a report naming this orphaned .part as unresumable, rather
            // than the assertions merely passing because the redirect captured
            // nothing at all.
            Assert.Contains("Download cache", result.Output);
            Assert.Contains("incomplete download", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("0 resumable", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithResumablePartial_ReportsItAsResumable()
    {
        // A .part with its .json sidecar present (URL + ETag) is the case where
        // DownloadService actually resumes on the next install.
        var partPath = Path.Combine(_fixture.Paths.DownloadCacheDirectory, "cafef00dcafef00d.part");
        var metaPath = Path.Combine(_fixture.Paths.DownloadCacheDirectory, "cafef00dcafef00d.json");
        await File.WriteAllBytesAsync(partPath, new byte[2048]);
        await File.WriteAllTextAsync(
            metaPath,
            """{"Url":"https://example.test/godot.zip","ArchiveName":"godot.zip","ETag":"\"abc123\""}""");

        var app = CliTestHarness.Create(_fixture);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Download cache", result.Output);
            Assert.Contains("incomplete download", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1 resumable", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithOnlyACompletedArchive_StillOffersTheCleanupHint()
    {
        // A completed .archive with no .part next to it is the normal outcome of an
        // install that failed after the download finished but before extraction
        // succeeded: ResolvePlanAsync promotes the .part to .archive before
        // InstallAsync's own target-exists check ever runs. Gating the cleanup hint
        // on "partials > 0" alone missed this case entirely -- a cache holding only
        // a stale multi-hundred-MB archive was reported in yellow with no remedy.
        await File.WriteAllBytesAsync(
            Path.Combine(_fixture.Paths.DownloadCacheDirectory, "deadbeefdeadbeef.archive"),
            new byte[4096]);

        var app = CliTestHarness.Create(_fixture);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Download cache", result.Output);
            Assert.Contains("completed archive", result.Output, StringComparison.OrdinalIgnoreCase);
            // The point of this test: no partials exist, yet the remedy still shows.
            Assert.DoesNotContain("incomplete download", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("godman clean", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithEmptyCache_ReportsEmpty()
    {
        // GodmanTestFixture's AppPaths creates DownloadCacheDirectory on construction,
        // so it exists and is empty here without any extra setup.
        var app = CliTestHarness.Create(_fixture);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Download cache", result.Output);
            Assert.Contains("empty", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("incomplete download", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithMissingCacheDirectory_DoesNotThrowAndSkipsCacheReport()
    {
        Directory.Delete(_fixture.Paths.DownloadCacheDirectory, recursive: true);

        var app = CliTestHarness.Create(_fixture);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            // Proves the channel is live: other doctor output still renders.
            Assert.Contains("No installs registered yet", result.Output);
            Assert.DoesNotContain("Download cache", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithMissingCacheDirectoryAndVerbose_DoesNotWarn()
    {
        // A missing DownloadCacheDirectory is the single most common state (a fresh
        // install that has never downloaded anything). Directory.Exists is what keeps
        // that off the screen; without it, Directory.GetFiles would throw
        // DirectoryNotFoundException, and the surrounding try/catch would turn that
        // into a spurious "warn: ... DirectoryNotFoundException" under --verbose even
        // though nothing is actually wrong. This test is what distinguishes "guard
        // present" from "guard absent but masked by the catch" -- see task-9-report.md
        // for the mutation that only this test (not
        // Doctor_WithMissingCacheDirectory_DoesNotThrowAndSkipsCacheReport) catches.
        Directory.Delete(_fixture.Paths.DownloadCacheDirectory, recursive: true);

        var diagnostics = new DiagnosticContext { Verbose = true };
        var app = CliTestHarness.Create(_fixture, diagnostics: diagnostics);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            // Proves the channel is live: other doctor output still renders.
            Assert.Contains("No installs registered yet", result.Output);
            Assert.DoesNotContain("warn:", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithUnreadableCache_WarnsUnderVerboseInsteadOfThrowing()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix permission bits don't apply on Windows.
        }

        File.WriteAllBytes(
            Path.Combine(_fixture.Paths.DownloadCacheDirectory, "abc123.archive"),
            new byte[16]);
        File.SetUnixFileMode(_fixture.Paths.DownloadCacheDirectory, UnixFileMode.None);

        var diagnostics = new DiagnosticContext { Verbose = true };
        var app = CliTestHarness.Create(_fixture, diagnostics: diagnostics);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("warn:", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("download cache", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            // Restore permissions so GodmanTestFixture.Dispose can delete TempRoot.
            File.SetUnixFileMode(
                _fixture.Paths.DownloadCacheDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Doctor_WarnsWhenGodmanGlobalRootStillNamesTheShimDirectory()
    {
        // A 1.3.0 user who kept GODMAN_GLOBAL_ROOT=<X>/bin: nothing in 1.4.0 looks in
        // <X>/bin/godman, so without a warning their global installs silently vanish.
        if (OperatingSystem.IsWindows()) return; // the prefix change is Linux-only

        using var fixture = new GodmanTestFixture(globalRoot: Path.Combine("opt", "bin"), seed: value =>
        {
            Directory.CreateDirectory(Path.Combine(value, "godman", "4.5.1-standard-linux-global"));
            File.WriteAllText(Path.Combine(value, "godman", "installs.json"), "{\"Installs\":[]}");
        });
        var value = Path.Combine(fixture.TempRoot, "opt", "bin");
        var app = CliTestHarness.Create(fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            var output = result.Output.Replace("\r", "").Replace("\n", "");
            Assert.Contains($"GODMAN_GLOBAL_ROOT={value} uses the pre-1.4.0 meaning", output);
            Assert.Contains($"set it to {Path.Combine(fixture.TempRoot, "opt")}", output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_DoesNotWarnAboutAGodmanGlobalRootPrefix()
    {
        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("pre-1.4.0 meaning", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
