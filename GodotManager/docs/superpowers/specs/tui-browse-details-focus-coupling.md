# TUI — Browse / Details Focus Coupling

## Status
Spec approved — ready for implementation plan.

## Problem
The current TUI uses F1 as a toggle that swaps the right panel between
**Details** and **Browse**. When the user opens Browse, the right panel
fully replaces the Details view, which has two unintuitive consequences:

1. The selected install's details and the inline action-key hints
   (`[a]ctivate [d]eactivate [r]emove`) disappear, so the user perceives
   actions as unavailable even though the global hotkeys still fire on
   the install selected in the left panel.
2. If the user happens to focus the Browse filter `TextField`, the
   letters `a` / `d` / `r` are captured as text input — so the
   perception ("actions are unavailable") matches reality in that
   specific sub-state.

The user reports this is unintuitive: to act on an install while Browse
is open they have to press F1 again to dismiss Browse first.

## Goal
Make the right panel's content track focus, so that whenever the user
is interacting with the **Installs** list the Details view is visible
and the action-key hints are in plain sight. Browse becomes a focused
"go look at remote versions" mode that the user explicitly enters via
F1 and exits via Tab/Esc.

## Decisions

| Question | Decision |
|---|---|
| Right panel model | Focus-coupled. Focus on Installs ⇒ Details; focus on Browse ⇒ Browse. |
| F1 semantics | "Enter Browse" — not a toggle. Idempotent when already in Browse. |
| Exit Browse | Tab (focus → Installs), or Esc (always, regardless of which sub-widget has focus). |
| a/d/r while focus is in Browse | Blocked. Status bar shows `Switch to Installs (Tab) to activate / deactivate / remove` for ~3 s. |
| Browse fetch behaviour | Unchanged — lazy on first entry per session. |
| Install dialog (F2) | After it closes, restore the right panel to whichever mode was active when it opened. |
| Help overlay text | Updated to reflect new F1 semantics and Esc behaviour. |

## State model

The right panel is in exactly one of two modes: `Details` (default) or
`Browse`. Mode is derived from focus, not toggled.

| Trigger | Right panel | Focus |
|---|---|---|
| App start | Details | Installs |
| F1 (from anywhere) | Browse | BrowseView |
| Tab (from Browse) | Details | Installs |
| Esc (in Browse) | Details | Installs |
| Tab (from Installs) | Details (unchanged) | RightFrame (no interactive widgets in Details — focus is harmless) |
| F1 (already in Browse) | Browse (unchanged) | BrowseView (no-op) |
| Install dialog closes | Restores previous mode | Restores previous focus |

Invariant: **whenever focus is on Installs, the right panel shows
Details.** That is what keeps the selected install's details and the
action-key hints visible whenever the user might act.

## Keyboard map

| Key | Before | After |
|---|---|---|
| F1 | Toggle Browse on/off | Enter Browse + focus BrowseView |
| Tab | Swap left ↔ right focus | Same; leaving Browse also reverts right panel to Details |
| Esc (in Browse) | (no binding) | Always exits Browse to Installs + Details, regardless of which sub-widget has focus. |
| a / d / r | Global, on selected install | Same when focus is on Installs. When focus is on Browse: blocked + status message. |
| F2, F3, ?, Ctrl+Q | Unchanged | Unchanged. |

(The Browse filter `TextField` and `Stable only` `CheckBox` are always
visible; users edit the filter as a normal text field after Tabbing or
clicking into it.)

## Components touched

### `GodotManager/Tui/TuiApp.cs`
- Replace `ToggleBrowseMode(IApplication)` with `EnterBrowseMode(IApplication)` and `ExitBrowseMode()`.
  - `EnterBrowseMode` is idempotent — calling it while `_browseMode` is true is a no-op (still sets focus to `_browseView` in case focus drifted).
  - `ExitBrowseMode` is also idempotent.
- Wire a focus-change handler on `_leftFrame` that calls `ExitBrowseMode()` when the left frame (Installs) gains focus while `_browseMode` is `true`.
- `HandleGlobalKey`:
  - F1 → `EnterBrowseMode(app)` (was `ToggleBrowseMode`).
  - Esc → if `_browseMode`, exit Browse (BrowseView raises `RequestExit`; TuiApp calls `ExitBrowseMode`).
  - a / d / r → if focus is in `_browseView` (or any descendant), do not invoke the registry/environment action; instead `SetStatus("Switch to Installs (Tab) to <verb>")` and mark the key handled.
