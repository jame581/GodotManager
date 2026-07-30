using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Infrastructure;
using GodotManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Testing;
using System.Net.Http;

namespace GodotManager.Tests.Helpers;

/// <summary>
/// Creates a CommandAppTester wired with the same commands as Program.cs
/// but using test-specific DI (isolated paths, mock HTTP).
/// </summary>
internal static class CliTestHarness
{
    /// <summary>
    /// Build a CommandAppTester with test DI. Uses the fixture's services
    /// and optionally overrides HttpClient with a custom one.
    /// </summary>
    public static CommandAppTester Create(GodmanTestFixture fixture, HttpClient? httpClient = null, DiagnosticContext? diagnostics = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(fixture.Paths);
        services.AddSingleton(fixture.Registry);
        services.AddSingleton(fixture.Environment);
        services.AddSingleton(diagnostics ?? new DiagnosticContext());
        // Same infinite timeout as Program.cs, for consistency: tests that don't
        // pass their own HttpClient still get one shaped like the real one.
        services.AddSingleton(httpClient ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan });
        services.AddSingleton<DownloadService>();
        services.AddSingleton<InstallerService>();
        services.AddSingleton<GodotDownloadUrlBuilder>();
        services.AddSingleton<GodotVersionFetcher>();

        var registrar = new TypeRegistrar(services);
        var app = new CommandAppTester(registrar);

        app.Configure(config =>
        {
            config.SetApplicationName("godman");

            config.AddCommand<ListCommand>("list");
            config.AddCommand<FetchCommand>("fetch");
            config.AddCommand<InstallCommand>("install");
            config.AddCommand<ActivateCommand>("activate");
            config.AddCommand<DeactivateCommand>("deactivate");
            config.AddCommand<RemoveCommand>("remove");
            config.AddCommand<DoctorCommand>("doctor");
            config.AddCommand<CleanCommand>("clean");
        });

        return app;
    }
}
