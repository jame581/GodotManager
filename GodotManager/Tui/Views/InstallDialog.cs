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
            if (!_installing) RequestStop();
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
        _progressBar.Visible = true;
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

        try
        {
            InstallEntry result = await _installer.InstallWithElevationAsync(request, progress =>
            {
                _app.Invoke(() =>
                {
                    _progressBar.Fraction = InstallProgressPresentation.ToFraction(progress);
                    _statusLabel.Text = InstallProgressPresentation.FormatProgressLabel(progress);
                });
            });

            // request.Checksums is always set here (TryBuildUri above only ever
            // builds an upstream release URL), mirroring the same check InstallCommand
            // makes before its own --verbose-gated warning. The TUI owns the whole
            // screen during an install, so the unverified condition is surfaced in
            // this dialog's own widgets rather than by writing to AnsiConsole.
            var unverified = request.Checksums is not null && !result.ChecksumVerified;

            _app.Invoke(() =>
            {
                Success = true;
                _statusLabel.Text = InstallProgressPresentation.BuildCompletionStatus(unverified);
                MessageBox.Query(
                    _app, "Success",
                    InstallProgressPresentation.BuildCompletionMessage(version, edition, unverified),
                    "OK");
                RequestStop();
            });
        }
        catch (Exception ex)
        {
            _app.Invoke(() =>
            {
                _statusLabel.Text = "Install failed.";
                MessageBox.ErrorQuery(_app, "Error", $"Install failed: {ex.Message}", "OK");
                _installing = false;
                _installButton.Visible = true;
            });
        }
    }
}
