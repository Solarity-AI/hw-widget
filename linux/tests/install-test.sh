#!/usr/bin/env sh
# Runs linux/install.sh against fake gnome-shell, gnome-extensions and gsettings in a temporary home.
set -u
here=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d); fails=0
mkdir -p "$tmp/bin"
check() { if printf '%s' "$2" | grep -q "$3"; then echo "PASS $1"; else echo "FAIL $1"; fails=$((fails + 1)); fi; }
fake() {   # fake <state> <enable exit code> <gnome-shell version>
  printf '#!/bin/sh\ncase "$1" in info) echo "  State: %s";; enable) exit %s;; esac\n' "$1" "$2" > "$tmp/bin/gnome-extensions"
  printf '#!/bin/sh\necho "GNOME Shell %s"\n' "$3" > "$tmp/bin/gnome-shell"
  printf '#!/bin/sh\nf=%s/gsettings.txt\ncase "$1" in get) cat "$f" 2>/dev/null || echo "@as []";; set) echo "$4" > "$f";; esac\n' "$tmp" > "$tmp/bin/gsettings"
  chmod +x "$tmp/bin/"*
}
run() { HOME=$tmp XDG_DATA_HOME=$tmp/share PATH="$tmp/bin:$PATH" sh "$here/install.sh" 2>&1; }
ext=$tmp/share/gnome-shell/extensions/hwwidget@solarity.ai

fake INITIALIZED 0 46.2
out=$(run)
check "first install says Enabled" "$out" "Enabled"
check "GNOME 46 gets the ES-module source" "$(cat "$ext/extension.js")" "^import GLib from"
fake ACTIVE 0 46.2
out=$(run)
check "reinstall while active asks for a log-out" "$out" "Log out and back in to load the new version"

# #110: GNOME 42-44 load only legacy modules.
rm -rf "$tmp/share"
fake "" 1 42.9
out=$(run)
check "GNOME 42 converts the extension" "$out" "Converted for GNOME 42"
check "GNOME 42 copy defines init()" "$(cat "$ext/extension.js")" "^function init()"
check "GNOME 42 copy has no import lines" "$(grep -c '^import ' "$ext/extension.js")" "^0$"
check "a new extension is added to the enabled list" "$(cat "$tmp/gsettings.txt")" "\['hwwidget@solarity.ai'\]"
echo "['ding@rastersoft.com']" > "$tmp/gsettings.txt"; rm -rf "$tmp/share"
out=$(run)
check "the enabled list keeps other extensions" "$(cat "$tmp/gsettings.txt")" "\['ding@rastersoft.com', 'hwwidget@solarity.ai'\]"
check "it says to log out once" "$out" "Log out and back in once"
fake "" 1 41.0
out=$(run)
check "GNOME 41 is refused" "$out" "too old"

rm -rf "$tmp"
echo "$fails failed"
[ "$fails" -eq 0 ]
