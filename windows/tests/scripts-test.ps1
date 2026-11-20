# Checks the install and uninstall script logic without installing anything. Run: powershell -File windows\tests\scripts-test.ps1
. (Join-Path $PSScriptRoot '..\lib.ps1')
$fails = 0
function Check([string]$name, [bool]$ok) { if ($ok) { Write-Host "PASS $name" } else { Write-Host "FAIL $name"; $script:fails++ } }
$report = Join-Path ([IO.Path]::GetTempPath()) "hwwidget-gate-test-$PID.txt"
Set-Content $report '23 passed, 0 failed'
$noReport = Join-Path $env:SystemRoot 'System32\where.exe'   # exits at once and writes no report
$threw = $false; try { Invoke-SelfTestGate $noReport $report | Out-Null } catch { $threw = $true }
Check 'gate: an old passing report does not pass a build that wrote none' $threw
Check 'gate: the old report was removed first' (-not (Test-Path $report))
Check 'uninstall: helper left -> warning, not removed' ((Get-UninstallSummary $true)[0] -like '⚠*still installed*')
Check 'uninstall: helper gone -> removed' ((Get-UninstallSummary $false)[0] -eq '✅ HW Widget removed.')
# TC-145: the build works from a folder whose name has [ or ] (PowerShell reads those as wildcards).
$box = Join-Path ([IO.Path]::GetTempPath()) "hwwidget-[1]-$PID"
[IO.Directory]::CreateDirectory((Join-Path $box 'src')) | Out-Null
$win = Split-Path $PSScriptRoot
foreach ($f in @('build.ps1', 'app.manifest') + @(Get-ChildItem -LiteralPath (Join-Path $win 'src') -Filter *.cs | ForEach-Object { "src\$($_.Name)" })) {
    [IO.File]::Copy((Join-Path $win $f), (Join-Path $box $f))
}
$exe = Join-Path $box 'bin\HWWidget.exe'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $box 'build.ps1') -Out $exe | Out-Null
Check 'TC-145 build: a folder name with brackets builds' (($LASTEXITCODE -eq 0) -and (Test-Path -LiteralPath $exe))
Remove-Item -LiteralPath $box -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "$fails failed"
exit $fails
