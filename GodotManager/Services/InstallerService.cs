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
    KnownChecksum? Known = null);

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
    bool ChecksumVerified = false);

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
            AddedAt = DateTimeOffset.UtcNow
        };

        registry.Installs.RemoveAll(x => string.Equals(x.Path, targetDir, StringComparison.OrdinalIgnoreCase));
        registry.Installs.Add(entry);

        if (request.Activate)
        {
            registry.MarkActive(entry.Id);
            await _environment.ApplyActiveAsync(entry, cancellationToken);
        }

        await _registry.SaveAsync(registry, cancellationToken);

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

        try
        {
            await RunElevatedInstallAsync(elevatedRequest, cancellationToken);
        }
        finally
        {
            // The download happened here, so the cache entry is this process's to clean
            // up: the child took the local-archive branch and its plan has no
            // CacheFilePath at all. This is meant to run after the child has exited,
            // since the archive it is extracting is that very file (on the
            // cancellation path that is not guaranteed: WaitForExitAsync(cancellationToken)
            // throws immediately without killing the child, rather than waiting for
            // it. That path is unreachable today -- nothing here supplies a live
            // token -- and would be benign on Windows even if it happened, since
            // SharpCompress holds FileShare.Read on the archive during extraction,
            // so the delete would just raise a sharing violation that TryDelete
            // swallows). Non-null only for downloads, so a user-supplied --archive
            // is never touched.
            if (plan.CacheFilePath is not null)
            {
                _download.DeleteCacheEntry(plan.CacheFilePath);
            }
        }

        var registry = await _registry.LoadAsync(cancellationToken);
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
            request.Known?.Verified ?? false);

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

            await process.WaitForExitAsync(cancellationToken);
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

    private async Task<InstallEntry> DryRunInstallAsync(InstallRequest request, string targetDir, InstallRegistry registry, CancellationToken cancellationToken)
    {
        var entry = new InstallEntry
        {
            Version = request.Version,
            Edition = request.Edition,
            Platform = request.Platform,
            Scope = request.Scope,
            Path = targetDir,
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
