#!/bin/bash
# Regenerates Resources/AppIcon.icns from the Windows app's PNG (windows/src/Trayify/Assets/Trayify.png).
# Only needed when the artwork changes; the .icns is committed.
set -euo pipefail
export PATH="/usr/bin:/bin:/usr/sbin:/sbin:$PATH"
cd "$(dirname "$0")/.."
SRC="../windows/src/Trayify/Assets/Trayify.png"
OUT="Resources/AppIcon.icns"
TMP="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$TMP"
for s in 16 32 128 256 512; do
  sips -z $s $s "$SRC" --out "$TMP/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2))
  sips -z $d $d "$SRC" --out "$TMP/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$TMP" -o "$OUT"
echo "wrote $OUT"
