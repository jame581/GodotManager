using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GodotManager.Config;
using GodotManager.Domain;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace GodotManager.Services;

internal sealed record InstallRequest(
    string Version,
    InstallEdition Edition,
    InstallPlatform Platform,
    Uri? DownloadUri,
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
        if (request.DownloadUri is null && string.IsNullOrWhiteSpace(request.ArchivePath))
        {
            throw new InvalidOperationException("Provide either a download URL or a local archive path.");
        }

        var registry = await _registry.LoadAsync(cancellationToken);
        var targetDir = request.InstallPath ?? Path.Combine(_paths.InstallRoot, BuildFolderName(request));

        if (Directory.Exists(targetDir) && !request.Force)
        {
            throw new IOException($"Install directory already exists: {targetDir}. Use --force to overwrite.");
        }

        Directory.CreateDirectory(targetDir);

        var archivePath = request.ArchivePath;
        if (archivePath is null && request.DownloadUri is not null)
        {
            archivePath = await DownloadAsync(request.DownloadUri, cancellationToken, progress);
        }

        if (archivePath is null)
        {
            throw new InvalidOperationException("Archive path was not resolved.");
        }

        await ExtractAsync(archivePath, targetDir, progress, cancellationToken);

        var entry = new InstallEntry
        {
            Version = request.Version,
            Edition = request.Edition,
            Platform = request.Platform,
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

    private static string BuildFolderName(InstallRequest request)
    {
        var edition = request.Edition == InstallEdition.DotNet ? "dotnet" : "standard";
        var platform = request.Platform == InstallPlatform.Windows ? "windows" : "linux";
        return $"{request.Version}-{edition}-{platform}";
    }

    private async Task<string> DownloadAsync(Uri uri, CancellationToken cancellationToken, Action<double>? progress)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

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
        return tempFile;
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
