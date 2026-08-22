using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GodotManager.Tests;

public class AppPathsTests
{
    [Fact]
    public void Linux_ScopePaths_KeepShimsInBinAndInstallsOutOfIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Skip on Windows; paths differ.
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new AppPaths();

        Assert.Equal(Path.Combine(home, ".local", "share", "godman", "installs"), paths.GetInstallRoot(InstallScope.User));
        // Global installs must NOT sit in the shim directory. /usr/local/bin/godman is
        // where the godman binary itself wants to live so that `sudo godman` resolves
        // (sudo's secure_path never contains ~/.local/bin), and a directory of that name
        // blocks it.
        Assert.Equal("/usr/local/lib/godman", paths.GetInstallRoot(InstallScope.Global));
        Assert.Equal(Path.Combine(home, ".local", "bin"), paths.GetShimDirectory(InstallScope.User));
        Assert.Equal("/usr/local/bin", paths.GetShimDirectory(InstallScope.Global));
        Assert.NotEqual(paths.GetShimDirectory(InstallScope.Global), Path.GetDirectoryName(paths.GetInstallRoot(InstallScope.Global)));
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

        Assert.Equal("/usr/local/lib/godman/installs.json", paths.GlobalRegistryFile);
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
    public void Linux_GodmanGlobalRootOverride_IsAPrefix_NotTheShimDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // GODMAN_GLOBAL_ROOT used to name the shim directory itself, with installs
        // dropped inside it. It now names a prefix, the way it already stood in for
        // %ProgramFiles% on Windows -- one variable still redirects both directories,
        // which is the property every fixture-based test depends on for isolation.
        using var fixture = new GodmanTestFixture();
        var prefix = Path.Combine(fixture.TempRoot, "global");

        Assert.Equal(Path.Combine(prefix, "bin"), fixture.Paths.GetShimDirectory(InstallScope.Global));
        Assert.Equal(Path.Combine(prefix, "lib", "godman"), fixture.Paths.GetInstallRoot(InstallScope.Global));
    }

    [Fact]
    public void Linux_MigrationPlan_PrefersGodmanRootOverGodotManagerRoot()
    {
        // TryMigrateDirectory is a no-op once the destination exists, so on a machine
        // carrying both old roots the one planned first is the one that survives. The
        // godman root has to come first: it is the newer layout, and letting the
        // abandoned godot-manager root win would replace live installs with stale ones.
        var plan = AppPaths.PlanLinuxMigrations("/home/tester", "/usr/local");

        var godman = plan.ToList().FindIndex(m => m.Source == "/usr/local/bin/godman");
        var godotManager = plan.ToList().FindIndex(m => m.Source == "/usr/local/bin/godot-manager");

        Assert.True(godman >= 0, "the old global godman root must be migrated");
        Assert.True(godotManager >= 0, "the legacy godot-manager global root must still be migrated");
        Assert.True(godman < godotManager, "godman must be planned before godot-manager");
    }

    [Fact]
    public void Linux_MigrationPlan_SendsBothOldGlobalRootsOutOfTheShimDirectory()
    {
        var plan = AppPaths.PlanLinuxMigrations("/home/tester", "/usr/local");

        Assert.Contains(("/usr/local/bin/godman", "/usr/local/lib/godman"), plan);
        Assert.Contains(("/usr/local/bin/godot-manager", "/usr/local/lib/godman"), plan);

        // Nothing may be migrated *into* the shim directory -- that is the collision the
        // whole move exists to remove.
        Assert.DoesNotContain(plan, m => Path.GetDirectoryName(m.Destination) == "/usr/local/bin");
    }

    [Fact]
    public void Linux_MigrationPlan_KeepsUserRootsPointedAtTheShareDirectory()
    {
        var plan = AppPaths.PlanLinuxMigrations("/home/tester", "/usr/local");
        var userInstalls = Path.Combine("/home/tester", ".local", "share", "godman", "installs");

        Assert.Contains((Path.Combine("/home/tester", ".local", "bin", "godman"), userInstalls), plan);
        Assert.Contains((Path.Combine("/home/tester", ".local", "bin", "godot-manager"), userInstalls), plan);
        Assert.Contains((Path.Combine("/home/tester", ".config", "godot-manager"),
            Path.Combine("/home/tester", ".config", "godman")), plan);
    }

    [Fact]
    public void InstallRootRelocations_MapEveryOldRootOntoItsCurrentLocation()
    {
        using var fixture = new GodmanTestFixture();
        var relocations = fixture.Paths.GetInstallRootRelocations();

        Assert.NotEmpty(relocations);

        // Every relocation has to land on a root this AppPaths actually resolves to,
        // otherwise RegistryService would rebase an entry onto a directory nothing
        // else in the tool ever writes.
        var liveRoots = new[]
        {
            fixture.Paths.GetInstallRoot(InstallScope.User),
            fixture.Paths.GetInstallRoot(InstallScope.Global)
        };

        Assert.All(relocations, r => Assert.Contains(r.NewRoot, liveRoots));
        Assert.All(relocations, r => Assert.NotEqual(r.OldRoot, r.NewRoot));
    }

    [Fact]
    public void Linux_InstallRootRelocations_CoverTheOldGlobalRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new GodmanTestFixture();
        var globalShim = fixture.Paths.GetShimDirectory(InstallScope.Global);
        var relocations = fixture.Paths.GetInstallRootRelocations();

        Assert.Contains(relocations, r =>
            r.OldRoot == Path.Combine(globalShim, "godman")
            && r.NewRoot == fixture.Paths.GetInstallRoot(InstallScope.Global));
    }

    [Fact]
    public void LegacyGlobalRegistryFiles_PointIntoTheOldGlobalRootsAndNotTheCurrentOne()
    {
        using var fixture = new GodmanTestFixture();
        var legacy = fixture.Paths.GetLegacyGlobalRegistryFiles();

        Assert.NotEmpty(legacy);
        Assert.DoesNotContain(fixture.Paths.GlobalRegistryFile, legacy);
        Assert.All(legacy, f => Assert.Equal("installs.json", Path.GetFileName(f)));

        // Each candidate must belong to a root the relocation map already knows about,
        // so the file fallback and the entry rebase can never disagree about where a
        // pre-migration machine kept its global state. The file is *inside* that root
        // on Linux and one level above it on Windows (where installs live in
        // <root>\installs), so the check is "some old root lives under this file's
        // directory" rather than an equality either platform would fail.
        var oldRoots = fixture.Paths.GetInstallRootRelocations().Select(r => r.OldRoot).ToList();
        Assert.All(legacy, f => Assert.Contains(oldRoots, r => r.StartsWith(Path.GetDirectoryName(f)!, StringComparison.Ordinal)));
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
        Assert.Contains(legacyPaths, p => p.Path == "/usr/local/bin/godman");
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
