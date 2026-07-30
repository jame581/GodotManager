using GodotManager.Infrastructure;
using GodotManager.Tui;
using System;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// Covers TuiApp's activate/deactivate/remove error-dialog formatting via the
/// standalone TuiErrorPresentation type (GodotManager/Tui/TuiErrorPresentation.cs)
/// rather than TuiApp itself -- see InstallDialogTests for why: touching a type
/// that carries Terminal.Gui field types trips a module initializer that throws
/// under the xunit test host in this environment.
/// </summary>
public class TuiErrorPresentationTests
{
    [Fact]
    public void BuildErrorBody_WithGodmanExceptionHint_AppendsTheHintOnItsOwnLine()
    {
        // RegistryService.SaveAsync's global-write failure is a GodmanException
        // whose whole actionable remedy ("re-run with sudo") lives in Hint. If
        // this dropped the hint, a TUI user activating/removing a global-scope
        // install without elevation would see a bare access-denied message with
        // no indication of what to do about it.
        var ex = new GodmanException("Failed to update the machine-wide registry", "Re-run with sudo.");

        var body = TuiErrorPresentation.BuildErrorBody("Activation failed", ex);

        Assert.Equal("Activation failed: Failed to update the machine-wide registry\nRe-run with sudo.", body);
    }

    [Fact]
    public void BuildErrorBody_WithGodmanExceptionWithoutHint_OmitsTheSecondLine()
    {
        var ex = new GodmanException("something went wrong");

        var body = TuiErrorPresentation.BuildErrorBody("Remove failed", ex);

        Assert.Equal("Remove failed: something went wrong", body);
        Assert.DoesNotContain('\n', body);
    }

    [Fact]
    public void BuildErrorBody_WithPlainException_UsesOnlyItsMessage()
    {
        var ex = new InvalidOperationException("no active install");

        var body = TuiErrorPresentation.BuildErrorBody("Deactivation failed", ex);

        Assert.Equal("Deactivation failed: no active install", body);
    }
}
