using GodotManager.Config;
using GodotManager.Infrastructure;
using GodotManager.Services;
using GodotManager.Tui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class TuiCommand : AsyncCommand<TuiCommand.Settings>
{
    private readonly TuiRunner _runner;

    public TuiCommand(RegistryService registry, InstallerService installer, EnvironmentService environment, AppPaths paths, GodotDownloadUrlBuilder urlBuilder, GodotVersionFetcher fetcher)
    {
        _runner = new TuiRunner(registry, installer, environment, paths, urlBuilder, fetcher);
    }

    internal sealed class Settings : GlobalSettings { }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
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
