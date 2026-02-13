using GodotManager.Domain;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

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

                if (!settings.DryRun)
                {
                    AnsiConsole.MarkupLineInterpolated($"[grey]Auto URL[/]: {url}");
                }
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
                settings.Force,
                settings.DryRun);

            if (settings.DryRun)
            {
                return await PreviewInstallAsync(request, url);
            }

            if (OperatingSystem.IsWindows() && settings.Scope == InstallScope.Global && !WindowsElevationHelper.IsElevated())
            {
                AnsiConsole.MarkupLine("[yellow]Administrator access is required for global installs. A UAC prompt will appear.[/]");
            }

            var result = await AnsiConsole.Progress()
                .AutoClear(true)
                .HideCompleted(true)
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Installing", maxValue: 100);
                    var lastReported = 0d;
                    var progress = new Action<double>(pct =>
                    {
                        var clamped = Math.Clamp(pct, 0d, 100d);
                        if (clamped < lastReported)
                        {
                            return;
                        }

                        lastReported = clamped;
                        task.Value = clamped;
                    });

                    return await _installer.InstallWithElevationAsync(request, progress);
                });
            AnsiConsole.MarkupLineInterpolated($"[green]Installed[/] {result.Version} ({result.Edition}, {result.Platform}) to [cyan]{result.Path}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static async Task<int> PreviewInstallAsync(InstallRequest request, Uri? url)
    {
        AnsiConsole.MarkupLine("[yellow bold]DRY RUN - No changes will be made[/]\n");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("Version", request.Version);
        table.AddRow("Edition", request.Edition.ToString());
        table.AddRow("Platform", request.Platform.ToString());
        table.AddRow("Scope", request.Scope.ToString());

        if (url != null)
        {
            table.AddRow("Download URL", url.ToString());
        }
        else if (!string.IsNullOrWhiteSpace(request.ArchivePath))
        {
            table.AddRow("Archive Path", request.ArchivePath);
        }

        var installPath = request.InstallPath ?? "[grey](auto-generated)[/]";
        table.AddRow("Install Path", installPath);
        table.AddRow("Force Overwrite", request.Force ? "Yes" : "No");
        table.AddRow("Activate After", request.Activate ? "Yes" : "No");

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[grey]Actions that would be performed:[/]");
        AnsiConsole.MarkupLine("[grey]1.[/] Download/copy archive");
        AnsiConsole.MarkupLine("[grey]2.[/] Extract to install directory");
        AnsiConsole.MarkupLine("[grey]3.[/] Register in installs.json");

        if (request.Activate)
        {
            AnsiConsole.MarkupLine("[grey]4.[/] Set as active install");
            AnsiConsole.MarkupLine("[grey]5.[/] Update GODOT_HOME environment variable");
            AnsiConsole.MarkupLine("[grey]6.[/] Write shim script");
        }

        await Task.CompletedTask;
        return 0;
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

        [CommandOption("--dry-run")]
        [Description("Preview the installation without making any changes.")]
        public bool DryRun { get; set; }

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
