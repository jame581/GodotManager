using GodotManager.Commands;
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public async Task ExecuteAsync_SwitchingInstalls_CleansPreviousShim()
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
        await _registry.SaveAsync(registry);

        // First activate entry1 - this creates a shim
        var settings1 = new ActivateCommand.Settings { Id = entry1.Id };
        await _command.ExecuteAsync(null!, settings1);

        var shimDir = _paths.GetShimDirectory(InstallScope.User);
        var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
        var shimPath = Path.Combine(shimDir, shimName);
        Assert.True(File.Exists(shimPath), "Shim should exist after first activation");

        // Read old shim content to verify it changes
        var oldShimContent = File.ReadAllText(shimPath);

        // Now switch to entry2
        var settings2 = new ActivateCommand.Settings { Id = entry2.Id };
        var result = await _command.ExecuteAsync(null!, settings2);

        // Assert
        Assert.Equal(0, result);
        Assert.True(File.Exists(shimPath), "Shim should exist after switch");

        var newShimContent = File.ReadAllText(shimPath);
        Assert.NotEqual(oldShimContent, newShimContent);
        Assert.Contains(installPath2, newShimContent);
    }

    [Fact]
    public void ElevatedActivatePayload_RoundTrip_Serialization()
    {
        // Arrange
        var id = Guid.NewGuid();
        var payload = new ElevatedActivatePayload(id, CreateDesktopShortcut: true);

        // Act
        var json = JsonSerializer.Serialize(payload);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var decodedJson = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var deserialized = JsonSerializer.Deserialize<ElevatedActivatePayload>(decodedJson);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(id, deserialized!.Id);
        Assert.True(deserialized.CreateDesktopShortcut);
    }

    [Fact]
    public void ElevatedActivatePayload_RoundTrip_WithoutDesktopShortcut()
    {
        // Arrange
        var id = Guid.NewGuid();
        var payload = new ElevatedActivatePayload(id, CreateDesktopShortcut: false);

        // Act
        var json = JsonSerializer.Serialize(payload);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var decodedJson = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var deserialized = JsonSerializer.Deserialize<ElevatedActivatePayload>(decodedJson);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(id, deserialized!.Id);
        Assert.False(deserialized.CreateDesktopShortcut);
    }

    [Fact]
    public void WindowsElevationHelper_IsElevated_ReturnsBool()
    {
        // This test simply verifies IsElevated runs without error.
        // In a normal test environment it returns false (not running as admin).
        var result = WindowsElevationHelper.IsElevated();

        if (OperatingSystem.IsWindows())
        {
            // Should return a valid bool; typically false in test runners
            Assert.IsType<bool>(result);
        }
        else
        {
            // On non-Windows, always returns false
            Assert.False(result);
        }
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