- `ShowInstallDialog(IApplication)` and the BrowseView → install flow: capture `_browseMode` before opening the dialog; restore it after the dialog closes (current behaviour calls `RefreshRegistryAsync` only).
- Status-bar shortcut label for F1: keep `"Browse"`. Tooltip wording (if any) updated.

### `GodotManager/Tui/Views/BrowseView.cs`
- Surface a `RequestExit` event that TuiApp subscribes to.
- Add an Esc key binding on the view (and on the inner `_filterField` /
  `_listView` if Esc isn't propagated up) that fires `RequestExit`.
- Add a `FocusList()` helper that moves focus to the result `ListView`.
  Called by `TuiApp.EnterBrowseMode` so that re-entering Browse always
  lands on the list, not on whichever sub-widget last had focus.

### `GodotManager/Tui/Views/HelpOverlay.cs`
- F1 line: `Open Browse versions` (was `Toggle Browse versions panel`).
- Add Esc line under "General": `Close dialog / exit Browse to Installs`.

### `GodotManager/Tui/TuiApp.cs` — status messages
- Re-use the existing `SetStatus(string, durationMs)` helper at the bottom of the file (already drives the `_statusMessage` label in the info bar). Default 3000 ms is fine.

## Edge cases

- **First-time Browse fetch.** Unchanged — `BrowseView.LoadVersionsAsync()` runs on first F1 entry per session. Subsequent F1 entries re-focus without re-fetching.
- **Acting "blind" via a/d/r.** Explicitly blocked when focus is in Browse (see decision table). The previously-supported but error-prone "act on whatever is selected in Installs while looking at Browse" path is removed in favour of an explicit Tab-first workflow.
- **Tab from Browse with filter focused.** Tab from Browse always exits to Installs regardless of which sub-widget (filter, list, checkbox) has focus. Filter text is preserved for the next Browse entry — only focus moves.
- **No installs registered.** Right panel shows the existing "No install selected." placeholder. F1 still enters Browse normally.
- **Install dialog from BrowseView.** When the user picks a remote version (`VersionSelected` → install dialog), the dialog opens over the current layout. After it closes:
  - The registry refresh runs (existing behaviour).
  - The right panel returns to whichever mode was active when the dialog opened (preserves user context).
- **Initial focus.** App start focuses Installs (current behaviour). No change required other than ensuring the focus-change handler doesn't fire spuriously on startup.

## Out of scope

- Visual restyling of any panel (colours, borders, frame titles beyond `Details`/`Browse Versions`).
- Mouse support.
- Saving the active right-panel mode across TUI sessions.
- Changes to InstallDialog, DoctorDialog, HelpOverlay layout.
- Refactoring `BrowseView` filter semantics beyond the Esc and `FocusList()` additions above.

## Testing approach

Terminal.Gui v2 rendering is not unit-tested in this project (Phase 4
established this convention). Verification is manual smoke testing:

1. App starts; right panel = Details, focus = Installs. ✓
2. F1 → right panel = Browse, focus = Browse. ✓
3. Tab → right panel = Details, focus = Installs. ✓
4. F1 → Esc → right panel = Details, focus = Installs. ✓
5. F1 → Tab into filter → type text → Esc → exits Browse; re-enter via F1, filter text is still there, focus is on the list. ✓
6. F1 → Tab into filter → type text → Tab again → exits Browse cleanly. ✓
7. Select an install, F1, press `a` → no activation, status message shown. ✓
8. Select an install, F1, Tab, press `a` → activation runs normally. ✓
9. F1, pick a remote version, install dialog runs, close it → right panel back in Browse. ✓
10. From Installs (default state), pick a remote version path via F1 → install dialog → close → since we were in Browse when dialog opened, return to Browse. (Same as 9; documents the rule from the inverse direction.) ✓

If logic warrants unit tests in the future, the focus-coupling state
transitions are pure functions of `(currentFocusFrame, currentMode)`
and could be extracted, but that refactor is not part of this scope.
