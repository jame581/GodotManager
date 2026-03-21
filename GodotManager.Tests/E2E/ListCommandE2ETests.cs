using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

public class ListCommandE2ETests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public ListCommandE2ETests() => _fixture = new GodmanTestFixture();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task List_WithEmptyRegistry_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture);
        await _fixture.Registry.SaveAsync(new InstallRegistry());

        var result = await app.RunAsync("list");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task List_WithInstalls_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture);
        var registry = new InstallRegistry();
        registry.Installs.Add(InstallEntryFactory.Create(
            version: "4.5.1", path: Path.Combine(_fixture.TempRoot, "g451")));
        registry.Installs.Add(InstallEntryFactory.Create(
            version: "4.4.0", path: Path.Combine(_fixture.TempRoot, "g440")));
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync("list");

        Assert.Equal(0, result.ExitCode);
    }
}
