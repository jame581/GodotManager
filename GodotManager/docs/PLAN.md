# Godot Installation Manager — Plan

## Scope
- .NET 10 console app to manage Godot installs (Standard and .NET) on Windows and Linux.
- Maintain installs registry, download/unpack builds, set active install via env var and shim.
- Supports both User and Global installation scopes on Windows and Linux.
- Phase 1: core CLI ✅; Phase 2: TUI built with Spectre.Console.CLI ✅; Phase 4: TUI Rework ✅; Phase 5: Linux packaging (partial) ✅; Phase 6: WinGet publishing ✅.

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
- **`doctor` gained two checks**: an active install whose directory is missing (a shim
  hard-codes an absolute path, so a blocked migration leaves `godot` resolving to
  nothing, and nothing but `activate` can repair a shim), and a legacy root that is
  still in use — the stock "this can be removed" advice would have destroyed installs
  that had not moved yet. Existence cannot answer "did the migration run", since
  `AppPaths` best-effort creates both roots on startup; the signal is whether anything
  landed in the destination.
- **`install.sh` refuses to install over a directory**, which `cp` would otherwise
  nest the binary inside — reachable now that the docs point at
  `GODMAN_INSTALL_DIR=/usr/local/bin`.

Windows paths are unchanged; it picks up the entry rebasing and registry fallback for
its own older `GodotManager` → `godman` migration for free.
