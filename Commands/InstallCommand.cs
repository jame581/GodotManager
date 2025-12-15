using System;
using System.Threading.Tasks;
using GodotManager.Domain;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    private readonly InstallerService _installer;

    public InstallCommand(InstallerService installer)
    {
        _installer = installer;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var request = new InstallRequest(
                settings.Version,
                settings.Edition,
                settings.Platform,
                settings.Url is null ? null : new Uri(settings.Url),
                settings.ArchivePath,
                settings.InstallPath,
                settings.Activate,
                settings.Force);

            var progress = new Action<double>(pct => AnsiConsole.MarkupLineInterpolated($"[grey]Progress:[/] {pct:F0}%"));
            var result = await _installer.InstallAsync(request, progress);
            AnsiConsole.MarkupLineInterpolated($"[green]Installed[/] {result.Version} ({result.Edition}, {result.Platform}) to [cyan]{result.Path}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Install failed:[/] {ex.Message}");
            return -1;
        }
    }

    internal sealed class Settings : CommandSettings
    {
        [CommandOption("-v|--version <VERSION>")]
        public string Version { get; set; } = string.Empty;

        [CommandOption("-e|--edition <EDITION>")]
        public InstallEdition Edition { get; set; } = InstallEdition.Standard;

        [CommandOption("-p|--platform <PLATFORM>")]
        public InstallPlatform Platform { get; set; } = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;

        [CommandOption("--url <URL>")]
        public string? Url { get; set; }

        [CommandOption("--archive <PATH>")]
        public string? ArchivePath { get; set; }

        [CommandOption("--path <DIR>")]
        public string? InstallPath { get; set; }

        [CommandOption("--activate")]
        public bool Activate { get; set; }

        [CommandOption("--force")]
        public bool Force { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Version))
            {
                return ValidationResult.Error("Version is required.");
            }

            if (string.IsNullOrWhiteSpace(Url) && string.IsNullOrWhiteSpace(ArchivePath))
            {
                return ValidationResult.Error("Provide --url or --archive.");
            }

            return ValidationResult.Success();
        }
    }
}
