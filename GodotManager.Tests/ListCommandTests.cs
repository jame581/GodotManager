using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using Spectre.Console;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class ListCommandTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public ListCommandTests()
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
        var result = await app.RunAsync(["list"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
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

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["list"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
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

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["list"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
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

        var app = CliTestHarness.Create(_fixture);

        // Act
        var result = await app.RunAsync(["list"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithVerifiedChecksum_RendersMarker()
    {
        var registry = await _fixture.Registry.LoadAsync();
        registry.Installs.Add(new InstallEntry
        {
            Version = "4.5.1",
            Path = Path.Combine(_fixture.TempRoot, "verified-install"),
            Checksum = new string('b', 128),
            ChecksumAlgorithm = "sha512",
            ChecksumVerified = true
        });
        await _fixture.Registry.SaveAsync(registry);

        var app = CliTestHarness.Create(_fixture);

        // ListCommand writes through the static AnsiConsole (per project convention),
        // not the CommandAppTester's own console, so it must be redirected here to
        // capture the rendered table.
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["list"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("✓", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
