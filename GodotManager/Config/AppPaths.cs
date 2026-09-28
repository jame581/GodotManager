using GodotManager.Domain;

namespace GodotManager.Config;

internal sealed class AppPaths
{
    private const string EnvHome = "GODMAN_HOME";
    private const string EnvGlobal = "GODMAN_GLOBAL_ROOT";
    private const string LegacyEnvHome = "GODOT_MANAGER_HOME";
    private const string LegacyEnvGlobal = "GODOT_MANAGER_GLOBAL_ROOT";
    private const string WindowsFolderName = "godman";
    private const string LegacyWindowsFolderName = "GodotManager";
    private const string LinuxFolderName = "godman";
    private const string LegacyLinuxFolderName = "godot-manager";

    /// <summary>
    /// Internal override for where launcher entries go, used by the test fixture. Launcher
    /// entries deliberately do NOT follow GODMAN_HOME / GODMAN_GLOBAL_ROOT: an entry is only
    /// useful where the desktop looks for it, and those user-facing overrides would move it
    /// somewhere no launcher reads.
    /// </summary>
    private const string EnvLauncherRoot = "GODMAN_LAUNCHER_ROOT";

    /// <summary>
    /// Default prefix for machine-wide state on Linux. The shim lives in
    /// <c>&lt;prefix&gt;/bin</c> and installs in <c>&lt;prefix&gt;/lib/godman</c>;
    /// <c>GODMAN_GLOBAL_ROOT</c> overrides the prefix, matching how the same
    /// variable already stands in for %ProgramFiles% on Windows.
    /// </summary>
    private const string DefaultLinuxGlobalPrefix = "/usr/local";

    public string ConfigDirectory { get; }
    public string RegistryFile { get; }
    public string GlobalRegistryFile { get; }
    public string EnvScriptPath { get; }
    public string DownloadCacheDirectory { get; }
    public string DesktopDirectory { get; }
    public string EnvVarName => "GODOT_HOME";

    private readonly string _userInstallRoot;
    private readonly string _globalInstallRoot;
    private readonly string _userShimDirectory;
    private readonly string _globalShimDirectory;
    private readonly string _globalConfigRoot;
    private readonly string _userLauncherDirectory;
    private readonly string _globalLauncherDirectory;
    private readonly string? _userLauncherIconPath;
    private readonly string? _globalLauncherIconPath;
    private readonly IReadOnlyList<(string OldRoot, string NewRoot)> _installRootRelocations;
    private readonly IReadOnlyList<string> _legacyGlobalRegistryFiles;
    private readonly IReadOnlyList<(string Path, string Description)> _legacyPaths;

