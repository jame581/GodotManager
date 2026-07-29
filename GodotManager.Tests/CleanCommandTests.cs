using GodotManager.Commands;
using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

public class CleanCommandTests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public CleanCommandTests()
    {
        _fixture = new GodmanTestFixture();
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Clean_RemovesUserPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Path overrides aimed at Linux layout; behavior on Windows is trivial.
        }

        var userInstall = _fixture.Paths.GetInstallRoot(InstallScope.User);
        var userShim = _fixture.Paths.GetShimDirectory(InstallScope.User);
        var config = _fixture.Paths.ConfigDirectory;

        Directory.CreateDirectory(userInstall);
        Directory.CreateDirectory(userShim);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(userInstall, "dummy"), "x");
        File.WriteAllText(Path.Combine(config, "cfg"), "x");

        // Place a godot shim in the shim directory
        File.WriteAllText(Path.Combine(userShim, "godot"), "shim");

        var app = CliTestHarness.Create(_fixture);
        var result = await app.RunAsync(["clean", "--yes"]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(userInstall));
        Assert.False(Directory.Exists(config));

        // On Linux, shim directory should still exist (it's shared, e.g. ~/.local/bin),
        // but the godot shim file inside should be removed.
        Assert.False(File.Exists(Path.Combine(userShim, "godot")));
    }

    [Fact]
    public async Task Clean_OnLinux_OnlyRemovesShimFile_NotEntireDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // This test targets Linux behavior where shim dir is shared.
        }

        var userShim = _fixture.Paths.GetShimDirectory(InstallScope.User);

        Directory.CreateDirectory(userShim);

        // Place the godot shim and an unrelated binary in the same directory
        File.WriteAllText(Path.Combine(userShim, "godot"), "shim");
        File.WriteAllText(Path.Combine(userShim, "other-tool"), "keep me");

        var app = CliTestHarness.Create(_fixture);
        await app.RunAsync(["clean", "--yes"]);

        // The godot shim should be removed
        Assert.False(File.Exists(Path.Combine(userShim, "godot")));

        // The unrelated binary and directory must survive
        Assert.True(Directory.Exists(userShim), "Shim directory should not be deleted on Linux");
        Assert.True(File.Exists(Path.Combine(userShim, "other-tool")), "Other files in shim directory must not be deleted");
    }

    [Fact]
    public async Task Clean_RemovesGlobalPaths_WhenOverridesSet()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Global concept not supported on Windows.
        }

        var globalInstall = _fixture.Paths.GetInstallRoot(InstallScope.Global);
        var globalShim = _fixture.Paths.GetShimDirectory(InstallScope.Global);

        Directory.CreateDirectory(globalInstall);
        Directory.CreateDirectory(globalShim);
        File.WriteAllText(Path.Combine(globalInstall, "dummy"), "x");
        File.WriteAllText(Path.Combine(globalShim, "godot"), "shim");

        var app = CliTestHarness.Create(_fixture);
        var result = await app.RunAsync(["clean", "--yes"]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(globalInstall));
        // On Linux, only the shim file is removed, not the directory
        Assert.False(File.Exists(Path.Combine(globalShim, "godot")));
    }

    [Fact]
    public void CleanupAll_RemovesTheDownloadCache()
    {
        File.WriteAllText(
            Path.Combine(_fixture.Paths.DownloadCacheDirectory, "abc123.archive"),
            "cached archive");

        CleanCommand.CleanupAll(_fixture.Paths);

        Assert.False(Directory.Exists(_fixture.Paths.DownloadCacheDirectory));
    }
}
