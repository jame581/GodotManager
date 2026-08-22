using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using System.Text.Json;

namespace GodotManager.Services;

/// <summary>
/// Reads and writes install registrations across the two on-disk registries:
/// the per-user file (<see cref="AppPaths.RegistryFile"/>) and the machine-wide
/// global file (<see cref="AppPaths.GlobalRegistryFile"/>).
///
/// Reads merge both files into one view; <see cref="InstallRegistry.ActiveId"/>
/// always comes from the user file, since active state is per-user even when the
/// active install itself is a global-scope entry. Writes split entries back to
/// their owning file by <see cref="InstallEntry.Scope"/>.
///
/// The global file is optional infrastructure from an unprivileged caller's point
/// of view: it may not exist, may not be readable, or may be malformed, and none
/// of that should stop a plain user from running `list`, `doctor`, or the TUI.
/// Reads of it are therefore best-effort. Writes to it are not -- a permission
/// failure while writing the global file means an operation the user asked for
/// (an install, an activation) did not actually happen, and must be reported.
/// </summary>
internal sealed class RegistryService
{
    private readonly AppPaths _paths;
    private readonly DiagnosticContext? _diagnostics;
    private readonly Func<bool> _isPrivilegedProcess;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RegistryService(AppPaths paths, DiagnosticContext? diagnostics = null, Func<bool>? isPrivilegedProcess = null)
    {
        _paths = paths;
        _diagnostics = diagnostics;
        _isPrivilegedProcess = isPrivilegedProcess ?? (() => Environment.IsPrivilegedProcess);
    }

    public async Task<InstallRegistry> LoadAsync(CancellationToken cancellationToken = default)
    {
        // Recovers machines where global-scope entries were previously written to
        // the elevated process's own user registry (e.g. /root/.config/godman on
        // Linux under sudo, where $HOME points at root's profile rather than the
        // real user's). Only attempted when running elevated, since an unprivileged
        // process could never have produced -- and could never fix -- that state.
        if (_isPrivilegedProcess())
        {
            await MigrateGlobalOrphansAsync(cancellationToken);
        }

        var userRegistry = await LoadFileAsync(_paths.RegistryFile, cancellationToken);
        var globalRegistry = await LoadGlobalBestEffortAsync(cancellationToken);

        var merged = new InstallRegistry
        {
            Installs = MergeInstalls(globalRegistry.Installs, userRegistry.Installs),
            ActiveId = userRegistry.ActiveId
        };

        RebaseRelocatedInstallPaths(merged);

        if (merged.ActiveId.HasValue)
        {
            merged.MarkActive(merged.ActiveId.Value);
        }

        return merged;
    }

