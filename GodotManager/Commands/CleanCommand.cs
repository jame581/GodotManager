using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GodotManager.Commands;

internal sealed class CleanCommand : Command<CleanCommand.Settings>
{
    private readonly AppPaths _paths;
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;
    private readonly DiagnosticContext? _diagnostics;

    public CleanCommand(AppPaths paths, RegistryService registry, EnvironmentService environment, DiagnosticContext? diagnostics = null)
    {
        _paths = paths;
        _registry = registry;
        _environment = environment;
        _diagnostics = diagnostics;
    }

    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--yes")]
        [Description("Skip confirmation prompt.")]
        public bool Yes { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Linux: stop before anything -- user-scope paths included -- is deleted when there
        // are global targets this user cannot remove, rather than cleaning half and printing
        // a "Failed to remove" line per global item. Before the prompt, so the user is not
        // asked to confirm an operation that is about to be refused.
        if (LinuxElevation.CheckClean(_paths, _environment.Launcher, context.Arguments) is { } denied)
        {
            return GodmanExceptionRenderer.Render("Clean failed:", denied);
        }

        // Launcher entries do not follow GODMAN_HOME / GODMAN_GLOBAL_ROOT (see AppPaths), so
        // even a sandboxed run removes the real ones -- say so before anything is deleted.
        var confirm = settings.Yes || AnsiConsole.Confirm(
            "This will remove godman installs, shims, config, and the launcher entries in your real application menu " +
            "(those do not follow GODMAN_HOME or GODMAN_GLOBAL_ROOT). Continue?", false);
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
            return 0;
        }

        // Read before anything is deleted: the registry is the only record of which desktop
        // shortcuts on Windows are ours.
        var installs = LoadInstallsBestEffort(_registry, _diagnostics, cancellationToken);

        if (OperatingSystem.IsWindows() && !WindowsElevationHelper.IsElevated() && HasGlobalCleanupTargets(_paths))
        {
            AnsiConsole.MarkupLine("[yellow]Administrator access is required to clean global installs/shims. A UAC prompt will appear.[/]");
            return RunElevatedCleanup();
        }

