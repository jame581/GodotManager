using GodotManager.Domain;

namespace GodotManager.Services;

/// <summary>
/// Detects a machine-wide godot shim that would take precedence over a freshly
/// activated user-scope one.
/// </summary>
/// <remarks>
/// Windows composes the effective PATH as Machine entries followed by User
/// entries, so a leftover global shim wins every lookup regardless of which
/// install the user just activated -- `godot` keeps launching the old version and
/// nothing in the output says why. Removing the global shim needs administrator
/// rights the activating user may not have, so this only reports the condition and
/// leaves the remedy to the caller.
/// </remarks>
internal static class ShimShadowing
{
    /// <summary>
    /// Pure so it is testable without touching PATH or the filesystem; the caller
    /// supplies the two facts it cannot derive.
    /// </summary>
    public static bool WouldShadow(
        InstallScope activatedScope,
        bool globalShimExists,
        string globalShimDirectory,
        string? machinePath)
    {
        if (activatedScope != InstallScope.User || !globalShimExists)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(machinePath) || string.IsNullOrWhiteSpace(globalShimDirectory))
        {
            return false;
        }

        // A shim that exists but whose directory is not on the machine PATH cannot
        // shadow anything, so it is not worth warning about.
        return machinePath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => string.Equals(Normalize(entry), Normalize(globalShimDirectory), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Message for the caller to render. Names the offending file, because the user
    /// otherwise has no way to find out which of the two shims won.
    /// </summary>
    public static string BuildWarning(string globalShimFile) =>
        $"A machine-wide shim at {globalShimFile} takes precedence over this user-scope " +
        "activation, so `godot` will keep launching the globally activated install. " +
        "Re-run activation from an elevated shell, or delete that file, to change it.";

    private static string Normalize(string path) =>
        path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
