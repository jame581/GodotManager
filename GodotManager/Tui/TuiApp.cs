using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using GodotManager.Tui.Views;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace GodotManager.Tui;

internal sealed class TuiApp
{
    private readonly RegistryService _registry;
    private readonly InstallerService _installer;
    private readonly EnvironmentService _environment;
    private readonly AppPaths _paths;
    private readonly GodotDownloadUrlBuilder _urlBuilder;
    private readonly GodotVersionFetcher _fetcher;

    private IApplication? _app;

    private InstallsListView? _installsList;
    private DetailsView? _detailsView;
    private BrowseView? _browseView;
    private FrameView? _rightFrame;
    private bool _browseMode;

    public TuiApp(
        RegistryService registry,
        InstallerService installer,
        EnvironmentService environment,
        AppPaths paths,
        GodotDownloadUrlBuilder urlBuilder,
        GodotVersionFetcher fetcher)
    {
        _registry = registry;
        _installer = installer;
        _environment = environment;
        _paths = paths;
        _urlBuilder = urlBuilder;
        _fetcher = fetcher;
    }

    public int Run()
    {
        var app = Application.Create();
        app.Init();
        _app = app;

        var window = new Window
        {
            Title = "Godot Manager",
            BorderStyle = LineStyle.Single
        };

        var leftFrame = new FrameView
        {
            Title = "Installs",
            X = 0,
            Y = 0,
            Width = Dim.Percent(30),
            Height = Dim.Fill(1)
        };

        _rightFrame = new FrameView
        {
            Title = "Details",
            X = Pos.Right(leftFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1)
        };

        _installsList = new InstallsListView();
        _installsList.SelectionChanged += OnInstallSelectionChanged;
        leftFrame.Add(_installsList);

        _detailsView = new DetailsView();
        _browseView = new BrowseView(_fetcher, app);
        _browseView.VersionSelected += OnBrowseVersionSelected;
        _rightFrame.Add(_detailsView);

        var statusBar = new StatusBar
        {
            Y = Pos.AnchorEnd(1)
        };
        statusBar.Add(
            new Shortcut(Key.F1, "Browse", () => ToggleBrowseMode(app), ""),
            new Shortcut(Key.F2, "Install", () => ShowInstallDialog(app), ""),
            new Shortcut(Key.F3, "Doctor", () => ShowDoctorDialog(app), ""),
            new Shortcut(Key.Q.WithCtrl, "Quit", () => RequestStop(window), "")
        );

        window.Add(leftFrame, _rightFrame, statusBar);

        window.KeyDown += (s, e) => HandleGlobalKey(e, app, window);

        _ = LoadInstallsAsync(app);

        app.Run(window);
        window.Dispose();

        return 0;
    }

    private void HandleGlobalKey(Key key, IApplication app, Window window)
    {
        if (key == Key.Q && !(_browseView?.HasFocus ?? false))
        {
            RequestStop(window);
            key.Handled = true;
        }
        else if (key == Key.A)
        {
            _ = ActivateSelectedAsync(app);
            key.Handled = true;
        }
        else if (key == Key.D)
        {
            _ = DeactivateAsync(app);
            key.Handled = true;
        }
        else if (key == Key.R)
        {
            _ = RemoveSelectedAsync(app);
            key.Handled = true;
        }
        else if (key.AsRune.Value == '?')
        {
            ShowHelpOverlay(app);
            key.Handled = true;
        }
    }

    private static void RequestStop(Window window)
    {
        window.RequestStop();
    }

    private void ToggleBrowseMode(IApplication app)
    {
        _browseMode = !_browseMode;
        _rightFrame!.RemoveAll();

        if (_browseMode)
        {
            _rightFrame.Title = "Browse Versions";
            _rightFrame.Add(_browseView!);
            _ = _browseView!.LoadVersionsAsync();
        }
        else
        {
            _rightFrame.Title = "Details";
            _rightFrame.Add(_detailsView!);
            UpdateDetailsForSelection();
        }
    }

    private void OnInstallSelectionChanged(object? sender, InstallEntry? entry)
    {
        if (!_browseMode)
        {
            _detailsView?.ShowEntry(entry);
        }
    }

    private void OnBrowseVersionSelected(object? sender, GodotRelease release)
    {
        var dialog = new InstallDialog(_installer, _urlBuilder, _paths, _app!);
        dialog.PresetVersion(release.Version);
        _app!.Run(dialog);
        dialog.Dispose();
        _ = RefreshRegistryAsync(_app!);
    }

