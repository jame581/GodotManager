using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using GodotManager.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace GodotManager.Tui.Views;

internal sealed class InstallDialog : Dialog
{
    private readonly InstallerService _installer;
    private readonly GodotDownloadUrlBuilder _urlBuilder;
    private readonly AppPaths _paths;
    private readonly IApplication _app;

    private readonly TextField _versionField;
    private readonly OptionSelector _editionSelector;
    private readonly OptionSelector _scopeSelector;
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly Button _installButton;
    private readonly Button _cancelButton;

    private bool _installing;
    private CancellationTokenSource? _cancellationSource;

    /// <summary>
    /// True only after an install completed successfully. Stays false if the
    /// user cancelled, closed the dialog with Esc, or the install threw.
    /// </summary>
    public bool Success { get; private set; }

    public InstallDialog(
        InstallerService installer,
        GodotDownloadUrlBuilder urlBuilder,
        AppPaths paths,
        IApplication app)
    {
        _installer = installer;
        _urlBuilder = urlBuilder;
        _paths = paths;
        _app = app;

        Title = "Install Godot";
        Width = Dim.Percent(60);
        Height = Dim.Percent(60);

        var versionLabel = new Label { Text = "Version:", X = 1, Y = 1 };
        _versionField = new TextField
        {
            X = 14, Y = 1, Width = Dim.Fill() - 2,
            CanFocus = true
        };

        var editionLabel = new Label { Text = "Edition:", X = 1, Y = 3 };
        _editionSelector = new OptionSelector
        {
            X = 14, Y = 3,
            Width = Dim.Fill() - 2,
            Orientation = Orientation.Horizontal,
            Labels = ["Standard", ".NET"],
            Values = [0, 1]
        };

        var scopeLabel = new Label { Text = "Scope:", X = 1, Y = 5 };
        _scopeSelector = new OptionSelector
        {
            X = 14, Y = 5,
            Width = Dim.Fill() - 2,
            Orientation = Orientation.Horizontal,
            Labels = ["User", "Global"],
            Values = [0, 1]
        };

        _progressBar = new ProgressBar
        {
            X = 1, Y = 8,
            Width = Dim.Fill() - 2,
            Height = 1,
            Fraction = 0f,
            Visible = false
        };

        _statusLabel = new Label
        {
            X = 1, Y = 9,
            Width = Dim.Fill() - 2,
            Text = "",
            Visible = false
        };

        _installButton = new Button { Text = "Install" };
        _installButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            _ = DoInstallAsync();
        };

        _cancelButton = new Button { Text = "Cancel" };
        _cancelButton.Accepting += (_, args) =>
        {
            args.Handled = true;

            // Both branches are intended to run on the UI thread: this handler fires
            // there directly, and _installing/_cancellationSource are only ever
            // mutated from inside an _app.Invoke callback in DoInstallAsync, which is
            // also meant to run there -- so by Terminal.Gui's own single-threaded
            // main-loop model there should be no race between "cancel mid-install"
            // and "the install just finished and cleared _installing out from under
            // this click". That model is argued from Terminal.Gui's documented
            // design, not observed: InstallDialog cannot be instantiated under the
            // xunit host in this environment, so this reasoning has not actually
            // been exercised against a live main loop.
            if (_installing)
            {
                _cancellationSource?.Cancel();
            }
            else
            {
                RequestStop();
            }
        };

        Add(versionLabel, _versionField, editionLabel, _editionSelector,
            scopeLabel, _scopeSelector, _progressBar, _statusLabel);