        CleanupAll(_paths, _environment.Launcher, installs, _diagnostics);
        return 0;
    }

    /// <summary>
    /// The registry's installs, or none when it cannot be read. Best-effort -- clean is the
    /// recovery tool, and it must still run on a machine whose installs.json is corrupt. The
    /// list only matters for Windows desktop shortcuts; everything else clean removes is
    /// found by location. Shared with the elevated mirror so the two cannot drift.
    /// </summary>
    internal static IReadOnlyList<InstallEntry> LoadInstallsBestEffort(
        RegistryService registry, DiagnosticContext? diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            return registry.LoadAsync(cancellationToken).GetAwaiter().GetResult().Installs;
        }
        catch (Exception ex)
        {
            diagnostics?.Warn($"could not read the registry before cleaning; desktop shortcuts will be left: {ex.Message}");
            return [];
        }
    }

    internal static void CleanupAll(
        AppPaths paths, LauncherService launcher, IReadOnlyList<InstallEntry>? installs = null, DiagnosticContext? diagnostics = null)
    {
        foreach (var scope in new[] { InstallScope.User, InstallScope.Global })
        {
            foreach (var removed in launcher.DeleteAll(scope, installs ?? []))
            {
                AnsiConsole.MarkupLineInterpolated($"[green]Removed[/] launcher entry: {removed}");
            }

            // DeleteAll only returns what it did delete; without this an unprivileged
            // clean was silent about the global entries it could not, unlike every
            // neighbouring global item below.
            foreach (var left in launcher.FindRemaining(scope))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Failed to remove[/] launcher entry: {left}");
            }
        }

        CleanupDirectory(paths.ConfigDirectory, "config");
        CleanupDirectory(paths.GetInstallRoot(InstallScope.User), "user installs");
        CleanupShimDirectory(paths.GetShimDirectory(InstallScope.User), "user shims");

        // On Linux GlobalRegistryFile lives inside GetInstallRoot(Global) itself, so the
        // directory delete below already sweeps it up -- reporting it here too would
        // just print two "Removed" lines for one recursive delete. On Windows it sits
        // one level up, beside installs\ and bin\, where the directory delete below
        // never reaches it, so it has to be removed explicitly or `clean` leaves a
        // stale installs.json behind in %ProgramFiles%\godman.
        var globalInstallRoot = paths.GetInstallRoot(InstallScope.Global);
        var globalRegistryIsInsideInstallRoot = string.Equals(
            Path.GetDirectoryName(paths.GlobalRegistryFile),
            globalInstallRoot,
            StringComparison.OrdinalIgnoreCase);

        if (!globalRegistryIsInsideInstallRoot)
        {
            CleanupFile(paths.GlobalRegistryFile, "global registry");
        }

        CleanupDirectory(globalInstallRoot, "global installs");

        // A root the migration has not moved yet still holds global installs -- `list` reads
        // them from there -- so it goes too. Only existing directories are returned, so this
        // prints nothing on a migrated machine.
        foreach (var legacyRoot in paths.GetLegacyGlobalInstallRoots())
        {
            CleanupDirectory(legacyRoot, "unmigrated global installs");
        }

        CleanupShimDirectory(paths.GetShimDirectory(InstallScope.Global), "global shims");
    }

    internal static bool HasGlobalCleanupTargets(AppPaths paths)
    {
        // An unmigrated legacy root counts: CleanupAll deletes it, and on Windows it is
        // the only thing on the machine that says a UAC prompt is needed -- AppPaths never
        // creates the new root while a move into it is pending, so nothing else exists.
        return Directory.Exists(paths.GetInstallRoot(InstallScope.Global))
            || Directory.Exists(paths.GetShimDirectory(InstallScope.Global))
            || File.Exists(paths.GlobalRegistryFile)
            || Directory.Exists(paths.GetLauncherDirectory(InstallScope.Global))
            || paths.GetLegacyGlobalInstallRoots().Count > 0;
    }

    private static int RunElevatedCleanup()
    {
        var payload = new ElevatedCleanPayload();
        var json = JsonSerializer.Serialize(payload);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var args = Environment.GetCommandLineArgs();
        var fileName = Environment.ProcessPath ?? args.First();

        var argumentBuilder = new StringBuilder();
        if (args.Length > 1 && args[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            argumentBuilder.Append(ProcessHelpers.QuoteArg(args[1]));
            argumentBuilder.Append(' ');
        }

        argumentBuilder.Append("clean-elevated --payload ");
        argumentBuilder.Append(ProcessHelpers.QuoteArg(encoded));

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argumentBuilder.ToString(),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory
        };

        try
        {
            // Remove Mark of the Web so SmartScreen won't silently block runas
            WindowsElevationHelper.TryRemoveZoneIdentifier(fileName);

            using var process = Process.Start(psi);
            if (process == null)
            {
                AnsiConsole.MarkupLine("[red]Clean failed:[/] Unable to start elevated clean process.");
                return -1;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Clean failed:[/] Elevated clean failed with exit code {process.ExitCode}.");
                return -1;
            }

            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AnsiConsole.MarkupLine("[red]Clean failed:[/] Elevation was canceled or blocked.");
            AnsiConsole.MarkupLine("[grey]Tip: If you downloaded this executable, right-click it → Properties → Unblock, or run:[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]  Unblock-File '{fileName}'[/]");
            return -1;
        }
    }

    private static void CleanupShimDirectory(string shimDir, string label)
    {
        if (!Directory.Exists(shimDir))
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]Skipped[/] {label}: {shimDir} (missing)");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // On Windows the shim directory is godman-owned (e.g. %APPDATA%\godman\bin),
            // safe to remove entirely.
            CleanupDirectory(shimDir, label);
            return;
        }

        // On Linux the shim directory may be shared (e.g. ~/.local/bin or /usr/local/bin).
        // Only remove our shim file, not the entire directory.
        var shimFile = Path.Combine(shimDir, "godot");
        if (File.Exists(shimFile))
        {
            try
            {
                File.Delete(shimFile);
                AnsiConsole.MarkupLineInterpolated($"[green]Removed[/] {label}: {shimFile}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Failed to remove[/] {label} shim at {shimFile}: {ex.Message}");
            }
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]Skipped[/] {label}: no shim found in {shimDir}");
        }
    }

    private static void CleanupFile(string path, string label)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                AnsiConsole.MarkupLineInterpolated($"[green]Removed[/] {label}: {path}");
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated($"[grey]Skipped[/] {label}: {path} (missing)");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to remove[/] {label} at {path}: {ex.Message}");
        }
    }

    private static void CleanupDirectory(string path, string label)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                AnsiConsole.MarkupLineInterpolated($"[green]Removed[/] {label}: {path}");
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated($"[grey]Skipped[/] {label}: {path} (missing)");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to remove[/] {label} at {path}: {ex.Message}");
        }
    }
}
