# Godot Installation Manager — Plan

## Scope
- .NET 9 console app to manage Godot installs (Standard and .NET) on Windows and Linux.
- Maintain installs registry, download/unpack builds, set active install via env var and shim.
- Supports both User and Global installation scopes on Windows and Linux.
- Phase 1: core CLI ✅; Phase 2: TUI built with Spectre.Console.CLI ✅.

## Phase 1 — Core CLI ✅ COMPLETE
- Commands:
  - **list** ✅: show registered installs, mark active in table format.
  - **fetch** ⚠️: display available remote versions for selection (currently stub).
  - **install** ✅: download with progress or import local archive, verify (checksum field available), unpack zip/tar.xz, register entry.
    - Options: `--version` (required), `--edition` (Standard|DotNet), `--platform` (Windows|Linux), `--scope` (User|Global, requires admin on Windows), `--url` or `--archive`, `--path`, `--force`, `--activate`.
  - **activate** ✅: update env var and shim/symlink to chosen install by id.
  - **remove** ✅: unregister and optionally delete files with `--delete` flag.
  - **doctor** ✅: validate registry, paths, env var, shim status.
  - **clean** ✅: remove all godot-manager installs, shims, and config with `--yes` flag.
- **Persistence** ✅: `installs.json` under Linux `~/.config/godot-manager/`, Windows `%APPDATA%\GodotManager\`.
- **Env var/shim** ✅:
  - Set `GODOT_HOME` env var pointing to active install path.
  - User scope: per-user environment variable.
  - Global scope: system-wide environment variable (requires admin/sudo).
  - Linux: symlink/script at `~/.local/bin/godot` (user) or `/usr/local/bin/godot` (global).
  - Windows: `godot.cmd` batch script in `%APPDATA%\GodotManager\bin\` (user) or `C:\Program Files\GodotManager\bin\` (global).
  - Auto-detect Godot binary names on Linux (`godot`, `Godot`, `Godot_v4`, `Godot_v3`).
- **Resilience** ✅: download with progress reporting, clear error messages, force overwrite support.

### Architecture
- **Domain**: `InstallEntry`, `InstallRegistry`, enums (`InstallEdition`, `InstallPlatform`, `InstallScope`).
- **Config**: `AppPaths` — cross-platform path resolution with env var overrides (`GODOT_MANAGER_HOME`, `GODOT_MANAGER_GLOBAL_ROOT`).
- **Services**:
  - `RegistryService` — JSON serialization/deserialization of install registry.
  - `EnvironmentService` — write shims and env vars, platform-specific logic.
  - `InstallerService` — download, extract archives (SharpCompress), register installs.
  - `GodotDownloadUrlBuilder` ✅ — auto-construct download URLs for known Godot versions/editions/platforms.
- **Commands**: CLI commands using Spectre.Console.Cli with typed settings classes.
- **Infrastructure**: `TypeRegistrar` for DI integration with Spectre.Console.Cli.

## Phase 2 — TUI (Spectre.Console.CLI) ✅ COMPLETE
- **tui** command launches interactive menu powered by Spectre.Console:
  - List installs: table view with active marker, version, edition, platform, path, timestamp, id.
  - Install: prompts for version, edition, platform, scope (Linux only), source (download auto-URL or local archive), install directory, force, activate.
  - Activate: selection prompt to switch active install.
  - Remove: selection prompt with option to delete files on disk.
  - Doctor: summary of registry, environment, and shim status.
  - Quit: exit TUI.
- Progress bars for install operations.
- `TuiRunner` reuses Phase 1 services (registry, installer, environment, url builder).

## Data Model ✅
- **InstallEntry**: `{ Id (Guid), Version (string), Edition (Standard|DotNet), Platform (Windows|Linux), Scope (User|Global), Path (string), Checksum? (string), AddedAt (DateTimeOffset), IsActive (bool, transient) }`.
- **InstallRegistry**: `{ Installs (List<InstallEntry>), ActiveId? (Guid) }`.
- **InstallScope** ✅: User or Global (both platforms; requires administrator privileges for Global).
- **Config**: AppPaths handles platform-specific defaults, supports env var overrides for testing and custom deployments.

## Testing ✅
- **Unit tests**: `AppPathsTests`, `CleanCommandTests` with temp directory and env var mocking.
- **Cross-platform validation**: tests guarded by `OperatingSystem.IsWindows()` checks.
- **Integration**: archive extraction via SharpCompress; download logic with HttpClient.
- Test project: `GodotManager.Tests` using xUnit.

## Dependencies
- **Spectre.Console** & **Spectre.Console.Cli**: CLI parsing and TUI rendering.
- **SharpCompress**: archive extraction (zip, tar.xz).
- **Microsoft.Extensions.DependencyInjection**: DI container for services.
- **System.Text.Json**: JSON persistence for registry.

## Paths & Environment ✅
- **Config directory**:
  - Linux: `~/.config/godot-manager/` (override: `GODOT_MANAGER_HOME`).
  - Windows: `%APPDATA%\GodotManager\`.
- **Install roots**:
  - User (Linux): `~/.local/bin/godot-manager/`.
  - Global (Linux): `/usr/local/bin/godot-manager/` (override: `GODOT_MANAGER_GLOBAL_ROOT`).
  - User (Windows): `%APPDATA%\GodotManager\installs\`.
  - Global (Windows): `C:\Program Files\GodotManager\installs\` (override: `GODOT_MANAGER_GLOBAL_ROOT`).
- **Shim directories**:
  - User (Linux): `~/.local/bin/`.
  - Global (Linux): `/usr/local/bin/`.
  - User (Windows): `%APPDATA%\GodotManager\bin\`.
  - Global (Windows): `C:\Program Files\GodotManager\bin\`.
- **Env script** (Linux only): `~/.config/godot-manager/env.sh` sourced by shim.
- **Environment variables**:
  - User scope: `EnvironmentVariableTarget.User`.
  - Global scope: `EnvironmentVariableTarget.Machine` (Windows) or system-wide (Linux).

## Completed Features ✅
- Full CLI command suite (list, install, activate, remove, doctor, clean).
- Interactive TUI with all major workflows.
- Cross-platform support (Windows, Linux).
- Scope-aware installs (User/Global on Windows and Linux, requires admin for Global).
- Auto-URL construction for Godot downloads via `GodotDownloadUrlBuilder`.
- Registry persistence with JSON.
- Environment variable and shim management.
- Archive extraction with progress reporting.
- Force overwrite and activation hooks.
- Cleanup command for full uninstall.

## Pending Features
- **fetch** command: wire real remote version discovery from Godot's official sources (GitHub releases API or downloads page scraping).
- **Checksum validation**: populate and verify `Checksum` field during downloads.
- **Resume support**: partially downloaded files resume capability.
- **Dry-run mode**: preview install/activate without making changes.
- **Verbosity levels**: configurable logging/output detail.

## Next Steps
- Implement real version fetching in `fetch` command (GitHub API or scraping).
- Add checksum verification for downloads.
- Explore resume support for interrupted downloads.
- Add integration tests for download/install flows (mocked/fixture-based).
- Document installation and usage in README (already present, verify alignment).
