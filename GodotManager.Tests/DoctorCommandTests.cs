using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class DoctorCommandTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;
    private readonly RegistryService _registry;
    private readonly DoctorCommand _command;

    public DoctorCommandTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "godman-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempRoot);
        Environment.SetEnvironmentVariable("GODMAN_HOME", _tempRoot);
        _paths = new AppPaths();
        _registry = new RegistryService(_paths);
        _command = new DoctorCommand(_registry, _paths);
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
    public async Task ExecuteAsync_WithActiveInstall_ReturnsZero()
    {
        // Arrange
        var installPath = Path.Combine(_tempRoot, "Godot_v4.5.1-stable");
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
    }

    [Fact]
    public async Task ExecuteAsync_WithNoActiveInstall_ReturnsZero()
    {
        // Arrange
        var installPath = Path.Combine(_tempRoot, "Godot_v4.5.1-stable");
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
