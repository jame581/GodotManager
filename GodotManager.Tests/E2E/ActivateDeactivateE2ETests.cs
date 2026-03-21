using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

public class ActivateDeactivateE2ETests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public ActivateDeactivateE2ETests() => _fixture = new GodmanTestFixture();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Activate_ValidId_ExitsZeroAndSetsActive()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);

        var exeName = OperatingSystem.IsWindows()
            ? "Godot_v4.5.1-stable_win64.exe"
            : "Godot_v4.5.1-stable_linux.x86_64";
        File.WriteAllText(Path.Combine(installPath, exeName), "fake");

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync("activate", entry.Id.ToString());

        Assert.Equal(0, result.ExitCode);

        var updated = await _fixture.Registry.LoadAsync();
        Assert.Equal(entry.Id, updated.ActiveId);
    }

    [Fact]
    public async Task Activate_InvalidId_ExitsNonZero()
    {
        var app = CliTestHarness.Create(_fixture);
        await _fixture.Registry.SaveAsync(new InstallRegistry());

        var result = await app.RunAsync("activate", Guid.NewGuid().ToString());

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Activate_DryRun_DoesNotModifyRegistry()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync("activate", entry.Id.ToString(), "--dry-run");

        Assert.Equal(0, result.ExitCode);

        var updated = await _fixture.Registry.LoadAsync();
        Assert.Null(updated.ActiveId);
    }

    [Fact]
    public async Task Deactivate_WithActiveInstall_ExitsZeroAndClears()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync("deactivate");

        Assert.Equal(0, result.ExitCode);

        var updated = await _fixture.Registry.LoadAsync();
        Assert.Null(updated.ActiveId);
    }

    [Fact]
    public async Task Deactivate_WithNoActive_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture);
        await _fixture.Registry.SaveAsync(new InstallRegistry());

        var result = await app.RunAsync("deactivate");

        Assert.Equal(0, result.ExitCode);
    }
}
