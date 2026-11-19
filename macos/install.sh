#!/bin/bash
# Builds HW Widget, copies it to ~/Applications, turns on Open at Login and starts it.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
green=$'\e[32m'; red=$'\e[31m'; off=$'\e[0m'
"$here/build.sh"
"$here/build/HW Widget.app/Contents/MacOS/HWWidget" --self-test >/dev/null || { echo "${red}❌ self-test failed${off}"; exit 1; }
pkill -x HWWidget 2>/dev/null || true
mkdir -p "$HOME/Applications"
rm -rf "$HOME/Applications/HW Widget.app"
cp -R "$here/build/HW Widget.app" "$HOME/Applications/"
app="$HOME/Applications/HW Widget.app/Contents/MacOS/HWWidget"
plist="$HOME/Library/LaunchAgents/ai.solarity.hw-widget.plist"
mkdir -p "$(dirname "$plist")"
cat > "$plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>ai.solarity.hw-widget</string>
  <key>ProgramArguments</key><array><string>${app}</string></array>
  <key>RunAtLoad</key><true/>
  <key>ProcessType</key><string>Interactive</string>
</dict></plist>
PLIST
open "$HOME/Applications/HW Widget.app"
echo "${green}✅ HW Widget installed in ~/Applications and starts at login. Look for the pill in the menu bar.${off}"
