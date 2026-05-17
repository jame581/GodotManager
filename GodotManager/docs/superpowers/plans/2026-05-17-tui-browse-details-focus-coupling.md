# TUI — Browse / Details Focus Coupling — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the F1 toggle with a focus-coupled right panel: focus on the Installs list always shows Details; F1 enters Browse; Tab/Esc exit back to Installs+Details. Block `a`/`d`/`r` with a status message while focus is in Browse, and preserve Browse mode across modal dialogs.

**Architecture:** Five small, sequenced edits across `TuiApp.cs`, `BrowseView.cs`, and `HelpOverlay.cs`. No new abstractions or events between TuiApp and BrowseView — Esc handling lives in `TuiApp.HandleGlobalKey` (which is hooked on `window.KeyDown`), and BrowseView only gains a tiny `FocusList()` helper. Verification is manual smoke testing (the codebase has no Terminal.Gui rendering tests by Phase 4 convention).

**Tech Stack:** .NET 10, Terminal.Gui v2 (`2.1.0`), Spectre.Console.Cli 0.55, xUnit (existing tests unaffected).

**Spec:** `GodotManager/docs/superpowers/specs/tui-browse-details-focus-coupling.md`

---

## File Structure

| File | Responsibility |
|---|---|
| `GodotManager/Tui/Views/BrowseView.cs` | Add `FocusList()` helper that focuses the result `ListView`. No behavioural change on its own. |
| `GodotManager/Tui/TuiApp.cs` | Replace `ToggleBrowseMode` with `EnterBrowseMode`/`ExitBrowseMode`. Add Esc handling, a/d/r focus-gating, dialog-close Browse-focus restoration. |
| `GodotManager/Tui/Views/HelpOverlay.cs` | Update the static help text to reflect new F1/Esc semantics and remove the stale `/ Focus filter field` line that never had a binding. |

---

## Reference: Current State (as of HEAD)

For copy/paste accuracy, the engineer should keep these snippets handy.

`TuiApp.cs:141` — current F1 shortcut wiring:
```csharp
new Shortcut(Key.F1, "Browse", () => ToggleBrowseMode(app), ""),
```

`TuiApp.cs:158-190` — current `HandleGlobalKey`:
```csharp
private void HandleGlobalKey(Key key, IApplication app, Window window)
{
    if (key == Key.Tab)
    {
        TogglePanelFocus();
        key.Handled = true;
    }
    else if (key == Key.Q && !(_browseView?.HasFocus ?? false))
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
```

`TuiApp.cs:197-205` — current `TogglePanelFocus`:
```csharp
private void TogglePanelFocus()
{
    if (_leftFrame is null || _rightFrame is null) return;

    if (_leftFrame.HasFocus)
        _rightFrame.SetFocus();
    else
        _leftFrame.SetFocus();
}
```

`TuiApp.cs:207-227` — current `ToggleBrowseMode`:
```csharp
private void ToggleBrowseMode(IApplication app)
{
    _browseMode = !_browseMode;
    _rightFrame!.RemoveAll();

    if (_browseMode)
    {
        _rightFrame.Title = "Browse Versions";
        _rightFrame.Add(_browseView!);
        _browseView!.SetFocus();
        SetStatus("Loading versions...", durationMs: 10000);
        _ = _browseView.LoadVersionsAsync().ContinueWith(_ =>
            _app?.Invoke(() => SetStatus("Versions loaded")));
    }
    else
    {
        _rightFrame.Title = "Details";
        _rightFrame.Add(_detailsView!);
        UpdateDetailsForSelection();
    }
}
```

---

## Task 1: Add `BrowseView.FocusList()` helper

**Why first:** Tasks 2 and 4 will call this method. Adding it first (as an unused public method) lets Task 2 land without forward references.

**Files:**
- Modify: `GodotManager/Tui/Views/BrowseView.cs`

- [ ] **Step 1: Add the helper method**

Open `GodotManager/Tui/Views/BrowseView.cs`. After the `ApplyFilter()` method (which is the last method in the class), add the new helper just before the closing brace of the class:

