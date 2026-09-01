#!/usr/bin/env bash
set -euo pipefail

APP_TARGET="$HOME/Applications/MateMCP Agent Companion.app"
LAUNCH_AGENTS="$HOME/Library/LaunchAgents"
LAUNCH_LABEL="com.matemcp.agent.companion"
LAUNCH_PLIST="$LAUNCH_AGENTS/$LAUNCH_LABEL.plist"
LAUNCH_DOMAIN="gui/$(id -u)"
SELF_COPY="$HOME/Library/Application Support/MateMCP/uninstall-companion-macos.sh"

launchctl bootout "$LAUNCH_DOMAIN/$LAUNCH_LABEL" >/dev/null 2>&1 || true
rm -f "$LAUNCH_PLIST"
rm -rf "$APP_TARGET"

if [[ "$SELF_COPY" != "${BASH_SOURCE[0]}" ]]; then
  rm -f "$SELF_COPY"
fi

echo "MateMCP Agent Companion uninstalled."
