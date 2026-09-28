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
    /// operation touches machine state and the global locations are not writable. Machine
    /// state is touched exactly when <see cref="ElevatedActivator.TouchesMachineState"/> says
    /// so -- the target is global, or the install being deactivated/switched away from is.
    /// Operations with no "previous" install (remove, deactivate, install without
    /// <c>--activate</c>) pass null.
    /// </summary>
    internal static bool MustStop(InstallScope targetScope, InstallScope? previousActiveScope, bool globalWritable) =>
        !globalWritable && ElevatedActivator.TouchesMachineState(targetScope, previousActiveScope);

    /// <summary>
    /// Null when the operation may proceed; otherwise the failure to report, carrying the
    /// sudo command for <paramref name="arguments"/> (the command's own arguments, program
    /// excluded) as its hint. Always null on Windows, whose UAC path is unchanged. Probes
    /// only when the operation touches machine state, so user-scope work never pays for it.
    /// </summary>
    public static GodmanException? Check(
        AppPaths paths, InstallScope targetScope, InstallScope? previousActiveScope, IReadOnlyList<string>? arguments)
    {
        if (OperatingSystem.IsWindows() || !ElevatedActivator.TouchesMachineState(targetScope, previousActiveScope))
        {
            return null;
        }

        var blocked = FindUnwritable(GlobalLocations(paths));
        return MustStop(targetScope, previousActiveScope, globalWritable: blocked is null)
            ? Denied(blocked!, arguments)
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
        var touchesGlobal = locations.Count > 0 ? InstallScope.Global : InstallScope.User;
        return MustStop(touchesGlobal, null, globalWritable: blocked is null)
            ? Denied(blocked!, arguments)
            : null;
    }

    /// <summary>
    /// The global locations every global-scope operation may write: the install root (which
    /// on Linux also holds the global registry) and the shim directory.
    /// </summary>
    private static IEnumerable<string> GlobalLocations(AppPaths paths)
    {
        yield return paths.GetInstallRoot(InstallScope.Global);
        yield return paths.GetShimDirectory(InstallScope.Global);
    }

    private static GodmanException Denied(string blocked, IReadOnlyList<string>? arguments) =>
        new($"{blocked} is not writable by this user, so nothing was changed.",
            GodmanException.ElevationHintFor(arguments));

    internal static string? FindUnwritable(IEnumerable<string> paths) =>
        paths.FirstOrDefault(path => !IsWritable(path));

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
