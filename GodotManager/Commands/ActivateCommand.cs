using GodotManager.Domain;
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

    public ActivateCommand(RegistryService registry, EnvironmentService environment)
    {
        _registry = registry;
        _environment = environment;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
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

        if (OperatingSystem.IsWindows() && install.Scope == InstallScope.Global && !WindowsElevationHelper.IsElevated())
        {
            AnsiConsole.MarkupLine("[yellow]Administrator access is required for global activation. A UAC prompt will appear.[/]");
            return await RunElevatedActivateAsync(settings.Id, settings.CreateDesktopShortcut);
        }

        try
        {
            await _environment.ApplyActiveAsync(install, dryRun: false, settings.CreateDesktopShortcut);
        }
        catch (UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine("[red]Activation failed:[/] Access denied while updating environment for this scope.");
            return -1;
        }
        catch (SecurityException)
        {
            AnsiConsole.MarkupLine("[red]Activation failed:[/] This scope requires elevated privileges.");
            return -1;
        }

        registry.MarkActive(install.Id);
        await _registry.SaveAsync(registry);

        AnsiConsole.MarkupLineInterpolated($"[green]Activated[/] {install.Version} ({install.Edition}, {install.Platform})");

        if (OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[grey]Note: Environment variable is set. Restart your terminal/shell to load GODOT_HOME.[/]");
        }

        return 0;
    }

    private static async Task<int> RunElevatedActivateAsync(Guid id, bool createDesktopShortcut)
    {
        var payload = new ElevatedActivatePayload(id, createDesktopShortcut);
        var json = JsonSerializer.Serialize(payload);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var args = Environment.GetCommandLineArgs();
        var fileName = Environment.ProcessPath ?? args.First();

        var argumentBuilder = new StringBuilder();
        if (args.Length > 1 && args[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            argumentBuilder.Append(QuoteArg(args[1]));
            argumentBuilder.Append(' ');
        }

        argumentBuilder.Append("activate-elevated --payload ");
        argumentBuilder.Append(QuoteArg(encoded));

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argumentBuilder.ToString(),
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                AnsiConsole.MarkupLine("[red]Activation failed:[/] Unable to start elevated activation process.");
                return -1;
            }

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Activation failed:[/] Elevated activation failed with exit code {process.ExitCode}.");
                return -1;
            }

            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AnsiConsole.MarkupLine("[red]Activation failed:[/] Elevation was canceled by the user.");
            return -1;
        }
    }

    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg) || arg.Contains(' ') || arg.Contains('"'))
        {
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        return arg;
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

    internal sealed class Settings : CommandSettings
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
