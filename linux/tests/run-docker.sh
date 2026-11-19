#!/usr/bin/env sh
# Runs the Linux tests in Docker (no GNOME needed) on 2 GJS versions: Debian trixie (GNOME 45+) and
# Ubuntu 22.04 (GNOME 42). PNG renders land in linux/tests/out/.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
# Git Bash on Windows: hand Docker C:/... paths and stop MSYS from rewriting /src.
if command -v cygpath >/dev/null 2>&1; then here=$(cygpath -m "$here"); export MSYS_NO_PATHCONV=1; fi
for base in debian:trixie-slim ubuntu:22.04; do
  tag=hwwidget-linux-test:$(echo "$base" | tr ':' '-')
  echo "== $base"
  docker build -q --build-arg BASE="$base" -t "$tag" "$here" >/dev/null
  docker run --rm -v "$here/..:/src" "$tag" sh -c 'gjs -m /src/tests/run.js /src/tests/out && gjs /src/tests/legacy-test.js && sh /src/tests/install-test.sh'
done
