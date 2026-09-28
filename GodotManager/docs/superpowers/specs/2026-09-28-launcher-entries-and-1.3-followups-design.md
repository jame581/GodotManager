# Launcher entries per install + 1.3.0 follow-ups (1.4.0)

Status: approved design, awaiting spec review. Ships in 1.4.0 on
`fix/global-install-root-out-of-shim-dir`, alongside the global-root move.

> **Amended during planning (2026-09-28).** The implementation plan
> (`../plans/2026-09-28-launcher-entries-and-1.3-followups.md`, "Deviations from the
> spec") refines six points; where this document and that list disagree, the list wins:
> registry guard rebases the on-disk copy instead of keeping a snapshot; `GODMAN_HOME`
> (not a fixture-set `XDG_DATA_HOME`) isolates launcher paths, and on Windows the Start
> Menu/Desktop derive from it; `activate` always rewrites the entry; the Linux file name
> carries an 8-hex Id prefix; `remove` deletes the entry only after the registry save
> succeeds; the new kill-wait result drives only a warning. After independent plan
> review: launcher paths ignore `GODMAN_HOME`/`GODMAN_GLOBAL_ROOT` (test-only
> `GODMAN_LAUNCHER_ROOT` instead); no `StartupWMClass` or `%f` in the `.desktop` file;
> `install --activate` splits off an elevated activation when switching away from a
> global install; `--force` reinstalls delete the replaced entry's launcher file.

## Intent

**Stated by Jan:**
- On Fedora/GNOME, find installed Godot versions through the desktop launcher.
- Windows already creates Start Menu shortcuts, but removing an install can leave
  its shortcut behind — removal must clean it up.
- Fold the open 1.3.0 review follow-ups into this release.

**Decided in brainstorming:**
- One launcher entry **per installed version** (not one that follows the active install).
- Same model on Windows and Linux.
- Pre-1.4.0 installs get their entry when activated; `doctor` reports missing ones.
- On by default; `install --no-shortcut` opts out, and the opt-out is remembered.

**Success looks like:** after `godman install 4.7.2`, "Godot 4.7.2 (Standard)" appears in
the GNOME overview with the Godot icon, launches the editor, and the running window groups
under that entry. After `godman remove <id>` or `godman clean`, nothing godman created is
left in any launcher, on either OS.

## Part 1 — Launcher entries

### Lifecycle

| Event | Launcher entry (Start Menu `.lnk` / `.desktop`) | Windows desktop `.lnk` |
|---|---|---|
| `install` (incl. `--activate`) | created, unless `--no-shortcut` | — |
| `activate` | ensured (created if missing) unless the entry opted out | created if `--create-desktop-shortcut` |
| switching active away from X | untouched | X's deleted |
| `deactivate` | untouched | deleted |
| `remove` (active or not) | deleted | deleted |
| `clean` (per scope) | all godman entries in that scope deleted, plus the icon | deleted for each registered install |

The Start Menu entry changes owner: it now belongs to the *install*, not the *activation*,
so `RemoveActiveAsync` stops deleting it and only deletes the desktop shortcut.

### `LauncherService` (new, `Services/LauncherService.cs`)

Extracted from `EnvironmentService` (`CreateShortcuts` / `DeleteShortcuts` move out).
Public surface:

- `void Create(InstallEntry entry)` — best-effort; resolves the executable via the
  same logic the shims use (folder-name guess, then `GodotExecutableLocator.Find`),
  so extract that into a shared helper rather than duplicating it a third time.
- `void Delete(InstallEntry entry)` — best-effort; deletes launcher entry + desktop shortcut.
- `void DeleteDesktopShortcut(InstallEntry entry)` — used by `RemoveActiveAsync`.
- `void DeleteAll(InstallScope scope)` — used by `clean`.
- `bool Exists(InstallEntry entry)` — used by `doctor`.

Pure, unit-tested statics (no I/O):

- `GetEntryPath(InstallEntry, AppPaths)` — scope → location mapping.
- `BuildDesktopFile(InstallEntry, string exePath, string iconPath)` — `.desktop` text.

All failures follow the existing convention: swallowed, `warn:` via `DiagnosticContext`
only under `--verbose`. Never `WarnAlways` — the TUI calls these under Terminal.Gui.

### Linux

- File name: `godman-godot-<version>-<edition>-<scope>.desktop`, lowercase, with any
  character outside `[a-z0-9.-]` replaced by `-`. Scope is in the name so a user and a
  global install of the same version never collide.
- Contents:
  ```ini
  [Desktop Entry]
  Type=Application
  Name=Godot 4.7.2 (Standard)
  Comment=Godot Engine editor, managed by godman
  Exec="/abs/path/to/Godot_v4.7.2-stable_linux.x86_64" %f
  Icon=/abs/path/to/icons/hicolor/scalable/apps/godman-godot.svg
  Terminal=false
  Categories=Development;IDE;
  StartupWMClass=Godot
  ```
  `Exec` quoting follows the Desktop Entry spec: the path is double-quoted and `"`, `` ` ``,
  `$` and `\` inside it are backslash-escaped. `Icon` is an absolute path so no icon-cache
  refresh is needed. `Name` carries no scope: a user and a global install of the same
  version show as two identically named entries, which is rare and acceptable — the
  filenames differ, so neither overwrites the other.
- Locations, both new on `AppPaths`:
  - User: `$XDG_DATA_HOME/applications`, default `~/.local/share/applications`.
  - Global: `<GODMAN_GLOBAL_ROOT prefix>/share/applications`, default
    `/usr/local/share/applications` (on the default `XDG_DATA_DIRS`).
  - Icon: `<same data dir>/icons/hicolor/scalable/apps/godman-godot.svg`, written on
    every `Create` (cheap, self-healing), deleted by `DeleteAll`.
- Test isolation: `GodmanTestFixture` must set `XDG_DATA_HOME` into `TempRoot` (and
  restore it). The global location already follows `GODMAN_GLOBAL_ROOT`. Add a fixture
  test that asserts neither resolved directory lies outside `TempRoot`.
- No `update-desktop-database` call: GNOME watches `applications/` directories, and the
  cache only matters for MIME associations, which we do not declare.

### Windows

- Start Menu: unchanged location and name —
  `<StartMenu|CommonStartMenu>\Programs\godman\Godot <ver> (<edition>).lnk`.
- Desktop: unchanged — `<Desktop>\Godot <ver> (<edition>).lnk`, activate-only.
- `DeleteAll(scope)` removes every `*.lnk` in that scope's `Programs\godman` folder (the
  folder is godman-owned) and the folder itself; for the desktop, only exact names derived
  from registered installs are deleted — the desktop is not ours to glob.

### Icon asset

Godot's official SVG logo, embedded as an `EmbeddedResource` in `GodotManager.csproj`.
Licensed CC BY 4.0 (Andrea Calabró); README gets an attribution line. Linux only —
Windows `.lnk` files take the icon from the executable.

### Opt-out persistence

`InstallEntry` gains `public bool? LauncherEntry { get; set; }`:

- `null` — recorded by godman < 1.4.0; treated as "wanted", so `activate` backfills it.
- `true` — created at install.
- `false` — `install --no-shortcut`; `activate` does not create one, `doctor` does not
  report it missing.

`activate` does not write the field back for a `null` entry; `null` and `true` behave the
same, so there is nothing to persist.

### Front-ends and plumbing

- `InstallCommand`: new `--no-shortcut` option → `InstallRequest.CreateLauncherEntry`.
  `ElevatedInstallPayload` and `ElevatedInstallCommand.BuildRequest` carry it, so global
  installs on Windows honor it.
- `InstallerService`: after registering the entry, calls `LauncherService.Create` unless
  opted out. Dry run lists it as an action.
- **Bug fix, same place:** `install --activate` currently calls `ApplyActiveAsync`
  without first calling `RemoveActiveAsync` on the previously active install, leaking
  its shim, PATH entry and desktop shortcut. Route it through the same cleanup `activate`
  does.
- `activate` / `activate-elevated` / TUI activate: `LauncherService.Create` if the entry
  is not opted out and `!Exists`.
- `remove` / `remove-elevated` / TUI remove: `LauncherService.Delete(install)`
  unconditionally, before optional file deletion. Dry run lists it.
- `clean` / `clean-elevated`: `LauncherService.DeleteAll(scope)`; dry run lists it.
- TUI `InstallDialog`: "Add to application launcher" checkbox, default checked.
- `doctor`: a check listing registered installs where `LauncherEntry != false` and
  `!Exists` — "run `godman activate <id>` to add it".

### Elevation

No new predicate. Every global-scope launcher write happens inside an operation that is
already elevated before its first machine-wide write (`install-elevated`,
`activate-elevated`, `remove-elevated`, `clean-elevated`). Because the global
`share/applications` write is new machine-wide state, the `elevation-parity-reviewer`
agent reviews the final diff specifically for any path that reaches it unelevated.

## Part 2 — 1.3.0 follow-ups

### 2.1 Global registry write guard compares content, not Id sets

`RegistryService.SaveAsync` (around line 141) writes the global file only when the Id set
of global entries changed. Part 1 makes this load-bearing for real: any in-place field
change to a global entry is silently dropped.

A naive content comparison against disk would regress: on an un-migrated machine the
in-memory `Path` has been rebased (`RebaseRelocatedInstallPaths`) while the file still
holds the old path, so every unprivileged user-scope save would see a difference and try
— and fail — to write the machine-wide file.

**Design:** `LoadAsync` records a snapshot of the global entries **after** rebasing
(serialized per Id). `SaveAsync` writes the global file iff the desired global entries
differ from that snapshot by Id set **or** by serialized content. A rebase alone never
triggers a write; a genuine field change does. When no snapshot exists (registry built
without `LoadAsync`), fall back to comparing against the file as read and rebased.

Tests: (a) rebase-only load + user-scope save → global file untouched; (b) mutate a
global entry's field + save → global file written; (c) existing Id-set cases still pass.

### 2.2 Timeout on the post-kill wait

`InstallerService.TryKillProcessTreeAsync` awaits `WaitForExitAsync()` unbounded.
Bound it (10 s, via a linked `CancellationTokenSource`) and return `bool` — whether the
process is confirmed gone. The caller keeps the cache archive when it is `false` (the
elevated child may still hold it) and emits a verbose `warn:`.

Test: a child that ignores termination is not practical cross-platform; test the timeout
branch by injecting the wait (internal overload taking a `Func<Task>` or `TimeSpan`) with a
never-exiting long-lived child that is killed in test cleanup. Record the observed
mutation result (remove the timeout → test hangs/fails) rather than predicting it.

### 2.3 Late-cancel cache leak

Cancellation landing between a successful elevated install and the `finally` that
decides the cache entry's fate leaks the archive. Base that decision on an
`installCompleted` flag set immediately after success, not on the token. Self-correcting
today (the next download clears a stale `.archive`), so if the change turns out to need
restructuring beyond that flag, drop it and document the decision in PLAN.md.

### 2.4 Correct the `Kill()` comment

The remark in `InstallerServiceInternalsTests.cs` says `Kill()` throws
`InvalidOperationException` for an exited process "on Windows". Per the docs that is
.NET Framework behavior; this project is net10.0-only. Rewrite the comment to state what
the catches actually cover (`InvalidOperationException` when no process is associated;
`Win32Exception` when the OS refuses). Keep the catches. Comment-only change.

### 2.5 `godman version`

New `VersionCommand`: prints godman version (same source as `SetApplicationVersion`),
.NET runtime (`RuntimeInformation.FrameworkDescription`) and OS
(`RuntimeInformation.OSDescription`), via Spectre.Console. `--version` unchanged. Note that
`install -v|--version <VERSION>` is an install option and does not conflict with a verb.

### Dropped

- "`remove` has no `remove-elevated` mirror on Windows" — stale; `remove-elevated` exists.
- "TUI coverage is manual-only" — not code-fixable; covered by the manual checklist below.

## Testing

**Automated**
- Pure: `GetEntryPath` for both scopes/platforms incl. overrides; `BuildDesktopFile`
  (quoting of spaces, `"`, `$`; filename sanitizing).
- E2E via `CliTestHarness` (Linux-effective paths under `TempRoot`): install → `.desktop`
  + icon exist; `--no-shortcut` → none and `LauncherEntry == false`; activate on a legacy
  (`null`) entry → created; activate on `false` → not created; remove active and
  non-active → gone; `install --activate` over an active install → previous shim cleaned;
  clean → all godman `.desktop` files and icon gone, foreign `.desktop` files untouched.
- Doctor reports a missing entry and does not report an opted-out one.
- Registry snapshot tests (2.1), kill-timeout test (2.2).
- Windows `.lnk` behavior is not exercisable on the Linux CI host beyond the pure path
  mapping; covered manually.

**Review:** `elevation-parity-reviewer` on the full diff, told which code is new and which
was moved from `EnvironmentService`.

**Manual (Jan, before tagging)**
- Fedora/GNOME, user scope: install → entry in overview with icon; launch; window groups
  under the entry; remove → gone.
- Fedora, global scope via `sudo`: entry under `/usr/local/share/applications`; remove.
- Existing 4.7.2: `godman activate` → entry appears; `doctor` clean afterwards.
- TUI: install with checkbox off/on; TUI remove.
- Windows: install → Start Menu entry; activate with desktop shortcut; switch active →
  desktop shortcut of the previous one gone, Start Menu entry kept; remove → both gone;
  clean → `Programs\godman` gone. Both scopes.

## Out of scope

- Per-install icons for .NET vs Standard editions.
- MIME association for `project.godot`.
- A `godman shortcuts` command.
- macOS.
