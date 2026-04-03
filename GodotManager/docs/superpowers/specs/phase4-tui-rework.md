# Phase 4 — TUI Rework (Lazygit-Style)

## Status
Spec approved — ready for implementation.

## Problem
The current `tui` command is a sequential menu loop built on Spectre.Console prompts. It cannot support a persistent multi-panel layout, real-time list updates, or simultaneous keyboard navigation and content display. Users must navigate through menus to reach any action rather than operating on a live view of their installs.

## Goal
Replace the menu-driven TUI with a persistent, panel-based interface inspired by lazygit — a left panel listing installs and a right panel showing details and actions, with keyboard-driven navigation and modal dialogs for complex workflows.

## Decisions
| Question | Decision |
|---|---|
| Framework | Terminal.Gui v2 prerelease (`2.0.0-develop.*`) |
| Migration | Replace the existing `tui` command entirely (no parallel command) |
| Scope | Full feature parity: list, details, activate, deactivate, remove, browse, install, doctor |
| Steam integration | Not in this phase — added in Phase 3 later |
| Install / Doctor UI | Modal dialogs (pop-up overlays) |
| Architecture | Thin views + direct service delegation (Approach A) |

## Layout

### Main view (installs + details)
```
┌─────────────────────────────────────────────────────────────┐
│ Godot Manager v1.1.0                                   [?]  │
├──────────────────┬──────────────────────────────────────────┤
│  INSTALLS (3)    │  DETAILS                                 │
│  ──────────────  │  ────────────────────────────────────────│
│ ▶ 4.5.1 [std] *  │  Version:   4.5.1                        │
│   4.4.0 [.NET]   │  Edition:   Standard                     │
│   4.3.0 [std]    │  Platform:  Linux                        │
│                  │  Scope:     User                          │
│                  │  Path:      ~/.local/share/godman/...     │
│                  │  SHA256:    abc123def456...               │
│                  │  Added:     2024-01-15                    │
│                  │  Status:    ● ACTIVE                     │
│                  │                                          │
│                  │  [a]ctivate  [d]eactivate  [r]emove      │
├──────────────────┴──────────────────────────────────────────┤
│ F1:Browse  F2:Install  F3:Doctor  q:Quit  ?:Help            │
└─────────────────────────────────────────────────────────────┘
```

- Left panel: ~30% width, scrollable list of registered installs.
  - Active install marked with `*` and highlighted.
  - Shows: version, edition abbreviation (`[std]` / `[.NET]`), active marker.
- Right panel: ~70% width, details of the selected install.
  - Shows all `InstallEntry` fields: version, edition, platform, scope, path, SHA256, added date, active status.
  - Inline action hints at the bottom.
- Status bar: fixed bottom row with keybinding reference.
- Title bar: app name + version, `[?]` hint.

### Browse view (right panel swaps content)
```
│  AVAILABLE VERSIONS                                          │
│  ─────────────────────────────────────────────────────────  │
│  Version     Released      Type                             │
│  4.5.1       2024-09-01    stable                           │
│  4.5.0-rc1   2024-08-10    pre-release                      │
│  4.4.2        2024-07-01    stable                           │
│  ...                                                         │
│                                                              │
│  [enter] Install  [/] Filter  [s] Stable-only               │
```
- Fetches remote versions asynchronously via `GodotVersionFetcher` (same as CLI `fetch`).
- Shows spinner/progress while loading.
- Filter bar (`/`) for substring search on version string.
- `s` toggles stable-only (hides pre-releases).
- Enter on a remote version pre-fills the Install dialog.

## Keyboard Map

| Key | Action |
|---|---|
| ↑ / ↓ or j / k | Navigate install list (left panel) |
| Tab / Shift+Tab | Switch focus between panels |
| F1 | Switch right panel to Browse view |
| F2 | Open Install dialog (modal) |
| F3 | Open Doctor dialog (modal) |
| a | Activate selected install |
| d | Deactivate currently active install |
| r | Remove selected install (confirm dialog) |
| Enter | Confirm selection |
| Esc | Close modal / cancel |
| q or Ctrl+Q | Quit |
| ? | Toggle help overlay |

