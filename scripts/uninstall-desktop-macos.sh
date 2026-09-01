#!/usr/bin/env bash
set -euo pipefail

CONFIG="$HOME/Library/Application Support/MateMCP"
COMPANION_UNINSTALL="$CONFIG/uninstall-companion-macos.sh"
AGENT_UNINSTALL="$HOME/.local/share/matemcp/uninstall-macos.sh"
SELF_COPY="$CONFIG/uninstall-desktop-macos.sh"

if [[ -x "$COMPANION_UNINSTALL" ]]; then
  "$COMPANION_UNINSTALL"
else
  launchctl bootout "gui/$(id -u)/com.matemcp.agent.companion" >/dev/null 2>&1 || true
  rm -f "$HOME/Library/LaunchAgents/com.matemcp.agent.companion.plist"
  rm -rf "$HOME/Applications/MateMCP Agent Companion.app"
fi

if [[ -x "$AGENT_UNINSTALL" ]]; then
  "$AGENT_UNINSTALL"
else
  launchctl bootout "gui/$(id -u)/com.matemcp.agent" >/dev/null 2>&1 || true
  rm -f "$HOME/Library/LaunchAgents/com.matemcp.agent.plist"
  rm -f "$HOME/.local/bin/matemcp"
  rm -rf "$HOME/.local/share/matemcp"
fi

rm -f "$SELF_COPY"
echo "MateMCP Desktop removed. Configuration, audit data, and Keychain credentials were preserved."
