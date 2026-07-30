using GodotManager.Infrastructure;

namespace GodotManager.Tui;

/// <summary>
/// Pure error-message formatting for TuiApp's activate/deactivate/remove failure
/// dialogs.
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
}
