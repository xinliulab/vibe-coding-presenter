#!/bin/bash
# Stop Vibe Coding Presenter. The app restores the presenter's normal keys on exit.
launchctl bootout "gui/$(id -u)/com.xinliulab.vibecodingpresenter" 2>/dev/null || true
pkill -TERM -x VibeCodingPresenter 2>/dev/null || true
echo "Vibe Coding Presenter stopped."
