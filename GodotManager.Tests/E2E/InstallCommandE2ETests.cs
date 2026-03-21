using GodotManager.Tests.Helpers;
using System;
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
            "install", "--version", "4.5.1", "--url", "http://example.com/godot.zip", "--dry-run");

        Assert.Equal(0, result.ExitCode);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.Empty(registry.Installs);
    }

    [Fact]
    public async Task Install_MissingVersion_ExitsNonZero()
    {
        var app = CliTestHarness.Create(_fixture);

        var result = await app.RunAsync("install");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Install_WithLocalArchive_ExitsZeroAndRegisters()
    {
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var app = CliTestHarness.Create(_fixture);

        var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
        var result = await app.RunAsync(
            "install", "--version", "4.5.1", "--archive", mockArchive, "--platform", platform);

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
            "install", "--version", "4.5.1", "--url", "http://test.com/godot.zip", "--platform", platform);

        Assert.Equal(0, result.ExitCode);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.Single(registry.Installs);

        System.IO.File.Delete(mockArchive);
    }

    [Fact]
    public async Task Install_WithActivate_SetsActive()
    {
        var mockArchive = MockArchiveFactory.CreateMockGodotArchive();
        var app = CliTestHarness.Create(_fixture);

        var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
        var result = await app.RunAsync(
            "install", "--version", "4.5.1", "--archive", mockArchive, "--platform", platform, "--activate");

        Assert.Equal(0, result.ExitCode);

        var registry = await _fixture.Registry.LoadAsync();
        Assert.Single(registry.Installs);
        Assert.NotNull(registry.ActiveId);

        System.IO.File.Delete(mockArchive);
    }
}
