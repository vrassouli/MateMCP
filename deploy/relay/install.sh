#!/usr/bin/env bash
set -euo pipefail

REPO="vrassouli/MateMCP"
REF="${MATEMCP_REF:-feat/relay-mvp}"
INSTALL_DIR="${MATEMCP_RELAY_DIR:-/opt/matemcp-relay}"
COMPOSE_URL="https://raw.githubusercontent.com/${REPO}/${REF}/deploy/relay/docker-compose.yml"

if [ "${EUID}" -ne 0 ]; then
  if ! command -v sudo >/dev/null 2>&1; then
    echo "This installer needs root privileges (sudo not found)." >&2
    exit 1
  fi
  exec sudo --preserve-env=MATEMCP_REF,MATEMCP_RELAY_DIR,MATEMCP_RELAY_IMAGE,MATEMCP_RELAY_BIND,MATEMCP_RELAY_PORT bash "$0" "$@"
fi

log() { printf '\n==> %s\n' "$*"; }

install_docker() {
  if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    return
  fi

  if ! command -v apt-get >/dev/null 2>&1; then
    echo "Docker is not installed and this installer currently supports automatic Docker setup on Debian/Ubuntu only." >&2
    exit 1
  fi

  log "Installing Docker Engine and Compose plugin"
  apt-get update
  apt-get install -y ca-certificates curl gnupg
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/$(. /etc/os-release && echo "$ID")/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  chmod a+r /etc/apt/keyrings/docker.gpg
  . /etc/os-release
  arch="$(dpkg --print-architecture)"
  echo "deb [arch=${arch} signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/${ID} ${VERSION_CODENAME} stable" > /etc/apt/sources.list.d/docker.list
  apt-get update
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  systemctl enable --now docker
}

generate_token() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 32
  else
    od -An -N32 -tx1 /dev/urandom | tr -d ' \n'
  fi
}

install_docker

log "Preparing ${INSTALL_DIR}"
install -d -m 0750 "${INSTALL_DIR}"
curl -fsSL "${COMPOSE_URL}" -o "${INSTALL_DIR}/docker-compose.yml"

ENV_FILE="${INSTALL_DIR}/.env"
if [ ! -f "${ENV_FILE}" ]; then
  umask 077
  cat > "${ENV_FILE}" <<EOF
MATEMCP_RELAY_IMAGE=${MATEMCP_RELAY_IMAGE:-vrassouli/matemcp-relay:dev}
MATEMCP_RELAY_BIND=${MATEMCP_RELAY_BIND:-0.0.0.0}
MATEMCP_RELAY_PORT=${MATEMCP_RELAY_PORT:-8080}
MATEMCP_RELAY_AGENT_TOKEN=$(generate_token)
MATEMCP_RELAY_CLIENT_TOKEN=$(generate_token)
MATEMCP_RELAY_MAX_BODY_BYTES=4194304
MATEMCP_RELAY_REQUEST_TIMEOUT_SECONDS=120
EOF
  chmod 600 "${ENV_FILE}"
  CREATED_ENV=1
else
  CREATED_ENV=0
fi

log "Pulling and starting MateMCP Relay"
cd "${INSTALL_DIR}"
docker compose pull
docker compose up -d

log "Waiting for health endpoint"
for _ in $(seq 1 30); do
  if curl -fsS "http://127.0.0.1:$(grep '^MATEMCP_RELAY_PORT=' "${ENV_FILE}" | cut -d= -f2)/health" >/dev/null 2>&1; then
    echo "MateMCP Relay is healthy."
    break
  fi
  sleep 1
done

if ! curl -fsS "http://127.0.0.1:$(grep '^MATEMCP_RELAY_PORT=' "${ENV_FILE}" | cut -d= -f2)/health" >/dev/null 2>&1; then
  echo "Relay did not become healthy. Recent logs:" >&2
  docker compose logs --tail=100 relay >&2 || true
  exit 1
fi

PORT="$(grep '^MATEMCP_RELAY_PORT=' "${ENV_FILE}" | cut -d= -f2)"
echo
echo "Installed in: ${INSTALL_DIR}"
echo "Local health: http://127.0.0.1:${PORT}/health"
echo "Compose file: ${INSTALL_DIR}/docker-compose.yml"
echo "Environment: ${ENV_FILE}"

if [ "${CREATED_ENV}" -eq 1 ]; then
  echo
echo "Generated credentials (save these securely):"
  grep '^MATEMCP_RELAY_AGENT_TOKEN=' "${ENV_FILE}"
  grep '^MATEMCP_RELAY_CLIENT_TOKEN=' "${ENV_FILE}"
fi

echo
echo "Update later with:"
echo "  cd ${INSTALL_DIR} && docker compose pull && docker compose up -d"
