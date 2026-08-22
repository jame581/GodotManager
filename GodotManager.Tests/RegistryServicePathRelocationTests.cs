using GodotManager.Domain;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// Covers the path repair that follows an install-root migration. Moving a root on
/// disk is only half the job: every entry already in the registry still carries the
/// absolute path it was installed to, so without a rebase on load, activation,
/// removal and the "is this install still there" check all miss a perfectly intact
/// install. The global root moving out of the shim directory made this reachable for
/// real rather than latent.
/// </summary>
public class RegistryServicePathRelocationTests : IDisposable
{
    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly GodmanTestFixture _fixture;

    public RegistryServicePathRelocationTests()
    {
        _fixture = new GodmanTestFixture();
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public async Task LoadAsync_RebasesEntryPath_OntoTheRelocatedInstallRoot()
    {
        var (oldRoot, newRoot) = FirstRelocation();
        var oldPath = Path.Combine(oldRoot, "4.6.2-standard-linux-global");
        var newPath = Path.Combine(newRoot, "4.6.2-standard-linux-global");
        Directory.CreateDirectory(newPath);

        await WriteGlobalFileAsync(InstallEntryFactory.Create(
            version: "4.6.2", scope: InstallScope.Global, path: oldPath));

        var loaded = await _fixture.Registry.LoadAsync();

        Assert.Equal(newPath, Assert.Single(loaded.Installs).Path);
    }

    [Fact]
    public async Task LoadAsync_LeavesEntryPathAlone_WhenTheOldRootIsStillThere()
    {
        // The migration is best-effort and needs privileges the caller may not have.
        // When it could not run, the recorded path is still correct and must survive
        // a load untouched -- rebasing here would break a working install.
        var (oldRoot, newRoot) = FirstRelocation();
        var oldPath = Path.Combine(oldRoot, "4.6.2-standard-linux-global");
        Directory.CreateDirectory(oldPath);
        Directory.CreateDirectory(Path.Combine(newRoot, "4.6.2-standard-linux-global"));

        await WriteGlobalFileAsync(InstallEntryFactory.Create(
            version: "4.6.2", scope: InstallScope.Global, path: oldPath));

        var loaded = await _fixture.Registry.LoadAsync();

        Assert.Equal(oldPath, Assert.Single(loaded.Installs).Path);
    }

    [Fact]
    public async Task LoadAsync_LeavesEntryPathAlone_WhenNothingLandedAtTheNewRoot()
    {
        // A half-finished migration leaves the files at neither path. Pointing the
        // entry at a directory that does not exist would trade a diagnosable "install
        // missing" for a misleading one that names the wrong location.
        var (oldRoot, _) = FirstRelocation();
        var oldPath = Path.Combine(oldRoot, "4.6.2-standard-linux-global");

        await WriteGlobalFileAsync(InstallEntryFactory.Create(
            version: "4.6.2", scope: InstallScope.Global, path: oldPath));

        var loaded = await _fixture.Registry.LoadAsync();

        Assert.Equal(oldPath, Assert.Single(loaded.Installs).Path);
    }

    [Fact]
    public async Task LoadAsync_DoesNotRebaseASiblingRootThatMerelySharesATextualPrefix()
    {
        // A plain string replace would rewrite <root>-old/... onto <newRoot>-old/...,
        // which is a different directory belonging to nobody. Matching has to respect
        // path segments.
        var (oldRoot, newRoot) = FirstRelocation();
        var siblingPath = Path.Combine(oldRoot + "-old", "4.6.2-standard-linux-global");
        Directory.CreateDirectory(Path.Combine(newRoot + "-old", "4.6.2-standard-linux-global"));

        await WriteGlobalFileAsync(InstallEntryFactory.Create(
            version: "4.6.2", scope: InstallScope.Global, path: siblingPath));

        var loaded = await _fixture.Registry.LoadAsync();

        Assert.Equal(siblingPath, Assert.Single(loaded.Installs).Path);
    }

    [Fact]
    public async Task LoadAsync_DoesNotWriteTheRebaseBackToDisk()
    {
        // Pins the in-memory-only decision. `list` and `doctor` are reads; making
        // either of them write would mean an unprivileged caller failing on the
        // global file for a command that was never supposed to touch it.
        var (oldRoot, newRoot) = FirstRelocation();
        var oldPath = Path.Combine(oldRoot, "4.6.2-standard-linux-global");
        Directory.CreateDirectory(Path.Combine(newRoot, "4.6.2-standard-linux-global"));

        await WriteGlobalFileAsync(InstallEntryFactory.Create(
            version: "4.6.2", scope: InstallScope.Global, path: oldPath));

        await _fixture.Registry.LoadAsync();

        var onDisk = JsonSerializer.Deserialize<InstallRegistry>(
            await File.ReadAllTextAsync(_fixture.Paths.GlobalRegistryFile), RawJsonOptions);

        Assert.Equal(oldPath, Assert.Single(onDisk!.Installs).Path);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheGlobalRegistryFromItsPreMigrationPath_WhenTheCurrentOneIsAbsent()
    {
        // The machine-wide registry lives inside the global install root, so it moved
        // with it -- and moving it needs root. Until an elevated command runs, every
        // ordinary `list`, `doctor` and TUI session would otherwise show no global
        // installs at all, which is indistinguishable from having lost them.
        var legacyFile = _fixture.Paths.GetLegacyGlobalRegistryFiles()[0];
        var installPath = Path.Combine(Path.GetDirectoryName(legacyFile)!, "4.6.2-standard-linux-global");
        Directory.CreateDirectory(installPath);

        await WriteRegistryAsync(legacyFile, InstallEntryFactory.Create(
            version: "4.6.2", scope: InstallScope.Global, path: installPath));

        Assert.False(File.Exists(_fixture.Paths.GlobalRegistryFile), "precondition: the current global registry must be absent");

        var loaded = await _fixture.Registry.LoadAsync();

        var entry = Assert.Single(loaded.Installs);
        Assert.Equal("4.6.2", entry.Version);
        Assert.Equal(InstallScope.Global, entry.Scope);
    }

    [Fact]
    public async Task LoadAsync_PrefersTheCurrentGlobalRegistry_WhenALegacyOneIsStillLyingAround()
    {
        // After a completed migration the old file is a leftover, not a second source.
        var legacyFile = _fixture.Paths.GetLegacyGlobalRegistryFiles()[0];
        await WriteRegistryAsync(legacyFile, InstallEntryFactory.Create(
            version: "4.0.0-stale", scope: InstallScope.Global, path: Path.Combine(_fixture.TempRoot, "stale")));
        await WriteRegistryAsync(_fixture.Paths.GlobalRegistryFile, InstallEntryFactory.Create(
            version: "4.6.2", scope: InstallScope.Global, path: Path.Combine(_fixture.TempRoot, "current")));

        var loaded = await _fixture.Registry.LoadAsync();

        Assert.Equal("4.6.2", Assert.Single(loaded.Installs).Version);
    }

    [Fact]
    public async Task SaveAsync_AfterReadingALegacyGlobalRegistry_DoesNotWriteTheGlobalFileForAnUnrelatedUserInstall()
    {
        // Reading the legacy file has to leave SaveAsync's "did the global set change"
        // comparison intact. If the fallback were skipped there, an ordinary
        // unprivileged user-scope install would look like it was adding every global
        // entry and demand elevation for an operation that never touched global scope.
        var legacyFile = _fixture.Paths.GetLegacyGlobalRegistryFiles()[0];
        var globalEntry = InstallEntryFactory.Create(
            version: "4.6.2", scope: InstallScope.Global, path: Path.Combine(_fixture.TempRoot, "global"));
        await WriteRegistryAsync(legacyFile, globalEntry);

        var loaded = await _fixture.Registry.LoadAsync();
        loaded.Installs.Add(InstallEntryFactory.Create(
            version: "4.7.2", scope: InstallScope.User, path: Path.Combine(_fixture.TempRoot, "user")));

        await _fixture.Registry.SaveAsync(loaded);

        Assert.False(
            File.Exists(_fixture.Paths.GlobalRegistryFile),
            "an unrelated user-scope save must not rewrite the machine-wide registry");
    }

    private static async Task WriteRegistryAsync(string file, InstallEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(
            file, JsonSerializer.Serialize(new InstallRegistry { Installs = { entry } }, RawJsonOptions));
    }

    private (string OldRoot, string NewRoot) FirstRelocation()
    {
        var globalRoot = _fixture.Paths.GetInstallRoot(InstallScope.Global);
        foreach (var relocation in _fixture.Paths.GetInstallRootRelocations())
        {
            if (relocation.NewRoot == globalRoot)
            {
                return relocation;
            }
        }

        throw new InvalidOperationException("no relocation targets the global install root");
    }

    private async Task WriteGlobalFileAsync(InstallEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Paths.GlobalRegistryFile)!);
        await File.WriteAllTextAsync(
            _fixture.Paths.GlobalRegistryFile,
            JsonSerializer.Serialize(new InstallRegistry { Installs = { entry } }, RawJsonOptions));
    }
}
