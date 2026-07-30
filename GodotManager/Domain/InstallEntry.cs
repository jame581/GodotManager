using System.Text.Json.Serialization;

namespace GodotManager.Domain;

public enum InstallEdition
{
    Standard,
    DotNet
}

public enum InstallPlatform
{
    Windows,
    Linux
}

public enum InstallScope
{
    User,
    Global
}

internal sealed class InstallEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Version { get; set; } = string.Empty;
    public InstallEdition Edition { get; set; }
    public InstallPlatform Platform { get; set; }
    public InstallScope Scope { get; set; } = InstallScope.User;
    public string Path { get; set; } = string.Empty;
    public string? Checksum { get; set; }

    /// <summary>
    /// Hash algorithm backing <see cref="Checksum"/>. Null means sha256, which is
    /// what godman &lt;= 1.2.0 wrote. New installs record "sha512".
    /// </summary>
    public string? ChecksumAlgorithm { get; set; }

    /// <summary>
    /// True when <see cref="Checksum"/> was matched against the checksums published
    /// upstream, rather than merely computed locally.
    /// </summary>
    public bool ChecksumVerified { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsActive { get; set; }
}
