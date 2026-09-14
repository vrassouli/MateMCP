#!/usr/bin/env bash
set -euo pipefail

REPO="vrassouli/MateMCP"
REF="${MATEMCP_INSTALL_REF:-main}"
API_INSTALL_DIR="${MATEMCP_API_INSTALL_DIR:-/opt/matemcp-api}"
RELAY_INSTALL_DIR="${MATEMCP_RELAY_DIR:-/opt/matemcp-relay}"

[[ $EUID -eq 0 ]] || { echo "Run as root (curl ... | sudo bash)." >&2; exit 1; }

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

download() {
  local component="$1"
  local url="https://raw.githubusercontent.com/${REPO}/${REF}/deploy/${component}/install.sh"
  echo "Downloading: $url"
  curl -fsSL "$url" -o "$TMP_DIR/install-${component}.sh"
  chmod +x "$TMP_DIR/install-${component}.sh"
}

read_env_value() {
  local file="$1" key="$2"
  [[ -r "$file" ]] || return 0
  sed -n "s/^${key}=//p" "$file" | head -n 1
}

replace_env_value() {
  local file="$1" key="$2" value="$3" tmp line
  tmp="$(mktemp "${file}.tmp.XXXXXX")"
  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == "${key}="* ]]; then
      printf '%s=%s\n' "$key" "$value"
    else
      printf '%s\n' "$line"
    fi
  done < "$file" > "$tmp"
  chmod 600 "$tmp"
  mv "$tmp" "$file"
}

printf '\n==> Downloading MateMCP server installers\n'
download api
download relay

printf '\n==> Installing/updating MateMCP API / Control Plane\n'
bash "$TMP_DIR/install-api.sh"

# The API installer owns the canonical public URL prompts on a fresh setup.
# Reuse the resulting configuration for Relay so users are not asked for the
# same values twice. On normal updates the existing API .env is preserved and
# no public URL prompt is needed at all.
API_ENV_FILE="${MATEMCP_API_ENV_FILE:-${API_INSTALL_DIR}/.env}"
RELAY_ENV_FILE="${RELAY_INSTALL_DIR}/.env"
export MATEMCP_API_ENV_FILE="$API_ENV_FILE"

if [[ -r "$API_ENV_FILE" ]]; then
  API_PUBLIC_URL="$(read_env_value "$API_ENV_FILE" MATEMCP_API_PUBLIC_URL)"
  RELAY_PUBLIC_URL="$(read_env_value "$API_ENV_FILE" MATEMCP_RELAY_URL)"
  API_INTERNAL_KEY="$(read_env_value "$API_ENV_FILE" MATEMCP_INTERNAL_API_KEY)"

  if [[ -n "${API_PUBLIC_URL:-}" ]]; then
    export MATEMCP_API_PUBLIC_URL_INPUT="${MATEMCP_API_PUBLIC_URL_INPUT:-$API_PUBLIC_URL}"
    export MATEMCP_API_INTERNAL_URL_INPUT="${MATEMCP_API_INTERNAL_URL_INPUT:-$API_PUBLIC_URL}"
  fi
  if [[ -n "${RELAY_PUBLIC_URL:-}" ]]; then
    export MATEMCP_RELAY_PUBLIC_URL_INPUT="${MATEMCP_RELAY_PUBLIC_URL_INPUT:-$RELAY_PUBLIC_URL}"
  fi

  # If Relay already has a modern configuration, keep its shared Control Plane
  # key aligned with the API. This also handles an API legacy-config migration
  # that rotates the internal key without exposing the secret to the terminal.
  if [[ -n "${API_INTERNAL_KEY:-}" && -f "$RELAY_ENV_FILE" ]] && \
     grep -q '^MATEMCP_INTERNAL_API_KEY=.' "$RELAY_ENV_FILE" && \
     grep -q '^MATEMCP_API_INTERNAL_URL=.' "$RELAY_ENV_FILE"; then
    RELAY_INTERNAL_KEY="$(read_env_value "$RELAY_ENV_FILE" MATEMCP_INTERNAL_API_KEY)"
    if [[ "$RELAY_INTERNAL_KEY" != "$API_INTERNAL_KEY" ]]; then
      replace_env_value "$RELAY_ENV_FILE" MATEMCP_INTERNAL_API_KEY "$API_INTERNAL_KEY"
      echo "Synchronized Relay internal Control Plane credential."
    fi
  fi
fi

printf '\n==> Installing/updating MateMCP Relay\n'
bash "$TMP_DIR/install-relay.sh"

printf '\nMateMCP server installation/update completed successfully.\n'
if [[ -n "${MATEMCP_API_PUBLIC_URL_INPUT:-}" ]]; then
  printf 'API health:   %s/health\n' "$MATEMCP_API_PUBLIC_URL_INPUT"
fi
if [[ -n "${MATEMCP_RELAY_PUBLIC_URL_INPUT:-}" ]]; then
  printf 'Relay health: %s/health\n' "$MATEMCP_RELAY_PUBLIC_URL_INPUT"
fi
