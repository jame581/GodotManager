using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using Spectre.Console;

namespace GodotManager.Tui;

internal sealed class TuiRunner
{
    private readonly RegistryService _registry;
    private readonly InstallerService _installer;
    private readonly EnvironmentService _environment;
    private readonly AppPaths _paths;
    private readonly GodotDownloadUrlBuilder _urlBuilder;
    private readonly GodotVersionFetcher _fetcher;

    public TuiRunner(RegistryService registry, InstallerService installer, EnvironmentService environment, AppPaths paths, GodotDownloadUrlBuilder urlBuilder, GodotVersionFetcher fetcher)
    {
        _registry = registry;
        _installer = installer;
        _environment = environment;
        _paths = paths;
        _urlBuilder = urlBuilder;
        _fetcher = fetcher;
    }

    public async Task<int> RunAsync()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("[bold]Godot Manager[/]")
                .AddChoices("List installs", "Browse versions", "Install", "Activate", "Remove", "Doctor", "Quit"));

            switch (choice)
            {
                case "List installs":
                    await ShowListAsync();
                    break;
                case "Browse versions":
                    await BrowseVersionsAsync();
                    break;
                case "Install":
                    await InstallFlowAsync();
                    break;
                case "Activate":
                    await ActivateFlowAsync();
                    break;
                case "Remove":
                    await RemoveFlowAsync();
                    break;
                case "Doctor":
                    await DoctorAsync();
                    break;
                case "Quit":
                    return 0;
            }
        }
    }

    private async Task ShowListAsync()
    {
        var registry = await _registry.LoadAsync();
        if (registry.Installs.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No installs registered.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Active", "Version", "Edition", "Platform", "Path", "Added", "Id");
        foreach (var install in registry.Installs.OrderByDescending(x => x.AddedAt))
        {
            var active = install.IsActive ? "[green]*[/]" : string.Empty;
            table.AddRow(active, install.Version, install.Edition.ToString(), install.Platform.ToString(), install.Path, install.AddedAt.ToString("u"), install.Id.ToString("N"));
        }

        AnsiConsole.Write(table);
    }

    private async Task InstallFlowAsync()
    {
        var version = AnsiConsole.Prompt(new TextPrompt<string>("Version:"));
        var edition = AnsiConsole.Prompt(new SelectionPrompt<InstallEdition>().Title("Edition:").AddChoices(InstallEdition.Standard, InstallEdition.DotNet));
        var platform = AnsiConsole.Prompt(new SelectionPrompt<InstallPlatform>().Title("Platform:").AddChoices(InstallPlatform.Windows, InstallPlatform.Linux)
            .UseConverter(p => p.ToString())) ;
        
        var scope = AnsiConsole.Prompt(new SelectionPrompt<InstallScope>()
            .Title("Scope:")
            .AddChoices(InstallScope.User, InstallScope.Global)
            .UseConverter(s => s == InstallScope.Global 
                ? "Global (requires admin/sudo)" 
                : "User"));
        
        var useLocal = AnsiConsole.Confirm("Use a local archive instead of downloading?", false);

        string? url = null;
        string? archive = null;

        if (useLocal)
        {
            archive = AnsiConsole.Prompt(new TextPrompt<string>("Archive path:"));
            if (!File.Exists(archive))
            {
                AnsiConsole.MarkupLine("[red]Archive not found.[/]");
                return;
            }
        }
        else
        {
            if (!_urlBuilder.TryBuildUri(version, edition, platform, out var built, out var error))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Could not build download URL:[/] {error}");
                return;
            }

            url = built!.ToString();
            AnsiConsole.MarkupLineInterpolated($"[grey]Download URL[/]: {url}");
        }

        var installDir = AnsiConsole.Prompt(new TextPrompt<string>("Install directory [grey](blank = default)[/]:").AllowEmpty());
        if (string.IsNullOrWhiteSpace(installDir))
        {
            installDir = null;
        }

        var force = AnsiConsole.Confirm("Force overwrite if directory exists?", false);
        var activate = AnsiConsole.Confirm("Activate after install?", true);
        var dryRun = AnsiConsole.Confirm("Preview only (dry-run)?", false);

        var request = new InstallRequest(version, edition, platform, scope, url is null ? null : new Uri(url), archive, installDir, activate, force, dryRun);

        if (dryRun)
        {
            PreviewInstall(request, url is null ? null : new Uri(url));
            return;
        }

        InstallEntry? result = null;
        await AnsiConsole.Progress().StartAsync(async ctx =>
        {
            var task = ctx.AddTask("install", maxValue: 100);
            result = await _installer.InstallAsync(request, pct => task.Value = Math.Min(100, pct));
        });

        if (result is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Installed[/] {result.Version} ({result.Edition}, {result.Platform}) -> [cyan]{result.Path}[/]");
        }
    }

    private static void PreviewInstall(InstallRequest request, Uri? url)
    {
        AnsiConsole.MarkupLine("\n[yellow bold]DRY RUN - No changes will be made[/]\n");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("Version", request.Version);
        table.AddRow("Edition", request.Edition.ToString());
        table.AddRow("Platform", request.Platform.ToString());
        table.AddRow("Scope", request.Scope.ToString());
        
        if (url != null)
        {
            table.AddRow("Download URL", url.ToString());
        }
        else if (!string.IsNullOrWhiteSpace(request.ArchivePath))
        {
            table.AddRow("Archive Path", request.ArchivePath);
        }

        var installPath = request.InstallPath ?? "[grey](auto-generated)[/]";
        table.AddRow("Install Path", installPath);
        table.AddRow("Force Overwrite", request.Force ? "Yes" : "No");
        table.AddRow("Activate After", request.Activate ? "Yes" : "No");

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[grey]Actions that would be performed:[/]");
        AnsiConsole.MarkupLine("[grey]1.[/] Download/copy archive");
        AnsiConsole.MarkupLine("[grey]2.[/] Extract to install directory");
        AnsiConsole.MarkupLine("[grey]3.[/] Register in installs.json");
        
        if (request.Activate)
        {
            AnsiConsole.MarkupLine("[grey]4.[/] Set as active install");
            AnsiConsole.MarkupLine("[grey]5.[/] Update GODOT_HOME environment variable");
            AnsiConsole.MarkupLine("[grey]6.[/] Write shim script");
        }
    }

    private async Task ActivateFlowAsync()
    {
        var registry = await _registry.LoadAsync();
        if (registry.Installs.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No installs to activate.[/]");
            return;
        }

        var choice = AnsiConsole.Prompt(new SelectionPrompt<InstallEntry>()
            .Title("Select install to activate")
            .UseConverter(i => Markup.Escape($"{i.Version} ({i.Edition}, {i.Platform}) [{i.Id:N}]"))
            .AddChoices(registry.Installs));

        var dryRun = AnsiConsole.Confirm("Preview only (dry-run)?", false);

        if (dryRun)
        {
            PreviewActivate(choice, registry);
            return;
        }

        registry.MarkActive(choice.Id);
        await _registry.SaveAsync(registry);
        await _environment.ApplyActiveAsync(choice);

        AnsiConsole.MarkupLineInterpolated($"[green]Activated[/] {choice.Version} ({choice.Edition}, {choice.Platform})");
        
        if (OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[grey]Note: Environment variable is set. Restart your terminal/shell to load GODOT_HOME.[/]");
        }
    }

    private static void PreviewActivate(InstallEntry install, InstallRegistry registry)
    {
        AnsiConsole.MarkupLine("\n[yellow bold]DRY RUN - No changes will be made[/]\n");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("Install ID", install.Id.ToString("N"));
        table.AddRow("Version", install.Version);
        table.AddRow("Edition", install.Edition.ToString());
        table.AddRow("Platform", install.Platform.ToString());
        table.AddRow("Scope", install.Scope.ToString());
        table.AddRow("Install Path", install.Path);
        
        var currentActive = registry.GetActive();
        if (currentActive != null)
        {
            table.AddRow("Currently Active", $"{currentActive.Version} ({currentActive.Edition})");
        }
        else
        {
            table.AddRow("Currently Active", "[grey](none)[/]");
        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[grey]Actions that would be performed:[/]");
        AnsiConsole.MarkupLine("[grey]1.[/] Mark install as active in registry");
        AnsiConsole.MarkupLine("[grey]2.[/] Update GODOT_HOME environment variable");
        AnsiConsole.MarkupLine("[grey]3.[/] Write shim script");
        AnsiConsole.MarkupLine("[grey]4.[/] Save registry changes");
    }

    private async Task RemoveFlowAsync()
    {
        var registry = await _registry.LoadAsync();
        if (registry.Installs.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No installs to remove.[/]");
            return;
        }

        var choice = AnsiConsole.Prompt(new SelectionPrompt<InstallEntry>()
            .Title("Select install to remove")
            .UseConverter(i => Markup.Escape($"{i.Version} ({i.Edition}, {i.Platform}) [{i.Id:N}]"))
            .AddChoices(registry.Installs));

        var deleteFiles = AnsiConsole.Confirm("Delete files on disk?", false);
        registry.Installs.Remove(choice);
        if (registry.ActiveId == choice.Id)
        {
            registry.ActiveId = null;
        }

        if (deleteFiles && Directory.Exists(choice.Path))
        {
            try
            {
                Directory.Delete(choice.Path, recursive: true);
                AnsiConsole.MarkupLineInterpolated($"[grey]Deleted files at[/] {choice.Path}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to delete files:[/] {ex.Message}");
            }
        }

        await _registry.SaveAsync(registry);
        AnsiConsole.MarkupLineInterpolated($"[green]Removed[/] {choice.Version} ({choice.Edition}, {choice.Platform})");
    }

    private async Task DoctorAsync()
    {
        var registry = await _registry.LoadAsync();
        var active = registry.GetActive();

        if (registry.Installs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No installs registered yet.[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Registry[/]: {registry.Installs.Count} install(s) tracked. Active: {(active is null ? "none" : active.Version)}");
        }

        var env = Environment.GetEnvironmentVariable(_paths.EnvVarName);
        if (string.IsNullOrEmpty(env))
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]{_paths.EnvVarName} not set in current session.[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[green]{_paths.EnvVarName}[/] -> {env}");
        }

        var scope = active?.Scope ?? InstallScope.User;
        var shimPath = OperatingSystem.IsWindows()
            ? Path.Combine(_paths.GetShimDirectory(scope), "godot.cmd")
            : Path.Combine(_paths.GetShimDirectory(scope), "godot");

        if (File.Exists(shimPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Shim present[/] at {shimPath}");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Shim missing[/] at {shimPath}");
        }
    }

    private async Task BrowseVersionsAsync()
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
                return;
            }

            var stableOnly = AnsiConsole.Confirm("Show only stable releases?", true);
            var filtered = stableOnly ? releases.Where(r => r.IsStable).ToList() : releases;

            var limit = Math.Min(50, filtered.Count);
            var display = filtered.Take(limit).ToList();

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumns("Version", "Type", "Standard", "Mono/.NET", "Published");

            foreach (var release in display)
            {
                var type = release.IsStable ? "[green]Stable[/]" : "[yellow]Preview[/]";
                var standard = release.HasStandard ? "[green]?[/]" : "[grey]-[/]";
                var dotnet = release.HasDotNet ? "[green]?[/]" : "[grey]-[/]";
                var published = release.PublishedAt.ToString("yyyy-MM-dd");

                table.AddRow(release.Version, type, standard, dotnet, published);
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLineInterpolated($"\n[grey]Showing {display.Count} of {filtered.Count} releases.[/]");

            var installNow = AnsiConsole.Confirm("Install a version from the list?", false);
            if (installNow)
            {
                await InstallFlowAsync();
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to fetch releases:[/] {ex.Message}");
        }
    }
}
