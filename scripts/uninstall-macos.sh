#!/usr/bin/env bash
set -euo pipefail

TARGET="$HOME/.local/share/matemcp"
BIN="$HOME/.local/bin/matemcp"
CONFIG="$HOME/Library/Application Support/MateMCP"

rm -f "$BIN"
rm -rf "$TARGET"

echo "MateMCP binaries removed."
echo "Configuration and audit data were kept at: $CONFIG"
echo "Delete that directory manually if you also want to remove local MateMCP data."
