using Spectre.Console;

namespace GodotManager.Infrastructure;

internal sealed class DiagnosticContext
{
    public bool Verbose { get; set; }

    /// <summary>
    /// Emit a warning that is only shown when verbose mode is on.
    /// </summary>
    public void Warn(string message)
    {
        if (Verbose)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]warn:[/] {message}");
        }
    }

    /// <summary>
    /// Emit a warning that is always shown regardless of verbose mode.
    /// Used for moderate-risk operations where silent failure causes user confusion.
    /// </summary>
    public static void WarnAlways(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[yellow]warn:[/] {message}");
    }
}
