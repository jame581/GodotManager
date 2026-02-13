using GodotManager.Config;
using GodotManager.Domain;
using System;
using System.IO;
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

        Assert.Equal(Path.Combine(home, ".local", "bin", "godman"), paths.GetInstallRoot(InstallScope.User));
        Assert.Equal("/usr/local/bin/godman", paths.GetInstallRoot(InstallScope.Global));
        Assert.Equal(Path.Combine(home, ".local", "bin"), paths.GetShimDirectory(InstallScope.User));
        Assert.Equal("/usr/local/bin", paths.GetShimDirectory(InstallScope.Global));
    }

    [Fact]
    public void Windows_ScopePaths_AreInAppDataAndProgramFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Skip on Linux; paths differ.
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var paths = new AppPaths();

        Assert.Equal(Path.Combine(appData, "godman", "installs"), paths.GetInstallRoot(InstallScope.User));
        Assert.Equal(Path.Combine(programFiles, "godman", "installs"), paths.GetInstallRoot(InstallScope.Global));
        Assert.Equal(Path.Combine(appData, "godman", "bin"), paths.GetShimDirectory(InstallScope.User));
        Assert.Equal(Path.Combine(programFiles, "godman", "bin"), paths.GetShimDirectory(InstallScope.Global));
    }
}
