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
                    OperatingSystem.IsWindows()
                        ? "Global-scope installs require administrator privileges. Re-run elevated."
                        : "Global-scope installs require root privileges. Re-run with sudo.",
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
        try
        {
            return await LoadFileAsync(_paths.GlobalRegistryFile, cancellationToken);
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"could not read the machine-wide registry at {_paths.GlobalRegistryFile}: {ex.Message}");
            return new InstallRegistry();
        }
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
