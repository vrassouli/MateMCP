#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${MATEMCP_WEB_INSTALL_DIR:-/opt/matemcp-web}"
REF="${MATEMCP_INSTALL_REF:-main}"
REPO_RAW="https://raw.githubusercontent.com/vrassouli/MateMCP/${REF}"

[[ $EUID -eq 0 ]] || { echo "Run as root (curl ... | sudo bash)." >&2; exit 1; }

ask() {
  local prompt="$1" default="$2"
  printf '%s [%s]: ' "$prompt" "$default" >/dev/tty
  IFS= read -r ANSWER </dev/tty || true
  ANSWER="${ANSWER:-$default}"
}

install_docker() {
  if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    return
  fi

  if ! command -v apt-get >/dev/null 2>&1; then
    echo "Docker is not installed and this installer currently supports automatic Docker setup on Debian/Ubuntu only." >&2
    exit 1
  fi

  echo
  echo "==> Installing Docker Engine and Compose plugin"
  apt-get update
  apt-get install -y ca-certificates curl gnupg
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL "https://download.docker.com/linux/$(. /etc/os-release && echo "$ID")/gpg" | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  chmod a+r /etc/apt/keyrings/docker.gpg
  . /etc/os-release
  arch="$(dpkg --print-architecture)"
  echo "deb [arch=${arch} signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/${ID} ${VERSION_CODENAME} stable" > /etc/apt/sources.list.d/docker.list
  apt-get update
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  systemctl enable --now docker
}

install_docker

mkdir -p "$INSTALL_DIR"
COMPOSE_URL="$REPO_RAW/deploy/web/docker-compose.yml"
echo "Downloading: $COMPOSE_URL"
curl -fsSL "$COMPOSE_URL" -o "$INSTALL_DIR/docker-compose.yml"
ENV_FILE="$INSTALL_DIR/.env"

if [[ ! -f "$ENV_FILE" ]]; then
  if [[ -n "${MATEMCP_WEB_BIND_INPUT:-}" ]]; then
    WEB_BIND="$MATEMCP_WEB_BIND_INPUT"
  else
    ask 'Web bind address (use the LAN IP when the reverse proxy is on another host)' '127.0.0.1'
    WEB_BIND="$ANSWER"
  fi

  if [[ -n "${MATEMCP_WEB_PORT_INPUT:-}" ]]; then
    WEB_PORT="$MATEMCP_WEB_PORT_INPUT"
  else
    ask 'Web host port' '8082'
    WEB_PORT="$ANSWER"
  fi

  umask 077
  {
    printf 'MATEMCP_WEB_IMAGE=%s\n' "${MATEMCP_WEB_IMAGE:-vrassouli/matemcp-web:latest}"
    printf 'MATEMCP_WEB_BIND=%s\n' "$WEB_BIND"
    printf 'MATEMCP_WEB_PORT=%s\n' "$WEB_PORT"
  } > "$ENV_FILE"
  chmod 600 "$ENV_FILE"
else
  echo "Using existing Web configuration from $ENV_FILE"
fi

cd "$INSTALL_DIR"
docker compose pull
docker compose up -d --force-recreate --remove-orphans

WEB_PORT="$(sed -n 's/^MATEMCP_WEB_PORT=//p' "$ENV_FILE" | head -n 1)"
WEB_PORT="${WEB_PORT:-8082}"
for _ in {1..45}; do
  curl -fsS "http://127.0.0.1:${WEB_PORT}/health" >/dev/null 2>&1 && {
    echo "MateMCP public Web is running."
    exit 0
  }
  sleep 1
done

echo "MateMCP public Web did not become healthy. Run: cd $INSTALL_DIR && docker compose logs" >&2
exit 1
