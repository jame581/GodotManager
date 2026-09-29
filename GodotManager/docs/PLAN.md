# Godot Installation Manager — Plan

## Scope
- .NET 10 console app to manage Godot installs (Standard and .NET) on Windows and Linux.
- Maintain installs registry, download/unpack builds, set active install via env var and shim.
- Supports both User and Global installation scopes on Windows and Linux.
- Phase 1: core CLI ✅; Phase 2: TUI built with Spectre.Console.CLI ✅; Phase 4: TUI Rework ✅; Phase 5: Linux packaging (partial) ✅; Phase 6: WinGet publishing ✅; Phase 7: install-flow robustness (1.3.0) ✅; Phase 7b: elevation parity ✅; Phase 8: global root out of the shim directory (1.4.0) ✅; 1.4.1: follow-ups from the 1.4.0 review ✅.

## Phase 1 — Core CLI ✅ COMPLETE
- Commands:
  - **list** ✅: show registered installs, mark active in table format.
  - **fetch** ✅: display available remote versions from GitHub releases with filtering options.
    - Options: `--stable` (show only stable releases), `--filter <VERSION>` (filter by version text), `--limit <COUNT>` (max results, default 20), `--no-cache` (skip cache, fetch fresh from GitHub).
    - Caches fetched releases to `releases-cache.json` with 24h TTL. Falls back to stale cache on network failure.
  - **install** ✅: download with progress or import local archive, verify (checksum field available), unpack zip/tar.xz, register entry.
    - Options: `--version` (required), `--edition` (Standard|DotNet), `--platform` (Windows|Linux), `--scope` (User|Global, requires admin on Windows), `--url` or `--archive`, `--path`, `--force`, `--activate`, `--dry-run` (preview without changes).
  - **activate** ✅: update env var and shim/symlink to chosen install by id.
    - Options: `--dry-run` (preview without changes).
  - **remove** ✅: unregister and optionally delete files with `--delete` flag. Supports `--dry-run` preview.
  - **doctor** ✅: validate registry, paths, env var, shim status.
  - **clean** ✅: remove all godman installs, shims, and config with `--yes` flag.
