using GodotManager.Domain;
using GodotManager.Infrastructure;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// The Linux up-front elevation decision: the pure rule (<see cref="LinuxElevation.MustStop"/>)
/// and the writability probe it is fed by. Found in manual testing: an unprivileged
/// <c>remove --delete</c> of a global entry tried to delete the install's files before the
/// registry write failed, so nothing on Linux decided before the first machine-wide write.
/// </summary>
public class LinuxElevationTests
{
    [Theory]
    [InlineData(InstallScope.User, false, false)]
    [InlineData(InstallScope.User, true, false)]
    [InlineData(InstallScope.Global, true, false)]
    [InlineData(InstallScope.Global, false, true)]
    public void MustStop_OnlyForAGlobalTargetThatIsNotWritable(InstallScope target, bool globalWritable, bool expected)
    {
        // Unlike Windows (ElevatedActivator.TouchesMachineState), the previously active
        // install does not count: see LinuxElevation.MustStop.
        Assert.Equal(expected, LinuxElevation.MustStop(target, globalWritable));
    }

    [Fact]
    public void IsWritable_WritableDirectory_IsTrue()
    {
        using var fixture = new GodmanTestFixture();
        var dir = Path.Combine(fixture.TempRoot, "w");
        Directory.CreateDirectory(dir);

        Assert.True(LinuxElevation.IsWritable(dir));
        Assert.Empty(Directory.GetFileSystemEntries(dir)); // the probe leaves nothing behind
    }

    [Fact]
    public void IsWritable_NonexistentPathUnderWritableParent_IsTrue()
    {
        using var fixture = new GodmanTestFixture();

        Assert.True(LinuxElevation.IsWritable(Path.Combine(fixture.TempRoot, "missing", "deeper")));
        Assert.False(Directory.Exists(Path.Combine(fixture.TempRoot, "missing"))); // probing creates nothing
    }

