#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${MATEMCP_API_INSTALL_DIR:-/opt/matemcp-api}"
REPO_RAW="${MATEMCP_REPO_RAW:-https://raw.githubusercontent.com/vrassouli/MateMCP/feat/multi-user-control-plane}"
[[ $EUID -eq 0 ]] || { echo "Run as root (curl ... | sudo bash)." >&2; exit 1; }
command -v docker >/dev/null && docker compose version >/dev/null 2>&1 || { echo "Docker Engine with Compose v2 is required." >&2; exit 1; }

generate_secret() { openssl rand -hex 32 2>/dev/null || od -An -N32 -tx1 /dev/urandom | tr -d ' \n'; }
ask() { local prompt="$1" default="$2" value; read -r -p "$prompt [$default]: " value </dev/tty || true; printf '%s' "${value:-$default}"; }

mkdir -p "$INSTALL_DIR"
curl -fsSL "$REPO_RAW/deploy/api/docker-compose.yml" -o "$INSTALL_DIR/docker-compose.yml"
ENV_FILE="$INSTALL_DIR/.env"
if [[ ! -f "$ENV_FILE" ]]; then
  API_URL="$(ask 'Public API URL' 'https://api.matemcp.com')"
  RELAY_URL="$(ask 'Public Relay URL' 'https://relay.matemcp.com')"
  DB_PROVIDER="$(ask 'Database provider (sqlite/sqlserver)' 'sqlite')"
  DB_PROVIDER="${DB_PROVIDER,,}"
  case "$DB_PROVIDER" in
    sqlite)
      CONNECTION_STRING='Data Source=/data/matemcp-api.db'
      ;;
    sqlserver)
      SQL_HOST="$(ask 'SQL Server host or IP' 'host.docker.internal')"
      SQL_PORT="$(ask 'SQL Server port' '1433')"
      SQL_DATABASE="$(ask 'Database name' 'MateMCP')"
      SQL_USER="$(ask 'SQL Server username' 'sa')"
      read -r -s -p 'SQL Server password: ' SQL_PASSWORD </dev/tty || true; echo >/dev/tty || true
      [[ -n "${SQL_PASSWORD:-}" ]] || { echo 'SQL Server password is required.' >&2; exit 1; }
      ENCRYPT="$(ask 'Encrypt SQL connection (true/false)' 'true')"
      TRUST="$(ask 'Trust server certificate (true/false)' 'false')"
      CONNECTION_STRING="Server=${SQL_HOST},${SQL_PORT};Database=${SQL_DATABASE};User Id=${SQL_USER};Password=${SQL_PASSWORD};Encrypt=${ENCRYPT};TrustServerCertificate=${TRUST};MultipleActiveResultSets=true"
      ;;
    *) echo 'Database provider must be sqlite or sqlserver.' >&2; exit 1 ;;
  esac
  CONNECTION_B64="$(printf '%s' "$CONNECTION_STRING" | base64 -w 0)"
  read -r -p 'Bootstrap account email (optional): ' ADMIN_EMAIL </dev/tty || true
  ADMIN_PASSWORD=''
  if [[ -n "${ADMIN_EMAIL:-}" ]]; then read -r -s -p 'Bootstrap account password (minimum 10 characters): ' ADMIN_PASSWORD </dev/tty || true; echo >/dev/tty || true; [[ ${#ADMIN_PASSWORD} -ge 10 ]] || { echo 'Password is too short.' >&2; exit 1; }; fi
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

cd "$INSTALL_DIR"; docker compose pull; docker compose up -d
for _ in {1..45}; do curl -fsS http://127.0.0.1:8081/health >/dev/null 2>&1 && { echo "MateMCP API is running."; echo "Relay must use the same MATEMCP_INTERNAL_API_KEY from $ENV_FILE"; exit 0; }; sleep 1; done
echo "MateMCP API did not become healthy. Run: cd $INSTALL_DIR && docker compose logs" >&2; exit 1
