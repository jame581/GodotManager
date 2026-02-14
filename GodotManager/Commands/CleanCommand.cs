using GodotManager.Config;
using GodotManager.Domain;
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

    public CleanCommand(AppPaths paths)
    {
        _paths = paths;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--yes")]
        [Description("Skip confirmation prompt.")]
        public bool Yes { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var confirm = settings.Yes || AnsiConsole.Confirm("This will remove godman installs, shims, and config. Continue?", false);
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
            return 0;
        }

        if (OperatingSystem.IsWindows() && !WindowsElevationHelper.IsElevated() && HasGlobalCleanupTargets(_paths))
        {
            AnsiConsole.MarkupLine("[yellow]Administrator access is required to clean global installs/shims. A UAC prompt will appear.[/]");
            return RunElevatedCleanup();
        }

        CleanupAll(_paths);
        return 0;
    }

    internal static void CleanupAll(AppPaths paths)
    {
        CleanupDirectory(paths.ConfigDirectory, "config");
        CleanupDirectory(paths.GetInstallRoot(InstallScope.User), "user installs");
        CleanupDirectory(paths.GetShimDirectory(InstallScope.User), "user shims");
        CleanupDirectory(paths.GetInstallRoot(InstallScope.Global), "global installs");
        CleanupDirectory(paths.GetShimDirectory(InstallScope.Global), "global shims");
    }

    private static bool HasGlobalCleanupTargets(AppPaths paths)
    {
        return Directory.Exists(paths.GetInstallRoot(InstallScope.Global))
            || Directory.Exists(paths.GetShimDirectory(InstallScope.Global));
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
            argumentBuilder.Append(QuoteArg(args[1]));
            argumentBuilder.Append(' ');
        }

        argumentBuilder.Append("clean-elevated --payload ");
        argumentBuilder.Append(QuoteArg(encoded));

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

    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg) || arg.Contains(' ') || arg.Contains('"'))
        {
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        return arg;
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
