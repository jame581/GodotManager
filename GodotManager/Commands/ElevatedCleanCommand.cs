using GodotManager.Config;
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

    public ElevatedCleanCommand(AppPaths paths)
    {
        _paths = paths;
    }

    public override int Execute(CommandContext context, Settings settings)
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

        CleanCommand.CleanupAll(_paths);
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