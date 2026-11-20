#!/bin/bash
# Builds "HW Widget.app" into macos/build/ with swiftc (Command Line Tools are enough, Xcode not needed)
# and signs it ad hoc. Runs on macOS 13 or later, Apple Silicon or Intel.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/build/HW Widget.app"
version="$(sed -n 's/^let appVersion = "\(.*\)"$/\1/p' "$here/Sources/Core.swift")"
green=$'\e[32m'; red=$'\e[31m'; dim=$'\e[2m'; off=$'\e[0m'
command -v swiftc >/dev/null || { echo "${red}❌ swiftc not found. Run: xcode-select --install${off}"; exit 1; }

rm -rf "$out"
mkdir -p "$out/Contents/MacOS" "$out/Contents/Resources"
echo "${dim}Compiling ${version}…${off}"
swiftc -O -swift-version 5 -target "$(uname -m)-apple-macos13.0" -framework IOKit \
  -o "$out/Contents/MacOS/HWWidget" "$here"/Sources/*.swift

cat > "$out/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>ai.solarity.hw-widget</string>
  <key>CFBundleName</key><string>HW Widget</string>
  <key>CFBundleDisplayName</key><string>HW Widget</string>
  <key>CFBundleExecutable</key><string>HWWidget</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${version}</string>
  <key>CFBundleVersion</key><string>${version}</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

if ! sign_err="$(codesign --force --sign - "$out" 2>&1)"; then
  echo "${red}❌ Signing failed: ${sign_err}${off}"; exit 1
fi
codesign --verify "$out" && echo "${green}✅ Built $out${off}"