    private void UpdateDetailsForSelection()
    {
        var selected = _installsList?.SelectedEntry;
        _detailsView?.ShowEntry(selected);
    }

    private async Task LoadInstallsAsync(IApplication app)
    {
        try
        {
            var registry = await _registry.LoadAsync();
            app.Invoke(() =>
            {
                _installsList?.SetInstalls(registry);
                UpdateDetailsForSelection();
            });
        }
        catch (Exception ex)
        {
            app.Invoke(() =>
            {
                MessageBox.ErrorQuery(app, "Error", $"Failed to load registry: {ex.Message}", "OK");
            });
        }
    }

    private async Task RefreshRegistryAsync(IApplication app)
    {
        await LoadInstallsAsync(app);
    }

    private async Task ActivateSelectedAsync(IApplication app)
    {
        var entry = _installsList?.SelectedEntry;
        if (entry is null || entry.IsActive) return;

        try
        {
            var registry = await _registry.LoadAsync();
            var previous = registry.GetActive();
            if (previous is not null)
            {
                await _environment.RemoveActiveAsync(previous);
            }

            await _environment.ApplyActiveAsync(entry);
            registry.MarkActive(entry.Id);
            await _registry.SaveAsync(registry);

            app.Invoke(() =>
            {
                MessageBox.Query(app, "Activated", $"Activated {entry.Version} ({entry.Edition})", "OK");
            });
            await RefreshRegistryAsync(app);
        }
        catch (Exception ex)
        {
            app.Invoke(() =>
            {
                MessageBox.ErrorQuery(app, "Error", $"Activation failed: {ex.Message}", "OK");
            });
        }
    }

    private async Task DeactivateAsync(IApplication app)
    {
        try
        {
            var registry = await _registry.LoadAsync();
            var active = registry.GetActive();
            if (active is null)
            {
                app.Invoke(() =>
                {
                    MessageBox.Query(app, "Info", "No active install to deactivate.", "OK");
                });
                return;
            }

            await _environment.RemoveActiveAsync(active);
            registry.ClearActive();
            await _registry.SaveAsync(registry);

            app.Invoke(() =>
            {
                MessageBox.Query(app, "Deactivated", $"Deactivated {active.Version} ({active.Edition})", "OK");
            });
            await RefreshRegistryAsync(app);
        }
        catch (Exception ex)
        {
            app.Invoke(() =>
            {
                MessageBox.ErrorQuery(app, "Error", $"Deactivation failed: {ex.Message}", "OK");
            });
        }
    }

    private async Task RemoveSelectedAsync(IApplication app)
    {
        var entry = _installsList?.SelectedEntry;
        if (entry is null) return;

        int? confirm = null;
        app.Invoke(() =>
        {
            confirm = MessageBox.Query(
                app,
                "Remove Install",
                $"Remove {entry.Version} ({entry.Edition})?\nFiles at: {entry.Path}",
                "Remove (keep files)", "Remove + Delete files", "Cancel");
        });

        if (confirm is null or 2) return;
        var deleteFiles = confirm == 1;

        try
        {
            var registry = await _registry.LoadAsync();

            if (entry.IsActive)
            {
                await _environment.RemoveActiveAsync(entry);
                registry.ClearActive();
            }

            if (deleteFiles && Directory.Exists(entry.Path))
            {
                Directory.Delete(entry.Path, recursive: true);
            }

            registry.Installs.RemoveAll(x => x.Id == entry.Id);
            await _registry.SaveAsync(registry);

            app.Invoke(() =>
            {
                MessageBox.Query(app, "Removed", $"Removed {entry.Version} ({entry.Edition})", "OK");
            });
            await RefreshRegistryAsync(app);
        }
        catch (Exception ex)
        {
            app.Invoke(() =>
            {
                MessageBox.ErrorQuery(app, "Error", $"Remove failed: {ex.Message}", "OK");
            });
        }
    }

    private void ShowInstallDialog(IApplication app)
    {
        var dialog = new InstallDialog(_installer, _urlBuilder, _paths, app);
        app.Run(dialog);
        dialog.Dispose();
        _ = RefreshRegistryAsync(app);
    }

    private void ShowDoctorDialog(IApplication app)
    {
        var dialog = new DoctorDialog(_registry, _environment, _paths, app);
        app.Run(dialog);
        dialog.Dispose();
    }

    private void ShowHelpOverlay(IApplication app)
    {
        var overlay = new HelpOverlay();
        app.Run(overlay);
        overlay.Dispose();
    }
}
