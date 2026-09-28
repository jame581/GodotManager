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
    /// <summary>
    /// The default layout, resolved without side effects. The public constructor with no
    /// overrides would run the real migrations (moving directories in the developer's
    /// home, rewriting ~/.local/bin/godot, and -- as root -- moving /usr/local/bin/godman)
    /// and create directories. No test in this class may do that: anything that moves or
    /// creates goes through temp paths (a fixture, or the internal planner/MigrateAndRepair).
    /// </summary>
    private static AppPaths DefaultPaths() => new AppPaths(applySideEffects: false);

    [Fact]
    public void Linux_ScopePaths_KeepShimsInBinAndInstallsOutOfIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Skip on Windows; paths differ.
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = DefaultPaths();

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
        var paths = DefaultPaths();

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
        var paths = DefaultPaths();

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
        var paths = DefaultPaths();

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

        // A temp home driven through the same planner + MigrateAndRepair the constructor
        // uses, so nothing under the real home is created, moved or rewritten.
        using var fixture = new GodmanTestFixture();
        var home = Path.Combine(fixture.TempRoot, "home");
        var oldInstallRoot = Path.Combine(home, ".local", "bin", "godman");
        var newInstallRoot = Path.Combine(home, ".local", "share", "godman", "installs");
        Directory.CreateDirectory(oldInstallRoot);
        File.WriteAllText(Path.Combine(oldInstallRoot, "test-marker.txt"), "migration-test");

        var moves = AppPaths.MigrateAndRepair(
            AppPaths.PlanLinuxMigrations(home, Path.Combine(fixture.TempRoot, "prefix"), migrateUser: true, migrateGlobal: false),
            []);

        Assert.Contains((oldInstallRoot, newInstallRoot), moves);
        Assert.False(Directory.Exists(oldInstallRoot), "Old install root should be removed after migration");
        Assert.True(File.Exists(Path.Combine(newInstallRoot, "test-marker.txt")), "Marker file should be migrated");
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
        var paths = DefaultPaths();
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
        var paths = DefaultPaths();
        var legacyPaths = paths.GetLegacyPaths();

        Assert.Contains(legacyPaths, p => p.Path == Path.Combine(appData, "GodotManager"));
        Assert.Contains(legacyPaths, p => p.Path == Path.Combine(programFiles, "GodotManager"));
    }

    [Fact]
    public void WithoutSideEffects_ResolvesTheSamePathsAndCreatesNothing()
    {
        // DefaultPaths() is only safe for the default-layout tests if it really touches
        // nothing; checked against fresh override roots that do not exist yet.
        using var fixture = new GodmanTestFixture();
        var home = Path.Combine(fixture.TempRoot, "fresh-home");
        var prefix = Path.Combine(fixture.TempRoot, "fresh-prefix");
        Environment.SetEnvironmentVariable("GODMAN_HOME", home);
        Environment.SetEnvironmentVariable("GODMAN_GLOBAL_ROOT", prefix);

        var inert = new AppPaths(applySideEffects: false);

        Assert.False(Directory.Exists(home), "no user directory may be created");
        Assert.False(Directory.Exists(prefix), "no global directory may be created");

        var live = new AppPaths();
        Assert.Equal(live.GetInstallRoot(InstallScope.User), inert.GetInstallRoot(InstallScope.User));
        Assert.Equal(live.GetInstallRoot(InstallScope.Global), inert.GetInstallRoot(InstallScope.Global));
        Assert.Equal(live.GetShimDirectory(InstallScope.Global), inert.GetShimDirectory(InstallScope.Global));
        Assert.Equal(live.GlobalRegistryFile, inert.GlobalRegistryFile);
        Assert.Equal(live.EnvScriptPath, inert.EnvScriptPath);
    }

    [Fact]
    public void Linux_DoesNotMigrateOldInstallRoot_WhenFileExists()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // ~/.local/bin/godman is the godman *binary* on any machine that used install.sh.
        // A file must never be mistaken for an install root. Temp home, as above.
        using var fixture = new GodmanTestFixture();
        var home = Path.Combine(fixture.TempRoot, "home");
        var binaryPath = Path.Combine(home, ".local", "bin", "godman");
        var newInstallRoot = Path.Combine(home, ".local", "share", "godman", "installs");
        Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
        File.WriteAllText(binaryPath, "fake-binary");

        var moves = AppPaths.MigrateAndRepair(
            AppPaths.PlanLinuxMigrations(home, Path.Combine(fixture.TempRoot, "prefix"), migrateUser: true, migrateGlobal: false),
            []);

        Assert.Empty(moves);
        Assert.Equal("fake-binary", File.ReadAllText(binaryPath));
        Assert.False(Directory.Exists(newInstallRoot), "nothing may be moved onto the user install root");
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

    [Fact]
    public void LauncherPaths_UnderTheFixture_StayInTempRootEvenWithXdgDataHome()
    {
        var savedXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Path.GetTempPath(), "must-not-be-used-" + Guid.NewGuid().ToString("N")));
        try
        {
            using var fixture = new GodmanTestFixture();

            foreach (var scope in new[] { InstallScope.User, InstallScope.Global })
            {
                Assert.StartsWith(fixture.TempRoot, fixture.Paths.GetLauncherDirectory(scope));
                var icon = fixture.Paths.GetLauncherIconPath(scope);
                if (icon is not null)
                {
                    Assert.StartsWith(fixture.TempRoot, icon);
                }
            }

            Assert.StartsWith(fixture.TempRoot, fixture.Paths.DesktopDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", savedXdg);
        }
    }

    [Fact]
    public void LauncherPaths_IgnoreGodmanHomeAndGlobalRoot()
    {
        // GODMAN_HOME / GODMAN_GLOBAL_ROOT are user-facing overrides for where godman keeps
        // its own files. A launcher entry is only useful where the desktop looks for it, so
        // those overrides must not move it. Only GODMAN_LAUNCHER_ROOT does.
        using var fixture = new GodmanTestFixture();
        var saved = Environment.GetEnvironmentVariable("GODMAN_LAUNCHER_ROOT");
        Environment.SetEnvironmentVariable("GODMAN_LAUNCHER_ROOT", null);
        try
        {
            var paths = new AppPaths();   // GODMAN_HOME / GODMAN_GLOBAL_ROOT still point into TempRoot

            Assert.DoesNotContain(fixture.TempRoot, paths.GetLauncherDirectory(InstallScope.User));
            Assert.DoesNotContain(fixture.TempRoot, paths.GetLauncherDirectory(InstallScope.Global));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal("/usr/local/share/applications", paths.GetLauncherDirectory(InstallScope.Global));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODMAN_LAUNCHER_ROOT", saved);
        }
    }

    [Fact]
    public void Linux_LauncherPaths_UnderLauncherRoot_FollowXdgLayout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new GodmanTestFixture();
        var root = Path.Combine(fixture.TempRoot, "launcher");

        Assert.Equal(Path.Combine(root, "user", "applications"), fixture.Paths.GetLauncherDirectory(InstallScope.User));
        Assert.Equal(Path.Combine(root, "global", "applications"), fixture.Paths.GetLauncherDirectory(InstallScope.Global));
        Assert.Equal(Path.Combine(root, "global", "icons", "hicolor", "scalable", "apps", "godman-godot.svg"),
            fixture.Paths.GetLauncherIconPath(InstallScope.Global));
    }

    // --- Repairing godman-authored files after a root move (review 2, item 1) ---

    private static readonly (string Source, string Destination)[] GlobalMove =
        [("/usr/local/bin/godman", "/usr/local/lib/godman")];

    [Fact]
    public void RewriteMovedRootReferences_RewritesAShimTargetUnderTheMovedRoot()
    {
        if (OperatingSystem.IsWindows()) return; // Unix separators in the fixtures below

        var shim = "#!/usr/bin/env bash\nsource \"/home/u/.config/godman/env.sh\" 2>/dev/null\nexec \"/usr/local/bin/godman/4.6.2-standard-linux-global/Godot_v4.6.2\" \"$@\"\n";

        var repaired = AppPaths.RewriteMovedRootReferences(shim, GlobalMove);

        Assert.Equal(
            "#!/usr/bin/env bash\nsource \"/home/u/.config/godman/env.sh\" 2>/dev/null\nexec \"/usr/local/lib/godman/4.6.2-standard-linux-global/Godot_v4.6.2\" \"$@\"\n",
            repaired);
    }

    [Fact]
    public void RewriteMovedRootReferences_RewritesTheEnvScriptExport()
    {
        if (OperatingSystem.IsWindows()) return;

        var repaired = AppPaths.RewriteMovedRootReferences(
            "export GODOT_HOME=\"/usr/local/bin/godman/4.6.2-standard-linux-global\"\n", GlobalMove);

        Assert.Equal("export GODOT_HOME=\"/usr/local/lib/godman/4.6.2-standard-linux-global\"\n", repaired);
    }

    [Fact]
    public void RewriteMovedRootReferences_LeavesASiblingThatOnlySharesAPrefixAlone()
    {
        if (OperatingSystem.IsWindows()) return;

        var shim = "exec \"/usr/local/bin/godman-old/4.6.2/Godot\" \"$@\"\n";

        Assert.Equal(shim, AppPaths.RewriteMovedRootReferences(shim, GlobalMove));
    }

    [Fact]
    public void RewriteMovedRootReferences_WithNoReference_ReturnsTheContentUnchanged()
    {
        if (OperatingSystem.IsWindows()) return;

        var shim = "#!/usr/bin/env bash\nexec \"/home/u/.local/share/godman/installs/4.7.2/Godot\" \"$@\"\n";

        Assert.Equal(shim, AppPaths.RewriteMovedRootReferences(shim, GlobalMove));
    }

    [Fact]
    public void RepairMovedRootReferences_TouchesOnlyFilesThatNameAMovedRoot()
    {
        using var fixture = new GodmanTestFixture();
        var oldRoot = Path.Combine(fixture.TempRoot, "old");
        var newRoot = Path.Combine(fixture.TempRoot, "new");
        var stale = Path.Combine(fixture.TempRoot, "stale-shim");
        var unrelated = Path.Combine(fixture.TempRoot, "unrelated-shim");
        var missing = Path.Combine(fixture.TempRoot, "missing-shim");
        File.WriteAllText(stale, $"exec \"{Path.Combine(oldRoot, "v", "Godot")}\" \"$@\"\n");
        File.WriteAllText(unrelated, "exec \"/somewhere/else/Godot\" \"$@\"\n");
        var unrelatedWrite = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(unrelated, unrelatedWrite);

        AppPaths.RepairMovedRootReferences([stale, unrelated, missing], [(oldRoot, newRoot)]);

        Assert.Contains(Path.Combine(newRoot, "v", "Godot"), File.ReadAllText(stale));
        Assert.Equal(unrelatedWrite, File.GetLastWriteTimeUtc(unrelated));
        Assert.False(File.Exists(missing), "a file that does not exist must not be created");
    }

    [Fact]
    public void MigrateAndRepair_WhenTheMoveIsBlocked_LeavesTheShimAlone()
    {
        // A blocked migration leaves the files where the shim already points; rewriting it
        // then would break a working `godot`.
        using var fixture = new GodmanTestFixture();
        var oldRoot = Path.Combine(fixture.TempRoot, "old");
        var newRoot = Path.Combine(fixture.TempRoot, "new");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot); // destination exists -> TryMigrateDirectory no-ops
        var shim = Path.Combine(fixture.TempRoot, "shim");
        var content = $"exec \"{Path.Combine(oldRoot, "v", "Godot")}\" \"$@\"\n";
        File.WriteAllText(shim, content);

        var moves = AppPaths.MigrateAndRepair([(oldRoot, newRoot)], [shim]);

        Assert.Empty(moves);
        Assert.Equal(content, File.ReadAllText(shim));
    }

    // --- Per-scope migration gating (review 2, item 2b) ---

    [Fact]
    public void MigrationGates_AHomeOverrideDoesNotBlockTheGlobalMigration()
    {
        // `export GODMAN_HOME=...; sudo -E godman list --scope Global` used to skip every
        // migration, so the default global root never moved.
        Assert.Equal((false, true), AppPaths.MigrationGates("/custom/home", null, null, null));
        Assert.Equal((false, true), AppPaths.MigrationGates(null, "/legacy/home", null, null));
    }

    [Fact]
    public void MigrationGates_AGlobalOverrideNamingTheDefaultPrefix_StillMigratesGlobal()
    {
        // What the 1.4.0 release notes tell users to set. Treating it as "an override" used
        // to switch the global move off, after which a root run's EnsureDirectories created
        // an empty /usr/local/lib/godman and blocked the move for good.
        Assert.Equal((true, true), AppPaths.MigrationGates(null, null, "/usr/local", null));
        Assert.Equal((true, true), AppPaths.MigrationGates(null, null, "/usr/local/", null));
        Assert.Equal((true, true), AppPaths.MigrationGates(null, null, null, "/usr/local"));
    }

    [Fact]
    public void MigrationGates_ACustomGlobalPrefix_StillMigratesGlobal_AndDoesNotBlockTheUserMigration()
    {
        // The global moves only ever go <prefix>/bin/godman -> <prefix>/lib/godman inside
        // the prefix in effect, so a user who updated /opt/godot/bin to /opt/godot gets
        // their root moved like everyone else.
        Assert.Equal((true, true), AppPaths.MigrationGates(null, null, "/opt/prefix", null));
        Assert.Equal((true, true), AppPaths.MigrationGates(null, null, null, "/opt/legacy"));
    }

    [Fact]
    public void MigrationGates_NoOverrides_MigratesBothScopes()
    {
        Assert.Equal((true, true), AppPaths.MigrationGates(null, null, null, null));
        Assert.Equal((false, true), AppPaths.MigrationGates("/h", null, "/p", null));
    }

    // --- GODMAN_GLOBAL_ROOT under 1.4.0 (review 3, item I1) ---

    [Fact]
    public void Linux_GlobalRootOverride_MigratesTheOldRootInsideThatPrefix()
    {
        if (OperatingSystem.IsWindows()) return; // Linux layout

        // Through the real constructor, with the prefix inside the fixture's TempRoot.
        using var fixture = new GodmanTestFixture(globalRoot: "custom", seed: prefix =>
        {
            var old = Path.Combine(prefix, "bin", "godman", "4.5.1-standard-linux-global");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "marker"), "x");
        });
        var prefix = Path.Combine(fixture.TempRoot, "custom");

        Assert.False(Directory.Exists(Path.Combine(prefix, "bin", "godman")), "the old root should have moved");
        Assert.True(File.Exists(Path.Combine(prefix, "lib", "godman", "4.5.1-standard-linux-global", "marker")));
    }

    [Fact]
    public void Linux_AFailedGlobalMove_LeavesNoEmptyDestinationBehind()
    {
        // TryMigrateDirectory no-ops once its destination exists, even empty. A rename that
        // fails must not be followed by EnsureDirectories creating that destination, or
        // every later run skips the move.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return; // POSIX permission simulation

        string? lockedBin = null;
        try
        {
            using var fixture = new GodmanTestFixture(globalRoot: "locked", seed: prefix =>
            {
                Directory.CreateDirectory(Path.Combine(prefix, "bin", "godman", "4.5.1"));
                lockedBin = Path.Combine(prefix, "bin");
                // rename(2) needs write access to the source's parent; the destination's
                // parent (<prefix>/lib) stays writable, so only the move itself fails.
                if (!OperatingSystem.IsWindows()) // restates the guard above for the analyzer
                {
                    File.SetUnixFileMode(lockedBin, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }
            });
            var prefix = Path.Combine(fixture.TempRoot, "locked");

            File.SetUnixFileMode(lockedBin!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.True(Directory.Exists(Path.Combine(prefix, "bin", "godman", "4.5.1")), "precondition: the move failed");
            Assert.False(Directory.Exists(Path.Combine(prefix, "lib", "godman")),
                "an empty destination would block the move on every later run");
        }
        finally
        {
            if (lockedBin is not null && Directory.Exists(lockedBin))
            {
                File.SetUnixFileMode(lockedBin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    private static void SeedPre140GlobalRoot(string value)
    {
        var old = Path.Combine(value, "godman");
        Directory.CreateDirectory(Path.Combine(old, "4.5.1-standard-linux-global"));
        File.WriteAllText(Path.Combine(old, "installs.json"), "{\"Installs\":[]}");
    }

    [Fact]
    public void Linux_AnOverrideKeepingThePre140Meaning_WarnsAndCreatesNoGlobalDirectories()
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = new GodmanTestFixture(globalRoot: Path.Combine("opt", "bin"), seed: SeedPre140GlobalRoot);
        var value = Path.Combine(fixture.TempRoot, "opt", "bin");

        Assert.False(Directory.Exists(Path.Combine(value, "bin")), "no <value>/bin shim directory may be created");
        Assert.False(Directory.Exists(Path.Combine(value, "lib")), "no <value>/lib/godman install root may be created");
        Assert.Equal(
            $"GODMAN_GLOBAL_ROOT={value} uses the pre-1.4.0 meaning (the shim directory). It now names a prefix: " +
            $"set it to {Path.Combine(fixture.TempRoot, "opt")} so installs in {Path.Combine(value, "godman")} are found and moved.",
            fixture.Paths.LegacyGlobalRootOverrideWarning);
    }

    [Fact]
    public void Linux_APre140OverrideNotEndingInBin_SaysToMoveTheRootByHand()
    {
        // Pointing at the parent only helps when the value is the shim directory <X>/bin:
        // the migration looks in <prefix>/bin/godman.
        if (OperatingSystem.IsWindows()) return;

        using var fixture = new GodmanTestFixture(globalRoot: "shims", seed: SeedPre140GlobalRoot);
        var value = Path.Combine(fixture.TempRoot, "shims");

        Assert.False(Directory.Exists(Path.Combine(value, "lib")));
        var warning = fixture.Paths.LegacyGlobalRootOverrideWarning;
        Assert.NotNull(warning);
        Assert.StartsWith($"GODMAN_GLOBAL_ROOT={value} uses the pre-1.4.0 meaning (the shim directory).", warning);
        Assert.Contains($"move {Path.Combine(value, "godman")} to <prefix>/lib/godman", warning);
        Assert.DoesNotContain($"set it to {fixture.TempRoot} ", warning);
    }

    [Fact]
    public void UsesPre140GlobalRootMeaning_OnlyWhileTheOldRootHasInstallsAndTheNewRegistryIsAbsent()
    {
        using var fixture = new GodmanTestFixture();
        var value = Path.Combine(fixture.TempRoot, "v");
        var newRegistry = Path.Combine(value, "lib", "godman", "installs.json");

        Assert.False(AppPaths.UsesPre140GlobalRootMeaning(value, newRegistry), "nothing there");

        Directory.CreateDirectory(Path.Combine(value, "godman"));
        Assert.False(AppPaths.UsesPre140GlobalRootMeaning(value, newRegistry), "an empty godman directory holds no installs");

        Directory.CreateDirectory(Path.Combine(value, "godman", "4.5.1"));
        Assert.True(AppPaths.UsesPre140GlobalRootMeaning(value, newRegistry), "an install directory");

        Directory.Delete(Path.Combine(value, "godman", "4.5.1"));
        File.WriteAllText(Path.Combine(value, "godman", "installs.json"), "{}");
        Assert.True(AppPaths.UsesPre140GlobalRootMeaning(value, newRegistry), "the 1.3.0 registry");

        Directory.CreateDirectory(Path.GetDirectoryName(newRegistry)!);
        File.WriteAllText(newRegistry, "{}");
        Assert.False(AppPaths.UsesPre140GlobalRootMeaning(value, newRegistry), "the new layout is in use");
    }

    [Fact]
    public void Linux_TheFixturesPrefixOverride_DoesNotWarn()
    {
        using var fixture = new GodmanTestFixture();
        Assert.Null(fixture.Paths.LegacyGlobalRootOverrideWarning);
    }

    [Fact]
    public void Linux_MigrationPlan_FilteredToGlobal_KeepsOnlyTheGlobalMovesInOrder()
    {
        if (OperatingSystem.IsWindows()) return; // Unix path literals

        var plan = AppPaths.PlanLinuxMigrations("/home/tester", "/usr/local", migrateUser: false, migrateGlobal: true);

        Assert.Equal(
            new[]
            {
                ("/usr/local/bin/godman", "/usr/local/lib/godman"),
                ("/usr/local/bin/godot-manager", "/usr/local/lib/godman")
            },
            plan);
    }

    [Fact]
    public void Linux_MigrationPlan_FilteredToUser_KeepsOnlyTheUserMoves()
    {
        if (OperatingSystem.IsWindows()) return; // Unix path literals

        var plan = AppPaths.PlanLinuxMigrations("/home/tester", "/usr/local", migrateUser: true, migrateGlobal: false);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, m => Assert.StartsWith("/home/tester/", m.Source));
        Assert.Equal(
            AppPaths.PlanLinuxMigrations("/home/tester", "/usr/local").Where(m => m.Source.StartsWith("/home/tester/", StringComparison.Ordinal)),
            plan);
    }

    [Fact]
    public void GetMigrationDestination_CoversEveryRelocation_AndContainsItsNewRoot()
    {
        // Doctor asks this where the migration's existence check looks. On Linux that is
        // the relocation's new root; on Windows the whole godman root above installs\.
        // Either way the relocation's new root must sit at or under it.
        using var fixture = new GodmanTestFixture();

        Assert.All(fixture.Paths.GetInstallRootRelocations(), r =>
        {
            var destination = fixture.Paths.GetMigrationDestination(r.OldRoot);
            Assert.NotNull(destination);
            Assert.True(PathRebase.IsUnder(r.NewRoot, destination!), $"{r.NewRoot} is not under {destination}");
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(r.NewRoot, destination);
            }
        });
    }
}
