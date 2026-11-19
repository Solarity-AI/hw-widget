#!/usr/bin/env sh
# Runs the Linux tests in Docker (no GNOME needed). PNG renders land in linux/tests/out/.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
# Git Bash on Windows: hand Docker C:/... paths and stop MSYS from rewriting /src.
if command -v cygpath >/dev/null 2>&1; then here=$(cygpath -m "$here"); export MSYS_NO_PATHCONV=1; fi
docker build -q -t hwwidget-linux-test "$here" >/dev/null
docker run --rm -v "$here/..:/src" hwwidget-linux-test sh -c 'gjs -m /src/tests/run.js /src/tests/out && sh /src/tests/install-test.sh'
