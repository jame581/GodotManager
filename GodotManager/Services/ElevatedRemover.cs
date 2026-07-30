using GodotManager.Domain;
using GodotManager.Infrastructure;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GodotManager.Services;

internal sealed record ElevatedRemovePayloadDto(Guid Id, bool DeleteFiles);

/// <summary>
/// Launches the hidden <c>remove-elevated</c> mirror under UAC.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ElevatedActivator"/> exactly, for the same reason: removing a
/// global-scope install writes the machine-wide registry under %ProgramFiles% and
/// deletes files there, neither of which an unelevated process can do. Before this
/// existed, both front-ends could only report the failure and tell the user to
/// re-run elevated -- which, from inside the TUI, meant quitting it entirely.
/// </remarks>
internal static class ElevatedRemover
{
    /// <summary>
    /// True when removal must be handed to an elevated process.
    /// </summary>
    public static bool IsRequired(InstallScope scope) =>
        OperatingSystem.IsWindows()
        && !WindowsElevationHelper.IsElevated()
        && TouchesMachineState(scope);

    /// <summary>
    /// The domain rule alone, free of environment probing so it is directly
    /// testable. Only the entry's own scope matters: a user-scope removal never
    /// writes machine-wide state, and deactivating it on the way out targets the
    /// user environment.
    /// </summary>
    internal static bool TouchesMachineState(InstallScope scope) => scope == InstallScope.Global;

    public static async Task<ElevatedOperationResult> RunAsync(
        Guid id, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new ElevatedRemovePayloadDto(id, deleteFiles));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var args = Environment.GetCommandLineArgs();
        var fileName = Environment.ProcessPath ?? args.First();

        var argumentBuilder = new StringBuilder();
        if (args.Length > 1 && args[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            argumentBuilder.Append(ProcessHelpers.QuoteArg(args[1]));
            argumentBuilder.Append(' ');
        }

        argumentBuilder.Append("remove-elevated --payload ");
        argumentBuilder.Append(ProcessHelpers.QuoteArg(encoded));

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argumentBuilder.ToString(),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory
        };

        try
        {
            // Remove Mark of the Web so SmartScreen won't silently block runas
            WindowsElevationHelper.TryRemoveZoneIdentifier(fileName);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return ElevatedOperationResult.Failed("Unable to start elevated removal process.");
            }

            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode switch
            {
                0 => ElevatedOperationResult.Ok(),

                // The entry was removed but its directory could not be deleted. The
                // child's own console is gone by now, so this is the only way the
                // user learns their disk was not actually reclaimed.
                Commands.ElevatedRemoveCommand.FilesSurvivedExitCode =>
                    ElevatedOperationResult.OkWithWarning(
                        "The install was unregistered, but its files could not be deleted "
                            + "and are still on disk."),

                _ => ElevatedOperationResult.Failed(
                    $"Elevated removal failed with exit code {process.ExitCode}.")
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ElevatedOperationResult.Failed(
                "Elevation was canceled or blocked.",
                "If you downloaded this executable, right-click it → Properties → Unblock, "
                    + $"or run: Unblock-File '{fileName}'");
        }
    }
}
