using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class ActivateCommandTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;
    private readonly ActivateCommand _command;

    public ActivateCommandTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "godman-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempRoot);

        _paths = new AppPaths();
        _registry = new RegistryService(_paths);
        _environment = new EnvironmentService(_paths);
        _command = new ActivateCommand(_registry, _environment);
    }

    [Fact]
    public async Task ExecuteAsync_ActivatesInstall()
    {
        // Arrange
        var installPath = Path.Combine(_tempRoot, "Godot_v4.5.1-stable_win64.exe");
        Directory.CreateDirectory(installPath);

        // Create fake executable
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
        await _registry.SaveAsync(registry);

        var settings = new ActivateCommand.Settings { Id = entry.Id };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);

        var updatedRegistry = await _registry.LoadAsync();
        Assert.Equal(entry.Id, updatedRegistry.ActiveId);
        var updatedEntry = updatedRegistry.Installs.First(e => e.Id == entry.Id);
        Assert.True(updatedEntry.IsActive);
    }

    [Fact]
    public async Task ExecuteAsync_WithNonExistentId_ReturnsError()
    {
        // Arrange
        var registry = new InstallRegistry();
        await _registry.SaveAsync(registry);

        var settings = new ActivateCommand.Settings { Id = Guid.NewGuid() };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithDryRun_DoesNotModifyRegistry()
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

        var settings = new ActivateCommand.Settings { Id = entry.Id, DryRun = true };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);

        var updatedRegistry = await _registry.LoadAsync();
        Assert.Null(updatedRegistry.ActiveId);
        Assert.False(entry.IsActive);
    }

    [Fact]
    public async Task ExecuteAsync_SwitchesActiveInstall()
    {
        // Arrange
        var installPath1 = Path.Combine(_tempRoot, "Godot_v4.5.1-stable_win64.exe");
        var installPath2 = Path.Combine(_tempRoot, "Godot_v4.4.0-stable_win64.exe");
        Directory.CreateDirectory(installPath1);
        Directory.CreateDirectory(installPath2);

        var exeName1 = OperatingSystem.IsWindows() ? "Godot_v4.5.1-stable_win64.exe" : "Godot_v4.5.1-stable_linux.x86_64";
        var exeName2 = OperatingSystem.IsWindows() ? "Godot_v4.4.0-stable_win64.exe" : "Godot_v4.4.0-stable_linux.x86_64";
        File.WriteAllText(Path.Combine(installPath1, exeName1), "fake");
        File.WriteAllText(Path.Combine(installPath2, exeName2), "fake");

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

        var settings = new ActivateCommand.Settings { Id = entry2.Id };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        Assert.Equal(0, result);

        var updatedRegistry = await _registry.LoadAsync();
        Assert.Equal(entry2.Id, updatedRegistry.ActiveId);
        var updatedEntry1 = updatedRegistry.Installs.First(e => e.Id == entry1.Id);
        var updatedEntry2 = updatedRegistry.Installs.First(e => e.Id == entry2.Id);
        Assert.False(updatedEntry1.IsActive);
        Assert.True(updatedEntry2.IsActive);
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
