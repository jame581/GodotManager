using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text;
using System.Text.Json;

namespace GodotManager.Commands;

internal sealed record ElevatedActivatePayload(Guid Id, bool CreateDesktopShortcut);

internal sealed class ElevatedActivateCommand : AsyncCommand<ElevatedActivateCommand.Settings>
{
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;

    public ElevatedActivateCommand(RegistryService registry, EnvironmentService environment)
    {
        _registry = registry;
        _environment = environment;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("Elevated activation is only supported on Windows.");
        }

        if (!WindowsElevationHelper.IsElevated())
        {
            return Fail("This command must be run as administrator.");
        }

        ElevatedActivatePayload? payload;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(settings.Payload));
            payload = JsonSerializer.Deserialize<ElevatedActivatePayload>(json);
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
        var install = registry.Installs.FirstOrDefault(x => x.Id == payload.Id);
        if (install is null)
        {
            return Fail($"No install found with id {payload.Id}");
        }

        try
        {
            await _environment.ApplyActiveAsync(install, dryRun: false, payload.CreateDesktopShortcut);
            registry.MarkActive(install.Id);
            await _registry.SaveAsync(registry);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Activated[/] {install.Version} ({install.Edition}, {install.Platform})");
        return 0;
    }

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]Activation failed:[/] {message}");
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