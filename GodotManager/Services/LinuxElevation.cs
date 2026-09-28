using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;

namespace GodotManager.Services;

/// <summary>
/// The Linux side of "decide elevation before the first machine-wide write" (CLAUDE.md).
/// Windows hands such operations to a UAC-elevated child (<see cref="ElevatedActivator"/>,
/// <see cref="ElevatedRemover"/>, <see cref="ElevatedDeactivator"/>); Linux has no such
/// hand-off -- the user must already be running under sudo -- so the best it can do is stop
/// before writing anything and print the exact sudo command to run instead. Before this
/// existed nothing checked up front: an unprivileged <c>remove --delete</c> of a global entry
/// tried to delete the install's files first and only failed at the registry write, and had
/// the files been deletable it would have deleted the install and left it registered.
/// </summary>
/// <remarks>
/// The probe is a <b>writability</b> check on the global locations, not a uid check
/// (<see cref="Environment.IsPrivilegedProcess"/>). What an operation needs is the right to
/// write <i>there</i>, which is a property of the directories, not of the user: the test
/// suite performs global-scope operations unprivileged inside a writable temp
/// GODMAN_GLOBAL_ROOT, and so can anyone who points GODMAN_GLOBAL_ROOT at a prefix they own.
/// A uid check would send both to sudo for nothing.
/// </remarks>
internal static class LinuxElevation
{
    /// <summary>
    /// What the TUI passes as its arguments: its handlers run inside <c>godman tui</c>, so
    /// the command to re-run under sudo is the TUI itself, not the action picked in it.
    /// </summary>
    public static readonly IReadOnlyList<string> TuiArguments = ["tui"];

    /// <summary>
    /// The domain rule alone, free of any probing so it is directly testable: stop when the
    /// operation's own <paramref name="targetScope"/> is global and the global locations are
    /// not writable.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than Windows' <see cref="ElevatedActivator.TouchesMachineState"/>:
    /// the install being switched away from (or deactivated) does not count. On Linux the
    /// active pointer lives in the per-user registry, and leaving a global install writes only
    /// the user's env.sh plus a best-effort delete of the global shim; the global registry is
    /// written only when its set of entries changes. Stopping there was a dead end: the UAC
    /// child on Windows keeps the user's profile, but sudo resets HOME, so the printed
    /// <c>sudo godman activate &lt;user-id&gt;</c> / <c>deactivate</c> / <c>tui</c> ran against
    /// root's registry ("No install found"). Those operations proceed, and a surviving global
    /// shim is reported by <see cref="ShimShadowing"/> instead.
    /// </remarks>
    internal static bool MustStop(InstallScope targetScope, bool globalWritable) =>
        !globalWritable && targetScope == InstallScope.Global;

    /// <summary>
    /// Null when the operation may proceed; otherwise the failure to report, carrying the
    /// sudo command for <paramref name="arguments"/> (the command's own arguments, program
    /// excluded) as its hint. <paramref name="targetScope"/> is the scope the operation itself
    /// writes -- the install being installed, activated or removed -- never the previously
    /// active one (see <see cref="MustStop"/>). Always null on Windows, whose UAC path is
    /// unchanged. Probes only for a global target, so user-scope work never pays for it.
    /// </summary>
    public static GodmanException? Check(AppPaths paths, InstallScope targetScope, IReadOnlyList<string>? arguments)
    {
        if (OperatingSystem.IsWindows() || targetScope != InstallScope.Global)
        {
            return null;
        }

        // A GODMAN_GLOBAL_ROOT still carrying its pre-1.4.0 meaning points at an empty prefix.
        // The first global write there creates a registry, after which the old layout is no
        // longer recognised and its installs drop out of sight -- so stop whatever the
        // permissions. No sudo hint: the value is what needs fixing, and the warning says how.
        if (paths.LegacyGlobalRootOverrideWarning is { } legacy)
        {
            return new GodmanException($"{legacy} Nothing was changed.");
        }

        var blocked = FindUnwritable(GlobalLocations(paths));
        return MustStop(targetScope, globalWritable: blocked is null)
            ? Denied(blocked!, GodmanException.ElevationHintFor(arguments))
            : null;
    }

