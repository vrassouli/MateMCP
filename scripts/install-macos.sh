#!/usr/bin/env bash
set -euo pipefail
ARCH="$(uname -m)"
case "$ARCH" in
  arm64) RID=osx-arm64 ;;
  x86_64) RID=osx-x64 ;;
  *) echo "Unsupported architecture: $ARCH" >&2; exit 1 ;;
esac
SOURCE="${1:-./artifacts/$RID}"
TARGET="$HOME/.local/share/matemcp"
BIN="$HOME/.local/bin"
mkdir -p "$TARGET" "$BIN" "$HOME/Library/Application Support/MateMCP"
cp -R "$SOURCE"/* "$TARGET"/
ln -sf "$TARGET/MateMCP.Agent" "$BIN/matemcp"
chmod +x "$TARGET/MateMCP.Agent"
echo "Installed MateMCP to $TARGET"
echo "Ensure $BIN is in PATH, configure appsettings.json, then run: matemcp"
