using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class RemoveCommandTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public RemoveCommandTests()
    {
        _fixture = new GodmanTestFixture();
    }

    [Fact]
    public async Task ExecuteAsync_RemovesInstallFromRegistry()
    {
        // Arrange
        var installPath = Path.Combine(_fixture.TempRoot, "Godot_v4.5.1-stable_win64.exe");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);

        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["remove", entry.Id.ToString()]);

        // Assert
        Assert.Equal(0, result.ExitCode);

        var updatedRegistry = await _fixture.Registry.LoadAsync();
        Assert.Empty(updatedRegistry.Installs);
        Assert.True(Directory.Exists(installPath), "Files should not be deleted without --delete flag");
    }

    [Fact]
    public async Task ExecuteAsync_WithDeleteFiles_RemovesDirectory()
    {
        // Arrange
        var installPath = Path.Combine(_fixture.TempRoot, "Godot_v4.5.1-stable_win64.exe");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "test.txt"), "test");

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);

        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["remove", entry.Id.ToString(), "--delete"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(installPath), "Directory should be deleted with --delete flag");
    }

    [Fact]
    public async Task ExecuteAsync_RemovingActiveInstall_Deactivates()
    {
        // Arrange
        var installPath = Path.Combine(_fixture.TempRoot, "Godot_v4.5.1-stable_win64.exe");
        Directory.CreateDirectory(installPath);

        // Create fake executable for deactivation
        var exeName = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        File.WriteAllText(Path.Combine(installPath, exeName), "fake");

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);

        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["remove", entry.Id.ToString()]);

        // Assert
        Assert.Equal(0, result.ExitCode);

        var updatedRegistry = await _fixture.Registry.LoadAsync();
        Assert.Null(updatedRegistry.ActiveId);
        Assert.Empty(updatedRegistry.Installs);
    }

    [Fact]
    public async Task ExecuteAsync_WithNonExistentId_ReturnsError()
    {
        // Arrange
        var registry = new InstallRegistry();
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["remove", Guid.NewGuid().ToString()]);

        // Assert
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_RemovingNonActiveInstall_KeepsActive()
    {
        // Arrange
        var installPath1 = Path.Combine(_fixture.TempRoot, "Godot_v4.5.1-stable_win64.exe");
        var installPath2 = Path.Combine(_fixture.TempRoot, "Godot_v4.4.0-stable_win64.exe");
        Directory.CreateDirectory(installPath1);
        Directory.CreateDirectory(installPath2);

        var exeName1 = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        File.WriteAllText(Path.Combine(installPath1, exeName1), "fake");

        var registry = new InstallRegistry();
        var entry1 = InstallEntryFactory.Create(version: "4.5.1", path: installPath1);

        var entry2 = InstallEntryFactory.Create(version: "4.4.0", path: installPath2);

        registry.Installs.Add(entry1);
        registry.Installs.Add(entry2);
        registry.MarkActive(entry1.Id);
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["remove", entry2.Id.ToString()]);

        // Assert
        Assert.Equal(0, result.ExitCode);

        var updatedRegistry = await _fixture.Registry.LoadAsync();
        Assert.Equal(entry1.Id, updatedRegistry.ActiveId);
        Assert.Single(updatedRegistry.Installs);
    }

    [Fact]
    public async Task ExecuteAsync_WithDryRun_DoesNotModifyRegistry()
    {
        // Arrange
        var installPath = Path.Combine(_fixture.TempRoot, "Godot_v4.5.1-stable_win64.exe");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "test.txt"), "test");

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);

        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["remove", entry.Id.ToString(), "--delete", "--dry-run"]);

        // Assert
        Assert.Equal(0, result.ExitCode);

        var updatedRegistry = await _fixture.Registry.LoadAsync();
        Assert.Single(updatedRegistry.Installs);
        Assert.Equal(entry.Id, updatedRegistry.ActiveId);
        Assert.True(Directory.Exists(installPath), "Files should not be deleted during dry run");
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
