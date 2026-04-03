# Phase 4 — TUI Rework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Spectre.Console menu-driven TUI with a persistent two-panel Terminal.Gui v2 interface featuring keyboard navigation, install list, details view, version browser, and modal dialogs for install/doctor workflows.

**Architecture:** Thin views calling existing services directly (RegistryService, InstallerService, EnvironmentService, GodotVersionFetcher, GodotDownloadUrlBuilder). No new service layer. TuiApp orchestrates panel layout and service injection. Views handle rendering and keyboard input only.

**Tech Stack:** Terminal.Gui v2 prerelease (`2.0.0-develop.5213`), .NET 10, existing Spectre.Console.Cli for command registration.

**Spec:** `GodotManager/docs/superpowers/specs/phase4-tui-rework.md`

---

## File Structure

```
GodotManager/Tui/
  TuiApp.cs              — App init, main window, two-panel layout, status bar, global key bindings
  InstallsListView.cs    — Left panel: scrollable list of registered installs with active marker
  DetailsView.cs         — Right panel (default): selected install details + action key hints
  BrowseView.cs          — Right panel (alternate): remote version browser with filter/stable toggle
  InstallDialog.cs       — Modal: install wizard (version, edition, platform, scope, source, progress)
  DoctorDialog.cs        — Modal: doctor checks report
  HelpOverlay.cs         — Modal: keyboard shortcut reference

GodotManager/Commands/
  TuiCommand.cs          — Modified: instantiate TuiApp instead of TuiRunner

GodotManager/
  GodotManager.csproj    — Modified: add Terminal.Gui package reference
  Program.cs             — No changes needed (DI registrations unchanged)
```

`TuiRunner.cs` is deleted after `TuiApp.cs` is complete.

---

## Reference: Key Service APIs

These are the existing service methods the TUI views will call. No modifications needed.

```csharp
// RegistryService
Task<InstallRegistry> LoadAsync(CancellationToken ct = default)
Task SaveAsync(InstallRegistry registry, CancellationToken ct = default)

// InstallRegistry (domain)
void MarkActive(Guid id)
InstallEntry? GetActive()
void ClearActive()
List<InstallEntry> Installs { get; set; }
Guid? ActiveId { get; set; }

// InstallerService
Task<InstallEntry> InstallWithElevationAsync(InstallRequest request, Action<double>? progress = null, CancellationToken ct = default)

// EnvironmentService
Task ApplyActiveAsync(InstallEntry entry, CancellationToken ct = default)
Task RemoveActiveAsync(InstallEntry? entry, CancellationToken ct = default)

// GodotVersionFetcher
Task<List<GodotRelease>> FetchReleasesAsync(bool skipCache = false, CancellationToken ct = default)

// GodotDownloadUrlBuilder
bool TryBuildUri(string version, InstallEdition edition, InstallPlatform platform, out Uri? uri, out string? error)

// InstallEntry (domain)
Guid Id, string Version, InstallEdition Edition, InstallPlatform Platform, InstallScope Scope,
string Path, string? Checksum, DateTimeOffset AddedAt, bool IsActive (transient)

// GodotRelease (record)
string Version, bool IsStable, bool HasStandard, bool HasDotNet, DateTimeOffset PublishedAt

// InstallRequest (record)
(string Version, InstallEdition Edition, InstallPlatform Platform, InstallScope Scope,
 Uri? DownloadUri, string? ArchivePath, string? InstallPath, bool Activate, bool Force, bool DryRun)
```

## Reference: Terminal.Gui v2 API Patterns

```csharp
// App lifecycle (instance-based, v2 pattern)
using (var app = Application.Create().Init())
{
    var window = new Window { Title = "My App" };
    window.Add(myView);
    app.Run(window);
    window.Dispose();
}

// Layout with Pos/Dim
view.X = 0;
view.Y = 0;
view.Width = Dim.Percent(30);
view.Height = Dim.Fill();

// Key bindings (v2 Command pattern)
AddCommand(Command.Accept, HandleAccept);
KeyBindings.Add(Key.Enter, Command.Accept);

// Focus: CanFocus defaults to false in v2
view.CanFocus = true;
view.TabStop = TabBehavior.TabStop;

// Async UI update from background thread
Application.Invoke(() => { /* update UI */ });

// Dialog (framework-managed)
app.Run<MyDialog>();
var result = app.GetResult<MyResult>();
```

---

## Task 1: Add Terminal.Gui Package Reference

**Files:**
- Modify: `GodotManager/GodotManager.csproj`

- [ ] **Step 1: Add Terminal.Gui v2 prerelease package**

```bash
cd /home/jame/source/repos/godot/GodotManager
dotnet add GodotManager/GodotManager.csproj package Terminal.Gui --prerelease
```

- [ ] **Step 2: Verify build succeeds**

```bash
dotnet build GodotManager/GodotManager.csproj
```

Expected: Build succeeded with 0 errors. Warnings about prerelease are fine.

- [ ] **Step 3: Verify tests still pass**

```bash
dotnet test -v minimal
```

