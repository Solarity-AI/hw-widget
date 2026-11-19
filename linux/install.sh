#!/usr/bin/env sh
# Installs the HW Widget GNOME Shell extension for the current user and enables it.
# GNOME 45-49 get the ES-module source as is; GNOME 42-44 get a copy converted to legacy modules.
set -eu
uuid=hwwidget@solarity.ai
here=$(cd "$(dirname "$0")" && pwd)
src="$here/gnome-extension/$uuid"
dest="${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/$uuid"
green=$(printf '\033[32m'); yellow=$(printf '\033[33m'); red=$(printf '\033[31m'); off=$(printf '\033[0m')

major=$(gnome-shell --version 2>/dev/null | sed -n 's/^GNOME Shell \([0-9][0-9]*\).*/\1/p')
if [ -n "$major" ] && [ "$major" -lt 42 ]; then
  echo "${red}❌ GNOME Shell $major is too old: HW Widget needs GNOME 42 or later.${off}"; exit 1
fi

# GNOME Shell keeps running the code it loaded until the session restarts, so an update to a running
# extension only takes effect after a log-out.
running=no
if gnome-extensions info "$uuid" 2>/dev/null | grep -qiE 'state: *(enabled|active)'; then running=yes; fi

rm -rf "$dest"
mkdir -p "$dest"
if [ -n "$major" ] && [ "$major" -lt 45 ]; then
  gjs -m "$here/legacy/convert.js" "$src" "$dest"
  echo "${green}✅ Converted for GNOME $major and copied to $dest${off}"
else
  cp "$src/"*.js "$src/metadata.json" "$src/stylesheet.css" "$dest/"
  echo "${green}✅ Copied to $dest${off}"
fi

if [ "$running" = yes ]; then
  gnome-extensions enable "$uuid" 2>/dev/null || true
  echo "${yellow}⚠ Updated. Log out and back in to load the new version.${off}"
elif gnome-extensions enable "$uuid" 2>/dev/null; then
  echo "${green}✅ Enabled. The pill is on the right of the top bar.${off}"
else
  # GNOME Shell only sees a new extension after it restarts. Add it to the enabled list now so it
  # starts by itself after the next log-in.
  list=$(gsettings get org.gnome.shell enabled-extensions 2>/dev/null || echo "@as []")
  case "$list" in
    *"'$uuid'"*) ;;
    *"[]"*) gsettings set org.gnome.shell enabled-extensions "['$uuid']" 2>/dev/null || true ;;
    *) gsettings set org.gnome.shell enabled-extensions "$(printf '%s' "$list" | sed "s/]\$/, '$uuid']/")" 2>/dev/null || true ;;
  esac
  echo "${yellow}⚠ Log out and back in once: the pill then starts by itself.${off}"
fi
