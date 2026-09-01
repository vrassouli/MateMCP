#!/usr/bin/env bash
set -euo pipefail

SOURCE="${1:-./payload}"
NO_START="${2:-}"
PACKAGE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPLICATIONS="$HOME/Applications"
SUPPORT="$HOME/Library/Application Support/MateMCP"
APP_SOURCE="$(find "$SOURCE" -maxdepth 1 -type d -name '*.app' -print -quit)"
APP_TARGET="$APPLICATIONS/MateMCP Agent Companion.app"
LAUNCH_LABEL="com.matemcp.agent.companion"
LAUNCH_PLIST="$HOME/Library/LaunchAgents/$LAUNCH_LABEL.plist"
LAUNCH_DOMAIN="gui/$(id -u)"

if [[ -z "$APP_SOURCE" || ! -d "$APP_SOURCE" ]]; then
  echo "MateMCP Agent Companion app bundle not found at: $SOURCE" >&2
  exit 1
fi

mkdir -p "$APPLICATIONS" "$SUPPORT" "$HOME/Library/LaunchAgents"
# Remove the old direct-executable LaunchAgent. Launching a Mac Catalyst GUI app
# directly from launchd can produce crash/reopen dialogs during login.
launchctl bootout "$LAUNCH_DOMAIN/$LAUNCH_LABEL" >/dev/null 2>&1 || true
rm -f "$LAUNCH_PLIST"

rm -rf "$APP_TARGET"
cp -R "$APP_SOURCE" "$APP_TARGET"
# CI test artifacts are ad-hoc signed rather than notarized. Clear quarantine so local development/test installs can launch.
xattr -dr com.apple.quarantine "$APP_TARGET" >/dev/null 2>&1 || true

EXECUTABLE_NAME="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP_TARGET/Contents/Info.plist")"
EXECUTABLE="$APP_TARGET/Contents/MacOS/$EXECUTABLE_NAME"
chmod +x "$EXECUTABLE"

if [[ -f "$PACKAGE_DIR/uninstall-companion-macos.sh" ]]; then
  cp "$PACKAGE_DIR/uninstall-companion-macos.sh" "$SUPPORT/uninstall-companion-macos.sh"
  chmod +x "$SUPPORT/uninstall-companion-macos.sh"
fi

echo "MateMCP Agent Companion installed/upgraded."
echo "Application: $APP_TARGET"
echo "Auto-start: disabled (open Companion only when needed)"

if [[ "$NO_START" != "--no-start" ]]; then
  open "$APP_TARGET"
  echo "MateMCP Agent Companion opened."
else
  echo "Start manually: open \"$APP_TARGET\""
fi
