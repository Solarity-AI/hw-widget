# Rules for AI agents

Verify: `sh tools/verify.sh` (runs every platform check this machine can run; CI runs `sh tools/verify.sh <platform>`,
1 platform per job, only when that platform's files change).

HW Widget: a small hardware pill (temperature, CPU, memory, GPU or disk, download speed) for the Windows taskbar, the GNOME
top bar and the macOS menu bar. Built from source on the user's machine; no admin rights, no network access, no telemetry.

## Commands

- `sh tools/verify.sh windows`: `windows/tests/run.ps1` = build with `windows/build.ps1`, `HWWidget.exe --selftest`, then
  `windows/tests/scripts-test.ps1` (scratch folders only, never the real Startup folder or registry). Needs Windows.
- `sh tools/verify.sh linux`: `linux/tests/run-docker.sh`, the GNOME extension tests in Docker (Debian trixie and Ubuntu 22.04).
- `sh tools/verify.sh macos-core`: `macos/tests/run-docker.sh`, the shared Swift core's self-test in Docker.
- `sh tools/verify.sh macos`: `macos/build.sh`, then the app's `--self-test` and `--probe 6`. Needs a Mac.
- `HWWidget.exe --render <dir>` (Windows) and `HWWidget --render <dir>` (macOS): PNG renders of every state.

## Layout

- `windows/src/*.cs`: the Windows app (`Sampler.cs` reads sensors, `Render.cs` draws, `WidgetStrip.cs` is the shared top strip).
- `linux/gnome-extension/hwwidget@solarity.ai/`: the GNOME extension (`sensors.js`, `model.js`, `render.js`).
- `macos/Sources/`: the macOS app (`Core.swift` is the shared logic the Docker test compiles).
- The same behaviour exists in all 3 ports: a change to sampling, the model or rendering usually needs its 2 counterparts and
  their self-test counts updated.
- Root `Install HW Widget (Windows).cmd` and `Uninstall HW Widget (Windows).cmd`: tested by `windows/tests/scripts-test.ps1`.

## Rules

- Line endings come from `.gitattributes`: `.cs`, `.ps1` and `.cmd` are CRLF; `.sh`, `.js` and `.swift` are LF. Keep them.
- Do not run the installers or uninstallers (`*.cmd` at the root, `linux/install.sh`, `macos/install.sh`, the uninstall
  scripts): they install into the user's session.
- No new dependencies and no network calls in the widget.

## Shipping

- 1 issue per piece of work, then a branch `feat/<issue>-<slug>` or `fix/<issue>-<slug>`.
- Commit subjects `feat: … (#<issue>)` or `fix: … (#<issue>)`; the pull request body says `Fixes #<issue>`.
- Squash merge once CI is green.
