#!/usr/bin/env bash
set -euo pipefail

SOURCE="${1:-./payload}"
PACKAGE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET="$HOME/.local/share/matemcp"
BIN="$HOME/.local/bin"
CONFIG="$HOME/Library/Application Support/MateMCP"

if [[ ! -x "$SOURCE/MateMCP.Agent" ]]; then
  echo "MateMCP payload not found at: $SOURCE" >&2
  exit 1
fi

mkdir -p "$TARGET" "$BIN" "$CONFIG"
rm -rf "$TARGET"/*
cp -R "$SOURCE"/* "$TARGET"/
chmod +x "$TARGET/MateMCP.Agent"

if [[ -f "$PACKAGE_DIR/uninstall-macos.sh" ]]; then
  cp "$PACKAGE_DIR/uninstall-macos.sh" "$TARGET/uninstall-macos.sh"
  chmod +x "$TARGET/uninstall-macos.sh"
fi

if [[ -f "$PACKAGE_DIR/prepare-external-test-macos.sh" ]]; then
  cp "$PACKAGE_DIR/prepare-external-test-macos.sh" "$TARGET/prepare-external-test-macos.sh"
  chmod +x "$TARGET/prepare-external-test-macos.sh"
fi

ln -sfn "$TARGET/MateMCP.Agent" "$BIN/matemcp"

echo "MateMCP installed/upgraded."
echo "Binary: $BIN/matemcp"
echo "Config: $CONFIG/appsettings.json"
if [[ -x "$TARGET/uninstall-macos.sh" ]]; then
  echo "Uninstall: $TARGET/uninstall-macos.sh"
fi
if [[ -x "$TARGET/prepare-external-test-macos.sh" ]]; then
  echo "External test setup: $TARGET/prepare-external-test-macos.sh <public-ipv4> [port]"
fi
echo
if [[ ":$PATH:" != *":$BIN:"* ]]; then
  echo "Note: add $BIN to PATH, or run $BIN/matemcp directly."
fi
echo "Start MateMCP:"
echo "  $BIN/matemcp"
