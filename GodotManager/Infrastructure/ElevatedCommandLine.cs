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
/// in a directory every default sudo configuration searches.
/// </summary>
internal static class ElevatedCommandLine
{
    /// <summary>
    /// Directories on every mainstream default sudo <c>secure_path</c>. Deliberately not
    /// <c>/usr/local/bin</c> or <c>/usr/local/sbin</c>: Fedora, Debian and Ubuntu include
    /// them, but RHEL-family systems (RHEL, Rocky, Alma) do not, and the real sudoers is
    /// root-only so it cannot be checked. A binary there gets its full path, which works
    /// everywhere; only a binary in one of these gets the bare name.
    /// </summary>
    internal static readonly IReadOnlyList<string> SecurePathDirectories =
        ["/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    /// <summary>
    /// The command to print. On Linux it also carries a rooted GODMAN_GLOBAL_ROOT (or the
    /// legacy alias) as <c>sudo NAME=value …</c>: sudo's env_reset drops it, and every
    /// elevated command godman suggests would otherwise act on the default /usr/local
    /// instead of the prefix the rest of the advice assumed. A relative value is left out,
    /// since godman never acts on one (see AppPaths.MigrationGates).
    /// </summary>
    public static string Render(string arguments) =>
        Render(arguments, SudoEnvironment(), Environment.ProcessPath, OperatingSystem.IsWindows());

    /// <summary>
    /// <see cref="Render(string)"/> for a command given as separate arguments, each quoted
    /// for the shell the way the path is, so the printed command survives being pasted.
    /// </summary>
    public static string RenderArguments(IEnumerable<string> arguments) =>
        Render(string.Join(' ', arguments.Select(Quote)));

    private static (string Name, string Value)? SudoEnvironment() =>
        Config.AppPaths.ReadGlobalRootOverride() is { } variable && Path.IsPathRooted(variable.Value)
            ? variable
            : null;

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

    /// <summary>
    /// <see cref="Render(string, string?, bool)"/> with one environment variable passed
    /// through sudo as <c>sudo NAME=value …</c> (quoted like the path). Null renders the
    /// plain command.
    /// </summary>
    /// <param name="windows">
    /// Ignored environment on Windows: elevation there is a UAC prompt, with no command
    /// line to put the variable on.
    /// </param>
    internal static string Render(string arguments, (string Name, string Value)? environment, string? processPath, bool windows)
    {
        var command = Render(arguments, processPath, windows);
        if (windows || environment is not { } variable)
        {
            return command;
        }

        // Render's Unix output always starts with "sudo "; the assignment goes right after it.
        return $"sudo {variable.Name}={Quote(variable.Value)} {command["sudo ".Length..]}";
    }

    /// <summary>
    /// Single-quotes anything outside a conservative set of characters no shell treats
    /// specially, so a printed command survives being pasted: globs, <c>~</c>, <c>#</c>,
    /// <c>!</c>, braces, redirections and separators included. A single quote inside is
    /// written as <c>'\''</c>.
    /// </summary>
    private static string Quote(string value) =>
        value.Length > 0 && value.All(IsShellSafe)
            ? value
            : "'" + value.Replace("'", "'\\''") + "'";

    private static bool IsShellSafe(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
            or '_' or '.' or '/' or ':' or '=' or '@' or '%' or '+' or ',' or '-';
}