- **Persistence** ✅: `installs.json` under Linux `~/.config/godman/`, Windows `%APPDATA%\godman\`.
- **Env var/shim** ✅:
  - Set `GODOT_HOME` env var pointing to active install path.
  - User scope: per-user environment variable.
  - Global scope: system-wide environment variable (requires admin/sudo).
  - Linux: symlink/script at `~/.local/bin/godot` (user) or `/usr/local/bin/godot` (global).
  - Windows: `godot.cmd` batch script in `%APPDATA%\godman\bin\` (user) or `C:\Program Files\godman\bin\` (global).
  - Auto-detect Godot binary names on Linux (`godot`, `Godot`, `Godot_v4`, `Godot_v3`).
- **Resilience** ✅: download with progress reporting, clear error messages, force overwrite support.

### Architecture
- **Domain**: `InstallEntry`, `InstallRegistry`, enums (`InstallEdition`, `InstallPlatform`, `InstallScope`).
- **Config**: `AppPaths` — cross-platform path resolution with env var overrides (`GODMAN_HOME`, `GODMAN_GLOBAL_ROOT`).
- **Services**:
  - `RegistryService` — JSON serialization/deserialization of install registry.
  - `EnvironmentService` — write shims and env vars, platform-specific logic.
  - `InstallerService` — download, extract archives (SharpCompress), register installs.
  - `GodotDownloadUrlBuilder` ✅ — auto-construct download URLs for known Godot versions/editions/platforms.
  - `GodotVersionFetcher` ✅ — fetch available Godot releases from GitHub API with filtering.
- **Commands**: CLI commands using Spectre.Console.Cli with typed settings classes.
- **Infrastructure**: `TypeRegistrar` for DI integration with Spectre.Console.Cli; `DiagnosticContext` for verbose warning system; `GlobalSettings` base class for shared CLI options; `VerboseInterceptor` for propagating global flags.

## Phase 2 — TUI (Spectre.Console.CLI) ✅ COMPLETE
- **tui** command launches interactive menu powered by Spectre.Console:
  - List installs: table view with active marker, version, edition, platform, path, timestamp, id.
  - Browse versions: fetch and display available Godot versions from GitHub with filtering.
  - Install: prompts for version, edition, platform, scope, source (download auto-URL or local archive), install directory, force, activate, dry-run preview.
  - Activate: selection prompt to switch active install with dry-run preview option.
  - Remove: selection prompt with option to delete files on disk.
  - Doctor: summary of registry, environment, and shim status.
  - Quit: exit TUI.
- Progress bars for install operations.
- Dry-run preview for install and activate operations.
- `TuiRunner` reuses Phase 1 services (registry, installer, environment, url builder, version fetcher).

## Data Model ✅
- **InstallEntry**: `{ Id (Guid), Version (string), Edition (Standard|DotNet), Platform (Windows|Linux), Scope (User|Global), Path (string), Checksum? (string), AddedAt (DateTimeOffset), IsActive (bool, transient) }`.
- **InstallRegistry**: `{ Installs (List<InstallEntry>), ActiveId? (Guid) }`.
- **InstallScope** ✅: User or Global (both platforms; requires administrator privileges for Global).
- **Config**: AppPaths handles platform-specific defaults, supports env var overrides for testing and custom deployments.

## Testing ✅
- **Unit tests**: `AppPathsTests`, `CleanCommandTests` with temp directory and env var mocking.
- **Integration tests** ✅: `InstallerServiceIntegrationTests` with mocked HTTP downloads and fixture-based archives.
  - Tests download flow, local archive installation, activation, force overwrite, dry-run, progress reporting, and multi-install scenarios.
  - Uses temporary directories and mock archives for complete isolation.
- **Cross-platform validation**: tests guarded by `OperatingSystem.IsWindows()` checks.
- **Integration**: archive extraction via SharpCompress; download logic with HttpClient.
- Test project: `GodotManager.Tests` using xUnit and custom HTTP mocking.

## Dependencies
- **Spectre.Console** & **Spectre.Console.Cli**: CLI parsing and TUI rendering.
- **SharpCompress**: archive extraction (zip, tar.xz).
- **Microsoft.Extensions.DependencyInjection**: DI container for services.
- **System.Text.Json**: JSON persistence for registry.
- **NSubstitute** (test only): Mocking framework for HTTP clients (note: tests use custom MockHttpMessageHandler).

## Paths & Environment ✅
- **Config directory**:
  - Linux: `~/.config/godman/` (override: `GODMAN_HOME`).
  - Windows: `%APPDATA%\godman\`.
- **Install roots**:
  - User (Linux): `~/.local/share/godman/installs/`.
  - Global (Linux): `/usr/local/lib/godman/` (override: `GODMAN_GLOBAL_ROOT`, a prefix).
  - User (Windows): `%APPDATA%\godman\installs\`.
  - Global (Windows): `C:\Program Files\godman\installs\` (override: `GODMAN_GLOBAL_ROOT`, a prefix).
- **Shim directories**:
  - User (Linux): `~/.local/bin/`.
  - Global (Linux): `/usr/local/bin/`.
  - User (Windows): `%APPDATA%\godman\bin\`.
  - Global (Windows): `C:\Program Files\godman\bin\`.
- **Env script** (Linux only): `~/.config/godman/env.sh` sourced by shim.
- **Environment variables**:
  - User scope: `EnvironmentVariableTarget.User`.
  - Global scope: `EnvironmentVariableTarget.Machine` (Windows) or system-wide (Linux).

## Completed Features ✅
- Full CLI command suite (list, fetch, install, activate, remove, doctor, clean).
- Interactive TUI with all major workflows including version browsing.
- Cross-platform support (Windows, Linux).
- Scope-aware installs (User/Global on Windows and Linux, requires admin for Global).
- Auto-URL construction for Godot downloads via `GodotDownloadUrlBuilder`.
- Remote version discovery via GitHub API with `GodotVersionFetcher`.
- Registry persistence with JSON.
- Environment variable and shim management.
- Archive extraction with progress reporting.
- Force overwrite and activation hooks.
- Cleanup command for full uninstall.
- Dry-run mode for install and activate commands (preview without changes).
- Integration tests for download/install flows with mocked HTTP and fixture-based archives.
- Verbose diagnostic warnings (`--verbose` / `-V`) for all best-effort operations; moderate-risk operations (previous activation cleanup) always warn.

## Pending Features
- ~~**Checksum validation**: populate and verify `Checksum` field during downloads.~~ ✅ Done (SHA-512 computed during download and local archive import, verified against upstream `SHA512-SUMS.txt` when available, stored in registry, shown in `list`).
- ~~**Resume support**: partially downloaded files resume capability.~~ ✅ Done (HTTP Range resume across invocations via DownloadService).
- ~~**Verbosity levels**: configurable logging/output detail.~~ ✅ Done (global `--verbose` / `-V` flag via `DiagnosticContext` + `VerboseInterceptor`).

## Next Steps
- ~~Add checksum verification for downloads.~~ ✅ Done.
- ~~Explore resume support for interrupted downloads.~~ ✅ Done.
- ~~Consider caching fetched version data to reduce GitHub API calls.~~ ✅ Done (24h TTL, `--no-cache` flag, offline fallback).
- ~~Extend dry-run to remove command.~~ ✅ Done.
- ~~Add end-to-end CLI command tests.~~ ✅ Done (23 E2E tests via Spectre.Console.Testing CommandAppTester).

## Phase 3 — Steam Detection

Detect Godot Engine installations managed by Steam and integrate them into godman.

### Background
- **Steam App ID**: 404790 (Standard edition only — Mono/.NET is NOT on Steam)
- Steam installs in `steamapps/common/Godot Engine/` under library folders
- Detect via `libraryfolders.vdf` (Valve Data Format) + `appmanifest_404790.acf`

### Detection Algorithm
1. Locate Steam: registry `HKLM\SOFTWARE\Wow6432Node\Valve\Steam\InstallPath` (Windows) or `~/.steam/steam` (Linux)
2. Parse `config/libraryfolders.vdf` to find all library folders
3. Check each library for `steamapps/appmanifest_404790.acf`
4. If found, locate Godot executable in `steamapps/common/Godot Engine/`

### Implementation
- **New service**: `SteamDetectorService` — discover Steam Godot installs
- **New enum value**: `InstallSource` (Manual, Steam) on `InstallEntry`
- **Modified commands**: `list` and `doctor` show Steam installs; `activate` can activate them
- **VDF parser**: minimal parser for `libraryfolders.vdf` key-value format

### Paths
| Platform | Steam Root | Library Config |
|----------|-----------|----------------|
| Windows | `C:\Program Files (x86)\Steam\` (from registry) | `config\libraryfolders.vdf` |
| Linux | `~/.steam/steam/` or `~/.local/share/Steam/` | `config/libraryfolders.vdf` |

## Phase 4 — TUI Rework (Lazygit-Style) ✅ COMPLETE

Replaced the Spectre.Console menu-driven TUI with a persistent two-panel Terminal.Gui v2 interface.

### Implementation
- **Terminal.Gui v2** (`2.0.0-develop.5213`) with sub-namespaces (`Terminal.Gui.Views`, `Terminal.Gui.ViewBase`, `Terminal.Gui.App`, `Terminal.Gui.Input`)
- **TuiApp**: Orchestrator — creates Terminal.Gui application, two-panel layout (30/70 split), status bar with F-key shortcuts, global keyboard handlers
- **InstallsListView**: Left panel — scrollable list with active marker (▸), selection events
- **DetailsView**: Right panel (default) — shows version, edition, platform, scope, path, added date, active status, action key hints
- **BrowseView**: Right panel (alternate, toggled via F1) — remote version browser with text filter, stable-only toggle, async fetch
- **InstallDialog**: Modal — version input, edition/scope selectors (OptionSelector), progress bar, async install via InstallerService
- **DoctorDialog**: Modal — runs registry, path, and shim checks with pass/fail report
- **HelpOverlay**: Modal — keyboard shortcut reference

### Keyboard Navigation
- **Tab / Shift+Tab**: Switch panels
- **↑ / ↓**: Navigate install list
- **a**: Activate selected | **d**: Deactivate | **r**: Remove
- **F1**: Toggle Browse panel | **F2**: Install dialog | **F3**: Doctor dialog
- **?**: Help overlay | **q / Ctrl+Q**: Quit

## Phase 5 — Linux Distribution Packaging ✅ PARTIAL

### Fedora (COPR) ✅
1. ✅ RPM spec file created (`packaging/rpm/godman.spec`) — packages self-contained linux-x64 binary
2. Publish to COPR repository (`dnf copr enable jame581/godman && dnf install godman`) — pending COPR account setup
3. Runtime deps (already on Fedora): glibc, libgcc, openssl-libs, libstdc++, libicu

### Arch Linux (AUR)
- Create PKGBUILD that downloads GitHub release binary
- Minimal effort, community-maintained after submission

### Installation Script ✅
- ✅ Shell installer created (`install.sh`): `curl -fsSL https://...install.sh | bash`
- Downloads latest release, extracts to `~/.local/bin/`, adds to PATH
- README updated with install instructions

