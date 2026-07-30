namespace GodotManager.Services;

/// <summary>
/// Locates the Godot binary inside an install directory, for the shim
/// EnvironmentService writes.
/// </summary>
/// <remarks>
/// Standard builds put the binary at the root of the install directory, so the
/// folder name alone used to be enough to construct its path. .NET/mono archives
/// do not: they extract into their own <c>Godot_vX-stable_mono_&lt;platform&gt;/</c>
/// directory, leaving the binary one level down. Searching only the top level
/// therefore finds nothing for every .NET-edition install, and the shim keeps a
/// fabricated path that was never on disk.
///
/// The search stops at the install directory's immediate children on purpose: a
/// mono install also contains <c>GodotSharp/</c>, and an unbounded recursion is a
/// standing invitation to bind the shim to whatever unrelated executable a future
/// archive layout happens to ship deeper in the tree.
/// </remarks>
internal static class GodotExecutableLocator
{
    /// <summary>
    /// Returns the best Godot binary under <paramref name="installRoot"/>, or null
    /// when none is found. <paramref name="windows"/> is a parameter rather than an
    /// <c>OperatingSystem.IsWindows()</c> call so both layouts stay testable on
    /// either platform.
    /// </summary>
    public static string? Find(string installRoot, bool windows)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
        {
            return null;
        }

        // Shallowest match wins, so a standard build's root binary is never passed
        // over in favour of something nested.
        return FindIn(installRoot, windows) ?? FindInSubdirectories(installRoot, windows);
    }

    private static string? FindInSubdirectories(string installRoot, bool windows)
    {
        foreach (var dir in Enumerate(() => Directory.EnumerateDirectories(installRoot)))
        {
            if (FindIn(dir, windows) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static string? FindIn(string directory, bool windows)
    {
        var pattern = windows ? "Godot*.exe" : "Godot*";

        return Enumerate(() => Directory.EnumerateFiles(directory, pattern))
            // Godot ships a _console variant beside the GUI binary; picking it by
            // enumeration order would pop an extra console window on every launch.
            .OrderBy(f => IsConsoleVariant(f) ? 1 : 0)
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsConsoleVariant(string path) =>
        Path.GetFileNameWithoutExtension(path)
            .EndsWith("_console", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Enumeration is best-effort, matching the surrounding convention: a
    /// permission or race failure while probing must not turn activation into an
    /// error.
    /// </summary>
    private static IEnumerable<string> Enumerate(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return enumerate().ToList();
        }
        catch
        {
            return [];
        }
    }
}
