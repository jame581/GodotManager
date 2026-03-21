using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class DeactivateCommandTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;
    private readonly DeactivateCommand _command;

    public DeactivateCommandTests()
    {
        _fixture = new GodmanTestFixture();
        _command = new DeactivateCommand(_fixture.Registry, _fixture.Environment);
    }

    [Fact]
    public async Task ExecuteAsync_WithActiveInstall_Deactivates()
    {
        // Arrange
        var installPath = Path.Combine(_fixture.TempRoot, "Godot_v4.5.1-stable_win64.exe");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);

        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

        // Act
        var result = await _command.ExecuteAsync(null!);

        // Assert
        Assert.Equal(0, result);

        var updatedRegistry = await _fixture.Registry.LoadAsync();
        Assert.Null(updatedRegistry.ActiveId);
        Assert.All(updatedRegistry.Installs, install => Assert.False(install.IsActive));
    }

    [Fact]
    public async Task ExecuteAsync_WithNoActiveInstall_ReturnsSuccess()
    {
        // Arrange
        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: Path.Combine(_fixture.TempRoot, "test"));

        registry.Installs.Add(entry);
        // Don't mark as active
        await _fixture.Registry.SaveAsync(registry);

        // Act
        var result = await _command.ExecuteAsync(null!);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_ClearsActiveInstallFromRegistry()
    {
        // Arrange
        var installPath = Path.Combine(_fixture.TempRoot, "Godot_v4.5.1-stable_win64.exe");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry1 = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        var entry2 = InstallEntryFactory.Create(version: "4.4.0", path: Path.Combine(_fixture.TempRoot, "test2"));

        registry.Installs.Add(entry1);
        registry.Installs.Add(entry2);
        registry.MarkActive(entry1.Id);
        await _fixture.Registry.SaveAsync(registry);

        // Act
        await _command.ExecuteAsync(null!);

        // Assert
        var updatedRegistry = await _fixture.Registry.LoadAsync();
        Assert.Null(updatedRegistry.ActiveId);
        Assert.Equal(2, updatedRegistry.Installs.Count);
        Assert.All(updatedRegistry.Installs, install => Assert.False(install.IsActive));
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
