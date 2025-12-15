using System.Net.Http;
using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Infrastructure;
using GodotManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

var services = new ServiceCollection();
services.AddSingleton<AppPaths>();
services.AddSingleton<RegistryService>();
services.AddSingleton<EnvironmentService>();
services.AddSingleton<InstallerService>();
services.AddSingleton<HttpClient>();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
	config.SetApplicationName("godot-manager");
	config.AddCommand<ListCommand>("list").WithDescription("List registered installs");
	config.AddCommand<FetchCommand>("fetch").WithDescription("Show available remote versions (stub)");
	config.AddCommand<InstallCommand>("install").WithDescription("Download or import an archive and register it");
	config.AddCommand<ActivateCommand>("activate").WithDescription("Activate a registered install");
	config.AddCommand<RemoveCommand>("remove").WithDescription("Remove a registered install");
	config.AddCommand<DoctorCommand>("doctor").WithDescription("Check registry and environment setup");
	config.AddCommand<TuiCommand>("tui").WithDescription("Launch interactive TUI");
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
catch (System.Exception ex)
{
	AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks);
	return -1;
}
