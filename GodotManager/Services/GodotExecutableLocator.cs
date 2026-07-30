using GodotManager.Infrastructure;

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
    /// Matching is forced case-insensitive on every platform. The default is
    /// <c>MatchCasing.PlatformDefault</c>, which is case-sensitive on Linux — that
    /// would silently narrow the behaviour this replaced, whose predecessor compared
    /// with <c>OrdinalIgnoreCase</c>. A lowercase <c>godot</c> binary is a real case:
    /// <c>InstallerService.MakeGodotBinaryExecutable</c> still probes for one.
    /// </summary>
    private static readonly EnumerationOptions MatchOptions =
        new() { MatchCasing = MatchCasing.CaseInsensitive };

    /// <summary>
    /// Returns the best Godot binary under <paramref name="installRoot"/>, or null
    /// when none is found. <paramref name="windows"/> is a parameter rather than an
    /// <c>OperatingSystem.IsWindows()</c> call so both layouts stay testable on
    /// either platform.
    /// </summary>
    public static string? Find(string installRoot, bool windows, DiagnosticContext? diagnostics = null)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
        {
            return null;
        }

        // Shallowest match wins, so a standard build's root binary is never passed
        // over in favour of something nested.
        return FindIn(installRoot, windows, diagnostics)
            ?? FindInSubdirectories(installRoot, windows, diagnostics);
    }

    private static string? FindInSubdirectories(string installRoot, bool windows, DiagnosticContext? diagnostics)
    {
        // Ordered, so that a directory holding several candidates (after a --force
        // merge, or a --path pointing somewhere hand-assembled) resolves to the same
        // binary on every machine rather than following filesystem enumeration order.
        var subdirectories = Enumerate(() => Directory.EnumerateDirectories(installRoot), installRoot, diagnostics)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        foreach (var dir in subdirectories)
        {
            if (FindIn(dir, windows, diagnostics) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static string? FindIn(string directory, bool windows, DiagnosticContext? diagnostics)
    {
        var pattern = windows ? "Godot*.exe" : "Godot*";

        return Enumerate(() => Directory.EnumerateFiles(directory, pattern, MatchOptions), directory, diagnostics)
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
    /// Enumeration is best-effort, matching the surrounding convention: a permission
    /// or race failure while probing must not turn activation into an error. It is
    /// still reported under <c>--verbose</c> — swallowing silently would leave a user
    /// whose install root is unreadable with a shim pointing at a nonexistent path
    /// and nothing to explain it.
    /// </summary>
    private static IEnumerable<string> Enumerate(
        Func<IEnumerable<string>> enumerate, string directory, DiagnosticContext? diagnostics)
    {
        try
        {
            return enumerate().ToList();
        }
        catch (Exception ex)
        {
            diagnostics?.Warn($"Could not enumerate files in {directory}: {ex.Message}");
            return [];
        }
    }
}
