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

COMPANION_PROCESS_PATTERN="$APP_TARGET/Contents/MacOS/"
if /usr/bin/pgrep -f "$COMPANION_PROCESS_PATTERN" >/dev/null 2>&1; then
  # Prefer an ordinary app quit so the Companion can persist a clean lifecycle
  # terminal event. Fall back to TERM only if the GUI does not exit promptly.
  /usr/bin/osascript -e 'tell application "MateMCP Agent Companion" to quit' >/dev/null 2>&1 || true
  close_attempt=0
  while /usr/bin/pgrep -f "$COMPANION_PROCESS_PATTERN" >/dev/null 2>&1 && [[ "$close_attempt" -lt 50 ]]; do
    sleep 0.1
    close_attempt=$((close_attempt + 1))
  done
  /usr/bin/pkill -TERM -f "$COMPANION_PROCESS_PATTERN" >/dev/null 2>&1 || true
fi

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
