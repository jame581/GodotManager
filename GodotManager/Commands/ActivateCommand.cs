using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;

namespace GodotManager.Commands;

internal sealed class ActivateCommand : AsyncCommand<ActivateCommand.Settings>
{
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;
    private readonly AppPaths _paths;

    public ActivateCommand(RegistryService registry, EnvironmentService environment, AppPaths paths)
    {
        _registry = registry;
        _environment = environment;
        _paths = paths;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var registry = await _registry.LoadAsync();
        var install = registry.Installs.FirstOrDefault(x => x.Id == settings.Id);
        if (install is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No install found with id[/] {settings.Id}");
            return -1;
        }

        if (settings.DryRun)
        {
            return PreviewActivate(install, registry);
        }

        var currentActive = registry.GetActive();

        // Elevation is needed when activating a global install OR switching away from
        // one, because cleaning up a global shim/PATH entry writes machine-wide state.
        // The rule lives in ElevatedActivator so the TUI applies exactly the same one.
        if (ElevatedActivator.IsRequired(install.Scope, currentActive?.Scope))
        {
            AnsiConsole.MarkupLine("[yellow]Administrator access is required. A UAC prompt will appear.[/]");
            return await RunElevatedActivateAsync(settings.Id, settings.CreateDesktopShortcut);
        }

        // Clean up previous activation to avoid stale shims/PATH entries
        if (currentActive != null && currentActive.Id != install.Id)
        {
            try
            {
                await _environment.RemoveActiveAsync(currentActive);
            }
            catch (Exception ex)
            {
                DiagnosticContext.WarnAlways($"Failed to clean up previous activation ({currentActive.Version}): {ex.Message}");
            }
        }

        try
        {
            await _environment.ApplyActiveAsync(install, dryRun: false, settings.CreateDesktopShortcut);
        }
        catch (UnauthorizedAccessException)
        {
            return GodmanExceptionRenderer.Render(
                "Activation failed:",
                "Access denied while updating environment for this scope.",
                GodmanException.ElevationHint);
        }
        catch (SecurityException)
        {
            return GodmanExceptionRenderer.Render(
                "Activation failed:",
                "This scope requires elevated privileges.",
                GodmanException.ElevationHint);
        }

        registry.MarkActive(install.Id);
        await _registry.SaveAsync(registry);

        AnsiConsole.MarkupLineInterpolated($"[green]Activated[/] {install.Version} ({install.Edition}, {install.Platform})");

        if (OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[grey]Note: Environment variable is set. Restart your terminal/shell to load GODOT_HOME.[/]");
            WarnIfShadowedByGlobalShim(_paths, install.Scope);
        }

        return 0;
    }

    /// <summary>
    /// Reports a leftover machine-wide shim that will outrank this activation.
    /// Rendered here rather than in EnvironmentService because the TUI runs the same
    /// service in-process under Terminal.Gui, where an unconditional AnsiConsole
    /// write would paint over a screen it does not own.
    /// </summary>
    internal static void WarnIfShadowedByGlobalShim(AppPaths paths, InstallScope activatedScope)
    {
        // Best-effort throughout: this runs *after* MarkActive and SaveAsync have
        // committed, and reading the machine PATH can throw SecurityException on a
        // locked-down host. Letting that escape would report a failure for an
        // activation that already succeeded and was persisted.
        try
        {
            var globalShim = Path.Combine(paths.GetShimDirectory(InstallScope.Global), "godot.cmd");

            if (ShimShadowing.WouldShadow(
                    activatedScope,
                    File.Exists(globalShim),
                    paths.GetShimDirectory(InstallScope.Global),
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)))
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]{ShimShadowing.BuildWarning(globalShim)}[/]");
            }
        }
        catch (Exception ex)
        {
            DiagnosticContext.WarnAlways($"Could not check for a shadowing global shim: {ex.Message}");
        }
    }

    private static async Task<int> RunElevatedActivateAsync(Guid id, bool createDesktopShortcut)
    {
        // The launch itself lives in ElevatedActivator so TuiApp can reuse it; this
        // is only the CLI's rendering of the result.
        var result = await ElevatedActivator.RunAsync(id, createDesktopShortcut);
        if (result.Succeeded)
        {
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated($"[red]Activation failed:[/] {result.Error}");
        if (result.Hint is { } hint)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]Tip: {hint}[/]");
        }

        return -1;
    }

    private static int PreviewActivate(InstallEntry install, InstallRegistry registry)
    {
        AnsiConsole.MarkupLine("[yellow bold]DRY RUN - No changes will be made[/]\n");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("Install ID", install.Id.ToString("N"));
        table.AddRow("Version", install.Version);
        table.AddRow("Edition", install.Edition.ToString());
        table.AddRow("Platform", install.Platform.ToString());
        table.AddRow("Scope", install.Scope.ToString());
        table.AddRow("Install Path", install.Path);

        var currentActive = registry.GetActive();
        if (currentActive != null)
        {
            table.AddRow("Currently Active", $"{currentActive.Version} ({currentActive.Edition})");
        }
        else
        {
            table.AddRow("Currently Active", "[grey](none)[/]");
        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[grey]Actions that would be performed:[/]");
        AnsiConsole.MarkupLine("[grey]1.[/] Mark install as active in registry");
        AnsiConsole.MarkupLine("[grey]2.[/] Update GODOT_HOME environment variable");

        var scope = install.Scope;
        var shimPath = OperatingSystem.IsWindows()
            ? Path.Combine("[scope-dir]", "godot.cmd")
            : Path.Combine("[scope-dir]", "godot");

        AnsiConsole.MarkupLineInterpolated($"[grey]3.[/] Write shim at {shimPath}");
        AnsiConsole.MarkupLine("[grey]4.[/] Save registry changes");

        return 0;
    }

    internal sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<id>")]
        public Guid Id { get; set; }

        [CommandOption("--dry-run")]
        [Description("Preview the activation without making any changes.")]
        public bool DryRun { get; set; }
        [CommandOption("--create-desktop-shortcut")]
        [Description("Create a desktop shortcut (Windows only).")]
        public bool CreateDesktopShortcut { get; set; }
    }
}
