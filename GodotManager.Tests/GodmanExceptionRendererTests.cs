using GodotManager.Infrastructure;
using Spectre.Console;
using Spectre.Console.Testing;
using System;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// Covers GodmanExceptionRenderer.Render directly against a TestConsole, rather
/// than through a full command. RemoveCommand, DeactivateCommand and
/// ActivateCommand all route their failure output through this one shared
/// method (rather than a re-inlined copy of the "prefix + hint" markup), so
/// pinning its exact output here is what actually protects every call site: a
/// regression here would silently break the hint line in all three commands at
/// once.
/// </summary>
public class GodmanExceptionRendererTests
{
    [Fact]
    public void Render_WithGodmanExceptionHint_PrintsPrefixMessageAndHintLine()
    {
        var console = new TestConsole();
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            var ex = new GodmanException("Failed to update the machine-wide registry", "Re-run with sudo.");

            var exitCode = GodmanExceptionRenderer.Render("Remove failed:", ex);

            Assert.Equal(-1, exitCode);
            Assert.Contains("Remove failed:", console.Output);
            Assert.Contains("Failed to update the machine-wide registry", console.Output);
            Assert.Contains("hint:", console.Output);
            Assert.Contains("Re-run with sudo.", console.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public void Render_WithGodmanExceptionWithoutHint_OmitsHintLine()
    {
        var console = new TestConsole();
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            var ex = new GodmanException("something went wrong");

            GodmanExceptionRenderer.Render("Deactivate failed:", ex);

            Assert.Contains("Deactivate failed:", console.Output);
            Assert.Contains("something went wrong", console.Output);
            Assert.DoesNotContain("hint:", console.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public void Render_WithPlainException_OmitsHintLine()
    {
        var console = new TestConsole();
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            var ex = new InvalidOperationException("no active install");

            GodmanExceptionRenderer.Render("Deactivate failed:", ex);

            Assert.Contains("Deactivate failed:", console.Output);
            Assert.Contains("no active install", console.Output);
            Assert.DoesNotContain("hint:", console.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public void Render_WithMessageAndExplicitHint_PrintsBoth()
    {
        // The overload ActivateCommand uses for its UnauthorizedAccessException/
        // SecurityException catches, which have no GodmanException to pull a Hint
        // from -- the hint is supplied directly instead.
        var console = new TestConsole();
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            GodmanExceptionRenderer.Render(
                "Activation failed:",
                "Access denied while updating environment for this scope.",
                GodmanException.ElevationHint);

            Assert.Contains("Activation failed:", console.Output);
            Assert.Contains("Access denied while updating environment for this scope.", console.Output);
            Assert.Contains("hint:", console.Output);
            Assert.Contains(OperatingSystem.IsWindows() ? "administrator" : "sudo", console.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }
}
