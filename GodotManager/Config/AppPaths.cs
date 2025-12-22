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
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigDirectory = System.IO.Path.Combine(appData, "GodotManager");
            _userShimDirectory = System.IO.Path.Combine(ConfigDirectory, "bin");
            _globalShimDirectory = _userShimDirectory;
            _userInstallRoot = System.IO.Path.Combine(ConfigDirectory, "installs");
            _globalInstallRoot = _userInstallRoot;
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ConfigDirectory = System.IO.Path.Combine(home, ".config", "godot-manager");
            _userShimDirectory = System.IO.Path.Combine(home, ".local", "bin");
            _globalShimDirectory = "/usr/local/bin";
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

        // Only attempt global dirs on Linux; permission may be required.
        if (!OperatingSystem.IsWindows())
        {
            TryCreateDirectory(_globalShimDirectory);
            TryCreateDirectory(_globalInstallRoot);
        }
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