### Priority
1. **GitHub Releases** ✅ — works now, README updated with install docs
2. **COPR RPM** ✅ spec ready — needs COPR account setup and publishing
3. **AUR** — Arch Linux community
4. **Homebrew tap** — macOS/WSL users

## Phase 6 — Automated WinGet Publishing ✅ COMPLETE

### Implementation
- ✅ `publish-winget` job added to `.github/workflows/release.yml` (integrated, not a separate workflow)
- ✅ Uses `vedantmgoyal9/winget-releaser@v2` action with `installers-regex: \.zip$` (matches zip assets, not exe/msi)
- ✅ Package ID: `JanMesarc.GodMan` (via `vars.WINGET_ID`)
- ✅ Only publishes stable releases (skips pre-release tags like `v1.0.0-beta1`)
- ✅ Token configured via `secrets.WINGET_TOKEN`

### Setup Steps (completed)
1. ✅ GitHub PAT with `public_repo` scope stored as `WINGET_TOKEN` secret
2. ✅ Fork `microsoft/winget-pkgs` to account
3. ✅ Initial version exists in winget-pkgs
4. ✅ Workflow integrated into release pipeline — subsequent releases auto-submit PRs

## Phase 7 — Install-Flow Robustness (1.3.0) ✅ COMPLETE

