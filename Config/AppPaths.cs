using System;

namespace GodotManager.Config;

internal sealed class AppPaths
{
    public string ConfigDirectory { get; }
    public string InstallRoot { get; }
    public string RegistryFile { get; }
    public string ShimDirectory { get; }
    public string EnvScriptPath { get; }
    public string EnvVarName => "GODOT_HOME";

    public AppPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigDirectory = System.IO.Path.Combine(appData, "GodotManager");
            ShimDirectory = System.IO.Path.Combine(ConfigDirectory, "bin");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ConfigDirectory = System.IO.Path.Combine(home, ".config", "godot-manager");
            ShimDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
        }

        InstallRoot = System.IO.Path.Combine(ConfigDirectory, "installs");
        RegistryFile = System.IO.Path.Combine(ConfigDirectory, "installs.json");
        EnvScriptPath = System.IO.Path.Combine(ConfigDirectory, "env.sh");

        System.IO.Directory.CreateDirectory(ConfigDirectory);
        System.IO.Directory.CreateDirectory(InstallRoot);
        System.IO.Directory.CreateDirectory(ShimDirectory);
    }
}
