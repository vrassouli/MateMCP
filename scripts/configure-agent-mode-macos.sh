#!/usr/bin/env bash
set -euo pipefail

MODE="${1:-}"
NO_START="${2:-}"
USER_NAME="${MATEMCP_TARGET_USER:-${SUDO_USER:-$USER}}"
USER_UID="${MATEMCP_TARGET_UID:-$(id -u "$USER_NAME")}"
USER_HOME="${MATEMCP_TARGET_HOME:-$(dscl . -read "/Users/$USER_NAME" NFSHomeDirectory 2>/dev/null | awk '{print $2}')}"

[[ "$MODE" == "Normal" || "$MODE" == "Elevated" ]] || { echo "Usage: $0 Normal|Elevated [--no-start]" >&2; exit 2; }
[[ -n "$USER_HOME" ]] || { echo "Could not determine MateMCP user's home directory." >&2; exit 1; }

AGENT_ROOT="$USER_HOME/.local/share/matemcp"
AGENT_EXE="$AGENT_ROOT/MateMCP.Agent"
CONFIG_ROOT="$USER_HOME/Library/Application Support/MateMCP"
MODE_FILE="$CONFIG_ROOT/agent-run-mode.txt"
USER_LAUNCH_DIR="$USER_HOME/Library/LaunchAgents"
USER_PLIST="$USER_LAUNCH_DIR/com.matemcp.agent.plist"
DAEMON_PLIST="/Library/LaunchDaemons/com.matemcp.agent.plist"
DAEMON_LOG_DIR="/Library/Logs/MateMCP"
LABEL="com.matemcp.agent"
GUI_DOMAIN="gui/$USER_UID"

[[ -x "$AGENT_EXE" ]] || { echo "MateMCP Agent not found: $AGENT_EXE" >&2; exit 1; }
mkdir -p "$CONFIG_ROOT" "$USER_LAUNCH_DIR"
chown "$USER_NAME" "$CONFIG_ROOT" "$USER_LAUNCH_DIR" 2>/dev/null || true

write_user_plist() {
  cat > "$USER_PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key><array><string>$AGENT_EXE</string></array>
  <key>WorkingDirectory</key><string>$AGENT_ROOT</string>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>$CONFIG_ROOT/agent.log</string>
  <key>StandardErrorPath</key><string>$CONFIG_ROOT/agent-error.log</string>
</dict>
</plist>
PLIST
  chown "$USER_NAME" "$USER_PLIST" 2>/dev/null || true
  chmod 600 "$USER_PLIST"
}

write_daemon_plist() {
  [[ "$EUID" -eq 0 ]] || { echo "Elevated mode requires Administrator authorization." >&2; exit 3; }
  mkdir -p "$DAEMON_LOG_DIR"
  chmod 755 "$DAEMON_LOG_DIR"
  cat > "$DAEMON_PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key><array><string>$AGENT_EXE</string></array>
  <key>WorkingDirectory</key><string>$AGENT_ROOT</string>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>EnvironmentVariables</key>
  <dict>
    <key>HOME</key><string>$USER_HOME</string>
    <key>USER</key><string>$USER_NAME</string>
    <key>LOGNAME</key><string>$USER_NAME</string>
    <key>MATEMCP_MAC_USER_HOME</key><string>$USER_HOME</string>
    <key>MATEMCP_MAC_USER_NAME</key><string>$USER_NAME</string>
    <key>MATEMCP_MAC_USER_UID</key><string>$USER_UID</string>
  </dict>
  <key>StandardOutPath</key><string>$DAEMON_LOG_DIR/agent.log</string>
  <key>StandardErrorPath</key><string>$DAEMON_LOG_DIR/agent-error.log</string>
</dict>
</plist>
PLIST
  chown root:wheel "$DAEMON_PLIST"
  chmod 644 "$DAEMON_PLIST"
}

# Stop both possible startup mechanisms before changing ownership/mode.
launchctl bootout "$GUI_DOMAIN/$LABEL" >/dev/null 2>&1 || true
if [[ "$EUID" -eq 0 ]]; then
  launchctl bootout "system/$LABEL" >/dev/null 2>&1 || true
elif [[ -f "$DAEMON_PLIST" ]]; then
  echo "Switching away from an elevated Agent requires Administrator authorization." >&2
  exit 3
fi

if [[ "$MODE" == "Elevated" ]]; then
  rm -f "$USER_PLIST"
  write_daemon_plist
else
  if [[ "$EUID" -eq 0 ]]; then rm -f "$DAEMON_PLIST"; fi
  write_user_plist
fi

printf '%s\n' "$MODE" > "$MODE_FILE"
chown "$USER_NAME" "$MODE_FILE" 2>/dev/null || true
chmod 600 "$MODE_FILE"

if [[ "$NO_START" != "--no-start" ]]; then
  if [[ "$MODE" == "Elevated" ]]; then
    launchctl bootstrap system "$DAEMON_PLIST" >/dev/null 2>&1 || true
    launchctl kickstart -k "system/$LABEL"
  else
    # If this script was authorized as root, enter the user's GUI bootstrap
    # namespace for LaunchAgent operations rather than creating a root GUI job.
    if [[ "$EUID" -eq 0 ]]; then
      launchctl asuser "$USER_UID" sudo -u "$USER_NAME" launchctl bootstrap "$GUI_DOMAIN" "$USER_PLIST" >/dev/null 2>&1 || true
      launchctl asuser "$USER_UID" sudo -u "$USER_NAME" launchctl kickstart -k "$GUI_DOMAIN/$LABEL"
    else
      launchctl bootstrap "$GUI_DOMAIN" "$USER_PLIST" >/dev/null 2>&1 || true
      launchctl kickstart -k "$GUI_DOMAIN/$LABEL"
    fi
  fi
fi

echo "MateMCP Agent execution mode: $MODE"
echo "Mode state: $MODE_FILE"
