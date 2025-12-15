using System;
using System.IO;
using System.Threading.Tasks;
using GodotManager.Config;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class DoctorCommand : AsyncCommand
{
    private readonly RegistryService _registry;
    private readonly AppPaths _paths;

    public DoctorCommand(RegistryService registry, AppPaths paths)
    {
        _registry = registry;
        _paths = paths;
    }

    public override async Task<int> ExecuteAsync(CommandContext context)
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

        return 0;
    }
}
