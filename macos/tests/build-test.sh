#!/usr/bin/env sh
# Runs macos/build.sh with fake uname, sysctl, swiftc and codesign and checks the -target it compiles for.
# Nothing is compiled or signed, so it also runs in Git Bash or on Linux: sh macos/tests/build-test.sh
set -u
here=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d); fails=0
trap 'rm -rf "$tmp"' EXIT

case_() {   # case_ <name> <uname -m> <sysctl -n hw.optional.arm64 output, or "none" when the key is missing> <target arch>
  d="$tmp/$(printf '%s' "$1" | tr -c 'a-zA-Z0-9' '-')"
  mkdir -p "$d/bin" "$d/macos/Sources"
  cp "$here/build.sh" "$d/macos/build.sh"
  printf 'let appVersion = "0.0.0"\n' > "$d/macos/Sources/Core.swift"
  printf '#!/bin/sh\necho %s\n' "$2" > "$d/bin/uname"
  if [ "$3" = none ]; then printf '#!/bin/sh\necho "sysctl: unknown oid" >&2\nexit 1\n' > "$d/bin/sysctl"
  else printf '#!/bin/sh\necho %s\n' "$3" > "$d/bin/sysctl"; fi
  printf '#!/bin/sh\necho "$*" >> "%s/swiftc.txt"\nwhile [ $# -gt 0 ]; do [ "$1" = -o ] && : > "$2"; shift; done\nexit 0\n' "$d" > "$d/bin/swiftc"
  printf '#!/bin/sh\nexit 0\n' > "$d/bin/codesign"
  chmod +x "$d/bin/"*
  PATH="$d/bin:$PATH" bash "$d/macos/build.sh" > "$d/out.txt" 2>&1
  got=$(grep -o -- '-target [^ ]*' "$d/swiftc.txt" 2>/dev/null)
  if [ "$got" = "-target $4-apple-macos13.0" ]; then echo "PASS TC-271 build: $1 -> $4"
  else echo "FAIL TC-271 build: $1 -> want $4, got '${got:-no swiftc call}'"; sed 's/^/    /' "$d/out.txt"; fails=$((fails + 1)); fi
}

case_ "Rosetta shell on Apple Silicon" x86_64 1 arm64
case_ "Apple Silicon" arm64 1 arm64
case_ "Intel Mac" x86_64 0 x86_64
case_ "Intel Mac without the arm64 key" x86_64 none x86_64

echo "$((4 - fails)) passed, $fails failed"
[ "$fails" -eq 0 ]