    public AppPaths()
    {
        var overrideBasePrimary = Environment.GetEnvironmentVariable(EnvHome);
        var overrideBaseLegacy = Environment.GetEnvironmentVariable(LegacyEnvHome);
        var overrideBase = overrideBasePrimary ?? overrideBaseLegacy;

        var overrideGlobalPrimary = Environment.GetEnvironmentVariable(EnvGlobal);
        var overrideGlobalLegacy = Environment.GetEnvironmentVariable(LegacyEnvGlobal);
        var overrideGlobalBase = overrideGlobalPrimary ?? overrideGlobalLegacy;

        var (migrateUser, migrateGlobal) = MigrationGates(
            overrideBasePrimary, overrideBaseLegacy, overrideGlobalPrimary, overrideGlobalLegacy);

        if (OperatingSystem.IsWindows())
        {
            var defaultAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var defaultProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var appData = overrideBase ?? defaultAppData;
            var programFiles = overrideGlobalBase ?? defaultProgramFiles;

            var userRoot = System.IO.Path.Combine(appData, WindowsFolderName);
            var globalRoot = System.IO.Path.Combine(programFiles, WindowsFolderName);

            // Each scope migrates only when its own root is at the default: a user who
            // exports GODMAN_HOME and runs `sudo -E godman ... --scope Global` still has a
            // default global root that needs moving.
            var windowsPlan = new List<(string Source, string Destination)>();
            if (migrateUser && appData == defaultAppData)
            {
                windowsPlan.Add((System.IO.Path.Combine(defaultAppData, LegacyWindowsFolderName), userRoot));
            }

            if (migrateGlobal && programFiles == defaultProgramFiles)
            {
                windowsPlan.Add((System.IO.Path.Combine(defaultProgramFiles, LegacyWindowsFolderName), globalRoot));
            }

            // The shims live inside the roots on Windows, so after a move they sit at the
            // new location still naming the old one.
            MigrateAndRepair(windowsPlan, new[]
            {
                System.IO.Path.Combine(userRoot, "bin", "godot.cmd"),
                System.IO.Path.Combine(globalRoot, "bin", "godot.cmd")
            });

            ConfigDirectory = userRoot;
            _userShimDirectory = System.IO.Path.Combine(userRoot, "bin");
            _userInstallRoot = System.IO.Path.Combine(userRoot, "installs");

            _installRootRelocations = new[]
            {
                (System.IO.Path.Combine(programFiles, LegacyWindowsFolderName, "installs"),
                    System.IO.Path.Combine(globalRoot, "installs")),
                (System.IO.Path.Combine(appData, LegacyWindowsFolderName, "installs"), _userInstallRoot)
            };

            // On Windows the registry sits beside installs\, in the root itself.
            _legacyGlobalRegistryFiles = new[]
            {
                System.IO.Path.Combine(programFiles, LegacyWindowsFolderName, "installs.json")
            };

            _legacyPaths = new[]
            {
                (System.IO.Path.Combine(appData, LegacyWindowsFolderName), "legacy config (GodotManager)"),
                (System.IO.Path.Combine(programFiles, LegacyWindowsFolderName), "legacy global (GodotManager)")
            };

            // Global scope for Windows: C:\Program Files\godman
            _globalInstallRoot = System.IO.Path.Combine(globalRoot, "installs");
            _globalShimDirectory = System.IO.Path.Combine(globalRoot, "bin");
            // Installs live in a subdirectory of the global root on Windows, so the
            // registry belongs in the root itself, beside installs\ and bin\.
            _globalConfigRoot = globalRoot;
        }
        else
        {
            var defaultHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var home = overrideBase ?? defaultHome;
            var globalPrefix = overrideGlobalBase ?? DefaultLinuxGlobalPrefix;

            var userConfigRoot = System.IO.Path.Combine(home, ".config", LinuxFolderName);
            var userInstallRoot = System.IO.Path.Combine(home, ".local", "share", LinuxFolderName, "installs");
            var userShimDirectory = System.IO.Path.Combine(home, ".local", "bin");
            var globalShim = System.IO.Path.Combine(globalPrefix, "bin");
            // Global installs deliberately do NOT live in the shim directory. A directory
            // called <shim>/godman claims the exact filename the godman binary needs there
            // for `sudo godman` to resolve at all -- sudo's secure_path never contains
            // ~/.local/bin -- so the two cannot coexist. See README's Paths section.
            var globalInstallRoot = System.IO.Path.Combine(globalPrefix, "lib", LinuxFolderName);

            var oldGlobalInstallRoot = System.IO.Path.Combine(globalShim, LinuxFolderName);
            var legacyGlobalInstallRoot = System.IO.Path.Combine(globalShim, LegacyLinuxFolderName);
            var oldUserInstallRoot = System.IO.Path.Combine(home, ".local", "bin", LinuxFolderName);
            var legacyUserInstallRoot = System.IO.Path.Combine(home, ".local", "bin", LegacyLinuxFolderName);

            // Gated per scope, not on "any override set": exporting GODMAN_HOME must not
            // stop `sudo -E godman ... --scope Global` from moving the default global root.
            MigrateAndRepair(
                PlanLinuxMigrations(
                    home,
                    globalPrefix,
                    migrateUser: migrateUser && home == defaultHome,
                    migrateGlobal: migrateGlobal && globalPrefix == DefaultLinuxGlobalPrefix),
                new[]
                {
                    System.IO.Path.Combine(userShimDirectory, "godot"),
                    System.IO.Path.Combine(globalShim, "godot"),
                    System.IO.Path.Combine(userConfigRoot, "env.sh")
                });

            ConfigDirectory = userConfigRoot;
            _userShimDirectory = userShimDirectory;
            _globalShimDirectory = globalShim;
            _userInstallRoot = userInstallRoot;
            _globalInstallRoot = globalInstallRoot;
            // On Linux the global install root IS the shared directory (installs sit
            // directly inside it), so the registry belongs there too.
            _globalConfigRoot = globalInstallRoot;

            _installRootRelocations = new[]
            {
                (oldGlobalInstallRoot, globalInstallRoot),
                (legacyGlobalInstallRoot, globalInstallRoot),
                (oldUserInstallRoot, userInstallRoot),
                (legacyUserInstallRoot, userInstallRoot)
            };

            // On Linux the registry lives inside the global install root, so it moved
            // with it. Same newest-first order as the migrations.
            _legacyGlobalRegistryFiles = new[]
            {
                System.IO.Path.Combine(oldGlobalInstallRoot, "installs.json"),
                System.IO.Path.Combine(legacyGlobalInstallRoot, "installs.json")
            };

            _legacyPaths = new[]
            {
                (System.IO.Path.Combine(home, ".config", LegacyLinuxFolderName), "legacy config (godot-manager)"),
                (legacyUserInstallRoot, "legacy installs (godot-manager)"),
                (legacyGlobalInstallRoot, "legacy global installs (godot-manager)"),
                (oldUserInstallRoot, "old install root (godman)"),
                (oldGlobalInstallRoot, "old global install root (godman)")
            };
        }

        RegistryFile = System.IO.Path.Combine(ConfigDirectory, "installs.json");
        GlobalRegistryFile = System.IO.Path.Combine(_globalConfigRoot, "installs.json");
        EnvScriptPath = System.IO.Path.Combine(ConfigDirectory, "env.sh");
        DownloadCacheDirectory = System.IO.Path.Combine(ConfigDirectory, "downloads");

        (_userLauncherDirectory, _globalLauncherDirectory, _userLauncherIconPath, _globalLauncherIconPath, var desktop) = ResolveLauncherLocations();
        DesktopDirectory = desktop;

        EnsureDirectories();
    }

