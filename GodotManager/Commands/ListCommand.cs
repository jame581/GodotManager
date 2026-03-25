using GodotManager.Infrastructure;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class ListCommand : AsyncCommand<ListCommand.Settings>
{
    private readonly RegistryService _registry;

    public ListCommand(RegistryService registry)
    {
        _registry = registry;
    }

    internal sealed class Settings : GlobalSettings { }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var registry = await _registry.LoadAsync();

        if (registry.Installs.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No installs registered.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Active", "Id", "Version", "Edition", "Platform", "Path", "SHA256", "Added");

        foreach (var install in registry.Installs.OrderByDescending(x => x.AddedAt))
        {
            var active = install.IsActive ? "[green]*[/]" : string.Empty;
            var checksum = install.Checksum is not null ? install.Checksum[..Math.Min(12, install.Checksum.Length)] : "[grey]-[/]";
            table.AddRow(active, install.Id.ToString("N"), install.Version, install.Edition.ToString(), install.Platform.ToString(), install.Path, checksum, install.AddedAt.ToString("u"));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
