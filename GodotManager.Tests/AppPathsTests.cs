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

        Assert.Equal(Path.Combine(home, ".local", "share", "godman", "installs"), paths.GetInstallRoot(InstallScope.User));
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

    [Fact]
    public void Linux_MigratesOldInstallRoot_WhenDirectoryExists()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var oldInstallRoot = Path.Combine(home, ".local", "bin", "godman");
        var newInstallRoot = Path.Combine(home, ".local", "share", "godman", "installs");

        // Skip if the godman binary file exists at this path (e.g. installed via install.sh)
        if (File.Exists(oldInstallRoot))
        {
            return;
        }

        // Clean up any previous test state
        if (Directory.Exists(newInstallRoot))
            Directory.Delete(newInstallRoot, true);

        // Create old install root as a directory with a marker file
        Directory.CreateDirectory(oldInstallRoot);
        var markerPath = Path.Combine(oldInstallRoot, "test-marker.txt");
        File.WriteAllText(markerPath, "migration-test");

        try
        {
            var paths = new AppPaths();

            // Old directory should have been migrated to new location
            Assert.False(Directory.Exists(oldInstallRoot), "Old install root should be removed after migration");
            Assert.True(Directory.Exists(newInstallRoot), "New install root should exist after migration");
            Assert.True(File.Exists(Path.Combine(newInstallRoot, "test-marker.txt")), "Marker file should be migrated");
        }
        finally
        {
            if (Directory.Exists(oldInstallRoot))
                Directory.Delete(oldInstallRoot, true);
            if (File.Exists(Path.Combine(newInstallRoot, "test-marker.txt")))
                File.Delete(Path.Combine(newInstallRoot, "test-marker.txt"));
        }
    }

    [Fact]
    public void Linux_DoesNotMigrateOldInstallRoot_WhenFileExists()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var binaryPath = Path.Combine(home, ".local", "bin", "godman");
        var newInstallRoot = Path.Combine(home, ".local", "share", "godman", "installs");

        // Skip if the path is already a directory (e.g. from another test)
        if (Directory.Exists(binaryPath))
        {
            return;
        }

        // Ensure the binary file exists (simulating install.sh behavior)
        var binaryExisted = File.Exists(binaryPath);
        if (!binaryExisted)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
            File.WriteAllText(binaryPath, "fake-binary");
        }

        try
        {
            // This should NOT throw — the file at the path should be left alone
            var paths = new AppPaths();

            Assert.True(File.Exists(binaryPath), "Binary file should still exist");
            Assert.True(Directory.Exists(newInstallRoot), "New install root should be created independently");
        }
        finally
        {
            if (!binaryExisted)
                File.Delete(binaryPath);
        }
    }
}