    public string GetInstallRoot(InstallScope scope)
    {
        return scope == InstallScope.Global ? _globalInstallRoot : _userInstallRoot;
    }

    public string GetShimDirectory(InstallScope scope)
    {
        return scope == InstallScope.Global ? _globalShimDirectory : _userShimDirectory;
    }

    /// <summary>
    /// Where godman writes each install's launcher entry: an XDG <c>applications/</c>
    /// directory on Linux, godman's own Start Menu folder on Windows. Not created here --
    /// <see cref="Services.LauncherService"/> creates it on first write, since the global
    /// one needs privileges most runs do not have.
    /// </summary>
    public string GetLauncherDirectory(InstallScope scope)
    {
        return scope == InstallScope.Global ? _globalLauncherDirectory : _userLauncherDirectory;
    }

    /// <summary>The icon the Linux <c>.desktop</c> entries point at; null on Windows.</summary>
    public string? GetLauncherIconPath(InstallScope scope)
    {
        return scope == InstallScope.Global ? _globalLauncherIconPath : _userLauncherIconPath;
    }

    /// <summary>
    /// Legacy directory paths that should have been migrated away by now, each with a
    /// description. Derived from the resolved (override-aware) locations rather than
    /// hardcoded defaults, so this agrees with <see cref="GetInstallRootRelocations"/>
    /// about where an un-migrated machine keeps its files -- <c>doctor</c> joins the two
    /// to decide whether a leftover directory is safe to delete or still the live copy.
    /// </summary>
    public IReadOnlyList<(string Path, string Description)> GetLegacyPaths()
    {
        return _legacyPaths;
    }

