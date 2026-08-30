#!/usr/bin/env bash
set -euo pipefail

REPO="${MATEMCP_REPO:-vrassouli/MateMCP}"
BRANCH="${MATEMCP_BRANCH:-feat/relay-mvp}"
WORKFLOW="${MATEMCP_WORKFLOW:-build.yml}"

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
command -v python3 >/dev/null 2>&1 || { echo "python3 is required." >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

API="https://api.github.com/repos/${REPO}"
echo "Finding latest successful MateMCP build for ${RID}..."

RUNS_JSON="$TMP/runs.json"
curl -fsSL -H "Accept: application/vnd.github+json" \
  "${API}/actions/workflows/${WORKFLOW}/runs?branch=${BRANCH}&status=success&event=push&per_page=20" \
  -o "$RUNS_JSON"

RUN_ID="$(python3 - "$RUNS_JSON" <<'PY'
import json, sys
with open(sys.argv[1]) as f:
    data = json.load(f)
for run in data.get("workflow_runs", []):
    if run.get("conclusion") == "success":
        print(run["id"])
        break
PY
)"

if [[ -z "$RUN_ID" ]]; then
  echo "No successful ${WORKFLOW} push build found for branch ${BRANCH}." >&2
  exit 1
fi

ARTIFACTS_JSON="$TMP/artifacts.json"
curl -fsSL -H "Accept: application/vnd.github+json" \
  "${API}/actions/runs/${RUN_ID}/artifacts?per_page=100" \
  -o "$ARTIFACTS_JSON"

read -r ARTIFACT_ID ARTIFACT_NAME < <(python3 - "$ARTIFACTS_JSON" "$RID" <<'PY'
import json, sys
with open(sys.argv[1]) as f:
    data = json.load(f)
name = "MateMCP-" + sys.argv[2]
for artifact in data.get("artifacts", []):
    if artifact.get("name") == name and not artifact.get("expired", False):
        print(artifact["id"], artifact["name"])
        break
PY
)

if [[ -z "${ARTIFACT_ID:-}" ]]; then
  echo "Artifact MateMCP-${RID} was not found in build ${RUN_ID}." >&2
  exit 1
fi

ZIP="$TMP/artifact.zip"
echo "Downloading ${ARTIFACT_NAME} from build ${RUN_ID}..."
if ! curl -fsSL -H "Accept: application/vnd.github+json" \
  "${API}/actions/artifacts/${ARTIFACT_ID}/zip" -o "$ZIP"; then
  echo "GitHub does not allow anonymous artifact download for this build." >&2
  echo "Publish MateMCP archives as GitHub Release assets, then point this bootstrap at the release asset." >&2
  exit 1
fi

mkdir -p "$TMP/artifact"
ditto -x -k "$ZIP" "$TMP/artifact"
ARCHIVE="$TMP/artifact/MateMCP-${RID}.tar.gz"
if [[ ! -f "$ARCHIVE" ]]; then
  ARCHIVE="$(find "$TMP/artifact" -maxdepth 2 -name 'MateMCP-*.tar.gz' -print -quit)"
fi
if [[ -z "$ARCHIVE" || ! -f "$ARCHIVE" ]]; then
  echo "MateMCP archive was not found inside the downloaded artifact." >&2
  exit 1
fi

mkdir -p "$TMP/package"
tar -xzf "$ARCHIVE" -C "$TMP/package"
chmod +x "$TMP/package/install-macos.sh"
"$TMP/package/install-macos.sh" "$TMP/package/payload"

echo
echo "MateMCP Agent installation complete."
echo "Run: $HOME/.local/bin/matemcp"
