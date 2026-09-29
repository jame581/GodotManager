using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GodotManager.Services;

/// <summary>
/// A checksum some other process already computed for the archive being installed,
/// together with whether that process matched it against the published sums. Set only
/// on the Windows elevated install, the one flow where the process that downloads is
/// not the process that installs.
/// </summary>
internal sealed record KnownChecksum(string Value, string Algorithm, bool Verified);

internal sealed record InstallRequest(
    string Version,
    InstallEdition Edition,
    InstallPlatform Platform,
    InstallScope Scope,
    Uri? DownloadUri,
    string? ArchivePath,
    string? InstallPath,
    bool Activate,
    bool Force,
    bool DryRun = false,
    ChecksumSource? Checksums = null,
    KnownChecksum? Known = null,
    bool CreateLauncherEntry = true);

internal sealed record InstallPlan(
    InstallRequest Request,
    string TargetDirectory,
    string? Checksum = null,
    string? ChecksumAlgorithm = null,
    bool ChecksumVerified = false,
    string? CacheFilePath = null,
    // Defaults to NotApplicable: every InstallPlan that does not come from an actual
    // download (a local --archive, --path, or the elevated child re-checking a
    // carried checksum) never ran verification at all, so there is nothing to warn
    // the user about. Only the DownloadUri branch of ResolvePlanAsync overrides these.
    ChecksumStatus ChecksumStatus = ChecksumStatus.NotApplicable,
    string? UnverifiedReason = null);

internal sealed record ElevatedInstallPayload(
    string Version,
    InstallEdition Edition,
    InstallPlatform Platform,
    InstallScope Scope,
    string? ArchivePath,
    string? InstallPath,
    bool Activate,
    bool Force,
    string? Checksum = null,
    string? ChecksumAlgorithm = null,
    bool ChecksumVerified = false,
    bool CreateLauncherEntry = true);

internal sealed class InstallerService
{
    private readonly AppPaths _paths;
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;
    private readonly DiagnosticContext? _diagnostics;
    private readonly DownloadService _download;

    public InstallerService(
        AppPaths paths,
        RegistryService registry,
        EnvironmentService environment,
        HttpClient? httpClient = null,
        DiagnosticContext? diagnostics = null,
        DownloadService? downloadService = null)
    {
        _paths = paths;
        _registry = registry;
        _environment = environment;
        _diagnostics = diagnostics;
        _download = downloadService ?? new DownloadService(paths, httpClient, diagnostics);
    }

