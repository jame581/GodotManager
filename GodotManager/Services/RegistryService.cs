using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GodotManager.Config;
using GodotManager.Domain;

namespace GodotManager.Services;

internal sealed class RegistryService
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RegistryService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<InstallRegistry> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.RegistryFile))
        {
            return new InstallRegistry();
        }

        await using var stream = File.OpenRead(_paths.RegistryFile);
        var registry = await JsonSerializer.DeserializeAsync<InstallRegistry>(stream, _jsonOptions, cancellationToken)
                       ?? new InstallRegistry();
        if (registry.ActiveId.HasValue)
        {
            registry.MarkActive(registry.ActiveId.Value);
        }

        return registry;
    }

    public async Task SaveAsync(InstallRegistry registry, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_paths.RegistryFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_paths.RegistryFile);
        await JsonSerializer.SerializeAsync(stream, registry, _jsonOptions, cancellationToken);
    }
}
