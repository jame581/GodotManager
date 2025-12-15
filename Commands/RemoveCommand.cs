using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class RemoveCommand : AsyncCommand<RemoveCommand.Settings>
{
    private readonly RegistryService _registry;

    public RemoveCommand(RegistryService registry)
    {
        _registry = registry;
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

        registry.Installs.Remove(install);
        if (registry.ActiveId == install.Id)
        {
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

    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "id")]
        public Guid Id { get; set; }

        [CommandOption("--delete")]
        public bool DeleteFiles { get; set; }
    }
}
