using System.Linq;
using GodotManager.Infrastructure;
using Xunit;

namespace GodotManager.Tests;

public class CliArgsNormalizerTests
{
    [Fact]
    public void Normalize_WithShortVersionFlag_MapsToVersionCommand()
    {
        var result = CliArgsNormalizer.Normalize(["-v"]);
        Assert.True(result.SequenceEqual(["version"]));
    }

    [Fact]
    public void Normalize_WithLongVersionFlag_MapsToVersionCommand()
    {
        var result = CliArgsNormalizer.Normalize(["--version"]);
        Assert.True(result.SequenceEqual(["version"]));
    }

    [Fact]
    public void Normalize_WithWindowsShortVersionFlag_MapsToVersionCommand()
    {
        var result = CliArgsNormalizer.Normalize(["/v"]);
        Assert.True(result.SequenceEqual(["version"]));
    }

    [Fact]
    public void Normalize_WithShorthandCommand_MapsToCommandName()
    {
        var result = CliArgsNormalizer.Normalize(["--list"]);
        Assert.True(result.SequenceEqual(["list"]));
    }

    [Fact]
    public void Normalize_WithRegularArgs_ReturnsSameArguments()
    {
        var input = new[] { "install", "--version", "4.5.1" };
        var result = CliArgsNormalizer.Normalize(input);
        Assert.Equal(input, result);
    }
}