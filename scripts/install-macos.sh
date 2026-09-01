#!/usr/bin/env bash
set -euo pipefail

PACKAGE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE="${1:-./payload}"
NO_START="${2:-}"
MODE="${3:-}"
DESKTOP_INSTALLER="$PACKAGE_DIR/install-desktop-macos.sh"

# In a unified Desktop package, install-macos.sh is the natural entry point.
# Delegate to the Desktop installer unless this script is being invoked
# internally for the Agent component only.
if [[ "$MODE" != "--agent-only" \
   && -x "$DESKTOP_INSTALLER" \
   && -x "$PACKAGE_DIR/agent-payload/MateMCP.Agent" \
   && -d "$PACKAGE_DIR/companion-payload" ]]; then
  if [[ "$SOURCE" == "--no-start" || "$NO_START" == "--no-start" ]]; then
    "$DESKTOP_INSTALLER" --no-start
  else
    "$DESKTOP_INSTALLER"
  fi
  exit 0
fi

TARGET="$HOME/.local/share/matemcp"
BIN="$HOME/.local/bin"
CONFIG="$HOME/Library/Application Support/MateMCP"
LAUNCH_AGENTS="$HOME/Library/LaunchAgents"
LAUNCH_LABEL="com.matemcp.agent"
LAUNCH_PLIST="$LAUNCH_AGENTS/$LAUNCH_LABEL.plist"
LAUNCH_DOMAIN="gui/$(id -u)"

if [[ ! -x "$SOURCE/MateMCP.Agent" ]]; then
  echo "MateMCP payload not found at: $SOURCE" >&2
  exit 1
fi

mkdir -p "$TARGET" "$BIN" "$CONFIG" "$LAUNCH_AGENTS"
launchctl bootout "$LAUNCH_DOMAIN/$LAUNCH_LABEL" >/dev/null 2>&1 || true
rm -rf "$TARGET"/*
cp -R "$SOURCE"/* "$TARGET"/
chmod +x "$TARGET/MateMCP.Agent"

if [[ -f "$PACKAGE_DIR/uninstall-macos.sh" ]]; then
  cp "$PACKAGE_DIR/uninstall-macos.sh" "$TARGET/uninstall-macos.sh"
  chmod +x "$TARGET/uninstall-macos.sh"
fi

if [[ -f "$PACKAGE_DIR/prepare-external-test-macos.sh" ]]; then
  cp "$PACKAGE_DIR/prepare-external-test-macos.sh" "$TARGET/prepare-external-test-macos.sh"
  chmod +x "$TARGET/prepare-external-test-macos.sh"
fi

ln -sfn "$TARGET/MateMCP.Agent" "$BIN/matemcp"

/usr/bin/plutil -create xml1 "$LAUNCH_PLIST"
/usr/bin/plutil -insert Label -string "$LAUNCH_LABEL" "$LAUNCH_PLIST"
/usr/bin/plutil -insert ProgramArguments -array "$LAUNCH_PLIST"
/usr/bin/plutil -insert ProgramArguments.0 -string "$TARGET/MateMCP.Agent" "$LAUNCH_PLIST"
/usr/bin/plutil -insert WorkingDirectory -string "$TARGET" "$LAUNCH_PLIST"
/usr/bin/plutil -insert RunAtLoad -bool true "$LAUNCH_PLIST"
/usr/bin/plutil -insert KeepAlive -bool true "$LAUNCH_PLIST"
/usr/bin/plutil -insert StandardOutPath -string "$CONFIG/agent.log" "$LAUNCH_PLIST"
/usr/bin/plutil -insert StandardErrorPath -string "$CONFIG/agent-error.log" "$LAUNCH_PLIST"
chmod 600 "$LAUNCH_PLIST"

echo "MateMCP installed/upgraded."
echo "Binary: $BIN/matemcp"
echo "Config: $CONFIG/appsettings.json"
echo "Credentials: macOS Keychain"
echo "LaunchAgent: $LAUNCH_PLIST"
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
  launchctl bootstrap "$LAUNCH_DOMAIN" "$LAUNCH_PLIST"
  launchctl kickstart -k "$LAUNCH_DOMAIN/$LAUNCH_LABEL"
  echo "MateMCP Agent started."
else
  echo "Start MateMCP:"
  echo "  launchctl bootstrap $LAUNCH_DOMAIN $LAUNCH_PLIST"
fi
