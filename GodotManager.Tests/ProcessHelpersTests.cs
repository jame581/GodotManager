using GodotManager.Infrastructure;
using Xunit;

namespace GodotManager.Tests;

public class ProcessHelpersTests
{
    [Fact]
    public void QuoteArg_SimpleArg_ReturnsUnchanged()
    {
        Assert.Equal("hello", ProcessHelpers.QuoteArg("hello"));
    }

    [Fact]
    public void QuoteArg_ArgWithSpaces_ReturnsQuoted()
    {
        Assert.Equal("\"hello world\"", ProcessHelpers.QuoteArg("hello world"));
    }

    [Fact]
    public void QuoteArg_ArgWithQuotes_EscapesAndQuotes()
    {
        Assert.Equal("\"say \\\"hi\\\"\"", ProcessHelpers.QuoteArg("say \"hi\""));
    }

    [Fact]
    public void QuoteArg_EmptyString_ReturnsQuoted()
    {
        Assert.Equal("\"\"", ProcessHelpers.QuoteArg(""));
    }

    [Fact]
    public void QuoteArg_WhitespaceOnly_ReturnsQuoted()
    {
        Assert.Equal("\" \"", ProcessHelpers.QuoteArg(" "));
    }
}
