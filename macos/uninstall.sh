#!/bin/bash
# Stops HW Widget and removes the app, its login item and its settings.
# Unload the login agent first: otherwise launchd keeps the job loaded until the next logout.
launchctl bootout "gui/$(id -u)/ai.solarity.hw-widget" 2>/dev/null || true
pkill -x HWWidget 2>/dev/null || true
rm -rf "$HOME/Applications/HW Widget.app" "$HOME/Library/LaunchAgents/ai.solarity.hw-widget.plist" "$HOME/Library/Application Support/HW Widget"
echo $'\e[32m✅ HW Widget removed.\e[0m'
