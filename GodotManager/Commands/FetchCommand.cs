using GodotManager.Infrastructure;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace GodotManager.Commands;

internal sealed class FetchCommand : AsyncCommand<FetchCommand.Settings>
{
    private readonly GodotVersionFetcher _fetcher;

    public FetchCommand(GodotVersionFetcher fetcher)
    {
        _fetcher = fetcher;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var statusMessage = settings.NoCache
                ? "Fetching available versions from GitHub..."
                : "Loading available versions...";

            var releases = await AnsiConsole.Status()
                .StartAsync(statusMessage, async ctx =>
                {
                    return await _fetcher.FetchReleasesAsync(skipCache: settings.NoCache);
                });

            if (releases.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No releases found.[/]");
                return 0;
            }

            var filtered = releases.AsEnumerable();

            if (settings.StableOnly)
            {
                filtered = filtered.Where(r => r.IsStable);
            }

            if (!string.IsNullOrWhiteSpace(settings.VersionFilter))
            {
                filtered = filtered.Where(r => r.Version.Contains(settings.VersionFilter, StringComparison.OrdinalIgnoreCase));
            }

            var finalList = filtered.Take(settings.Limit).ToList();

            if (finalList.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No releases match the specified filters.[/]");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumns("Version", "Type", "Standard", "Mono/.NET", "Published");

            foreach (var release in finalList)
            {
                var type = release.IsStable ? "[green]Stable[/]" : "[yellow]Preview[/]";
                var standard = release.HasStandard ? "[green]?[/]" : "[grey]-[/]";
                var dotnet = release.HasDotNet ? "[green]?[/]" : "[grey]-[/]";
                var published = release.PublishedAt.ToString("yyyy-MM-dd");

                table.AddRow(release.Version, type, standard, dotnet, published);
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLineInterpolated($"\n[grey]Showing {finalList.Count} of {releases.Count} total releases.[/]");

            var cacheAge = _fetcher.GetCacheAge();
            if (cacheAge.HasValue)
            {
                var age = DateTimeOffset.UtcNow - cacheAge.Value;
                var ageText = age.TotalHours < 1 ? $"{age.Minutes}m ago" : $"{age.TotalHours:F0}h ago";
                AnsiConsole.MarkupLineInterpolated($"[grey]Cache updated {ageText}. Use --no-cache to force refresh.[/]");
            }

            if (!settings.StableOnly)
            {
                AnsiConsole.MarkupLine("[grey]Tip: Use --stable to show only stable releases.[/]");
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to fetch releases:[/] {ex.Message}");
            return -1;
        }
    }

    internal sealed class Settings : GlobalSettings
    {
        [CommandOption("--stable")]
        [Description("Show only stable releases.")]
        public bool StableOnly { get; set; }

        [CommandOption("--filter <VERSION>")]
        [Description("Filter versions by text (e.g., '4.3').")]
        public string? VersionFilter { get; set; }

        [CommandOption("--limit <COUNT>")]
        [Description("Maximum number of releases to display. Default: 20.")]
        [DefaultValue(20)]
        public int Limit { get; set; } = 20;

        [CommandOption("--no-cache")]
        [Description("Skip cache and fetch fresh data from GitHub.")]
        public bool NoCache { get; set; }
    }
}
