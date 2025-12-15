using System;
using System.Text.Json.Serialization;

namespace GodotManager.Domain;

internal enum InstallEdition
{
    Standard,
    DotNet
}

internal enum InstallPlatform
{
    Windows,
    Linux
}

internal sealed class InstallEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Version { get; set; } = string.Empty;
    public InstallEdition Edition { get; set; }
    public InstallPlatform Platform { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Checksum { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsActive { get; set; }
}
