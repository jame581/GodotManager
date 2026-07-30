using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Tests.Helpers;
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
    public void Linux_GlobalRegistryFile_SitsBesideGlobalInstallRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // On Linux the global install root IS the shared directory (installs sit
        // directly inside it, per GetInstallRoot(Global) above), so the registry
        // belongs right there beside them, not in some other parent.
        var paths = new AppPaths();

        Assert.Equal("/usr/local/bin/godman/installs.json", paths.GlobalRegistryFile);
    }

    [Fact]
    public void Windows_GlobalRegistryFile_SitsInGlobalRootParent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // On Windows the global install root is <globalRoot>\installs, so the
        // registry has to sit one level up, beside installs\ and bin\ -- not
        // inside the installs directory itself.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var paths = new AppPaths();

        Assert.Equal(Path.Combine(programFiles, "godman", "installs.json"), paths.GlobalRegistryFile);
    }

    [Fact]
    public void GlobalRegistryFile_IsIsolatedByGodmanGlobalRootOverride()
    {
        // Guards the assumption every RegistryService test relies on: GodmanTestFixture's
        // GODMAN_GLOBAL_ROOT override reaches the registry path too, not just the
        // install root, so tests never risk touching a real global registry.
        using var fixture = new GodmanTestFixture();

        Assert.StartsWith(fixture.TempRoot, fixture.Paths.GlobalRegistryFile);
        Assert.Equal("installs.json", Path.GetFileName(fixture.Paths.GlobalRegistryFile));
        Assert.NotEqual(fixture.Paths.RegistryFile, fixture.Paths.GlobalRegistryFile);
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
    public void Linux_GetLegacyPaths_ReturnsExpectedPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new AppPaths();
        var legacyPaths = paths.GetLegacyPaths();

        Assert.Contains(legacyPaths, p => p.Path == Path.Combine(home, ".config", "godot-manager"));
        Assert.Contains(legacyPaths, p => p.Path == Path.Combine(home, ".local", "bin", "godot-manager"));
        Assert.Contains(legacyPaths, p => p.Path == "/usr/local/bin/godot-manager");
        Assert.Contains(legacyPaths, p => p.Path == Path.Combine(home, ".local", "bin", "godman"));
    }

    [Fact]
    public void Windows_GetLegacyPaths_ReturnsExpectedPaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var paths = new AppPaths();
        var legacyPaths = paths.GetLegacyPaths();

        Assert.Contains(legacyPaths, p => p.Path == Path.Combine(appData, "GodotManager"));
        Assert.Contains(legacyPaths, p => p.Path == Path.Combine(programFiles, "GodotManager"));
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

    [Fact]
    public void DownloadCacheDirectory_IsUnderConfigDirectoryAndExists()
    {
        using var fixture = new GodmanTestFixture();

        Assert.Equal(
            Path.Combine(fixture.Paths.ConfigDirectory, "downloads"),
            fixture.Paths.DownloadCacheDirectory);
        Assert.True(Directory.Exists(fixture.Paths.DownloadCacheDirectory));
    }
}
