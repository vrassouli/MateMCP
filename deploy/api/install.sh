#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${MATEMCP_API_INSTALL_DIR:-/opt/matemcp-api}"
REPO_RAW="https://raw.githubusercontent.com/vrassouli/MateMCP/main"
[[ $EUID -eq 0 ]] || { echo "Run as root (curl ... | sudo bash)." >&2; exit 1; }
command -v docker >/dev/null && docker compose version >/dev/null 2>&1 || { echo "Docker Engine with Compose v2 is required." >&2; exit 1; }

generate_secret() { openssl rand -hex 32 2>/dev/null || od -An -N32 -tx1 /dev/urandom | tr -d ' \n'; }
ask() {
  local prompt="$1" default="$2"
  printf '%s [%s]: ' "$prompt" "$default" >/dev/tty
  IFS= read -r ANSWER </dev/tty || true
  ANSWER="${ANSWER:-$default}"
}

ask_secret() {
  local prompt="$1"
  printf '%s: ' "$prompt" >/dev/tty
  IFS= read -r -s ANSWER </dev/tty || true
  printf '\n' >/dev/tty
}

mkdir -p "$INSTALL_DIR"
COMPOSE_URL="$REPO_RAW/deploy/api/docker-compose.yml"
echo "Downloading: $COMPOSE_URL"
curl -fsSL "$COMPOSE_URL" -o "$INSTALL_DIR/docker-compose.yml"
ENV_FILE="$INSTALL_DIR/.env"

if [[ -f "$ENV_FILE" ]]; then
  if ! grep -q '^MATEMCP_INTERNAL_API_KEY=.' "$ENV_FILE" || \
     ! grep -q '^MATEMCP_DB_PROVIDER=.' "$ENV_FILE" || \
     ! grep -q '^MATEMCP_DB_CONNECTION_STRING_BASE64=.' "$ENV_FILE"; then
    ENV_BACKUP="$ENV_FILE.pre-multi-user-$(date -u +%Y%m%dT%H%M%SZ)"
    cp "$ENV_FILE" "$ENV_BACKUP"
    chmod 600 "$ENV_BACKUP"
    rm "$ENV_FILE"
    echo "Legacy MateMCP API configuration detected."
    echo "Backup created at $ENV_BACKUP"
    echo "The API must be reconfigured for the multi-user control plane."
  else
    echo "Using existing API configuration from $ENV_FILE"
  fi
fi

if [[ ! -f "$ENV_FILE" ]]; then
  if [[ -n "${MATEMCP_API_PUBLIC_URL_INPUT:-}" ]]; then API_URL="$MATEMCP_API_PUBLIC_URL_INPUT"; else ask 'Public API URL' 'https://api.matemcp.com'; API_URL="$ANSWER"; fi
  if [[ -n "${MATEMCP_RELAY_PUBLIC_URL_INPUT:-}" ]]; then RELAY_URL="$MATEMCP_RELAY_PUBLIC_URL_INPUT"; else ask 'Public Relay URL' 'https://relay.matemcp.com'; RELAY_URL="$ANSWER"; fi
  ask 'Database provider (sqlite/sqlserver)' 'sqlite'; DB_PROVIDER="$ANSWER"
  DB_PROVIDER="${DB_PROVIDER,,}"
  case "$DB_PROVIDER" in
    sqlite)
      CONNECTION_STRING='Data Source=/data/matemcp-api.db'
      ;;
    sqlserver)
      ask 'SQL Server host or IP' 'host.docker.internal'; SQL_HOST="$ANSWER"
      ask 'SQL Server port' '1433'; SQL_PORT="$ANSWER"
      ask 'Database name' 'MateMCP'; SQL_DATABASE="$ANSWER"
      ask 'SQL Server username' 'sa'; SQL_USER="$ANSWER"
      ask_secret 'SQL Server password'; SQL_PASSWORD="$ANSWER"
      [[ -n "${SQL_PASSWORD:-}" ]] || { echo 'SQL Server password is required.' >&2; exit 1; }
      ask 'Encrypt SQL connection (true/false)' 'true'; ENCRYPT="$ANSWER"
      ask 'Trust server certificate (true/false)' 'false'; TRUST="$ANSWER"
      CONNECTION_STRING="Server=${SQL_HOST},${SQL_PORT};Database=${SQL_DATABASE};User Id=${SQL_USER};Password=${SQL_PASSWORD};Encrypt=${ENCRYPT};TrustServerCertificate=${TRUST};MultipleActiveResultSets=true"
      ;;
    *) echo 'Database provider must be sqlite or sqlserver.' >&2; exit 1 ;;
  esac
  CONNECTION_B64="$(printf '%s' "$CONNECTION_STRING" | base64 -w 0)"
  printf 'Bootstrap account email (optional): ' >/dev/tty
  read -r ADMIN_EMAIL </dev/tty || true
  ADMIN_PASSWORD=''
  if [[ -n "${ADMIN_EMAIL:-}" ]]; then ask_secret 'Bootstrap account password (minimum 10 characters)'; ADMIN_PASSWORD="$ANSWER"; [[ ${#ADMIN_PASSWORD} -ge 10 ]] || { echo 'Password is too short.' >&2; exit 1; }; fi
  umask 077
  {
    printf 'MATEMCP_API_IMAGE=%s\n' "${MATEMCP_API_IMAGE:-vrassouli/matemcp-api:dev}"
    printf 'MATEMCP_API_BIND=0.0.0.0\nMATEMCP_API_PORT=8081\n'
    printf 'MATEMCP_API_PUBLIC_URL=%s\nMATEMCP_RELAY_URL=%s\n' "$API_URL" "$RELAY_URL"
    printf 'MATEMCP_INTERNAL_API_KEY=%s\n' "$(generate_secret)"
    printf 'MATEMCP_DB_PROVIDER=%s\nMATEMCP_DB_CONNECTION_STRING_BASE64=%s\n' "$DB_PROVIDER" "$CONNECTION_B64"
    printf 'MATEMCP_BOOTSTRAP_ADMIN_EMAIL=%s\nMATEMCP_BOOTSTRAP_ADMIN_PASSWORD=%s\n' "$ADMIN_EMAIL" "$ADMIN_PASSWORD"
  } > "$ENV_FILE"
  chmod 600 "$ENV_FILE"
fi

cd "$INSTALL_DIR"
docker compose pull
docker compose up -d --force-recreate --remove-orphans
for _ in {1..45}; do curl -fsS http://127.0.0.1:8081/health >/dev/null 2>&1 && { echo "MateMCP API is running."; echo "Relay must use the same MATEMCP_INTERNAL_API_KEY from $ENV_FILE"; exit 0; }; sleep 1; done
echo "MateMCP API did not become healthy. Run: cd $INSTALL_DIR && docker compose logs" >&2; exit 1
