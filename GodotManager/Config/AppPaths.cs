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

    public string ConfigDirectory { get; }
    public string RegistryFile { get; }
    public string EnvScriptPath { get; }
    public string EnvVarName => "GODOT_HOME";

    private readonly string _userInstallRoot;
    private readonly string _globalInstallRoot;
    private readonly string _userShimDirectory;
    private readonly string _globalShimDirectory;

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

            // Global scope for Windows: C:\Program Files\godman
            _globalInstallRoot = System.IO.Path.Combine(globalRoot, "installs");
            _globalShimDirectory = System.IO.Path.Combine(globalRoot, "bin");
        }
        else
        {
            var defaultHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var home = overrideBase ?? defaultHome;
            var globalShim = overrideGlobalBase ?? "/usr/local/bin";

            var userConfigRoot = System.IO.Path.Combine(home, ".config", LinuxFolderName);
            var userInstallRoot = System.IO.Path.Combine(home, ".local", "share", LinuxFolderName, "installs");
            var globalInstallRoot = System.IO.Path.Combine(globalShim, LinuxFolderName);

            if (allowMigration && home == defaultHome && globalShim == "/usr/local/bin")
            {
                // Migrate from legacy godot-manager paths
                var legacyConfigRoot = System.IO.Path.Combine(defaultHome, ".config", LegacyLinuxFolderName);
                var legacyUserInstallRoot = System.IO.Path.Combine(defaultHome, ".local", "bin", LegacyLinuxFolderName);
                var legacyGlobalInstallRoot = System.IO.Path.Combine("/usr/local/bin", LegacyLinuxFolderName);
                TryMigrateDirectory(legacyConfigRoot, userConfigRoot);
                TryMigrateDirectory(legacyGlobalInstallRoot, globalInstallRoot);
                if (System.IO.Directory.Exists(legacyUserInstallRoot))
                {
                    TryMigrateDirectory(legacyUserInstallRoot, userInstallRoot);
                }

                // Migrate from old ~/.local/bin/godman/ install root (only if it's a directory, not the binary)
                var oldUserInstallRoot = System.IO.Path.Combine(defaultHome, ".local", "bin", LinuxFolderName);
                if (System.IO.Directory.Exists(oldUserInstallRoot))
                {
                    TryMigrateDirectory(oldUserInstallRoot, userInstallRoot);
                }
            }

            ConfigDirectory = userConfigRoot;
            _userShimDirectory = System.IO.Path.Combine(home, ".local", "bin");
            _globalShimDirectory = globalShim;
            _userInstallRoot = userInstallRoot;
            _globalInstallRoot = globalInstallRoot;
        }

        RegistryFile = System.IO.Path.Combine(ConfigDirectory, "installs.json");
        EnvScriptPath = System.IO.Path.Combine(ConfigDirectory, "env.sh");

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
    /// Returns legacy directory paths that should have been migrated.
    /// Each entry is (legacyPath, description).
    /// </summary>
    public IReadOnlyList<(string Path, string Description)> GetLegacyPaths()
    {
        var paths = new List<(string, string)>();

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            paths.Add((System.IO.Path.Combine(appData, LegacyWindowsFolderName), "legacy config (GodotManager)"));
            paths.Add((System.IO.Path.Combine(programFiles, LegacyWindowsFolderName), "legacy global (GodotManager)"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            paths.Add((System.IO.Path.Combine(home, ".config", LegacyLinuxFolderName), "legacy config (godot-manager)"));
            paths.Add((System.IO.Path.Combine(home, ".local", "bin", LegacyLinuxFolderName), "legacy installs (godot-manager)"));
            paths.Add((System.IO.Path.Combine("/usr/local/bin", LegacyLinuxFolderName), "legacy global installs (godot-manager)"));
            paths.Add((System.IO.Path.Combine(home, ".local", "bin", LinuxFolderName), "old install root (godman)"));
        }

        return paths;
    }

    private void EnsureDirectories()
    {
        System.IO.Directory.CreateDirectory(ConfigDirectory);
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

            System.IO.Directory.Move(source, destination);
        }
        catch
        {
            // Best-effort migration; leave legacy paths intact on failure.
        }
    }
}
