# GodotManager (godman)

.NET 10 console + TUI tool to install/manage Godot Engine builds. CLI is `godman`.
End-user docs live in README.md — this file is for navigating the codebase.

## Build & Test

Requires .NET 10 SDK (pre-release at time of writing).

```bash
dotnet build
dotnet test -v minimal
dotnet test --filter "FullyQualifiedName~InstallerServiceIntegrationTests"
dotnet test --filter "FullyQualifiedName~GodotManager.Tests.E2E"
```

CI runs `dotnet test -v minimal` on ubuntu-latest + windows-latest (.github/workflows/ci.yml).

## Layout

- `GodotManager/Program.cs` — DI wiring + Spectre.Console.Cli command registration (entry point).
- `GodotManager/Commands/` — one class per CLI verb (`install`, `activate`, `fetch`, …).
- `GodotManager/Services/` — `InstallerService`, `RegistryService`, `EnvironmentService`, `GodotVersionFetcher`, `GodotDownloadUrlBuilder`, `WindowsElevationHelper`, `DownloadService` (transport: managed cache under `<ConfigDirectory>/downloads`, HTTP Range resume, retry, SHA-512 verification against `godotengine/godot-builds`).
- `GodotManager/Domain/` — `InstallEntry`, `InstallRegistry` (persisted JSON model).
- `GodotManager/Config/AppPaths.cs` — resolves all on-disk paths; honors env-var overrides.
- `GodotManager/Infrastructure/` — DI glue (`TypeRegistrar`), `DiagnosticContext`, `VerboseInterceptor`, `GlobalSettings`, `ProcessHelpers`.
- `GodotManager/Tui/` — Terminal.Gui 2.x interactive mode; `TuiApp.cs` + `Views/`.
- `GodotManager.Tests/` — xUnit + NSubstitute + Spectre.Console.Testing. `E2E/` for end-to-end CLI tests, `Helpers/` for fixtures.

## Elevation / re-entry pattern

Global-scope `install`, `activate`, `clean`, `remove`, and `deactivate` need admin
rights. Instead of failing, the user-scope command re-launches itself elevated and
dispatches to a hidden mirror command: `install-elevated`, `activate-elevated`,
`clean-elevated`, `remove-elevated`, `deactivate-elevated` (registered with
`.IsHidden()` in Program.cs, implemented in `Commands/Elevated*Command.cs`).

On Windows this triggers UAC via `WindowsElevationHelper`; on Linux the user must
already be running under `sudo`. When adding a new command that touches global paths,
follow the same split.

Two rules learned the hard way, both from bugs that shipped past a green suite:

- **Decide elevation before the first machine-wide write, not after it fails.** The
  predicate lives in `Services/Elevated{Activator,Remover,Deactivator}.cs` as
  `TouchesMachineState` (pure, unit-tested) wrapped by `IsRequired` (adds the OS and
  elevation probe). Note that `activate` needs elevation when the install being
  *deactivated* is global, not just the one being activated.
- **Both front-ends must go through the same predicate.** Every TUI handler in
  `Tui/TuiApp.cs` has a CLI counterpart in `Commands/`; four separate bugs came from a
  TUI handler reimplementing one and dropping its elevation or error handling. The
  launchers return an `ElevatedOperationResult` rather than printing, because the TUI
  calls them while Terminal.Gui owns the screen.

## Path resolution & test isolation

`AppPaths` reads `GODMAN_HOME` and `GODMAN_GLOBAL_ROOT` (with legacy
`GODOT_MANAGER_HOME` / `GODOT_MANAGER_GLOBAL_ROOT` aliases) before falling back to
platform defaults. Tests rely on this — never hardcode paths.

Use `GodmanTestFixture` (saves/restores env vars, creates a temp `TempRoot`, builds
wired-up `AppPaths`/`RegistryService`/`EnvironmentService`) for any test that hits
the filesystem. For end-to-end CLI tests use `CliTestHarness.Create(fixture, httpClient)`
with a mocked `HttpClient` from `MockHttpHandlers` — it mirrors Program.cs's DI but with
the fixture's services. See `GodotManager.Tests/Helpers/`.

## Conventions

- `Nullable` and `ImplicitUsings` are enabled — don't fight the analyzer.
- Best-effort operations (shim cleanup, PATH writes, cache I/O) swallow failures by
  default and only emit `warn:` via `DiagnosticContext` when `--verbose`/`-V` is set
  (intercepted by `VerboseInterceptor`). Preserve that pattern; don't promote
  warnings to exceptions.
- All user-facing output goes through Spectre.Console (`AnsiConsole.*`) — not
  `Console.WriteLine`.

## Release & packaging

- Version is set in `GodotManager/GodotManager.csproj` (`<Version>`/`<AssemblyVersion>`).
- WinGet publishing: automated from `.github/workflows/release.yml` (`publish-winget` job); no manifests are checked into this repo.
- RPM spec: `packaging/rpm/godman.spec`.
- Linux one-liner installer: `install.sh`.
- Release pipeline: `.github/workflows/release.yml`.
