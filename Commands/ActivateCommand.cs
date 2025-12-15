using System;
using System.Linq;
using System.Threading.Tasks;
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

        registry.MarkActive(install.Id);
        await _registry.SaveAsync(registry);
        await _environment.ApplyActiveAsync(install);

        AnsiConsole.MarkupLineInterpolated($"[green]Activated[/] {install.Version} ({install.Edition}, {install.Platform})");
        return 0;
    }

    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<id>")]
        public Guid Id { get; set; }
    }
}
