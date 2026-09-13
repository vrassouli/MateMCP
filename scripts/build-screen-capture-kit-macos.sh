#!/usr/bin/env bash
set -euo pipefail
OUT="${1:?output path is required}"
RID="${2:-osx-arm64}"
case "$RID" in
  osx-arm64|arm64) TARGET="arm64-apple-macos13.0" ;;
  osx-x64|x86_64) TARGET="x86_64-apple-macos13.0" ;;
  *) echo "Unsupported macOS RID/arch: $RID" >&2; exit 2 ;;
esac
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/src/MateMCP.Agent/Native/macos/MateMCP.ScreenCaptureKitHelper.swift"
mkdir -p "$(dirname "$OUT")"
swiftc -swift-version 5 -parse-as-library -O -target "$TARGET" "$SRC" -o "$OUT" \
  -framework CoreGraphics -framework ScreenCaptureKit -framework CoreMedia \
  -framework VideoToolbox -framework ImageIO -framework UniformTypeIdentifiers
chmod +x "$OUT"
