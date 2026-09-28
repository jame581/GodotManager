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
    // target, previous active, writable, expected
    [InlineData(InstallScope.User, null, false, false)]
    [InlineData(InstallScope.User, null, true, false)]
    [InlineData(InstallScope.User, InstallScope.User, false, false)]
    [InlineData(InstallScope.Global, null, true, false)]
    [InlineData(InstallScope.Global, null, false, true)]
    [InlineData(InstallScope.Global, InstallScope.User, false, true)]
    [InlineData(InstallScope.Global, InstallScope.Global, false, true)]
    [InlineData(InstallScope.Global, InstallScope.Global, true, false)]
    // Switching away from an active global install touches the global shim even when the
    // target is user-scope -- the clause that is easy to drop (see ElevatedActivator).
    [InlineData(InstallScope.User, InstallScope.Global, false, true)]
    [InlineData(InstallScope.User, InstallScope.Global, true, false)]
    public void MustStop_OnlyWhenMachineStateIsTouchedAndNotWritable(
        InstallScope target, InstallScope? previousActive, bool globalWritable, bool expected)
    {
        Assert.Equal(expected, LinuxElevation.MustStop(target, previousActive, globalWritable));
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
            var denied = LinuxElevation.Check(fixture.Paths, InstallScope.Global, null, ["remove", "abc", "--delete"]);

            Assert.NotNull(denied);
            Assert.Contains(root, denied!.Message);
            Assert.Contains("sudo GODMAN_GLOBAL_ROOT=", denied.Hint);
            Assert.EndsWith(" remove abc --delete", denied.Hint);
            Assert.DoesNotContain("<same arguments>", denied.Hint);

            // A user-scope operation never probes -- and never stops.
            Assert.Null(LinuxElevation.Check(fixture.Paths, InstallScope.User, InstallScope.User, ["activate", "x"]));
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

        Assert.Null(LinuxElevation.Check(fixture.Paths, InstallScope.Global, InstallScope.Global, ["remove", "x"]));
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
}
