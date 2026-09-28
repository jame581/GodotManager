using GodotManager.Domain;
using GodotManager.Tests.Helpers;
using Spectre.Console;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests.E2E;

public class RemoveCommandE2ETests : IDisposable
{
    private readonly GodmanTestFixture _fixture;

    public RemoveCommandE2ETests() => _fixture = new GodmanTestFixture();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Remove_ValidId_ExitsZeroAndRemovesEntry()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync(["remove", entry.Id.ToString()]);

        Assert.Equal(0, result.ExitCode);

        var updated = await _fixture.Registry.LoadAsync();
        Assert.Empty(updated.Installs);
    }

    [Fact]
    public async Task Remove_WithDelete_DeletesFiles()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "godot"), "fake");

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync(["remove", entry.Id.ToString(), "--delete"]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(installPath));
    }

    [Fact]
    public async Task Remove_NonexistentId_ExitsNonZero()
    {
        var app = CliTestHarness.Create(_fixture);
        await _fixture.Registry.SaveAsync(new InstallRegistry());

        var result = await app.RunAsync(["remove", Guid.NewGuid().ToString()]);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Remove_GlobalScopeEntry_ReadOnlyRegistryFile_StopsAtThePreCheckWithTheRealCommand()
    {
        // Originally reproduced Jan's manual-test observation (task-13-brief.md Bug A): the
        // hint of RegistryService.SaveAsync's late global-write failure was dropped. Since
        // LinuxElevation.Check probes the global registry file up front, a read-only file
        // now stops the removal at the pre-check instead, before anything is written; this
        // pins that the rendering (prefix, hint, the real arguments) survived the move.
        //
        // The late path itself -- SaveAsync's GodmanException, rewritten by
        // GodmanException.WithArguments in RemoveCommand/InstallCommand/DeactivateCommand --
        // is now reachable only through a race between the pre-check and the save (TOCTOU),
        // which a test cannot set up deterministically; a user-registry failure raises no
        // hint at all. WithArguments is covered by its unit test in LinuxElevationTests.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return; // Permission simulation below is POSIX-specific and root bypasses it.
        }

        var installPath = Path.Combine(_fixture.TempRoot, "global-install");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(scope: InstallScope.Global, path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry); // Global file is still writable here.

        // Unlike RegistryServiceTests's equivalent test -- which chmods the
        // *directory* because its global file has never been created -- this
        // scenario needs an existing entry to remove, so installs.json already
        // exists. File.Create on an existing path only needs write permission on
        // the file itself, not the directory, so the file (not the directory) is
        // what must be locked down here.
        var globalRegistryFile = _fixture.Paths.GlobalRegistryFile;
        File.SetUnixFileMode(globalRegistryFile, UnixFileMode.UserRead);

        try
        {
            var app = CliTestHarness.Create(_fixture);

            // RemoveCommand writes through the static AnsiConsole, not
            // CommandAppTester's own console -- see ListCommandTests.cs:129-141.
            var originalConsole = AnsiConsole.Console;
            AnsiConsole.Console = app.Console;
            try
            {
                var result = await app.RunAsync(["remove", entry.Id.ToString()]);

                Assert.NotEqual(0, result.ExitCode);

                // Positive assertion proving the redirect is live, paired with the
                // negative assertion below: the old, unhandled-exception behavior
                // rendered via Spectre's own "Error:" prefix, not ours.
                Assert.Contains("Remove failed:", result.Output);
                Assert.DoesNotContain("Error:", result.Output);

                var output = result.Output.Replace("\r", "").Replace("\n", "");
                Assert.Contains("hint:", output);
                Assert.Contains("sudo", output, StringComparison.OrdinalIgnoreCase);
                Assert.Contains($"remove {entry.Id}", output);
                Assert.DoesNotContain("<same arguments>", output);
            }
            finally
            {
                AnsiConsole.Console = originalConsole;
            }
        }
        finally
        {
            // Restore write access so GodmanTestFixture.Dispose can clean up TempRoot.
            File.SetUnixFileMode(globalRegistryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Remove_DryRun_DoesNotModifyRegistry()
    {
        var app = CliTestHarness.Create(_fixture);
        var installPath = Path.Combine(_fixture.TempRoot, "g451");
        Directory.CreateDirectory(installPath);

        var registry = new InstallRegistry();
        var entry = InstallEntryFactory.Create(version: "4.5.1", path: installPath);
        registry.Installs.Add(entry);
        await _fixture.Registry.SaveAsync(registry);

        var result = await app.RunAsync(["remove", entry.Id.ToString(), "--dry-run"]);

        Assert.Equal(0, result.ExitCode);

        var updated = await _fixture.Registry.LoadAsync();
        Assert.Single(updated.Installs);
    }
}