## File Structure

```
GodotManager/Tui/
  TuiApp.cs            # Application setup, main window, panel layout orchestration
  InstallsListView.cs  # Left panel — scrollable list of registered installs
  DetailsView.cs       # Right panel — selected install details and action hints
  BrowseView.cs        # Right panel (alternate) — remote version browser
  InstallDialog.cs     # Modal dialog — install wizard (version, edition, platform, scope, source)
  DoctorDialog.cs      # Modal dialog — doctor checks report
  HelpOverlay.cs       # ? overlay — keyboard shortcut reference
```

`TuiRunner.cs` is **replaced** by `TuiApp.cs` (and the supporting files above).

## Architecture

### Approach: Thin Views + Service Delegation
- Views are pure UI: layout, keyboard handling, rendering.
- Views call existing services directly (no new intermediary layer):
  - `RegistryService` — load/reload install list, activate, deactivate, remove.
  - `InstallerService` — install new versions with progress callback.
  - `EnvironmentService` — apply/remove shim + env var.
  - `GodotVersionFetcher` — fetch remote versions for Browse view.
  - `GodotDownloadUrlBuilder` — build download URLs in Install dialog.
- Services are injected into `TuiApp` and passed to views/dialogs as needed.
- No new services introduced.

### Data flow
1. `TuiCommand` (Spectre.Console.Cli) resolves DI, creates `TuiApp`, calls `RunAsync()`.
2. `TuiApp` initialises Terminal.Gui, builds the layout, passes services to panels.
3. `InstallsListView` loads registry on init and refreshes after mutating operations.
4. `BrowseView` fetches versions lazily (on first focus) with async progress display.
5. `InstallDialog` collects input and delegates to `InstallerService`.
6. After any operation that mutates state, `InstallsListView` reloads from registry.

## Implementation Steps

1. Add `Terminal.Gui` v2 prerelease package reference to `GodotManager.csproj`.
2. Delete `TuiRunner.cs`.
3. Create `TuiApp.cs` — main window, two-panel split layout, status bar, title bar.
4. Create `InstallsListView.cs` — list view with keyboard navigation, refresh method.
5. Create `DetailsView.cs` — detail fields for selected install, action key hints.
6. Create `BrowseView.cs` — async version list, filter bar, stable-only toggle.
7. Create `InstallDialog.cs` — wizard modal: version, edition, platform, scope, source, progress.
8. Create `DoctorDialog.cs` — modal: runs doctor checks, displays results table.
9. Create `HelpOverlay.cs` — full keybinding reference overlay.
10. Update `TuiCommand.cs` to instantiate `TuiApp` instead of `TuiRunner`.
11. Update `Program.cs` DI registrations if needed (no new services expected).
12. Update `PLAN.md` — mark Phase 4 complete once done.
13. Add/update tests for any testable TUI logic (view state, not rendering).

## Notes

### Clean Up command
The current menu-driven TUI has a "Clean up" menu item. In the new TUI this operation is **intentionally removed** from the TUI — it is available via the CLI `clean` command only. It is a destructive operation that doesn't benefit from a persistent panel interface.

### Async threading in Terminal.Gui v2
Terminal.Gui v2 runs on a single UI thread. Async service calls (installs, version fetching) must marshal results back to the UI thread using `Application.Invoke()` (v1) or the equivalent v2 pattern. Progress callbacks from `InstallerService` must similarly be dispatched. This is an implementation-time concern, not a design constraint.

## Out of Scope (This Phase)
- Steam installs integration (Phase 3).
- macOS support.
- Mouse support (keyboard-only for now).
- Clean up operation (use `godman clean` CLI command).
- TUI-specific unit tests for Terminal.Gui rendering (integration tested manually).
