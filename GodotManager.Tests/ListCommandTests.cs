using GodotManager.Commands;
using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class ListCommandTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;
    private readonly ListCommand _command;

    public ListCommandTests()
    {
        _fixture = new GodmanTestFixture();
        _command = new ListCommand(_fixture.Registry);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoInstalls_ReturnsZero()
    {
        // Arrange
        var registry = new InstallRegistry();
        await _fixture.Registry.SaveAsync(registry);

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
        var entry1 = InstallEntryFactory.Create(
            version: "4.5.1",
            edition: InstallEdition.Standard,
            path: System.IO.Path.Combine(_fixture.TempRoot, "Godot_v4.5.1"));

        var entry2 = InstallEntryFactory.Create(
            version: "4.4.0",
            edition: InstallEdition.DotNet,
            path: System.IO.Path.Combine(_fixture.TempRoot, "Godot_v4.4.0"));

        registry.Installs.Add(entry1);
        registry.Installs.Add(entry2);
        await _fixture.Registry.SaveAsync(registry);

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
        var entry = InstallEntryFactory.Create(
            version: "4.5.1",
            edition: InstallEdition.Standard,
            path: System.IO.Path.Combine(_fixture.TempRoot, "Godot_v4.5.1"));

        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

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
        var entry = InstallEntryFactory.Create(
            version: "4.5.1",
            edition: InstallEdition.Standard,
            path: System.IO.Path.Combine(_fixture.TempRoot, "Godot_v4.5.1"),
            checksum: "abc123def456789012345678901234567890abcdef1234567890abcdef12345678");

        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        // Act
        var result = await _command.ExecuteAsync(null!);

        // Assert
        Assert.Equal(0, result);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
