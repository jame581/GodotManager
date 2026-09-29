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

    /// <summary>
    /// Whether this install should have an application-launcher entry. Null means the
    /// entry was recorded by godman &lt; 1.4.0, before launcher entries existed; that is
    /// treated as "wanted", so activating such an install creates one. False is an
    /// explicit <c>install --no-shortcut</c> and is honoured by activate and doctor.
    /// </summary>
    public bool? LauncherEntry { get; set; }

    /// <summary>
    /// Windows only: the file name of this install's Start Menu shortcut, recorded when the
    /// plain <c>Godot {version} ({edition}).lnk</c> was already held by another install in the
    /// scope (two installs of one version and edition at different <c>--path</c>s), so the
    /// second one no longer overwrites the first's shortcut and removing one no longer deletes
    /// the other's. Null means the plain name -- every entry before 1.4.1, and every install
    /// that had it to itself -- and nothing else is ever recorded.
    /// </summary>
    public string? LauncherFileName { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsActive { get; set; }
}
