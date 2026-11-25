#!/usr/bin/env sh
# The repo's 1 verify command: sh tools/verify.sh [windows|linux|macos|macos-core ...]
# With no argument it runs every platform check this machine can run; CI calls it with 1 platform per job.
#   windows     windows/tests/run.ps1: build, --selftest, scripts-test.ps1 (needs Windows)
#   linux       linux/tests/run-docker.sh: GNOME extension tests in Docker
#   macos-core  macos/tests/run-docker.sh: the shared Swift core's self-test in Docker
#   macos       macos/build.sh, then the app's --self-test and a 6 s live probe (needs a Mac)
# Prints PASS, FAIL or SKIP per platform and exits non-zero when any check fails.
set -u
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root" || exit 1
os=$(uname -s)
is_windows() { case "$os" in MINGW*|MSYS*|CYGWIN*) return 0;; *) return 1;; esac; }
has_docker() { command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; }
if [ $# -eq 0 ]; then
  set --
  if is_windows; then set -- "$@" windows; fi
  if [ "$os" = Darwin ]; then set -- "$@" macos; fi
  if has_docker; then set -- "$@" linux macos-core; fi
  [ $# -eq 0 ] && { echo "SKIP nothing this machine can run (needs Windows, macOS or Docker)"; exit 0; }
fi
fails=0
for p in "$@"; do
  echo "== $p"
  case "$p" in
    windows) powershell -NoProfile -ExecutionPolicy Bypass -File windows/tests/run.ps1 ;;
    linux) sh linux/tests/run-docker.sh ;;
    macos-core) sh macos/tests/run-docker.sh ;;
    macos)
      bin="macos/build/HW Widget.app/Contents/MacOS/HWWidget"
      ./macos/build.sh && "$bin" --self-test && "$bin" --probe 6 ;;
    *) echo "unknown platform: $p"; false ;;
  esac
  if [ $? -eq 0 ]; then echo "PASS $p"; else echo "FAIL $p"; fails=$((fails + 1)); fi
done
[ "$fails" -eq 0 ]
