#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INSTALLER="$ROOT/deploy/api/install.sh"

FUNCTIONS="$(awk '/^build_api_image_from_source\(\)/,/^install_docker$/ { if ($0 == "install_docker") exit; print }' "$INSTALLER")"
[[ "$FUNCTIONS" == *"acquire_api_image()"* ]] || { echo "Could not extract API image acquisition functions." >&2; exit 1; }
eval "$FUNCTIONS"

REF="test-ref"
failure_tmp="$(mktemp -d)"
mktemp() { printf "%s\n" "$failure_tmp"; }
curl() { return 23; }
if build_api_image_from_source "vrassouli/matemcp-api:test"; then
  echo "Source-build fallback unexpectedly succeeded after a download failure." >&2
  exit 1
else
  status=$?
fi
[[ "$status" -eq 23 ]] || { echo "Expected source-build failure 23, got $status." >&2; exit 1; }
[[ ! -d "$failure_tmp" ]] || { echo "Fallback temp directory was not cleaned." >&2; exit 1; }
unset -f mktemp curl

DOCKER_IMAGE='vrassouli/matemcp-api:latest'
DOCKER_PULL_STATUS=0
BUILD_CALLS=0
BUILT_IMAGE=''

docker() {
  if [[ "$*" == "compose config --images" ]]; then
    printf "%s\n" "$DOCKER_IMAGE"
    return 0
  fi
  if [[ "$*" == "compose pull" ]]; then
    return "$DOCKER_PULL_STATUS"
  fi
  echo "Unexpected docker invocation in test: $*" >&2
  return 99
}

build_api_image_from_source() {
  BUILT_IMAGE="$1"
  BUILD_CALLS=$((BUILD_CALLS + 1))
  return 0
}

DOCKER_PULL_STATUS=0
BUILD_CALLS=0
BUILT_IMAGE=''
acquire_api_image
[[ "$BUILD_CALLS" -eq 0 ]] || { echo "Fallback ran after a successful registry pull." >&2; exit 1; }

DOCKER_PULL_STATUS=42
BUILD_CALLS=0
BUILT_IMAGE=''
acquire_api_image
[[ "$BUILD_CALLS" -eq 1 ]] || { echo "Fallback did not run exactly once." >&2; exit 1; }
[[ "$BUILT_IMAGE" == "$DOCKER_IMAGE" ]] || { echo "Fallback built the wrong image tag." >&2; exit 1; }

DOCKER_IMAGE='registry.example.test/custom/api:prod'
DOCKER_PULL_STATUS=42
BUILD_CALLS=0
BUILT_IMAGE=''
if acquire_api_image; then
  echo "Custom image unexpectedly used the official source-build fallback." >&2
  exit 1
fi
[[ "$BUILD_CALLS" -eq 0 ]] || { echo "Fallback ran for a custom image." >&2; exit 1; }

echo "API image acquisition fallback tests passed."