- **DownloadService**: managed download cache at `<config>/downloads/`, HTTP Range
  resume across invocations guarded by a `Content-Range` offset check on the
  response (an `If-Range` ETag is layered on top when a prior ETag is known, but
  the offset check is what makes resume safe even without one), retry with
  backoff (three attempts total). Not a content cache — a completed download is
  never reused to skip a fetch; it only makes an interrupted transfer resumable.
- **Checksum verification**: SHA-512 against `godotengine/godot-builds`'
  `SHA512-SUMS.txt`, keyed on the post-redirect filename. Mismatch aborts, deletes
  the archive, and clears the cache entry so the next run can't resume the bad
  bytes; unobtainable sums (the normal case for some versions) fall back to
  unverified and continue. Registry records `ChecksumAlgorithm` and
  `ChecksumVerified`; pre-1.3.0 entries fail closed and read back as unverified.
- **Staging extraction**: fresh installs extract to a sibling of the target and
  swap atomically; `--force` merges instead, so unrelated files already in a
  `--path` directory survive.
- **Elevated installs**: the parent passes its verification result to the child,
  which re-hashes the archive before honouring that claim rather than trusting the
  payload — closes a user-to-admin file-swap window in the cache directory. The
  parent also owns cache cleanup, since it is the one that downloaded.
- **GodmanException**: expected failures render a message plus an actionable hint,
  rendered in the command catch blocks (Spectre does not propagate to Program.cs).
- **doctor**: reports download cache size and how many incomplete downloads are
  genuinely resumable.
