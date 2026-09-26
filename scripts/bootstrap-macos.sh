#!/usr/bin/env bash
set -euo pipefail

REPO="${MATEMCP_REPO:-vrassouli/MateMCP}"
RELEASE_TAG="${MATEMCP_DESKTOP_RELEASE_TAG:-${MATEMCP_AGENT_RELEASE_TAG:-${MATEMCP_RELEASE_TAG:-agent-latest}}}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This installer currently supports macOS only." >&2
  exit 1
fi

case "$(uname -m)" in
  arm64)
    RID="osx-arm64"
    DESKTOP=true
    ARCHIVE_NAME="MateMCP-Desktop-macos-arm64.tar.gz"
    ;;
  x86_64)
    RID="osx-x64"
    DESKTOP=false
    ARCHIVE_NAME="MateMCP-${RID}.tar.gz"
    echo "Note: the native MateMCP Companion is not published for Intel Macs yet; installing the Agent-only package." >&2
    ;;
  *) echo "Unsupported Mac architecture: $(uname -m)" >&2; exit 1 ;;
esac

command -v curl >/dev/null 2>&1 || { echo "curl is required." >&2; exit 1; }
command -v plutil >/dev/null 2>&1 || { echo "plutil is required." >&2; exit 1; }
command -v shasum >/dev/null 2>&1 || { echo "shasum is required." >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# GitHub API calls are normally anonymous for public releases, but shared CI runner IPs
# can exhaust the unauthenticated rate limit. When GitHub provides a token, keep it
# out of process arguments by passing the Authorization header through a private curl config.
GITHUB_AUTH_TOKEN="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
CURL_AUTH_ARGS=()
if [[ -n "$GITHUB_AUTH_TOKEN" ]]; then
  CURL_AUTH_CONFIG="$TMP/curl-auth.conf"
  printf 'header = "Authorization: Bearer %s"\n' "$GITHUB_AUTH_TOKEN" > "$CURL_AUTH_CONFIG"
  chmod 600 "$CURL_AUTH_CONFIG"
  CURL_AUTH_ARGS=(--config "$CURL_AUTH_CONFIG")
fi

RELEASE_API="https://api.github.com/repos/${REPO}/releases/tags/${RELEASE_TAG}"
RELEASE_JSON="$TMP/release.json"
ARCHIVE="$TMP/$ARCHIVE_NAME"

echo "Resolving ${ARCHIVE_NAME} from release ${RELEASE_TAG}..."
curl -fsSL \
  "${CURL_AUTH_ARGS[@]}" \
  -H "Accept: application/vnd.github+json" \
  -H "User-Agent: MateMCP-Bootstrap/1.0" \
  "$RELEASE_API" -o "$RELEASE_JSON"

ASSET_COUNT="$(plutil -extract assets raw "$RELEASE_JSON")"
ASSET_ID=""
ASSET_URL=""
ASSET_DIGEST=""
for ((i = 0; i < ASSET_COUNT; i++)); do
  name="$(plutil -extract "assets.${i}.name" raw "$RELEASE_JSON")"
  if [[ "$name" == "$ARCHIVE_NAME" ]]; then
    ASSET_ID="$(plutil -extract "assets.${i}.id" raw "$RELEASE_JSON")"
    ASSET_URL="$(plutil -extract "assets.${i}.browser_download_url" raw "$RELEASE_JSON")"
    ASSET_DIGEST="$(plutil -extract "assets.${i}.digest" raw "$RELEASE_JSON" 2>/dev/null || true)"
    break
  fi
done

[[ -n "$ASSET_ID" && -n "$ASSET_URL" ]] || { echo "Release asset ${ARCHIVE_NAME} was not found in ${RELEASE_TAG}." >&2; exit 1; }

echo "Downloading ${ARCHIVE_NAME} from release ${RELEASE_TAG}..."
curl -fL "${CURL_AUTH_ARGS[@]}" -H "User-Agent: MateMCP-Bootstrap/1.0" "$ASSET_URL" -o "$ARCHIVE"

if [[ "$ASSET_DIGEST" == sha256:* ]]; then
  EXPECTED_SHA256="${ASSET_DIGEST#sha256:}"
  ACTUAL_SHA256="$(shasum -a 256 "$ARCHIVE" | awk '{print $1}')"
  [[ "$ACTUAL_SHA256" == "$EXPECTED_SHA256" ]] || { echo "Downloaded package SHA-256 verification failed." >&2; exit 1; }
fi

mkdir -p "$TMP/package"
tar -xzf "$ARCHIVE" -C "$TMP/package"

if [[ "$DESKTOP" == true ]]; then
  # Use the explicit Desktop wrapper here so the bootstrap remains compatible
  # during release rollover with both old and new Desktop package layouts.
  INSTALLER="$TMP/package/install-desktop-macos.sh"
  [[ -f "$INSTALLER" ]] || { echo "Downloaded package does not contain install-desktop-macos.sh" >&2; exit 1; }
  chmod +x "$INSTALLER"
  "$INSTALLER"

  # The Companion update checker compares the installed Desktop package with the
  # current GitHub release asset id. Bootstrap knows exactly which release asset
  # it installed, so establish that baseline immediately and avoid a false
  # "update available" prompt on first launch.
  DESKTOP_STATE_DIR="$HOME/Library/Application Support/MateMCP Companion"
  mkdir -p "$DESKTOP_STATE_DIR"
  printf '%s' "$ASSET_ID" > "$DESKTOP_STATE_DIR/.desktop-release-asset"
  chmod 600 "$DESKTOP_STATE_DIR/.desktop-release-asset" 2>/dev/null || true

  echo
  echo "MateMCP Desktop installation complete."
  echo "The Agent and native Companion are running and will start automatically when you sign in."
else
  INSTALLER="$TMP/package/install-macos.sh"
  [[ -f "$INSTALLER" ]] || { echo "Downloaded package does not contain install-macos.sh" >&2; exit 1; }
  chmod +x "$INSTALLER"
  "$INSTALLER" "$TMP/package/payload"
  echo
  echo "MateMCP Agent installation complete."
  echo "The Agent is running and will start automatically when you sign in."
  echo "Management UI: http://127.0.0.1:45871/ui"
fi
