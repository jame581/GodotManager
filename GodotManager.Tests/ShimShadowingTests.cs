using GodotManager.Domain;
using GodotManager.Services;
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
}
