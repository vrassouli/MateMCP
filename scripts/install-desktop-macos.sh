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
COMPANION_APP="$HOME/Applications/MateMCP Agent Companion.app"

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
echo "Components: background Agent + on-demand Agent Companion"
echo "Uninstall: $CONFIG/uninstall-desktop-macos.sh"

if [[ "$NO_START" != "--no-start" ]]; then
  launchctl bootstrap "$LAUNCH_DOMAIN" "$AGENT_PLIST" >/dev/null 2>&1 || true
  launchctl kickstart -k "$LAUNCH_DOMAIN/com.matemcp.agent"
  open "$COMPANION_APP"
  echo "MateMCP Agent started in the background; Companion opened for this install session."
  echo "Companion will not open automatically on future sign-ins."
fi
