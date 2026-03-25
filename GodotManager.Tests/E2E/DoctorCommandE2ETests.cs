using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

public class DoctorCommandE2ETests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public DoctorCommandE2ETests() => _fixture = new GodmanTestFixture();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Doctor_WithEmptyRegistry_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture);
        await _fixture.Registry.SaveAsync(new InstallRegistry());

        var result = await app.RunAsync("doctor");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Doctor_WithActiveInstall_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture);
        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(
            version: "4.5.1", path: Path.Combine(_fixture.TempRoot, "g451"));
        registry.Installs.Add(entry);
        registry.MarkActive(entry.Id);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync("doctor");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Doctor_WithLegacyDirectory_ExitsZero()
    {
        var app = CliTestHarness.Create(_fixture);
        await _fixture.Registry.SaveAsync(new InstallRegistry());

        // Create a legacy directory at a path GetLegacyPaths() will check
        var legacyPaths = _fixture.Paths.GetLegacyPaths();
        var legacyDir = legacyPaths[0].Path;
        var createdDir = false;

        if (!Directory.Exists(legacyDir))
        {
            Directory.CreateDirectory(legacyDir);
            createdDir = true;
        }

        try
        {
            var result = await app.RunAsync("doctor");

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            if (createdDir)
            {
                try { Directory.Delete(legacyDir, true); } catch { }
            }
        }
    }
}
