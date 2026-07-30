using GodotManager.Infrastructure;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GodotManager.Services;

internal sealed record ElevatedActivatePayloadDto(Guid Id, bool CreateDesktopShortcut);

/// <summary>
/// Outcome of an elevated activation, carried back rather than printed.
/// </summary>
/// <remarks>
/// The TUI runs this on the same code path as the CLI while Terminal.Gui owns the
/// screen, so writing to AnsiConsole from here would paint over it -- the same
/// reason DownloadOutcome carries UnverifiedReason instead of reporting it itself.
/// </remarks>
internal readonly record struct ElevatedActivationResult(bool Succeeded, string? Error, string? Hint)
{
    public static ElevatedActivationResult Ok() => new(true, null, null);

    public static ElevatedActivationResult Failed(string error, string? hint = null) =>
        new(false, error, hint);
}

/// <summary>
/// Launches the hidden <c>activate-elevated</c> mirror under UAC.
/// </summary>
/// <remarks>
/// Shared by ActivateCommand and TuiApp. Activation touches machine-wide state
/// whenever the install being activated -- or the one being deactivated -- is
/// global: clearing GODOT_HOME for a global entry writes an
/// EnvironmentVariableTarget.Machine variable, which throws SecurityException
/// ("Requested registry access is not allowed") from an unelevated process. The TUI
/// previously had no elevation path at all, so once a global install was active,
/// every subsequent activation threw and left it active -- an unrecoverable state
/// from inside the TUI.
/// </remarks>
internal static class ElevatedActivator
{
    /// <summary>
    /// True when activation must be performed by an elevated process.
    /// </summary>
    public static bool IsRequired(Domain.InstallScope targetScope, Domain.InstallScope? currentActiveScope) =>
        OperatingSystem.IsWindows()
        && !WindowsElevationHelper.IsElevated()
        && TouchesMachineState(targetScope, currentActiveScope);

    /// <summary>
    /// The domain rule on its own, free of any environment probing so it can be
    /// tested directly: activation touches machine-wide state when the install being
    /// activated is global, or when the one being deactivated is.
    /// </summary>
    /// <remarks>
    /// The second clause is the one that is easy to omit and expensive to omit.
    /// Deactivating a global entry clears GODOT_HOME through an
    /// EnvironmentVariableTarget.Machine write, so leaving it out does not merely
    /// skip a UAC prompt -- it throws SecurityException before the active pointer
    /// can be moved, which strands the caller on the global install permanently.
    /// </remarks>
    internal static bool TouchesMachineState(
        Domain.InstallScope targetScope, Domain.InstallScope? currentActiveScope) =>
        targetScope == Domain.InstallScope.Global
        || currentActiveScope == Domain.InstallScope.Global;

    public static async Task<ElevatedActivationResult> RunAsync(
        Guid id, bool createDesktopShortcut, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new ElevatedActivatePayloadDto(id, createDesktopShortcut));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var args = Environment.GetCommandLineArgs();
        var fileName = Environment.ProcessPath ?? args.First();

        var argumentBuilder = new StringBuilder();
        if (args.Length > 1 && args[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            argumentBuilder.Append(ProcessHelpers.QuoteArg(args[1]));
            argumentBuilder.Append(' ');
        }

        argumentBuilder.Append("activate-elevated --payload ");
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
                return ElevatedActivationResult.Failed("Unable to start elevated activation process.");
            }

            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0
                ? ElevatedActivationResult.Ok()
                : ElevatedActivationResult.Failed(
                    $"Elevated activation failed with exit code {process.ExitCode}.");
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
