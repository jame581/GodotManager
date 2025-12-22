using System;
using GodotManager.Domain;
using GodotManager.Services;
using Xunit;

namespace GodotManager.Tests;

public class GodotDownloadUrlBuilderTests
{
    private readonly GodotDownloadUrlBuilder _builder = new();

    [Theory]
    [InlineData("4.5.1", InstallEdition.Standard, InstallPlatform.Linux, "https://godot-releases.nbg1.your-objectstorage.com/4.5.1-stable/Godot_v4.5.1-stable_linux.x86_64.zip")]
    [InlineData("4.5.1", InstallEdition.DotNet, InstallPlatform.Linux, "https://godot-releases.nbg1.your-objectstorage.com/4.5.1-stable/Godot_v4.5.1-stable_mono_linux_x86_64.zip")]
    public void TryBuildUri_Linux_ReturnsExpected(string version, InstallEdition edition, InstallPlatform platform, string expected)
    {
        var ok = _builder.TryBuildUri(version, edition, platform, out var uri, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(expected, uri!.ToString());
    }

    [Theory]
    [InlineData("4.5.1", InstallEdition.Standard, "win64.exe.zip")]
    [InlineData("4.5.1", InstallEdition.DotNet, "win64.mono.zip")]
    public void TryBuildUri_Windows_UsesOfficialEndpoint(string version, InstallEdition edition, string slug)
    {
        var ok = _builder.TryBuildUri(version, edition, InstallPlatform.Windows, out var uri, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal($"https://downloads.godotengine.org/?version={version}&flavor=stable&slug={slug}&platform=windows.64", uri!.ToString());
    }

    [Fact]
    public void TryBuildUri_UnsupportedPlatform_ReturnsError()
    {
        var ok = _builder.TryBuildUri("4.5.1", InstallEdition.Standard, (InstallPlatform)999, out var uri, out var error);

        Assert.False(ok);
        Assert.Null(uri);
        Assert.Equal("Unsupported platform.", error);
    }
}
