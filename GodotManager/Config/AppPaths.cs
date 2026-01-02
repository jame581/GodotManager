using System;
using GodotManager.Domain;

namespace GodotManager.Config;

internal sealed class AppPaths
{
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
        var overrideBase = Environment.GetEnvironmentVariable("GODOT_MANAGER_HOME");
        var overrideGlobalBase = Environment.GetEnvironmentVariable("GODOT_MANAGER_GLOBAL_ROOT");

        if (OperatingSystem.IsWindows())
        {
            var appData = overrideBase ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigDirectory = System.IO.Path.Combine(appData, "GodotManager");
            _userShimDirectory = System.IO.Path.Combine(appData, "GodotManager", "bin");
            _userInstallRoot = System.IO.Path.Combine(appData, "GodotManager", "installs");

            // Global scope for Windows: C:\Program Files\GodotManager
            var programFiles = overrideGlobalBase ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            _globalInstallRoot = System.IO.Path.Combine(programFiles, "GodotManager", "installs");
            _globalShimDirectory = System.IO.Path.Combine(programFiles, "GodotManager", "bin");
        }
        else
        {
            var home = overrideBase ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ConfigDirectory = System.IO.Path.Combine(home, ".config", "godot-manager");
            _userShimDirectory = System.IO.Path.Combine(home, ".local", "bin");
            _globalShimDirectory = overrideGlobalBase ?? "/usr/local/bin";
            _userInstallRoot = System.IO.Path.Combine(home, ".local", "bin", "godot-manager");
            _globalInstallRoot = System.IO.Path.Combine(_globalShimDirectory, "godot-manager");
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
}
