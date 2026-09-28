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

            // Not pinned to a release number, so it survives version bumps. What it guards
            // is the source: the entry assembly under the test host is the test runner, so
            // GetEntryAssembly() would print the runner's version and still look plausible.
            // Compared against the godman assembly's version read independently, from the
            // file on disk rather than through the loaded type the command itself uses.
            var version = GodotManager.Commands.VersionCommand.GodmanVersion;
            Assert.Matches(@"^\d+\.\d+\.\d+$", version);
            var godmanAssemblyFile = typeof(GodotManager.Commands.VersionCommand).Assembly.Location;
            Assert.Equal(
                System.Reflection.AssemblyName.GetAssemblyName(godmanAssemblyFile).Version!.ToString(3),
                version);
            Assert.NotSame(typeof(GodotManager.Commands.VersionCommand).Assembly, System.Reflection.Assembly.GetEntryAssembly());
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }
}
