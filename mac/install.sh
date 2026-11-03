#!/bin/bash
# Build Vibe Coding Presenter for macOS, install it to ~/Applications, and start it.
#
#   ./mac/install.sh              build, install, and start
#   ./mac/install.sh --detect     also wait for you to plug in a presenter and save its IDs
#                                 (rarely needed: presenters are detected automatically)
#   ./mac/install.sh --at-login   also start automatically every time you log in
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
APP_NAME="VibeCodingPresenter"
BUNDLE_ID="com.xinliulab.vibecodingpresenter"
BUILD="$ROOT/build"
APP="$BUILD/$APP_NAME.app"
DEST="$HOME/Applications/$APP_NAME.app"
SUPPORT="$HOME/Library/Application Support/$APP_NAME"
CONFIG="$SUPPORT/config.json"
AGENT="$HOME/Library/LaunchAgents/$BUNDLE_ID.plist"

AT_LOGIN=0
DETECT=0
for arg in "$@"; do
    case "$arg" in
        --at-login) AT_LOGIN=1 ;;
        --detect) DETECT=1 ;;
        *) echo "Unknown option: $arg" >&2; exit 1 ;;
    esac
done

# Stop any running copy before replacing it.
"$ROOT/stop.sh" >/dev/null 2>&1 || true

echo "Building $APP_NAME..."
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
swiftc -swift-version 5 -O -o "$APP/Contents/MacOS/$APP_NAME" "$ROOT/VibeCodingPresenter.swift"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>Vibe Coding Presenter</string>
    <key>CFBundleExecutable</key><string>$APP_NAME</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSUIElement</key><true/>
</dict>
</plist>
PLIST
codesign --force --sign - "$APP" >/dev/null 2>&1

mkdir -p "$HOME/Applications"
rm -rf "$DEST"
ditto "$APP" "$DEST"
echo "Installed: $DEST"

mkdir -p "$SUPPORT"
if [ ! -f "$CONFIG" ]; then
    cp "$ROOT/config.sample.json" "$CONFIG"
    echo "Created config: $CONFIG"
fi

if [ "$DETECT" -eq 1 ]; then
    "$DEST/Contents/MacOS/$APP_NAME" --detect 60
fi

if [ "$AT_LOGIN" -eq 1 ]; then
    mkdir -p "$(dirname "$AGENT")"
    cat > "$AGENT" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>$BUNDLE_ID</string>
    <key>ProgramArguments</key>
    <array><string>$DEST/Contents/MacOS/$APP_NAME</string></array>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
    <key>ProcessType</key><string>Interactive</string>
    <key>LimitLoadToSessionType</key><string>Aqua</string>
</dict>
</plist>
PLIST
    launchctl bootstrap "gui/$(id -u)" "$AGENT"
    echo "Starts at login: $AGENT"
else
    open "$DEST"
fi

cat <<EOF

Vibe Coding Presenter is running (microphone icon in the menu bar).
  Double Up   -> jump to Claude and start/stop dictation (Cmd+D)
  Double Down -> stop dictation if it is on, wait, then press Enter
Presenters are detected automatically; menu bar icon > Presenters shows and changes the choice.
First run: allow "$APP_NAME" in System Settings > Privacy & Security > Accessibility.
After every rebuild macOS may ask again, because the app is re-signed.
Config: $CONFIG
Log:    $HOME/Library/Logs/$APP_NAME.log
Stop:   $ROOT/stop.sh
EOF
