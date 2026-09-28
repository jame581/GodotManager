namespace GodotManager.Config;

/// <summary>
/// The one segment-aware "is this path under that root" rule, shared by everything that
/// follows an install-root move: <see cref="Services.RegistryService"/> rebasing entry
/// paths, <see cref="AppPaths"/> repairing the shim and env script it moved out from
/// under, and <c>doctor</c> deciding whether a legacy root is still referenced. Keeping one
/// copy matters because the failure mode is silent: a textual prefix match would treat
/// <c>/usr/local/bin/godman-old</c> as a child of <c>/usr/local/bin/godman</c>.
/// </summary>
internal static class PathRebase
{
    private static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Rewrites <paramref name="path"/> from under <paramref name="oldRoot"/> to under
    /// <paramref name="newRoot"/>. Matching is segment-aware, so a sibling root that
    /// merely shares a textual prefix is not treated as a child.
    /// </summary>
    public static bool TryRebase(string path, string oldRoot, string newRoot, out string rebased)
    {
        rebased = string.Empty;

        var root = oldRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0 || !path.StartsWith(root, Comparison))
        {
            return false;
        }

        if (path.Length == root.Length)
        {
            rebased = newRoot;
            return true;
        }

        var separator = path[root.Length];
        if (separator != Path.DirectorySeparatorChar && separator != Path.AltDirectorySeparatorChar)
        {
            return false;
        }

        rebased = Path.Combine(newRoot, path[(root.Length + 1)..]);
        return true;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or lies under it.</summary>
    public static bool IsUnder(string path, string root) => TryRebase(path, root, root, out _);
}
