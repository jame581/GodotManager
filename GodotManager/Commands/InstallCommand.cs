using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace GodotManager.Commands;

internal sealed class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    private readonly InstallerService _installer;
    private readonly GodotDownloadUrlBuilder _urlBuilder;
    private readonly AppPaths _paths;
    private readonly DiagnosticContext _diagnostics;

    public InstallCommand(InstallerService installer, GodotDownloadUrlBuilder urlBuilder, AppPaths paths, DiagnosticContext diagnostics)
    {
        _installer = installer;
        _urlBuilder = urlBuilder;
        _paths = paths;
        _diagnostics = diagnostics;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
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

            // Verification is only possible when we built the URL ourselves and so
            // know which upstream release it belongs to.
            var checksums = (string.IsNullOrWhiteSpace(settings.Url) && string.IsNullOrWhiteSpace(settings.ArchivePath))
                ? new ChecksumSource(settings.Version)
                : null;

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
                settings.DryRun,
                checksums,
                CreateLauncherEntry: !settings.NoShortcut);

            // Activation elevation is decided the same way `activate` decides it, before anything
            // is written: a user-scope install over an active global one must clear machine-wide
            // state, which an unelevated process cannot do (CLAUDE.md, "Decide elevation before
            // the first machine-wide write"). The split is Windows-only (IsRequired is false
            // elsewhere). On Linux a global-scope install stops up front in LinuxElevation.Check
            // when the global locations are not writable; the activation runs in-process, and
            // switching away from an active global install is not stopped (only the target
            // counts, see LinuxElevation.MustStop): when unprivileged, RemoveUnix cannot delete
            // the global shim, so it is reported after the install -- WarnIfShadowedByGlobalShim.
            if (!settings.DryRun && LinuxElevation.Check(_paths, request.Scope, context.Arguments) is { } denied)
            {
                throw denied;
            }

            var activation = await _installer.PlanActivationAsync(request, cancellationToken);
            request = activation.Request;

            if (settings.DryRun)
            {
                return await PreviewInstallAsync(request, url);
            }

            if (OperatingSystem.IsWindows() && settings.Scope == InstallScope.Global && !WindowsElevationHelper.IsElevated())
            {
                AnsiConsole.MarkupLine("[yellow]Administrator access is required for global installs. A UAC prompt will appear.[/]");
            }

            var verificationStatus = ChecksumStatus.NotApplicable;
            string? verificationReason = null;

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

                    return await _installer.InstallWithElevationAsync(
                        request,
                        progress,
                        onVerified: (status, reason) =>
                        {
                            verificationStatus = status;
                            verificationReason = reason;
                        });
                });
            AnsiConsole.MarkupLineInterpolated($"[green]Installed[/] {result.Version} ({result.Edition}, {result.Platform}) to [cyan]{result.Path}[/]");

            // A failed elevated activation still falls through to the checksum warning
            // below: the install itself happened, and whether its archive was verified is
            // no less true for the activation having failed.
            var activationFailed = false;
            if (activation.ActivateSeparately)
            {
                AnsiConsole.MarkupLine("[yellow]Administrator access is required to switch away from the active global install. A UAC prompt will appear.[/]");
                var elevated = await _installer.CompleteActivationAsync(activation, result);
                if (elevated is { Succeeded: false } failed)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]Activation failed:[/] {failed.Error}");
                    if (failed.Hint is { } hint)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[grey]Tip: {hint}[/]");
                    }

                    activationFailed = true;
                }
            }
            else if (activation.ActivatedInProcess)
            {
                ActivateCommand.WarnIfShadowedByGlobalShim(_paths, request.Scope, _diagnostics);
            }

            // Only when verification was actually attempted and did not succeed.
            // NotApplicable covers both "no published sums to check against" (--url
            // or --archive, which never carry a ChecksumSource) and "this release
            // publishes none" (upstream 404, the ordinary case for many releases) --
            // neither is an error, so neither should print a warning on every such
            // install and train the user to ignore it. The reason names what
            // actually went wrong instead of sending the user on a second ~70 MB
            // download via --verbose just to find out. Unconditional writes belong
            // here, in the command layer, not in a service the TUI also runs
            // in-process.
            if (verificationStatus == ChecksumStatus.Unverified)
            {
                DiagnosticContext.WarnAlways(
                    "this download could not be verified against the checksums published " +
                    $"upstream: {verificationReason}");
            }

            return activationFailed ? -1 : 0;
        }
        catch (GodmanException ex)
        {
            return GodmanExceptionRenderer.Render("Install failed:", ex.WithArguments(context.Arguments));
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
        var step = 1;
        AnsiConsole.MarkupLine($"[grey]{step++}.[/] Download/copy archive");
        AnsiConsole.MarkupLine($"[grey]{step++}.[/] Extract to install directory");
        AnsiConsole.MarkupLine($"[grey]{step++}.[/] Register in installs.json");

        if (request.CreateLauncherEntry)
        {
            AnsiConsole.MarkupLine($"[grey]{step++}.[/] Add application launcher entry");
        }

        if (request.Activate)
        {
            AnsiConsole.MarkupLine($"[grey]{step++}.[/] Set as active install");
            AnsiConsole.MarkupLine($"[grey]{step++}.[/] Update GODOT_HOME environment variable");
            AnsiConsole.MarkupLine($"[grey]{step++}.[/] Write shim script");
        }

        await Task.CompletedTask;
        return 0;
    }

    private static int Fail(string message)
    {
        return GodmanExceptionRenderer.Render("Install failed:", message);
    }

    internal sealed class Settings : GlobalSettings
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

        [CommandOption("--no-shortcut")]
        [Description("Do not add an application-launcher entry (Start Menu on Windows, app menu on Linux).")]
        public bool NoShortcut { get; set; }

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
