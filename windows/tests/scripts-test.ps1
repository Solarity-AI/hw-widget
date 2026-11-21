# Checks the build and the exe's own install and uninstall steps against scratch folders, without touching the
# real Startup folder, registry or helper. Run: powershell -File windows\tests\scripts-test.ps1
$fails = 0
function Check([string]$name, [bool]$ok) { if ($ok) { Write-Host "PASS $name" } else { Write-Host "FAIL $name"; $script:fails++ } }
$win = Split-Path $PSScriptRoot
# TC-145 and the normal unzip name: the build works from folders with brackets, spaces, parentheses and &.
foreach ($name in @("hwwidget-[1]-$PID", "hw widget-main (1) & co-$PID")) {
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
Write-Host "$fails failed"
exit $fails
