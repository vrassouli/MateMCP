#!/usr/bin/env bash
set -euo pipefail

MODE="${1:-}"
TARGET="$HOME/.local/share/matemcp"
BIN="$HOME/.local/bin/matemcp"
CONFIG="$HOME/Library/Application Support/MateMCP"
LAUNCH_LABEL="com.matemcp.agent"
LAUNCH_PLIST="$HOME/Library/LaunchAgents/$LAUNCH_LABEL.plist"
DESKTOP_UNINSTALL="$CONFIG/uninstall-desktop-macos.sh"
COMPANION_APP="$HOME/Applications/MateMCP Agent Companion.app"
COMPANION_PLIST="$HOME/Library/LaunchAgents/com.matemcp.agent.companion.plist"

# If this is a Desktop installation, the obvious uninstall-macos.sh entry point
# removes both components. The Desktop wrapper passes --agent-only when it
# intentionally reaches the Agent component cleanup.
if [[ "$MODE" != "--agent-only" && -x "$DESKTOP_UNINSTALL" && ( -d "$COMPANION_APP" || -f "$COMPANION_PLIST" ) ]]; then
  "$DESKTOP_UNINSTALL"
  exit 0
fi

launchctl bootout "gui/$(id -u)/$LAUNCH_LABEL" >/dev/null 2>&1 || true
rm -f "$LAUNCH_PLIST"

rm -f "$BIN"
rm -rf "$TARGET"

echo "MateMCP binaries removed."
echo "Configuration and audit data were kept at: $CONFIG"
echo "Delete that directory manually if you also want to remove local MateMCP data."
