using GodotManager.Infrastructure;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using Xunit;

namespace GodotManager.Tests;

public class ElevatedCommandLineTests
{
    [Fact]
    public void BinaryOutsideSecurePath_IsNamedByItsFullPath()
    {
        // ~/.local/bin is install.sh's default and is never on sudo's secure_path, so
        // `sudo godman` would be "command not found" there.
        Assert.Equal(
            "sudo /home/jan/.local/bin/godman list",
            ElevatedCommandLine.Render("list", "/home/jan/.local/bin/godman", windows: false));
    }

    [Theory]
    [InlineData("/usr/bin/godman")]
    [InlineData("/usr/sbin/godman")]
    [InlineData("/sbin/godman")]
    [InlineData("/bin/godman")]
    public void BinaryOnEveryDefaultSecurePath_UsesTheBareName(string processPath)
    {
        Assert.Equal("sudo godman doctor", ElevatedCommandLine.Render("doctor", processPath, windows: false));
    }

    [Theory]
    [InlineData("/usr/local/bin/godman")]
    [InlineData("/usr/local/sbin/godman")]
    public void BinaryInUsrLocal_IsNamedByItsFullPath(string processPath)
    {
        // RHEL-family secure_path leaves /usr/local/* off, so `sudo godman` would not
        // resolve there; a full path works everywhere.
        Assert.Equal($"sudo {processPath} doctor", ElevatedCommandLine.Render("doctor", processPath, windows: false));
    }

    [Fact]
    public void PathWithSpaces_IsQuotedForTheShell()
    {
        Assert.Equal(
            "sudo '/home/j an/bin/godman' list",
            ElevatedCommandLine.Render("list", "/home/j an/bin/godman", windows: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/usr/lib64/dotnet/dotnet")]
    public void UnknownOrDotnetHost_FallsBackToTheBareName(string? processPath)
    {
        Assert.Equal("sudo godman list", ElevatedCommandLine.Render("list", processPath, windows: false));
    }

    [Fact]
    public void Windows_HasNoSudoPrefix()
    {
        Assert.Equal("godman list", ElevatedCommandLine.Render("list", @"C:\Tools\godman.exe", windows: true));
    }

    // --- Passing GODMAN_GLOBAL_ROOT through sudo (review 3 follow-up, H3) ---

    [Fact]
    public void WithAnEnvironmentOverride_PassesItThroughSudo()
    {
        // sudo's env_reset drops GODMAN_GLOBAL_ROOT, so a plain `sudo godman list` would
        // migrate the default /usr/local instead of the user's prefix.
        Assert.Equal(
            "sudo GODMAN_GLOBAL_ROOT=/opt/godot /home/jan/.local/bin/godman list",
            ElevatedCommandLine.Render("list", ("GODMAN_GLOBAL_ROOT", "/opt/godot"), "/home/jan/.local/bin/godman", windows: false));
        Assert.Equal(
            "sudo GODMAN_GLOBAL_ROOT=/opt/godot godman list",
            ElevatedCommandLine.Render("list", ("GODMAN_GLOBAL_ROOT", "/opt/godot"), "/usr/bin/godman", windows: false));
    }

    [Fact]
    public void WithAnEnvironmentOverride_QuotesAValueTheShellWouldSplit()
    {
        Assert.Equal(
            "sudo GODMAN_GLOBAL_ROOT='/opt/my godot' /usr/local/bin/godman list",
            ElevatedCommandLine.Render("list", ("GODMAN_GLOBAL_ROOT", "/opt/my godot"), "/usr/local/bin/godman", windows: false));
    }

    [Fact]
    public void WithoutAnEnvironmentOverride_IsUnchanged_AndWindowsIgnoresIt()
    {
        Assert.Equal(
            ElevatedCommandLine.Render("list", "/home/jan/.local/bin/godman", windows: false),
            ElevatedCommandLine.Render("list", null, "/home/jan/.local/bin/godman", windows: false));
        Assert.Equal(
            "godman list",
            ElevatedCommandLine.Render("list", ("GODMAN_GLOBAL_ROOT", @"D:\Apps"), @"C:\Tools\godman.exe", windows: true));
    }

    [Fact]
    public void Render_CarriesARootedGlobalRootOverride_AndOnlyThatOne()
    {
        // Every elevated command godman prints goes through Render, so the override rides
        // along everywhere, not just in the callers that remembered it.
        using var fixture = new GodmanTestFixture();
        var prefix = Path.Combine(fixture.TempRoot, "global");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("godman list", ElevatedCommandLine.Render("list"));
            return;
        }

        Assert.StartsWith($"sudo GODMAN_GLOBAL_ROOT={prefix} ", ElevatedCommandLine.Render("list"));

        Environment.SetEnvironmentVariable("GODMAN_GLOBAL_ROOT", "rel");   // relative: never acted on
        Assert.DoesNotContain("GLOBAL_ROOT", ElevatedCommandLine.Render("list"));

        Environment.SetEnvironmentVariable("GODMAN_GLOBAL_ROOT", null);
        Environment.SetEnvironmentVariable("GODOT_MANAGER_GLOBAL_ROOT", "/opt/legacy");
        Assert.StartsWith("sudo GODOT_MANAGER_GLOBAL_ROOT=/opt/legacy ", ElevatedCommandLine.Render("list"));

        Environment.SetEnvironmentVariable("GODOT_MANAGER_GLOBAL_ROOT", null);
        Assert.DoesNotContain("GLOBAL_ROOT", ElevatedCommandLine.Render("list"));
    }

    [Theory]
    [InlineData("/opt/a(b)")]
    [InlineData("/opt/a;b")]
    [InlineData("/opt/a&b")]
    [InlineData("/opt/*")]
    [InlineData("~/godot")]
    [InlineData("/opt/a|b")]
    [InlineData("/opt/a#b")]
    [InlineData("/opt/a!b")]
    [InlineData("/opt/[ab]")]
    [InlineData("/opt/{a,b}")]
    [InlineData("/opt/a<b>")]
    public void Quote_AnyShellMetacharacter_IsQuoted(string value)
    {
        Assert.Equal(
            $"sudo GODMAN_GLOBAL_ROOT='{value}' /usr/local/bin/godman list",
            ElevatedCommandLine.Render("list", ("GODMAN_GLOBAL_ROOT", value), "/usr/local/bin/godman", windows: false));
    }

    [Fact]
    public void Quote_EscapesASingleQuote_AndLeavesSafeValuesAlone()
    {
        Assert.Equal(
            "sudo GODMAN_GLOBAL_ROOT='/opt/it'\\''s' /usr/local/bin/godman list",
            ElevatedCommandLine.Render("list", ("GODMAN_GLOBAL_ROOT", "/opt/it's"), "/usr/local/bin/godman", windows: false));
        Assert.Equal(
            "sudo GODMAN_GLOBAL_ROOT=/opt/godot-4.5_x+y,z@h:1%=2 /usr/local/bin/godman list",
            ElevatedCommandLine.Render("list", ("GODMAN_GLOBAL_ROOT", "/opt/godot-4.5_x+y,z@h:1%=2"), "/usr/local/bin/godman", windows: false));
        Assert.Equal(
            "sudo '/home/j(1)/bin/godman' list",
            ElevatedCommandLine.Render("list", "/home/j(1)/bin/godman", windows: false));
    }
}
