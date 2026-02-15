using GodotManager.Domain;

namespace GodotManager.Services;

internal sealed class GodotDownloadUrlBuilder
{
    private const string ObjectStorageBase = "https://godot-releases.nbg1.your-objectstorage.com";
    private const string OfficialDownloadsBase = "https://downloads.godotengine.org/";

    public bool TryBuildUri(string version, InstallEdition edition, InstallPlatform platform, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        var versionSegment = $"{version}-stable";

        switch (platform)
        {
            case InstallPlatform.Linux:
                return TryLinux(version, edition, versionSegment, out uri, out error);
            case InstallPlatform.Windows:
                return TryWindows(version, edition, out uri, out error);
            default:
                error = "Unsupported platform.";
                return false;
        }
    }

    private bool TryLinux(string version, InstallEdition edition, string versionSegment, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        var file = edition switch
        {
            InstallEdition.Standard => $"Godot_v{version}-stable_linux.x86_64.zip",
            InstallEdition.DotNet => $"Godot_v{version}-stable_mono_linux_x86_64.zip",
            _ => null
        };

        if (file is null)
        {
            error = "Unsupported edition.";
            return false;
        }

        var candidate = $"{ObjectStorageBase}/{versionSegment}/{file}";
        return TryCreate(candidate, out uri, out error);
    }

    private bool TryWindows(string version, InstallEdition edition, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        var slug = edition switch
        {
            InstallEdition.Standard => "win64.exe.zip",
            InstallEdition.DotNet => "mono_win64.zip",
            _ => null
        };

        if (slug is null)
        {
            error = "Unsupported edition.";
            return false;
        }

        var candidate = $"{OfficialDownloadsBase}?version={version}&flavor=stable&slug={slug}&platform=windows.64";
        return TryCreate(candidate, out uri, out error);
    }

    private static bool TryCreate(string candidate, out Uri? uri, out string? error)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
        {
            uri = parsed;
            error = null;
            return true;
        }

        uri = null;
        error = "Failed to build download URL.";
        return false;
    }
}