    /// <summary>
    /// The directory moves that bring a default-layout Linux machine from an older path
    /// scheme onto the current one, in the order they must run.
    ///
    /// Pure -- it plans, it never touches disk -- because the ordering carries the whole
    /// decision: <see cref="TryMigrateDirectory"/> is a no-op once the destination exists,
    /// so when a machine has both <c>&lt;shim&gt;/godman</c> and
    /// <c>&lt;shim&gt;/godot-manager</c> sitting there, whichever is listed first wins and
    /// the other is left behind for <c>doctor</c> to report. Listing godman first preserves
    /// the precedence the pre-1.4.0 migration had when it folded godot-manager into
    /// <c>&lt;shim&gt;/godman</c>. Getting that backwards would silently resurrect an
    /// abandoned install root over the live one, which is exactly the kind of thing that
    /// needs a unit test rather than a privileged machine to catch.
    /// </summary>
    internal static IReadOnlyList<(string Source, string Destination)> PlanLinuxMigrations(string home, string globalPrefix)
    {
        return PlanLinuxMigrationsByScope(home, globalPrefix)
            .Select(m => (m.Source, m.Destination))
            .ToList();
    }

    /// <summary>
    /// <see cref="PlanLinuxMigrations(string, string)"/> restricted to the scopes allowed
    /// to migrate. The relative order within the plan is preserved.
    /// </summary>
    internal static IReadOnlyList<(string Source, string Destination)> PlanLinuxMigrations(
        string home, string globalPrefix, bool migrateUser, bool migrateGlobal)
    {
        return PlanLinuxMigrationsByScope(home, globalPrefix)
            .Where(m => m.Scope == InstallScope.Global ? migrateGlobal : migrateUser)
            .Select(m => (m.Source, m.Destination))
            .ToList();
    }

    /// <summary>
    /// Which scopes may run their default-layout migrations. Pure so the gating is
    /// unit-testable without a real home directory or root. Each scope looks only at its
    /// own overrides: the migrations move default locations, and an override on one scope
    /// says nothing about whether the other scope is at its default.
    /// </summary>
    internal static (bool MigrateUser, bool MigrateGlobal) MigrationGates(
        string? homeOverride, string? legacyHomeOverride, string? globalOverride, string? legacyGlobalOverride)
    {
        return (homeOverride == null && legacyHomeOverride == null,
                globalOverride == null && legacyGlobalOverride == null);
    }

    private static IReadOnlyList<(string Source, string Destination, InstallScope Scope)> PlanLinuxMigrationsByScope(string home, string globalPrefix)
    {
        var userConfigRoot = System.IO.Path.Combine(home, ".config", LinuxFolderName);
        var userInstallRoot = System.IO.Path.Combine(home, ".local", "share", LinuxFolderName, "installs");
        var userBin = System.IO.Path.Combine(home, ".local", "bin");
        var globalShim = System.IO.Path.Combine(globalPrefix, "bin");
        var globalInstallRoot = System.IO.Path.Combine(globalPrefix, "lib", LinuxFolderName);

        // Note that <shim>/godman and ~/.local/bin/godman are a *file* on any machine that
        // installed the binary there. A file must never be mistaken for an install root;
        // TryMigrateDirectory's Directory.Exists check on the source is what keeps the two
        // cases apart, which is why these entries can be listed unconditionally.
        return new[]
        {
            (System.IO.Path.Combine(home, ".config", LegacyLinuxFolderName), userConfigRoot, InstallScope.User),
            (System.IO.Path.Combine(globalShim, LinuxFolderName), globalInstallRoot, InstallScope.Global),
            (System.IO.Path.Combine(globalShim, LegacyLinuxFolderName), globalInstallRoot, InstallScope.Global),
            (System.IO.Path.Combine(userBin, LinuxFolderName), userInstallRoot, InstallScope.User),
            (System.IO.Path.Combine(userBin, LegacyLinuxFolderName), userInstallRoot, InstallScope.User)
        };
    }

