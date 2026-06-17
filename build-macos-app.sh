#!/usr/bin/env bash
#
# Build a self-contained osx-arm64 release of Usage Monitor and package it into
# a proper /Applications/UsageMonitor.app bundle — a dock-less (LSUIElement) tray
# agent. Mirrors the proven WhisperKeyboard.app layout: self-contained folder
# publish (not single-file), ad-hoc codesigned, quarantine cleared.
#
# Usage:
#   ./build-macos-app.sh              # build, install to /Applications, and launch
#   ./build-macos-app.sh --no-install # build into ./dist only, don't touch /Applications
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_ROOT"

DOTNET="/opt/homebrew/bin/dotnet"        # keg-only dotnet@8
PROJECT="src/UsageMonitor/UsageMonitor.csproj"
RID="osx-arm64"
APP_NAME="UsageMonitor"
BUNDLE_ID="com.usage-monitor"            # == LaunchAgent Label == plist filename stem
PUBLISH_DIR="$REPO_ROOT/tmp/publish-$RID"

INSTALL=1
APP_PARENT="/Applications"
for arg in "$@"; do
  case "$arg" in
    --no-install) INSTALL=0; APP_PARENT="$REPO_ROOT/dist" ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "error: unknown argument: $arg" >&2; exit 2 ;;
  esac
done
APP="$APP_PARENT/$APP_NAME.app"

if [[ ! -x "$DOTNET" ]]; then
  DOTNET="$(command -v dotnet || true)"
  [[ -n "$DOTNET" ]] || { echo "error: dotnet not found (expected keg-only /opt/homebrew/bin/dotnet)" >&2; exit 1; }
fi

echo "==> Publishing self-contained $RID build (this restores the runtime pack on first run)..."
rm -rf "$PUBLISH_DIR"
"$DOTNET" publish "$PROJECT" \
  -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -p:PublishSingleFile=false \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$PUBLISH_DIR"

# Use sudo only for the steps that touch a non-writable /Applications.
SUDO=""
if [[ $INSTALL -eq 1 && ! -w "$APP_PARENT" ]]; then
  echo "    $APP_PARENT is not writable; using sudo for the install steps."
  SUDO="sudo"
fi

echo "==> Assembling $APP ..."
$SUDO mkdir -p "$APP_PARENT"
$SUDO rm -rf "$APP"
$SUDO mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
$SUDO cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"
$SUDO chmod +x "$APP/Contents/MacOS/$APP_NAME"

echo "==> Generating icon (AppIcon.icns) from Assets/usage-monitor.png ..."
SRC_PNG="$REPO_ROOT/src/UsageMonitor/Assets/usage-monitor.png"
if [[ -f "$SRC_PNG" ]] && command -v sips >/dev/null && command -v iconutil >/dev/null; then
  WORK="$(mktemp -d)"
  ICONSET="$WORK/AppIcon.iconset"
  mkdir -p "$ICONSET"
  for sz in 16 32 128 256 512; do
    sips -z "$sz" "$sz" "$SRC_PNG" --out "$ICONSET/icon_${sz}x${sz}.png"     >/dev/null 2>&1 || true
    d=$((sz * 2))
    sips -z "$d" "$d"   "$SRC_PNG" --out "$ICONSET/icon_${sz}x${sz}@2x.png"  >/dev/null 2>&1 || true
  done
  if iconutil -c icns "$ICONSET" -o "$WORK/AppIcon.icns" 2>/dev/null; then
    $SUDO cp "$WORK/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
  else
    echo "    (iconutil failed — continuing without a custom icon)"
  fi
  rm -rf "$WORK"
else
  echo "    (sips/iconutil or source PNG unavailable — continuing without a custom icon)"
fi

echo "==> Writing Info.plist ..."
GIT_SHA="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
$SUDO tee "$APP/Contents/Info.plist" >/dev/null <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>                  <string>Usage Monitor</string>
    <key>CFBundleDisplayName</key>           <string>Usage Monitor</string>
    <key>CFBundleIdentifier</key>            <string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key>            <string>$APP_NAME</string>
    <key>CFBundlePackageType</key>           <string>APPL</string>
    <key>CFBundleIconFile</key>              <string>AppIcon.icns</string>
    <key>CFBundleVersion</key>               <string>1.0.0</string>
    <key>CFBundleShortVersionString</key>    <string>1.0.0 ($GIT_SHA)</string>
    <key>CFBundleInfoDictionaryVersion</key> <string>6.0</string>
    <key>LSUIElement</key>                   <true/>
    <key>NSHighResolutionCapable</key>       <true/>
    <key>LSMinimumSystemVersion</key>        <string>11.0</string>
</dict>
</plist>
PLIST

# Clear quarantine THEN ad-hoc sign — signing must be last, since any edit inside
# the bundle invalidates the signature. An ad-hoc signature is required for an
# arm64 binary to exec at all on Apple Silicon.
echo "==> Clearing quarantine + ad-hoc signing ..."
$SUDO xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
$SUDO codesign --force --deep --sign - "$APP"

echo "==> Done: $APP"
if [[ $INSTALL -eq 1 ]]; then
  echo "==> Stopping any running instance ..."
  # If launch-at-login is enabled, launchd would relaunch a plain-killed process,
  # so bootout the LaunchAgent first (best-effort), then kill any stragglers —
  # otherwise the old binary stays resident and 'open' just reactivates it.
  launchctl bootout "gui/$(id -u)/$BUNDLE_ID" 2>/dev/null || true
  pkill -x "$APP_NAME" 2>/dev/null || true
  sleep 1

  echo "==> Launching ..."
  open -n "$APP"   # -n forces a fresh instance rather than reactivating a stale one
  sleep 2
  NEW_PID="$(pgrep -x "$APP_NAME" | head -n1 || true)"
  if [[ -n "$NEW_PID" ]]; then
    echo "PASS: $APP_NAME is running (pid $NEW_PID) — look for its icon in the menu bar (no Dock tile by design)."
  else
    echo "WARN: could not confirm $APP_NAME is running; check ~/Library/Application Support/UsageMonitor/usage.log"
  fi
else
  echo "Built (not installed). To run: open \"$APP\""
fi
