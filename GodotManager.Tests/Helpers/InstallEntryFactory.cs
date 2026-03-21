using GodotManager.Domain;
using System;

namespace GodotManager.Tests.Helpers;

/// <summary>
/// Factory for creating InstallEntry instances with sensible defaults.
/// </summary>
internal static class InstallEntryFactory
{
    public static InstallEntry Create(
        string version = "4.5.1",
        InstallEdition edition = InstallEdition.Standard,
        InstallScope scope = InstallScope.User,
        string? path = null,
        string? checksum = null)
    {
        return new InstallEntry
        {
            Version = version,
            Edition = edition,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = scope,
            Path = path ?? $"/tmp/godman-test/{Guid.NewGuid():N}",
            Checksum = checksum
        };
    }
}
