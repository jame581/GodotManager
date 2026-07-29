using GodotManager.Domain;
using GodotManager.Infrastructure;
using GodotManager.Tests.Helpers;
using Spectre.Console;
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

    [Fact]
    public async Task Doctor_WithStalePartial_ReportsIt()
    {
        await File.WriteAllBytesAsync(
            Path.Combine(_fixture.Paths.DownloadCacheDirectory, "deadbeefdeadbeef.part"),
            new byte[2048]);

        var app = CliTestHarness.Create(_fixture);

        // DoctorCommand writes through the static AnsiConsole (per project convention),
        // not the CommandAppTester's own console, so it must be redirected here to
        // capture the rendered output. See ListCommandTests for the same pattern.
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            // Positive assertion proving the redirected channel actually carried
            // doctor's output, not just that "incomplete download" is absent by luck.
            Assert.Contains("Download cache", result.Output);
            Assert.Contains("incomplete download", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithEmptyCache_ReportsEmpty()
    {
        // GodmanTestFixture's AppPaths creates DownloadCacheDirectory on construction,
        // so it exists and is empty here without any extra setup.
        var app = CliTestHarness.Create(_fixture);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Download cache", result.Output);
            Assert.Contains("empty", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("incomplete download", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithMissingCacheDirectory_DoesNotThrowAndSkipsCacheReport()
    {
        Directory.Delete(_fixture.Paths.DownloadCacheDirectory, recursive: true);

        var app = CliTestHarness.Create(_fixture);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            // Proves the channel is live: other doctor output still renders.
            Assert.Contains("No installs registered yet", result.Output);
            Assert.DoesNotContain("Download cache", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithUnreadableCache_WarnsUnderVerboseInsteadOfThrowing()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix permission bits don't apply on Windows.
        }

        File.WriteAllBytes(
            Path.Combine(_fixture.Paths.DownloadCacheDirectory, "abc123.archive"),
            new byte[16]);
        File.SetUnixFileMode(_fixture.Paths.DownloadCacheDirectory, UnixFileMode.None);

        var diagnostics = new DiagnosticContext { Verbose = true };
        var app = CliTestHarness.Create(_fixture, diagnostics: diagnostics);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("warn:", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("download cache", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
            // Restore permissions so GodmanTestFixture.Dispose can delete TempRoot.
            File.SetUnixFileMode(
                _fixture.Paths.DownloadCacheDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
