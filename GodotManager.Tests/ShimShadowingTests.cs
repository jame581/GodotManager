using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Infrastructure;
using GodotManager.Services;
using GodotManager.Tests.Helpers;
using Spectre.Console;
using Spectre.Console.Testing;
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

    [Fact]
    public void BuildUnixWarning_QuotesTheShimPathInTheRemedy()
    {
        // The remedy is meant to be pasted: an unquoted path with a space would make
        // `sudo rm` act on two wrong paths.
        var warning = ShimShadowing.BuildUnixWarning("/opt/my godot/bin/godot");

        Assert.Contains("`sudo rm '/opt/my godot/bin/godot'`", warning);
    }

    [Fact]
    public void GetDeactivateWarning_QuotesTheShimPathInTheRemedy()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new GodmanTestFixture(globalRoot: "my global");
        var shim = CreateGlobalShim(fixture);

        var warning = ShimShadowing.GetDeactivateWarning(fixture.Paths, InstallScope.Global);

        Assert.Contains($"`sudo rm '{shim}'`", warning);
    }

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
        // Conditional: the shim may be a live machine-wide activation other users rely on.
        Assert.Contains($"If it is a leftover from an earlier global activation, remove it with `sudo rm {shim}`", warning);
        Assert.Contains("if other users rely on the machine-wide install, leave it.", warning);
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
    // --- A failed probe is best-effort noise: --verbose only (issue #5) ---

    private static string RunWarn(bool verbose, Func<AppPaths, InstallScope, Action<string>?, string?> probe)
    {
        using var fixture = new GodmanTestFixture();
        var console = new TestConsole();
        var original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            ActivateCommand.WarnIfShadowedByGlobalShim(
                fixture.Paths, InstallScope.User, new DiagnosticContext { Verbose = verbose }, probe);
            return console.Output;
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    private static string? FailingProbe(AppPaths paths, InstallScope scope, Action<string>? onFailure)
    {
        onFailure?.Invoke("Could not check for a shadowing global shim: boom");
        return null;
    }

    [Fact]
    public void WarnIfShadowedByGlobalShim_ProbeFailure_IsSilentWithoutVerbose()
    {
        Assert.Equal(string.Empty, RunWarn(verbose: false, FailingProbe));
    }

    [Fact]
    public void WarnIfShadowedByGlobalShim_ProbeFailure_WarnsUnderVerbose()
    {
        var output = RunWarn(verbose: true, FailingProbe);

        Assert.Contains("warn:", output);
        Assert.Contains("Could not check for a shadowing global shim", output);
    }

    [Fact]
    public void WarnIfShadowedByGlobalShim_FoundShadow_IsPrintedWithoutVerbose()
    {
        // Only the *failure to check* is verbose-gated; a shim that does outrank the
        // activation is the whole point of the command and always shows.
        var output = RunWarn(verbose: false, (_, _, _) => "a global shim outranks this activation");

        Assert.Contains("a global shim outranks this activation", output);
    }
}
