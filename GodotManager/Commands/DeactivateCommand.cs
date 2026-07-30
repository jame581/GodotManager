using GodotManager.Infrastructure;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class DeactivateCommand : AsyncCommand<DeactivateCommand.Settings>
{
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;

    public DeactivateCommand(RegistryService registry, EnvironmentService environment)
    {
        _registry = registry;
        _environment = environment;
    }

    internal sealed class Settings : GlobalSettings { }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var registry = await _registry.LoadAsync();
            var activeInstall = registry.GetActive();

            if (activeInstall is null)
            {
                AnsiConsole.MarkupLine("[yellow]No active installation to deactivate.[/]");
                return 0;
            }

            // Deactivating a global install clears GODOT_HOME and strips PATH through
            // machine-scope writes, which throw SecurityException unelevated. Hand the
            // whole operation over rather than failing on the first write, matching
            // install, activate and remove.
            if (ElevatedDeactivator.IsRequired(activeInstall.Scope))
            {
                AnsiConsole.MarkupLine("[yellow]Administrator access is required. A UAC prompt will appear.[/]");

                var elevated = await ElevatedDeactivator.RunAsync(activeInstall.Id);
                if (elevated.Succeeded)
                {
                    return 0;
                }

                AnsiConsole.MarkupLineInterpolated($"[red]Deactivate failed:[/] {elevated.Error}");
                if (elevated.Hint is { } elevationHint)
                {
                    AnsiConsole.MarkupLineInterpolated($"[grey]Tip: {elevationHint}[/]");
                }

                return -1;
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
        catch (GodmanException ex)
        {
            return GodmanExceptionRenderer.Render("Deactivate failed:", ex);
        }
        catch (Exception ex)
        {
            return GodmanExceptionRenderer.Render("Deactivate failed:", ex.Message);
        }
    }
}
