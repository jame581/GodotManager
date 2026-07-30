using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text;
using System.Text.Json;

namespace GodotManager.Commands;

/// <summary>
/// Hidden mirror of <see cref="DeactivateCommand"/> that runs as administrator, so a
/// global-scope install can be deactivated without relaunching from an elevated
/// shell. Launched by <see cref="ElevatedDeactivator"/>.
/// </summary>
internal sealed class ElevatedDeactivateCommand : AsyncCommand<ElevatedDeactivateCommand.Settings>
{
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;

    public ElevatedDeactivateCommand(RegistryService registry, EnvironmentService environment)
    {
        _registry = registry;
        _environment = environment;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("Elevated deactivation is only supported on Windows.");
        }

        if (!WindowsElevationHelper.IsElevated())
        {
            return Fail("This command must be run as administrator.");
        }

        ElevatedDeactivatePayloadDto? payload;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(settings.Payload));
            payload = JsonSerializer.Deserialize<ElevatedDeactivatePayloadDto>(json);
        }
        catch (Exception ex)
        {
            return Fail($"Invalid payload: {ex.Message}");
        }

        if (payload is null)
        {
            return Fail("Invalid payload.");
        }

        var registry = await _registry.LoadAsync();
        var active = registry.GetActive();

        if (active is null)
        {
            // Already deactivated between the parent's read and this process
            // starting -- the caller's goal is satisfied, so this is not a failure.
            AnsiConsole.MarkupLine("[yellow]No active installation to deactivate.[/]");
            return 0;
        }

        if (active.Id != payload.Id)
        {
            // Refuse rather than deactivate something the user never selected: the
            // parent decided elevation was needed based on the entry it saw active.
            return Fail("The active install changed since deactivation was requested; re-run it.");
        }

        try
        {
            await _environment.RemoveActiveAsync(active);
            registry.ClearActive();
            await _registry.SaveAsync(registry);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Deactivated[/] {active.Version} ({active.Edition}, {active.Platform})");
        return 0;
    }

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]Deactivate failed:[/] {message}");
        return -1;
    }

    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--payload <DATA>")]
        public string Payload { get; set; } = string.Empty;

        public override ValidationResult Validate()
        {
            return string.IsNullOrWhiteSpace(Payload)
                ? ValidationResult.Error("--payload is required.")
                : ValidationResult.Success();
        }
    }
}