- **Windows verification: done.** The elevated install path (UAC, the
  `install-elevated` re-entry, parent/child checksum handoff) was exercised by hand
  on Windows 11, along with cross-process Range resume, corrupt-resume rejection,
  cache cleanup, the `%ProgramFiles%\godman\installs.json` registry location, and
  the 1.2.0 → 1.3.0 upgrade path. Note the elevated paths **cannot** be tested with
  the `GODMAN_*` overrides the rest of the suite isolates with: UAC children are
  created with a fresh environment block, so an isolated run would install to the
  temp target while registering in the real `%ProgramFiles%`.
- **Still not covered**: the TUI's unverified-checksum banner (`InstallDialog.cs`).
  Terminal.Gui's module initializer throws under the xunit test host, so only the
  pure formatting helpers are unit tested; rendering the banner needs an induced
  sums-fetch failure. Shipping as a known gap.

## Phase 7b — Elevation parity, found by Windows verification ✅ COMPLETE

Manual Windows testing found five bugs a green 257-test suite had missed. Four were
one pattern: **TUI handlers reimplementing their CLI counterpart while dropping its
elevation and error handling.** `install` was unaffected because it delegates to
`InstallerService`; `remove`, `activate` and `deactivate` were not.

- **All five machine-state verbs now elevate identically from either front-end**, via
  one predicate per verb — `TouchesMachineState` (pure, unit-tested) wrapped by
  `IsRequired` (adds the OS and elevation probe) in
  `Services/Elevated{Activator,Remover,Deactivator}.cs`. `remove-elevated` and
  `deactivate-elevated` join the existing three mirrors.
- The launchers **return an `ElevatedOperationResult` instead of printing**, because
  the TUI calls them while Terminal.Gui owns the screen. The child's console closes
  with it, so an outcome the parent must report has to cross back through the exit
  code — as "unregistered, but the files survived" now does for `remove-elevated`.
- **`.NET`/mono installs were unusable after activation on both platforms** (a
  pre-1.2.0-era bug): mono archives extract into a nested
  `Godot_vX-stable_mono_<platform>/` directory, so a folder-name guess with a
  top-level-only fallback wrote a shim pointing at a path that never existed.
  `GodotExecutableLocator` searches root then immediate children, prefers the GUI
  binary over `_console`, and forces case-insensitive matching (the platform default
  is case-sensitive on Linux).
- **A leftover global shim silently outranks a user-scope activation**, since Windows
  searches the machine `PATH` first. Reported by `ShimShadowing`, not auto-corrected —
  removing it needs rights the activating user may not have.
- **The test suite was writing to the real Windows registry.** `AppPaths` redirects
  files, but `EnvironmentService` appended its shim directory to the persisted User
  `PATH` regardless; `GodmanTestFixture` now snapshots and restores it.

Spec: `GodotManager/docs/superpowers/specs/2026-07-28-v1.3.0-install-robustness-design.md`

## Phase 8 — Global install root out of the shim directory (1.4.0) ✅ COMPLETE

Global installs lived at `/usr/local/bin/godman`, which claimed the exact filename
the godman binary needs in `/usr/local/bin` for `sudo godman` to resolve — `sudo`
replaces PATH with `secure_path`, which never contains the `~/.local/bin` that
`install.sh` writes to. The documented `sudo godman ...` workflow could not work on a
default sudo configuration, and the obvious fix was blocked by godman's own directory.

- **The root moved to `/usr/local/lib/godman`**, and `GODMAN_GLOBAL_ROOT` became a
  *prefix* (shim `<prefix>/bin`, installs `<prefix>/lib/godman`) instead of naming the
  shim directory. That matches what it already meant on Windows, and keeps one
  variable redirecting both directories — the property `GodmanTestFixture` relies on.
  **Breaking**: a value that used to mean `/usr/local/bin` is now `/usr/local`.
- **A root move orphans absolute registry paths.** `InstallEntry.Path` is absolute, so
  the move invalidates every entry pointing into the old root.
  `AppPaths.GetInstallRootRelocations` publishes the old→new map and
  `RegistryService.RebaseRelocatedInstallPaths` applies it on load — in memory only,
  since a read command must never write, and for a global entry that would mean
  writing a file an unprivileged caller cannot touch. Derived and idempotent, so it is
  recomputed each load rather than persisted.
