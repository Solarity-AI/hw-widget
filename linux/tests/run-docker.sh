#!/usr/bin/env sh
# Runs the Linux tests in Docker (no GNOME needed). PNG renders land in linux/tests/out/.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
docker build -q -t hwwidget-linux-test "$here" >/dev/null
docker run --rm -v "$here/..:/src" hwwidget-linux-test gjs -m /src/tests/run.js /src/tests/out
