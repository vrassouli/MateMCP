#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <archive.tar.gz> <agent|desktop>" >&2
  exit 2
fi

archive="$1"
kind="$2"

case "$kind" in
  agent|desktop) ;;
  *) echo "Unsupported package kind: $kind" >&2; exit 2 ;;
esac

: "${MATEMCP_MACOS_P12_BASE64:?MATEMCP_MACOS_P12_BASE64 is required}"
: "${MATEMCP_MACOS_P12_PASSWORD:?MATEMCP_MACOS_P12_PASSWORD is required}"
: "${MATEMCP_APPLE_ID:?MATEMCP_APPLE_ID is required}"
: "${MATEMCP_APPLE_APP_PASSWORD:?MATEMCP_APPLE_APP_PASSWORD is required}"
: "${MATEMCP_APPLE_TEAM_ID:?MATEMCP_APPLE_TEAM_ID is required}"

work="$(mktemp -d "${TMPDIR:-/tmp}/matemcp-sign.XXXXXX")"
keychain="$work/matemcp-signing.keychain-db"
keychain_password="$(openssl rand -hex 24)"
p12="$work/developer-id.p12"
package="$work/package"
notary_zip="$work/notarize.zip"

cleanup() {
  security delete-keychain "$keychain" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

mkdir -p "$package"
tar -xzf "$archive" -C "$package"
printf '%s' "$MATEMCP_MACOS_P12_BASE64" | base64 --decode > "$p12"

security create-keychain -p "$keychain_password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$keychain_password" "$keychain"
security import "$p12" -k "$keychain" -P "$MATEMCP_MACOS_P12_PASSWORD" -T /usr/bin/codesign -T /usr/bin/security
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$keychain_password" "$keychain"

identity="$(security find-identity -v -p codesigning "$keychain" | awk '/Developer ID Application/ {print $2; exit}')"
if [[ -z "$identity" ]]; then
  echo "No Developer ID Application identity was found in the supplied certificate." >&2
  exit 1
fi

sign_binary() {
  local path="$1"
  local identifier="$2"
  [[ -f "$path" ]] || return 0
  codesign --force --options runtime --timestamp --identifier "$identifier" --keychain "$keychain" --sign "$identity" "$path"
  codesign --verify --strict --verbose=2 "$path"
  codesign -dv --verbose=4 "$path" 2>&1 | grep -Fq "Identifier=$identifier"
  if codesign -dv --verbose=4 "$path" 2>&1 | grep -Fq 'TeamIdentifier=not set'; then
    echo "Developer ID signed artifact unexpectedly has no TeamIdentifier: $path" >&2
    exit 1
  fi
}

sign_binary "$package/payload/MateMCP.Agent" "com.matemcp.agent"
sign_binary "$package/payload/MateMCP.ScreenCaptureKitHelper" "com.matemcp.agent.screencapturekit"
sign_binary "$package/agent-payload/MateMCP.Agent" "com.matemcp.agent"
sign_binary "$package/agent-payload/MateMCP.ScreenCaptureKitHelper" "com.matemcp.agent.screencapturekit"

app=""
if [[ "$kind" == "desktop" ]]; then
  app="$(find "$package/companion-payload" -maxdepth 1 -type d -name '*.app' -print -quit)"
  if [[ -z "$app" ]]; then
    echo "Companion app bundle was not found in Desktop package." >&2
    exit 1
  fi
  codesign --force --deep --options runtime --timestamp --keychain "$keychain" --sign "$identity" "$app"
  codesign --verify --deep --strict --verbose=2 "$app"
  bundle_id="$(defaults read "$app/Contents/Info" CFBundleIdentifier)"
  if [[ "$bundle_id" != "com.matemcp.agent.companion" ]]; then
    echo "Unexpected Companion bundle identifier: $bundle_id" >&2
    exit 1
  fi
fi

# Notarize all signed code as a single ZIP. Apple can issue tickets for the
# nested signed executables; the Companion app is stapled when present.
ditto -c -k --sequesterRsrc --keepParent "$package" "$notary_zip"
xcrun notarytool submit "$notary_zip" \
  --apple-id "$MATEMCP_APPLE_ID" \
  --password "$MATEMCP_APPLE_APP_PASSWORD" \
  --team-id "$MATEMCP_APPLE_TEAM_ID" \
  --wait

if [[ -n "$app" ]]; then
  xcrun stapler staple "$app"
  xcrun stapler validate "$app"
fi

rm -f "$archive"
tar -czf "$archive" -C "$package" .

echo "Signed and notarized $archive ($kind)."
