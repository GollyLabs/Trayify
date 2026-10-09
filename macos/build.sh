#!/bin/bash
# Builds Trayify.app into macos/build/ from the Swift package. No Xcode project or third-party tools needed.
#   ./build.sh            release build, universal (arm64 + x86_64) when possible
#   ./build.sh --native   release build for this Mac's architecture only
#   ./build.sh --debug    debug build (native)
set -euo pipefail
export PATH="/usr/bin:/bin:/usr/sbin:/sbin:$PATH"
cd "$(dirname "$0")"

VERSION="${TRAYIFY_VERSION:-1.0.0}"
BUILD_NUMBER="${TRAYIFY_BUILD:-$(/usr/bin/git rev-list --count HEAD 2>/dev/null || echo 1)}"
BUNDLE_ID="com.gollylabs.trayify"
CONFIG=release
ARCH_ARGS=(--arch arm64 --arch x86_64)
for a in "$@"; do
  case "$a" in
    --native) ARCH_ARGS=() ;;
    --debug) CONFIG=debug; ARCH_ARGS=() ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done

echo "==> swift build -c $CONFIG ${ARCH_ARGS[*]:-}"
if ! /usr/bin/swift build -c "$CONFIG" ${ARCH_ARGS[@]+"${ARCH_ARGS[@]}"}; then
  if [ ${#ARCH_ARGS[@]} -gt 0 ]; then
    echo "==> universal build failed; falling back to native" >&2
    ARCH_ARGS=()
    /usr/bin/swift build -c "$CONFIG"
  else
    exit 1
  fi
fi
BIN_DIR="$(/usr/bin/swift build -c "$CONFIG" ${ARCH_ARGS[@]+"${ARCH_ARGS[@]}"} --show-bin-path)"

APP="build/Trayify.app"
echo "==> assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/Trayify" "$APP/Contents/MacOS/Trayify"
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD_NUMBER/" Resources/Info.plist > "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
printf 'APPL????' > "$APP/Contents/PkgInfo"

# Ad-hoc signature with a fixed identifier and an explicit designated requirement, so the
# Accessibility (TCC) grant has a stable identity to match. Ad-hoc grants may still need
# re-granting after a rebuild (see README).
echo "==> codesign (ad-hoc, identifier $BUNDLE_ID)"
/usr/bin/codesign --force --sign - --identifier "$BUNDLE_ID" --options runtime \
  -r="designated => identifier \"$BUNDLE_ID\"" "$APP"
/usr/bin/codesign --verify --strict --verbose=1 "$APP"
echo "==> architectures: $(/usr/bin/lipo -archs "$APP/Contents/MacOS/Trayify")"
echo "==> built $(pwd)/$APP"
