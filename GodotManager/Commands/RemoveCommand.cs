using GodotManager.Domain;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace GodotManager.Commands;

internal sealed class RemoveCommand : AsyncCommand<RemoveCommand.Settings>
{
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;

    public RemoveCommand(RegistryService registry, EnvironmentService environment)
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
            return PreviewRemove(install, registry, settings);
        }

        registry.Installs.Remove(install);

        // Deactivate if this is the active installation
        if (registry.ActiveId == install.Id)
        {
            await _environment.RemoveActiveAsync(install);
            registry.ActiveId = null;
        }

        if (settings.DeleteFiles && Directory.Exists(install.Path))
        {
            try
            {
                Directory.Delete(install.Path, recursive: true);
                AnsiConsole.MarkupLineInterpolated($"[grey]Deleted files at[/] {install.Path}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to delete files:[/] {ex.Message}");
            }
        }

        await _registry.SaveAsync(registry);
        AnsiConsole.MarkupLineInterpolated($"[green]Removed[/] {install.Version} ({install.Edition}, {install.Platform})");
        return 0;
    }

    private static int PreviewRemove(InstallEntry install, InstallRegistry registry, Settings settings)
    {
        AnsiConsole.MarkupLine("[yellow bold]DRY RUN - No changes will be made[/]\n");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("Id", install.Id.ToString());
        table.AddRow("Version", install.Version);
        table.AddRow("Edition", install.Edition.ToString());
        table.AddRow("Platform", install.Platform.ToString());
        table.AddRow("Scope", install.Scope.ToString());
        table.AddRow("Path", install.Path);
        table.AddRow("Delete Files", settings.DeleteFiles ? "Yes" : "No");

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[grey]Actions that would be performed:[/]");
        AnsiConsole.MarkupLine("[grey]1.[/] Unregister from installs.json");

        var step = 2;
        if (registry.ActiveId == install.Id)
        {
            AnsiConsole.MarkupLine($"[grey]{step}.[/] Deactivate (clear GODOT_HOME, remove shims)");
            step++;
        }

        if (settings.DeleteFiles)
        {
            AnsiConsole.MarkupLine($"[grey]{step}.[/] Delete files at {Markup.Escape(install.Path)}");
        }

        return 0;
    }

    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<id>")]
        public Guid Id { get; set; }

        [CommandOption("--delete")]
        public bool DeleteFiles { get; set; }

        [CommandOption("--dry-run")]
        [Description("Preview the removal without making any changes.")]
        public bool DryRun { get; set; }
    }
}
