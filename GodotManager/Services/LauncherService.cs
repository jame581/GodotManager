using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using System.Text;

namespace GodotManager.Services;

/// <summary>
/// Owns every application-launcher file godman writes for an install: an XDG
/// <c>.desktop</c> entry on Linux, a Start Menu <c>.lnk</c> on Windows, plus the
/// optional Windows desktop shortcut. One entry per installed version, created on
/// install and deleted on remove/clean.
///
/// Everything here is best-effort, like shim cleanup: a failure is a verbose
/// <c>warn:</c>, never an exception, because a missing launcher entry must not fail
/// an install or strand a removal half-done. And never <c>WarnAlways</c> -- the TUI
/// calls this while Terminal.Gui owns the screen.
/// </summary>
internal sealed class LauncherService
{
    internal const string DesktopFilePrefix = "godman-godot-";
    private const string IconResourceName = "GodotManager.Assets.godot-icon.svg";

    private readonly AppPaths _paths;
    private readonly DiagnosticContext? _diagnostics;

    public LauncherService(AppPaths paths, DiagnosticContext? diagnostics = null)
    {
        _paths = paths;
        _diagnostics = diagnostics;
    }

    public void Create(InstallEntry entry)
    {
        try
        {
            var windows = OperatingSystem.IsWindows();
            var exe = GodotExecutableLocator.ResolveForInstall(entry.Path, windows, _diagnostics);
            var entryPath = GetEntryPath(entry, _paths);
            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);

            if (windows)
            {
                WindowsShortcut.Create(entryPath, exe, entry.Path, DisplayName(entry));
                return;
            }

            var iconPath = _paths.GetLauncherIconPath(entry.Scope)!;
            WriteIcon(iconPath);
            File.WriteAllText(entryPath, BuildDesktopFile(entry, exe, iconPath));
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to create launcher entry for {entry.Version}: {ex.Message}");
        }
    }

    public void CreateDesktopShortcut(InstallEntry entry)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var exe = GodotExecutableLocator.ResolveForInstall(entry.Path, windows: true, _diagnostics);
            Directory.CreateDirectory(_paths.DesktopDirectory);
            WindowsShortcut.Create(GetDesktopShortcutPath(entry, _paths), exe, entry.Path, DisplayName(entry));
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to create desktop shortcut for {entry.Version}: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes the install's launcher entry. Not the desktop shortcut: that belongs to the
    /// activation and goes through <see cref="EnvironmentService.RemoveActiveAsync"/>, which
    /// every remove path runs for the active install. Its name is shared by every install
    /// of the same version and edition, so deleting it here would take the active
    /// install's shortcut away when a non-active sibling is removed.
    /// </summary>
    public void Delete(InstallEntry entry)
    {
        TryDelete(GetEntryPath(entry, _paths));
    }

    public bool DesktopShortcutExists(InstallEntry entry) =>
        OperatingSystem.IsWindows() && File.Exists(GetDesktopShortcutPath(entry, _paths));

    /// <summary>
    /// Whether two entries map to the same desktop shortcut file. The name carries only
    /// version and edition, so a reinstall in place of the same ones rewrites the file it
    /// found, while a different version at that path has to delete the old name.
    /// </summary>
    internal bool SharesDesktopShortcut(InstallEntry a, InstallEntry b) =>
        string.Equals(GetDesktopShortcutPath(a, _paths), GetDesktopShortcutPath(b, _paths), StringComparison.OrdinalIgnoreCase);

    public void DeleteDesktopShortcut(InstallEntry entry)
    {
        if (OperatingSystem.IsWindows())
        {
            TryDelete(GetDesktopShortcutPath(entry, _paths));
        }
    }

    public bool Exists(InstallEntry entry) => File.Exists(GetEntryPath(entry, _paths));

    /// <summary>
    /// Removes every launcher file godman owns in <paramref name="scope"/>. On Linux the
    /// <c>applications/</c> directory is shared with every other app, so only files with
    /// godman's prefix are touched. On Windows the Start Menu folder is godman's own and
    /// goes whole; the desktop is not ours, so only the exact names of
    /// <paramref name="installs"/> are deleted there.
    /// </summary>
    public IReadOnlyList<string> DeleteAll(InstallScope scope, IEnumerable<InstallEntry> installs)
    {
        var removed = new List<string>();
        var dir = _paths.GetLauncherDirectory(scope);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                    removed.Add(dir);
                }

                foreach (var entry in installs.Where(x => x.Scope == scope))
                {
                    var shortcut = GetDesktopShortcutPath(entry, _paths);
                    if (TryDelete(shortcut))
                    {
                        removed.Add(shortcut);
                    }
                }
            }
            else
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.EnumerateFiles(dir, DesktopFilePrefix + "*.desktop").ToList())
                    {
                        if (TryDelete(file))
                        {
                            removed.Add(file);
                        }
                    }
                }

                var icon = _paths.GetLauncherIconPath(scope);
                if (icon is not null && TryDelete(icon))
                {
                    removed.Add(icon);
                }
            }
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to clean launcher entries in {dir}: {ex.Message}");
        }

        return removed;
    }

    /// <summary>
    /// The godman launcher files <see cref="DeleteAll"/> would target that are still
    /// on disk -- after a DeleteAll, the ones it could not remove (an unprivileged
    /// clean against the global directory). Same selection as DeleteAll: Linux the
    /// prefixed <c>.desktop</c> files plus the icon, Windows the Start Menu folder.
    /// Returned rather than reported: the caller owns the output. Best-effort; a
    /// directory it cannot even enumerate yields an empty list.
    /// </summary>
    public IReadOnlyList<string> FindRemaining(InstallScope scope)
    {
        var remaining = new List<string>();
        var dir = _paths.GetLauncherDirectory(scope);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (Directory.Exists(dir))
                {
                    remaining.Add(dir);
                }
            }
            else
            {
                if (Directory.Exists(dir))
                {
                    remaining.AddRange(Directory.EnumerateFiles(dir, DesktopFilePrefix + "*.desktop"));
                }

                var icon = _paths.GetLauncherIconPath(scope);
                if (icon is not null && File.Exists(icon))
                {
                    remaining.Add(icon);
                }
            }
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to list launcher entries in {dir}: {ex.Message}");
        }

        return remaining;
    }

    internal static string DisplayName(InstallEntry entry) => $"Godot {entry.Version} ({entry.Edition})";

    /// <summary>
    /// Lowercase, <c>[a-z0-9.-]</c> only, and carries the first 8 hex digits of the Id so
    /// two installs of one version at different <c>--path</c>s never share a file.
    /// </summary>
    internal static string BuildDesktopFileName(InstallEntry entry)
    {
        var raw = $"{DesktopFilePrefix}{entry.Version}-{entry.Edition}-{entry.Scope}-{entry.Id.ToString("N")[..8]}".ToLowerInvariant();
        var builder = new StringBuilder(raw.Length + 8);
        foreach (var c in raw)
        {
            builder.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' ? c : '-');
        }

        return builder.Append(".desktop").ToString();
    }

    internal static string GetEntryPath(InstallEntry entry, AppPaths paths) =>
        Path.Combine(
            paths.GetLauncherDirectory(entry.Scope),
            OperatingSystem.IsWindows() ? StartMenuFileName(entry) : BuildDesktopFileName(entry));

    /// <summary>
    /// The Start Menu file name: the one recorded for this entry, or the plain one built from
    /// its version and edition (see <see cref="InstallEntry.LauncherFileName"/>).
    /// </summary>
    internal static string StartMenuFileName(InstallEntry entry) =>
        entry.LauncherFileName ?? PlainStartMenuFileName(entry);

    private static string PlainStartMenuFileName(InstallEntry entry) => DisplayName(entry) + ".lnk";

    /// <summary>
    /// The name to record for <paramref name="entry"/>'s Start Menu shortcut: null (the plain
    /// name) unless another install in the same scope, one that has a shortcut, already holds
    /// it, in which case the entry's short id disambiguates. Pure and platform-independent so
    /// it can be tested anywhere; the caller stores the result on Windows only. Decided once,
    /// at install time, and recorded: the name cannot be recomputed from the siblings later,
    /// because removing the first install would then rename the second's shortcut out from
    /// under it.
    /// </summary>
    internal static string? ChooseStartMenuFileName(InstallEntry entry, IEnumerable<InstallEntry> installs)
    {
        var plain = PlainStartMenuFileName(entry);
        var taken = installs.Any(other =>
            other.Id != entry.Id
            && other.Scope == entry.Scope
            && other.LauncherEntry == true
            && string.Equals(StartMenuFileName(other), plain, StringComparison.OrdinalIgnoreCase));

        return taken ? $"{DisplayName(entry)} ({entry.Id.ToString("N")[..8]}).lnk" : null;
    }

    private static string GetDesktopShortcutPath(InstallEntry entry, AppPaths paths) =>
        Path.Combine(paths.DesktopDirectory, DisplayName(entry) + ".lnk");

    internal static string BuildDesktopFile(InstallEntry entry, string exePath, string iconPath)
    {
        var builder = new StringBuilder();
        builder.Append("[Desktop Entry]\n");
        builder.Append("Type=Application\n");
        builder.Append("Name=").Append(EscapeValue(DisplayName(entry))).Append('\n');
        builder.Append("Comment=Godot Engine editor, managed by godman\n");
        // No %f: no MimeType is declared, and a project.godot passed this way would run
        // the project rather than open it in the editor.
        builder.Append("Exec=").Append(QuoteExecArgument(exePath)).Append('\n');
        builder.Append("Icon=").Append(EscapeValue(iconPath)).Append('\n');
        builder.Append("Terminal=false\n");
        builder.Append("Categories=Development;IDE;\n");
        // No StartupWMClass: every version's entry would claim the same "Godot" class,
        // so the shell would group every running editor under one arbitrary entry.
        // Startup notification lets it tie the window to the entry that launched it.
        builder.Append("StartupNotify=true\n");
        return builder.ToString();
    }

    /// <summary>
    /// Desktop Entry spec, "The Exec key": the argument is double-quoted with ", `, $
    /// and \ backslash-escaped inside; a literal % is doubled so it is not read as a
    /// field code; and the general string-escape rule is applied on top, which is why
    /// a literal backslash ends up as four.
    /// </summary>
    private static string QuoteExecArgument(string argument)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in argument)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }

            quoted.Append(c);
        }

        quoted.Append('"');
        return EscapeValue(quoted.ToString().Replace("%", "%%"));
    }

    private static string EscapeValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    private static void WriteIcon(string iconPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
        using var resource = typeof(LauncherService).Assembly.GetManifestResourceStream(IconResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {IconResourceName} is missing.");
        using var file = File.Create(iconPath);
        resource.CopyTo(file);
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to delete {path}: {ex.Message}");
            return false;
        }
    }
}
