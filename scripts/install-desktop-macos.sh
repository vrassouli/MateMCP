#!/usr/bin/env bash
set -euo pipefail

NO_START="${1:-}"
PACKAGE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
AGENT_PAYLOAD="$PACKAGE_DIR/agent-payload"
COMPANION_PAYLOAD="$PACKAGE_DIR/companion-payload"
AGENT_INSTALLER="$PACKAGE_DIR/install-macos.sh"
COMPANION_INSTALLER="$PACKAGE_DIR/install-companion-macos.sh"
CONFIG="$HOME/Library/Application Support/MateMCP"
LAUNCH_DOMAIN="gui/$(id -u)"
AGENT_PLIST="$HOME/Library/LaunchAgents/com.matemcp.agent.plist"
COMPANION_PLIST="$HOME/Library/LaunchAgents/com.matemcp.agent.companion.plist"

[[ -x "$AGENT_INSTALLER" ]] || { echo "Agent installer not found: $AGENT_INSTALLER" >&2; exit 1; }
[[ -x "$COMPANION_INSTALLER" ]] || { echo "Companion installer not found: $COMPANION_INSTALLER" >&2; exit 1; }

# --agent-only prevents install-macos.sh from delegating back to this Desktop wrapper.
"$AGENT_INSTALLER" "$AGENT_PAYLOAD" --no-start --agent-only
"$COMPANION_INSTALLER" "$COMPANION_PAYLOAD" --no-start

mkdir -p "$CONFIG"
cp "$PACKAGE_DIR/uninstall-desktop-macos.sh" "$CONFIG/uninstall-desktop-macos.sh"
chmod +x "$CONFIG/uninstall-desktop-macos.sh"

echo
echo "MateMCP Desktop installed/upgraded."
echo "Components: Agent + Agent Companion"
echo "Uninstall: $CONFIG/uninstall-desktop-macos.sh"

if [[ "$NO_START" != "--no-start" ]]; then
  launchctl bootstrap "$LAUNCH_DOMAIN" "$AGENT_PLIST"
  launchctl kickstart -k "$LAUNCH_DOMAIN/com.matemcp.agent"
  launchctl bootstrap "$LAUNCH_DOMAIN" "$COMPANION_PLIST"
  launchctl kickstart -k "$LAUNCH_DOMAIN/com.matemcp.agent.companion"
  echo "MateMCP Agent and Companion started."
fi
