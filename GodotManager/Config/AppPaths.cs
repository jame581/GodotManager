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

        var allowMigration = overrideBasePrimary == null
            && overrideBaseLegacy == null
            && overrideGlobalPrimary == null
            && overrideGlobalLegacy == null;

        if (OperatingSystem.IsWindows())
        {
            var defaultAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var defaultProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var appData = overrideBase ?? defaultAppData;
            var programFiles = overrideGlobalBase ?? defaultProgramFiles;

            var userRoot = System.IO.Path.Combine(appData, WindowsFolderName);
            var globalRoot = System.IO.Path.Combine(programFiles, WindowsFolderName);

            if (allowMigration && appData == defaultAppData && programFiles == defaultProgramFiles)
            {
                var legacyUserRoot = System.IO.Path.Combine(defaultAppData, LegacyWindowsFolderName);
                var legacyGlobalRoot = System.IO.Path.Combine(defaultProgramFiles, LegacyWindowsFolderName);
                TryMigrateDirectory(legacyUserRoot, userRoot);
                TryMigrateDirectory(legacyGlobalRoot, globalRoot);
            }

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

            if (allowMigration && home == defaultHome && globalPrefix == DefaultLinuxGlobalPrefix)
            {
                foreach (var (source, destination) in PlanLinuxMigrations(home, globalPrefix))
                {
                    TryMigrateDirectory(source, destination);
                }
            }

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
            (System.IO.Path.Combine(home, ".config", LegacyLinuxFolderName), userConfigRoot),
            (System.IO.Path.Combine(globalShim, LinuxFolderName), globalInstallRoot),
            (System.IO.Path.Combine(globalShim, LegacyLinuxFolderName), globalInstallRoot),
            (System.IO.Path.Combine(userBin, LinuxFolderName), userInstallRoot),
            (System.IO.Path.Combine(userBin, LegacyLinuxFolderName), userInstallRoot)
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

    private static void TryMigrateDirectory(string source, string destination)
    {
        try
        {
            if (!System.IO.Directory.Exists(source) || System.IO.Directory.Exists(destination))
            {
                return;
            }

            var parentDir = System.IO.Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parentDir))
            {
                System.IO.Directory.CreateDirectory(parentDir);
            }

            System.IO.Directory.Move(source, destination);
        }
        catch
        {
            // Best-effort migration; leave legacy paths intact on failure.
        }
    }
}