        AddButton(_installButton);
        AddButton(_cancelButton);
    }

    public void PresetVersion(string version)
    {
        _versionField.Text = version;
    }

    private async Task DoInstallAsync()
    {
        var version = _versionField.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(version))
        {
            MessageBox.ErrorQuery(_app, "Error", "Please enter a version.", "OK");
            return;
        }

        var edition = _editionSelector.Value == 1 ? InstallEdition.DotNet : InstallEdition.Standard;
        var platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;
        var scope = _scopeSelector.Value == 1 ? InstallScope.Global : InstallScope.User;

        if (!_urlBuilder.TryBuildUri(version, edition, platform, out var uri, out var error))
        {
            MessageBox.ErrorQuery(_app, "Error", $"Could not build download URL: {error}", "OK");
            return;
        }

        _installing = true;
        _cancellationSource = new CancellationTokenSource();
        _progressBar.Visible = true;
        _progressBar.Fraction = 0f;
        _statusLabel.Visible = true;
        _statusLabel.Text = "Starting install...";
        _installButton.Visible = false;

        // TryBuildUri above always produces an upstream release URL, so a
        // ChecksumSource is always correct here. Named, because a positional
        // argument in this position would bind to DryRun.
        var request = new InstallRequest(
            version, edition, platform, scope,
            uri, null, null,
            Activate: true, Force: false,
            Checksums: new ChecksumSource(version));

        var verificationStatus = ChecksumStatus.NotApplicable;
        string? verificationReason = null;
        var cancellationToken = _cancellationSource.Token;

        try
        {
            InstallEntry result = await _installer.InstallWithElevationAsync(
                request,
                progress =>
                {
                    _app.Invoke(() =>
                    {
                        _progressBar.Fraction = InstallProgressPresentation.ToFraction(progress);
                        _statusLabel.Text = InstallProgressPresentation.FormatProgressLabel(progress);
                    });
                },
                cancellationToken,
                onVerified: (status, reason) =>
                {
                    verificationStatus = status;
                    verificationReason = reason;
                });

            // NotApplicable covers both "no published sums to check against" and
            // "this release publishes none upstream" -- neither is an error, so
            // neither should be surfaced as though the download were suspect. Only
            // Unverified -- an attempt that was actually made and did not succeed --
            // is worth telling the user about. The TUI owns the whole screen during
            // an install, so this is surfaced in the dialog's own widgets rather
            // than by writing to AnsiConsole, mirroring InstallCommand's own
            // --verbose-gated warning for the CLI.
            var unverified = verificationStatus == ChecksumStatus.Unverified;

            _app.Invoke(() =>
            {
                Success = true;
                _installing = false;
                DisposeCancellationSource();
                _statusLabel.Text = InstallProgressPresentation.BuildCompletionStatus(unverified);
                MessageBox.Query(
                    _app, "Success",
                    InstallProgressPresentation.BuildCompletionMessage(version, edition, unverified, verificationReason),
                    "OK");
                RequestStop();
            });
        }
        catch (OperationCanceledException)
        {
            // Not an install failure: the user asked for this. InstallerService's
            // own OperationCanceledException handler (Task 7b) has already cleaned
            // up any staging directory; the partial download in the cache is
            // deliberately left alone so a retry can resume it rather than
            // restart from zero. Stay in the dialog -- do not RequestStop -- and
            // put it back in a state where Install can be pressed again.
            _app.Invoke(() =>
            {
                _installing = false;
                DisposeCancellationSource();
                _progressBar.Visible = false;
                _statusLabel.Text = InstallProgressPresentation.BuildCancelledStatus();
                _installButton.Visible = true;
            });
        }
        catch (Exception ex)
        {
            _app.Invoke(() =>
            {
                _installing = false;
                DisposeCancellationSource();
                _statusLabel.Text = "Install failed.";

                MessageBox.ErrorQuery(_app, "Error", TuiErrorPresentation.BuildErrorBody("Install failed", ex), "OK");
                _installButton.Visible = true;
            });
        }
    }

    /// <summary>
    /// Disposal is intended to happen only from within an _app.Invoke callback
    /// (i.e. on the UI thread), matching where the Cancel button's Accepting
    /// handler reads and calls <see cref="_cancellationSource"/> -- otherwise a
    /// click racing the install's completion could call Cancel() on an
    /// already-disposed source. As with the Cancel handler's own comment, this is
    /// reasoned from Terminal.Gui's single-threaded main-loop model, not verified
    /// by a test: InstallDialog cannot be instantiated under the xunit host in
    /// this environment.
    /// </summary>
    private void DisposeCancellationSource()
    {
        _cancellationSource?.Dispose();
        _cancellationSource = null;
    }
}