Expected: Passed! 111 passed, 3 skipped.

- [ ] **Step 4: Commit**

```bash
git add GodotManager/GodotManager.csproj
git commit -m "deps: add Terminal.Gui v2 prerelease for TUI rework

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 2: Create TuiApp — Main Window and Panel Layout

**Files:**
- Create: `GodotManager/Tui/TuiApp.cs`

This is the orchestrator. It creates the Terminal.Gui application, builds the two-panel layout with a status bar, wires global keyboard shortcuts, and passes services to child views.

- [ ] **Step 1: Create TuiApp.cs with layout scaffolding**

```csharp
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using Terminal.Gui;

namespace GodotManager.Tui;

internal sealed class TuiApp
{
    private readonly RegistryService _registry;
    private readonly InstallerService _installer;
    private readonly EnvironmentService _environment;
    private readonly AppPaths _paths;
    private readonly GodotDownloadUrlBuilder _urlBuilder;
    private readonly GodotVersionFetcher _fetcher;

    private InstallsListView? _installsList;
    private DetailsView? _detailsView;
    private BrowseView? _browseView;
    private View? _rightPanel;
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
        using var app = Application.Create().Init();

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

        _installsList = new InstallsListView(_registry);
        _installsList.SelectionChanged += OnInstallSelectionChanged;
        leftFrame.Add(_installsList);

        _detailsView = new DetailsView();
        _browseView = new BrowseView(_fetcher);
        _rightPanel = _detailsView;
        _rightFrame.Add(_detailsView);

        var statusBar = new StatusBar
        {
            Y = Pos.AnchorEnd(1)
        };
        statusBar.Add(
            new Shortcut(Key.F1, "Browse", () => ToggleBrowseMode(app)),
            new Shortcut(Key.F2, "Install", () => ShowInstallDialog(app)),
            new Shortcut(Key.F3, "Doctor", () => ShowDoctorDialog(app)),
            new Shortcut(Key.Q.WithCtrl, "Quit", () => app.RequestStop())
        );

        window.Add(leftFrame, _rightFrame, statusBar);

        window.KeyDown += (s, e) => HandleGlobalKey(e, app);

        _ = LoadInstallsAsync();

        app.Run(window);
        window.Dispose();

