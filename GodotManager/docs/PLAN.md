# Godot Installation Manager — Plan

## Scope
- .NET 10 console app to manage Godot installs (Standard and .NET) on Windows and Linux.
- Maintain installs registry, download/unpack builds, set active install via env var and shim.
- Supports both User and Global installation scopes on Windows and Linux.
- Phase 1: core CLI ✅; Phase 2: TUI built with Spectre.Console.CLI ✅.

## Phase 1 — Core CLI ✅ COMPLETE
- Commands:
  - **list** ✅: show registered installs, mark active in table format.
  - **fetch** ✅: display available remote versions from GitHub releases with filtering options.
    - Options: `--stable` (show only stable releases), `--filter <VERSION>` (filter by version text), `--limit <COUNT>` (max results, default 20).
  - **install** ✅: download with progress or import local archive, verify (checksum field available), unpack zip/tar.xz, register entry.
    - Options: `--version` (required), `--edition` (Standard|DotNet), `--platform` (Windows|Linux), `--scope` (User|Global, requires admin on Windows), `--url` or `--archive`, `--path`, `--force`, `--activate`, `--dry-run` (preview without changes).
  - **activate** ✅: update env var and shim/symlink to chosen install by id.
    - Options: `--dry-run` (preview without changes).
  - **remove** ✅: unregister and optionally delete files with `--delete` flag.
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
- **Infrastructure**: `TypeRegistrar` for DI integration with Spectre.Console.Cli.

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
  - User (Linux): `~/.local/bin/godman/`.
  - Global (Linux): `/usr/local/bin/godman/` (override: `GODMAN_GLOBAL_ROOT`).
  - User (Windows): `%APPDATA%\godman\installs\`.
  - Global (Windows): `C:\Program Files\godman\installs\` (override: `GODMAN_GLOBAL_ROOT`).
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

## Pending Features
- **Checksum validation**: populate and verify `Checksum` field during downloads.
- **Resume support**: partially downloaded files resume capability.
- **Verbosity levels**: configurable logging/output detail.

## Next Steps
- Add checksum verification for downloads.
- Explore resume support for interrupted downloads.
- Consider caching fetched version data to reduce GitHub API calls.
- Extend dry-run to remove command.
- Add end-to-end CLI command tests.
