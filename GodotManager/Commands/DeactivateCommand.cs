using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class DeactivateCommand : AsyncCommand
{
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;

    public DeactivateCommand(RegistryService registry, EnvironmentService environment)
    {
        _registry = registry;
        _environment = environment;
    }

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var registry = await _registry.LoadAsync();
        var activeInstall = registry.GetActive();

        if (activeInstall is null)
        {
            AnsiConsole.MarkupLine("[yellow]No active installation to deactivate.[/]");
            return 0;
        }

        await _environment.RemoveActiveAsync(activeInstall);
        registry.ClearActive();
        await _registry.SaveAsync(registry);

        AnsiConsole.MarkupLineInterpolated($"[green]Deactivated[/] {activeInstall.Version} ({activeInstall.Edition}, {activeInstall.Platform})");

        if (OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[grey]Environment variable GODOT_HOME has been removed. Restart your terminal/shell.[/]");
        }

        return 0;
    }
}
