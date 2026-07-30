using Spectre.Console;

namespace GodotManager.Infrastructure;

/// <summary>
/// Renders a command failure line, plus a "hint:" follow-up line when the
/// failure carries a remedy. Shared by every command catch block so the
/// markup, colors, and wording stay identical across call sites; only the
/// failure prefix differs per command.
/// </summary>
internal static class GodmanExceptionRenderer
{
    /// <summary>
    /// Renders <paramref name="prefix"/> in red followed by
    /// <paramref name="ex"/>'s message, then a grey "hint:" line if
    /// <paramref name="ex"/> is a <see cref="GodmanException"/> with a
    /// non-empty <see cref="GodmanException.Hint"/>. Returns -1, the exit
    /// code every caller uses for a failed command.
    /// </summary>
    public static int Render(string prefix, Exception ex)
    {
        var hint = ex is GodmanException godmanEx ? godmanEx.Hint : null;
        return Render(prefix, ex.Message, hint);
    }

    /// <summary>
    /// Renders <paramref name="prefix"/> in red followed by
    /// <paramref name="message"/>, then a grey "hint:" line when
    /// <paramref name="hint"/> is non-empty. Returns -1, the exit code
    /// every caller uses for a failed command.
    /// </summary>
    public static int Render(string prefix, string message, string? hint = null)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]{prefix}[/] {message}");
        if (!string.IsNullOrEmpty(hint))
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]hint:[/] {hint}");
        }

        return -1;
    }
}
