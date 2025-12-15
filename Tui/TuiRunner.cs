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

    public TuiRunner(RegistryService registry, InstallerService installer, EnvironmentService environment, AppPaths paths)
    {
        _registry = registry;
        _installer = installer;
        _environment = environment;
        _paths = paths;
    }

    public async Task<int> RunAsync()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("[bold]Godot Manager[/]")
                .AddChoices("List installs", "Install", "Activate", "Remove", "Doctor", "Quit"));

            switch (choice)
            {
                case "List installs":
                    await ShowListAsync();
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
        var source = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Source:").AddChoices("Download URL", "Local archive"));

        string? url = null;
        string? archive = null;

        if (source == "Download URL")
        {
            url = AnsiConsole.Prompt(new TextPrompt<string>("Archive URL:"));
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                AnsiConsole.MarkupLine("[red]Invalid URL.[/]");
                return;
            }
        }
        else
        {
            archive = AnsiConsole.Prompt(new TextPrompt<string>("Archive path:"));
            if (!File.Exists(archive))
            {
                AnsiConsole.MarkupLine("[red]Archive not found.[/]");
                return;
            }
        }

        var installDir = AnsiConsole.Prompt(new TextPrompt<string>("Install directory [grey](blank = default)[/]:").AllowEmpty());
        if (string.IsNullOrWhiteSpace(installDir))
        {
            installDir = null;
        }

        var force = AnsiConsole.Confirm("Force overwrite if directory exists?", false);
        var activate = AnsiConsole.Confirm("Activate after install?", true);

        var request = new InstallRequest(version, edition, platform, url is null ? null : new Uri(url), archive, installDir, activate, force);

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

        registry.MarkActive(choice.Id);
        await _registry.SaveAsync(registry);
        await _environment.ApplyActiveAsync(choice);

        AnsiConsole.MarkupLineInterpolated($"[green]Activated[/] {choice.Version} ({choice.Edition}, {choice.Platform})");
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

        var shimPath = OperatingSystem.IsWindows()
            ? Path.Combine(_paths.ShimDirectory, "godot.cmd")
            : Path.Combine(_paths.ShimDirectory, "godot");

        if (File.Exists(shimPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Shim present[/] at {shimPath}");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Shim missing[/] at {shimPath}");
        }
    }
}
