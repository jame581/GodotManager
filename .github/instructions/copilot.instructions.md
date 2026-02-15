---
description: Project-wide coding and review guidance for GodotManager (godman)
applyTo: "**"
---

# GodotManager Copilot Instructions

These instructions apply to all files in this repository.

## Project Context
- `godman` is a `.NET 10` CLI/TUI tool for installing and managing Godot Engine builds.
- Platforms: Windows and Linux.
- Install scopes: `User` and `Global`.
- Primary commands: `list`, `fetch`, `install`, `activate`, `deactivate`, `remove`, `doctor`, `clean`, `tui`.
- CLI framework: `Spectre.Console.Cli`; DI via `Microsoft.Extensions.DependencyInjection`.

## Architecture and Boundaries
- Keep domain models in `GodotManager/Domain` (`InstallEntry`, `InstallRegistry`, enums).
- Keep path and environment logic centralized in services/config (`AppPaths`, `EnvironmentService`, `RegistryService`, `InstallerService`).
- Keep command classes in `GodotManager/Commands` focused on input validation, UX output, and service orchestration.
- Do not move business logic into commands when it belongs in services.
- Register new services/commands in `GodotManager/Program.cs` and keep command naming consistent with existing commands.

## Cross-Platform and Privilege Rules
- Preserve cross-platform behavior (`OperatingSystem.IsWindows()` checks where needed).
- Never break user-scope workflows while adding global-scope features.
- Global scope may require elevation:
	- Windows: maintain existing UAC/elevated-command pattern (`*-elevated` commands + payload flow).
	- Linux: keep compatibility with sudo-based usage.
- Keep `GODOT_HOME` handling and shim creation/removal consistent with `EnvironmentService` and `AppPaths`.
- Keep support for environment variable overrides: `GODMAN_HOME`, `GODMAN_GLOBAL_ROOT` (and current legacy compatibility behavior).

## Coding Style
- Follow existing C# style in repo:
	- file-scoped namespaces,
	- `internal sealed` where appropriate,
	- nullable-aware code,
	- async/await with cancellation tokens when relevant,
	- small, focused methods.
- Prefer explicit, actionable error messages shown to users.
- Use Spectre.Console output style consistent with existing commands (`MarkupLine`, tables, progress).
- Avoid broad refactors unless explicitly requested.

## Command and UX Expectations
- Preserve existing CLI option names/semantics unless the task explicitly requests changes.
- If adding options, include validation in `CommandSettings.Validate()`.
- Dry-run behavior must not mutate filesystem, registry JSON, env vars, or shims.
- Keep command results deterministic and script-friendly (success `0`, failure `-1`, consistent messaging).

## Data and Filesystem Safety
- Prefer root-cause fixes over patches.
- Do not introduce destructive behavior without explicit confirmation flags (e.g., keep `--yes`/`--delete` patterns).
- Maintain deterministic install directory behavior (archive-name based, with fallback naming).
- Keep best-effort cleanup/migration logic non-fatal where current code follows that pattern.

## Testing Requirements
- Add or update tests in `GodotManager.Tests` for behavior changes.
- Use xUnit patterns already used in the repo.
- For filesystem/environment tests, isolate with temp directories and restore environment variables in cleanup.
- Prefer focused test runs first (affected test file/class), then broader runs when needed.

## Dependencies and Tooling
- Reuse existing dependencies when possible.
- Avoid adding new packages unless clearly necessary.
- Keep build/test compatibility with:
	- `dotnet build`
	- `dotnet test -v minimal`

## Documentation
- Update `README.md` when changing commands, flags, paths, prerequisites, or user-visible behavior.
- Keep terminology consistent: project/tool name is `godman` (formerly Godot Manager).
