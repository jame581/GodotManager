# Godot Manager

A .NET 9 console/TUI tool to install, manage, and activate Godot Engine builds (Standard and .NET) on Linux and Windows.

## Features
- Install from official URLs (auto-built) or local archives; supports Linux and Windows, Standard or .NET editions.
- Scope-aware installs on Linux: user (`~/.local/bin/godot-manager`) or global (`/usr/local/bin/godot-manager`).
- Registry of installs with activation; sets `GODOT_HOME` and writes shims (`godot` or `godot.cmd`).
- Interactive TUI (`tui`) and CLI commands (`list`, `install`, `activate`, `remove`, `doctor`, `clean`).
- Cleanup command to remove installs, shims, and config.

### Prerequisites

Software what you need is:

* [.NET 9](https://dotnet.microsoft.com/en-us/download)

That's all :)

## Quickstart
```bash
# List installs
 dotnet run --list

# Install latest 4.5.1 Linux Standard (auto URL) to user scope and activate
 dotnet run --install --version 4.5.1 --edition Standard --platform linux --scope User --activate

# Install .NET edition for Linux from auto URL, global scope (Linux only)
 sudo dotnet run --install --version 4.5.1 --edition DotNet --platform linux --scope Global --activate

# Run TUI
 dotnet run --tui

# Doctor and cleanup
 dotnet run --doctor
 dotnet run --clean --yes
```

## Commands
- `list` — show registered installs, active marker.
- `install` — download (auto URL) or use `--archive`; options: `--version`, `--edition`, `--platform`, `--scope`, `--path`, `--activate`, `--force`.
- `activate <id>` — switch active install.
- `remove <id> [--delete]` — unregister (optionally delete files).
- `fetch` — placeholder for remote discovery.
- `doctor` — check registry/env/shim.
- `tui` — interactive menu for the above.
- `clean [--yes]` — remove installs, shims, config.

## Paths
- Config: `~/.config/godot-manager/` (Linux) or `%APPDATA%\GodotManager\` (Windows).
- Installs (Linux): user `~/.local/bin/godot-manager/`, global `/usr/local/bin/godot-manager/`.
- Shim: user `~/.local/bin/godot`; global `/usr/local/bin/godot` (Linux); Windows `%APPDATA%\GodotManager\bin\godot.cmd`.

## Building & Tests
```bash
dotnet build
dotnet test -v minimal
```

## Notes
- Global installs on Linux may require sudo to create directories under `/usr/local/bin`.
- Fetch command is currently a stub; URLs are auto-built for known patterns.

## Author

* **Jan Mesarč** - *Programmer* - [Portfolio](https://janmesarc.online/)
* **Do you want support my work, my dreams?** - [Buy me a coffee](https://www.buymeacoffee.com/jame581)
* **Take a look on my other games** - [itch.io](https://jame581.itch.io/)