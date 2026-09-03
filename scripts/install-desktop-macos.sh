#!/usr/bin/env bash
set -euo pipefail

NO_START=""
AGENT_MODE=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-start) NO_START="--no-start"; shift ;;
    --agent-mode) AGENT_MODE="${2:-}"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

PACKAGE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
AGENT_PAYLOAD="$PACKAGE_DIR/agent-payload"
COMPANION_PAYLOAD="$PACKAGE_DIR/companion-payload"
AGENT_INSTALLER="$PACKAGE_DIR/install-macos.sh"
COMPANION_INSTALLER="$PACKAGE_DIR/install-companion-macos.sh"
CONFIG="$HOME/Library/Application Support/MateMCP"
MODE_FILE="$CONFIG/agent-run-mode.txt"
COMPANION_APP="$HOME/Applications/MateMCP Agent Companion.app"

[[ -x "$AGENT_INSTALLER" ]] || { echo "Agent installer not found: $AGENT_INSTALLER" >&2; exit 1; }
[[ -x "$COMPANION_INSTALLER" ]] || { echo "Companion installer not found: $COMPANION_INSTALLER" >&2; exit 1; }

if [[ -z "$AGENT_MODE" && -f "$MODE_FILE" ]]; then AGENT_MODE="$(tr -d '[:space:]' < "$MODE_FILE")"; fi
[[ "$AGENT_MODE" == "Normal" || "$AGENT_MODE" == "Elevated" ]] || AGENT_MODE="Normal"

# Install user-facing Companion/support files before the Agent. Elevated Agent
# configuration intentionally root-protects its executable and persistent state,
# so it must be the final installation step that touches the shared support dir.
"$COMPANION_INSTALLER" "$COMPANION_PAYLOAD" --no-start
mkdir -p "$CONFIG"
cp "$PACKAGE_DIR/uninstall-desktop-macos.sh" "$CONFIG/uninstall-desktop-macos.sh"
chmod +x "$CONFIG/uninstall-desktop-macos.sh"

# --agent-only prevents install-macos.sh from delegating back to this Desktop wrapper.
"$AGENT_INSTALLER" "$AGENT_PAYLOAD" --no-start --agent-only "$AGENT_MODE"

echo
echo "MateMCP Desktop installed/upgraded."
echo "Components: background Agent + on-demand Agent Companion"
echo "Agent execution mode: $AGENT_MODE"
echo "Uninstall: $CONFIG/uninstall-desktop-macos.sh"

if [[ "$NO_START" != "--no-start" ]]; then
  CONFIGURE_MODE="$HOME/.local/share/matemcp/configure-agent-mode-macos.sh"
  if [[ "$AGENT_MODE" == "Elevated" || -f /Library/LaunchDaemons/com.matemcp.agent.plist ]]; then
    sudo MATEMCP_TARGET_USER="$USER" MATEMCP_TARGET_UID="$(id -u)" MATEMCP_TARGET_HOME="$HOME" "$CONFIGURE_MODE" "$AGENT_MODE"
  else
    "$CONFIGURE_MODE" "$AGENT_MODE"
  fi
  open "$COMPANION_APP"
  echo "MateMCP Agent started in the configured background mode; Companion opened for this install session."
  echo "Companion will not open automatically on future sign-ins."
fi
