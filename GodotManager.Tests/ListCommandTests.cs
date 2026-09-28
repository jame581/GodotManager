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

    [Fact]
    public async Task List_WarnsWhenGodmanGlobalRootStillNamesTheShimDirectory()
    {
        // A 1.3.0 user who kept GODMAN_GLOBAL_ROOT=<X>/bin: nothing in 1.4.0 looks in
        // <X>/bin/godman, so without a warning their global installs silently vanish.
        if (OperatingSystem.IsWindows()) return; // the prefix change is Linux-only

        using var fixture = new GodmanTestFixture(globalRoot: Path.Combine("opt", "bin"), seed: value =>
        {
            Directory.CreateDirectory(Path.Combine(value, "godman", "4.5.1-standard-linux-global"));
            File.WriteAllText(Path.Combine(value, "godman", "installs.json"), "{\"Installs\":[]}");
        });
        var value = Path.Combine(fixture.TempRoot, "opt", "bin");
        var app = CliTestHarness.Create(fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["list"]);

            Assert.Equal(0, result.ExitCode);
            var output = result.Output.Replace("\r", "").Replace("\n", "");
            Assert.Contains($"GODMAN_GLOBAL_ROOT={value} uses the pre-1.4.0 meaning", output);
            Assert.Contains($"set it to {Path.Combine(fixture.TempRoot, "opt")}", output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task List_DoesNotWarnAboutAGodmanGlobalRootPrefix()
    {
        var app = CliTestHarness.Create(_fixture);
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["list"]);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("pre-1.4.0 meaning", result.Output);
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
