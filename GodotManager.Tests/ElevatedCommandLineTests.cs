using GodotManager.Infrastructure;
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
}
