using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GodotManager.Domain;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class ActivateCommand : AsyncCommand<ActivateCommand.Settings>
{
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;

    public ActivateCommand(RegistryService registry, EnvironmentService environment)
    {
        _registry = registry;
        _environment = environment;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var registry = await _registry.LoadAsync();
        var install = registry.Installs.FirstOrDefault(x => x.Id == settings.Id);
        if (install is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No install found with id[/] {settings.Id}");
            return -1;
        }

        if (settings.DryRun)
        {
            return PreviewActivate(install, registry);
        }

        registry.MarkActive(install.Id);
        await _registry.SaveAsync(registry);
        await _environment.ApplyActiveAsync(install, dryRun: false, settings.CreateDesktopShortcut);

        AnsiConsole.MarkupLineInterpolated($"[green]Activated[/] {install.Version} ({install.Edition}, {install.Platform})");
        
        if (OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[grey]Note: Environment variable is set. Restart your terminal/shell to load GODOT_HOME.[/]");
        }
        
        return 0;
    }

    private static int PreviewActivate(InstallEntry install, InstallRegistry registry)
    {
        AnsiConsole.MarkupLine("[yellow bold]DRY RUN - No changes will be made[/]\n");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("Install ID", install.Id.ToString("N"));
        table.AddRow("Version", install.Version);
        table.AddRow("Edition", install.Edition.ToString());
        table.AddRow("Platform", install.Platform.ToString());
        table.AddRow("Scope", install.Scope.ToString());
        table.AddRow("Install Path", install.Path);
        
        var currentActive = registry.GetActive();
        if (currentActive != null)
        {
            table.AddRow("Currently Active", $"{currentActive.Version} ({currentActive.Edition})");
        }
        else
        {
            table.AddRow("Currently Active", "[grey](none)[/]");
        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[grey]Actions that would be performed:[/]");
        AnsiConsole.MarkupLine("[grey]1.[/] Mark install as active in registry");
        AnsiConsole.MarkupLine("[grey]2.[/] Update GODOT_HOME environment variable");
        
        var scope = install.Scope;
        var shimPath = OperatingSystem.IsWindows()
            ? Path.Combine("[scope-dir]", "godot.cmd")
            : Path.Combine("[scope-dir]", "godot");
        
        AnsiConsole.MarkupLineInterpolated($"[grey]3.[/] Write shim at {shimPath}");
        AnsiConsole.MarkupLine("[grey]4.[/] Save registry changes");

        return 0;
    }

    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<id>")]
        public Guid Id { get; set; }

        [CommandOption("--dry-run")]
        [Description("Preview the activation without making any changes.")]
        public bool DryRun { get; set; }
        [CommandOption("--create-desktop-shortcut")]
        [Description("Create a desktop shortcut (Windows only).")]
        public bool CreateDesktopShortcut { get; set; }    }
}