    /// <summary>
    /// Install roots used by earlier versions, paired with where their contents live
    /// now. The constructor moves the directories themselves, but registry entries
    /// written before a move still carry the old absolute path, so
    /// <see cref="Services.RegistryService"/> rebases them against this map on load.
    /// Ordered newest-layout-first, the same way the migrations run.
    /// </summary>
    public IReadOnlyList<(string OldRoot, string NewRoot)> GetInstallRootRelocations()
    {
        return _installRootRelocations;
    }

    /// <summary>
    /// Where the machine-wide registry used to live, newest layout first.
    ///
    /// The global registry file sits inside the global install root, so it moved when
    /// that root did -- and moving it needs privileges an ordinary caller does not
    /// have. Without a read-side fallback, every unprivileged <c>list</c>, <c>doctor</c>
    /// and TUI session on a not-yet-migrated machine would simply stop seeing global
    /// installs, which looks exactly like data loss. Read-only: writes always go to
    /// <see cref="GlobalRegistryFile"/>, so the first elevated operation moves the
    /// machine onto the current layout for good.
    /// </summary>
    public IReadOnlyList<string> GetLegacyGlobalRegistryFiles()
    {
        return _legacyGlobalRegistryFiles;
    }

    private static (string User, string Global, string? UserIcon, string? GlobalIcon, string Desktop) ResolveLauncherLocations()
    {
        var root = Environment.GetEnvironmentVariable(EnvLauncherRoot);

        if (OperatingSystem.IsWindows())
        {
            var userStartMenu = root is null
                ? Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
                : System.IO.Path.Combine(root, "user");
            var globalStartMenu = root is null
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                : System.IO.Path.Combine(root, "global");
            var desktop = root is null
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : System.IO.Path.Combine(root, "Desktop");

            // .lnk files take their icon from the executable, so no icon path on Windows.
            return (System.IO.Path.Combine(userStartMenu, "Programs", WindowsFolderName),
                    System.IO.Path.Combine(globalStartMenu, "Programs", WindowsFolderName),
                    null, null, desktop);
        }

        string userDataDir;
        string globalDataDir;
        if (root is not null)
        {
            userDataDir = System.IO.Path.Combine(root, "user");
            globalDataDir = System.IO.Path.Combine(root, "global");
        }
        else
        {
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            userDataDir = !string.IsNullOrEmpty(xdgDataHome) && System.IO.Path.IsPathRooted(xdgDataHome)
                ? xdgDataHome
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            // On the default XDG_DATA_DIRS (/usr/local/share:/usr/share) regardless of
            // GODMAN_GLOBAL_ROOT -- a custom install prefix is not a place GNOME looks.
            globalDataDir = "/usr/local/share";
        }

        return (System.IO.Path.Combine(userDataDir, "applications"),
                System.IO.Path.Combine(globalDataDir, "applications"),
                LauncherIconPath(userDataDir),
                LauncherIconPath(globalDataDir),
                System.IO.Path.Combine(root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"));
    }

    private static string LauncherIconPath(string dataDir) =>
        System.IO.Path.Combine(dataDir, "icons", "hicolor", "scalable", "apps", "godman-godot.svg");

    private void EnsureDirectories()
    {
        System.IO.Directory.CreateDirectory(ConfigDirectory);
        System.IO.Directory.CreateDirectory(DownloadCacheDirectory);
        System.IO.Directory.CreateDirectory(_userShimDirectory);
        System.IO.Directory.CreateDirectory(_userInstallRoot);

        // Attempt global dirs; permission may be required.
        TryCreateDirectory(_globalShimDirectory);
        TryCreateDirectory(_globalInstallRoot);
    }

    private static void TryCreateDirectory(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
        }
        catch
        {
            // Ignore permission errors; caller may operate in user scope.
        }
    }

    /// <summary>
    /// Runs <paramref name="plan"/> in order, then repairs the godman-authored files that
    /// still name a root that just moved. Returns the moves that actually happened.
    ///
    /// The repair is the half that is easy to forget. The shim hard-codes
    /// <c>exec "&lt;old root&gt;/&lt;version&gt;/…"</c> and env.sh exports the old root as
    /// GODOT_HOME, and nothing else rewrites them -- while the registry rebases its
    /// entries onto the new root by itself, so every other check (doctor's "active install
    /// directory missing" included) sees a healthy machine whose <c>godot</c> no longer
    /// runs. Only a move that succeeded triggers it: a blocked migration leaves the files
    /// where the shim already points.
    ///
    /// Never throws: it runs inside the <see cref="AppPaths"/> constructor, and a failed
    /// migration or repair must not stop an unrelated command.
    /// </summary>
    internal static IReadOnlyList<(string Source, string Destination)> MigrateAndRepair(
        IEnumerable<(string Source, string Destination)> plan,
        IEnumerable<string> filesToRepair)
    {
        var moves = new List<(string Source, string Destination)>();
        foreach (var (source, destination) in plan)
        {
            if (TryMigrateDirectory(source, destination))
            {
                moves.Add((source, destination));
            }
        }

        RepairMovedRootReferences(filesToRepair, moves);
        return moves;
    }

    /// <summary>
    /// Best-effort file-applying wrapper around <see cref="RewriteMovedRootReferences"/>:
    /// only files that exist and actually name a moved root are rewritten. The write
    /// truncates in place, so the shim keeps its executable bit.
    /// </summary>
    internal static void RepairMovedRootReferences(
        IEnumerable<string> files, IReadOnlyList<(string Source, string Destination)> moves)
    {
        if (moves.Count == 0)
        {
            return;
        }

        foreach (var file in files)
        {
            try
            {
                if (!System.IO.File.Exists(file))
                {
                    continue;
                }

                var content = System.IO.File.ReadAllText(file);
                var repaired = RewriteMovedRootReferences(content, moves);
                if (!string.Equals(content, repaired, StringComparison.Ordinal))
                {
                    System.IO.File.WriteAllText(file, repaired);
                }
            }
            catch
            {
                // Best-effort, like the move itself: doctor reports a shim whose target is
                // missing, and `activate` rewrites it.
            }
        }
    }

    /// <summary>
    /// Rewrites every double-quoted path in <paramref name="content"/> that lies under a
    /// moved source root onto its destination. Every path godman writes into the shim
    /// (<c>source "…"</c>, <c>exec "…"</c>), <c>godot.cmd</c> (<c>"…" %*</c>) and env.sh
    /// (<c>GODOT_HOME="…"</c>) is quoted, so matching quoted strings with the registry's
    /// segment-aware rule (<see cref="PathRebase"/>) reaches exactly those paths and never a
    /// sibling that only shares a textual prefix. Pure.
    /// </summary>
    internal static string RewriteMovedRootReferences(
        string content, IReadOnlyList<(string Source, string Destination)> moves)
    {
        return System.Text.RegularExpressions.Regex.Replace(content, "\"([^\"\r\n]*)\"", match =>
        {
            var value = match.Groups[1].Value;
            foreach (var (source, destination) in moves)
            {
                if (PathRebase.TryRebase(value, source, destination, out var rebased))
                {
                    return "\"" + rebased + "\"";
                }
            }

            return match.Value;
        });
    }

    /// <summary>True only when the directory was actually moved.</summary>
    internal static bool TryMigrateDirectory(string source, string destination)
    {
        try
        {
            if (!System.IO.Directory.Exists(source) || System.IO.Directory.Exists(destination))
            {
                return false;
            }

            var parentDir = System.IO.Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parentDir))
            {
                System.IO.Directory.CreateDirectory(parentDir);
            }

            System.IO.Directory.Move(source, destination);
            return true;
        }
        catch
        {
            // Best-effort migration; leave legacy paths intact on failure.
            return false;
        }
    }
}
