using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text;
using System.Text.Json;

namespace GodotManager.Commands;

/// <summary>
/// Hidden mirror of <see cref="RemoveCommand"/> that runs as administrator, so a
/// global-scope install can be removed without the user having to quit and relaunch
/// from an elevated shell. Launched by <see cref="ElevatedRemover"/>.
/// </summary>
internal sealed class ElevatedRemoveCommand : AsyncCommand<ElevatedRemoveCommand.Settings>
{
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;

    public ElevatedRemoveCommand(RegistryService registry, EnvironmentService environment)
    {
        _registry = registry;
        _environment = environment;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("Elevated removal is only supported on Windows.");
        }

        if (!WindowsElevationHelper.IsElevated())
        {
            return Fail("This command must be run as administrator.");
        }

        ElevatedRemovePayloadDto? payload;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(settings.Payload));
            payload = JsonSerializer.Deserialize<ElevatedRemovePayloadDto>(json);
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

        var filesSurvived = false;

        try
        {
            if (registry.ActiveId == install.Id)
            {
                await _environment.RemoveActiveAsync(install);
                registry.ActiveId = null;
            }

            if (payload.DeleteFiles && Directory.Exists(install.Path))
            {
                // Best-effort, matching RemoveCommand: a file that cannot be deleted
                // must not strand the entry in the registry describing an install the
                // user asked to be rid of.
                try
                {
                    Directory.Delete(install.Path, recursive: true);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to delete files:[/] {ex.Message}");
                    filesSurvived = true;
                }
            }

            registry.Installs.RemoveAll(x => x.Id == install.Id);
            await _registry.SaveAsync(registry);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Removed[/] {install.Version} ({install.Edition}, {install.Platform})");

        // This process gets its own console window, which closes the moment it
        // exits, so nothing written above is readable. The exit code is the only
        // channel back to the parent -- and reporting a plain success here would
        // tell the user their files were deleted while a full install remains on
        // disk with no registry entry left pointing at it.
        return filesSurvived ? FilesSurvivedExitCode : 0;
    }

    /// <summary>
    /// The registry entry was removed but its files could not be deleted. Distinct
    /// from both success and failure: the removal itself did happen.
    /// </summary>
    internal const int FilesSurvivedExitCode = 2;

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]Remove failed:[/] {message}");
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
