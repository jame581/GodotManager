using GodotManager.Services;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// Direct coverage of InstallerService.TryKillProcessTreeAsync -- the helper the
/// task-13 review's Critical fix round added to RunElevatedInstallAsync's
/// cancellation path (a cancelled Windows + Global-scope elevated install used to
/// leave the elevated child running while deleting the archive it might still have
/// open). The production call site is reachable only from that Windows +
/// Global-scope + unelevated branch, which cannot be exercised on this development
/// platform -- but the helper itself has no such dependency: it operates on any
/// <see cref="Process"/>, which is exactly why it was made `internal` (rather than
/// staying `private`) and reached here via this assembly's own
/// InternalsVisibleTo("GodotManager.Tests"), the same pattern already used
/// elsewhere in this project for internals that need direct test coverage.
/// </summary>
public class InstallerServiceInternalsTests
{
    [Fact]
    public async Task TryKillProcessTreeAsync_WithARunningProcess_ActuallyTerminatesIt()
    {
        using var process = StartLongRunningProcess();

        var stopwatch = Stopwatch.StartNew();
        await InstallerService.TryKillProcessTreeAsync(process);
        stopwatch.Stop();

        Assert.True(process.HasExited, "the process must actually be terminated, not merely asked to exit");

        // The child sleeps for 30s (or its Windows equivalent). A real kill returns
        // in a small fraction of that; this bound is generous enough to avoid
        // flaking under load while still catching the case where Kill() silently
        // did nothing and this just waited the child out to its natural end.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"expected the process to be killed quickly, but this took {stopwatch.Elapsed}");
    }

    /// <summary>
    /// Covers the scenario the fix exists to survive -- cancellation lands after
    /// the elevated child has already exited on its own -- but not, it turns out,
    /// by exercising the catch blocks that were written to guard it.
    /// </summary>
    /// <remarks>
    /// Mutation testing on this method (see the fix report's addendum) found this
    /// assertion passes identically whether TryKillProcessTreeAsync's
    /// `if (!HasExited)` guard and its try/catch(InvalidOperationException)/
    /// catch(Win32Exception) are present or deleted outright. On this platform and
    /// .NET version, calling Process.Kill(entireProcessTree: true) and then
    /// WaitForExitAsync() on a Process whose underlying OS process already exited
    /// simply does not throw -- so for exactly the input this test constructs, the
    /// guard and catches are not load-bearing here. They are kept in production
    /// code regardless: .NET's own documentation states Kill() throws
    /// InvalidOperationException for an already-exited process on Windows, which
    /// this Linux environment cannot exercise either way. This test is retained as
    /// a real behavioral pin (the documented contract genuinely holds: nothing
    /// throws for this input, on this platform, today) and as a regression guard --
    /// not as proof that the defensive code is what makes it hold here.
    /// </remarks>
    [Fact]
    public async Task TryKillProcessTreeAsync_WithAnAlreadyExitedProcess_DoesNotThrow()
    {
        using var process = StartShortLivedProcess();
        await process.WaitForExitAsync();
        Assert.True(process.HasExited, "arrange step: the process must have exited before this test's real assertion runs");

        var exception = await Record.ExceptionAsync(() => InstallerService.TryKillProcessTreeAsync(process));

        Assert.Null(exception);
    }

    private static Process StartLongRunningProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 31 127.0.0.1")
            : new ProcessStartInfo("sleep", "30");
        psi.UseShellExecute = false;

        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start test process.");
    }

    private static Process StartShortLivedProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("true", string.Empty);
        psi.UseShellExecute = false;

        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start test process.");
    }
}
