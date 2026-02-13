using GodotManager.Config;
using GodotManager.Services;
using GodotManager.Tui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class TuiCommand : AsyncCommand
{
    private readonly TuiRunner _runner;

    public TuiCommand(RegistryService registry, InstallerService installer, EnvironmentService environment, AppPaths paths, GodotDownloadUrlBuilder urlBuilder, GodotVersionFetcher fetcher)
    {
        _runner = new TuiRunner(registry, installer, environment, paths, urlBuilder, fetcher);
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
