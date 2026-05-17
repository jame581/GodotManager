using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class DoctorCommandTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public DoctorCommandTests()
    {
        _fixture = new GodmanTestFixture();
    }

    [Fact]
    public async Task ExecuteAsync_WithNoInstalls_ReturnsZero()
    {
        // Arrange
        var registry = new InstallRegistry();
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["doctor"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithActiveInstall_ReturnsZero()
    {
        // Arrange
        var installPath = Path.Combine(_fixture.TempRoot, "Godot_v4.5.1-stable");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(
            version: "4.5.1",
            edition: InstallEdition.Standard,
            path: installPath);

        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["doctor"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoActiveInstall_ReturnsZero()
    {
        // Arrange
        var installPath = Path.Combine(_fixture.TempRoot, "Godot_v4.5.1-stable");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(
            version: "4.5.1",
            edition: InstallEdition.Standard,
            path: installPath);

        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["doctor"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
