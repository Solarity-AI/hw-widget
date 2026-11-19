#!/usr/bin/env sh
# Installs the HW Widget GNOME Shell extension (GNOME 45-49) for the current user and enables it.
set -eu
uuid=hwwidget@solarity.ai
here=$(cd "$(dirname "$0")" && pwd)
dest="${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/$uuid"
green=$(printf '\033[32m'); yellow=$(printf '\033[33m'); off=$(printf '\033[0m')

# GNOME Shell keeps running the code it loaded until the session restarts, so an update to a running
# extension only takes effect after a log-out.
running=no
if gnome-extensions info "$uuid" 2>/dev/null | grep -qiE 'state: *(enabled|active)'; then running=yes; fi

rm -rf "$dest"
mkdir -p "$dest"
cp "$here/gnome-extension/$uuid/"*.js "$here/gnome-extension/$uuid/metadata.json" "$here/gnome-extension/$uuid/stylesheet.css" "$dest/"
echo "${green}✅ Copied to $dest${off}"

if [ "$running" = yes ]; then
  gnome-extensions enable "$uuid" 2>/dev/null || true
  echo "${yellow}⚠ Updated. Log out and back in to load the new version.${off}"
elif gnome-extensions enable "$uuid" 2>/dev/null; then
  echo "${green}✅ Enabled. The pill is on the right of the top bar.${off}"
else
  # GNOME Shell only sees a new extension after it restarts: log out and back in on Wayland.
  echo "${yellow}⚠ Log out and back in, then run: gnome-extensions enable $uuid${off}"
fi
