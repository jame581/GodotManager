using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
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

    private readonly TextField _versionField;
    private readonly OptionSelector _editionSelector;
    private readonly OptionSelector _scopeSelector;
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly Button _installButton;
    private readonly Button _cancelButton;

    private bool _installing;

    public InstallDialog(
        InstallerService installer,
        GodotDownloadUrlBuilder urlBuilder,
        AppPaths paths)
    {
        _installer = installer;
        _urlBuilder = urlBuilder;
        _paths = paths;

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
        _installButton.Accepting += (_, _) => _ = DoInstallAsync();

        _cancelButton = new Button { Text = "Cancel" };
        _cancelButton.Accepting += (_, _) =>
        {
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
            MessageBox.ErrorQuery(Application.Instance, "Error", "Please enter a version.", "OK");
            return;
        }

        var edition = _editionSelector.Value == 1 ? InstallEdition.DotNet : InstallEdition.Standard;
        var platform = OperatingSystem.IsWindows() ? InstallPlatform.Windows : InstallPlatform.Linux;
        var scope = _scopeSelector.Value == 1 ? InstallScope.Global : InstallScope.User;

        if (!_urlBuilder.TryBuildUri(version, edition, platform, out var uri, out var error))
        {
            MessageBox.ErrorQuery(Application.Instance, "Error", $"Could not build download URL: {error}", "OK");
            return;
        }

        _installing = true;
        _progressBar.Visible = true;
        _statusLabel.Visible = true;
        _statusLabel.Text = "Starting install...";
        _installButton.Visible = false;

        var request = new InstallRequest(
            version, edition, platform, scope,
            uri, null, null,
            Activate: true, Force: false);

        try
        {
            await _installer.InstallWithElevationAsync(request, progress =>
            {
                Application.Instance.Invoke(() =>
                {
                    _progressBar.Fraction = (float)progress;
                    _statusLabel.Text = progress < 1.0
                        ? $"Installing... {progress:P0}"
                        : "Finalizing...";
                });
            });

            Application.Instance.Invoke(() =>
            {
                _statusLabel.Text = "Install complete!";
                MessageBox.Query(Application.Instance, "Success", $"Installed Godot {version} ({edition})", "OK");
                RequestStop();
            });
        }
        catch (Exception ex)
        {
            Application.Instance.Invoke(() =>
            {
                _statusLabel.Text = "Install failed.";
                MessageBox.ErrorQuery(Application.Instance, "Error", $"Install failed: {ex.Message}", "OK");
                _installing = false;
                _installButton.Visible = true;
            });
        }
    }
}
