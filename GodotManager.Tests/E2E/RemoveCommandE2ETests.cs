using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

public class RemoveCommandE2ETests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public RemoveCommandE2ETests() => _fixture = new GodmanTestFixture();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Remove_ValidId_ExitsZeroAndRemovesEntry()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync("remove", entry.Id.ToString());

        Assert.Equal(0, result.ExitCode);

        var updated = await _fixture.Registry.LoadAsync();
        Assert.Empty(updated.Installs);
    }

    [Fact]
    public async Task Remove_WithDelete_DeletesFiles()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "godot"), "fake");

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync("remove", entry.Id.ToString(), "--delete");

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(installPath));
    }

    [Fact]
    public async Task Remove_NonexistentId_ExitsNonZero()
    {
        var app = CliTestHarness.Create(_fixture);
        await _fixture.Registry.SaveAsync(new InstallRegistry());

        var result = await app.RunAsync("remove", Guid.NewGuid().ToString());

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Remove_DryRun_DoesNotModifyRegistry()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync("remove", entry.Id.ToString(), "--dry-run");

        Assert.Equal(0, result.ExitCode);

        var updated = await _fixture.Registry.LoadAsync();
        Assert.Single(updated.Installs);
    }
}
