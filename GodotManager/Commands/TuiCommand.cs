using GodotManager.Config;
using GodotManager.Infrastructure;
using GodotManager.Services;
using GodotManager.Tui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class TuiCommand : AsyncCommand<TuiCommand.Settings>
{
    private readonly TuiApp _app;

    public TuiCommand(RegistryService registry, InstallerService installer, EnvironmentService environment, AppPaths paths, GodotDownloadUrlBuilder urlBuilder, GodotVersionFetcher fetcher)
    {
        _app = new TuiApp(registry, installer, environment, paths, urlBuilder, fetcher);
    }

    internal sealed class Settings : GlobalSettings { }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            return Task.FromResult(_app.Run());
        }
        catch (System.Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks);
            return Task.FromResult(-1);
        }
    }
}
