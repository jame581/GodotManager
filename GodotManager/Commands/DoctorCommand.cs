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
    private readonly DiagnosticContext? _diagnostics;

    public DoctorCommand(RegistryService registry, AppPaths paths, DiagnosticContext? diagnostics = null)
    {
        _registry = registry;
        _paths = paths;
        _diagnostics = diagnostics;
    }

    internal sealed class Settings : GlobalSettings { }

    /// <summary>
    /// Whether anything actually landed in a relocation destination. Existence alone
    /// does not answer it: <see cref="AppPaths"/> best-effort-creates both install roots
    /// on every run, so an empty destination is the normal state of a machine whose
    /// migration has not run. An unreadable directory counts as empty, which keeps the
    /// advice on the safe side -- never tell someone to delete a directory when we
    /// cannot confirm its contents were copied somewhere else.
    /// </summary>
    private static bool HasContent(string directory)
    {
        try
        {
            return Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch
        {
            return false;
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
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

        // A shim that exists says nothing about whether it still resolves: it hard-codes
        // an absolute path into the install directory, so an install root that moved (a
        // path migration) or vanished leaves `godot` on PATH pointing at nothing. The
        // registry rebases entries onto a completed migration by itself; what it cannot
        // repair is a shim, and that only shows up as a missing directory here.
        if (active is not null && !string.IsNullOrEmpty(active.Path) && !Directory.Exists(active.Path))
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Active install directory missing[/]: {active.Path}");
            AnsiConsole.MarkupLine("[grey]  Run the activate command again to rewrite the shim, or remove the entry.[/]");
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
        var relocations = _paths.GetInstallRootRelocations();
        foreach (var (legacyPath, description) in legacyPaths)
        {
            if (!Directory.Exists(legacyPath))
            {
                continue;
            }

            AnsiConsole.MarkupLineInterpolated($"[yellow]Legacy directory found[/]: {legacyPath} ({description})");

            // "Delete it" is the right advice only once the migration has actually run.
            // A root whose destination does not exist yet is not a leftover -- it is
            // still the live copy of those installs, and the machine-wide one needs
            // privileges to move. Telling someone to remove that would destroy the
            // installs it is describing.
            // A legacy entry names a whole root; a relocation names the install
            // directory inside it, which on Windows is one level deeper. Match either.
            var pending = relocations
                .Where(r => string.Equals(r.OldRoot, legacyPath, StringComparison.Ordinal)
                    || r.OldRoot.StartsWith(legacyPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToList();

            if (pending.Count > 0 && pending.All(r => !HasContent(r.NewRoot)))
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[grey]  Still in use -- these installs have not moved to {pending[0].NewRoot} yet. Run an elevated godman command to complete the move; do not delete this directory.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[grey]  This directory can be removed after verifying your installs are intact.[/]");
            }
        }

        // Surface abandoned partials, which can be hundreds of megabytes.
        // Best-effort: doctor must survive a cache directory that is missing,
        // unreadable, or holds something unexpected rather than throwing.
        try
        {
            if (Directory.Exists(_paths.DownloadCacheDirectory))
            {
                var cacheFiles = Directory.GetFiles(_paths.DownloadCacheDirectory);
                var totalBytes = cacheFiles.Sum(f => new FileInfo(f).Length);
                var partFiles = cacheFiles.Where(f => f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)).ToList();
                var partials = partFiles.Count;

                // A completed archive with no partial sitting next to it is the
                // normal outcome of an install that failed after the download
                // finished but before extraction succeeded: ResolvePlanAsync runs
                // the download (and the promotion from .part to .archive) before
                // InstallAsync's target-exists check, so a pre-existing target
                // without --force, or a failure during extraction, leaves exactly
                // this behind with no .part anywhere in sight.
                var completedArchives = cacheFiles.Count(
                    f => f.EndsWith(".archive", StringComparison.OrdinalIgnoreCase));

                // A .part is only resumable when its .json sidecar (URL + ETag) is
                // still present -- see the cache-lifecycle invariant in DownloadService:
                // without it there is no If-Range guard, so DownloadAsync discards the
                // bytes and restarts from zero instead of resuming.
                var cacheFileSet = new HashSet<string>(cacheFiles, StringComparer.OrdinalIgnoreCase);
                var resumablePartials = partFiles.Count(f => cacheFileSet.Contains(Path.ChangeExtension(f, ".json")));

                if (cacheFiles.Length == 0)
                {
                    AnsiConsole.MarkupLineInterpolated($"[green]Download cache[/] empty: {_paths.DownloadCacheDirectory}");
                }
                else
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $"[yellow]Download cache[/]: {cacheFiles.Length} file(s), {totalBytes / 1024d / 1024d:F1} MB in {_paths.DownloadCacheDirectory}");

                    if (partials > 0)
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"[yellow]  {partials} incomplete download(s)[/], {resumablePartials} resumable.");
                    }

                    if (completedArchives > 0)
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"[yellow]  {completedArchives} completed archive(s)[/] left over from an install that did not finish.");
                    }

                    // Any leftover cache file, partial or completed, is safe to
                    // discard -- gating this hint on partials alone missed the
                    // completed-archive case, which is now the more common one.
                    if (partials > 0 || completedArchives > 0)
                    {
                        AnsiConsole.MarkupLine("[grey]  Run 'godman clean' to discard them.[/]");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Could not inspect download cache at {_paths.DownloadCacheDirectory}: {ex.Message}");
        }

        return 0;
    }
}
