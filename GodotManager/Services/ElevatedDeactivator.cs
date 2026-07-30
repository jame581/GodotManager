using GodotManager.Domain;
using GodotManager.Infrastructure;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GodotManager.Services;

internal sealed record ElevatedDeactivatePayloadDto(Guid Id);

/// <summary>
/// Launches the hidden <c>deactivate-elevated</c> mirror under UAC.
/// </summary>
/// <remarks>
/// Deactivating a global-scope install clears GODOT_HOME and strips the shim
/// directory from PATH through EnvironmentVariableTarget.Machine writes, which throw
/// SecurityException ("Requested registry access is not allowed") from an unelevated
/// process. Unlike the activate and remove gaps this closes, the deactivate one was
/// never a TUI-versus-CLI asymmetry: neither front-end elevated, because the
/// elevation pattern originally covered only install, activate and clean.
/// </remarks>
internal static class ElevatedDeactivator
{
    /// <summary>
    /// True when deactivation must be handed to an elevated process.
    /// </summary>
    public static bool IsRequired(InstallScope activeScope) =>
        OperatingSystem.IsWindows()
        && !WindowsElevationHelper.IsElevated()
        && TouchesMachineState(activeScope);

    /// <summary>
    /// The domain rule alone, free of environment probing so it is directly
    /// testable. Only the active entry's scope matters -- deactivation touches
    /// whichever scope's environment the active install occupies.
    /// </summary>
    internal static bool TouchesMachineState(InstallScope activeScope) =>
        activeScope == InstallScope.Global;

    /// <summary>
    /// <paramref name="id"/> pins which install the caller believed was active, so
    /// the elevated child can refuse to act if it changed in between rather than
    /// deactivating something the user never selected.
    /// </summary>
    public static async Task<ElevatedActivationResult> RunAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new ElevatedDeactivatePayloadDto(id));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var args = Environment.GetCommandLineArgs();
        var fileName = Environment.ProcessPath ?? args.First();

        var argumentBuilder = new StringBuilder();
        if (args.Length > 1 && args[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            argumentBuilder.Append(ProcessHelpers.QuoteArg(args[1]));
            argumentBuilder.Append(' ');
        }

        argumentBuilder.Append("deactivate-elevated --payload ");
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
                return ElevatedActivationResult.Failed("Unable to start elevated deactivation process.");
            }

            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0
                ? ElevatedActivationResult.Ok()
                : ElevatedActivationResult.Failed(
                    $"Elevated deactivation failed with exit code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ElevatedActivationResult.Failed(
                "Elevation was canceled or blocked.",
                "If you downloaded this executable, right-click it → Properties → Unblock, "
                    + $"or run: Unblock-File '{fileName}'");
        }
    }
}
