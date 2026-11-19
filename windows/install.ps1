# Installs HW Widget for the current user: compiles it into %LOCALAPPDATA%\HWWidget, runs its self-test,
# starts it with Windows and launches it. No admin rights, no downloads.
param([switch]$EnableCpuTemp)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'HWWidget'
$exe = Join-Path $dir 'HWWidget.exe'
Get-Process HWWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
& (Join-Path $PSScriptRoot 'build.ps1') -Out $exe
if ($LASTEXITCODE -ne 0) { exit 1 }
$report = Join-Path $env:TEMP 'hwwidget-selftest.txt'
Start-Process $exe -ArgumentList '--selftest', "`"$report`"" -Wait
$last = Get-Content $report -Tail 1
if ($last -notmatch ' 0 failed') { Write-Host "❌ Self-test failed: $last (details in $report)" -ForegroundColor Red; exit 1 }
Write-Host "✅ Self-test: $last" -ForegroundColor Green
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HWWidget' -Value "`"$exe`""
Start-Process $exe
Write-Host '✅ HW Widget installed and running. It sits on the taskbar left of the tray and starts with Windows.' -ForegroundColor Green
Write-Host '   Hover it for details, click to pin the card, right-click for settings.' -ForegroundColor DarkGray
$helper = Join-Path $env:ProgramFiles 'HWWidget\HWWidgetSensor.exe'
if ($EnableCpuTemp -or (Test-Path $helper)) {
    # Opt-in: 1 UAC prompt installs (or updates) the SYSTEM helper that reads admin-only CPU sensors.
    $p = Start-Process $exe -ArgumentList '--install-helper' -Verb RunAs -Wait -PassThru -ErrorAction SilentlyContinue
    if ($p -and $p.ExitCode -eq 0) { Write-Host '✅ CPU temperature helper installed and running.' -ForegroundColor Green }
    else { Write-Host '⚠ CPU temperature helper not installed (admin prompt declined or failed).' -ForegroundColor Yellow }
} else {
    Write-Host '   No CPU temperature? Right-click the pill and choose "Enable CPU temperature (admin)".' -ForegroundColor DarkGray
}