```csharp
    public void FocusList()
    {
        _listView.SetFocus();
    }
```

The full bottom of the file should now read:

```csharp
    private void ApplyFilter()
    {
        // ... existing body unchanged ...
    }

    public void FocusList()
    {
        _listView.SetFocus();
    }
}
```

- [ ] **Step 2: Verify the project builds**

Run:
```bash
dotnet build GodotManager/GodotManager.csproj -nologo
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Verify tests still pass**

Run:
```bash
dotnet test -v minimal --nologo
```

Expected: `Passed!  - Failed: 0, Passed: 111, Skipped: 3, Total: 114`.

- [ ] **Step 4: Commit**

```bash
git add GodotManager/Tui/Views/BrowseView.cs
git commit -m "feat(tui): add BrowseView.FocusList() helper

Will be called by TuiApp's EnterBrowseMode to ensure Browse entry always
focuses the result list rather than whichever sub-widget last had focus."
```

---

## Task 2: Refactor TuiApp to focus-coupled Browse mode

**Files:**
- Modify: `GodotManager/Tui/TuiApp.cs`

This task replaces `ToggleBrowseMode` with separate `EnterBrowseMode`/`ExitBrowseMode` methods, makes F1 enter (not toggle), and makes Tab from Browse revert the panel to Details before moving focus to Installs. Esc handling is added so users have a second way out.

- [ ] **Step 1: Replace `ToggleBrowseMode` with two new methods**

In `GodotManager/Tui/TuiApp.cs`, find the existing `ToggleBrowseMode` method (lines ~207-227, see Reference section above). Delete it and add these two methods in its place:

```csharp
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
```

- [ ] **Step 2: Update the F1 status-bar shortcut**

In `TuiApp.Run()`, find the F1 shortcut on the status bar (around line 141):

```csharp
new Shortcut(Key.F1, "Browse", () => ToggleBrowseMode(app), ""),
```

Replace with:

```csharp
new Shortcut(Key.F1, "Browse", () => EnterBrowseMode(app), ""),
```

- [ ] **Step 3: Update `TogglePanelFocus` to exit Browse on Tab-to-Installs**

Find `TogglePanelFocus()` (lines ~197-205). Replace the entire method body with:

```csharp
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
```

- [ ] **Step 4: Add Esc handling in `HandleGlobalKey`**

In `HandleGlobalKey`, insert a new `else if` branch handling Esc. Place it directly after the `Key.Tab` branch so it fires before the action keys. The chain should look like:

```csharp
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
        else if (key == Key.A)
        {
            _ = ActivateSelectedAsync(app);
            key.Handled = true;
        }
        // ... rest unchanged for now (Task 3 will update a/d/r) ...
```

- [ ] **Step 5: Build**

```bash
dotnet build GodotManager/GodotManager.csproj -nologo
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Run the existing test suite (sanity check)**

```bash
dotnet test -v minimal --nologo
```

Expected: `Passed!  - Failed: 0, Passed: 111, Skipped: 3, Total: 114`. None of the tests exercise the TUI, so they should all still pass.

- [ ] **Step 7: Manual smoke test the new behaviour**

Run the TUI:
```bash
dotnet run --project GodotManager -- tui
```

Walk through these scenarios and confirm each:

