using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class ListCommand : AsyncCommand
{
    private readonly RegistryService _registry;

    public ListCommand(RegistryService registry)
    {
        _registry = registry;
    }

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var registry = await _registry.LoadAsync();

        if (registry.Installs.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No installs registered.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Active", "Id", "Version", "Edition", "Platform", "Path", "Added");

        foreach (var install in registry.Installs.OrderByDescending(x => x.AddedAt))
        {
            var active = install.IsActive ? "[green]*[/]" : string.Empty;
            table.AddRow(active, install.Id.ToString("N"), install.Version, install.Edition.ToString(), install.Platform.ToString(), install.Path, install.AddedAt.ToString("u"));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
