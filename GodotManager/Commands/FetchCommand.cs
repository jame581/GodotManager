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
            var releases = await AnsiConsole.Status()
                .StartAsync("Fetching available versions from GitHub...", async ctx =>
                {
                    return await _fetcher.FetchReleasesAsync();
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

    internal sealed class Settings : CommandSettings
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
    }
}
