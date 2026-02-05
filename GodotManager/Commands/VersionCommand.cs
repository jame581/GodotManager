using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class VersionCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var infoVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var version = infoVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
        AnsiConsole.MarkupLineInterpolated($"godot-manager {version}");
        return 0;
    }
}
