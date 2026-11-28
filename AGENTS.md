# Rules for AI agents

Verify: `sh tools/verify.sh` (runs every platform check this machine can run; CI runs `sh tools/verify.sh <platform>`,
1 platform per job, only when that platform's files change).

HW Widget: a small hardware pill (temperature, CPU, memory, GPU or disk, download speed) for the Windows taskbar, the GNOME
top bar and the macOS menu bar. Built from source on the user's machine; no admin rights, no network access, no telemetry.

## Commands

The no-argument run prints `SKIP <platform> (<reason>)` for each check this machine can't run; Docker counts as missing
when `docker info` doesn't answer within 15 s.

- `sh tools/verify.sh windows-core`: build with `windows/build.ps1`, then the self-test, in about 15 s: the fast loop for a
  change in `windows/src`. Needs Windows.
- `sh tools/verify.sh windows`: `windows/tests/run.ps1` = the same, then `windows/tests/scripts-test.ps1` (scratch folders
  only, never the real Startup folder or registry), 2–5 min. Run it for a change to the installers or `windows/*.cmd`/`*.ps1`.
- `sh tools/verify.sh linux`: `linux/tests/run-docker.sh`, the GNOME extension tests in Docker (Debian trixie and Ubuntu 22.04).
- `sh tools/verify.sh macos-core`: `macos/tests/run-docker.sh`, the shared Swift core's self-test in Docker.
- `sh tools/verify.sh macos`: `macos/build.sh`, then the app's `--self-test` and `--probe 6`. Needs a Mac.
- After a build, `windows/bin/HWWidget.exe --selftest <report.txt>` and `windows/bin/HWWidget.exe --render <dir>` (PNG
  renders of every state); on a Mac `"macos/build/HW Widget.app/Contents/MacOS/HWWidget" --render <dir>`. Always pass the
  file or folder: without it the exe starts the real widget.

## Layout

- `windows/src/*.cs`: the Windows app (`Sampler.cs` reads sensors, `Processes.cs` ranks the busiest processes for the hover
  card, `Render.cs` formats and draws, `TestModes.cs` holds the self-test checks and `--render`). It is C# 5 only, built by
  the in-box .NET Framework `csc.exe` through `windows/build.cmd`: no `?.`, `$"..."`, `nameof` or expression-bodied members.
- `linux/gnome-extension/hwwidget@solarity.ai/`: the GNOME extension (`sensors.js`, `model.js`, `render.js`).
- `linux/tests/run.js`: the GNOME extension's checks; `linux/tests/install-test.sh` tests the install scripts.
- `macos/Sources/`: the macOS app (`Sampler.swift` reads sensors, `App.swift` draws, `Core.swift` is the shared logic and its
  self-test checks, which `macos/tests/main.swift` runs in Docker).
- The same behaviour exists in all 3 ports: sampling is `Sampler.cs` / `sensors.js` / `Sampler.swift`; formatting and the
  pill's cells are `Render.cs` / `model.js` / `Core.swift`; drawing is `Render.cs` / `render.js` / `App.swift`. A change to
  one usually needs its 2 counterparts, a check in each port, and the check counts in README's "Develop and test" table.
- Root `Install HW Widget (Windows).cmd` and `Uninstall HW Widget (Windows).cmd`: tested by `windows/tests/scripts-test.ps1`.

## Rules

- `windows/src/WidgetStrip.cs` is the shared top strip, the same file as `shared/WidgetStrip.cs` in
  `Solarity-AI/ai-usage-taskbar-widgets` and in a third, private widget repo: change all 3 copies together, or not at all.
- Line endings come from `.gitattributes`: `.cs`, `.ps1` and `.cmd` are CRLF; `.sh`, `.js` and `.swift` are LF. Keep them.
- Do not run the installers or uninstallers (`*.cmd` at the root, `linux/install.sh`, `macos/install.sh`, the uninstall
  scripts): they install into the user's session.
- No new dependencies and no network calls in the widget.

## Shipping

- 1 issue per piece of work, then a branch `feat/<issue>-<slug>` or `fix/<issue>-<slug>`.
- Commit subjects `feat: … (#<issue>)` or `fix: … (#<issue>)`; the pull request body says `Fixes #<issue>`.
- Squash merge once CI is green.
