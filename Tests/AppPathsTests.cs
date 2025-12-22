using System;
using System.IO;
using GodotManager.Config;
using GodotManager.Domain;
using Xunit;

namespace GodotManager.Tests;

public class AppPathsTests
{
    [Fact]
    public void Linux_ScopePaths_ArePlacedInLocalBin()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Skip on Windows; paths differ.
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new AppPaths();

        Assert.Equal(Path.Combine(home, ".local", "bin", "godot-manager"), paths.GetInstallRoot(InstallScope.User));
        Assert.Equal("/usr/local/bin/godot-manager", paths.GetInstallRoot(InstallScope.Global));
        Assert.Equal(Path.Combine(home, ".local", "bin"), paths.GetShimDirectory(InstallScope.User));
        Assert.Equal("/usr/local/bin", paths.GetShimDirectory(InstallScope.Global));
    }
}
