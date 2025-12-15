using System.Threading.Tasks;
using GodotManager.Tui;
using GodotManager.Services;
using GodotManager.Config;
using Spectre.Console.Cli;
using Spectre.Console;

namespace GodotManager.Commands;

internal sealed class TuiCommand : AsyncCommand
{
    private readonly TuiRunner _runner;

    public TuiCommand(RegistryService registry, InstallerService installer, EnvironmentService environment, AppPaths paths)
    {
        _runner = new TuiRunner(registry, installer, environment, paths);
    }

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            return await _runner.RunAsync();
        }
        catch (System.Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks);
            return -1;
        }
    }
}
