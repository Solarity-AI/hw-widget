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
Write-Host "$fails failed"
exit $fails
