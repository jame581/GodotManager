using GodotManager.Domain;
using GodotManager.Infrastructure;

namespace GodotManager.Tui;

/// <summary>
/// Pure message formatting for TuiApp's activate/deactivate/remove dialogs --
/// both the failure bodies and the remove confirmation.
/// </summary>
/// <remarks>
/// Deliberately kept free of any Terminal.Gui type -- see the identical remark on
/// <see cref="InstallProgressPresentation"/>. TuiApp itself carries Terminal.Gui
/// field types (IApplication, ListView, etc.), so even a static method call
/// against it loads those types and trips Terminal.Gui's module initializer under
/// the xunit test host. Living here instead is what lets this logic be tested.
/// </remarks>
internal static class TuiErrorPresentation
{
    /// <summary>
    /// A failed global-registry write (see RegistryService.SaveAsync) reaches
    /// TuiApp's activate/deactivate/remove handlers as a GodmanException whose
    /// actionable remedy (e.g. "re-run with sudo") rides entirely on Hint, same as
    /// InstallDialog's install failures. Showing only ex.Message would silently
    /// drop it, leaving a TUI user with a bare failure and no next step.
    /// </summary>
    internal static string BuildErrorBody(string prefix, Exception ex) =>
        ex is GodmanException { Hint: { } hint }
            ? $"{prefix}: {ex.Message}\n{hint}"
            : $"{prefix}: {ex.Message}";

    /// <summary>
    /// Body for the "Removed" confirmation, naming any install directory that
    /// survived the removal.
    /// </summary>
    /// <remarks>
    /// Deleting the files is best-effort, matching RemoveCommand's own try/catch
    /// around Directory.Delete. It has to be: for a global-scope entry the delete
    /// fails first under an unelevated process, and the registry write that follows
    /// is what raises the GodmanException carrying the "re-run elevated" hint. A
    /// delete that throws instead of reporting takes that hint down with it, so the
    /// failure the user is shown no longer says what to do about it.
    /// </remarks>
    internal static string BuildRemovedBody(
        string version, InstallEdition edition, string? path, string? deleteFailure) =>
        deleteFailure is null
            ? $"Removed {version} ({edition})"
            : $"Removed {version} ({edition})\nFiles at {path} could not be deleted: {deleteFailure}";
}
