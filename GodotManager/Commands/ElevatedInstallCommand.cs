using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text;
using System.Text.Json;

namespace GodotManager.Commands;

internal sealed class ElevatedInstallCommand : AsyncCommand<ElevatedInstallCommand.Settings>
{
    private readonly InstallerService _installer;

    public ElevatedInstallCommand(InstallerService installer)
    {
        _installer = installer;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("Elevated installs are only supported on Windows.");
        }

        if (!WindowsElevationHelper.IsElevated())
        {
            return Fail("This command must be run as administrator.");
        }

        ElevatedInstallPayload? payload;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(settings.Payload));
            payload = JsonSerializer.Deserialize<ElevatedInstallPayload>(json);
        }
        catch (Exception ex)
        {
            return Fail($"Invalid payload: {ex.Message}");
        }

        if (payload is null)
        {
            return Fail("Invalid payload.");
        }

        var request = new InstallRequest(
            payload.Version,
            payload.Edition,
            payload.Platform,
            payload.Scope,
            DownloadUri: null,
            payload.ArchivePath,
            payload.InstallPath,
            payload.Activate,
            payload.Force,
            DryRun: false);

        try
        {
            var result = await _installer.InstallAsync(request);
            AnsiConsole.MarkupLineInterpolated($"[green]Installed[/] {result.Version} ({result.Edition}, {result.Platform}) to [cyan]{result.Path}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]Install failed:[/] {message}");
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
