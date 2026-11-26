#!/usr/bin/env sh
# The repo's 1 verify command: sh tools/verify.sh [windows|windows-core|linux|macos|macos-core ...]
# With no argument it runs every platform check this machine can run; CI calls it with 1 platform per job.
#   windows       windows/tests/run.ps1: build, --selftest, scripts-test.ps1 (needs Windows)
#   windows-core  windows/tests/run.ps1 -SkipScripts: build and --selftest only, the fast loop for windows/src (needs Windows)
#   linux         linux/tests/run-docker.sh: GNOME extension tests in Docker
#   macos-core    macos/tests/run-docker.sh: the shared Swift core's self-test in Docker
#   macos         macos/build.sh, then the app's --self-test and a 6 s live probe (needs a Mac)
# Prints PASS, FAIL or SKIP per platform and exits non-zero when any check fails.
set -u
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root" || exit 1
os=$(uname -s)
is_windows() { case "$os" in MINGW*|MSYS*|CYGWIN*) return 0;; *) return 1;; esac; }
# A Docker Desktop that is installed but not answering makes `docker info` wait forever: give it 15 s where a timeout
# command exists (macOS has none by default). The answer is kept, so the probe runs at most once.
docker_state=
has_docker() {
  if [ -z "$docker_state" ]; then
    docker_state=no
    if command -v docker >/dev/null 2>&1; then
      if command -v timeout >/dev/null 2>&1; then
        timeout 15 docker info >/dev/null 2>&1 && docker_state=yes
      else
        docker info >/dev/null 2>&1 && docker_state=yes
      fi
    fi
  fi
  [ "$docker_state" = yes ]
}
no_docker="Docker not found or not answering within 15 s"
if [ $# -eq 0 ]; then
  set --
  if is_windows; then set -- "$@" windows; else echo "SKIP windows (needs Windows)"; fi
  if [ "$os" = Darwin ]; then set -- "$@" macos; else echo "SKIP macos (needs a Mac)"; fi
  if has_docker; then set -- "$@" linux macos-core; else echo "SKIP linux, macos-core ($no_docker)"; fi
  [ $# -eq 0 ] && { echo "SKIP nothing this machine can run (needs Windows, macOS or Docker)"; exit 0; }
fi
fails=0
for p in "$@"; do
  echo "== $p"
  case "$p" in
    windows) powershell -NoProfile -ExecutionPolicy Bypass -File windows/tests/run.ps1 ;;
    windows-core) powershell -NoProfile -ExecutionPolicy Bypass -File windows/tests/run.ps1 -SkipScripts ;;
    linux) if has_docker; then sh linux/tests/run-docker.sh; else echo "$no_docker"; false; fi ;;
    macos-core) if has_docker; then sh macos/tests/run-docker.sh; else echo "$no_docker"; false; fi ;;
    macos)
      bin="macos/build/HW Widget.app/Contents/MacOS/HWWidget"
      ./macos/build.sh && "$bin" --self-test && "$bin" --probe 6 ;;
    *) echo "unknown platform: $p"; false ;;
  esac
  if [ $? -eq 0 ]; then echo "PASS $p"; else echo "FAIL $p"; fails=$((fails + 1)); fi
done
[ "$fails" -eq 0 ]
