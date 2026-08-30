#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This helper is for macOS only." >&2
  exit 1
fi

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <public-ipv4> [mcp-port]" >&2
  echo "Example: $0 203.0.113.10 45871" >&2
  exit 1
fi

PUBLIC_IP="$1"
MCP_PORT="${2:-45871}"
CONFIG_DIR="$HOME/Library/Application Support/MateMCP"
CONFIG_PATH="$CONFIG_DIR/appsettings.json"
PFX_PATH="$CONFIG_DIR/external-test.pfx"
CERT_NAME="matemcp-ip"
LE_LIVE_DIR="/etc/letsencrypt/live/$CERT_NAME"

if ! [[ "$PUBLIC_IP" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
  echo "For the MVP external test, provide a public IPv4 address." >&2
  exit 1
fi

for part in ${PUBLIC_IP//./ }; do
  if (( part < 0 || part > 255 )); then
    echo "Invalid IPv4 address: $PUBLIC_IP" >&2
    exit 1
  fi
done

if ! [[ "$MCP_PORT" =~ ^[0-9]+$ ]] || (( MCP_PORT < 1 || MCP_PORT > 65535 )); then
  echo "Invalid MCP port: $MCP_PORT" >&2
  exit 1
fi

if [[ ! -f "$CONFIG_PATH" ]]; then
  echo "MateMCP configuration not found at: $CONFIG_PATH" >&2
  echo "Run matemcp once first so the configuration is created." >&2
  exit 1
fi

if ! command -v certbot >/dev/null 2>&1; then
  echo "Certbot is required. Install it with: brew install certbot" >&2
  exit 1
fi

if ! command -v openssl >/dev/null 2>&1; then
  echo "OpenSSL is required." >&2
  exit 1
fi

CERTBOT_VERSION="$(certbot --version 2>&1 | awk '{print $2}' || true)"
CERTBOT_MAJOR="${CERTBOT_VERSION%%.*}"
CERTBOT_MINOR="$(printf '%s' "$CERTBOT_VERSION" | cut -d. -f2)"
if [[ "$CERTBOT_MAJOR" =~ ^[0-9]+$ && "$CERTBOT_MINOR" =~ ^[0-9]+$ ]]; then
  if (( CERTBOT_MAJOR < 5 || (CERTBOT_MAJOR == 5 && CERTBOT_MINOR < 4) )); then
    echo "Certbot 5.4+ is required for IP certificate support. Found: $CERTBOT_VERSION" >&2
    exit 1
  fi
fi

mkdir -p "$CONFIG_DIR"
chmod 700 "$CONFIG_DIR" || true
BACKUP_PATH="$CONFIG_PATH.backup.$(date +%Y%m%d%H%M%S)"
cp "$CONFIG_PATH" "$BACKUP_PATH"
chmod 600 "$BACKUP_PATH" || true

echo
cat <<EOF
MateMCP secure external-test preparation
----------------------------------------
Public IP: $PUBLIC_IP
MCP port:  $MCP_PORT

Before continuing, configure router/NAT so TCP port 80 on the public IP reaches
TCP port 80 on this Mac. Let's Encrypt HTTP-01 validation requires public port 80.

After setup, forward public TCP port $MCP_PORT to this Mac's TCP port $MCP_PORT.
Approval/status endpoints remain loopback-only.
EOF

echo
read -r -p "Is public TCP port 80 currently forwarded to this Mac? [y/N] " answer
if [[ ! "$answer" =~ ^[Yy]$ ]]; then
  echo "No changes made. Configure the temporary port-80 forward and run this script again." >&2
  exit 2
fi

echo "Requesting a short-lived, publicly trusted Let's Encrypt certificate for $PUBLIC_IP ..."
sudo certbot certonly \
  --standalone \
  --preferred-profile shortlived \
  --ip-address "$PUBLIC_IP" \
  --cert-name "$CERT_NAME"

if [[ ! -f "$LE_LIVE_DIR/fullchain.pem" || ! -f "$LE_LIVE_DIR/privkey.pem" ]]; then
  echo "Certbot completed but the expected certificate files were not found in $LE_LIVE_DIR" >&2
  exit 1
fi

TOKEN="matemcp_$(openssl rand -hex 32)"
PFX_PASSWORD="$(openssl rand -hex 24)"
TMP_PFX="$(mktemp -t matemcp-external.XXXXXX.pfx)"
trap 'rm -f "$TMP_PFX"' EXIT

sudo openssl pkcs12 -export \
  -out "$TMP_PFX" \
  -inkey "$LE_LIVE_DIR/privkey.pem" \
  -in "$LE_LIVE_DIR/fullchain.pem" \
  -passout "pass:$PFX_PASSWORD"

sudo chown "$(id -u):$(id -g)" "$TMP_PFX"
mv "$TMP_PFX" "$PFX_PATH"
chmod 600 "$PFX_PATH"
trap - EXIT

MATEMCP_CONFIG_PATH="$CONFIG_PATH" \
MATEMCP_BIND_ADDRESS="0.0.0.0" \
MATEMCP_PORT="$MCP_PORT" \
MATEMCP_TOKEN="$TOKEN" \
MATEMCP_PFX_PATH="$PFX_PATH" \
MATEMCP_PFX_PASSWORD="$PFX_PASSWORD" \
python3 <<'PY'
import json
import os
from pathlib import Path

path = Path(os.environ["MATEMCP_CONFIG_PATH"])
data = json.loads(path.read_text())
mate = data.setdefault("Mate", {})
mate["BindAddress"] = os.environ["MATEMCP_BIND_ADDRESS"]
mate["Port"] = int(os.environ["MATEMCP_PORT"])
mate["AllowInsecureHttp"] = False
mate["AccessToken"] = os.environ["MATEMCP_TOKEN"]
mate["CertificatePath"] = os.environ["MATEMCP_PFX_PATH"]
mate["CertificatePassword"] = os.environ["MATEMCP_PFX_PASSWORD"]
mate["RequireShellApproval"] = True
path.write_text(json.dumps(data, indent=2) + "\n")
PY
chmod 600 "$CONFIG_PATH" || true

echo
echo "MateMCP external-test configuration is ready."
echo "Configuration backup: $BACKUP_PATH"
echo "HTTPS endpoint: https://$PUBLIC_IP:$MCP_PORT/mcp"
echo
echo "NEW ACCESS TOKEN (store it securely; the previous token was rotated):"
echo "$TOKEN"
echo
echo "Next steps:"
echo "  1. Remove the temporary public port-80 forward if you do not need it right now."
echo "  2. Forward public TCP $MCP_PORT -> this Mac TCP $MCP_PORT."
echo "  3. Restart MateMCP: matemcp"
echo "  4. From a different network, test: curl https://$PUBLIC_IP:$MCP_PORT/health"
echo
echo "Important: the Let's Encrypt IP certificate is short-lived (about 6 days)."
echo "For this MVP test, rerun certificate setup before expiry. Automated renewal is a follow-up task."
