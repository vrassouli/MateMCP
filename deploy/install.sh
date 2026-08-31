#!/usr/bin/env bash
set -euo pipefail

REPO="${MATEMCP_REPO:-vrassouli/MateMCP}"
REF="${MATEMCP_REF:-feat/multi-user-control-plane}"

[[ $EUID -eq 0 ]] || { echo "Run as root (curl ... | sudo bash)." >&2; exit 1; }

ask() {
  local prompt="$1" default="$2"
  printf '%s [%s]: ' "$prompt" "$default" >/dev/tty
  IFS= read -r ANSWER </dev/tty || true
  ANSWER="${ANSWER:-$default}"
}

ask 'Public API URL' 'https://api.matemcp.com'
export MATEMCP_API_PUBLIC_URL_INPUT="$ANSWER"
ask 'Public Relay URL' 'https://relay.matemcp.com'
export MATEMCP_RELAY_PUBLIC_URL_INPUT="$ANSWER"
export MATEMCP_API_INTERNAL_URL_INPUT="${MATEMCP_API_INTERNAL_URL_INPUT:-$MATEMCP_API_PUBLIC_URL_INPUT}"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

download() {
  local component="$1"
  curl -fsSL "https://raw.githubusercontent.com/${REPO}/${REF}/deploy/${component}/install.sh" -o "$TMP_DIR/install-${component}.sh"
  chmod +x "$TMP_DIR/install-${component}.sh"
}

printf '\n==> Downloading MateMCP installers\n'
download api
download relay

printf '\n==> Installing MateMCP API / Control Plane\n'
bash "$TMP_DIR/install-api.sh"

printf '\n==> Installing MateMCP Relay\n'
bash "$TMP_DIR/install-relay.sh"

printf '\nMateMCP server installation completed successfully.\n'
printf 'API health:   %s/health\n' "$MATEMCP_API_PUBLIC_URL_INPUT"
printf 'Relay health: %s/health\n' "$MATEMCP_RELAY_PUBLIC_URL_INPUT"
