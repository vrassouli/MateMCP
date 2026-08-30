#!/usr/bin/env bash
set -euo pipefail

REPO="${MATEMCP_REPO:-vrassouli/MateMCP}"
RELEASE_TAG="${MATEMCP_RELEASE_TAG:-agent-dev}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This installer currently supports macOS only." >&2
  exit 1
fi

case "$(uname -m)" in
  arm64) RID="osx-arm64" ;;
  x86_64) RID="osx-x64" ;;
  *) echo "Unsupported Mac architecture: $(uname -m)" >&2; exit 1 ;;
esac

command -v curl >/dev/null 2>&1 || { echo "curl is required." >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

ARCHIVE_NAME="MateMCP-${RID}.tar.gz"
URL="https://github.com/${REPO}/releases/download/${RELEASE_TAG}/${ARCHIVE_NAME}"
ARCHIVE="$TMP/$ARCHIVE_NAME"

echo "Downloading ${ARCHIVE_NAME} from release ${RELEASE_TAG}..."
curl -fL "$URL" -o "$ARCHIVE"

mkdir -p "$TMP/package"
tar -xzf "$ARCHIVE" -C "$TMP/package"

if [[ ! -x "$TMP/package/install-macos.sh" ]]; then
  chmod +x "$TMP/package/install-macos.sh"
fi

"$TMP/package/install-macos.sh" "$TMP/package/payload"

echo
echo "MateMCP Agent installation complete."
echo "Run: $HOME/.local/bin/matemcp"
