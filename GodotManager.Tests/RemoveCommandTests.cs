using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class RemoveCommandTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;
    private readonly RemoveCommand _command;

    public RemoveCommandTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "godman-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempRoot);

        _paths = new AppPaths();
        _registry = new RegistryService(_paths);
        _environment = new EnvironmentService(_paths);
        _command = new RemoveCommand(_registry, _environment);
    }

    [Fact]
    public async Task ExecuteAsync_RemovesInstallFromRegistry()
    {
        // Arrange
        var installPath = Path.Combine(_tempRoot, "Godot_v4.5.1-stable_win64.exe");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = installPath
        };

        registry.Installs.Add(entry);
        await _registry.SaveAsync(registry);

        var settings = new RemoveCommand.Settings { Id = entry.Id, DeleteFiles = false };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);

        var updatedRegistry = await _registry.LoadAsync();
        Assert.Empty(updatedRegistry.Installs);
        Assert.True(Directory.Exists(installPath), "Files should not be deleted without --delete flag");
    }

    [Fact]
    public async Task ExecuteAsync_WithDeleteFiles_RemovesDirectory()
    {
        // Arrange
        var installPath = Path.Combine(_tempRoot, "Godot_v4.5.1-stable_win64.exe");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "test.txt"), "test");

        var registry = new InstallRegistry();
        var entry = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = installPath
        };

        registry.Installs.Add(entry);
        await _registry.SaveAsync(registry);

        var settings = new RemoveCommand.Settings { Id = entry.Id, DeleteFiles = true };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);
        Assert.False(Directory.Exists(installPath), "Directory should be deleted with --delete flag");
    }

    [Fact]
    public async Task ExecuteAsync_RemovingActiveInstall_Deactivates()
    {
        // Arrange
        var installPath = Path.Combine(_tempRoot, "Godot_v4.5.1-stable_win64.exe");
        Directory.CreateDirectory(installPath);

        // Create fake executable for deactivation
        var exeName = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        File.WriteAllText(Path.Combine(installPath, exeName), "fake");

        var registry = new InstallRegistry();
        var entry = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = installPath
        };

        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _registry.SaveAsync(registry);

        var settings = new RemoveCommand.Settings { Id = entry.Id, DeleteFiles = false };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);

        var updatedRegistry = await _registry.LoadAsync();
        Assert.Null(updatedRegistry.ActiveId);
        Assert.Empty(updatedRegistry.Installs);
    }

    [Fact]
    public async Task ExecuteAsync_WithNonExistentId_ReturnsError()
    {
        // Arrange
        var registry = new InstallRegistry();
        await _registry.SaveAsync(registry);

        var settings = new RemoveCommand.Settings { Id = Guid.NewGuid(), DeleteFiles = false };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public async Task ExecuteAsync_RemovingNonActiveInstall_KeepsActive()
    {
        // Arrange
        var installPath1 = Path.Combine(_tempRoot, "Godot_v4.5.1-stable_win64.exe");
        var installPath2 = Path.Combine(_tempRoot, "Godot_v4.4.0-stable_win64.exe");
        Directory.CreateDirectory(installPath1);
        Directory.CreateDirectory(installPath2);

        var exeName1 = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        File.WriteAllText(Path.Combine(installPath1, exeName1), "fake");

        var registry = new InstallRegistry();
        var entry1 = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = installPath1
        };

        var entry2 = new InstallEntry
        {
            Version = "4.4.0",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = installPath2
        };

        registry.Installs.Add(entry1);
        registry.Installs.Add(entry2);
        registry.MarkActive(entry1.Id);
        await _registry.SaveAsync(registry);

        var settings = new RemoveCommand.Settings { Id = entry2.Id, DeleteFiles = false };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);

        var updatedRegistry = await _registry.LoadAsync();
        Assert.Equal(entry1.Id, updatedRegistry.ActiveId);
        Assert.Single(updatedRegistry.Installs);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
