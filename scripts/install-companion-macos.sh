#!/usr/bin/env bash
set -euo pipefail

SOURCE="${1:-./payload}"
NO_START="${2:-}"
PACKAGE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPLICATIONS="$HOME/Applications"
SUPPORT="$HOME/Library/Application Support/MateMCP"
APP_SOURCE="$(find "$SOURCE" -maxdepth 1 -type d -name '*.app' -print -quit)"
APP_TARGET="$APPLICATIONS/MateMCP Agent Companion.app"
LAUNCH_AGENTS="$HOME/Library/LaunchAgents"
LAUNCH_LABEL="com.matemcp.agent.companion"
LAUNCH_PLIST="$LAUNCH_AGENTS/$LAUNCH_LABEL.plist"
LAUNCH_DOMAIN="gui/$(id -u)"

if [[ -z "$APP_SOURCE" || ! -d "$APP_SOURCE" ]]; then
  echo "MateMCP Agent Companion app bundle not found at: $SOURCE" >&2
  exit 1
fi

mkdir -p "$APPLICATIONS" "$LAUNCH_AGENTS" "$SUPPORT"
launchctl bootout "$LAUNCH_DOMAIN/$LAUNCH_LABEL" >/dev/null 2>&1 || true
rm -rf "$APP_TARGET"
cp -R "$APP_SOURCE" "$APP_TARGET"

EXECUTABLE_NAME="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP_TARGET/Contents/Info.plist")"
EXECUTABLE="$APP_TARGET/Contents/MacOS/$EXECUTABLE_NAME"
chmod +x "$EXECUTABLE"

/usr/bin/plutil -create xml1 "$LAUNCH_PLIST"
/usr/bin/plutil -insert Label -string "$LAUNCH_LABEL" "$LAUNCH_PLIST"
/usr/bin/plutil -insert ProgramArguments -array "$LAUNCH_PLIST"
/usr/bin/plutil -insert ProgramArguments.0 -string "$EXECUTABLE" "$LAUNCH_PLIST"
/usr/bin/plutil -insert RunAtLoad -bool true "$LAUNCH_PLIST"
chmod 600 "$LAUNCH_PLIST"

if [[ -f "$PACKAGE_DIR/uninstall-companion-macos.sh" ]]; then
  cp "$PACKAGE_DIR/uninstall-companion-macos.sh" "$SUPPORT/uninstall-companion-macos.sh"
  chmod +x "$SUPPORT/uninstall-companion-macos.sh"
fi

echo "MateMCP Agent Companion installed/upgraded."
echo "Application: $APP_TARGET"
echo "LaunchAgent: $LAUNCH_PLIST"

if [[ "$NO_START" != "--no-start" ]]; then
  launchctl bootstrap "$LAUNCH_DOMAIN" "$LAUNCH_PLIST"
  launchctl kickstart -k "$LAUNCH_DOMAIN/$LAUNCH_LABEL"
  echo "MateMCP Agent Companion started."
else
  echo "Start manually: open \"$APP_TARGET\""
fi
