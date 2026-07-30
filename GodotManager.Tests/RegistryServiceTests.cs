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
