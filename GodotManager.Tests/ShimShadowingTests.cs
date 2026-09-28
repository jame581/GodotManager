using GodotManager.Domain;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// Covers detection of a machine-wide shim that outranks a user-scope activation.
/// </summary>
public class ShimShadowingTests
{
    private const string GlobalShimDir = @"C:\Program Files\godman\bin";
    private const string MachinePath = @"C:\Windows\system32;C:\Program Files\godman\bin;C:\Windows";

    [Fact]
    public void WouldShadow_UserScopeWithAGlobalShimOnMachinePath_IsTrue()
    {
        // The observed failure: a global shim left behind by an earlier global
        // activation kept `godot` pointing at that install even after a user-scope
        // install was activated, because Machine PATH is searched first.
        Assert.True(ShimShadowing.WouldShadow(
            InstallScope.User, globalShimExists: true, GlobalShimDir, MachinePath));
    }

    [Fact]
    public void WouldShadow_GlobalScopeActivation_IsFalse()
    {
        // Activating globally writes that very shim -- it is the intended winner.
        Assert.False(ShimShadowing.WouldShadow(
            InstallScope.Global, globalShimExists: true, GlobalShimDir, MachinePath));
    }

    [Fact]
    public void WouldShadow_WhenNoGlobalShimExists_IsFalse()
    {
        Assert.False(ShimShadowing.WouldShadow(
            InstallScope.User, globalShimExists: false, GlobalShimDir, MachinePath));
    }

    [Fact]
    public void WouldShadow_WhenTheShimDirectoryIsNotOnMachinePath_IsFalse()
    {
        // Present on disk but unreachable, so it cannot win a PATH lookup.
        Assert.False(ShimShadowing.WouldShadow(
            InstallScope.User, globalShimExists: true, GlobalShimDir, @"C:\Windows\system32;C:\Windows"));
    }

    [Theory]
    [InlineData(@"C:\Windows;C:\Program Files\godman\bin\")]
    [InlineData(@"C:\Windows; C:\Program Files\godman\bin ")]
    [InlineData(@"C:\Windows;c:\program files\godman\BIN")]
    public void WouldShadow_ToleratesTrailingSeparatorsWhitespaceAndCase(string machinePath)
    {
        // PATH is hand-edited by users and installers alike; a comparison that
        // missed these would silently stop warning in exactly the messy setups
        // where the warning matters most.
        Assert.True(ShimShadowing.WouldShadow(
            InstallScope.User, globalShimExists: true, GlobalShimDir, machinePath));
    }

    [Fact]
    public void WouldShadow_WithNoMachinePath_IsFalse()
    {
        Assert.False(ShimShadowing.WouldShadow(
            InstallScope.User, globalShimExists: true, GlobalShimDir, null));
    }

    [Fact]
    public void BuildWarning_NamesTheShimFileAndAWayOut()
    {
        var warning = ShimShadowing.BuildWarning(@"C:\Program Files\godman\bin\godot.cmd");

        Assert.Contains(@"C:\Program Files\godman\bin\godot.cmd", warning);
        Assert.Contains("elevated", warning);
    }

    // --- GetWarning, Linux branch -------------------------------------------------
    // The shared helper all four front-ends (CLI activate/install, TUI activate/install
    // dialog) call. On Linux an unprivileged RemoveUnix cannot delete the global shim
    // (EACCES), which then outranks the user shim wherever /usr/local/bin comes first.

    private static string CreateGlobalShim(GodmanTestFixture fixture)
    {
        var dir = fixture.Paths.GetShimDirectory(InstallScope.Global);
        Directory.CreateDirectory(dir);
        var shim = Path.Combine(dir, "godot");
        File.WriteAllText(shim, "#!/bin/sh\n");
        return shim;
    }

    [Fact]
    public void GetWarning_Linux_UserActivationWithASurvivingGlobalShim_NamesItAndTheRemedy()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new GodmanTestFixture();
        var shim = CreateGlobalShim(fixture);

        var warning = ShimShadowing.GetWarning(fixture.Paths, InstallScope.User);

        Assert.NotNull(warning);
        Assert.Contains(shim, warning);
        Assert.Contains($"sudo rm {shim}", warning);
        // The actual global shim directory, not a hardcoded /usr/local/bin: under a
        // GODMAN_GLOBAL_ROOT prefix (as here) that would name the wrong directory.
        Assert.Contains($"wherever {Path.GetDirectoryName(shim)} comes before ~/.local/bin", warning);
        Assert.DoesNotContain("/usr/local/bin", warning);
    }

    [Fact]
    public void GetWarning_Linux_UserActivationWithNoGlobalShim_IsNull()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new GodmanTestFixture();

        Assert.Null(ShimShadowing.GetWarning(fixture.Paths, InstallScope.User));
    }

    [Fact]
    public void GetWarning_Linux_GlobalActivation_IsNull()
    {
        // A global activation just wrote that very shim -- it is the intended winner.
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new GodmanTestFixture();
        CreateGlobalShim(fixture);

        Assert.Null(ShimShadowing.GetWarning(fixture.Paths, InstallScope.Global));
    }

    [Fact]
    public void GetWarning_Linux_IgnoresAWindowsStyleShimName()
    {
        // Only `godot` resolves on Linux; a stray godot.cmd shadows nothing.
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new GodmanTestFixture();
        var dir = fixture.Paths.GetShimDirectory(InstallScope.Global);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "godot.cmd"), "");

        Assert.Null(ShimShadowing.GetWarning(fixture.Paths, InstallScope.User));
    }
}
