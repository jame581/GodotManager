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
        if (request.DownloadUri is null && string.IsNullOrWhiteSpace(request.ArchivePath))
        {
            throw new InvalidOperationException("Provide either a download URL or a local archive path.");
        }

        var registry = await _registry.LoadAsync(cancellationToken);
        
        // For custom install paths, use as-is. Otherwise, derive from archive name.
        string? archiveName = null;
        string targetDir;
        
        // If custom install path specified, use it directly
        if (request.InstallPath is not null)
        {
            targetDir = request.InstallPath;
        }
        // Otherwise, we need to determine from archive - download or get from local path first
        else if (request.ArchivePath is not null)
        {
            archiveName = Path.GetFileName(request.ArchivePath);
            var folderName = archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? archiveName[..^4]
                : archiveName;
            targetDir = Path.Combine(_paths.GetInstallRoot(request.Scope), folderName);
        }
        else if (request.DownloadUri is not null)
        {
            // For download, we need to fetch the archive name first
            var (tempPath, downloadedName) = await DownloadAsync(request.DownloadUri, cancellationToken, progress);
            archiveName = downloadedName;
            var folderName = archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? archiveName[..^4]
                : archiveName;
            targetDir = Path.Combine(_paths.GetInstallRoot(request.Scope), folderName);
            
            // Store for later use
            request = request with { ArchivePath = tempPath };
        }
        else
        {
            throw new InvalidOperationException("Could not determine installation directory.");
        }

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

    private async Task<(string TempPath, string ArchiveName)> DownloadAsync(Uri uri, CancellationToken cancellationToken, Action<double>? progress)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Extract filename from Content-Disposition header or URL
        var archiveName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"') 
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
