using System;
using System.ComponentModel;
using System.Threading.Tasks;
using GodotManager.Domain;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    private readonly InstallerService _installer;
    private readonly GodotDownloadUrlBuilder _urlBuilder;

    public InstallCommand(InstallerService installer, GodotDownloadUrlBuilder urlBuilder)
    {
        _installer = installer;
        _urlBuilder = urlBuilder;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            Uri? url = null;
            if (!string.IsNullOrWhiteSpace(settings.Url))
            {
                url = new Uri(settings.Url);
            }
            else if (string.IsNullOrWhiteSpace(settings.ArchivePath))
            {
                if (!_urlBuilder.TryBuildUri(settings.Version, settings.Edition, settings.Platform, out url, out var error))
                {
                    return Fail(error ?? "Unable to build download URL.");
                }

                AnsiConsole.MarkupLineInterpolated($"[grey]Auto URL[/]: {url}");
            }

            var request = new InstallRequest(
                settings.Version,
                settings.Edition,
                settings.Platform,
                settings.Scope,
                url,
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
        [CommandOption("-v|--version <VERSION>")]
        public string Version { get; set; } = string.Empty;

        [CommandOption("-e|--edition <EDITION>")]
        public InstallEdition Edition { get; set; } = InstallEdition.Standard;

        [CommandOption("-p|--platform <PLATFORM>")]
        public InstallPlatform Platform { get; set; } = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;

        [CommandOption("-s|--scope <SCOPE>")]
        [Description("Install scope: User or Global. Global requires administrator privileges.")]
        public InstallScope Scope { get; set; } = InstallScope.User;

        [CommandOption("-u|--url <URL>")]
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

            if (!string.IsNullOrWhiteSpace(Url) && !Uri.IsWellFormedUriString(Url, UriKind.Absolute))
            {
                return ValidationResult.Error("--url is not a valid absolute URI.");
            }

            return ValidationResult.Success();
        }
    }
}
