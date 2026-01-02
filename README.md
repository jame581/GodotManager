# Godot Manager

A .NET 9 console/TUI tool to install, manage, and activate Godot Engine builds (Standard and .NET) on Linux and Windows.

## Features
- Install from official URLs (auto-built) or local archives; supports Linux and Windows, Standard or .NET editions.
- Scope-aware installs: user or global (requires administrator privileges for global scope).
- Registry of installs with activation; sets `GODOT_HOME` and writes shims (`godot` or `godot.cmd`).
- Interactive TUI (`tui`) and CLI commands (`list`, `fetch`, `install`, `activate`, `remove`, `doctor`, `clean`).
- Dry-run mode to preview install/activate operations without making changes.
- Cleanup command to remove installs, shims, and config.

### Prerequisites

Software what you need is:

* [.NET 9](https://dotnet.microsoft.com/en-us/download)

That's all :)

## Quickstart
```bash
# List installs
dotnet run --list

# Browse available versions from GitHub
dotnet run --fetch --stable --limit 10

# Preview installation (dry-run)
dotnet run --install --version 4.5.1 --edition Standard --platform windows --scope User --dry-run

# Install latest 4.5.1 Windows Standard (auto URL) to user scope and activate
dotnet run --install --version 4.5.1 --edition Standard --platform windows --scope User --activate

# Install .NET edition for Windows from auto URL, global scope (requires admin)
dotnet run --install --version 4.5.1 --edition DotNet --platform windows --scope Global --activate

# Install on Linux global scope (requires sudo)
sudo dotnet run --install --version 4.5.1 --edition Standard --platform linux --scope Global --activate

# Preview activation (dry-run)
dotnet run --activate <id> --dry-run

# Run TUI
dotnet run --tui

# Doctor and cleanup
dotnet run --doctor
dotnet run --clean --yes
```

## Commands
- `list` — show registered installs, active marker.
- `fetch` — browse available Godot versions from GitHub; options: `--stable`, `--filter <VERSION>`, `--limit <COUNT>`.
- `install` — download (auto URL) or use `--archive`; options: `--version`, `--edition`, `--platform`, `--scope`, `--path`, `--activate`, `--force`, `--dry-run`.
- `activate <id>` — switch active install; options: `--dry-run`.
- `remove <id> [--delete]` — unregister (optionally delete files).
- `doctor` — check registry/env/shim.
- `tui` — interactive menu for the above.
- `clean [--yes]` — remove installs, shims, config.

## Paths
### Linux
- **Config**: `~/.config/godot-manager/`
- **User installs**: `~/.local/bin/godot-manager/`
- **Global installs**: `/usr/local/bin/godot-manager/`
- **User shim**: `~/.local/bin/godot`
- **Global shim**: `/usr/local/bin/godot`

### Windows
- **Config**: `%APPDATA%\GodotManager\`
- **User installs**: `%APPDATA%\GodotManager\installs\`
- **Global installs**: `C:\Program Files\GodotManager\installs\`
- **User shim**: `%APPDATA%\GodotManager\bin\godot.cmd`
- **Global shim**: `C:\Program Files\GodotManager\bin\godot.cmd`

## Building & Tests
```bash
# Build the project
dotnet build

# Run all tests
dotnet test -v minimal

# Run integration tests only
dotnet test --filter "FullyQualifiedName~InstallerServiceIntegrationTests"

# Run with detailed output
dotnet test -v detailed
```

**Test Coverage**:
- Unit tests for path resolution and configuration
- Integration tests for download/install flows with mocked HTTP
- Cross-platform validation (Windows/Linux)
- Isolated test environments with temporary directories

## Notes
- **Global installs require elevated privileges**:
  - Linux: run with `sudo`
  - Windows: run as Administrator
- Global scope sets system-wide environment variables and shims accessible to all users.
- The `fetch` command queries GitHub API to discover available Godot versions.
- Auto-URL construction for known Godot version patterns.
- Environment variable overrides available: `GODOT_MANAGER_HOME`, `GODOT_MANAGER_GLOBAL_ROOT`
- **Windows environment variables**: After activation, `GODOT_HOME` is set in the registry and current process. New terminal sessions will automatically load it; existing sessions can verify with `doctor` command.
- **Windows PATH**: The shim directory is automatically added to your PATH during activation. Restart your terminal after activation to use the `godot` command.

## Author

* **Jan Mesarč** - *Programmer* - [Portfolio](https://janmesarc.online/)
* **Do you want support my work, my dreams?** - [Buy me a coffee](https://www.buymeacoffee.com/jame581)
* **Take a look on my other games** - [itch.io](https://jame581.itch.io/)