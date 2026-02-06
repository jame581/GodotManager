using System;
using System.IO;
using System.ComponentModel;
using GodotManager.Config;
using GodotManager.Domain;
using Spectre.Console;
using Spectre.Console.Cli;

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

        CleanupDirectory(_paths.ConfigDirectory, "config");
        CleanupDirectory(_paths.GetInstallRoot(InstallScope.User), "user installs");
        CleanupDirectory(_paths.GetShimDirectory(InstallScope.User), "user shims");
        CleanupDirectory(_paths.GetInstallRoot(InstallScope.Global), "global installs");
        CleanupDirectory(_paths.GetShimDirectory(InstallScope.Global), "global shims");

        return 0;
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
