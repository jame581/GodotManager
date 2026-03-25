using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using GodotManager.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class DoctorCommand : AsyncCommand<DoctorCommand.Settings>
{
    private readonly RegistryService _registry;
    private readonly AppPaths _paths;

    public DoctorCommand(RegistryService registry, AppPaths paths)
    {
        _registry = registry;
        _paths = paths;
    }

    internal sealed class Settings : GlobalSettings { }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
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

        // Check if shim directory is in PATH (Windows only)
        if (OperatingSystem.IsWindows())
        {
            var shimDir = _paths.GetShimDirectory(scope);
            var pathVar = Environment.GetEnvironmentVariable("PATH", registryTarget) ?? string.Empty;
            var inPath = pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Any(p => string.Equals(p.Trim(), shimDir, StringComparison.OrdinalIgnoreCase));

            if (inPath)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]Shim directory in PATH[/]: {shimDir}");
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Shim directory NOT in PATH[/]: {shimDir}");
                AnsiConsole.MarkupLine($"[grey]  Run activate command again to add it to PATH, then restart your terminal.[/]");
            }
        }

        // Check for leftover legacy paths that should have been migrated
        var legacyPaths = _paths.GetLegacyPaths();
        foreach (var (legacyPath, description) in legacyPaths)
        {
            if (Directory.Exists(legacyPath))
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Legacy directory found[/]: {legacyPath} ({description})");
                AnsiConsole.MarkupLine("[grey]  This directory can be removed after verifying your installs are intact.[/]");
            }
        }

        return 0;
    }
}
