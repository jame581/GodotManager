using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class ListCommandTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;
    private readonly RegistryService _registry;
    private readonly ListCommand _command;

    public ListCommandTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "godman-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempRoot);

        Environment.SetEnvironmentVariable("GODMAN_HOME", _tempRoot);

        _paths = new AppPaths();
        _registry = new RegistryService(_paths);
        _command = new ListCommand(_registry);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoInstalls_ReturnsZero()
    {
        // Arrange
        var registry = new InstallRegistry();
        await _registry.SaveAsync(registry);

        // Act
        var result = await _command.ExecuteAsync(null!);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithInstalls_ReturnsZero()
    {
        // Arrange
        var registry = new InstallRegistry();
        var entry1 = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = Path.Combine(_tempRoot, "Godot_v4.5.1")
        };

        var entry2 = new InstallEntry
        {
            Version = "4.4.0",
            Edition = InstallEdition.DotNet,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = Path.Combine(_tempRoot, "Godot_v4.4.0")
        };

        registry.Installs.Add(entry1);
        registry.Installs.Add(entry2);
        await _registry.SaveAsync(registry);

        // Act
        var result = await _command.ExecuteAsync(null!);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithActiveInstall_ReturnsZero()
    {
        // Arrange
        var registry = new InstallRegistry();
        var entry = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = Path.Combine(_tempRoot, "Godot_v4.5.1")
        };

        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _registry.SaveAsync(registry);

        // Act
        var result = await _command.ExecuteAsync(null!);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithChecksum_ReturnsZero()
    {
        // Arrange
        var registry = new InstallRegistry();
        var entry = new InstallEntry
        {
            Version = "4.5.1",
            Edition = InstallEdition.Standard,
            Platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux,
            Scope = InstallScope.User,
            Path = Path.Combine(_tempRoot, "Godot_v4.5.1"),
            Checksum = "abc123def456789012345678901234567890abcdef1234567890abcdef12345678"
        };

        registry.Installs.Add(entry);
        await _registry.SaveAsync(registry);

        // Act
        var result = await _command.ExecuteAsync(null!);

        // Assert
        Assert.Equal(0, result);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GODMAN_HOME", null);

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
