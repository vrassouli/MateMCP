#!/usr/bin/env bash
set -euo pipefail

SOURCE="${1:-./payload}"
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
ln -sfn "$TARGET/MateMCP.Agent" "$BIN/matemcp"

echo "MateMCP installed."
echo "Binary: $BIN/matemcp"
echo "Config: $CONFIG/appsettings.json (created securely on first run)"
echo
if [[ ":$PATH:" != *":$BIN:"* ]]; then
  echo "Note: add $BIN to PATH, or run $BIN/matemcp directly."
fi
echo "Start MateMCP once to generate configuration:"
echo "  $BIN/matemcp"
