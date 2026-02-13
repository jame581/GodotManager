using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GodotManager.Config;
using GodotManager.Domain;
using SharpCompress.Archives;
using SharpCompress.Common;

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

internal sealed record InstallPlan(InstallRequest Request, string TargetDirectory);

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
    private readonly HttpClient _httpClient;

    public InstallerService(AppPaths paths, RegistryService registry, EnvironmentService environment, HttpClient? httpClient = null)
    {
        _paths = paths;
        _registry = registry;
        _environment = environment;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<InstallEntry> InstallAsync(InstallRequest request, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var registry = await _registry.LoadAsync(cancellationToken);
        var plan = await ResolvePlanAsync(request, progress, cancellationToken);
        request = plan.Request;
        var targetDir = plan.TargetDirectory;

        if (request.DryRun)
        {
            return await DryRunInstallAsync(request, targetDir, registry, cancellationToken);
        }

        if (Directory.Exists(targetDir) && !request.Force)
        {
            throw new IOException($"Install directory already exists: {targetDir}. Use --force to overwrite.");
        }

        Directory.CreateDirectory(targetDir);

        // Get archive path if not already set from earlier download
        string archivePath;
        if (request.ArchivePath is not null)
        {
            archivePath = request.ArchivePath;
        }
        else
        {
            throw new InvalidOperationException("Archive path was not resolved.");
        }

        await ExtractAsync(archivePath, targetDir, progress, cancellationToken);

        // Ensure Linux Godot binary is executable after extraction
        if (request.Platform == InstallPlatform.Linux)
        {
            try
            {
                string? binary = null;
                var candidates = new[]
                {
                    Path.Combine(targetDir, "godot"),
                    Path.Combine(targetDir, "Godot"),
                    Path.Combine(targetDir, "Godot_v4"),
                    Path.Combine(targetDir, "Godot_v3")
                };

                binary = Array.Find(candidates, File.Exists);

                if (binary == null)
                {
                    var files = Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories);
                    binary = files.FirstOrDefault(f =>
                    {
                        var name = Path.GetFileName(f);
                        return name.StartsWith("Godot") || name.StartsWith("godot");
                    });
                }

                if (binary != null)
                {
                    UnixFilePermissions.MakeExecutable(binary);
                }
            }
            catch
            {
                // best-effort: if we fail to set executable permission, installation still succeeds
            }
        }

        var entry = new InstallEntry
        {
            Version = request.Version,
            Edition = request.Edition,
            Platform = request.Platform,
            Scope = request.Scope,
            Path = targetDir,
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
        string targetDir;

        if (request.InstallPath is not null)
        {
            targetDir = request.InstallPath;
        }
        else if (request.ArchivePath is not null)
        {
            archiveName = Path.GetFileName(request.ArchivePath);
            var folderName = BuildInstallFolderName(request, archiveName);
            targetDir = Path.Combine(_paths.GetInstallRoot(request.Scope), folderName);
        }
        else if (request.DownloadUri is not null)
        {
            var (tempPath, downloadedName) = await DownloadAsync(request.DownloadUri, cancellationToken, progress);
            archiveName = downloadedName;
            var folderName = BuildInstallFolderName(request, archiveName);
            targetDir = Path.Combine(_paths.GetInstallRoot(request.Scope), folderName);
            request = request with { ArchivePath = tempPath };
        }
        else
        {
            throw new InvalidOperationException("Could not determine installation directory.");
        }

        return new InstallPlan(request, targetDir);
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
            argumentBuilder.Append(QuoteArg(args[1]));
            argumentBuilder.Append(' ');
        }

        argumentBuilder.Append("install-elevated --payload ");
        argumentBuilder.Append(QuoteArg(encoded));

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argumentBuilder.ToString(),
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
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
            throw new InvalidOperationException("Elevation was canceled by the user.");
        }
    }

    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg) || arg.Contains(' ') || arg.Contains('"'))
        {
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        return arg;
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

    private static string BuildInstallFolderName(InstallRequest request, string? archiveName)
    {
        var candidate = Path.GetFileName(archiveName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return BuildFolderName(request);
        }

        var folderName = StripKnownArchiveSuffixes(candidate);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return BuildFolderName(request);
        }

        return folderName;
    }

    private static string StripKnownArchiveSuffixes(string fileName)
    {
        var suffixes = new[]
        {
            ".zip",
            ".exe",
            ".x86_64",
            ".apk"
        };

        var result = fileName;
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
                removedAny = true;
                break;
            }
        }

        return result;
    }

    private async Task<(string TempPath, string ArchiveName)> DownloadAsync(Uri uri, CancellationToken cancellationToken, Action<double>? progress)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Extract filename from Content-Disposition header or URL
        var archiveName = response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
                         ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                         ?? Path.GetFileName(uri.LocalPath);
        
        var total = response.Content.Headers.ContentLength ?? -1;
        var tempFile = Path.GetTempFileName();

        await using var network = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = File.Create(tempFile);
        var buffer = new byte[81920];
        long read = 0;
        int r;

        while ((r = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, r), cancellationToken);
            read += r;

            if (total > 0)
            {
                var pct = (double)read / total * 100d;
                progress?.Invoke(pct);
            }
        }

        progress?.Invoke(100);
        return (tempFile, archiveName);
    }

    private static async Task ExtractAsync(string archivePath, string destination, Action<double>? progress, CancellationToken cancellationToken)
    {
        using var archive = ArchiveFactory.Open(archivePath);
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
