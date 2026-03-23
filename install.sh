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
