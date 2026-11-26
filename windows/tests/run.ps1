# Windows checks with 1 exit code: builds HWWidget.exe the installer's way, runs its --selftest and
# windows\tests\scripts-test.ps1. Run: powershell -NoProfile -ExecutionPolicy Bypass -File windows\tests\run.ps1
# -SkipScripts stops after the self-test: the fast loop for a change in windows\src (sh tools/verify.sh windows-core).
param([string]$Report = (Join-Path ([IO.Path]::GetTempPath()) "hwwidget-selftest-$PID.txt"), [switch]$SkipScripts)
$win = Split-Path $PSScriptRoot
$exe = Join-Path $win 'bin\HWWidget.exe'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $win 'build.ps1')
if ($LASTEXITCODE -ne 0) { Write-Host 'FAIL build'; exit 1 }
Start-Process -FilePath $exe -ArgumentList '--selftest', "`"$Report`"" -Wait
if (-not (Test-Path -LiteralPath $Report)) { Write-Host "FAIL self-test wrote no report to $Report"; exit 1 }
Get-Content -LiteralPath $Report
if ((Get-Content -LiteralPath $Report -Tail 1) -notmatch ' 0 failed') { Write-Host 'FAIL self-test'; exit 1 }
if ($SkipScripts) { Write-Host 'PASS windows-core (scripts-test.ps1 skipped)'; exit 0 }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'scripts-test.ps1')
if ($LASTEXITCODE -ne 0) { Write-Host 'FAIL scripts-test.ps1'; exit 1 }
Write-Host 'PASS windows'
exit 0
