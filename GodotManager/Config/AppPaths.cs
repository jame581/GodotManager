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

    /// <summary>
    /// Set when <c>GODMAN_GLOBAL_ROOT</c> (or its legacy alias) still carries its pre-1.4.0
    /// meaning -- the shim directory, with installs in <c>&lt;value&gt;/godman</c> -- as
    /// judged by <see cref="UsesPre140GlobalRootMeaning"/>. Null otherwise, and always on
    /// Windows, where the variable's meaning did not change. The text is for
    /// <c>list</c> and <c>doctor</c> to print; this class only detects.
    /// </summary>
    public string? LegacyGlobalRootOverrideWarning { get; }

    /// <summary>
    /// The global-root variable in effect and its value (the primary name, or the legacy
    /// alias when only that is set; an empty value counts as unset). Null without an
    /// override. For remedies that must hand it to a sudo that would otherwise drop it.
    /// </summary>
    public (string Name, string Value)? GlobalRootOverride { get; }

    /// <summary>
    /// The GODMAN_HOME value in effect (or its legacy alias; an empty value counts as unset).
    /// Null without an override. While it is set godman never moves user install roots
    /// (<see cref="MigrationGates"/>), which doctor has to say rather than offer the move.
    /// </summary>
    public string? HomeOverride { get; }

    /// <summary>
    /// True when <see cref="LegacyGlobalRootOverrideWarning"/> is set: the global shim and
    /// install directories under the misread prefix are then not created, since they
    /// would be junk directories inside the old shim directory.
    /// </summary>
    private readonly bool _skipGlobalDirectories;

    /// <summary>
    /// False for a relative GODMAN_GLOBAL_ROOT: paths still resolve (against the working
    /// directory, as they always have), but no global directory is created under it.
    /// </summary>
    private readonly bool _globalPrefixRooted;

    /// <summary>
    /// False for a relative GODMAN_HOME: the same rule as <see cref="_globalPrefixRooted"/>.
    /// Paths resolve as given, but no user directory is created under the working
    /// directory; the writes that need one (registry, cache, install, shim) create it then.
    /// </summary>
    private readonly bool _homeRooted;

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
    private readonly IReadOnlyList<(string Source, string Destination)> _migrationMoves;
    private readonly IReadOnlyList<string> _legacyGlobalRegistryFiles;
    private readonly IReadOnlyList<(string Path, string Description)> _legacyPaths;

    public AppPaths() : this(applySideEffects: true)
    {
    }

    /// <summary>
    /// <paramref name="applySideEffects"/> false resolves every path exactly as the public
    /// constructor does but runs no migration, no shim/env repair and creates no directory.
    /// For tests that assert the default layout: with no overrides set, the public
    /// constructor would act on the developer's real home and, as root, on /usr/local.
    /// </summary>
    internal AppPaths(bool applySideEffects)
    {
        var overrideBasePrimary = Environment.GetEnvironmentVariable(EnvHome);
        var overrideBaseLegacy = Environment.GetEnvironmentVariable(LegacyEnvHome);
        var overrideBase = ResolveOverride(overrideBasePrimary, overrideBaseLegacy);
        HomeOverride = overrideBase;

        var overrideGlobalPrimary = Environment.GetEnvironmentVariable(EnvGlobal);
        var overrideGlobalLegacy = Environment.GetEnvironmentVariable(LegacyEnvGlobal);
        GlobalRootOverride = ReadGlobalRootOverride();
        var overrideGlobalBase = GlobalRootOverride?.Value;

        var (migrateUser, migrateGlobal) = MigrationGates(
            overrideBasePrimary, overrideBaseLegacy, overrideGlobalPrimary, overrideGlobalLegacy);

        if (OperatingSystem.IsWindows())
        {
            var defaultAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var defaultProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var appData = overrideBase ?? defaultAppData;
            _homeRooted = System.IO.Path.IsPathRooted(appData);
            var programFiles = overrideGlobalBase ?? defaultProgramFiles;
            _globalPrefixRooted = System.IO.Path.IsPathRooted(programFiles);

            var userRoot = System.IO.Path.Combine(appData, WindowsFolderName);
            var globalRoot = System.IO.Path.Combine(programFiles, WindowsFolderName);

            // What the constructor runs, and -- below -- what doctor reads: one plan, so the
            // advice cannot promise a move no run makes. The user scope migrates only while
            // its root is the default (no GODMAN_HOME), like Linux. The global scope moves
            // <prefix>\GodotManager inside whatever absolute prefix is in effect, also like
            // Linux: the legacy root sits beside the new one, so a prefix chosen through
            // GODOT_MANAGER_GLOBAL_ROOT in 1.3 has one to move, and only a run that plans it
            // ever will. A relative prefix moves nothing (MigrationGates).
            var windowsPlan = PlanWindowsMigrations(
                appData, programFiles, migrateUser && appData == defaultAppData, migrateGlobal);

            // The shims live inside the roots on Windows, so after a move they sit at the
            // new location still naming the old one.
            if (applySideEffects)
            {
                MigrateAndRepair(windowsPlan, new[]
                {
                    System.IO.Path.Combine(userRoot, "bin", "godot.cmd"),
                    System.IO.Path.Combine(globalRoot, "bin", "godot.cmd")
                });
            }

            ConfigDirectory = userRoot;
            _userShimDirectory = System.IO.Path.Combine(userRoot, "bin");
            _userInstallRoot = System.IO.Path.Combine(userRoot, "installs");

            // Exactly the moves the constructor runs -- doctor looks at the same directory
            // TryMigrateDirectory checks (the whole root on Windows, not the installs\ inside
            // it the relocation map names). EnsureDirectories also reads this to keep from
            // creating a destination whose move is still pending.
            _migrationMoves = windowsPlan;

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
            _homeRooted = System.IO.Path.IsPathRooted(home);
            var globalPrefix = overrideGlobalBase ?? DefaultLinuxGlobalPrefix;
            _globalPrefixRooted = System.IO.Path.IsPathRooted(globalPrefix);

            var userConfigRoot = System.IO.Path.Combine(home, ".config", LinuxFolderName);
            var userInstallRoot = System.IO.Path.Combine(home, ".local", "share", LinuxFolderName, "installs");
            var userShimDirectory = System.IO.Path.Combine(home, ".local", "bin");
            var globalShim = System.IO.Path.Combine(globalPrefix, "bin");
            // Global installs deliberately do NOT live in the shim directory. A directory
            // called <shim>/godman claims the exact filename the godman binary needs there
            // for `sudo godman` to resolve at all -- sudo's secure_path never contains
            // ~/.local/bin -- so the two cannot coexist. See README's Paths section.
            var globalInstallRoot = System.IO.Path.Combine(globalPrefix, "lib", LinuxFolderName);

            if (overrideGlobalBase is not null
                && UsesPre140GlobalRootMeaning(overrideGlobalBase, System.IO.Path.Combine(globalInstallRoot, "installs.json")))
            {
                LegacyGlobalRootOverrideWarning = BuildLegacyGlobalRootOverrideWarning(
                    GlobalRootOverride!.Value.Name, overrideGlobalBase);
                _skipGlobalDirectories = true;
            }

            var oldGlobalInstallRoot = System.IO.Path.Combine(globalShim, LinuxFolderName);
            var legacyGlobalInstallRoot = System.IO.Path.Combine(globalShim, LegacyLinuxFolderName);
            var oldUserInstallRoot = System.IO.Path.Combine(home, ".local", "bin", LinuxFolderName);
            var legacyUserInstallRoot = System.IO.Path.Combine(home, ".local", "bin", LegacyLinuxFolderName);

            // Gated per scope, not on "any override set": exporting GODMAN_HOME must not
            // stop `sudo -E godman ... --scope Global` from moving the default global root.
            // The global moves run under any prefix (see MigrationGates).
            if (applySideEffects)
            {
                MigrateAndRepair(
                    PlanLinuxMigrations(
                        home,
                        globalPrefix,
                        migrateUser: migrateUser && home == defaultHome,
                        migrateGlobal: migrateGlobal),
                    LinuxRepairTargets(
                        System.IO.Path.Combine(userShimDirectory, "godot"),
                        System.IO.Path.Combine(globalShim, "godot"),
                        System.IO.Path.Combine(userConfigRoot, "env.sh")));
            }

            ConfigDirectory = userConfigRoot;
            _userShimDirectory = userShimDirectory;
            _globalShimDirectory = globalShim;
            _userInstallRoot = userInstallRoot;
            _globalInstallRoot = globalInstallRoot;
            // On Linux the global install root IS the shared directory (installs sit
            // directly inside it), so the registry belongs there too.
            _globalConfigRoot = globalInstallRoot;

            // What doctor consults (GetMigrationDestination): exactly the moves the constructor
            // runs. A planned move doctor cannot see through makes it give advice that never
            // takes effect -- with GODMAN_HOME set it said "rmdir the empty destination, then
            // godman moves them", but no run did and the next one recreated the directory.
            _migrationMoves = PlanLinuxMigrations(
                home, globalPrefix, migrateUser: migrateUser && home == defaultHome, migrateGlobal: migrateGlobal);

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

        if (applySideEffects)
        {
            EnsureDirectories();
        }
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
    /// <summary>
    /// The Windows migrations, in the order they run: the user root, then the machine-wide one.
    /// Pure, for the same reason <see cref="PlanLinuxMigrations(string, string)"/> is: the
    /// constructor runs this plan and doctor reads it, so a rule that lived only in the
    /// constructor let doctor promise moves that never ran. <paramref name="appData"/> and
    /// <paramref name="programFiles"/> are the effective bases (defaults or overrides); each
    /// legacy <c>GodotManager</c> root sits beside its <c>godman</c> destination.
    /// </summary>
    internal static IReadOnlyList<(string Source, string Destination)> PlanWindowsMigrations(
        string appData, string programFiles, bool migrateUser, bool migrateGlobal)
    {
        var plan = new List<(string Source, string Destination)>();
        if (migrateUser)
        {
            plan.Add((System.IO.Path.Combine(appData, LegacyWindowsFolderName),
                System.IO.Path.Combine(appData, WindowsFolderName)));
        }

        if (migrateGlobal)
        {
            plan.Add((System.IO.Path.Combine(programFiles, LegacyWindowsFolderName),
                System.IO.Path.Combine(programFiles, WindowsFolderName)));
        }

        return plan;
    }

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
    /// Which scopes may run their migrations. Pure so the gating is unit-testable without a
    /// real home directory or root. Each scope looks only at its own overrides: an override
    /// on one scope says nothing about the other.
    ///
    /// The user moves run only without a GODMAN_HOME override. The global moves run under
    /// any GODMAN_GLOBAL_ROOT, so the global override arguments never turn them off: on
    /// Linux they only move <c>&lt;prefix&gt;/bin/godman</c> (or <c>godot-manager</c>) to
    /// <c>&lt;prefix&gt;/lib/godman</c> inside whatever prefix is in effect, which is
    /// exactly what a user who re-pointed a 1.3.0 value like <c>/opt/godot/bin</c> at
    /// <c>/opt/godot</c> needs -- and an override naming the default prefix
    /// (<c>/usr/local</c>, what the release notes tell users to set) must not behave any
    /// differently from no override. The one exception is a relative prefix: it resolves
    /// against whatever directory godman happens to run in, so it never migrates (and the
    /// constructor creates no global directory under it). An empty value counts as unset
    /// (<see cref="ResolveOverride"/>). The Windows constructor still restricts its global
    /// move to the default %ProgramFiles% on its own.
    /// </summary>
    internal static (bool MigrateUser, bool MigrateGlobal) MigrationGates(
        string? homeOverride, string? legacyHomeOverride, string? globalOverride, string? legacyGlobalOverride)
    {
        var global = ResolveOverride(globalOverride, legacyGlobalOverride);
        return (ResolveOverride(homeOverride, legacyHomeOverride) is null,
                global is null || System.IO.Path.IsPathRooted(global));
    }

    /// <summary>
    /// The global-root variable in effect, read from the process environment: its name
    /// (the primary one, or the legacy alias when only that is set) and value. Null when
    /// neither is set to a non-empty value. Relative values are returned as they are;
    /// callers decide what to do with them.
    /// </summary>
    internal static (string Name, string Value)? ReadGlobalRootOverride()
    {
        var primary = Environment.GetEnvironmentVariable(EnvGlobal);
        var value = ResolveOverride(primary, Environment.GetEnvironmentVariable(LegacyEnvGlobal));
        return value is null ? null : (string.IsNullOrEmpty(primary) ? LegacyEnvGlobal : EnvGlobal, value);
    }

    /// <summary>
    /// The override in effect: the primary variable, else the legacy alias. An empty value
    /// counts as unset -- <c>export GODMAN_GLOBAL_ROOT=</c> means "no override", not a
    /// prefix of "" that resolves against the working directory.
    /// </summary>
    internal static string? ResolveOverride(string? primary, string? legacy) =>
        string.IsNullOrEmpty(primary) ? (string.IsNullOrEmpty(legacy) ? null : legacy) : primary;

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
    /// The directory whose existence decides whether the migration covering
    /// <paramref name="oldRoot"/> can still run: <see cref="TryMigrateDirectory"/> no-ops
    /// once it exists, even empty. On Linux that is the relocation's new root itself; on
    /// Windows it is the whole godman root, one level above the <c>installs</c> directory
    /// the relocation map names. Null when no planned move covers the path.
    /// </summary>
    internal string? GetMigrationDestination(string oldRoot)
    {
        foreach (var (source, destination) in _migrationMoves)
        {
            if (PathRebase.TryRebase(oldRoot, source, destination, out _))
            {
                return destination;
            }
        }

        return null;
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

    /// <summary>
    /// The pre-migration global install roots that still exist on this machine. On Linux
    /// <c>&lt;prefix&gt;/bin/godman</c> and <c>&lt;prefix&gt;/bin/godot-manager</c>; on Windows
    /// <c>&lt;prefix&gt;\GodotManager</c>. Until the first privileged run moves them,
    /// <see cref="GetLegacyGlobalRegistryFiles"/> makes <c>list</c> show their installs, so
    /// <c>clean</c> has to treat them as global targets -- and decide on elevation for them
    /// before deleting anything, since nothing else creates them or asks for UAC.
    /// Directories only: once migrated, <c>&lt;prefix&gt;/bin/godman</c> is the name the godman
    /// binary itself takes, and that file must never be matched. For the same reason a root
    /// holding the running executable is left out (godman unzipped into a folder that carries
    /// the old product name): deleting it would take the tool with the installs.
    /// </summary>
    public IReadOnlyList<string> GetLegacyGlobalInstallRoots() =>
        GetLegacyGlobalInstallRoots(Environment.ProcessPath);

    internal IReadOnlyList<string> GetLegacyGlobalInstallRoots(string? running)
    {
        return _legacyGlobalRegistryFiles
            .Select(System.IO.Path.GetDirectoryName)
            .OfType<string>()
            .Where(Directory.Exists)
            .Where(root => running is null || !PathRebase.IsUnder(running, root))
            .ToList();
    }

    /// <summary>
    /// The Linux files <see cref="MigrateAndRepair"/> rewrites after a move: both shims,
    /// this run's env.sh, and -- when it is a different file -- the env.sh the global shim
    /// sources. That one is whatever <c>EnvScriptPath</c> the activating run had, which
    /// under <c>sudo -E</c> or a HOME-resetting sudo is not this run's, and it exports the
    /// old root as GODOT_HOME just the same.
    ///
    /// Lazy: <see cref="RepairMovedRootReferences"/> stops before enumerating when nothing
    /// moved, so the global shim is read only after a move. The shim is parsed before it
    /// is yielded, i.e. before the repair rewrites it. An unreadable shim adds nothing.
    /// </summary>
    internal static IEnumerable<string> LinuxRepairTargets(string userShim, string globalShim, string envScript)
    {
        yield return userShim;

        string? sourced = null;
        try
        {
            if (System.IO.File.Exists(globalShim))
            {
                sourced = Services.EnvironmentService.ParseShimSourcedScript(System.IO.File.ReadAllText(globalShim));
            }
        }
        catch
        {
            // Best-effort, like the rest of the repair.
        }

        yield return globalShim;
        yield return envScript;

        if (sourced is not null && !string.Equals(sourced, envScript, StringComparison.Ordinal))
        {
            yield return sourced;
        }
    }

    /// <summary>
    /// Whether a GODMAN_GLOBAL_ROOT value still means what it did before 1.4.0, when it
    /// named the shim directory and installs lived in <c>&lt;value&gt;/godman</c>: that
    /// directory holds a registry (<c>installs.json</c>) or at least one install directory,
    /// and the registry of the current layout (<paramref name="globalRegistryFile"/>) does
    /// not exist yet. Once the current layout has a registry the value is taken at its
    /// word. Reads the disk; an unreadable directory counts as holding nothing.
    /// </summary>
    internal static bool UsesPre140GlobalRootMeaning(string overrideValue, string globalRegistryFile)
    {
        try
        {
            if (System.IO.File.Exists(globalRegistryFile))
            {
                return false;
            }

            var oldRoot = System.IO.Path.Combine(overrideValue, LinuxFolderName);
            return System.IO.File.Exists(System.IO.Path.Combine(oldRoot, "installs.json"))
                || (System.IO.Directory.Exists(oldRoot) && System.IO.Directory.EnumerateDirectories(oldRoot).Any());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pointing the variable at the value's parent only fixes things when the value is a
    /// <c>&lt;X&gt;/bin</c> shim directory: the migration looks in
    /// <c>&lt;prefix&gt;/bin/godman</c>. Any other value needs the root moved by hand.
    /// </summary>
    internal static string BuildLegacyGlobalRootOverrideWarning(string variable, string value)
    {
        var trimmed = value.Length > 1 ? value.TrimEnd('/') : value;
        var oldRoot = System.IO.Path.Combine(trimmed, LinuxFolderName);
        var parent = System.IO.Path.GetDirectoryName(trimmed);
        var prefix = $"{variable}={value} uses the pre-1.4.0 meaning (the shim directory). It now names a prefix: ";

        return string.Equals(System.IO.Path.GetFileName(trimmed), "bin", StringComparison.Ordinal) && !string.IsNullOrEmpty(parent)
            ? prefix + $"set it to {parent} so installs in {oldRoot} are found and moved."
            : prefix + $"set it to the prefix you want, move {oldRoot} to <prefix>/lib/godman yourself and " +
              "update the install paths recorded in its installs.json (godman only moves <prefix>/bin/godman automatically).";
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
        if (_homeRooted)
        {
            System.IO.Directory.CreateDirectory(ConfigDirectory);
            System.IO.Directory.CreateDirectory(DownloadCacheDirectory);
            System.IO.Directory.CreateDirectory(_userShimDirectory);
            System.IO.Directory.CreateDirectory(_userInstallRoot);
        }

        if (_skipGlobalDirectories || !_globalPrefixRooted)
        {
            return;
        }

        // Attempt global dirs; permission may be required. Never create one that is, or
        // lies inside, the destination of a move whose source is still there -- the
        // migration no-ops once its destination exists, even empty, so an empty directory
        // made here after a failed move would block that move on every later run.
        foreach (var directory in new[] { _globalShimDirectory, _globalInstallRoot })
        {
            if (!IsInsidePendingMigrationDestination(directory))
            {
                TryCreateDirectory(directory);
            }
        }
    }

    private bool IsInsidePendingMigrationDestination(string directory)
    {
        foreach (var (source, destination) in _migrationMoves)
        {
            if (PathRebase.IsUnder(directory, destination) && System.IO.Directory.Exists(source))
            {
                return true;
            }
        }

        return false;
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