- **The machine-wide registry moved with the root**, and moving it needs privileges.
  Without a read-side fallback every unprivileged `list`, `doctor` and TUI session on a
  not-yet-migrated machine shows zero global installs — indistinguishable from data
  loss. Reads fall back to the pre-migration path; writes always target the current
  one, so the first elevated operation settles the machine on the new layout.
- **The migration ordering is the whole decision.** `TryMigrateDirectory` no-ops once
  the destination exists, so on a machine carrying both old roots whichever runs first
  wins. That lives in the pure, unit-tested `AppPaths.PlanLinuxMigrations` rather than
  inline in the constructor, for the same reason `TouchesMachineState` does.
- **A completed move repairs the files that name the old root.** The shim hard-codes
  `exec "<old root>/<version>/…"` and `env.sh` exports the old root as `GODOT_HOME`.
  After a *successful* directory move, `AppPaths.MigrateAndRepair` rewrites those
  (user and global shim, `env.sh`), matching quoted paths with the same segment-aware
  rule the registry rebase uses (`PathRebase`). A blocked move rewrites nothing: the
  files are still where the shim points.
- **`doctor` gained three checks**: the shim's own target (parsed out of `godot` /
  `godot.cmd`) is missing — the only check that sees a completed migration whose shim
  was not repaired, since the registry rebases the active entry onto the new root,
  which exists; an active install whose directory is missing (a vanished install — it
  catches neither a blocked migration, which leaves the shim working, nor a completed
  one); and a legacy root that is still in use — the stock "this can be removed" advice
  would have destroyed installs that had not moved yet. Existence cannot answer "did
  the migration run", since `AppPaths` best-effort creates both roots on startup; the
  signal is whether anything landed in the destination and whether any registry entry
  still points into the old root.
- **`install.sh` refuses to install over a directory**, which `cp` would otherwise
  nest the binary inside — reachable now that the docs point at
  `GODMAN_INSTALL_DIR=/usr/local/bin`.

Windows paths are unchanged; it picks up the entry rebasing and registry fallback for
its own older `GodotManager` → `godman` migration for free.

User-facing release notes: `GodotManager/docs/release-notes/1.4.0.md` — paste into the
GitHub release after `release.yml` creates it.

### Launcher entries per install + 1.3.0 follow-ups ✅ COMPLETE

Every install now gets its own application-launcher entry, and the open 1.3.0 review
follow-ups were closed in the same release.

- **`AppPaths`** resolves per-scope launcher directories and, on Linux, an icon path
  under `icons/hicolor/scalable/apps/godman-godot.svg`; both ignore
  `GODMAN_HOME`/`GODMAN_GLOBAL_ROOT` and are redirected only by the internal
  `GODMAN_LAUNCHER_ROOT`, which `GodmanTestFixture` now also saves/restores.
- **`LauncherService`** owns the `.desktop` file (Linux) and Start Menu `.lnk`
  (Windows) for an install, plus the optional Windows desktop shortcut; every write
  is best-effort like shim cleanup, never an exception.
- **Global registry write guard now compares content, not just the Id set** (1.3.0
  follow-up 2.1): `SaveAsync` re-reads the global file itself, rebases that copy the
  same way `LoadAsync` rebases the caller's, and writes the global file only when the
  desired global entries differ from the rebased on-disk copy by Id set or by
  serialized content — a rebase alone never triggers a write, but a genuine in-place
  field change does.
- **`install` / `activate` / `remove` / `clean` wired to `LauncherService`**: install
  creates an entry (`--no-shortcut` opts out, recorded on `InstallEntry.LauncherEntry`
  and honoured by `activate` and `doctor`); activate backfills the entry for
  pre-1.4.0 installs; remove and clean delete it, remove only after the registry save
  succeeds so a failed global write never orphans the entry; deactivate leaves it in
  place. `install --activate` also cleans up the previously active install, and on
  Windows a user-scope install over an active global one activates through the
  elevated path (the same predicate `activate` already used).
