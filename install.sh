#!/usr/bin/env bash
set -euo pipefail

# godman installer for Linux
# Usage: curl -fsSL https://raw.githubusercontent.com/jame581/GodotManager/main/install.sh | bash

REPO="jame581/GodotManager"
INSTALL_DIR="${GODMAN_INSTALL_DIR:-$HOME/.local/bin}"
BINARY_NAME="godman"

info()  { printf '\033[1;34m%s\033[0m\n' "$*"; }
error() { printf '\033[1;31mError: %s\033[0m\n' "$*" >&2; exit 1; }

# Detect architecture
ARCH=$(uname -m)
case "$ARCH" in
  x86_64) RID="linux-x64" ;;
  *)      error "Unsupported architecture: $ARCH. Only x86_64 is supported." ;;
esac

# Get latest release tag
info "Fetching latest release..."
LATEST=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | grep '"tag_name"' | head -1 | sed 's/.*"tag_name": *"\([^"]*\)".*/\1/')
[ -z "$LATEST" ] && error "Could not determine latest release."

VERSION="${LATEST#v}"
ASSET_NAME="${BINARY_NAME}-${RID}-${VERSION}.tar.gz"
DOWNLOAD_URL="https://github.com/$REPO/releases/download/$LATEST/$ASSET_NAME"

info "Installing godman $VERSION to $INSTALL_DIR..."

# Create install directory
mkdir -p "$INSTALL_DIR"

# Download and extract
TEMP_DIR=$(mktemp -d)
trap 'rm -rf "$TEMP_DIR"' EXIT

info "Downloading $DOWNLOAD_URL..."
curl -fsSL -o "$TEMP_DIR/$ASSET_NAME" "$DOWNLOAD_URL"

info "Extracting..."
mkdir -p "$TEMP_DIR/extract"
tar -xzf "$TEMP_DIR/$ASSET_NAME" -C "$TEMP_DIR/extract"

# Find and install the binary
BINARY=$(find "$TEMP_DIR/extract" -name "$BINARY_NAME" -type f | head -1)
[ -z "$BINARY" ] && error "Could not find $BINARY_NAME in archive."

# godman <= 1.3.0 kept global installs in a directory called <shim>/godman, so on a
# machine that has not migrated yet, "$INSTALL_DIR/$BINARY_NAME" can be a directory --
# and `cp` would quietly drop the binary *inside* it rather than failing.
#
# The remedy names godman by its full path: `sudo godman` cannot work here, because the
# directory is squatting on that very name and sudo's secure_path never includes
# ~/.local/bin, where a working godman usually lives.
#
# Under `curl ... | sudo GODMAN_INSTALL_DIR=/usr/local/bin bash`, HOME is root's and
# `command -v` searches sudo's secure_path, so neither finds the invoking user's own
# godman; SUDO_USER's home, from the passwd database, does. Re-running "without
# GODMAN_INSTALL_DIR" under that same sudo would install into root's home instead, so the
# advice then says to run it as that user.
if [ -d "$INSTALL_DIR/$BINARY_NAME" ]; then
  USER_HOME="$HOME"
  REINSTALL="re-run this installer without GODMAN_INSTALL_DIR"
  if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ]; then
    USER_HOME=$(getent passwd "$SUDO_USER" 2>/dev/null | cut -d: -f6 || true)
    # No getent (or no passwd entry): let the shell expand ~user. SUDO_USER goes through
    # eval, so only a plain POSIX user name is accepted.
    if [ -z "$USER_HOME" ] && printf '%s' "$SUDO_USER" | grep -Eq '^[a-z_][a-z0-9_-]*[$]?$'; then
      USER_HOME=$(eval echo "~$SUDO_USER" 2>/dev/null || true)
      case "$USER_HOME" in "~"*) USER_HOME="" ;; esac   # unknown user: left unexpanded
    fi
    REINSTALL="re-run this installer as $SUDO_USER, without sudo and without GODMAN_INSTALL_DIR"
  fi
  # Empty when SUDO_USER's home could not be resolved: then no concrete path is named,
  # rather than root's.
  USER_BINARY=""
  if [ -n "$USER_HOME" ]; then
    USER_BINARY="$USER_HOME/.local/bin/$BINARY_NAME"
  fi

  EXISTING=$(command -v "$BINARY_NAME" 2>/dev/null || true)
  if ! { [ -n "$EXISTING" ] && [ -f "$EXISTING" ] && [ -x "$EXISTING" ]; }; then
    EXISTING=""
    if [ -n "$USER_BINARY" ] && [ -f "$USER_BINARY" ] && [ -x "$USER_BINARY" ]; then
      EXISTING="$USER_BINARY"
    fi
  fi

  if [ -n "$EXISTING" ]; then
    error "$INSTALL_DIR/$BINARY_NAME is a directory, not a file. This is an old global install root; migrate it to <prefix>/lib/godman first by running: sudo \"$EXISTING\" list (that binary must be godman 1.4.0 or later; if it is older, $REINSTALL first) -- then re-run this installer, or pick another GODMAN_INSTALL_DIR."
  elif [ -n "$USER_BINARY" ]; then
    error "$INSTALL_DIR/$BINARY_NAME is a directory, not a file. This is an old global install root. Install godman to your user directory first ($REINSTALL), migrate by running: sudo \"$USER_BINARY\" list -- then re-run this installer."
  else
    error "$INSTALL_DIR/$BINARY_NAME is a directory, not a file. This is an old global install root. Install godman to your user directory first ($REINSTALL), migrate by running: sudo <full path to that godman> list -- then re-run this installer."
  fi
fi

cp "$BINARY" "$INSTALL_DIR/$BINARY_NAME"
chmod +x "$INSTALL_DIR/$BINARY_NAME"

info "Installed godman $VERSION to $INSTALL_DIR/$BINARY_NAME"

# Add install dir to PATH if not already present
if ! echo "$PATH" | tr ':' '\n' | grep -qx "$INSTALL_DIR"; then
  EXPORT_LINE="export PATH=\"$INSTALL_DIR:\$PATH\""

  # Determine which shell config file to update
  SHELL_RC=""
  if [ -n "${ZSH_VERSION:-}" ] || [ "$(basename "${SHELL:-}")" = "zsh" ]; then
    SHELL_RC="$HOME/.zshrc"
  elif [ -n "${BASH_VERSION:-}" ] || [ "$(basename "${SHELL:-}")" = "bash" ]; then
    SHELL_RC="$HOME/.bashrc"
  fi

  if [ -n "$SHELL_RC" ]; then
    # Only add if not already in the file
    if ! grep -qF "$INSTALL_DIR" "$SHELL_RC" 2>/dev/null; then
      echo "" >> "$SHELL_RC"
      echo "# Added by godman installer" >> "$SHELL_RC"
      echo "$EXPORT_LINE" >> "$SHELL_RC"
      info "Added $INSTALL_DIR to PATH in $SHELL_RC"
    fi
    echo ""
    info "Restart your shell or run: source $SHELL_RC"
    echo ""
  else
    echo ""
    info "Could not detect shell config. Add $INSTALL_DIR to your PATH manually:"
    echo ""
    echo "  $EXPORT_LINE"
    echo ""
  fi
fi

info "Done! Run 'godman --help' to get started."