    /// <summary>
    /// <c>clean</c>'s version of <see cref="Check"/>: it has no scope of its own, so it touches
    /// machine state when there is something global of godman's to delete. On Linux that is
    /// judged by godman's own files, not by directories existing: the global shim directory
    /// is the shared <c>/usr/local/bin</c>, which is always there and never writable by a
    /// user, and an ordinary user cleaning only their own installs must not be sent to sudo.
    /// </summary>
    public static GodmanException? CheckClean(AppPaths paths, LauncherService launcher, IReadOnlyList<string>? arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var globalInstallRoot = paths.GetInstallRoot(InstallScope.Global);
        var globalShim = Path.Combine(paths.GetShimDirectory(InstallScope.Global), "godot");
        var launcherEntries = launcher.FindRemaining(InstallScope.Global);

        var locations = new List<string>();
        if (Directory.Exists(globalInstallRoot) || File.Exists(paths.GlobalRegistryFile))
        {
            locations.Add(globalInstallRoot);

            // clean deletes the root itself, which is a write to its parent (/usr/local/lib).
            if (Directory.Exists(globalInstallRoot) && Path.GetDirectoryName(globalInstallRoot) is { } parent)
            {
                locations.Add(parent);
            }
        }

        if (File.Exists(globalShim))
        {
            locations.Add(paths.GetShimDirectory(InstallScope.Global));
        }

        if (launcherEntries.Count > 0)
        {
            locations.Add(paths.GetLauncherDirectory(InstallScope.Global));
        }

        var blocked = FindUnwritable(locations);
        var target = locations.Count > 0 ? InstallScope.Global : InstallScope.User;
        if (!MustStop(target, globalWritable: blocked is null))
        {
            return null;
        }

        // sudo resets HOME, so the elevated clean removes root's user-scope files, not this
        // user's: say so, or the user's own installs and config silently survive. On its own
        // line, so copying the command to the end of its line copies only the command.
        return Denied(
            blocked!,
            GodmanException.ElevationHintFor(arguments)
                + "\nUnder sudo, clean also removes root's own user-scope godman files, not yours; "
                + "then run `godman clean` without sudo for yours.");
    }

    /// <summary>
    /// The global locations every global-target operation may write: the install root, the
    /// shim directory, and -- when it exists -- the global registry file itself (inside the
    /// root on Linux). The file is probed separately because a read-only registry in a
    /// writable root passes the directory probes, and the operation would then extract,
    /// write the shim or delete files before the registry save failed.
    /// </summary>
    private static IEnumerable<string> GlobalLocations(AppPaths paths)
    {
        yield return paths.GetInstallRoot(InstallScope.Global);
        yield return paths.GetShimDirectory(InstallScope.Global);
        if (File.Exists(paths.GlobalRegistryFile))
        {
            yield return paths.GlobalRegistryFile;
        }
    }

    private static GodmanException Denied(string blocked, string hint) =>
        new($"{blocked} is not writable by this user, so nothing was changed.", hint);

    internal static string? FindUnwritable(IEnumerable<string> paths) =>
        paths.FirstOrDefault(path => File.Exists(path) ? !IsFileWritable(path) : !IsWritable(path));

    /// <summary>
    /// Whether this process can open the existing file <paramref name="path"/> for writing.
    /// Opened without truncating (FileMode.Open) and closed at once, so the probe changes
    /// nothing. Best-effort: never throws, and any failure means "not writable".
    /// </summary>
    internal static bool IsFileWritable(string path)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this process can create a file in <paramref name="path"/>, or -- when it does
    /// not exist yet -- in its nearest existing ancestor, which is where creating it would
    /// write. Checked by actually creating (and deleting) a uniquely named file rather than
    /// reading mode bits, so ACLs, read-only mounts and root's bypass all count the way the
    /// real write will. Best-effort: never throws, and any failure means "not writable".
    /// </summary>
    internal static bool IsWritable(string path)
    {
        try
        {
            var directory = Path.GetFullPath(path);
            while (!Directory.Exists(directory))
            {
                var parent = Path.GetDirectoryName(directory);
                if (parent is null)
                {
                    return false;
                }

                directory = parent;
            }

            var probe = Path.Combine(directory, $".godman-write-probe-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