- **Elevated-install cancellation follow-ups** (1.3.0 follow-ups 2.2–2.4): the
  post-kill wait is now bounded (10 s) and returns whether the process is confirmed
  gone, so the caller only keeps the cache archive when it might not be; a cancel
  landing after a success is reported as success instead of leaking the cache; the
  `Kill()` comment now states what the catches actually cover instead of citing
  .NET Framework behavior this net10.0-only project doesn't have.
- **`doctor`** reports installs missing their launcher entry (not ones that opted
  out via `--no-shortcut`); the TUI install dialog gained an "Add to application
  launcher" checkbox, default on.
- **`godman version`** (1.3.0 follow-up 2.5): prints godman, .NET runtime, and OS
  versions; `--version` is unchanged.
- **Dropped follow-ups**, both stale on inspection: "`remove` has no
  `remove-elevated` mirror on Windows" — `remove-elevated` already existed; "TUI
  coverage is manual-only" — not code-fixable, stays covered by the manual
  verification checklist.
- **Known limitation (Windows)**: two installs of the same version and edition in one
  scope at different `--path`s share one Start Menu name; the second overwrites the
  first's shortcut, since the `.lnk` filename carries no per-install id the way the
  Linux `.desktop` filename does. *(Fixed in 1.4.1 for new installs; see below.)*

Spec: `GodotManager/docs/superpowers/specs/2026-09-28-launcher-entries-and-1.3-followups-design.md`

### Pre-release review fixes (PR #2) ✅ COMPLETE

The first Windows run of the suite found three host-dependent test failures (a
`Path.GetDirectoryName` split of Unix paths, two unguarded Linux-literal tests, a `clean`
test that UAC-relaunched the test host). Copilot's review was cut off by its per-review
cost cap on this diff, so a local `/code-review high` ran instead. Each of its 10
findings was verified against the code; six were fixed with regression tests, and a
parity review of those fixes found one regression and three smaller gaps, also fixed:

- `LinuxElevation.Check` refuses global targets under a pre-1.4.0 `GODMAN_GLOBAL_ROOT`
  (one write used to end the legacy-layout detection for good).
- `clean` removes an unmigrated legacy global root (`AppPaths.GetLegacyGlobalInstallRoots`,
  directories only so the godman binary never matches) and stops when it cannot.
- Linux `_migrationMoves` equals what the constructor runs, so doctor no longer offers
  user-root moves under `GODMAN_HOME` (`DoctorCommand.NoPlannedMoveAdvice`).
- The Windows desktop shortcut belongs to the activation: `LauncherService.Delete` no
  longer removes it; `--force` over the active install removes the replaced one's.
- Printed `rmdir` / `sudo rm` remedies are shell-quoted (`ElevatedCommandLine.Quote`).
- The TUI install dialog shows one "Installed, not activated" box
  (`InstallProgressPresentation.BuildCompletionDialog`).

## 1.4.1 — Follow-ups from the 1.4.0 review ✅ COMPLETE

All five issues deferred from 1.4.0, plus the Windows Start Menu name collision. None is a
regression from 1.3.0. User-facing notes: `GodotManager/docs/release-notes/1.4.1.md`.

