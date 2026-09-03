#!/usr/bin/env bash
set -euo pipefail

PACKAGE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE="${1:-./payload}"
NO_START="${2:-}"
MODE="${3:-}"
AGENT_MODE="${4:-}"
DESKTOP_INSTALLER="$PACKAGE_DIR/install-desktop-macos.sh"
CONFIGURE_MODE_SOURCE="$PACKAGE_DIR/configure-agent-mode-macos.sh"

# In a unified Desktop package, install-macos.sh is the natural entry point.
# Delegate to the Desktop installer unless this script is being invoked
# internally for the Agent component only.
if [[ "$MODE" != "--agent-only" \
   && -x "$DESKTOP_INSTALLER" \
   && -x "$PACKAGE_DIR/agent-payload/MateMCP.Agent" \
   && -d "$PACKAGE_DIR/companion-payload" ]]; then
  args=()
  if [[ "$SOURCE" == "--no-start" || "$NO_START" == "--no-start" ]]; then args+=(--no-start); fi
  if [[ -n "$AGENT_MODE" ]]; then args+=(--agent-mode "$AGENT_MODE"); fi
  "$DESKTOP_INSTALLER" "${args[@]}"
  exit 0
fi

TARGET="$HOME/.local/share/matemcp"
BIN="$HOME/.local/bin"
CONFIG="$HOME/Library/Application Support/MateMCP"
MODE_FILE="$CONFIG/agent-run-mode.txt"
CONFIGURE_MODE="$TARGET/configure-agent-mode-macos.sh"

if [[ ! -x "$SOURCE/MateMCP.Agent" ]]; then
  echo "MateMCP payload not found at: $SOURCE" >&2
  exit 1
fi
[[ -f "$CONFIGURE_MODE_SOURCE" ]] || { echo "Agent mode configurator not found: $CONFIGURE_MODE_SOURCE" >&2; exit 1; }

if [[ -z "$AGENT_MODE" ]]; then
  if [[ -f "$MODE_FILE" ]]; then AGENT_MODE="$(tr -d '[:space:]' < "$MODE_FILE")"; fi
  [[ "$AGENT_MODE" == "Elevated" || "$AGENT_MODE" == "Normal" ]] || AGENT_MODE="Normal"
fi
[[ "$AGENT_MODE" == "Elevated" || "$AGENT_MODE" == "Normal" ]] || { echo "Agent mode must be Normal or Elevated." >&2; exit 2; }

mkdir -p "$TARGET" "$BIN" "$CONFIG"
# Stop the user LaunchAgent before replacing files. Elevated updates normally run
# from the already-root Agent, and the configurator handles the system daemon.
launchctl bootout "gui/$(id -u)/com.matemcp.agent" >/dev/null 2>&1 || true
rm -rf "$TARGET"/*
cp -R "$SOURCE"/* "$TARGET"/
chmod +x "$TARGET/MateMCP.Agent"
cp "$CONFIGURE_MODE_SOURCE" "$CONFIGURE_MODE"
chmod +x "$CONFIGURE_MODE"

if [[ -f "$PACKAGE_DIR/uninstall-macos.sh" ]]; then
  cp "$PACKAGE_DIR/uninstall-macos.sh" "$TARGET/uninstall-macos.sh"
  chmod +x "$TARGET/uninstall-macos.sh"
fi

if [[ -f "$PACKAGE_DIR/prepare-external-test-macos.sh" ]]; then
  cp "$PACKAGE_DIR/prepare-external-test-macos.sh" "$TARGET/prepare-external-test-macos.sh"
  chmod +x "$TARGET/prepare-external-test-macos.sh"
fi

ln -sfn "$TARGET/MateMCP.Agent" "$BIN/matemcp"

run_configurator() {
  local mode="$1"
  local extra="${2:-}"
  if [[ "$mode" == "Elevated" ]]; then
    if [[ "$EUID" -eq 0 ]]; then
      MATEMCP_TARGET_USER="${SUDO_USER:-$USER}" MATEMCP_TARGET_UID="$(id -u "${SUDO_USER:-$USER}")" MATEMCP_TARGET_HOME="$HOME" \
        "$CONFIGURE_MODE" "$mode" "$extra"
    else
      sudo MATEMCP_TARGET_USER="$USER" MATEMCP_TARGET_UID="$(id -u)" MATEMCP_TARGET_HOME="$HOME" \
        "$CONFIGURE_MODE" "$mode" "$extra"
    fi
  else
    # Removing a previously installed LaunchDaemon also requires authorization.
    if [[ -f /Library/LaunchDaemons/com.matemcp.agent.plist && "$EUID" -ne 0 ]]; then
      sudo MATEMCP_TARGET_USER="$USER" MATEMCP_TARGET_UID="$(id -u)" MATEMCP_TARGET_HOME="$HOME" \
        "$CONFIGURE_MODE" "$mode" "$extra"
    else
      "$CONFIGURE_MODE" "$mode" "$extra"
    fi
  fi
}

run_configurator "$AGENT_MODE" --no-start

echo "MateMCP installed/upgraded."
echo "Binary: $BIN/matemcp"
echo "Config: $CONFIG/appsettings.json"
echo "Credentials: macOS Keychain"
echo "Agent execution mode: $AGENT_MODE"
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

if [[ "$NO_START" != "--no-start" ]]; then
  run_configurator "$AGENT_MODE"
  echo "MateMCP Agent started."
fi
