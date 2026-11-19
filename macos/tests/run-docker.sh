#!/usr/bin/env sh
# Compiles macos/Sources/Core.swift with the Linux self-test runner in the swift:6.0 image and runs it.
# The AppKit and IOKit parts need a Mac: run macos/build.sh and the app's --self-test there.
set -eu
here=$(cd "$(dirname "$0")/.." && pwd)
# Git Bash on Windows: hand Docker C:/... paths and stop MSYS from rewriting /src and /tmp.
if command -v cygpath >/dev/null 2>&1; then here=$(cygpath -m "$here"); export MSYS_NO_PATHCONV=1; fi
docker run --rm -v "$here:/src" -w /tmp swift:6.0 sh -c \
  'swiftc -swift-version 5 -o core-test /src/Sources/Core.swift /src/tests/main.swift && ./core-test'
