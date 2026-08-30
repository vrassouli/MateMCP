#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${MATEMCP_API_INSTALL_DIR:-/opt/matemcp-api}"
REPO_RAW="${MATEMCP_REPO_RAW:-https://raw.githubusercontent.com/vrassouli/MateMCP/feat/relay-mvp}"

if [[ $EUID -ne 0 ]]; then
  echo "Run this installer as root (for example: curl ... | sudo bash)." >&2
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is required. Install Docker Engine first." >&2
  exit 1
fi

if ! docker compose version >/dev/null 2>&1; then
  echo "Docker Compose v2 is required." >&2
  exit 1
fi

mkdir -p "$INSTALL_DIR"
curl -fsSL "$REPO_RAW/deploy/api/docker-compose.yml" -o "$INSTALL_DIR/docker-compose.yml"

ENV_FILE="$INSTALL_DIR/.env"
if [[ ! -f "$ENV_FILE" ]]; then
  read -r -p "MateMCP admin email [admin@matemcp.com]: " ADMIN_EMAIL </dev/tty || true
  ADMIN_EMAIL="${ADMIN_EMAIL:-admin@matemcp.com}"
  read -r -s -p "MateMCP admin password: " ADMIN_PASSWORD </dev/tty || true
  echo >/dev/tty || true

  if [[ -z "${ADMIN_PASSWORD:-}" ]]; then
    echo "Admin password is required." >&2
    exit 1
  fi

  cat > "$ENV_FILE" <<EOF
MATEMCP_API_IMAGE=vrassouli/matemcp-api:dev
MATEMCP_API_BIND=0.0.0.0
MATEMCP_API_PORT=8081
MATEMCP_API_PUBLIC_URL=https://api.matemcp.com
MATEMCP_RELAY_RESOURCE=https://relay.matemcp.com
MATEMCP_API_ADMIN_EMAIL=$ADMIN_EMAIL
MATEMCP_API_ADMIN_PASSWORD=$ADMIN_PASSWORD
EOF
  chmod 600 "$ENV_FILE"
fi

cd "$INSTALL_DIR"
docker compose pull
docker compose up -d

for _ in {1..30}; do
  if curl -fsS http://127.0.0.1:8081/health >/dev/null 2>&1; then
    echo
    echo "MateMCP API is running."
    echo "Local health: http://127.0.0.1:8081/health"
    echo "Public URL: https://api.matemcp.com"
    exit 0
  fi
  sleep 1
done

echo "MateMCP API did not become healthy. Check: cd $INSTALL_DIR && docker compose logs" >&2
exit 1
