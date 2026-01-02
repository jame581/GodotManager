using System;
using System.IO;
using System.Threading.Tasks;
using GodotManager.Config;
using GodotManager.Services;
using GodotManager.Domain;
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

        // Check environment variable in current process
        var envInProcess = Environment.GetEnvironmentVariable(_paths.EnvVarName, EnvironmentVariableTarget.Process);
        
        // Check environment variable in user/machine registry
        var scope = active?.Scope ?? InstallScope.User;
        var registryTarget = OperatingSystem.IsWindows() && scope == InstallScope.Global
            ? EnvironmentVariableTarget.Machine
            : EnvironmentVariableTarget.User;
        var envInRegistry = OperatingSystem.IsWindows() 
            ? Environment.GetEnvironmentVariable(_paths.EnvVarName, registryTarget)
            : null;

        if (string.IsNullOrEmpty(envInProcess))
        {
            if (!string.IsNullOrEmpty(envInRegistry) && OperatingSystem.IsWindows())
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]{_paths.EnvVarName} not set in current session[/] (restart terminal/shell to load: {envInRegistry})");
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]{_paths.EnvVarName} not set.[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[green]{_paths.EnvVarName}[/] -> {envInProcess}");
            
            if (OperatingSystem.IsWindows() && envInRegistry != envInProcess)
            {
                AnsiConsole.MarkupLineInterpolated($"[grey]  Registry value:[/] {envInRegistry ?? "(not set)"}");
            }
        }

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

        return 0;
    }
}
