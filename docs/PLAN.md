# Godot Installation Manager — Plan

## Scope
- .NET 9 console app to manage Godot installs (Standard and .NET) on Windows and Linux.
- Maintain installs registry, download/unpack builds, set active install via env var and shim.
- Phase 1: core CLI; Phase 2: TUI built with Spectre.Console.CLI.

## Phase 1 — Core CLI
- Fetch available versions/editions/platforms from official Godot sources.
- Commands:
  - list: show registered installs, mark active.
  - fetch: display available remote versions for selection.
  - install <version> [--dotnet|--standard] [--platform auto|win|linux] [--path <dir>] [--force]: download with progress, verify (checksum if available), unpack zip/tar.xz, register entry.
  - activate <id>: update env var and shim/symlink to chosen install.
  - remove <id> [--delete]: unregister and optionally delete files.
  - doctor: validate registry, paths, env var, shim.
- Persistence: installs.json under Linux `~/.config/godot-manager/`, Windows `%APPDATA%\GodotManager\`.
- Env var/shim: set GODOT_HOME; Linux symlink `~/.local/bin/godot`; Windows `godot.cmd` in a user bin dir on PATH.
- Resilience: resume-friendly downloads, clear error messages, dry-run for install/activate.

### Current CLI (baseline)
- Commands wired: list, fetch (stub), install, activate, remove, doctor (env/registry checks).
- Install supports `--url` or `--archive` plus `--edition`, `--platform`, `--path`, `--force`, `--activate`.
- Registry stored at `~/.config/godot-manager/installs.json` (Linux) or `%APPDATA%\GodotManager\installs.json` (Windows).
- Shim written to `~/.local/bin/godot` or `%APPDATA%\GodotManager\bin\godot.cmd`; env script exported as `GODOT_HOME`.

## Phase 2 — TUI (Spectre.Console.CLI)
- Use Spectre.Console.CLI for command parsing plus Spectre.Console for TUI rendering.
- Views/screens:
  - Installs: list, show active, actions (activate/remove).
  - Activate: picker to switch active install.
  - Fetch/Install: browse remote versions/editions, start download with progress bars; confirm force overwrite.
- Keep TUI as a layer over Phase 1 services to reuse logic.

## Data Model
- Install entry: { id, version, edition (Standard|DotNet), platform, path, checksum?, addedAt, isActive }.
- Config: default installs root, shim target, verbosity.

## Testing
- Unit: registry read/write, selection logic, path resolution.
- Integration (guarded/mocked for CI): download/unpack fixture; env var/shim update dry-runs.
- Cross-platform validation for archive handling and paths.

## Next Steps
- Scaffold solution structure (CLI services + domain + persistence).
- Add version source client and registry store. ✅ registry in place; source client still pending.
- Implement install flow end-to-end; then activation. ✅ initial flow with URL/archive + activation hook.
- Build TUI surfaces on top of commands once Phase 1 is stable.
- Wire real remote version fetch and checksum validation; add resume support.
