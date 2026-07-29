using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Infrastructure;
using GodotManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Reflection;
using System.Threading;

var diagnostics = new DiagnosticContext();

var services = new ServiceCollection();
services.AddSingleton(diagnostics);
services.AddSingleton<AppPaths>();
services.AddSingleton<RegistryService>();
services.AddSingleton<EnvironmentService>();
services.AddSingleton<DownloadService>();
services.AddSingleton<InstallerService>();
// HttpClient's default 100s timeout applies to the whole body read under
// ResponseHeadersRead, not just the headers -- a 70+ MB archive would need
// sustained ~700 KB/s just to avoid it timing out mid-download. Now that
// DownloadService resumes partial transfers, there is no reason to time out an
// otherwise-healthy slow connection instead of letting it finish.
services.AddSingleton(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
services.AddSingleton<GodotDownloadUrlBuilder>();
services.AddSingleton<GodotVersionFetcher>();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetInterceptor(new VerboseInterceptor(diagnostics));
    config.SetApplicationName("godman");
    config.SetApplicationVersion(Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

    config.AddExample("list");
    config.AddExample("install", "--version", "4.5.1", "--edition", "Standard", "--activate");
    config.AddExample("activate", "<id>", "--verbose");

    config.AddCommand<ListCommand>("list").WithDescription("List registered installs");
    config.AddCommand<FetchCommand>("fetch").WithDescription("Browse available Godot versions from GitHub");
    config.AddCommand<InstallCommand>("install").WithDescription("Download or import an archive and register it");
    config.AddCommand<ElevatedInstallCommand>("install-elevated")
        .WithDescription("Install with elevated privileges (internal)")
        .IsHidden();
    config.AddCommand<ElevatedActivateCommand>("activate-elevated")
        .WithDescription("Activate with elevated privileges (internal)")
        .IsHidden();
    config.AddCommand<ElevatedCleanCommand>("clean-elevated")
        .WithDescription("Clean with elevated privileges (internal)")
        .IsHidden();
    config.AddCommand<ActivateCommand>("activate").WithDescription("Activate a registered install");
    config.AddCommand<DeactivateCommand>("deactivate").WithDescription("Deactivate the current active install");
    config.AddCommand<RemoveCommand>("remove").WithDescription("Remove a registered install");
    config.AddCommand<DoctorCommand>("doctor").WithDescription("Check registry and environment setup");
    config.AddCommand<TuiCommand>("tui").WithDescription("Launch interactive TUI");
    config.AddCommand<CleanCommand>("clean").WithDescription("Remove godman installs, shims, and config");

});


try
{
    return app.Run(args);
}
catch (CommandRuntimeException ex)
{
    AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
    return -1;
}
catch (GodmanException ex)
{
    return GodmanExceptionRenderer.Render("error:", ex);
}
catch (System.Exception ex)
{
    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks);
    return -1;
}
