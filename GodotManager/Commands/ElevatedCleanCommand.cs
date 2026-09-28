using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text;
using System.Text.Json;

namespace GodotManager.Commands;

internal sealed record ElevatedCleanPayload();

internal sealed class ElevatedCleanCommand : Command<ElevatedCleanCommand.Settings>
{
    private readonly AppPaths _paths;
    private readonly RegistryService _registry;
    private readonly DiagnosticContext? _diagnostics;

    public ElevatedCleanCommand(AppPaths paths, RegistryService registry, DiagnosticContext? diagnostics = null)
    {
        _paths = paths;
        _registry = registry;
        _diagnostics = diagnostics;
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("Elevated clean is only supported on Windows.");
        }

        if (!WindowsElevationHelper.IsElevated())
        {
            return Fail("This command must be run as administrator.");
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(settings.Payload));
            _ = JsonSerializer.Deserialize<ElevatedCleanPayload>(json)
                ?? throw new InvalidOperationException("Invalid payload.");
        }
        catch (Exception ex)
        {
            return Fail($"Invalid payload: {ex.Message}");
        }

        IReadOnlyList<InstallEntry> installs;
        try
        {
            installs = _registry.LoadAsync(cancellationToken).GetAwaiter().GetResult().Installs;
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"could not read the registry before cleaning; desktop shortcuts will be left: {ex.Message}");
            installs = [];
        }

        CleanCommand.CleanupAll(_paths, installs, _diagnostics);
        return 0;
    }

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]Clean failed:[/] {message}");
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