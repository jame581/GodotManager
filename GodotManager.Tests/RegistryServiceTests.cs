using GodotManager.Domain;
using GodotManager.Infrastructure;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using Spectre.Console;
using Spectre.Console.Testing;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// Covers the scope-aware split registry introduced to fix global-scope installs
/// being invisible outside the elevated process that created them (Task 12).
/// Every test constructs its own <see cref="RegistryService"/> against the
/// fixture's isolated <see cref="GodotManager.Config.AppPaths"/> rather than
/// touching real machine paths -- see <see cref="GodmanTestFixture"/>, which
/// already overrides GODMAN_GLOBAL_ROOT to a temp directory.
/// </summary>
public class RegistryServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly GodmanTestFixture _fixture;

    public RegistryServiceTests()
    {
        _fixture = new GodmanTestFixture();
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public async Task LoadAsync_MergesGlobalAndUserEntries_ActiveIdFromUserResolvesGlobalEntry()
    {
        var globalEntry = InstallEntryFactory.Create(
            version: "4.5.0",
            scope: InstallScope.Global,
            path: Path.Combine(_fixture.TempRoot, "global-install"));
        var userEntry = InstallEntryFactory.Create(
            version: "4.4.0",
            scope: InstallScope.User,
            path: Path.Combine(_fixture.TempRoot, "user-install"));

        var toSave = new InstallRegistry { Installs = { globalEntry, userEntry } };
        toSave.MarkActive(globalEntry.Id);
        await _fixture.Registry.SaveAsync(toSave);

        // Sanity: confirm the write actually split across the two files, not just
        // that the merged read happens to look right by accident.
        var globalOnDisk = await ReadRawAsync(_fixture.Paths.GlobalRegistryFile);
        var userOnDisk = await ReadRawAsync(_fixture.Paths.RegistryFile);
        Assert.Single(globalOnDisk.Installs);
        Assert.Equal(globalEntry.Id, globalOnDisk.Installs[0].Id);
        Assert.Single(userOnDisk.Installs);
        Assert.Equal(userEntry.Id, userOnDisk.Installs[0].Id);

        var loaded = await _fixture.Registry.LoadAsync();

        Assert.Equal(2, loaded.Installs.Count);
        Assert.Equal(globalEntry.Id, loaded.ActiveId);

        var active = loaded.GetActive();
        Assert.NotNull(active);
        Assert.Equal(globalEntry.Id, active!.Id);
        Assert.Equal(InstallScope.Global, active.Scope);
        Assert.True(active.IsActive);
    }

    [Fact]
    public async Task LoadAsync_WithUnreadableGlobalRegistry_DoesNotThrowAndStillListsUserInstalls()
    {
        var diagnostics = new DiagnosticContext { Verbose = true };
        var service = new RegistryService(_fixture.Paths, diagnostics);

        var userEntry = InstallEntryFactory.Create(
            version: "4.4.0",
            scope: InstallScope.User,
            path: Path.Combine(_fixture.TempRoot, "user-only"));
        await service.SaveAsync(new InstallRegistry { Installs = { userEntry } });

        // Malform the global registry directly -- simulates "unreadable" without
        // needing real filesystem permission games for this case.
        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Paths.GlobalRegistryFile)!);
        await File.WriteAllTextAsync(_fixture.Paths.GlobalRegistryFile, "{ not valid json ");

        var console = new TestConsole();
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = console;
        InstallRegistry loaded;
        try
        {
            loaded = await service.LoadAsync(); // Must not throw.
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }

        Assert.Single(loaded.Installs);
        Assert.Equal(userEntry.Id, loaded.Installs[0].Id);
        Assert.Contains("warn:", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("machine-wide registry", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_RoutesEntriesToTheirScopeFile()
    {
        var globalEntry = InstallEntryFactory.Create(
            version: "4.5.0",
            scope: InstallScope.Global,
            path: Path.Combine(_fixture.TempRoot, "global"));
        var userEntry = InstallEntryFactory.Create(
            version: "4.4.0",
            scope: InstallScope.User,
            path: Path.Combine(_fixture.TempRoot, "user"));

        await _fixture.Registry.SaveAsync(new InstallRegistry { Installs = { globalEntry, userEntry } });

        var globalOnDisk = await ReadRawAsync(_fixture.Paths.GlobalRegistryFile);
        var userOnDisk = await ReadRawAsync(_fixture.Paths.RegistryFile);

        Assert.Single(globalOnDisk.Installs);
        Assert.Equal(globalEntry.Id, globalOnDisk.Installs[0].Id);
        Assert.DoesNotContain(userOnDisk.Installs, x => x.Id == globalEntry.Id);

        Assert.Single(userOnDisk.Installs);
        Assert.Equal(userEntry.Id, userOnDisk.Installs[0].Id);
        Assert.DoesNotContain(globalOnDisk.Installs, x => x.Id == userEntry.Id);
    }

    [Fact]
    public async Task SaveAsync_ForUserOnlyChange_NeverTouchesGlobalFile()
    {
        // Regression guard for the trap this design has to avoid: a merged
        // LoadAsync() pulls existing global entries into registry.Installs, so a
        // naive "always write both files" SaveAsync would demand elevation for
        // an ordinary, unprivileged user-scope install on a machine that already
        // has global installs. It must not even attempt the global write here.
        var globalEntry = InstallEntryFactory.Create(
            version: "4.5.0",
            scope: InstallScope.Global,
            path: Path.Combine(_fixture.TempRoot, "existing-global"));
        await _fixture.Registry.SaveAsync(new InstallRegistry { Installs = { globalEntry } });
        Assert.True(File.Exists(_fixture.Paths.GlobalRegistryFile));
        var globalWriteTimeBefore = File.GetLastWriteTimeUtc(_fixture.Paths.GlobalRegistryFile);

        await Task.Delay(20); // Ensure a distinguishable timestamp if it gets rewritten.

        var loaded = await _fixture.Registry.LoadAsync();
        Assert.Contains(loaded.Installs, x => x.Id == globalEntry.Id);

        var userEntry = InstallEntryFactory.Create(
            version: "4.4.0",
            scope: InstallScope.User,
            path: Path.Combine(_fixture.TempRoot, "new-user"));
        loaded.Installs.Add(userEntry);
        await _fixture.Registry.SaveAsync(loaded);

        var globalWriteTimeAfter = File.GetLastWriteTimeUtc(_fixture.Paths.GlobalRegistryFile);
        Assert.Equal(globalWriteTimeBefore, globalWriteTimeAfter);
    }

    [Fact]
    public async Task SaveAsync_WithStrayGlobalEntryInOwnUserFile_LeavesItInPlaceAndNeverTouchesGlobalFile()
    {
        // Reproduces the code-review finding: a Global-scope entry can end up
        // recorded in a *real, unprivileged* user's own registry file -- e.g. a
        // pre-1.3.0 install, or a `sudo -E` elevation that preserved $HOME instead
        // of switching to root's profile (MigrateGlobalOrphansAsync only ever
        // reaches a stray sitting in the *elevated* process's own file; this one
        // never was elevated, so migration never runs for it). LoadAsync merges it
        // into registry.Installs regardless of where it physically lives, so an
        // ordinary unprivileged save for something entirely unrelated -- activating
        // a different, User-scope install -- must not be forced into a
        // permission-gated global write for state it never touched.
        var stray = InstallEntryFactory.Create(
            version: "4.6.0",
            scope: InstallScope.Global,
            path: Path.Combine(_fixture.TempRoot, "stray-in-user-file"));
        await WriteOwnFileAsync(stray);

        var unprivileged = new RegistryService(_fixture.Paths, isPrivilegedProcess: () => false);
        var loaded = await unprivileged.LoadAsync();
        Assert.Contains(loaded.Installs, x => x.Id == stray.Id);

        // Entirely unrelated to global scope: add a new User-scope install and
        // activate it.
        var userEntry = InstallEntryFactory.Create(
            version: "4.4.0",
            scope: InstallScope.User,
            path: Path.Combine(_fixture.TempRoot, "unrelated-user-install"));
        loaded.Installs.Add(userEntry);
        loaded.MarkActive(userEntry.Id);

        // Must not throw -- this is the exact bug: an unprivileged save being
        // forced into a permission-gated global write for state it never touched.
        await unprivileged.SaveAsync(loaded);

        Assert.False(
            File.Exists(_fixture.Paths.GlobalRegistryFile),
            "an unrelated unprivileged save must never create/touch the global registry file");

        var userOnDisk = await ReadRawAsync(_fixture.Paths.RegistryFile);
        Assert.Contains(userOnDisk.Installs, x => x.Id == stray.Id && x.Scope == InstallScope.Global);
        Assert.Contains(userOnDisk.Installs, x => x.Id == userEntry.Id);
        Assert.Equal(userEntry.Id, userOnDisk.ActiveId);
    }

    [Fact]
    public async Task LoadAsync_WithDivergentDuplicateId_GlobalEntryWins()
    {
        // Pins MergeInstalls's documented "global wins" tie-break against a case
        // where the two on-disk copies genuinely disagree, not just duplicate each
        // other -- the state a partial migration failure leaves behind (global
        // write succeeded, own-file removal did not; see
        // MigrateGlobalOrphansAsync_LiftsOrphanedGlobalEntry_AndDoesNotDuplicateOnRerun
        // for the same scenario from the migration side).
        var id = Guid.NewGuid();
        var globalCopy = InstallEntryFactory.Create(
            version: "4.7.1",
            scope: InstallScope.Global,
            path: "/usr/local/bin/godman/current");
        globalCopy.Id = id;

        var staleUserCopy = InstallEntryFactory.Create(
            version: "4.5.0",
            scope: InstallScope.Global,
            path: "/tmp/stale-leftover");
        staleUserCopy.Id = id;

        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Paths.GlobalRegistryFile)!);
        await File.WriteAllTextAsync(
            _fixture.Paths.GlobalRegistryFile,
            JsonSerializer.Serialize(new InstallRegistry { Installs = { globalCopy } }, RawJsonOptions));
        await WriteOwnFileAsync(staleUserCopy);

        var loaded = await _fixture.Registry.LoadAsync();

        var merged = Assert.Single(loaded.Installs, x => x.Id == id);
        Assert.Equal("4.7.1", merged.Version);
        Assert.Equal("/usr/local/bin/godman/current", merged.Path);
    }

    [Fact]
    public async Task SaveAsync_WithDivergentDuplicateAcrossFiles_UnrelatedSave_NeverTouchesGlobalFileAndKeepsTheEntry()
    {
        // Round-2 review finding: the round-1 stray filter (Scope == Global &&
        // present in the user file) misclassified exactly this state -- a same-Id
        // duplicate left by a partial migration failure -- as a stray, excluding the
        // legitimate global copy from desiredGlobal and silently dropping it from
        // the next global-file write. The fix requires the duplicate to ALSO be
        // absent from the global file before it counts as a stray.
        var id = Guid.NewGuid();
        var canonicalInGlobalFile = InstallEntryFactory.Create(
            version: "4.7.1",
            scope: InstallScope.Global,
            path: "/usr/local/bin/godman/current");
        canonicalInGlobalFile.Id = id;

        var staleDuplicateInUserFile = InstallEntryFactory.Create(
            version: "4.5.0",
            scope: InstallScope.Global,
            path: "/tmp/stale-leftover");
        staleDuplicateInUserFile.Id = id;

        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Paths.GlobalRegistryFile)!);
        await File.WriteAllTextAsync(
            _fixture.Paths.GlobalRegistryFile,
            JsonSerializer.Serialize(new InstallRegistry { Installs = { canonicalInGlobalFile } }, RawJsonOptions));
        await WriteOwnFileAsync(staleDuplicateInUserFile);
        var globalWriteTimeBefore = File.GetLastWriteTimeUtc(_fixture.Paths.GlobalRegistryFile);

        await Task.Delay(20); // Ensure a distinguishable timestamp if it gets rewritten.

        // What a real caller would actually pass to SaveAsync: LoadAsync's merge has
        // already deduped the two rows down to the single global-sourced copy, plus
        // whatever unrelated User-scope change this save is actually making.
        var unrelatedUserEntry = InstallEntryFactory.Create(
            version: "4.4.0",
            scope: InstallScope.User,
            path: Path.Combine(_fixture.TempRoot, "unrelated-user-install"));
        var toSave = new InstallRegistry { Installs = { canonicalInGlobalFile, unrelatedUserEntry } };

        var unprivileged = new RegistryService(_fixture.Paths, isPrivilegedProcess: () => false);
        await unprivileged.SaveAsync(toSave); // Must not throw.

        // Nothing about the global set actually changed, so no write is attempted --
        // and the entry is not lost.
        var globalWriteTimeAfter = File.GetLastWriteTimeUtc(_fixture.Paths.GlobalRegistryFile);
        Assert.Equal(globalWriteTimeBefore, globalWriteTimeAfter);

        var globalOnDisk = await ReadRawAsync(_fixture.Paths.GlobalRegistryFile);
        Assert.Contains(globalOnDisk.Installs, x => x.Id == id && x.Version == "4.7.1");

        // The stale duplicate row is cleaned out of the user file as a side effect
        // (a write to the user's own file, no elevation needed) since the entry is
        // already safely recorded in the global file.
        var userOnDisk = await ReadRawAsync(_fixture.Paths.RegistryFile);
        Assert.DoesNotContain(userOnDisk.Installs, x => x.Id == id);
        Assert.Contains(userOnDisk.Installs, x => x.Id == unrelatedUserEntry.Id);
    }

    [Fact]
    public async Task SaveAsync_WithDivergentDuplicateAcrossFiles_WhenGlobalSetGenuinelyChanges_PreservesTheDuplicateEntry()
    {
        // Same starting state as the test above, but this save genuinely does add a
        // new global entry, forcing the global file to actually be rewritten. The
        // pre-fix bug: the misclassified "stray" was excluded from desiredGlobal, so
        // this write would have contained only the new entry and silently deleted
        // the pre-existing one from the machine-wide registry.
        var id = Guid.NewGuid();
        var canonicalInGlobalFile = InstallEntryFactory.Create(
            version: "4.7.1",
            scope: InstallScope.Global,
            path: "/usr/local/bin/godman/current");
        canonicalInGlobalFile.Id = id;

        var staleDuplicateInUserFile = InstallEntryFactory.Create(
            version: "4.5.0",
            scope: InstallScope.Global,
            path: "/tmp/stale-leftover");
        staleDuplicateInUserFile.Id = id;

        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Paths.GlobalRegistryFile)!);
        await File.WriteAllTextAsync(
            _fixture.Paths.GlobalRegistryFile,
            JsonSerializer.Serialize(new InstallRegistry { Installs = { canonicalInGlobalFile } }, RawJsonOptions));
        await WriteOwnFileAsync(staleDuplicateInUserFile);

        var newGlobalEntry = InstallEntryFactory.Create(
            version: "4.7.2",
            scope: InstallScope.Global,
            path: "/usr/local/bin/godman/new-install");
        var toSave = new InstallRegistry { Installs = { canonicalInGlobalFile, newGlobalEntry } };

        await _fixture.Registry.SaveAsync(toSave);

        var globalOnDisk = await ReadRawAsync(_fixture.Paths.GlobalRegistryFile);
        Assert.Contains(globalOnDisk.Installs, x => x.Id == id && x.Version == "4.7.1");
        Assert.Contains(globalOnDisk.Installs, x => x.Id == newGlobalEntry.Id);
        Assert.Equal(2, globalOnDisk.Installs.Count);
    }

    [Fact]
    public async Task SaveAsync_WhenGlobalWriteFails_ThrowsGodmanExceptionWithHint()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return; // Permission simulation below is POSIX-specific and root bypasses it.
        }

        var globalDir = Path.GetDirectoryName(_fixture.Paths.GlobalRegistryFile)!;
        Directory.CreateDirectory(globalDir);
        File.SetUnixFileMode(globalDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var entry = InstallEntryFactory.Create(
                scope: InstallScope.Global,
                path: Path.Combine(_fixture.TempRoot, "blocked"));

            var ex = await Assert.ThrowsAsync<GodmanException>(
                () => _fixture.Registry.SaveAsync(new InstallRegistry { Installs = { entry } }));

            Assert.Contains("global", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(ex.Hint);
            Assert.Contains("sudo", ex.Hint, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Restore write access so GodmanTestFixture.Dispose can clean up TempRoot.
            File.SetUnixFileMode(globalDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task MigrateGlobalOrphansAsync_LiftsOrphanedGlobalEntry_AndDoesNotDuplicateOnRerun()
    {
        var orphan = InstallEntryFactory.Create(
            version: "4.5.0",
            scope: InstallScope.Global,
            path: Path.Combine(_fixture.TempRoot, "orphan"));

        await WriteOwnFileAsync(orphan);

        var service = new RegistryService(_fixture.Paths);
        await service.MigrateGlobalOrphansAsync();

        var userAfterFirst = await ReadRawAsync(_fixture.Paths.RegistryFile);
        var globalAfterFirst = await ReadRawAsync(_fixture.Paths.GlobalRegistryFile);
        Assert.Empty(userAfterFirst.Installs);
        Assert.Single(globalAfterFirst.Installs);
        Assert.Equal(orphan.Id, globalAfterFirst.Installs[0].Id);

        // Reintroduce the same entry into the own file, as if the own-file
        // removal half of a prior migration had not landed (or a stale binary
        // wrote it again). A second migration run must dedup by Id, not add a
        // second copy to the global file.
        await WriteOwnFileAsync(orphan);
        await service.MigrateGlobalOrphansAsync();

        var globalAfterSecond = await ReadRawAsync(_fixture.Paths.GlobalRegistryFile);
        Assert.Single(globalAfterSecond.Installs);
        Assert.Equal(orphan.Id, globalAfterSecond.Installs[0].Id);

        var userAfterSecond = await ReadRawAsync(_fixture.Paths.RegistryFile);
        Assert.Empty(userAfterSecond.Installs);
    }

    [Fact]
    public async Task LoadAsync_OnlyMigratesOrphans_WhenProcessIsPrivileged()
    {
        var orphan = InstallEntryFactory.Create(
            version: "4.5.0",
            scope: InstallScope.Global,
            path: Path.Combine(_fixture.TempRoot, "orphan"));
        await WriteOwnFileAsync(orphan);

        var unprivileged = new RegistryService(_fixture.Paths, isPrivilegedProcess: () => false);
        await unprivileged.LoadAsync();

        Assert.False(File.Exists(_fixture.Paths.GlobalRegistryFile));
        var userStillOrphaned = await ReadRawAsync(_fixture.Paths.RegistryFile);
        Assert.Single(userStillOrphaned.Installs);

        var privileged = new RegistryService(_fixture.Paths, isPrivilegedProcess: () => true);
        await privileged.LoadAsync();

        Assert.True(File.Exists(_fixture.Paths.GlobalRegistryFile));
        var globalNow = await ReadRawAsync(_fixture.Paths.GlobalRegistryFile);
        Assert.Single(globalNow.Installs);
        var userNow = await ReadRawAsync(_fixture.Paths.RegistryFile);
        Assert.Empty(userNow.Installs);
    }

    private async Task WriteOwnFileAsync(InstallEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Paths.RegistryFile)!);
        await File.WriteAllTextAsync(
            _fixture.Paths.RegistryFile,
            JsonSerializer.Serialize(new InstallRegistry { Installs = { entry } }, RawJsonOptions));
    }

    private static async Task<InstallRegistry> ReadRawAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<InstallRegistry>(json, RawJsonOptions)
               ?? new InstallRegistry();
    }
}
