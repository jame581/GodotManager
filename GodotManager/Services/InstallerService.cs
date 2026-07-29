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
    bool DryRun = false);

internal sealed record InstallPlan(
    InstallRequest Request,
    string TargetDirectory,
    string? Checksum = null,
    string? ChecksumAlgorithm = null,
    bool ChecksumVerified = false,
    string? CacheFilePath = null);

internal sealed record ElevatedInstallPayload(
    string Version,
    InstallEdition Edition,
    InstallPlatform Platform,
    InstallScope Scope,
    string? ArchivePath,
    string? InstallPath,
    bool Activate,
    bool Force);

internal sealed class InstallerService
{
    private readonly AppPaths _paths;
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;
    private readonly DiagnosticContext? _diagnostics;
    private readonly HttpClient _httpClient;

    public InstallerService(AppPaths paths, RegistryService registry, EnvironmentService environment, HttpClient? httpClient = null, DiagnosticContext? diagnostics = null)
    {
        _paths = paths;
        _registry = registry;
        _environment = environment;
        _diagnostics = diagnostics;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<InstallEntry> InstallAsync(InstallRequest request, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var registry = await _registry.LoadAsync(cancellationToken);
        var plan = await ResolvePlanAsync(request, progress, cancellationToken);
        request = plan.Request;
        var targetDir = plan.TargetDirectory;
        var checksum = plan.Checksum;

        if (request.DryRun)
        {
            return await DryRunInstallAsync(request, targetDir, registry, cancellationToken);
        }

        if (Directory.Exists(targetDir) && !request.Force)
        {
            throw new IOException($"Install directory already exists: {targetDir}. Use --force to overwrite.");
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

        try
        {
            await ExtractAsync(archivePath, stagingDir, progress, cancellationToken);

            if (Directory.Exists(targetDir))
            {
                // --force onto an existing directory merges. Replacing would delete
                // unrelated files, because --path accepts an arbitrary directory.
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
                Directory.Exists(targetDir)
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
        return entry;
    }

    public async Task<InstallEntry> InstallWithElevationAsync(InstallRequest request, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || request.Scope != InstallScope.Global || WindowsElevationHelper.IsElevated())
        {
            return await InstallAsync(request, progress, cancellationToken);
        }

        var plan = await ResolvePlanAsync(request, progress, cancellationToken);
        var elevatedRequest = plan.Request with { DownloadUri = null, DryRun = false, InstallPath = plan.TargetDirectory };
        await RunElevatedInstallAsync(elevatedRequest, cancellationToken);

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

        string? archiveName = null;
        string? checksum = null;
        string targetDir;

        if (request.InstallPath is not null)
        {
            targetDir = request.InstallPath;
            if (request.ArchivePath is not null && File.Exists(request.ArchivePath))
            {
                checksum = await ComputeChecksumAsync(request.ArchivePath, cancellationToken);
            }
        }
        else if (request.ArchivePath is not null)
        {
            archiveName = Path.GetFileName(request.ArchivePath);
            var folderName = BuildInstallFolderName(request, archiveName);
            targetDir = Path.Combine(_paths.GetInstallRoot(request.Scope), folderName);
            if (File.Exists(request.ArchivePath))
            {
                checksum = await ComputeChecksumAsync(request.ArchivePath, cancellationToken);
            }
        }
        else if (request.DownloadUri is not null)
        {
            var (tempPath, downloadedName, downloadChecksum) = await DownloadAsync(request.DownloadUri, cancellationToken, progress);
            archiveName = downloadedName;
            checksum = downloadChecksum;
            var folderName = BuildInstallFolderName(request, archiveName);
            targetDir = Path.Combine(_paths.GetInstallRoot(request.Scope), folderName);
            request = request with { ArchivePath = tempPath };
        }
        else
        {
            throw new InvalidOperationException("Could not determine installation directory.");
        }

        return new InstallPlan(request, targetDir, checksum, checksum is null ? null : "sha512");
    }

    private async Task RunElevatedInstallAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        var payload = new ElevatedInstallPayload(
            request.Version,
            request.Edition,
            request.Platform,
            request.Scope,
            request.ArchivePath,
            request.InstallPath,
            request.Activate,
            request.Force);

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

    private async Task<(string TempPath, string ArchiveName, string Checksum)> DownloadAsync(Uri uri, CancellationToken cancellationToken, Action<double>? progress)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Extract filename from Content-Disposition header or URL
        var archiveName = response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
                         ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                         ?? Path.GetFileName(uri.LocalPath);

        var total = response.Content.Headers.ContentLength ?? -1;
        var tempFile = Path.GetTempFileName();

        using var sha512 = SHA512.Create();
        await using var network = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = File.Create(tempFile);
        var buffer = new byte[81920];
        long read = 0;
        int r;

        while ((r = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, r), cancellationToken);
            sha512.TransformBlock(buffer, 0, r, null, 0);
            read += r;

            if (total > 0)
            {
                var pct = (double)read / total * 100d;
                progress?.Invoke(pct);
            }
        }

        sha512.TransformFinalBlock([], 0, 0);
        var checksum = Convert.ToHexStringLower(sha512.Hash!);

        progress?.Invoke(100);
        return (tempFile, archiveName, checksum);
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
    private static void MergeDirectory(string source, string destination)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

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
