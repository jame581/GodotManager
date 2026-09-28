namespace GodotManager.Infrastructure;

/// <summary>
/// Renders the command a user should type to run godman elevated, for remedies printed
/// on screen.
///
/// "Run <c>sudo godman …</c>" is only good advice when sudo can find godman. sudo replaces
/// PATH with its <c>secure_path</c>, which never contains <c>~/.local/bin</c> -- where
/// <c>install.sh</c> puts the binary by default -- and on a machine that has not migrated
/// yet <c>/usr/local/bin/godman</c> is a directory, so the binary cannot be there either.
/// On exactly the machines that need the remedy, <c>sudo godman</c> is "command not
/// found". So the remedy names the running binary by its full path unless it already sits
/// in a directory sudo searches.
/// </summary>
internal static class ElevatedCommandLine
{
    /// <summary>
    /// The directories of a stock sudo <c>secure_path</c> on Fedora, Debian and Ubuntu.
    /// Not universal: RHEL-family systems (RHEL, Rocky, Alma) leave <c>/usr/local/bin</c>
    /// and <c>/usr/local/sbin</c> off it, so there a godman in <c>/usr/local/bin</c> gets a
    /// bare <c>sudo godman</c> remedy that will not resolve. The real sudoers is not read
    /// (it is root-only); installing to <c>/usr/bin</c>, as the RPM does, works everywhere.
    /// </summary>
    internal static readonly IReadOnlyList<string> SecurePathDirectories =
        ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    public static string Render(string arguments) =>
        Render(arguments, Environment.ProcessPath, OperatingSystem.IsWindows());

    /// <param name="processPath">The running executable; injected for tests.</param>
    /// <param name="windows">
    /// On Windows elevation is a UAC prompt, not a prefix: the command is the same, run
    /// from an elevated terminal, and the callers keep their existing wording.
    /// </param>
    internal static string Render(string arguments, string? processPath, bool windows)
    {
        if (windows)
        {
            return $"godman {arguments}";
        }

        // Nothing better to offer when the process is not the godman binary itself: no
        // path at all, or `dotnet GodotManager.dll` during development, where the host
        // path would be the wrong thing to run.
        if (string.IsNullOrEmpty(processPath)
            || string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.Ordinal))
        {
            return $"sudo godman {arguments}";
        }

        var directory = Path.GetDirectoryName(processPath)?.TrimEnd('/');
        if (directory is not null && SecurePathDirectories.Contains(directory, StringComparer.Ordinal))
        {
            return $"sudo {Quote(Path.GetFileName(processPath))} {arguments}";
        }

        return $"sudo {Quote(processPath)} {arguments}";
    }

    private static string Quote(string path) =>
        path.IndexOfAny([' ', '\'', '"', '$', '`', '\\', '\t']) >= 0
            ? "'" + path.Replace("'", "'\\''") + "'"
            : path;
}