        return 0;
    }

    private void HandleGlobalKey(Key key, IApplication app)
    {
        if (key == Key.Q && !(_browseView?.HasFocus ?? false))
        {
            app.RequestStop();
            key.Handled = true;
        }
        else if (key == Key.A)
        {
            _ = ActivateSelectedAsync();
            key.Handled = true;
        }
        else if (key == Key.D)
        {
            _ = DeactivateAsync();
            key.Handled = true;
        }
        else if (key == Key.R)
        {
            _ = RemoveSelectedAsync(app);
            key.Handled = true;
        }
        else if (key == (Key)((int)'?'))
        {
            ShowHelpOverlay(app);
            key.Handled = true;
        }
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

    private void UpdateDetailsForSelection()
    {
        var selected = _installsList?.SelectedEntry;
        _detailsView?.ShowEntry(selected);
    }

    private async Task LoadInstallsAsync()
    {
        try
        {
            var registry = await _registry.LoadAsync();
            Application.Invoke(() =>
            {
                _installsList?.SetInstalls(registry);
                UpdateDetailsForSelection();
            });
        }
        catch (Exception ex)
        {
            Application.Invoke(() =>
            {
                MessageBox.ErrorQuery("Error", $"Failed to load registry: {ex.Message}", "OK");
            });
        }
    }

    private async Task RefreshRegistryAsync()
    {
        await LoadInstallsAsync();
    }

    private async Task ActivateSelectedAsync()
    {
        var entry = _installsList?.SelectedEntry;
        if (entry is null) return;
        if (entry.IsActive) return;

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

            Application.Invoke(() =>
            {
                MessageBox.Query("Activated", $"Activated {entry.Version} ({entry.Edition})", "OK");
            });
            await RefreshRegistryAsync();
        }
        catch (Exception ex)
        {
            Application.Invoke(() =>
            {
                MessageBox.ErrorQuery("Error", $"Activation failed: {ex.Message}", "OK");
            });
        }
    }

    private async Task DeactivateAsync()
    {
        try
        {
            var registry = await _registry.LoadAsync();
            var active = registry.GetActive();
            if (active is null)
            {
                Application.Invoke(() =>
                {
                    MessageBox.Query("Info", "No active install to deactivate.", "OK");
                });
                return;
            }

            await _environment.RemoveActiveAsync(active);
            registry.ClearActive();
            await _registry.SaveAsync(registry);

            Application.Invoke(() =>
            {
                MessageBox.Query("Deactivated", $"Deactivated {active.Version} ({active.Edition})", "OK");
            });
            await RefreshRegistryAsync();
        }
        catch (Exception ex)
        {
            Application.Invoke(() =>
            {
                MessageBox.ErrorQuery("Error", $"Deactivation failed: {ex.Message}", "OK");
            });
        }
    }

    private async Task RemoveSelectedAsync(IApplication app)
    {
        var entry = _installsList?.SelectedEntry;
        if (entry is null) return;

        var confirm = MessageBox.Query(
            "Remove Install",
            $"Remove {entry.Version} ({entry.Edition})?\nFiles at: {entry.Path}",
            "Remove (keep files)", "Remove + Delete files", "Cancel");

        if (confirm == 2) return;
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

            Application.Invoke(() =>
            {
                MessageBox.Query("Removed", $"Removed {entry.Version} ({entry.Edition})", "OK");
            });
            await RefreshRegistryAsync();
        }
        catch (Exception ex)
        {
            Application.Invoke(() =>
            {
                MessageBox.ErrorQuery("Error", $"Remove failed: {ex.Message}", "OK");
            });
        }
    }

    private void ShowInstallDialog(IApplication app)
    {
        var dialog = new InstallDialog(_installer, _urlBuilder, _paths);
        app.Run(dialog);
        dialog.Dispose();
        _ = RefreshRegistryAsync();
    }

    private void ShowDoctorDialog(IApplication app)
    {
        var dialog = new DoctorDialog(_registry, _environment, _paths);
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
```

- [ ] **Step 2: Verify it compiles (with stub files)**

This won't compile yet because it depends on `InstallsListView`, `DetailsView`, `BrowseView`, `InstallDialog`, `DoctorDialog`, and `HelpOverlay` which don't exist yet. We'll verify compilation after Task 3-8. For now, just verify the file has no syntax errors by reviewing it.

- [ ] **Step 3: Commit**

```bash
git add GodotManager/Tui/TuiApp.cs
git commit -m "feat(tui): add TuiApp main window with two-panel layout

Orchestrates Terminal.Gui v2 app lifecycle, left/right panel split,
status bar shortcuts, and global keyboard handlers. Delegates to
existing services for all business operations.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 3: Create InstallsListView — Left Panel

**Files:**
- Create: `GodotManager/Tui/InstallsListView.cs`

Scrollable list of registered installs. Fires `SelectionChanged` when the user navigates. Supports j/k vim navigation.

- [ ] **Step 1: Create InstallsListView.cs**

```csharp
using GodotManager.Domain;
using GodotManager.Services;
using Terminal.Gui;

namespace GodotManager.Tui;

internal sealed class InstallsListView : ListView
{
    private readonly RegistryService _registry;
    private List<InstallEntry> _entries = [];

    public event EventHandler<InstallEntry?>? SelectionChanged;

    public InstallEntry? SelectedEntry =>
        SelectedItem >= 0 && SelectedItem < _entries.Count
            ? _entries[SelectedItem]
            : null;

    public InstallsListView(RegistryService registry)
    {
        _registry = registry;

        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        TabStop = TabBehavior.TabStop;

        AddCommand(Command.Up, () => { MoveUp(); return true; });
        AddCommand(Command.Down, () => { MoveDown(); return true; });
        KeyBindings.Add(Key.K, Command.Up);
        KeyBindings.Add(Key.J, Command.Down);

        SelectedItemChanged += (s, e) =>
        {
            SelectionChanged?.Invoke(this, SelectedEntry);
        };
    }

    public void SetInstalls(InstallRegistry registry)
    {
        _entries = registry.Installs
            .OrderByDescending(x => x.AddedAt)
            .ToList();

        SetSource(_entries.Select(FormatEntry).ToList());

        if (_entries.Count > 0)
        {
            SelectedItem = 0;
            SelectionChanged?.Invoke(this, SelectedEntry);
        }
    }

    private static string FormatEntry(InstallEntry entry)
    {
        var active = entry.IsActive ? " *" : "  ";
        var edition = entry.Edition == InstallEdition.DotNet ? ".NET" : "std";
        return $"{active} {entry.Version} [{edition}]";
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add GodotManager/Tui/InstallsListView.cs
git commit -m "feat(tui): add InstallsListView left panel

Scrollable install list with j/k vim bindings, active marker,
and SelectionChanged event for driving the details panel.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 4: Create DetailsView — Right Panel (Default)

**Files:**
- Create: `GodotManager/Tui/DetailsView.cs`

Displays all fields of the selected `InstallEntry` and shows action key hints at the bottom.

- [ ] **Step 1: Create DetailsView.cs**

```csharp
using GodotManager.Domain;
using Terminal.Gui;

namespace GodotManager.Tui;

internal sealed class DetailsView : View
{
    private readonly Label _content;
    private readonly Label _actions;

    public DetailsView()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        TabStop = TabBehavior.TabStop;

        _content = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2),
            Text = "No install selected."
        };

        _actions = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(1),
            Height = 1,
            Text = "[a]ctivate  [d]eactivate  [r]emove"
        };

        Add(_content, _actions);
    }

    public void ShowEntry(InstallEntry? entry)
    {
        if (entry is null)
        {
            _content.Text = "No install selected.";
            return;
        }

        var status = entry.IsActive ? "● ACTIVE" : "○ Inactive";
        var checksum = string.IsNullOrEmpty(entry.Checksum)
            ? "(none)"
            : entry.Checksum.Length > 16
                ? entry.Checksum[..16] + "..."
                : entry.Checksum;

        _content.Text =
            $"Version:   {entry.Version}\n" +
            $"Edition:   {entry.Edition}\n" +
            $"Platform:  {entry.Platform}\n" +
            $"Scope:     {entry.Scope}\n" +
            $"Path:      {entry.Path}\n" +
            $"SHA256:    {checksum}\n" +
            $"Added:     {entry.AddedAt:yyyy-MM-dd HH:mm}\n" +
            $"Status:    {status}\n" +
            $"Id:        {entry.Id:N}";
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add GodotManager/Tui/DetailsView.cs
git commit -m "feat(tui): add DetailsView right panel

Displays all InstallEntry fields and action key hints for the
selected install.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 5: Create BrowseView — Right Panel (Alternate)

**Files:**
- Create: `GodotManager/Tui/BrowseView.cs`

Displays remote Godot versions fetched from GitHub. Supports `/` filter bar and `s` stable-only toggle. Enter on a version could trigger the install dialog (wired by TuiApp).

- [ ] **Step 1: Create BrowseView.cs**

```csharp
using GodotManager.Services;
using Terminal.Gui;

namespace GodotManager.Tui;

internal sealed class BrowseView : View
{
    private readonly GodotVersionFetcher _fetcher;
    private readonly ListView _list;
    private readonly Label _statusLabel;
    private readonly TextField _filterField;

    private List<GodotRelease> _allReleases = [];
    private List<GodotRelease> _filteredReleases = [];
    private bool _stableOnly = true;
    private bool _loaded;

    public event EventHandler<GodotRelease?>? VersionSelected;

    public BrowseView(GodotVersionFetcher fetcher)
    {
        _fetcher = fetcher;

        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;

        _statusLabel = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = 1,
            Text = "Press F1 to load versions..."
        };

        _filterField = new TextField
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = 1,
            Text = string.Empty,
            Visible = false
        };
        _filterField.TextChanged += (s, e) => ApplyFilter();

        _list = new ListView
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };

        var hints = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(1),
            Height = 1,
            Text = "[enter] Install  [/] Filter  [s] Stable-only  [Esc] Back"
        };

        Add(_statusLabel, _filterField, _list, hints);

        KeyBindings.Add(Key.S, Command.ToggleChecked);
        AddCommand(Command.ToggleChecked, () =>
        {
            _stableOnly = !_stableOnly;
            ApplyFilter();
            _statusLabel.Text = _stableOnly ? "Filter: stable only" : "Filter: all releases";
            return true;
        });

        KeyBindings.Add(Key.Slash, Command.Find);
        AddCommand(Command.Find, () =>
        {
            _filterField.Visible = !_filterField.Visible;
            if (_filterField.Visible)
            {
                _filterField.SetFocus();
            }
            else
            {
                _filterField.Text = string.Empty;
                ApplyFilter();
                _list.SetFocus();
            }
            return true;
        });

        _list.KeyDown += (s, e) =>
        {
            if (e == Key.Enter)
            {
                var idx = _list.SelectedItem;
                if (idx >= 0 && idx < _filteredReleases.Count)
                {
                    VersionSelected?.Invoke(this, _filteredReleases[idx]);
                }
                e.Handled = true;
            }
        };
    }

    public async Task LoadVersionsAsync()
    {
        if (_loaded) return;

        Application.Invoke(() =>
        {
            _statusLabel.Text = "Fetching versions from GitHub...";
        });

        try
        {
            var releases = await _fetcher.FetchReleasesAsync();
            _allReleases = releases;
            _loaded = true;

            Application.Invoke(() =>
            {
                _statusLabel.Text = $"{_allReleases.Count} versions loaded. [s] toggle stable-only ({(_stableOnly ? "ON" : "OFF")})";
                ApplyFilter();
            });
        }
        catch (Exception ex)
        {
            Application.Invoke(() =>
            {
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    private void ApplyFilter()
    {
        var filter = _filterField.Text?.ToString() ?? string.Empty;
        _filteredReleases = _allReleases
            .Where(r => !_stableOnly || r.IsStable)
            .Where(r => string.IsNullOrEmpty(filter) || r.Version.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .ToList();

        _list.SetSource(_filteredReleases.Select(FormatRelease).ToList());
    }

    private static string FormatRelease(GodotRelease r)
    {
        var type = r.IsStable ? "stable " : "preview";
        var editions = (r.HasStandard, r.HasDotNet) switch
        {
            (true, true) => "std+.NET",
            (true, false) => "std",
            (false, true) => ".NET",
            _ => "?"
        };
        return $"{r.Version,-16} {type}  {editions,-10} {r.PublishedAt:yyyy-MM-dd}";
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add GodotManager/Tui/BrowseView.cs
git commit -m "feat(tui): add BrowseView version browser panel

Async version fetching from GitHub, stable-only toggle, text filter,
and version selection event for triggering install.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 6: Create InstallDialog — Modal Install Wizard

**Files:**
- Create: `GodotManager/Tui/InstallDialog.cs`

A modal dialog that collects install parameters (version, edition, platform, scope, source) and runs the install via `InstallerService`. Shows a progress bar during download/extract.

- [ ] **Step 1: Create InstallDialog.cs**

```csharp
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using Terminal.Gui;

namespace GodotManager.Tui;

internal sealed class InstallDialog : Dialog
{
    private readonly InstallerService _installer;
    private readonly GodotDownloadUrlBuilder _urlBuilder;
    private readonly AppPaths _paths;

    private readonly TextField _versionField;
    private readonly RadioGroup _editionRadio;
    private readonly RadioGroup _platformRadio;
    private readonly RadioGroup _scopeRadio;
    private readonly CheckBox _activateCheck;
    private readonly CheckBox _forceCheck;
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;

    public InstallDialog(InstallerService installer, GodotDownloadUrlBuilder urlBuilder, AppPaths paths)
    {
        _installer = installer;
        _urlBuilder = urlBuilder;
        _paths = paths;

        Title = "Install Godot";
        Width = Dim.Percent(60);
        Height = Dim.Percent(70);

        var y = 0;

        Add(new Label { X = 1, Y = y, Text = "Version:" });
        _versionField = new TextField
        {
            X = 14,
            Y = y,
            Width = Dim.Fill(2),
            Height = 1,
            Text = string.Empty
        };
        Add(_versionField);
        y += 2;

        Add(new Label { X = 1, Y = y, Text = "Edition:" });
        _editionRadio = new RadioGroup
        {
            X = 14,
            Y = y,
            RadioLabels = ["Standard", ".NET"],
            Orientation = Orientation.Horizontal
        };
        Add(_editionRadio);
        y += 2;

        Add(new Label { X = 1, Y = y, Text = "Platform:" });
        var defaultPlatform = OperatingSystem.IsWindows() ? 0 : 1;
        _platformRadio = new RadioGroup
        {
            X = 14,
            Y = y,
            RadioLabels = ["Windows", "Linux"],
            SelectedItem = defaultPlatform,
            Orientation = Orientation.Horizontal
        };
        Add(_platformRadio);
        y += 2;

        Add(new Label { X = 1, Y = y, Text = "Scope:" });
        _scopeRadio = new RadioGroup
        {
            X = 14,
            Y = y,
            RadioLabels = ["User", "Global (admin)"],
            Orientation = Orientation.Horizontal
        };
        Add(_scopeRadio);
        y += 2;

        _activateCheck = new CheckBox
        {
            X = 1,
            Y = y,
            Text = "Activate after install",
            CheckedState = CheckState.Checked
        };
        Add(_activateCheck);
        y++;

        _forceCheck = new CheckBox
        {
            X = 1,
            Y = y,
            Text = "Force overwrite if exists",
            CheckedState = CheckState.UnChecked
        };
        Add(_forceCheck);
        y += 2;

        _progressBar = new ProgressBar
        {
            X = 1,
            Y = y,
            Width = Dim.Fill(2),
            Height = 1,
            Fraction = 0f,
            Visible = false
        };
        Add(_progressBar);
        y++;

        _statusLabel = new Label
        {
            X = 1,
            Y = y,
            Width = Dim.Fill(2),
            Height = 1,
            Text = string.Empty
        };
        Add(_statusLabel);

        var installBtn = new Button { Text = "Install", IsDefault = true };
        installBtn.Accepting += (s, e) =>
        {
            _ = RunInstallAsync();
            e.Handled = true;
        };

        var cancelBtn = new Button { Text = "Cancel" };
        cancelBtn.Accepting += (s, e) =>
        {
            RequestStop();
            e.Handled = true;
        };

        AddButton(installBtn);
        AddButton(cancelBtn);
    }

    public void PresetVersion(string version)
    {
        _versionField.Text = version;
    }

    private async Task RunInstallAsync()
    {
        var version = _versionField.Text?.ToString()?.Trim();
        if (string.IsNullOrEmpty(version))
        {
            MessageBox.ErrorQuery("Validation", "Version is required.", "OK");
            return;
        }

        var edition = _editionRadio.SelectedItem == 0 ? InstallEdition.Standard : InstallEdition.DotNet;
        var platform = _platformRadio.SelectedItem == 0 ? InstallPlatform.Windows : InstallPlatform.Linux;
        var scope = _scopeRadio.SelectedItem == 0 ? InstallScope.User : InstallScope.Global;
        var activate = _activateCheck.CheckedState == CheckState.Checked;
        var force = _forceCheck.CheckedState == CheckState.Checked;

        if (!_urlBuilder.TryBuildUri(version, edition, platform, out var uri, out var error))
        {
            MessageBox.ErrorQuery("URL Error", $"Could not build download URL: {error}", "OK");
            return;
        }

        _progressBar.Visible = true;
        _statusLabel.Text = "Installing...";

        var request = new InstallRequest(version, edition, platform, scope, uri, null, null, activate, force);

        try
        {
            var result = await _installer.InstallWithElevationAsync(request, pct =>
            {
                Application.Invoke(() =>
                {
                    _progressBar.Fraction = (float)(pct / 100.0);
                    _statusLabel.Text = $"Installing... {pct:F0}%";
                });
            });

            Application.Invoke(() =>
            {
                _progressBar.Fraction = 1f;
                _statusLabel.Text = $"Installed {result.Version} ({result.Edition}) → {result.Path}";
                MessageBox.Query("Success", $"Installed {result.Version} ({result.Edition}, {result.Platform})", "OK");
                RequestStop();
            });
        }
        catch (Exception ex)
        {
            Application.Invoke(() =>
            {
                _progressBar.Visible = false;
                _statusLabel.Text = string.Empty;
                MessageBox.ErrorQuery("Install Failed", ex.Message, "OK");
            });
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add GodotManager/Tui/InstallDialog.cs
git commit -m "feat(tui): add InstallDialog modal wizard

Collects version, edition, platform, scope with progress bar and
delegates to InstallerService. Supports preset version from browse view.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 7: Create DoctorDialog — Modal Doctor Report

**Files:**
- Create: `GodotManager/Tui/DoctorDialog.cs`

A modal dialog that runs the same diagnostic checks as the CLI `doctor` command and displays results in a scrollable text view.

- [ ] **Step 1: Create DoctorDialog.cs**

```csharp
using GodotManager.Config;
using GodotManager.Domain;
using GodotManager.Services;
using Terminal.Gui;

namespace GodotManager.Tui;

internal sealed class DoctorDialog : Dialog
{
    public DoctorDialog(RegistryService registry, EnvironmentService environment, AppPaths paths)
    {
        Title = "Doctor";
        Width = Dim.Percent(70);
        Height = Dim.Percent(80);

        var textView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = true,
            Text = "Running diagnostics..."
        };
        Add(textView);

        var closeBtn = new Button { Text = "Close", IsDefault = true };
        closeBtn.Accepting += (s, e) =>
        {
            RequestStop();
            e.Handled = true;
        };
        AddButton(closeBtn);

        _ = RunDiagnosticsAsync(registry, paths, textView);
    }

    private static async Task RunDiagnosticsAsync(RegistryService registry, AppPaths paths, TextView output)
    {
        try
        {
            var reg = await registry.LoadAsync();
            var lines = new System.Text.StringBuilder();

            lines.AppendLine("=== Registry ===");
            lines.AppendLine($"  Installs: {reg.Installs.Count}");
            var active = reg.GetActive();
            lines.AppendLine(active is not null
                ? $"  Active:   {active.Version} ({active.Edition}, {active.Platform})"
                : "  Active:   (none)");
            lines.AppendLine();

            lines.AppendLine("=== Environment ===");
            var godotHome = Environment.GetEnvironmentVariable("GODOT_HOME");
            lines.AppendLine($"  GODOT_HOME (process): {godotHome ?? "(not set)"}");

            if (OperatingSystem.IsWindows())
            {
                var userHome = Environment.GetEnvironmentVariable("GODOT_HOME", EnvironmentVariableTarget.User);
                var machineHome = Environment.GetEnvironmentVariable("GODOT_HOME", EnvironmentVariableTarget.Machine);
                lines.AppendLine($"  GODOT_HOME (user):    {userHome ?? "(not set)"}");
                lines.AppendLine($"  GODOT_HOME (machine): {machineHome ?? "(not set)"}");
            }
            lines.AppendLine();

            lines.AppendLine("=== Shims ===");
            foreach (var scope in new[] { InstallScope.User, InstallScope.Global })
            {
                var shimDir = paths.GetShimDirectory(scope);
                var shimName = OperatingSystem.IsWindows() ? "godot.cmd" : "godot";
                var shimPath = Path.Combine(shimDir, shimName);
                var exists = File.Exists(shimPath);
                lines.AppendLine($"  {scope} shim: {shimPath} {(exists ? "✓" : "✗ missing")}");
            }
            lines.AppendLine();

            lines.AppendLine("=== Paths ===");
            lines.AppendLine($"  Config:        {paths.ConfigDirectory}");
            lines.AppendLine($"  User installs: {paths.GetInstallRoot(InstallScope.User)}");
            lines.AppendLine($"  Global installs: {paths.GetInstallRoot(InstallScope.Global)}");
            lines.AppendLine();

            var legacy = paths.GetLegacyPaths();
            if (legacy.Count > 0)
            {
                lines.AppendLine("=== Legacy Paths (should migrate) ===");
                foreach (var (path, desc) in legacy)
                {
                    if (Directory.Exists(path) || File.Exists(path))
                    {
                        lines.AppendLine($"  ⚠ {desc}: {path}");
                    }
                }
            }

            Application.Invoke(() =>
            {
                output.Text = lines.ToString();
            });
        }
        catch (Exception ex)
        {
            Application.Invoke(() =>
            {
                output.Text = $"Error running diagnostics:\n{ex.Message}";
            });
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add GodotManager/Tui/DoctorDialog.cs
git commit -m "feat(tui): add DoctorDialog modal diagnostics

Shows registry status, environment variables, shim checks, paths,
and legacy path warnings in a scrollable text view.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 8: Create HelpOverlay — Keyboard Reference

**Files:**
- Create: `GodotManager/Tui/HelpOverlay.cs`

- [ ] **Step 1: Create HelpOverlay.cs**

```csharp
using Terminal.Gui;

namespace GodotManager.Tui;

internal sealed class HelpOverlay : Dialog
{
    public HelpOverlay()
    {
        Title = "Keyboard Shortcuts";
        Width = Dim.Percent(50);
        Height = Dim.Percent(60);

        var helpText = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ReadOnly = true,
            Text =
                "Navigation\n" +
                "  ↑/↓ or j/k    Navigate install list\n" +
                "  Tab/Shift+Tab  Switch focus between panels\n" +
                "\n" +
                "Actions\n" +
                "  a              Activate selected install\n" +
                "  d              Deactivate current active\n" +
                "  r              Remove selected install\n" +
                "\n" +
                "Views\n" +
                "  F1             Toggle Browse versions panel\n" +
                "  F2             Open Install dialog\n" +
                "  F3             Open Doctor dialog\n" +
                "\n" +
                "General\n" +
                "  ?              Toggle this help\n" +
                "  q / Ctrl+Q     Quit\n" +
                "  Esc            Close dialog / cancel\n"
        };
        Add(helpText);

        var closeBtn = new Button { Text = "Close", IsDefault = true };
        closeBtn.Accepting += (s, e) =>
        {
            RequestStop();
            e.Handled = true;
        };
        AddButton(closeBtn);
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add GodotManager/Tui/HelpOverlay.cs
git commit -m "feat(tui): add HelpOverlay keyboard reference dialog

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 9: Wire TuiCommand to TuiApp and Delete TuiRunner

**Files:**
- Modify: `GodotManager/Commands/TuiCommand.cs`
- Delete: `GodotManager/Tui/TuiRunner.cs`

- [ ] **Step 1: Update TuiCommand.cs to use TuiApp**

Replace the entire content of `GodotManager/Commands/TuiCommand.cs` with:

```csharp
using GodotManager.Config;
using GodotManager.Infrastructure;
using GodotManager.Services;
using GodotManager.Tui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GodotManager.Commands;

internal sealed class TuiCommand : AsyncCommand<TuiCommand.Settings>
{
    private readonly RegistryService _registry;
    private readonly InstallerService _installer;
    private readonly EnvironmentService _environment;
    private readonly AppPaths _paths;
    private readonly GodotDownloadUrlBuilder _urlBuilder;
    private readonly GodotVersionFetcher _fetcher;

    public TuiCommand(
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

    internal sealed class Settings : GlobalSettings { }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var app = new TuiApp(_registry, _installer, _environment, _paths, _urlBuilder, _fetcher);
            var result = app.Run();
            return Task.FromResult(result);
        }
        catch (System.Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks);
            return Task.FromResult(-1);
        }
    }
}
```

- [ ] **Step 2: Delete TuiRunner.cs**

```bash
rm GodotManager/Tui/TuiRunner.cs
```

- [ ] **Step 3: Verify build succeeds**

```bash
cd /home/jame/source/repos/godot/GodotManager
dotnet build GodotManager/GodotManager.csproj
```

Expected: Build succeeded with 0 errors. If there are compilation issues with Terminal.Gui v2 API differences (method names, constructor patterns), fix them now.

- [ ] **Step 4: Verify tests still pass**

```bash
dotnet test -v minimal
```

Expected: Passed! 111 passed, 3 skipped. The existing CLI command tests should be unaffected since they use `CliTestHarness` which registers command classes — and `TuiCommand` is not exercised by CLI tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(tui): wire TuiApp and remove TuiRunner

Replace Spectre.Console menu TUI with Terminal.Gui v2 persistent
two-panel interface. TuiCommand now delegates to TuiApp.Run().

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 10: Wire BrowseView → InstallDialog Version Preset

**Files:**
- Modify: `GodotManager/Tui/TuiApp.cs`

When user presses Enter on a version in BrowseView, open InstallDialog pre-filled with that version.

- [ ] **Step 1: Add VersionSelected handler in TuiApp.cs**

In the `TuiApp` constructor or `Run()` method, after creating `_browseView`, add:

```csharp
_browseView.VersionSelected += (s, release) =>
{
    if (release is null) return;
    var dialog = new InstallDialog(_installer, _urlBuilder, _paths);
    dialog.PresetVersion(release.Version);
    app.Run(dialog);
    dialog.Dispose();
    _ = RefreshRegistryAsync();
};
```

Note: This must be wired inside `Run()` where `app` is in scope. Move the event subscription there.

- [ ] **Step 2: Verify build succeeds**

```bash
dotnet build GodotManager/GodotManager.csproj
```

- [ ] **Step 3: Commit**

```bash
git add GodotManager/Tui/TuiApp.cs
git commit -m "feat(tui): wire browse → install version preset

Selecting a version in BrowseView opens InstallDialog pre-filled
with the selected version string.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 11: Integration Testing and API Fixes

**Files:**
- Possibly modify: any `GodotManager/Tui/*.cs` file

Terminal.Gui v2 is a prerelease and its API may have changed from what the migration guide documents. This task is for running the app, identifying and fixing compilation or runtime issues.

- [ ] **Step 1: Build and fix any compilation errors**

```bash
cd /home/jame/source/repos/godot/GodotManager
dotnet build GodotManager/GodotManager.csproj 2>&1
```

Fix any errors — common v2 prerelease issues:
- `Shortcut` constructor may differ — check if `StatusBar` uses `ShortcutCollection` or a different API.
- `RadioGroup.RadioLabels` might be `string[]` or `List<string>`.
- `ListView.SetSource()` may need `IListDataSource` instead of `IList`.
- `ProgressBar.Fraction` may be `float` or `double`.
- `Dialog.AddButton()` may not exist — use `Add()` and position manually.
- `Key` comparison operators may differ.
- `Application.Create()` may not exist — try `new Application()` or static `Application.Init()`.

Consult the Terminal.Gui v2 source on GitHub (`gui-cs/Terminal.Gui`) if needed.

- [ ] **Step 2: Run existing tests to ensure no regressions**

```bash
dotnet test -v minimal
```

Expected: 111 passed, 3 skipped.

- [ ] **Step 3: Quick manual smoke test**

```bash
cd /home/jame/source/repos/godot/GodotManager
dotnet run --project GodotManager -- tui
```

Verify:
- Window renders with left panel (installs) and right panel (details).
- Arrow keys / j/k navigate the install list.
- Tab switches focus between panels.
- F1 toggles browse mode (may fail to load if no network — that's OK).
- F2 opens install dialog.
- F3 opens doctor dialog.
- ? shows help overlay.
- q or Ctrl+Q quits.

- [ ] **Step 4: Commit any fixes**

```bash
git add -A
git commit -m "fix(tui): resolve Terminal.Gui v2 API compatibility issues

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 12: Update PLAN.md

**Files:**
- Modify: `GodotManager/docs/PLAN.md`

- [ ] **Step 1: Update Phase 4 status in PLAN.md**

Change the Phase 4 header section to reflect completion. Update:
- Phase 4 header: add `✅ COMPLETE` marker
- Add summary of what was implemented
- Move relevant items from "Next Steps" if any apply

Find the line:
```
## Phase 4 — TUI Rework (Lazygit-Style)
```

Replace it with:
```
## Phase 4 — TUI Rework (Lazygit-Style) ✅ COMPLETE
```

Add under the heading (before the subsections):
```
Replaced Spectre.Console menu-driven TUI with persistent two-panel Terminal.Gui v2 interface.
- **Left panel**: scrollable install list with active marker, j/k vim navigation.
- **Right panel**: details view (selected install) or browse view (remote versions).
- **Modals**: install wizard with progress bar, doctor diagnostics, help overlay.
- **Keyboard**: F1 browse, F2 install, F3 doctor, a/d/r activate/deactivate/remove, ? help, q quit.
- **Framework**: Terminal.Gui v2 prerelease with instance-based application model.
```

Also update the scope line at the top of the file to include Phase 4:
```
- Phase 1: core CLI ✅; Phase 2: TUI built with Spectre.Console.CLI ✅; Phase 4: TUI rework (Terminal.Gui) ✅; Phase 5: Linux packaging (partial) ✅; Phase 6: WinGet publishing ✅.
```

- [ ] **Step 2: Commit**

```bash
git add GodotManager/docs/PLAN.md
git commit -m "docs: mark Phase 4 TUI rework as complete

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Summary

| Task | Description | Files |
|---|---|---|
| 1 | Add Terminal.Gui package reference | `GodotManager.csproj` |
| 2 | Create TuiApp (main window + layout) | `Tui/TuiApp.cs` |
| 3 | Create InstallsListView (left panel) | `Tui/InstallsListView.cs` |
| 4 | Create DetailsView (right panel) | `Tui/DetailsView.cs` |
| 5 | Create BrowseView (version browser) | `Tui/BrowseView.cs` |
| 6 | Create InstallDialog (modal wizard) | `Tui/InstallDialog.cs` |
| 7 | Create DoctorDialog (modal report) | `Tui/DoctorDialog.cs` |
| 8 | Create HelpOverlay (key reference) | `Tui/HelpOverlay.cs` |
| 9 | Wire TuiCommand + delete TuiRunner | `Commands/TuiCommand.cs`, delete `Tui/TuiRunner.cs` |
| 10 | Wire BrowseView → InstallDialog preset | `Tui/TuiApp.cs` |
| 11 | Integration testing + API fixes | Any TUI file |
| 12 | Update PLAN.md | `docs/PLAN.md` |