    [Fact]
    public void IsWritable_ReadOnlyDirectory_IsFalse()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return; // root bypasses mode bits
        using var fixture = new GodmanTestFixture();
        var dir = Path.Combine(fixture.TempRoot, "ro");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, (UnixFileMode)Convert.ToInt32("555", 8));
        try
        {
            Assert.False(LinuxElevation.IsWritable(dir));
        }
        finally
        {
            File.SetUnixFileMode(dir, (UnixFileMode)Convert.ToInt32("755", 8));
        }
    }

    [Fact]
    public void IsWritable_NonexistentPathUnderReadOnlyParent_IsFalse()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return; // root bypasses mode bits
        using var fixture = new GodmanTestFixture();
        var dir = Path.Combine(fixture.TempRoot, "ro");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, (UnixFileMode)Convert.ToInt32("555", 8));
        try
        {
            Assert.False(LinuxElevation.IsWritable(Path.Combine(dir, "lib", "godman")));
        }
        finally
        {
            File.SetUnixFileMode(dir, (UnixFileMode)Convert.ToInt32("755", 8));
        }
    }

    [Fact]
    public void Check_GlobalRootReadOnly_ReturnsHintNamingTheRealArguments()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        using var fixture = new GodmanTestFixture();
        var root = fixture.Paths.GetInstallRoot(InstallScope.Global);
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, (UnixFileMode)Convert.ToInt32("555", 8));
        try
        {
            var denied = LinuxElevation.Check(fixture.Paths, InstallScope.Global, ["remove", "abc", "--delete"]);

            Assert.NotNull(denied);
            Assert.Contains(root, denied!.Message);
            Assert.Contains("sudo GODMAN_GLOBAL_ROOT=", denied.Hint);
            Assert.EndsWith(" remove abc --delete", denied.Hint);
            Assert.DoesNotContain("<same arguments>", denied.Hint);

            // A user-scope operation never probes -- and never stops.
            Assert.Null(LinuxElevation.Check(fixture.Paths, InstallScope.User, ["activate", "x"]));
        }
        finally
        {
            File.SetUnixFileMode(root, (UnixFileMode)Convert.ToInt32("755", 8));
        }
    }

    [Fact]
    public void Check_WritableGlobalRoot_DoesNotStop()
    {
        // The test suite's own global-scope operations run unprivileged inside the fixture's
        // writable temp root: a uid check would stop every one of them.
        using var fixture = new GodmanTestFixture();

        Assert.Null(LinuxElevation.Check(fixture.Paths, InstallScope.Global, ["remove", "x"]));
    }

    [Fact]
    public void Check_WithAPre140GlobalRootOverride_StopsEveryGlobalTarget()
    {
        // A 1.3.0 value (the shim directory) read as a prefix points at an empty <value>/lib/godman.
        // A global write there would create a fresh registry, after which godman no longer
        // recognises the old layout: the warning stops and the installs in <value>/godman drop
        // out of sight. Writability says nothing about this, so it must not be what decides.
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new GodmanTestFixture(globalRoot: Path.Combine("opt", "bin"), seed: value =>
        {
            Directory.CreateDirectory(Path.Combine(value, "godman", "4.5.1-standard-linux-global"));
            File.WriteAllText(Path.Combine(value, "godman", "installs.json"), "{\"Installs\":[]}");
        });
        Assert.NotNull(fixture.Paths.LegacyGlobalRootOverrideWarning); // precondition

        var denied = LinuxElevation.Check(fixture.Paths, InstallScope.Global, ["install", "--scope", "Global"]);

        Assert.NotNull(denied);
        Assert.Contains(fixture.Paths.LegacyGlobalRootOverrideWarning!, denied!.Message);
        Assert.DoesNotContain("sudo", denied.Hint ?? ""); // sudo does not help: the value is wrong
        Assert.Null(LinuxElevation.Check(fixture.Paths, InstallScope.User, ["install"]));
    }

    [Fact]
    public void ElevationHintFor_QuotesEachArgument_AndFallsBackWithoutArguments()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new GodmanTestFixture();

        var hint = GodmanException.ElevationHintFor(["install", "--path", "/opt/my godot"]);
        Assert.EndsWith(" install --path '/opt/my godot'", hint);

        Assert.Equal(GodmanException.ElevationHint, GodmanException.ElevationHintFor(null));
        Assert.Equal(GodmanException.ElevationHint, GodmanException.ElevationHintFor([]));
    }

    [Fact]
    public void IsFileWritable_ReadOnlyFile_IsFalse_AndDoesNotTruncate()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        using var fixture = new GodmanTestFixture();
        var file = Path.Combine(fixture.TempRoot, "installs.json");
        File.WriteAllText(file, "{}");

        Assert.True(LinuxElevation.IsFileWritable(file));
        Assert.Equal("{}", File.ReadAllText(file));

        File.SetUnixFileMode(file, UnixFileMode.UserRead);
        try
        {
            Assert.False(LinuxElevation.IsFileWritable(file));
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void WithArguments_ReplacesOnlyThePlaceholderElevationHint()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new GodmanTestFixture();
        var late = new GodmanException("Failed to update the machine-wide registry", GodmanException.ElevationHint);

        var rewritten = late.WithArguments(["remove", "abc", "--delete"]);
        Assert.Equal(late.Message, rewritten.Message);
        Assert.EndsWith(" remove abc --delete", rewritten.Hint);

        var other = new GodmanException("x", "try --force");
        Assert.Same(other, other.WithArguments(["remove"]));
    }

    [Fact]
    public void CheckClean_Hint_KeepsTheCommandAloneOnItsLine_AndSaysWhatSudoCleans()
    {
        // The addendum used to run straight on after the command ("… clean --yes That
        // cleans …"), so copying to the end of the line pasted extra words into the shell.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        using var fixture = new GodmanTestFixture();
        var root = fixture.Paths.GetInstallRoot(InstallScope.Global);
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, (UnixFileMode)Convert.ToInt32("555", 8));
        try
        {
            var denied = LinuxElevation.CheckClean(fixture.Paths, fixture.Launcher, ["clean", "--yes"]);

            Assert.NotNull(denied);
            var lines = denied!.Hint!.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.EndsWith(" clean --yes", lines[0]);
            Assert.Contains("root's own user-scope godman files", lines[1]);
            Assert.Contains("without sudo", lines[1]);
        }
        finally
        {
            File.SetUnixFileMode(root, (UnixFileMode)Convert.ToInt32("755", 8));
        }
    }
}
