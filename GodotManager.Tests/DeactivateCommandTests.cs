using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class DeactivateCommandTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;
    private readonly RegistryService _registry;
    private readonly EnvironmentService _environment;
    private readonly DeactivateCommand _command;

    public DeactivateCommandTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "godman-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempRoot);

        _paths = new AppPaths();
        _registry = new RegistryService(_paths);
        _environment = new EnvironmentService(_paths);
        _command = new DeactivateCommand(_registry, _environment);
    }

    [Fact]
    public async Task ExecuteAsync_WithActiveInstall_Deactivates()
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
        registry.MarkActive(entry.Id);
        await _registry.SaveAsync(registry);

        // Act
        var result = await _command.ExecuteAsync(null!);

        // Assert
        Assert.Equal(0, result);

        var updatedRegistry = await _registry.LoadAsync();
        Assert.Null(updatedRegistry.ActiveId);
        Assert.All(updatedRegistry.Installs, install => Assert.False(install.IsActive));
    }

    [Fact]
    public async Task ExecuteAsync_WithNoActiveInstall_ReturnsSuccess()
    {
        // Arrange
        var registry = new InstallRegistry();
        var entry = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = Path.Combine(_tempRoot, "test")
        };

        registry.Installs.Add(entry);
        // Don't mark as active
        await _registry.SaveAsync(registry);

        // Act
        var result = await _command.ExecuteAsync(null!);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_ClearsActiveInstallFromRegistry()
    {
        // Arrange
        var installPath = Path.Combine(_tempRoot, "Godot_v4.5.1-stable_win64.exe");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry1 = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = installPath
        };

        var entry2 = new InstallEntry
        {
            Version = "4.4.0",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = Path.Combine(_tempRoot, "test2")
        };

        registry.Installs.Add(entry1);
        registry.Installs.Add(entry2);
        registry.MarkActive(entry1.Id);
        await _registry.SaveAsync(registry);

        // Act
        await _command.ExecuteAsync(null!);

        // Assert
        var updatedRegistry = await _registry.LoadAsync();
        Assert.Null(updatedRegistry.ActiveId);
        Assert.Equal(2, updatedRegistry.Installs.Count);
        Assert.All(updatedRegistry.Installs, install => Assert.False(install.IsActive));
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
