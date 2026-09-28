using GodotManager.Tests.Helpers;
using Spectre.Console;
using System;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class VersionCommandTests : IDisposable
{
    private readonly GodmanTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Version_PrintsGodmanRuntimeAndOs()
    {
        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["version"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("godman", result.Output);
            Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, result.Output);
            Assert.Contains($"godman {GodotManager.Commands.VersionCommand.GodmanVersion}", result.Output);
            Assert.StartsWith("1.4", GodotManager.Commands.VersionCommand.GodmanVersion);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }
}
