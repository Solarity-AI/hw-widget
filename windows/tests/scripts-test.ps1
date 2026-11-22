# Checks the build and the exe's own install and uninstall steps against scratch folders, without touching the
# real Startup folder, registry or helper. Run: powershell -File windows\tests\scripts-test.ps1
$fails = 0
function Check([string]$name, [bool]$ok) { if ($ok) { Write-Host "PASS $name" } else { Write-Host "FAIL $name"; $script:fails++ } }
$win = Split-Path $PSScriptRoot
# TC-145 and the normal unzip name: the build works from folders with brackets, spaces, parentheses and &.
# QA-17: an & with no space next to it needs quotes of its own on the way to cmd.
foreach ($name in @("hwwidget-[1]-$PID", "hw widget-main (1) & co-$PID", "hwwidget&co-$PID")) {
    $box = Join-Path ([IO.Path]::GetTempPath()) $name
    [IO.Directory]::CreateDirectory((Join-Path $box 'src')) | Out-Null
    foreach ($f in @('build.cmd', 'build.ps1', 'app.manifest') + @(Get-ChildItem -LiteralPath (Join-Path $win 'src') -Filter *.cs | ForEach-Object { "src\$($_.Name)" })) {
        [IO.File]::Copy((Join-Path $win $f), (Join-Path $box $f))
    }
    $exe = Join-Path $box 'bin\HWWidget.exe'
    $log = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $box 'build.ps1') -Out $exe 2>&1 | Out-String
    Check "build: folder '$name' builds" (($LASTEXITCODE -eq 0) -and (Test-Path -LiteralPath $exe))
    Check "build: folder '$name' prints no cmd errors" ($log -notmatch 'cannot find|not recognized|syntax is incorrect')
    if ($name -like 'hw widget*' -and (Test-Path -LiteralPath $exe)) {
        $startup = Join-Path $box 'Startup'
        $lnk = Join-Path $startup 'HW Widget.lnk'
        Set-Content -LiteralPath (Join-Path $box 'bin\selftest.txt') '99 passed, 0 failed'
        $p = Start-Process -FilePath $exe -ArgumentList '--install', '--startup', "`"$startup`"", '--no-launch' -Wait -PassThru
        Check 'install: exit code 0' ($p.ExitCode -eq 0)
        $last = Get-Content -LiteralPath (Join-Path $box 'bin\selftest.txt') -Tail 1
        Check 'install: a fresh self-test report passed' (($last -match '^\d+ passed, 0 failed$') -and ($last -ne '99 passed, 0 failed'))
        $target = if (Test-Path -LiteralPath $lnk) { (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath } else { '' }
        Check 'install: Startup shortcut points at the exe' ($target -eq $exe)
        $p = Start-Process -FilePath $exe -ArgumentList '--uninstall', '--startup', "`"$startup`"", '--keep-helper' -Wait -PassThru
        Check 'uninstall: exit code 0 and shortcut gone' (($p.ExitCode -eq 0) -and -not (Test-Path -LiteralPath $lnk))
    }
    Remove-Item -LiteralPath $box -Recurse -Force -ErrorAction SilentlyContinue
}
# The installer notices a window that runs inside an app's private file copy (MSIX virtualization), where an
# install is invisible at sign-in and dies with the app, and installs through Task Scheduler instead.
$installer = Join-Path (Split-Path $win) 'Install HW Widget (Windows).cmd'
$fake = Join-Path ([IO.Path]::GetTempPath()) "hwwidget-appdata-$PID"
[IO.Directory]::CreateDirectory((Join-Path $fake 'Packages\Some.App_x\LocalCache\Local')) | Out-Null
$realAppData = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = $fake
    $out = & cmd /c "`"$installer`" --check-sandbox" 2>&1 | Out-String
    Check 'installer: a plain window is not sandboxed' (($LASTEXITCODE -eq 1) -and ($out.Trim() -eq 'not sandboxed'))
    # Inside the app, the probe the installer writes to %LOCALAPPDATA% shows up in the package's LocalCache too.
    Set-Content -LiteralPath (Join-Path $fake 'Packages\Some.App_x\LocalCache\Local\HWWidget-sandbox-probe.tmp') 'x'
    $out = & cmd /c "`"$installer`" --check-sandbox" 2>&1 | Out-String
    Check 'installer: a window inside an app package is sandboxed' (($LASTEXITCODE -eq 0) -and ($out.Trim() -eq 'sandboxed'))
    Check 'installer: the sandbox probe is cleaned up' (-not (Test-Path -LiteralPath (Join-Path $fake 'HWWidget-sandbox-probe.tmp')))
} finally { $env:LOCALAPPDATA = $realAppData; Remove-Item -LiteralPath $fake -Recurse -Force -ErrorAction SilentlyContinue }
$text = [IO.File]::ReadAllText($installer)
Check 'installer: builds before it stops the running widget' ($text.IndexOf('call "%%BUILD%%" "%%NEW%%"') -gt 0 -and $text.IndexOf('call "%%BUILD%%" "%%NEW%%"') -lt $text.IndexOf('taskkill'))
$untext = [IO.File]::ReadAllText((Join-Path (Split-Path $win) 'Uninstall HW Widget (Windows).cmd'))
# QA-07: call expands % twice, so a folder name holding % broke the path when it was written straight into call.
Check 'installer and uninstaller: build.cmd is called through variables' (($text + $untext) -notmatch 'call "%~dp0')
# QA-09: a new exe that fails its self-test is rolled back to the old one, which restarts.
Check 'installer: the old exe is kept until the self-test passes' ($text.IndexOf('copy /y "%EXE%" "%OLD%"') -gt 0 -and $text.IndexOf('copy /y "%EXE%" "%OLD%"') -lt $text.IndexOf('taskkill'))
Check 'installer: the old-folder exe is deleted only after the self-test passed' ($text.IndexOf('del "%OLDEXE%"') -gt $text.IndexOf('goto selftest'))
Check 'installer: a failed self-test puts the old exe back' ($text -match ':selftest[\s\S]*?move /y "%OLD%" "%EXE%"[\s\S]*?start "" "%EXE%"')
# QA-08: the installer says "running" after a Startup failure, so the widget must start before that exit code.
$installCs = [IO.File]::ReadAllText((Join-Path $win 'src\Install.cs'))
Check 'install: the widget starts before a Startup failure returns' ($installCs.IndexOf('Process.Start(exe)') -gt 0 -and $installCs.IndexOf('Process.Start(exe)') -lt $installCs.IndexOf('return StartupFailed'))
Check 'installer: a failed swap restarts the installed widget' ($text -match ':swapfail[\s\S]*?start "" "%EXE%"')
Check 'installer: a sandboxed window installs through Task Scheduler' ($text -match 'goto outside' -and $text -match 'schtasks /Create')

Write-Host "$fails failed"
exit $fails