1. App starts → right panel says **Details**, focus indicator on Installs frame. ✓
2. Press **F1** → right panel switches to **Browse Versions**, focus moves into Browse, "Loading versions..." appears in status area. ✓
3. Press **Tab** from Browse → right panel reverts to **Details**, focus moves to Installs. ✓
4. Press **F1** then **Esc** → right panel reverts to **Details**, focus on Installs. ✓
5. Press **F1**, Tab into the filter `TextField`, type a few characters, press **Tab** → exits Browse cleanly to Installs+Details (filter text is preserved internally — it will still be there next time you enter Browse). ✓
6. Press **F1** again from step 5 → right panel = Browse, filter text from step 5 still present, focus is on the **result list**, not the filter. ✓
7. Press **F1** while already in Browse → no visible change; focus stays on / returns to the list. ✓
8. Press **F1**, Tab into the filter, press **Esc** → should exit Browse to Installs+Details. **Contingency:** if Esc is consumed by the `TextField` (likely behaviour: focuses the parent, clears nothing, doesn't exit), the global handler won't fire. In that case, add this `KeyDown` handler to `BrowseView`'s constructor (after `Add(filterLabel, _filterField, _stableOnly, _listView);`):

   ```csharp
   _filterField.KeyDown += (_, e) =>
   {
       if (e == Key.Esc)
       {
           RequestExit?.Invoke(this, EventArgs.Empty);
           e.Handled = true;
       }
   };
   ```

   Then add to BrowseView's class body:

   ```csharp
   public event EventHandler? RequestExit;
   ```

   And in TuiApp.Run() after `_browseView.VersionSelected += OnBrowseVersionSelected;`:

   ```csharp
   _browseView.RequestExit += (_, _) =>
   {
       ExitBrowseMode();
       _leftFrame?.SetFocus();
   };
   ```

   Re-test scenario 8. If the global handler did fire on its own, skip this contingency.

Quit the TUI with **Ctrl+Q**.

- [ ] **Step 8: Commit**

```bash
git add GodotManager/Tui/TuiApp.cs
git commit -m "feat(tui): couple right panel mode to focus instead of toggling

F1 now \"enters\" Browse rather than toggling. Tab from Browse reverts the
right panel to Details and focuses Installs; Esc does the same. This
enforces the invariant that whenever focus is on the Installs list, the
Details view (with action key hints) is visible.

Spec: docs/superpowers/specs/tui-browse-details-focus-coupling.md"
```

---

## Task 3: Block `a`/`d`/`r` when focus is in Browse

**Files:**
- Modify: `GodotManager/Tui/TuiApp.cs`

When focus is in Browse, the user has no visible install context (no Details panel showing). Acting blind on the most-recently-selected install is error-prone. Block the action and show a discoverable status message instead.

- [ ] **Step 1: Add the `IsBrowseFocused()` helper**

In `GodotManager/Tui/TuiApp.cs`, add this helper anywhere in the class (a natural spot is right after `TogglePanelFocus`):

```csharp
    private bool IsBrowseFocused() => _browseView?.HasFocus ?? false;
```

`View.HasFocus` in Terminal.Gui v2 is true when the view *or any descendant* has focus, so this catches the cases where focus is on the filter field, the stable-only checkbox, or the result list.

- [ ] **Step 2: Update `a`/`d`/`r` branches in `HandleGlobalKey`**

Find the three action-key branches in `HandleGlobalKey` and replace them with the focus-gated versions:

```csharp
        else if (key == Key.A)
        {
            if (IsBrowseFocused())
            {
                SetStatus("Switch to Installs (Tab) to activate");
            }
            else
            {
                _ = ActivateSelectedAsync(app);
            }
            key.Handled = true;
        }
        else if (key == Key.D)
        {
            if (IsBrowseFocused())
            {
                SetStatus("Switch to Installs (Tab) to deactivate");
            }
            else
            {
                _ = DeactivateAsync(app);
            }
            key.Handled = true;
        }
        else if (key == Key.R)
        {
            if (IsBrowseFocused())
            {
                SetStatus("Switch to Installs (Tab) to remove");
            }
            else
            {
                _ = RemoveSelectedAsync(app);
            }
            key.Handled = true;
        }
```

- [ ] **Step 3: Build**

```bash
dotnet build GodotManager/GodotManager.csproj -nologo
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Manual smoke test**

```bash
dotnet run --project GodotManager -- tui
```

Pre-req: have at least one install registered. If not, install one via F2 first or run `godman install --version 4.5.1 --edition Standard --platform <yours> --activate` from another shell, then re-launch the TUI.

1. With focus on Installs, select an install, press **a** → activation prompt/result appears as before (no regression). ✓
2. Press **F1** to enter Browse. While focus is in Browse, press **a** → no activation, status bar shows `Switch to Installs (Tab) to activate` for ~3 s. ✓
3. Still in Browse, press **d** → status shows `Switch to Installs (Tab) to deactivate`. ✓
4. Still in Browse, press **r** → status shows `Switch to Installs (Tab) to remove`. ✓
5. Press **Tab** to exit Browse, then **a** → activation works on the currently-selected install. ✓
6. **In Browse**, Tab into the filter, type `a`/`d`/`r` → these go into the filter as text (filter has focus, action keys are still blocked at the global level). Verify the filter content shows the letters and the status bar still shows the "Switch to Installs..." message. ✓

Quit with **Ctrl+Q**.

- [ ] **Step 5: Commit**

```bash
git add GodotManager/Tui/TuiApp.cs
git commit -m "feat(tui): block a/d/r in Browse with discoverable status message

When focus is in BrowseView, the user can't see the selected install's
details, so silently acting on the install is error-prone. Show a status
message telling them to Tab back to Installs first."
```

---

## Task 4: Preserve Browse focus across modal dialogs

**Files:**
- Modify: `GodotManager/Tui/TuiApp.cs`

After F2 (Install), F3 (Doctor), or the BrowseView → Install flow, focus may drift if Terminal.Gui's modal restoration doesn't put it back exactly where it was. Add a small defensive helper that re-focuses the Browse list if the user was in Browse before the modal opened.

- [ ] **Step 1: Add the `RestoreBrowseFocusIfNeeded()` helper**

In `GodotManager/Tui/TuiApp.cs`, add this helper near `IsBrowseFocused()`:

```csharp
    private void RestoreBrowseFocusIfNeeded()
    {
        if (_browseMode && _browseView is not null && !_browseView.HasFocus)
        {
            _browseView.FocusList();
        }
    }
```

- [ ] **Step 2: Call it from `ShowInstallDialog`**

Find the existing `ShowInstallDialog` (around line 399) and replace its body with:

```csharp
    private void ShowInstallDialog(IApplication app)
    {
        var dialog = new InstallDialog(_installer, _urlBuilder, _paths, app);
        app.Run(dialog);
        dialog.Dispose();
        _ = RefreshRegistryAsync(app);
        SetStatus("Install complete");
        RestoreBrowseFocusIfNeeded();
    }
```

- [ ] **Step 3: Call it from `ShowDoctorDialog`**

Find `ShowDoctorDialog` (around line 408) and replace its body with:

```csharp
    private void ShowDoctorDialog(IApplication app)
    {
        var dialog = new DoctorDialog(_registry, _environment, _paths, app);
        app.Run(dialog);
        dialog.Dispose();
        RestoreBrowseFocusIfNeeded();
    }
```

- [ ] **Step 4: Call it from `OnBrowseVersionSelected`**

Find `OnBrowseVersionSelected` (around line 237) and replace its body with:

```csharp
    private void OnBrowseVersionSelected(object? sender, GodotRelease release)
    {
        var dialog = new InstallDialog(_installer, _urlBuilder, _paths, _app!);
        dialog.PresetVersion(release.Version);
        _app!.Run(dialog);
        dialog.Dispose();
        _ = RefreshRegistryAsync(_app!);
        RestoreBrowseFocusIfNeeded();
    }
```

- [ ] **Step 5: Build**

```bash
dotnet build GodotManager/GodotManager.csproj -nologo
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Manual smoke test**

```bash
dotnet run --project GodotManager -- tui
```

1. From the default state (Installs focused), press **F2**, cancel the dialog → focus returns to wherever it was (no regression). ✓
2. Press **F1** to enter Browse, then **F2** to open Install dialog, cancel → right panel is still Browse, focus is back on the Browse list. ✓
3. Press **F1**, then **F3** (Doctor), close Doctor → right panel is still Browse, focus is back on the Browse list. ✓
4. Press **F1**, scroll to a version, press **Enter** to open the pre-filled Install dialog, cancel → right panel is still Browse, focus is back on the Browse list. ✓

Quit with **Ctrl+Q**.

- [ ] **Step 7: Commit**

```bash
git add GodotManager/Tui/TuiApp.cs
git commit -m "fix(tui): restore Browse focus after closing modal dialogs

Terminal.Gui's modal restore can leave focus outside the underlying
BrowseView. Refocus the result list on dialog close when the user was
in Browse, so they return to where they started."
```

---

## Task 5: Update HelpOverlay text

**Files:**
- Modify: `GodotManager/Tui/Views/HelpOverlay.cs`

Reflect the new F1 semantics, document Esc, and remove the stale `/ Focus filter field` line (which has had no binding since the BrowseView refactor — pre-existing documentation bug).

- [ ] **Step 1: Replace the `Text =` literal**

Open `GodotManager/Tui/Views/HelpOverlay.cs`. Replace the entire `Text =` string literal (currently the multi-line `"=== Navigation ===\n" + ...` block) with:

```csharp
            Text =
                "=== Navigation ===\n" +
                "  Tab / Shift+Tab    Switch panels\n" +
                "  ↑ / ↓              Move in list\n" +
                "  Enter              Select / Confirm\n" +
                "  Esc                Close dialog / Exit Browse to Installs\n" +
                "\n" +
                "=== Actions ===\n" +
                "  a                  Activate selected install\n" +
                "  d                  Deactivate current install\n" +
                "  r                  Remove selected install\n" +
                "  (When focus is in Browse, a/d/r are blocked — press\n" +
                "   Tab first to return to the Installs list.)\n" +
                "\n" +
                "=== Views ===\n" +
                "  F1                 Open Browse panel\n" +
                "  F2                 Open Install dialog\n" +
                "  F3                 Open Doctor dialog\n" +
                "  ?                  Show this help\n" +
                "\n" +
                "=== Browse Panel ===\n" +
                "  Enter              Install selected version\n" +
                "  Tab / Esc          Exit Browse, return to Installs\n" +
                "\n" +
                "=== General ===\n" +
                "  q / Ctrl+Q         Quit"
```

- [ ] **Step 2: Build**

```bash
dotnet build GodotManager/GodotManager.csproj -nologo
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Manual smoke test**

```bash
dotnet run --project GodotManager -- tui
```

Press **?** to open the help overlay. Verify the rendered text matches the above (F1 says "Open Browse panel", Esc line is present, the `/ Focus filter field` line is gone, the `a/d/r` block has the parenthetical note). Press **Esc** to close the overlay; you should return to where you were. Quit with **Ctrl+Q**.

- [ ] **Step 4: Commit**

```bash
git add GodotManager/Tui/Views/HelpOverlay.cs
git commit -m "docs(tui): update help overlay for focus-coupled Browse panel

F1 now opens Browse (not toggles); Esc and Tab both exit Browse. Adds
a note that a/d/r are blocked while focus is in Browse. Removes the
stale \"/ Focus filter field\" line that has had no binding since
Phase 4."
```

---

## Final verification

After all five tasks are committed:

- [ ] **Run the full spec smoke test suite** from the spec's "Testing approach" section against the running TUI:

```bash
dotnet test -v minimal --nologo    # sanity: 111 passed
dotnet run --project GodotManager -- tui
```

Walk through scenarios 1–10 from the spec. All should pass. ✓

- [ ] **Glance at `git log --oneline -6`** to confirm the five commits landed in order and the working tree is clean.

---

## Summary

| Task | What it does | Files | Risk |
|---|---|---|---|
| 1 | Add `BrowseView.FocusList()` helper | `Views/BrowseView.cs` | Trivial |
| 2 | Refactor toggle → Enter/Exit; F1 enters; Tab/Esc exit | `TuiApp.cs` | Low — main UX change |
| 3 | Block a/d/r in Browse with status message | `TuiApp.cs` | Low |
| 4 | Restore Browse focus after modal dialogs | `TuiApp.cs` | Defensive |
| 5 | Update help overlay text | `Views/HelpOverlay.cs` | Cosmetic |

No tests are added or modified — the spec explicitly defers TUI logic extraction. If subsequent work extracts the focus-coupling state machine into a pure function, that's a separate refactor.
