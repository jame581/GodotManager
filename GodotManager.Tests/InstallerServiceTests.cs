using GodotManager.Domain;
using GodotManager.Services;
using Xunit;

namespace GodotManager.Tests;

public class InstallerServiceTests
{
    private static InstallRequest MakeRequest(InstallPlatform platform = InstallPlatform.Linux)
    {
        return new InstallRequest(
            "4.5.1", InstallEdition.Standard, platform, InstallScope.User,
            null, "/tmp/archive.zip", null, false, false);
    }

    [Theory]
    [InlineData("Godot_v4.5-stable_linux.x86_64.tar.gz", "Godot_v4.5-stable_linux")]
    [InlineData("Godot_v4.5-stable_linux.tar.gz", "Godot_v4.5-stable_linux")]
    [InlineData("Godot_v4.5-stable_linux.tar", "Godot_v4.5-stable_linux")]
    [InlineData("Godot_v4.5-stable_linux.x86_64.zip", "Godot_v4.5-stable_linux")]
    [InlineData("Godot_v4.5-stable_win64.exe.zip", "Godot_v4.5-stable_win64")]
    [InlineData("Godot_v4.5-stable_mono_linux_x86_64.zip", "Godot_v4.5-stable_mono_linux_x86_64")]
    public void BuildInstallFolderName_StripsArchiveSuffixes(string archiveName, string expected)
    {
        var request = MakeRequest();
        var result = InstallerService.BuildInstallFolderName(request, archiveName);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildInstallFolderName_FallsBackToConvention_WhenNoArchiveName()
    {
        var request = MakeRequest();
        var result = InstallerService.BuildInstallFolderName(request, null);
        Assert.Equal("4.5.1-standard-linux-user", result);
    }
}