    public async Task SaveAsync(InstallRegistry registry, CancellationToken cancellationToken = default)
    {
        // Read the global file once, up front. It settles two separate questions
        // below: which Global-scope entries are strays (next comment) and, later,
        // whether the global file needs rewriting at all. Reading it only once
        // keeps both questions answered from the same snapshot.
        var currentGlobal = await LoadGlobalBestEffortAsync(cancellationToken);
        var currentGlobalIds = currentGlobal.Installs.Select(x => x.Id).ToHashSet();

        // Scope alone doesn't say which file a Global-scope entry actually lives in
        // on disk right now. It can be a stray sitting in *this file's own* user
        // registry rather than the global one -- a pre-1.3.0 install recorded before
        // scope-aware save existed, or a `sudo -E` elevation that preserved $HOME
        // instead of switching to root's profile (the migration in LoadAsync only
        // ever reaches a stray in the *elevated* process's own file; one sitting in a
        // real, unprivileged user's own file is never touched by it). LoadAsync
        // merges such a stray into `registry.Installs` regardless, so every caller of
        // SaveAsync -- including an ordinary unprivileged save with no interest in
        // global scope at all -- would otherwise see it and try to write it into the
        // real global file.
        //
        // Physical presence in the user file is necessary but not sufficient to call
        // an entry a stray, though: a partial migration failure (global write
        // succeeded, own-file removal did not -- see MigrateGlobalOrphansAsync) also
        // leaves a same-Id row physically sitting in the user file for an entry that
        // is already a legitimate global entry. MergeInstalls already resolves that
        // duplicate to the global copy for the merged view LoadAsync hands callers,
        // so treating "also present in the user file" alone as "stray" would take
        // that already-global entry back out of desiredGlobal below and silently
        // drop it from the next global-file write -- an earlier version of this fix
        // did exactly that. "Already recorded in the global file" always wins over
        // "also has a leftover row in the user file": an entry only counts as a
        // stray when it is in the user file and NOT already in the global file.
        var rawUserIds = (await LoadFileAsync(_paths.RegistryFile, cancellationToken))
            .Installs.Select(x => x.Id).ToHashSet();

        bool IsStrayInUserFile(InstallEntry entry) =>
            entry.Scope == InstallScope.Global
            && rawUserIds.Contains(entry.Id)
            && !currentGlobalIds.Contains(entry.Id);

        var strayGlobalInUserFile = registry.Installs.Where(IsStrayInUserFile).ToList();

        var desiredGlobal = registry.Installs
            .Where(x => x.Scope == InstallScope.Global && !IsStrayInUserFile(x))
            .ToList();

        // An entry already recorded in the global file is written back to the user
        // file only if it's a stray (i.e. not already global). A same-Id row that's
        // present in both files but resolved as "already global" is deliberately
        // dropped from the user-file write here -- self-healing the leftover
        // duplicate a partial migration failure left behind, using only a write to
        // the user's own file, no elevation required.
        var userEntries = registry.Installs
            .Where(x => x.Scope != InstallScope.Global)
            .Concat(strayGlobalInUserFile)
            .ToList();

        // Only touch the global file when the set of *legitimately global* entries
        // actually changed (strays excluded above). Every write -- including a plain
        // unprivileged `godman install --scope User` -- passes through here with the
        // machine's existing global entries still present in `registry.Installs`
        // (LoadAsync merges them in), so writing the global file unconditionally
        // would demand elevation for operations that never intended to touch global
        // scope at all.
        var desiredGlobalIds = desiredGlobal.Select(x => x.Id).ToHashSet();

        if (!currentGlobalIds.SetEquals(desiredGlobalIds))
        {
            try
            {
                await SaveFileAsync(_paths.GlobalRegistryFile, new InstallRegistry { Installs = desiredGlobal }, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new GodmanException(
                    $"Failed to update the machine-wide registry at {_paths.GlobalRegistryFile}: {ex.Message}",
                    GodmanException.ElevationHint,
                    ex);
            }
        }

        await SaveFileAsync(
            _paths.RegistryFile,
            new InstallRegistry { Installs = userEntries, ActiveId = registry.ActiveId },
            cancellationToken);
    }

    /// <summary>
    /// Lifts global-scope entries out of this (elevated) process's own user
    /// registry and into the machine-wide global registry. Idempotent: entries
    /// already present in the global registry (by Id) are not duplicated, and a
    /// run that finds nothing to migrate touches neither file.
    ///
    /// Best-effort: a failure here (global file unreadable/unwritable, malformed
    /// JSON, etc.) is not this operation's problem to surface -- it is reported
    /// as a verbose warning and the caller proceeds with whatever LoadAsync can
    /// still merge from the two files as they stand.
    /// </summary>
    internal async Task MigrateGlobalOrphansAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ownRegistry = await LoadFileAsync(_paths.RegistryFile, cancellationToken);
            var orphans = ownRegistry.Installs.Where(x => x.Scope == InstallScope.Global).ToList();
            if (orphans.Count == 0)
            {
                return;
            }

            var globalRegistry = await LoadFileAsync(_paths.GlobalRegistryFile, cancellationToken);
            var existingGlobalIds = globalRegistry.Installs.Select(x => x.Id).ToHashSet();
            var toAdd = orphans.Where(x => !existingGlobalIds.Contains(x.Id)).ToList();

            if (toAdd.Count > 0)
            {
                globalRegistry.Installs.AddRange(toAdd);
                await SaveFileAsync(_paths.GlobalRegistryFile, globalRegistry, cancellationToken);
            }

            // Only drop the orphans from the own-user file once they are confirmed
            // safe in the global file (the save above either succeeded or this line
            // is unreached because it threw) -- never remove the only copy of an
            // entry before its replacement copy is durable.
            ownRegistry.Installs.RemoveAll(x => x.Scope == InstallScope.Global);
            await SaveFileAsync(_paths.RegistryFile, ownRegistry, cancellationToken);
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"could not migrate global-scope installs into the machine-wide registry: {ex.Message}");
        }
    }

    private async Task<InstallRegistry> LoadGlobalBestEffortAsync(CancellationToken cancellationToken)
    {
        var file = ResolveGlobalRegistryFileForRead();

        try
        {
            return await LoadFileAsync(file, cancellationToken);
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"could not read the machine-wide registry at {file}: {ex.Message}");
            return new InstallRegistry();
        }
    }

    /// <summary>
    /// The machine-wide registry moved with the global install root, and moving it
    /// needs privileges an ordinary caller does not have -- so on a machine where the
    /// migration has not run yet, the file is still at its old path. Reading falls
    /// back to it; writing never does (see <see cref="SaveAsync"/>, which always
    /// targets <see cref="AppPaths.GlobalRegistryFile"/>), so the first elevated
    /// operation settles the machine on the current layout.
    ///
    /// The current path wins whenever it exists, even if an old file is still lying
    /// around: after a migration the old one is a leftover, not a second source.
    /// </summary>
    private string ResolveGlobalRegistryFileForRead()
    {
        if (File.Exists(_paths.GlobalRegistryFile))
        {
            return _paths.GlobalRegistryFile;
        }

        foreach (var legacy in _paths.GetLegacyGlobalRegistryFiles())
        {
            if (File.Exists(legacy))
            {
                _diagnostics?.Warn($"reading the machine-wide registry from its pre-migration path {legacy}; run an elevated godman command to complete the move");
                return legacy;
            }
        }

        return _paths.GlobalRegistryFile;
    }

    /// <summary>
    /// Repairs entry paths left behind by a directory migration. <see cref="AppPaths"/>
    /// moves an old install root to its current location, but the absolute path recorded
    /// in the registry when the entry was written still points at the old root, so every
    /// later lookup -- activation, removal, the "does this install still exist" check --
    /// would miss.
    ///
    /// Applied to the merged view in memory only, and deliberately so: persisting here
    /// would mean a plain `list` or `doctor` writing to the registry, and for a Global
    /// entry writing the *global* file -- which an unprivileged caller cannot do, turning
    /// a read-only command into a permission failure.
    ///
    /// Nor does it need to persist. The rebase is derived and idempotent, so it is
    /// recomputed on every load and the in-memory view is always the authoritative one.
    /// (It does reach disk for user-scope entries on the next <see cref="SaveAsync"/>,
    /// which rewrites the user file unconditionally. Global entries usually will not:
    /// the global write is guarded on the *set of Ids* changing, which a path-only
    /// correction does not change.)
    /// </summary>
    private void RebaseRelocatedInstallPaths(InstallRegistry registry)
    {
        var relocations = _paths.GetInstallRootRelocations();
        if (relocations.Count == 0)
        {
            return;
        }

        foreach (var entry in registry.Installs)
        {
            if (string.IsNullOrEmpty(entry.Path) || Directory.Exists(entry.Path))
            {
                continue;
            }

            foreach (var (oldRoot, newRoot) in relocations)
            {
                if (!TryRebasePath(entry.Path, oldRoot, newRoot, out var rebased))
                {
                    continue;
                }

                // Only follow a relocation that actually landed. A migration blocked by
                // permissions (the common case: a global root that needs root to move)
                // leaves the files at the old path, and a half-finished one leaves them
                // at neither -- in both cases the recorded path is still the best
                // information available and must not be overwritten with a guess.
                if (!Directory.Exists(rebased))
                {
                    continue;
                }

                _diagnostics?.Warn($"Install {entry.Id} moved with its install root: {entry.Path} -> {rebased}");
                entry.Path = rebased;
                break;
            }
        }
    }

    /// <summary>
    /// Rewrites <paramref name="path"/> from under <paramref name="oldRoot"/> to under
    /// <paramref name="newRoot"/>. Matching is segment-aware, so a sibling root that
    /// merely shares a textual prefix (<c>/usr/local/bin/godman-old</c> against
    /// <c>/usr/local/bin/godman</c>) is not treated as a child.
    /// </summary>
    private static bool TryRebasePath(string path, string oldRoot, string newRoot, out string rebased)
    {
        rebased = string.Empty;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var root = oldRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0 || !path.StartsWith(root, comparison))
        {
            return false;
        }

        if (path.Length == root.Length)
        {
            rebased = newRoot;
            return true;
        }

        var separator = path[root.Length];
        if (separator != Path.DirectorySeparatorChar && separator != Path.AltDirectorySeparatorChar)
        {
            return false;
        }

        rebased = Path.Combine(newRoot, path[(root.Length + 1)..]);
        return true;
    }

    private async Task<InstallRegistry> LoadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new InstallRegistry();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<InstallRegistry>(stream, _jsonOptions, cancellationToken)
               ?? new InstallRegistry();
    }

    private async Task SaveFileAsync(string path, InstallRegistry registry, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, registry, _jsonOptions, cancellationToken);
    }

    /// <summary>
    /// Global entries win ties by Id -- the only way a duplicate Id could appear in
    /// both files is a migration whose global write succeeded but whose own-file
    /// removal did not (see <see cref="MigrateGlobalOrphansAsync"/>), and the
    /// global copy is the one a subsequent migration attempt will keep.
    /// </summary>
    private static List<InstallEntry> MergeInstalls(List<InstallEntry> globalInstalls, List<InstallEntry> userInstalls)
    {
        var merged = new List<InstallEntry>(globalInstalls);
        var globalIds = globalInstalls.Select(x => x.Id).ToHashSet();
        merged.AddRange(userInstalls.Where(x => !globalIds.Contains(x.Id)));
        return merged;
    }
}
