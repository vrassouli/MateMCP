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

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

URL="https://github.com/${REPO}/releases/download/${RELEASE_TAG}/${ARCHIVE_NAME}"
ARCHIVE="$TMP/$ARCHIVE_NAME"

echo "Downloading ${ARCHIVE_NAME} from release ${RELEASE_TAG}..."
curl -fL "$URL" -o "$ARCHIVE"

mkdir -p "$TMP/package"
tar -xzf "$ARCHIVE" -C "$TMP/package"

if [[ "$DESKTOP" == true ]]; then
  # Use the explicit Desktop wrapper here so the bootstrap remains compatible
  # during release rollover with both old and new Desktop package layouts.
  INSTALLER="$TMP/package/install-desktop-macos.sh"
  [[ -f "$INSTALLER" ]] || { echo "Downloaded package does not contain install-desktop-macos.sh" >&2; exit 1; }
  chmod +x "$INSTALLER"
  "$INSTALLER"
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
