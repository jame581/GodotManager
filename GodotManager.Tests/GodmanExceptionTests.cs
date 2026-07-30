using System;
using GodotManager.Infrastructure;
using Xunit;

namespace GodotManager.Tests;

public class GodmanExceptionTests
{
    [Fact]
    public void GodmanException_CarriesMessageAndHint()
    {
        var ex = new GodmanException("something went wrong", "try --force");

        Assert.Equal("something went wrong", ex.Message);
        Assert.Equal("try --force", ex.Hint);
    }

    [Fact]
    public void GodmanException_HintIsOptional()
    {
        Assert.Null(new GodmanException("something went wrong").Hint);
    }

    [Fact]
    public void ElevationHint_NamesTheRemedyForThisPlatform()
    {
        // RegistryService.SaveAsync's global-write failure and ActivateCommand's
        // UnauthorizedAccessException/SecurityException catches both pull this
        // same string, so the wording must actually differ per platform rather
        // than always saying "sudo" (wrong on Windows) or always saying
        // "administrator" (wrong on Linux/macOS).
        var hint = GodmanException.ElevationHint;

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("administrator", hint, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sudo", hint, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("sudo", hint, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("administrator", hint, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ChecksumMismatchException_IsGodmanExceptionAndNamesBothHashes()
    {
        var sumsUri = new Uri("https://example.test/SHA512-SUMS.txt");
        var ex = new ChecksumMismatchException("Godot_v4.5.1-stable_linux.x86_64.zip", "aaa", "bbb", sumsUri);

        Assert.IsAssignableFrom<GodmanException>(ex);
        Assert.Equal("Godot_v4.5.1-stable_linux.x86_64.zip", ex.ArchiveName);
        Assert.Equal("aaa", ex.Expected);
        Assert.Equal("bbb", ex.Actual);
        Assert.Equal(sumsUri, ex.SumsUri);
        Assert.Contains("aaa", ex.Message);
        Assert.Contains("bbb", ex.Message);
        Assert.NotNull(ex.Hint);
    }
}
