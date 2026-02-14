# godman

[![CI Status](https://github.com/jame581/GodotManager/actions/workflows/ci.yml/badge.svg)](https://github.com/jame581/GodotManager/actions/workflows/ci.yml)
[![Build Status](https://github.com/jame581/GodotManager/actions/workflows/release.yml/badge.svg)](https://github.com/jame581/GodotManager/actions/workflows/release.yml)
[![Latest Release](https://img.shields.io/badge/GitHub-Release-blue?logo=github)](https://github.com/jame581/GodotManager/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/jame581/GodotManager/blob/main/LICENSE)

godman (formerly Godot Manager) is a .NET 10 console/TUI tool to install, manage, and activate Godot Engine builds (Standard and .NET) on Linux and Windows.

## Features
- Install from official URLs (auto-built) or local archives; supports Linux and Windows, Standard or .NET editions.
- Scope-aware installs: user or global (requires administrator privileges for global scope).
- Each install is extracted into its own subfolder under the install root, based on downloaded archive name (with deterministic fallback when source URL has no archive filename).
- Registry of installs with activation; sets `GODOT_HOME` and writes shims (`godot` or `godot.cmd`).
- Interactive TUI (`tui`) and CLI commands (`list`, `fetch`, `install`, `activate`, `remove`, `doctor`, `clean`).
- Dry-run mode to preview install/activate operations without making changes.
- Cleanup command to remove installs, shims, and config.

### Prerequisites

Software what you need is:

* [.NET 10](https://dotnet.microsoft.com/en-us/download)

That's all :)

## Quickstart

### Install via WinGet (Windows)
```powershell
winget install --id JanMesarc.GodMan
```

### Usage example
```bash
# List installs
godman list

# Browse available versions from GitHub
godman fetch --stable --limit 10

# Preview installation (dry-run)
godman install --version 4.5.1 --edition Standard --platform windows --scope User --dry-run

# Install latest 4.5.1 Windows Standard (auto URL) to user scope and activate
godman install --version 4.5.1 --edition Standard --platform windows --scope User --activate

# Install .NET edition for Windows from auto URL, global scope (UAC prompt)
godman install --version 4.5.1 --edition DotNet --platform windows --scope Global --activate

# Install on Linux global scope (requires sudo)
sudo godman install --version 4.5.1 --edition Standard --platform linux --scope Global --activate

# Preview activation (dry-run)
godman activate <id> --dry-run

# Run TUI
godman tui

# Doctor and cleanup
godman doctor
godman clean --yes
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
- `version` — show the current godman version.

## Paths
### Linux
- **Config**: `~/.config/godman/`
- **User installs**: `~/.local/bin/godman/`
- **Global installs**: `/usr/local/bin/godman/`
- **User shim**: `~/.local/bin/godot`
- **Global shim**: `/usr/local/bin/godot`

### Windows
- **Config**: `%APPDATA%\godman\`
- **User installs**: `%APPDATA%\godman\installs\`
- **Global installs**: `C:\Program Files\godman\installs\`
- **User shim**: `%APPDATA%\godman\bin\godot.cmd`
- **Global shim**: `C:\Program Files\godman\bin\godot.cmd`

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
  - Windows: a UAC prompt will appear when global scope is selected
- **Global activation also requires elevated privileges** (because it updates system-wide environment variables/shims); on Windows, `activate` now shows a UAC prompt automatically.
- **Global cleanup requires elevated privileges**; on Windows, `clean` shows a UAC prompt automatically when global paths are being removed.
- Global scope sets system-wide environment variables and shims accessible to all users.
- The `fetch` command queries GitHub API to discover available Godot versions.
- Auto-URL construction for known Godot version patterns.
- Environment variable overrides available: `GODMAN_HOME`, `GODMAN_GLOBAL_ROOT`
- **Windows environment variables**: After activation, `GODOT_HOME` is set in the registry and current process. New terminal sessions will automatically load it; existing sessions can verify with `doctor` command.
- **Windows PATH**: The shim directory is automatically added to your PATH during activation. Restart your terminal after activation to use the `godot` command.

## Author

* **Jan Mesarč** - *Programmer* - [Portfolio](https://janmesarc.online/)
* **Do you want support my work, my dreams?** - [Buy me a coffee](https://www.buymeacoffee.com/jame581)
* **Take a look on my other games** - [itch.io](https://jame581.itch.io/)