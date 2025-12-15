using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class FetchCommand : AsyncCommand
{
    public override Task<int> ExecuteAsync(CommandContext context)
    {
        AnsiConsole.MarkupLine("[yellow]Remote version fetch is not wired yet.[/] Use install with --url for now.");
        return Task.FromResult(0);
    }
}
