using System.Reflection;
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
    private FrameView? _leftFrame;
    private FrameView? _rightFrame;
    private Label? _statusMessage;
    private Label? _infoLabel;
    private int _statusClearToken;
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

        // Load registry synchronously before building UI
        InstallRegistry? initialRegistry = null;
        try
        {
            initialRegistry = _registry.LoadAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Will show empty list; errors surface through Doctor
        }

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
            Height = Dim.Fill(2)
        };
        _leftFrame = leftFrame;

        _rightFrame = new FrameView
        {
            Title = "Details",
            X = Pos.Right(leftFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2)
        };

        _installsList = new InstallsListView();
        _installsList.SelectionChanged += OnInstallSelectionChanged;
        leftFrame.Add(_installsList);

        _detailsView = new DetailsView();
        _browseView = new BrowseView(_fetcher, app);
        _browseView.VersionSelected += OnBrowseVersionSelected;
        _rightFrame.Add(_detailsView);

        // Populate install list from pre-loaded registry
        if (initialRegistry is not null)
        {
            _installsList.SetInstalls(initialRegistry);
            UpdateDetailsForSelection();
        }

        // Info bar with status message and version info
        var infoBar = new View
        {
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false
        };

        _statusMessage = new Label
        {
            Text = "",
            X = 1,
            Y = 0,
            Width = Dim.Percent(40)
        };

        var infoText = BuildInfoText(initialRegistry);
        _infoLabel = new Label
        {
            Text = infoText,
            X = Pos.AnchorEnd(infoText.Length + 1),
            Y = 0
        };

        infoBar.Add(_statusMessage, _infoLabel);

        var statusBar = new StatusBar
        {
            Y = Pos.AnchorEnd(1)
        };
        statusBar.Add(
            new Shortcut(Key.F1, "Browse", () => EnterBrowseMode(app), ""),
            new Shortcut(Key.F2, "Install", () => ShowInstallDialog(app), ""),
            new Shortcut(Key.F3, "Doctor", () => ShowDoctorDialog(app), ""),
            new Shortcut(Key.Q.WithCtrl, "Quit", () => RequestStop(window), "")
        );

        window.Add(leftFrame, _rightFrame, infoBar, statusBar);

        window.KeyDown += (s, e) => HandleGlobalKey(e, app, window);

        // Use Application-level KeyDown to intercept a/d/r BEFORE the
        // focused view (e.g., the inner ListView) gets a chance to swallow
        // them as type-ahead navigation.
        app.Keyboard.KeyDown += (_, key) => HandleActionKey(key, app, window);

        app.Run(window);
        window.Dispose();
        ((IDisposable)app).Dispose();

        return 0;
    }

    private void HandleGlobalKey(Key key, IApplication app, Window window)
    {
        if (key == Key.Tab)
        {
            TogglePanelFocus();
            key.Handled = true;
        }
        else if (key == Key.Esc && _browseMode)
        {
            ExitBrowseMode();
            _leftFrame?.SetFocus();
            key.Handled = true;
        }
        else if (key == Key.Q && !(_browseView?.HasFocus ?? false))
        {
            RequestStop(window);
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

    private void TogglePanelFocus()
    {
        if (_leftFrame is null || _rightFrame is null) return;

        if (_leftFrame.HasFocus)
        {
            _rightFrame.SetFocus();
        }
        else
        {
            // Moving focus to the Installs list — if we're in Browse,
            // exit it first so Details becomes visible alongside the
            // selected install (spec invariant: focus on Installs ⇒
            // right panel = Details).
            if (_browseMode)
            {
                ExitBrowseMode();
            }
            _leftFrame.SetFocus();
        }
    }

    private bool IsBrowseFocused() => _browseView?.HasFocus ?? false;

    private void HandleActionKey(Key key, IApplication app, View mainWindow)
    {
        if (key.Handled) return;
        if (key != Key.A && key != Key.D && key != Key.R) return;
        // Skip when a modal dialog is on top (Install/Doctor/Help/MessageBox)
        // so letter keys flow into the modal's text fields and buttons as usual.
        if (app.TopRunnableView != mainWindow) return;
        // Skip when Browse's filter text field has focus — user is typing
        // a search term, not invoking an action.
        if (_browseView?.IsFilterFocused == true) return;

        if (IsBrowseFocused())
        {
            var verb = key == Key.A ? "activate"
                : key == Key.D ? "deactivate"
                : "remove";
            SetStatus($"Switch to Installs (Tab) to {verb}");
            key.Handled = true;
            return;
        }

        var entry = _installsList?.SelectedEntry;
        if (entry is null)
        {
            SetStatus("No install selected");
            key.Handled = true;
            return;
        }

        if (key == Key.A)
        {
            if (entry.IsActive) SetStatus($"{entry.Version} is already active");
            else _ = ActivateSelectedAsync(app);
        }
        else if (key == Key.D)
        {
            _ = DeactivateAsync(app);
        }
        else if (key == Key.R)
        {
            _ = RemoveSelectedAsync(app);
        }
        key.Handled = true;
    }

    private void RestoreBrowseFocusIfNeeded()
    {
        if (_browseMode && _browseView is not null && !_browseView.HasFocus)
        {
            _browseView.FocusList();
        }
    }

    private void EnterBrowseMode(IApplication app)
    {
        if (_browseView is null || _rightFrame is null) return;

        if (_browseMode)
        {
            // Already in Browse — just ensure focus is on the list,
            // in case it had drifted (e.g., into the filter).
            _browseView.FocusList();
            return;
        }

        _browseMode = true;
        _rightFrame.RemoveAll();
        _rightFrame.Title = "Browse Versions";
        _rightFrame.Add(_browseView);
        _browseView.FocusList();

        SetStatus("Loading versions...", durationMs: 10000);
        _ = _browseView.LoadVersionsAsync().ContinueWith(_ =>
            _app?.Invoke(() => SetStatus("Versions loaded")));
    }

    private void ExitBrowseMode()
    {
        if (_detailsView is null || _rightFrame is null) return;
        if (!_browseMode) return;

        _browseMode = false;
        _rightFrame.RemoveAll();
        _rightFrame.Title = "Details";
        _rightFrame.Add(_detailsView);
        UpdateDetailsForSelection();
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
        var installed = dialog.Success;
        dialog.Dispose();
        if (installed)
        {
            _ = RefreshRegistryAsync(_app!);
        }
        RestoreBrowseFocusIfNeeded();
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
                UpdateInfoBar(registry);
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

            // Activating a global install -- or switching away from one -- writes
            // machine-wide state. RemoveActiveAsync below clears GODOT_HOME with an
            // EnvironmentVariableTarget.Machine write for a global entry, which throws
            // SecurityException ("Requested registry access is not allowed") when this
            // process is not elevated. Without this branch the TUI could activate a
            // global install and then never activate anything again, because every
            // later attempt threw before MarkActive could move the pointer off it.
            if (ElevatedActivator.IsRequired(entry.Scope, previous?.Scope))
            {
                var elevated = await ElevatedActivator.RunAsync(entry.Id, createDesktopShortcut: false);
                if (!elevated.Succeeded)
                {
                    app.Invoke(() => MessageBox.ErrorQuery(
                        app,
                        "Error",
                        $"Activation failed: {elevated.Error}"
                            + (elevated.Hint is null ? "" : $"\n{elevated.Hint}"),
                        "OK"));
                    return;
                }

                // The elevated child performed the activation and saved the registry.
                await RefreshRegistryAsync(app);
                app.Invoke(() =>
                {
                    MessageBox.Query(app, "Activated", $"Activated {entry.Version} ({entry.Edition})", "OK");
                    SetStatus($"Activated {entry.Version}");
                });
                return;
            }

            if (previous is not null)
            {
                await _environment.RemoveActiveAsync(previous);
            }

            await _environment.ApplyActiveAsync(entry);
            registry.MarkActive(entry.Id);
            await _registry.SaveAsync(registry);

            // A leftover machine-wide shim outranks this activation on PATH, so
            // `godot` would keep launching the previous install with nothing on
            // screen explaining why. Surfaced in the dialog rather than written to
            // AnsiConsole, which Terminal.Gui owns while the TUI is running.
            var shadowWarning = BuildShimShadowWarning(entry.Scope);

            app.Invoke(() =>
            {
                MessageBox.Query(
                    app,
                    "Activated",
                    $"Activated {entry.Version} ({entry.Edition})"
                        + (shadowWarning is null ? "" : $"\n\n{shadowWarning}"),
                    "OK");
            });
            await RefreshRegistryAsync(app);
            app.Invoke(() => SetStatus($"Activated {entry.Version}"));
        }
        catch (Exception ex)
        {
            app.Invoke(() =>
            {
                MessageBox.ErrorQuery(app, "Error", TuiErrorPresentation.BuildErrorBody("Activation failed", ex), "OK");
            });
        }
    }

    /// <summary>
    /// Null when nothing shadows the activation, otherwise the message to append to
    /// the confirmation dialog.
    /// </summary>
    private string? BuildShimShadowWarning(InstallScope activatedScope)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var globalShimDir = _paths.GetShimDirectory(InstallScope.Global);
        var globalShim = Path.Combine(globalShimDir, "godot.cmd");

        return ShimShadowing.WouldShadow(
            activatedScope,
            File.Exists(globalShim),
            globalShimDir,
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine))
            ? ShimShadowing.BuildWarning(globalShim)
            : null;
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

            // Clearing GODOT_HOME for a global entry is a machine-scope write that
            // throws SecurityException unelevated, so without this the TUI could not
            // deactivate a global install at all -- the same hole the CLI had.
            if (ElevatedDeactivator.IsRequired(active.Scope))
            {
                var elevated = await ElevatedDeactivator.RunAsync(active.Id);
                if (!elevated.Succeeded)
                {
                    app.Invoke(() => MessageBox.ErrorQuery(
                        app,
                        "Error",
                        $"Deactivate failed: {elevated.Error}"
                            + (elevated.Hint is null ? "" : $"\n{elevated.Hint}"),
                        "OK"));
                    return;
                }

                await RefreshRegistryAsync(app);
                app.Invoke(() =>
                {
                    MessageBox.Query(app, "Deactivated", $"Deactivated {active.Version} ({active.Edition})", "OK");
                    SetStatus($"Deactivated {active.Version}");
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
            app.Invoke(() => SetStatus($"Deactivated {active.Version}"));
        }
        catch (Exception ex)
        {
            app.Invoke(() =>
            {
                MessageBox.ErrorQuery(app, "Error", TuiErrorPresentation.BuildErrorBody("Deactivation failed", ex), "OK");
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
            // A global-scope removal writes the machine-wide registry and deletes
            // files under %ProgramFiles%. Elevate for the whole operation instead of
            // failing on the first write, which previously left the TUI user with a
            // "re-run elevated" hint they could only act on by quitting the TUI.
            if (ElevatedRemover.IsRequired(entry.Scope))
            {
                var elevated = await ElevatedRemover.RunAsync(entry.Id, deleteFiles);
                if (!elevated.Succeeded)
                {
                    app.Invoke(() => MessageBox.ErrorQuery(
                        app,
                        "Error",
                        $"Remove failed: {elevated.Error}"
                            + (elevated.Hint is null ? "" : $"\n{elevated.Hint}"),
                        "OK"));
                    return;
                }

                await RefreshRegistryAsync(app);
                app.Invoke(() =>
                {
                    MessageBox.Query(app, "Removed", $"Removed {entry.Version} ({entry.Edition})", "OK");
                    SetStatus($"Removed {entry.Version}");
                });
                return;
            }

            var registry = await _registry.LoadAsync();

            if (entry.IsActive)
            {
                await _environment.RemoveActiveAsync(entry);
                registry.ClearActive();
            }

            string? deleteFailure = null;
            if (deleteFiles && Directory.Exists(entry.Path))
            {
                try
                {
                    Directory.Delete(entry.Path, recursive: true);
                }
                catch (Exception ex)
                {
                    // Best-effort, exactly as RemoveCommand treats it. Letting this
                    // throw aborts the removal before SaveAsync runs, and SaveAsync is
                    // what raises the hinted GodmanException for a global-scope entry --
                    // so throwing here replaces an actionable "re-run elevated" with a
                    // bare access-denied and leaves the install still registered.
                    deleteFailure = ex.Message;
                }
            }

            registry.Installs.RemoveAll(x => x.Id == entry.Id);
            await _registry.SaveAsync(registry);

            app.Invoke(() =>
            {
                MessageBox.Query(
                    app,
                    "Removed",
                    TuiErrorPresentation.BuildRemovedBody(entry.Version, entry.Edition, entry.Path, deleteFailure),
                    "OK");
            });
            await RefreshRegistryAsync(app);
            app.Invoke(() => SetStatus($"Removed {entry.Version}"));
        }
        catch (Exception ex)
        {
            app.Invoke(() =>
            {
                MessageBox.ErrorQuery(app, "Error", TuiErrorPresentation.BuildErrorBody("Remove failed", ex), "OK");
            });
        }
    }

    private void ShowInstallDialog(IApplication app)
    {
        var dialog = new InstallDialog(_installer, _urlBuilder, _paths, app);
        app.Run(dialog);
        var installed = dialog.Success;
        dialog.Dispose();
        if (installed)
        {
            _ = RefreshRegistryAsync(app);
            SetStatus("Install complete");
        }
        RestoreBrowseFocusIfNeeded();
    }

    private void ShowDoctorDialog(IApplication app)
    {
        var dialog = new DoctorDialog(_registry, _environment, _paths, app);
        app.Run(dialog);
        dialog.Dispose();
        RestoreBrowseFocusIfNeeded();
    }

    private void ShowHelpOverlay(IApplication app)
    {
        var overlay = new HelpOverlay();
        app.Run(overlay);
        overlay.Dispose();
    }

    private static string GetGodmanVersion()
    {
        return typeof(TuiApp).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "?";
    }

    private static string BuildInfoText(InstallRegistry? registry)
    {
        var version = GetGodmanVersion();
        var active = registry?.GetActive();
        var activeText = active is not null
            ? $"{active.Version} ({(active.Edition == InstallEdition.DotNet ? ".NET" : "Std")})"
            : "None";
        var count = registry?.Installs.Count ?? 0;
        return $"godman v{version} │ Active: {activeText} │ {count} installed";
    }

    private void SetStatus(string message, int durationMs = 3000)
    {
        if (_statusMessage is null || _app is null) return;

        var token = Interlocked.Increment(ref _statusClearToken);
        _statusMessage.Text = message;

        _ = Task.Run(async () =>
        {
            await Task.Delay(durationMs);
            if (Volatile.Read(ref _statusClearToken) == token)
            {
                _app.Invoke(() => _statusMessage.Text = "");
            }
        });
    }

    private void UpdateInfoBar(InstallRegistry? registry)
    {
        if (_infoLabel is null) return;
        var text = BuildInfoText(registry);
        _infoLabel.Text = text;
        _infoLabel.X = Pos.AnchorEnd(text.Length + 1);
    }
}
