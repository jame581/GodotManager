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
    public async Task Doctor_WithOrphanedPartial_ReportsItAsNotResumable()
    {
        // No .json sidecar: per DownloadService's cache-lifecycle invariant, a .part
        // without a sidecar has no ETag to guard a Range request with, so DownloadAsync
        // does not resume it -- it truncates and restarts from zero. Doctor must not
        // claim this one "will resume".
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
            // Three positive assertions on the redirected channel, ending with the
            // specific "0 resumable" count: together they prove doctor actually
            // rendered a report naming this orphaned .part as unresumable, rather
            // than the assertions merely passing because the redirect captured
            // nothing at all.
            Assert.Contains("Download cache", result.Output);
            Assert.Contains("incomplete download", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("0 resumable", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithResumablePartial_ReportsItAsResumable()
    {
        // A .part with its .json sidecar present (URL + ETag) is the case where
        // DownloadService actually resumes on the next install.
        var partPath = Path.Combine(_fixture.Paths.DownloadCacheDirectory, "cafef00dcafef00d.part");
        var metaPath = Path.Combine(_fixture.Paths.DownloadCacheDirectory, "cafef00dcafef00d.json");
        await File.WriteAllBytesAsync(partPath, new byte[2048]);
        await File.WriteAllTextAsync(
            metaPath,
            """{"Url":"https://example.test/godot.zip","ArchiveName":"godot.zip","ETag":"\"abc123\""}""");

        var app = CliTestHarness.Create(_fixture);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Download cache", result.Output);
            Assert.Contains("incomplete download", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1 resumable", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task Doctor_WithOnlyACompletedArchive_StillOffersTheCleanupHint()
    {
        // A completed .archive with no .part next to it is the normal outcome of an
        // install that failed after the download finished but before extraction
        // succeeded: ResolvePlanAsync promotes the .part to .archive before
        // InstallAsync's own target-exists check ever runs. Gating the cleanup hint
        // on "partials > 0" alone missed this case entirely -- a cache holding only
        // a stale multi-hundred-MB archive was reported in yellow with no remedy.
        await File.WriteAllBytesAsync(
            Path.Combine(_fixture.Paths.DownloadCacheDirectory, "deadbeefdeadbeef.archive"),
            new byte[4096]);

        var app = CliTestHarness.Create(_fixture);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Download cache", result.Output);
            Assert.Contains("completed archive", result.Output, StringComparison.OrdinalIgnoreCase);
            // The point of this test: no partials exist, yet the remedy still shows.
            Assert.DoesNotContain("incomplete download", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("godman clean", result.Output);
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
    public async Task Doctor_WithMissingCacheDirectoryAndVerbose_DoesNotWarn()
    {
        // A missing DownloadCacheDirectory is the single most common state (a fresh
        // install that has never downloaded anything). Directory.Exists is what keeps
        // that off the screen; without it, Directory.GetFiles would throw
        // DirectoryNotFoundException, and the surrounding try/catch would turn that
        // into a spurious "warn: ... DirectoryNotFoundException" under --verbose even
        // though nothing is actually wrong. This test is what distinguishes "guard
        // present" from "guard absent but masked by the catch" -- see task-9-report.md
        // for the mutation that only this test (not
        // Doctor_WithMissingCacheDirectory_DoesNotThrowAndSkipsCacheReport) catches.
        Directory.Delete(_fixture.Paths.DownloadCacheDirectory, recursive: true);

        var diagnostics = new DiagnosticContext { Verbose = true };
        var app = CliTestHarness.Create(_fixture, diagnostics: diagnostics);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var result = await app.RunAsync(["doctor"]);

            Assert.Equal(0, result.ExitCode);
            // Proves the channel is live: other doctor output still renders.
            Assert.Contains("No installs registered yet", result.Output);
            Assert.DoesNotContain("warn:", result.Output, StringComparison.OrdinalIgnoreCase);
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
