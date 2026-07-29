using GodotManager.Domain;
using GodotManager.Tui;
using System;
using Xunit;

namespace GodotManager.Tests;

/// <summary>
/// Covers the pure formatting/conversion logic InstallDialog.DoInstallAsync uses
/// to drive its progress bar and completion message. Lives on the standalone
/// InstallProgressPresentation type (GodotManager/Tui/InstallProgressPresentation.cs)
/// rather than on InstallDialog itself: Terminal.Gui runs a module initializer the
/// moment any type from its assembly is touched — including a Dialog subclass's own
/// static members — and that initializer throws under the xunit test host in this
/// environment. There is no TUI test harness in this project, and building one just
/// for these two bugs would be overbuilt for what they are.
/// </summary>
public class InstallDialogTests
{
    [Theory]
    [InlineData(0d, 0f)]
    [InlineData(50d, 0.5f)]
    [InlineData(100d, 1f)]
    public void ToFraction_ConvertsThe0To100ProgressReportInto0To1(double progress, float expected)
    {
        // Both DownloadService and InstallerService's extraction step report
        // progress on a 0..100 scale (InstallCommand's Spectre task is configured
        // with maxValue: 100 to match). Feeding that straight into ProgressBar's
        // 0..1 Fraction — the bug this pins — pinned the bar at full immediately
        // and would show 5000% here if the conversion were removed.
        Assert.Equal(expected, InstallProgressPresentation.ToFraction(progress));
    }

    [Fact]
    public void ToFraction_ClampsOutOfRangeInput()
    {
        Assert.Equal(1f, InstallProgressPresentation.ToFraction(150d));
        Assert.Equal(0f, InstallProgressPresentation.ToFraction(-10d));
    }

    [Fact]
    public void FormatProgressLabel_MidInstall_ShowsInstallingWithPercent()
    {
        // progress is 0..100 (not 0..1), so a threshold check written against 1.0
        // — the bug this pins — would report "Finalizing..." starting at the very
        // first callback instead of only once the transfer actually completes.
        Assert.Equal("Installing... 42%", InstallProgressPresentation.FormatProgressLabel(42d));
    }

    [Fact]
    public void FormatProgressLabel_AtOneHundred_ShowsFinalizing()
    {
        Assert.Equal("Finalizing...", InstallProgressPresentation.FormatProgressLabel(100d));
    }

    [Fact]
    public void BuildCompletionStatus_WhenVerified_ReportsPlainSuccess()
    {
        Assert.Equal("Install complete!", InstallProgressPresentation.BuildCompletionStatus(unverified: false));
    }

    [Fact]
    public void BuildCompletionStatus_WhenUnverified_NamesTheUnverifiedCondition()
    {
        // The TUI used to report success unconditionally, hiding a download that
        // never got checked against the published sums.
        var status = InstallProgressPresentation.BuildCompletionStatus(unverified: true);

        Assert.Contains("unverified", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCompletionMessage_WhenUnverified_NamesTheChecksumCondition()
    {
        var message = InstallProgressPresentation.BuildCompletionMessage("4.5.1", InstallEdition.Standard, unverified: true);

        Assert.Contains("4.5.1", message);
        Assert.Contains("could not be verified", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCompletionMessage_WhenVerified_DoesNotMentionVerification()
    {
        var message = InstallProgressPresentation.BuildCompletionMessage("4.5.1", InstallEdition.Standard, unverified: false);

        Assert.Contains("4.5.1", message);
        Assert.DoesNotContain("verif", message, StringComparison.OrdinalIgnoreCase);
    }
}
