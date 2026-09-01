#!/usr/bin/env bash
set -euo pipefail

TARGET="$HOME/.local/share/matemcp"
BIN="$HOME/.local/bin/matemcp"
CONFIG="$HOME/Library/Application Support/MateMCP"
LAUNCH_LABEL="com.matemcp.agent"
LAUNCH_PLIST="$HOME/Library/LaunchAgents/$LAUNCH_LABEL.plist"

launchctl bootout "gui/$(id -u)/$LAUNCH_LABEL" >/dev/null 2>&1 || true
rm -f "$LAUNCH_PLIST"

rm -f "$BIN"
rm -rf "$TARGET"

echo "MateMCP binaries removed."
echo "Configuration and audit data were kept at: $CONFIG"
echo "Delete that directory manually if you also want to remove local MateMCP data."