    /// <param name="onVerified">
    /// Fired once, after the plan is resolved and before any files move, with the
    /// outcome of checking the download against the checksums published upstream.
    /// A transient diagnostic, not part of the persisted result: callers that care
    /// whether to warn the user (only the command layer does) read it here rather
    /// than through <see cref="InstallEntry"/>, which never carries it.
    /// </param>
    public async Task<InstallEntry> InstallAsync(
        InstallRequest request,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default,
        Action<ChecksumStatus, string?>? onVerified = null)
    {
        var registry = await _registry.LoadAsync(cancellationToken);
        var plan = await ResolvePlanAsync(request, progress, cancellationToken);
        onVerified?.Invoke(plan.ChecksumStatus, plan.UnverifiedReason);
        request = plan.Request;
        var targetDir = plan.TargetDirectory;
        var checksum = plan.Checksum;

        if (request.DryRun)
        {
            return await DryRunInstallAsync(request, targetDir, registry, cancellationToken);
        }

        if (Directory.Exists(targetDir) && !request.Force)
        {
            var owner = registry.Installs.FirstOrDefault(
                x => string.Equals(x.Path, targetDir, StringComparison.OrdinalIgnoreCase));

            var hint = owner is null
                ? "Re-run with --force to overwrite it, or delete the directory yourself."
                : $"Re-run with --force to overwrite it, or run: godman remove {owner.Id:N} --delete";

            throw new GodmanException($"Install directory already exists: {targetDir}", hint);
        }

        // Get archive path if not already set from earlier download
        if (request.ArchivePath is null)
        {
            throw new InvalidOperationException("Archive path was not resolved.");
        }

        var archivePath = request.ArchivePath;

        var targetParent = Path.GetDirectoryName(targetDir);
        if (string.IsNullOrEmpty(targetParent))
        {
            throw new GodmanException(
                $"Cannot determine the parent directory of {targetDir}",
                "Pass an absolute path to --path.");
        }

        // Directory.Move requires the destination's parent to exist, and requires
        // both ends on one volume. Deriving staging from the target's own parent
        // satisfies both for any --path, not just the default install root.
        Directory.CreateDirectory(targetParent);
        var stagingDir = Path.Combine(targetParent, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        // The merge branch is the one non-atomic step; if it throws part-way the target
        // is a mixture of old and new files and the user must be told so.
        var mergeStarted = false;

        try
        {
            await ExtractAsync(archivePath, stagingDir, progress, cancellationToken);

            if (Directory.Exists(targetDir))
            {
                // --force onto an existing directory merges. Replacing would delete
                // unrelated files, because --path accepts an arbitrary directory.
                mergeStarted = true;
                MergeDirectory(stagingDir, targetDir);
                TryDeleteStagingDirectory(stagingDir);
            }
            else
            {
                Directory.Move(stagingDir, targetDir);
            }

            // Ensure the Linux Godot binary is executable. This runs on targetDir
            // after the swap or merge, so it applies to both branches and is not
            // undone by File.Copy.
            if (request.Platform == InstallPlatform.Linux)
            {
                MakeGodotBinaryExecutable(targetDir);
            }
        }
        catch (OperationCanceledException)
        {
            TryDeleteStagingDirectory(stagingDir);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteStagingDirectory(stagingDir);

            throw new GodmanException(
                $"Installation failed while extracting to {targetDir}: {ex.Message}",
                mergeStarted
                    ? "The install directory was partially updated; re-run with --force once the cause is fixed."
                    : Directory.Exists(targetDir)
                        ? "The existing install was left in place."
                        : "No partial install was left behind.",
                ex);
        }

        var entry = new InstallEntry
        {
            Version = request.Version,
            Edition = request.Edition,
            Platform = request.Platform,
            Scope = request.Scope,
            Path = targetDir,
            Checksum = checksum,
            ChecksumAlgorithm = plan.ChecksumAlgorithm,
            ChecksumVerified = plan.ChecksumVerified,
            LauncherEntry = request.CreateLauncherEntry,
            AddedAt = DateTimeOffset.UtcNow
        };

        // Captured before RemoveAll: when --force reinstalls the active install in place,
        // the previous active entry is the one being replaced, and its cleanup still has to
        // run against the scope it was activated in.
        var previousActive = request.Activate ? registry.GetActive() : null;

        // Captured here, before RemoveAll drops them; their launcher files are deleted only
        // after the save below succeeds.
        var replaced = registry.Installs
            .Where(x => string.Equals(x.Path, targetDir, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var activeIdBeforeReplace = registry.ActiveId;

        // A --force reinstall in place keeps the entry's Id. ActiveId lives in the per-user
        // registry, and on Linux a global install runs under sudo, which resets HOME: the
        // process doing the reinstall loads root's registry, never sees the user's ActiveId,
        // and could not move it. A fresh Id would leave the user's pointer dangling --
        // GetActive() null, `deactivate` refusing, the shim still running -- on the main
        // Linux global flow. Same scope only: an entry of another scope is not this
        // install's. It also keeps the Linux .desktop name (which carries the Id) stable, so
        // no duplicate "Godot X" survives in the app menu.
        var replacedSameScope = replaced.FirstOrDefault(x => x.Scope == entry.Scope);
        if (replacedSameScope is not null)
        {
            entry.Id = replacedSameScope.Id;
        }

        // Only when *this* registry says the replaced entry was the active one -- what the
        // Id above cannot do for a process that cannot see it -- the activation is
        // re-applied to the reinstalled directory: the shim names an executable, not the
        // directory, so a different version merged over the old one would otherwise leave
        // the shim running the old binary under a registry that says the new one is active.
        var replacedActive = replaced.FirstOrDefault(x => x.Id == activeIdBeforeReplace && x.Scope == entry.Scope);

        // Whether the opt-in Windows desktop shortcut exists, read before anything below can
        // delete it (RemoveActiveAsync does). It belongs to the activation, which the
        // reinstall in place inherits, so a reinstall must not take it away.
        var keepDesktopShortcut = replacedActive is not null && _environment.Launcher.DesktopShortcutExists(replacedActive);

        registry.Installs.RemoveAll(x => string.Equals(x.Path, targetDir, StringComparison.OrdinalIgnoreCase));

        // The Start Menu name has to be chosen before the save, since it is recorded on the
        // entry: a second install of one version and edition takes a suffixed name instead
        // of overwriting the first's shortcut. Windows only -- the Linux .desktop name
        // already carries the id.
        if (OperatingSystem.IsWindows() && request.CreateLauncherEntry)
        {
            entry.LauncherFileName = LauncherService.ChooseStartMenuFileName(entry, registry.Installs);
        }

        registry.Installs.Add(entry);

        var reapplyActivation = replacedActive is not null && !request.Activate;
        if (reapplyActivation)
        {
            registry.MarkActive(entry.Id);
        }

        if (request.Activate)
        {
            // Same cleanup `activate` does. Without it, switching the active install through
            // `install --activate` (and the TUI install dialog, which always activates) left
            // the previous activation's shim, PATH entry and desktop shortcut behind. On
            // Windows, callers that would need elevation for this split the activation off
            // beforehand -- see NeedsSeparateElevatedActivation; that split is Windows-only.
            // On Linux it always runs here, in-process; when unprivileged, RemoveUnix cannot
            // delete a global shim (EACCES), only warns under --verbose, and the shim
            // survives -- the callers report it afterwards through ShimShadowing.GetWarning.
            if (previousActive is not null)
            {
                try
                {
                    await _environment.RemoveActiveAsync(previousActive, cancellationToken);
                }
                catch (Exception ex)
                {
                    _diagnostics?.Warn($"Failed to clean up previous activation ({previousActive.Version}): {ex.Message}");
                }
            }

            registry.MarkActive(entry.Id);
            // Without the launcher entry: that is created below, only once the save lands.
            await _environment.ApplyActiveAsync(
                entry, dryRun: false, createDesktopShortcut: keepDesktopShortcut, ensureLauncherEntry: false, cancellationToken);
        }

        await _registry.SaveAsync(registry, cancellationToken);

        // After the save, unlike the --activate branch above: the files were merged already, so
        // a failed write leaves the shim naming a directory that still exists and a registry
        // that still describes it, rather than a shim ahead of the registry.
        if (reapplyActivation)
        {
            // Best-effort: the install is committed, so a shim or PATH write that fails here
            // must not report the whole install as failed and skip the launcher work below.
            try
            {
                await _environment.ApplyActiveAsync(
                    entry, dryRun: false, createDesktopShortcut: keepDesktopShortcut, ensureLauncherEntry: false, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _diagnostics?.Warn($"Reinstalled {entry.Version}, but could not refresh its activation: {ex.Message}");
            }
        }

        // After the save, symmetric with remove: a failed registry write must neither leave
        // a launcher entry pointing at an unregistered install nor have deleted the entry of
        // the install it was meant to replace. Activation above deliberately skips the
        // launcher write, so this is the only place an install creates one. The deletes run
        // first: on Windows the old and new entries share one .lnk name, so Delete(old)
        // must not run after Create(entry); with --no-shortcut the old one is correctly
        // left removed.
        foreach (var old in replaced)
        {
            _environment.Launcher.Delete(old);

            // The desktop shortcut belongs to the activation. When the replacement inherited
            // it, the activation above already wrote it under the replacement's name; the old
            // file goes only if that name differs. Otherwise the replaced entry was active in
            // another scope and the shortcut has no owner left, so it goes.
            if (old.Id == activeIdBeforeReplace
                && (!ReferenceEquals(old, replacedActive) || !_environment.Launcher.SharesDesktopShortcut(old, entry)))
            {
                _environment.Launcher.DeleteDesktopShortcut(old);
            }
        }

        if (request.CreateLauncherEntry)
        {
            _environment.Launcher.Create(entry);
        }

        // Non-null only for downloads, so a user-supplied --archive is never touched.
        if (plan.CacheFilePath is not null)
        {
            _download.DeleteCacheEntry(plan.CacheFilePath);
        }

        return entry;
    }

    /// <param name="onVerified">See <see cref="InstallAsync"/>. On the elevation
    /// branch this fires from the plan resolved here in the unelevated parent —
    /// the process that actually ran the download and the sums check — rather than
    /// from anything the elevated child later re-derives.</param>
    public async Task<InstallEntry> InstallWithElevationAsync(
        InstallRequest request,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default,
        Action<ChecksumStatus, string?>? onVerified = null)
    {
        if (!OperatingSystem.IsWindows() || request.Scope != InstallScope.Global || WindowsElevationHelper.IsElevated())
        {
            return await InstallAsync(request, progress, cancellationToken, onVerified);
        }

        var plan = await ResolvePlanAsync(request, progress, cancellationToken);
        onVerified?.Invoke(plan.ChecksumStatus, plan.UnverifiedReason);

        // This process did the downloading and so is the only one that could compare
        // the bytes against the published sums. Carrying that result across is what
        // keeps the child's registry entry — and the marker `list` renders from it —
        // an account of what actually happened rather than of what the child could
        // still measure for itself.
        var elevatedRequest = plan.Request with
        {
            DownloadUri = null,
            DryRun = false,
            InstallPath = plan.TargetDirectory,
            Known = plan.Checksum is null
                ? null
                : new KnownChecksum(plan.Checksum, plan.ChecksumAlgorithm!, plan.ChecksumVerified)
        };

        var completed = false;
        try
        {
            await RunElevatedInstallAsync(elevatedRequest, cancellationToken);
            completed = true;
        }
        finally
        {
            // The download happened here, so the cache entry is this process's to
            // clean up: the child took the local-archive branch and its plan has no
            // CacheFilePath at all. On the cancellation path,
            // RunElevatedInstallAsync's own OperationCanceledException handling
            // attempts to kill the child and bounds the wait for its exit before
            // rethrowing -- but that kill is not guaranteed: the child may be
            // elevated in a way this process cannot terminate, or the wait may
            // simply time out. When that happens, this finally still runs (and the
            // archive may still be in use), which is exactly why the entry is kept
            // below rather than deleted.
            //
            // On cancellation specifically, the entry is deliberately kept rather
            // than deleted: the whole point of a cancelled elevated install is that
            // the completed, verified download survives so a retry reuses it
            // instead of re-downloading (same resume contract as the unelevated
            // path) -- and keeping it also avoids deleting a file the child may
            // still have open. Only a genuine outcome -- success or a real failure
            // -- clears the cache. Non-null only for downloads, so a user-supplied
            // --archive is never touched.
            //
            // A token cancelled *after* the child already succeeded does not make
            // this a cancelled install -- keying on `completed` rather than the
            // token alone is what stops that late cancel leaking the entry. That
            // includes the narrow window where the cancellation token fires after
            // the child already exited 0 but before this process observed it:
            // RunElevatedInstallAsync's OperationCanceledException handling treats
            // that as success rather than a cancel (see ChildAlreadySucceeded), so
            // `completed` is still set to true here.
            if (plan.CacheFilePath is not null && (completed || !cancellationToken.IsCancellationRequested))
            {
                _download.DeleteCacheEntry(plan.CacheFilePath);
            }
        }

        // Reaching here means RunElevatedInstallAsync returned without throwing, so
        // `completed` is always true and `cancellationToken` may already be
        // cancelled (the late-cancel-after-success window) -- CancellationToken.None
        // is what stops that from making this reload throw and report a finished
        // install as cancelled.
        var registry = await _registry.LoadAsync(CancellationToken.None);
        var match = registry.Installs
            .OrderByDescending(x => x.AddedAt)
            .FirstOrDefault(x =>
                string.Equals(x.Version, request.Version, StringComparison.OrdinalIgnoreCase) &&
                x.Edition == request.Edition &&
                x.Platform == request.Platform &&
                x.Scope == request.Scope &&
                string.Equals(x.Path, plan.TargetDirectory, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new InvalidOperationException("Installation completed but registry entry could not be found.");
    }

    private async Task<InstallPlan> ResolvePlanAsync(InstallRequest request, Action<double>? progress, CancellationToken cancellationToken)
    {
        if (request.DownloadUri is null && string.IsNullOrWhiteSpace(request.ArchivePath))
        {
            throw new InvalidOperationException("Provide either a download URL or a local archive path.");
        }

        string? checksum = null;
        string targetDir;

        if (request.InstallPath is not null)
        {
            // Normalize once, here. Staging derives the target's parent via
            // Path.GetDirectoryName, which returns "" for a bare relative name like
            // "mydir" and returns the directory itself for a trailing separator.
            // GetFullPath fixes the former; it preserves trailing separators, so the
            // latter needs the explicit trim. A root path trims to itself and has no
            // parent, but whether that reaches the "no parent directory" guard in
            // InstallAsync depends on --force: a root path almost always already
            // exists, so without --force the Directory.Exists guard nearer the top
            // of InstallAsync fires first, with its own --force hint -- a different
            // error entirely. Only with --force (or a root that somehow does not
            // exist) does execution reach the parent-directory guard below.
            // Path.GetFullPath("") throws ArgumentException instead of degenerating
            // usefully either way, so an empty or whitespace-only --path is routed
            // around it and left for that same downstream guard to catch.
            targetDir = string.IsNullOrWhiteSpace(request.InstallPath)
                ? request.InstallPath
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.InstallPath));
            if (request.ArchivePath is not null && File.Exists(request.ArchivePath))
            {
                checksum = await ComputeChecksumAsync(request.ArchivePath, cancellationToken);
            }

            if (request.Known is not null)
            {
                return ReconcileKnownChecksum(request, targetDir, checksum, request.Known);
            }
        }
        else if (request.ArchivePath is not null)
        {
            var folderName = BuildInstallFolderName(request, Path.GetFileName(request.ArchivePath));
            targetDir = Path.Combine(_paths.GetInstallRoot(request.Scope), folderName);
            if (File.Exists(request.ArchivePath))
            {
                checksum = await ComputeChecksumAsync(request.ArchivePath, cancellationToken);
            }
        }
        else if (request.DownloadUri is not null)
        {
            var outcome = await _download.DownloadAsync(
                request.DownloadUri, request.Checksums, progress, cancellationToken);

            // Folder naming uses the PRE-redirect name, exactly as before 1.3.0.
            // outcome.ResolvedFileName is the post-redirect name and is only ever the
            // SHA512-SUMS.txt lookup key; using it here would rename every install.
            var folderName = BuildInstallFolderName(request, outcome.ArchiveName);
            targetDir = Path.Combine(_paths.GetInstallRoot(request.Scope), folderName);
            request = request with { ArchivePath = outcome.FilePath };

            return new InstallPlan(
                request,
                targetDir,
                outcome.Sha512,
                "sha512",
                outcome.Status == ChecksumStatus.Verified,
                outcome.FilePath,
                outcome.Status,
                outcome.UnverifiedReason);
        }
        else
        {
            throw new InvalidOperationException("Could not determine installation directory.");
        }

        return new InstallPlan(request, targetDir, checksum, checksum is null ? null : "sha512");
    }

    /// <summary>
    /// Adopts a checksum another process computed for this archive.
    /// </summary>
    /// <remarks>
    /// The claim arrives base64 on the command line of a process running as
    /// administrator, so on its own it is an assertion, not evidence. This process
    /// re-hashes the archive and compares it against the claimed value, so a
    /// disagreement aborts the install. That narrows the window in which a swap
    /// would go undetected; it does not close it. <see cref="ComputeChecksumAsync"/>
    /// opens and closes its own handle here, during <c>ResolvePlanAsync</c>, and
    /// <c>ExtractAsync</c> reopens the same path afterwards, once
    /// <c>InstallAsync</c> has already run the target-exists check and created the
    /// staging directory — a swap timed into that gap would still defeat the
    /// re-hash. Fully closing it would mean holding one <c>FileShare</c>-restricted
    /// handle across both the hash and the extract, which is a larger change than
    /// this one. The window is real rather than theoretical regardless: the
    /// download lands in a cache directory the unelevated parent can write, and the
    /// child extracts it with administrator rights.
    ///
    /// What stays taken on trust is <c>Verified</c> itself — proving it would mean
    /// re-fetching the published sums inside the elevated process, turning a network
    /// hiccup into a downgrade of an install that was verified. Accepting it costs
    /// nothing extra, because a payload able to lie about <c>Verified</c> can equally
    /// point <c>ArchivePath</c> and <c>InstallPath</c> anywhere, which the elevated
    /// install already grants. Everything this method cannot check fails closed.
    /// </remarks>
    private InstallPlan ReconcileKnownChecksum(
        InstallRequest request, string targetDir, string? computed, KnownChecksum known)
    {
        if (computed is null || !string.Equals(known.Algorithm, "sha512", StringComparison.OrdinalIgnoreCase))
        {
            // Nothing to compare against: no readable archive, or an algorithm this
            // build does not compute. Keep whatever was measured here and drop the
            // claimed status rather than record a claim that was never checked.
            _diagnostics?.Warn(
                $"Ignoring a carried {known.Algorithm} checksum that could not be re-checked against the archive.");
            return new InstallPlan(request, targetDir, computed, computed is null ? null : "sha512");
        }

        if (!string.Equals(known.Value, computed, StringComparison.OrdinalIgnoreCase))
        {
            throw new GodmanException(
                "The archive changed after it was downloaded, so it is no longer the file that was checked.",
                "Re-run the install. If it happens again, something else on this machine is writing to the download cache.");
        }

        // The unelevated parent already ran the real verification and is the only
        // process that could have warned about it; that plumbing lives in
        // InstallWithElevationAsync, not here. This process only knows the bool the
        // payload carried, not why it is false when it is, so it cannot reconstruct
        // a specific reason -- but it can still fail closed on the status.
        return new InstallPlan(
            request, targetDir, computed, "sha512", known.Verified,
            ChecksumStatus: known.Verified ? ChecksumStatus.Verified : ChecksumStatus.Unverified,
            UnverifiedReason: known.Verified
                ? null
                : "the unelevated process that downloaded this archive could not verify it");
    }

    /// <summary>
    /// True when activating a freshly installed <paramref name="requestScope"/> install
    /// would write machine-wide state -- i.e. a user-scope install switching away from an
    /// active global one. Such callers install with Activate = false and then activate
    /// through <see cref="ElevatedActivator"/>, exactly as <c>activate</c> does. A
    /// global-scope install needs no split: on Windows it already runs wholly in the
    /// elevated child.
    /// </summary>
    internal static bool NeedsSeparateElevatedActivation(InstallScope requestScope, InstallScope? previousActiveScope) =>
        requestScope == InstallScope.User
        && ElevatedActivator.TouchesMachineState(requestScope, previousActiveScope);

    /// <summary>
    /// The activation half of an install, decided the way <c>activate</c> decides it and
    /// before anything is written (CLAUDE.md, "Decide elevation before the first
    /// machine-wide write"): a user-scope install over an active global one must clear
    /// machine-wide state, which an unelevated process cannot do, so it installs unactivated
    /// and <see cref="CompleteActivationAsync"/> hands the activation to the elevated child.
    /// The split is Windows-only (<see cref="ElevatedActivator.IsRequired"/> is false
    /// elsewhere). Shared by the CLI and the TUI install dialog, which had each composed
    /// this predicate, the request split and the launcher call by hand; they still present
    /// the outcome themselves, and the CLI announces the UAC prompt between the two calls.
    /// </summary>
    public Task<InstallActivationPlan> PlanActivationAsync(
        InstallRequest request, CancellationToken cancellationToken = default) =>
        PlanActivationAsync(request, ElevatedActivator.IsRequired, cancellationToken);

    internal async Task<InstallActivationPlan> PlanActivationAsync(
        InstallRequest request,
        Func<InstallScope, InstallScope?, bool> isElevationRequired,
        CancellationToken cancellationToken = default)
    {
        if (!request.Activate || request.DryRun)
        {
            return new InstallActivationPlan(request, ActivateSeparately: false);
        }

        var currentActive = (await _registry.LoadAsync(cancellationToken)).GetActive();
        return isElevationRequired(request.Scope, currentActive?.Scope)
            && NeedsSeparateElevatedActivation(request.Scope, currentActive?.Scope)
                ? new InstallActivationPlan(request with { Activate = false }, ActivateSeparately: true)
                : new InstallActivationPlan(request, ActivateSeparately: false);
    }

    /// <summary>
    /// The separate activation <see cref="PlanActivationAsync(InstallRequest, CancellationToken)"/>
    /// asked for, run through <see cref="ElevatedActivator"/> exactly as <c>activate</c> does;
    /// null when the plan needed none. Returns the outcome instead of printing it: the TUI
    /// calls this while Terminal.Gui owns the screen. Takes no cancellation token on
    /// purpose: the install has already committed, so a cancel from the dialog must not turn
    /// it into a "cancelled" one (the elevated launch never took one either).
    /// </summary>
    public async Task<ElevatedOperationResult?> CompleteActivationAsync(
        InstallActivationPlan plan, InstallEntry installed) =>
        plan.ActivateSeparately
            ? await ElevatedActivator.RunAsync(installed.Id, createDesktopShortcut: false)
            : null;

    /// <summary>
    /// Projects a request onto the wire format the elevated child is launched with.
    /// </summary>
    internal static ElevatedInstallPayload BuildElevatedPayload(InstallRequest request) =>
        new(
            request.Version,
            request.Edition,
            request.Platform,
            request.Scope,
            request.ArchivePath,
            request.InstallPath,
            request.Activate,
            request.Force,
            request.Known?.Value,
            request.Known?.Algorithm,
            request.Known?.Verified ?? false,
            request.CreateLauncherEntry);

    private async Task RunElevatedInstallAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        var payload = BuildElevatedPayload(request);

        var json = JsonSerializer.Serialize(payload);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var args = Environment.GetCommandLineArgs();
        var fileName = Environment.ProcessPath ?? args.First();

        var argumentBuilder = new StringBuilder();
        if (args.Length > 1 && args[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            argumentBuilder.Append(ProcessHelpers.QuoteArg(args[1]));
            argumentBuilder.Append(' ');
        }

        argumentBuilder.Append("install-elevated --payload ");
        argumentBuilder.Append(ProcessHelpers.QuoteArg(encoded));

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argumentBuilder.ToString(),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory
        };

        try
        {
            // Remove Mark of the Web so SmartScreen won't silently block runas
            WindowsElevationHelper.TryRemoveZoneIdentifier(fileName);

            using var process = Process.Start(psi);
            if (process == null)
            {
                throw new InvalidOperationException("Unable to start elevated installer.");
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // WaitForExitAsync(cancellationToken) can throw here not only when
                // the token fires while the child is still running, but also in the
                // narrow window where the child already exited 0 (it finished the
                // install) right as the cancel landed, before this await observed
                // that exit. Treat that case as success rather than a cancel: the
                // install genuinely completed, and killing an already-exited process
                // or reporting a finished install as "cancelled" would both be wrong.
                if (!ChildAlreadySucceeded(process))
                {
                    // Left alone, the elevated process would keep running unobserved
                    // (reading, or possibly still writing into, the very cache
                    // archive the caller's finally block is about to decide whether
                    // to delete) while this process reports "cancelled" and moves
                    // on. The kill is attempted and the wait bounded (see
                    // TryKillProcessTreeAsync); when it isn't confirmed, the child
                    // may still be using the archive or go on to complete the
                    // install on its own.
                    if (!await TryKillProcessTreeAsync(process))
                    {
                        // Kept regardless (see InstallWithElevationAsync): a
                        // surviving child may still be reading the archive, or go on
                        // to finish the install and write the registry itself, so
                        // deleting the cache here could delete a file still in use
                        // or force a needless re-download of a since-completed
                        // install.
                        _diagnostics?.Warn("the elevated installer did not confirm exit after cancellation; it may still be running.");
                    }
                    throw;
                }

                // Fall through instead of rethrowing: the ExitCode check below sees
                // 0 and passes, so this method returns normally and
                // InstallWithElevationAsync marks the install completed.
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Elevated installer failed with exit code {process.ExitCode}.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(
                "Elevation was canceled or blocked. If you downloaded this executable, " +
                "right-click it → Properties → Unblock, or run: Unblock-File '" + fileName + "'",
                ex);
        }
    }

    /// <summary>
    /// True when <paramref name="process"/> has already exited on its own with exit
    /// code 0 -- the late-cancel race in <see cref="RunElevatedInstallAsync"/> where
    /// the cancellation token fires after the elevated child finished successfully
    /// but before <c>WaitForExitAsync</c> observed that exit. Pure and
    /// side-effect-free so it can be tested directly against a real process.
    /// </summary>
    /// <remarks>
    /// <c>HasExited</c> and <c>ExitCode</c> both throw <see cref="InvalidOperationException"/>
    /// when no process is associated with the object any more (disposed, or never
    /// started); that is treated as "not a success" rather than allowed to escape,
    /// since the caller is already inside its own <c>OperationCanceledException</c>
    /// handling and must not have that replaced by a different exception.
    /// </remarks>
    internal static bool ChildAlreadySucceeded(Process process)
    {
        try
        {
            return process.HasExited && process.ExitCode == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort termination of the elevated child and everything it spawned,
    /// used only from the WaitForExitAsync cancellation path above. Cancellation
    /// must never fail because of this: the child may have already exited in the
    /// gap between the token firing and this call (or between the HasExited check
    /// and Kill itself), and Kill() only requests termination -- it does not wait
    /// for it -- so this awaits <paramref name="waitForExit"/> (real exit, by
    /// default) afterward to try to confirm the process, and whatever file handles
    /// it held, are actually gone before returning. That wait is itself bounded by
    /// <paramref name="timeout"/>: an elevated child the OS will not let us reap
    /// must not hang cancellation forever. The returned bool is true when exit was
    /// actually confirmed within that bound, or when no process is associated with
    /// the object any more (already exited or never started) -- both cases mean
    /// nothing is left to wait for. False covers every other outcome (timeout, a
    /// refused kill, or a partially-killed tree), and callers must treat false as
    /// "may still be running", not as failure.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than <c>private</c> purely so
    /// InstallerServiceInternalsTests (via this assembly's
    /// InternalsVisibleTo("GodotManager.Tests")) can spawn a real child process and
    /// drive this directly. This method itself has no Windows/elevation dependency
    /// -- it operates on any <see cref="Process"/> -- only the production call site
    /// in <see cref="RunElevatedInstallAsync"/> is gated to the Windows +
    /// Global-scope + unelevated branch. Do not call this from outside that call
    /// site and the tests that cover it directly.
    /// </remarks>
    internal static async Task<bool> TryKillProcessTreeAsync(
        Process process,
        TimeSpan? timeout = null,
        Func<Process, CancellationToken, Task>? waitForExit = null)
    {
        waitForExit ??= static (p, token) => p.WaitForExitAsync(token);

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            // Bounded: Kill() only requests termination, and an elevated child the OS
            // will not let us reap must not hang cancellation forever.
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            await waitForExit(process, cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // No process is associated with this object any more (it was disposed or never
            // started). Nothing is left to wait for.
            return true;
        }
        catch (Win32Exception)
        {
            // The OS refused the kill (e.g. already terminating, or access denied).
            return false;
        }
        catch (AggregateException)
        {
            // Documented for Kill(entireProcessTree: true) when part of the tree could not
            // be killed -- the likely case for an unelevated parent and an elevated child.
            // Escaping here would replace the OperationCanceledException the caller is
            // about to rethrow, turning a cancel into a reported failure.
            return false;
        }
    }

    private async Task<InstallEntry> DryRunInstallAsync(InstallRequest request, string targetDir, InstallRegistry registry, CancellationToken cancellationToken)
    {
        var entry = new InstallEntry
        {
            Version = request.Version,
            Edition = request.Edition,
            Platform = request.Platform,
            Scope = request.Scope,
            Path = targetDir,
            LauncherEntry = request.CreateLauncherEntry,
            AddedAt = DateTimeOffset.UtcNow
        };

        await Task.CompletedTask;
        return entry;
    }

    private static string BuildFolderName(InstallRequest request)
    {
        var edition = request.Edition == InstallEdition.DotNet ? "dotnet" : "standard";
        var platform = request.Platform == InstallPlatform.Windows ? "windows" : "linux";
        var scope = request.Scope == InstallScope.Global ? "global" : "user";
        return $"{request.Version}-{edition}-{platform}-{scope}";
    }

    internal static string BuildInstallFolderName(InstallRequest request, string? archiveName)
    {
        var candidate = Path.GetFileName(archiveName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return BuildFolderName(request);
        }

        var (folderName, removedSuffix) = StripKnownArchiveSuffixes(candidate);
        if (!removedSuffix && !candidate.Contains('.'))
        {
            return BuildFolderName(request);
        }

        if (string.IsNullOrWhiteSpace(folderName))
        {
            return BuildFolderName(request);
        }

        return folderName;
    }

    private static (string Value, bool RemovedSuffix) StripKnownArchiveSuffixes(string fileName)
    {
        var suffixes = new[]
        {
            ".tar.gz",
            ".tar",
            ".zip",
            ".exe",
            ".x86_64",
            ".apk"
        };

        var result = fileName;
        var removedSuffix = false;
        var removedAny = true;

        while (removedAny)
        {
            removedAny = false;

            foreach (var suffix in suffixes)
            {
                if (!result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result = result[..^suffix.Length].Trim();
                removedSuffix = true;
                removedAny = true;
                break;
            }
        }

        return (result, removedSuffix);
    }

    internal static async Task<string> ComputeChecksumAsync(string filePath, CancellationToken cancellationToken = default)
    {
        using var sha512 = SHA512.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await sha512.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Copies <paramref name="source"/> over <paramref name="destination"/>, overwriting
    /// collisions and leaving everything else in the destination untouched.
    /// </summary>
    /// <remarks>
    /// Only ever called with a staging directory ExtractAsync just populated. That
    /// method filters out archive entries where IsDirectory is true, so nothing it
    /// writes ever leaves an empty directory behind -- every directory under
    /// <paramref name="source"/> contains at least one file somewhere beneath it.
    /// That is what makes a directory-only pre-pass unnecessary: the per-file
    /// Directory.CreateDirectory below already creates every directory that matters
    /// as it copies the file that justifies it existing.
    /// </remarks>
    private static void MergeDirectory(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private void MakeGodotBinaryExecutable(string directory)
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(directory, "godot"),
                Path.Combine(directory, "Godot"),
                Path.Combine(directory, "Godot_v4"),
                Path.Combine(directory, "Godot_v3")
            };

            var binary = Array.Find(candidates, File.Exists);

            if (binary == null)
            {
                var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
                binary = files.FirstOrDefault(f =>
                {
                    var name = Path.GetFileName(f);
                    return name.StartsWith("Godot") || name.StartsWith("godot");
                });
            }

            if (binary != null)
            {
                UnixFilePermissions.MakeExecutable(binary, _diagnostics);
            }
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to set executable permission on Godot binary: {ex.Message}");
        }
    }

    private void TryDeleteStagingDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to clean up staging directory {path}: {ex.Message}");
        }
    }

    private static async Task ExtractAsync(string archivePath, string destination, Action<double>? progress, CancellationToken cancellationToken)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        var total = entries.Count;
        var processed = 0;

        foreach (var entry in entries)
        {
            entry.WriteToDirectory(destination, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });

            processed++;
            var pct = total == 0 ? 100 : (double)processed / total * 100d;
            progress?.Invoke(pct);
            cancellationToken.ThrowIfCancellationRequested();
        }

        await Task.CompletedTask;
    }
}
