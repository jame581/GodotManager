using GodotManager.Domain;

namespace GodotManager.Tui;

/// <summary>
/// Pure formatting/conversion helpers for InstallDialog's progress bar and
/// completion messaging.
/// </summary>
/// <remarks>
/// Deliberately kept free of any Terminal.Gui type. Terminal.Gui runs a module
/// initializer (<c>ConfigurationManager.Initialize</c>) the moment any type from
/// its assembly is actually touched — including a Dialog subclass's own static
/// members — and that initializer throws under the xunit test host in this
/// environment (a metadata-loading conflict with the test platform's own copy of
/// <c>System.Diagnostics.CodeAnalysis</c> attributes). Living here instead of on
/// <c>InstallDialog</c> itself is what lets this logic be unit tested at all.
/// </remarks>
internal static class InstallProgressPresentation
{
    /// <summary>
    /// Converts a 0..100 progress report — what both DownloadService and
    /// InstallerService's extraction step report, and what InstallCommand's own
    /// Spectre progress task (maxValue: 100) is built around — into the 0..1
    /// fraction Terminal.Gui's ProgressBar.Fraction expects. Left as a plain
    /// division, this used to feed the 0..100 value straight into a 0..1 field,
    /// pinning the bar at full for the entire install and showing "5000%".
    /// </summary>
    internal static float ToFraction(double progressPercent) =>
        (float)(Math.Clamp(progressPercent, 0d, 100d) / 100d);

    /// <summary>
    /// Same 0..100 convention as <see cref="ToFraction"/>: the threshold below must
    /// be 100, not 1, or "Finalizing..." never shows until the very last callback.
    /// </summary>
    internal static string FormatProgressLabel(double progressPercent) =>
        progressPercent < 100.0
            ? $"Installing... {progressPercent:F0}%"
            : "Finalizing...";

    /// <summary>
    /// Shown in the dialog's status label after the user cancels a mid-flight
    /// install (InstallDialog.DoInstallAsync's OperationCanceledException catch).
    /// A cancellation is not a failure, so this is deliberately distinct wording
    /// from anything install-failure related.
    /// </summary>
    internal static string BuildCancelledStatus() => "Install cancelled.";

    internal static string BuildCompletionStatus(bool unverified) =>
        unverified ? "Install complete (unverified checksum)." : "Install complete!";

    /// <summary>
    /// The TUI's own account of what CLI installs already tell the user via
    /// DiagnosticContext.WarnAlways when a download could not be checked against
    /// the checksums published upstream. The TUI owns the whole screen during an
    /// install, so this reaches the user through the dialog's own widgets rather
    /// than a write to AnsiConsole.
    /// </summary>
    /// <param name="reason">
    /// Why verification did not succeed, from InstallerService's onVerified
    /// callback. Only meaningful when <paramref name="unverified"/> is true; the
    /// caller is expected to have already excluded the "nothing to check" case
    /// (no published sums) from that flag, so a message is never shown for it.
    /// </param>
    internal static string BuildCompletionMessage(
        string version, InstallEdition edition, bool unverified, string? reason = null) =>
        unverified
            ? $"Installed Godot {version} ({edition}), but the download could not be verified " +
              "against the checksums published upstream" +
              (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}.")
            : $"Installed Godot {version} ({edition})";
}
