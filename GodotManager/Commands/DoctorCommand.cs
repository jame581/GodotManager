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

        // Reads the target out of the shim itself (see InspectShim): the registry cannot
        // tell a shim that still names a moved root from a healthy one.
        var shim = EnvironmentService.InspectShim(_paths, scope, _diagnostics);
        if (shim.Exists)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Shim present[/] at {shim.ShimPath}");

            if (shim.MissingTarget is not null)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Shim points at a missing binary[/]: {shim.MissingTarget}");
                // A global shim needs root to rewrite -- name a command sudo can find.
                var rewrite = active is not null && active.Scope == InstallScope.Global
                    ? $"Run `{ElevatedCommandLine.Render($"activate {active.Id}")}` to rewrite the shim."
                    : active is not null
                        ? $"Run `godman activate {active.Id}` to rewrite the shim."
                        : "Run the activate command again to rewrite the shim.";
                AnsiConsole.MarkupLineInterpolated($"[grey]  {rewrite}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Shim missing[/] at {shim.ShimPath}");
        }

        // The registry's own view: an active entry whose directory is gone (deleted by
        // hand, or an old root whose migration left it at neither path). It does not catch
        // a completed migration -- the registry rebases the entry onto the new root -- nor
        // a blocked one, which leaves the files, and the shim, where they were; the shim
        // target check above is what covers a moved root.
        if (active is not null && !string.IsNullOrEmpty(active.Path) && !Directory.Exists(active.Path))
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Active install directory missing[/]: {active.Path}");
            AnsiConsole.MarkupLine("[grey]  Run the activate command again to rewrite the shim, or remove the entry.[/]");
        }

        // One entry per install is created on install. An explicit --no-shortcut is not a
        // problem. Pre-1.4.0 installs (LauncherEntry == null) never had one, which is every
        // install on an upgraded machine -- one summary line for those rather than a
        // two-line block each; a per-install line only for an entry godman did write
        // (LauncherEntry == true) that has since gone missing.
        var launcher = new LauncherService(_paths, _diagnostics);
        var predatingEntries = registry.Installs.Count(x => x.LauncherEntry == null && !launcher.Exists(x));
        if (predatingEntries > 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]{predatingEntries} install(s) predate launcher entries[/]; `godman activate <id>` adds one (and makes it active).");
        }

        foreach (var install in registry.Installs.Where(x => x.LauncherEntry == true && !launcher.Exists(x)))
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Launcher entry missing[/] for {install.Version} ({install.Edition}, {install.Scope}) [grey]{install.Id}[/]");
            // Honest about the side effects: activate is the only command that writes a missing
            // entry, and it also switches the active version (and needs root for a global one).
            var activate = install.Scope == InstallScope.Global
                ? ElevatedCommandLine.Render($"activate {install.Id}")
                : $"godman activate {install.Id}";
            AnsiConsole.MarkupLineInterpolated($"[grey]  Run: {activate} -- this also makes it the active version. Or reinstall it with --force.[/]");
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

            // Content in the destination is not enough on its own either: a machine that
            // carries two old roots migrates only the first one planned, and any registry
            // entry the move did not reach still names this directory (the registry rebases
            // an entry only onto a destination that actually holds it). If anything
            // registered still lives here, it is live.
            var referenced = registry.Installs.Any(x => !string.IsNullOrEmpty(x.Path) && PathRebase.IsUnder(x.Path, legacyPath));

            // A move is still possible only while the destination is empty:
            // TryMigrateDirectory no-ops once it holds anything, so for a root whose
            // destination already has content "run godman to complete the move" would be
            // advice that can never work, repeated forever.
            var movePending = pending.Count > 0 && pending.All(r => !HasContent(r.NewRoot));
            var isGlobalRoot = pending.Any(r => string.Equals(
                r.NewRoot, _paths.GetInstallRoot(InstallScope.Global), StringComparison.Ordinal));

            if (movePending)
            {
                // Only the machine-wide root needs privileges. User roots move on every
                // ordinary run, and under a HOME-resetting sudo an elevated run would
                // migrate root's home, not this user's.
                var remedy = !isGlobalRoot
                    ? "godman moves them on its next ordinary (non-sudo) run"
                    : OperatingSystem.IsWindows()
                        ? "Run an elevated godman command to complete the move"
                        : $"Run `{ElevatedCommandLine.Render("list")}` to complete the move";
                AnsiConsole.MarkupLineInterpolated(
                    $"[grey]  Still in use -- installs here have not moved to {pending[0].NewRoot} yet. {remedy}; do not delete this directory.[/]");
            }
            else if (referenced)
            {
                var why = pending.Count > 0
                    ? $"godman cannot move them automatically because {pending[0].NewRoot} already exists"
                    : "godman does not move this directory automatically";
                AnsiConsole.MarkupLineInterpolated(
                    $"[grey]  Still in use -- registered installs still live here and {why}. Reinstall or remove those installs first; do not delete this directory until then.[/]");
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