- [#3](https://github.com/jame581/GodotManager/issues/3) — `LinuxElevation.CheckRemove`
  extends a refused global remove's hint with "run `godman deactivate` first" when the
  entry is the active one (sudo resets HOME, so the elevated remove would not deactivate
  it). CLI and TUI call the same method; the hint-less pre-1.4.0-root refusal is left alone.
- [#4](https://github.com/jame581/GodotManager/issues/4) — `InstallerService.PlanActivationAsync`
  / `CompleteActivationAsync` back both `InstallCommand` and `InstallDialog` (predicate,
  request split, elevated launch). Two calls rather than one `InstallAndActivate`, because
  the CLI announces the UAC prompt between them after its live progress display has
  cleared. `CompleteActivationAsync` takes no cancellation token on purpose.
- [#5](https://github.com/jame581/GodotManager/issues/5) — the shadow-probe failure warns
  only under `-V`; `ActivateCommand` and `InstallCommand` take the `DiagnosticContext`.
- [#6](https://github.com/jame581/GodotManager/issues/6) — `CleanCommand.LoadInstallsBestEffort`;
  clean and doctor use `EnvironmentService.Launcher`; doctor's `HasContent` folded into
  `ProbeDestination` with an `Unreadable` state (pinned by a test).
- [#7](https://github.com/jame581/GodotManager/issues/7) —
  1. `clean` on Windows removes an unmigrated legacy global root
     (`GetLegacyGlobalInstallRoots` no longer returns `[]` there, skips a root holding the
     running executable or the app's base directory) and `HasGlobalCleanupTargets` counts it,
     so UAC is decided first. `CleanupLegacyRoot` treats the Windows root like the current
     layout (`installs\\`, `bin\\`, registry; the folder only if empty) because
     `<prefix>\\GodotManager` is a product-name folder, not a godman-only one; Linux's
     `<prefix>/bin/godman` still goes whole.
  2. `AppPaths.PlanWindowsMigrations` drives both the Windows constructor and
     `_migrationMoves`. The global move now runs under any absolute `GODMAN_GLOBAL_ROOT`
     (as on Linux) instead of only at the default `%ProgramFiles%`, so doctor's advice
     matches what runs.
  3. **Not fixed, documented:** doctor may call the only copy of pre-1.4.0 installs
     "removable" under a *relative* `GODMAN_GLOBAL_ROOT`, once the current registry exists
     (doctor cannot see entries only the legacy registry lists). Contrived; the relative
     value is already unsupported for migration.
  4. `install --force` keeps the replaced entry's **Id**. The first attempt carried
     `ActiveId` over, and a review showed it did nothing on the main Linux global flow:
     sudo loads root's registry, which never held the user's `ActiveId`. Reusing the Id
     keeps the user's pointer valid whoever runs the install. When the process's own
     registry says the entry was active, the activation is re-applied (a different version
     merged over the old one otherwise leaves the shim on the old binary) and the Windows
     desktop shortcut is kept. The re-apply runs after the registry save and only when the
     process's own registry marks the entry active, so a *different version* merged over the
     active global install's directory under sudo leaves the shim on the old binary
     (documented limitation; same-version reinstalls are unaffected).
- **Launcher deletes:** `LauncherService.Delete`/`Exists` ignore an entry with
  `LauncherEntry == false`. On Windows such an entry resolves to the plain shortcut name,
  which a sibling may hold, so removing the opted-out one used to delete the sibling's.
- **Windows Start Menu collision** — the shortcut name is chosen once at install time and
  recorded on `InstallEntry.LauncherFileName` (null = plain name, so existing entries and
  older registries are unchanged). Recorded, not recomputed from siblings: removing the
  first install would otherwise rename the second's. Two pre-1.4.1 installs that already
  share the plain name keep sharing it.

**Verification.** Every elevation E2E test returns early under root
(`Environment.IsPrivilegedProcess`), so the suite has to be run as a non-root user to
execute them. Regression tests were mutation-checked (fix removed, test fails) where
possible. The Windows-only paths (constructor plan, desktop-shortcut handling, `.lnk`
creation) are covered by pure-function tests on Linux and otherwise only by the
`windows-latest` CI leg. A parity review of the branch found the `ActiveId` flaw above,
a vacuous Linux test (the legacy-root clause is only separable on Windows), and untested
guards; all were fixed.

**Released** as v1.4.1 on 2026-09-29: the tag sits on `e79862a` (the merge of PR #9). The
release run passed on its first attempt, WinGet included (1.4.0's needed five, all of them
`publish-winget` failing while komac created a branch in the `winget-pkgs` fork). The
`windows-latest` CI leg passed on the merged head, which covers the Windows-only paths
listed above. A cloud session cannot push the tag (the push is refused with a 403), so it
was pushed from a local checkout, and the release body was then updated by hand from
`release-notes/1.4.1.md`, since `release.yml` only auto-generates one.
