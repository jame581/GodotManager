using GodotManager.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Runtime.InteropServices;

namespace GodotManager.Commands;

internal sealed class VersionCommand : Command<VersionCommand.Settings>
{
    // GlobalSettings, like every other user-facing command, so `godman version -V` parses.
    internal sealed class Settings : GlobalSettings { }

    /// <summary>
    /// Same source Program.cs gives SetApplicationVersion, so the two never disagree.
    /// This assembly, not GetEntryAssembly(): under the test host the entry assembly is
    /// the test runner, which would print the wrong number and still pass.
    /// </summary>
    internal static string GodmanVersion =>
        typeof(VersionCommand).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLineInterpolated($"[green]godman[/] {GodmanVersion}");
        AnsiConsole.MarkupLineInterpolated($"[grey]runtime[/] {RuntimeInformation.FrameworkDescription}");
        AnsiConsole.MarkupLineInterpolated($"[grey]os[/] {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        return 0;
    }
}
