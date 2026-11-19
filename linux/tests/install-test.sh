#!/usr/bin/env sh
# Runs linux/install.sh against a fake gnome-extensions and a temporary home; prints PASS/FAIL lines.
set -u
here=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d); fails=0
mkdir -p "$tmp/bin"
check() { if printf '%s' "$2" | grep -q "$3"; then echo "PASS $1"; else echo "FAIL $1"; fails=$((fails + 1)); fi; }
fake() { printf '#!/bin/sh\ncase "$1" in info) echo "  State: %s";; enable) exit 0;; esac\n' "$1" > "$tmp/bin/gnome-extensions"; chmod +x "$tmp/bin/gnome-extensions"; }
fake INITIALIZED
out=$(HOME=$tmp XDG_DATA_HOME=$tmp/share PATH="$tmp/bin:$PATH" sh "$here/install.sh" 2>&1)
check "first install says Enabled" "$out" "Enabled"
fake ACTIVE
out=$(HOME=$tmp XDG_DATA_HOME=$tmp/share PATH="$tmp/bin:$PATH" sh "$here/install.sh" 2>&1)
check "reinstall while active asks for a log-out" "$out" "Log out and back in to load the new version"
rm -rf "$tmp"
echo "$fails failed"
[ "$fails" -eq 0 ]
