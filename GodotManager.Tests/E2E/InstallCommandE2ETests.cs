using GodotManager.Tests.Helpers;
using Spectre.Console;
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

public class InstallCommandE2ETests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public InstallCommandE2ETests() => _fixture = new GodmanTestFixture();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Install_DryRun_ExitsZeroAndDoesNotRegister()
    {
        var app = CliTestHarness.Create(_fixture);

        var result = await app.RunAsync(
            ["install", "--version", "4.5.1", "--url", "http://example.com/godot.zip", "--dry-run"]);

        Assert.Equal(0, result.ExitCode);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.Empty(registry.Installs);
    }

    [Fact]
    public async Task Install_MissingVersion_ExitsNonZero()
    {
        var app = CliTestHarness.Create(_fixture);

        var result = await app.RunAsync(["install"]);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Install_WithLocalArchive_ExitsZeroAndRegisters()
    {
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var app = CliTestHarness.Create(_fixture);

        var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
        var result = await app.RunAsync(
            ["install", "--version", "4.5.1", "--archive", mockArchive, "--platform", platform]);

        Assert.Equal(0, result.ExitCode);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.Single(registry.Installs);
        Assert.Equal("4.5.1", registry.Installs[0].Version);
        Assert.NotNull(registry.Installs[0].Checksum);

        System.IO.File.Delete(mockArchive);
    }

    [Fact]
    public async Task Install_WithMockedDownload_ExitsZeroAndRegisters()
    {
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var httpClient = new HttpClient(new MockFileHttpHandler(mockArchive));
        var app = CliTestHarness.Create(_fixture, httpClient);

        var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
        var result = await app.RunAsync(
            ["install", "--version", "4.5.1", "--url", "http://test.com/godot.zip", "--platform", platform]);

        Assert.Equal(0, result.ExitCode);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.Single(registry.Installs);

        System.IO.File.Delete(mockArchive);
    }

    [Fact]
    public async Task Install_WhenAnAutoUrlInstallCannotBeVerified_WarnsTheUser()
    {
        // Only an auto-built URL identifies an upstream release, so only it gets a
        // ChecksumSource — and only then is "could not verify" worth saying.
        var app = CliTestHarness.Create(_fixture, ArchiveWithoutSums(out var mockArchive));

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
            var result = await app.RunAsync(["install", "--version", "4.5.1", "--platform", platform]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("could not be verified", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }

        System.IO.File.Delete(mockArchive);
    }

    [Fact]
    public async Task Install_WithACustomUrl_DoesNotWarnAboutVerification()
    {
        // A --url install has no published sums by definition. Warning here would
        // fire on every such install and train the user to ignore the warning.
        var app = CliTestHarness.Create(_fixture, ArchiveWithoutSums(out var mockArchive));

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
            var result = await app.RunAsync(
                ["install", "--version", "4.5.1", "--url", "https://test.invalid/godot.zip", "--platform", platform]);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("could not be verified", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }

        System.IO.File.Delete(mockArchive);
    }

    [Fact]
    public async Task Install_WhenTheDownloadIsVerified_DoesNotWarn()
    {
        // The command-layer half of the elevated-install gate. A Windows global install
        // resolves its plan in the unelevated parent and writes its registry entry in
        // the elevated child; if that entry comes back with ChecksumVerified = false the
        // user is told a verified download could not be verified. Paired with
        // Install_WhenAnAutoUrlInstallCannotBeVerified_WarnsTheUser above, this pins the
        // message to the flag rather than to the mere presence of a ChecksumSource.
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var archiveBytes = System.IO.File.ReadAllBytes(mockArchive);
        var httpClient = new HttpClient(new MockSumsHttpHandler(
            archiveBytes, "Godot_v4.5.1-stable_linux.x86_64.zip", "SHA512-SUMS.txt"));
        var app = CliTestHarness.Create(_fixture, httpClient);

        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
            var result = await app.RunAsync(["install", "--version", "4.5.1", "--platform", platform]);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("could not be verified", result.Output);

            var registry = await _fixture.Registry.LoadAsync();
            Assert.True(Assert.Single(registry.Installs).ChecksumVerified);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }

        System.IO.File.Delete(mockArchive);
    }

    /// <summary>
    /// Serves a real Godot-shaped archive for any request except the published
    /// SHA512-SUMS.txt, which answers 404 — the common upstream case of a release
    /// with no published checksums.
    /// </summary>
    private static HttpClient ArchiveWithoutSums(out string mockArchive)
    {
        mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var archiveBytes = System.IO.File.ReadAllBytes(mockArchive);

        return new HttpClient(new SequencedHttpHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("SHA512-SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archiveBytes) };
            ok.Content.Headers.ContentLength = archiveBytes.Length;
            return ok;
        }));
    }

    [Fact]
    public async Task Install_WhenTheServerKeepsFailing_RendersAnActionableErrorNotAStackTrace()
    {
        // Now that installs actually go through DownloadService, an exhausted retry
        // loop is a live user-facing path. TransientDownloadException is outside the
        // GodmanException hierarchy, so an unwrapped one renders as a stack trace.
        var httpClient = new HttpClient(new SequencedHttpHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var app = CliTestHarness.Create(_fixture, httpClient);

        // Commands write through the static AnsiConsole, not the tester's own
        // console, so it has to be redirected to capture the rendered failure.
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = app.Console;
        try
        {
            var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
            var result = await app.RunAsync(
                ["install", "--version", "4.5.1", "--url", "https://test.invalid/godot.zip", "--platform", platform]);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Install failed:", result.Output);
            Assert.Contains("hint:", result.Output);
            Assert.DoesNotContain("TransientDownloadException", result.Output);
            Assert.DoesNotContain("GodotManager.Services.DownloadService", result.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }

        Assert.Empty((await _fixture.Registry.LoadAsync()).Installs);
    }

    [Fact]
    public async Task Install_WithActivate_SetsActive()
    {
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var app = CliTestHarness.Create(_fixture);

        var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
        var result = await app.RunAsync(
            ["install", "--version", "4.5.1", "--archive", mockArchive, "--platform", platform, "--activate"]);

        Assert.Equal(0, result.ExitCode);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.Single(registry.Installs);
        Assert.NotNull(registry.ActiveId);

        System.IO.File.Delete(mockArchive);
    }
}
