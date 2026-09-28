using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;

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
/// leaves the remedy to the caller. On Linux an unprivileged activation cannot
/// delete the global shim under /usr/local/bin (nor a dead leftover there), and it
/// outranks the user shim under sudo's secure_path and in any shell that puts
/// /usr/local/bin before ~/.local/bin.
/// </remarks>
internal static class ShimShadowing
{
    /// <summary>
    /// The one shadowing check every front-end runs after a successful in-process
    /// activation (ActivateCommand, InstallCommand, TuiApp, InstallDialog). Returns
    /// the message to show, or null when nothing outranks the activation.
    /// </summary>
    /// <remarks>
    /// Returns text rather than printing because the TUI calls it while Terminal.Gui
    /// owns the screen. Best-effort: it runs after the activation has been committed,
    /// so any probing failure yields no warning instead of turning a successful
    /// activation into an error; <paramref name="onProbeFailure"/> lets the CLI keep
    /// reporting that the check itself failed.
    /// </remarks>
    public static string? GetWarning(
        AppPaths paths, InstallScope activatedScope, Action<string>? onProbeFailure = null)
    {
        try
        {
            var globalShimDir = paths.GetShimDirectory(InstallScope.Global);

            if (OperatingSystem.IsWindows())
            {
                var globalCmd = Path.Combine(globalShimDir, "godot.cmd");
                return WouldShadow(
                        activatedScope,
                        File.Exists(globalCmd),
                        globalShimDir,
                        Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine))
                    ? BuildWarning(globalCmd)
                    : null;
            }

            // Linux: EnvironmentService.RemoveUnix cannot delete the global shim from an
            // unprivileged process (EACCES), and a dead leftover from an earlier global
            // activation survives the same way. It is not checked against PATH: the shim
            // always wins under sudo's secure_path and in any shell that puts
            // /usr/local/bin first, while default login shells (Fedora's /etc/skel/.bashrc,
            // Debian/Ubuntu ~/.profile) usually put ~/.local/bin first -- so the warning may
            // fire when the current shell is not affected. It is kept unconditional because
            // sudo is exactly where godman tells users to run global commands.
            var globalShim = Path.Combine(globalShimDir, "godot");
            return activatedScope == InstallScope.User && File.Exists(globalShim)
                ? BuildUnixWarning(globalShim)
                : null;
        }
        catch (Exception ex)
        {
            onProbeFailure?.Invoke($"Could not check for a shadowing global shim: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The deactivate counterpart of <see cref="GetWarning"/>, run by DeactivateCommand and
    /// TuiApp after a successful in-process deactivation. On Linux an unprivileged
    /// deactivation of a global install proceeds (LinuxElevation.MustStop) but cannot delete
    /// the global shim, so `godot` keeps launching the install that was just deactivated.
    /// Null on Windows, where a global deactivation runs elevated and removes it. Best-effort.
    /// </summary>
    public static string? GetDeactivateWarning(AppPaths paths, InstallScope deactivatedScope)
    {
        if (OperatingSystem.IsWindows() || deactivatedScope != InstallScope.Global)
        {
            return null;
        }

        try
        {
            var globalShim = Path.Combine(paths.GetShimDirectory(InstallScope.Global), "godot");
            return File.Exists(globalShim)
                ? $"The global shim at {globalShim} could not be removed, so `godot` still launches the " +
                  $"install that was just deactivated. Remove it with `sudo rm {ElevatedCommandLine.Quote(globalShim)}` if no other " +
                  "user relies on the machine-wide install."
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Linux counterpart of <see cref="BuildWarning"/>: names the file, where it wins,
    /// and the remedy -- conditionally, since the shim may be a live machine-wide
    /// activation that other users on the machine still rely on.
    /// </summary>
    public static string BuildUnixWarning(string globalShimFile) =>
        $"The global shim at {globalShimFile} takes precedence over this user-scope activation " +
        $"wherever {Path.GetDirectoryName(globalShimFile)} comes before ~/.local/bin on PATH (including under sudo), " +
        "so `godot` there will keep running whatever that shim points at. If it is a leftover from an " +
        $"earlier global activation, remove it with `sudo rm {ElevatedCommandLine.Quote(globalShimFile)}`; if other users rely on the " +
        "machine-wide install, leave it.";

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

    /// <summary>
    /// Separators are hardcoded rather than taken from <see cref="Path"/>: this
    /// parses a Windows PATH value (it already assumes ';' as the delimiter), and on
    /// Linux both <c>DirectorySeparatorChar</c> and <c>AltDirectorySeparatorChar</c>
    /// are '/', so a host-derived trim would leave a trailing backslash in place and
    /// silently stop matching.
    /// </summary>
    private static string Normalize(string path) => path.Trim().TrimEnd('\\', '/');
}
