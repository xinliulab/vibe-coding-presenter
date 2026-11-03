#!/bin/bash
# Stop Vibe Coding Presenter and remove the app and its login item.
# Your config in ~/Library/Application Support/VibeCodingPresenter is kept.
ROOT="$(cd "$(dirname "$0")" && pwd)"
"$ROOT/stop.sh"
rm -f "$HOME/Library/LaunchAgents/com.xinliulab.vibecodingpresenter.plist"
rm -rf "$HOME/Applications/VibeCodingPresenter.app"
echo "Removed the app and the login item."
