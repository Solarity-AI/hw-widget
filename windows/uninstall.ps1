# Stops HW Widget and removes it, its startup entry, its settings and the optional CPU temperature helper.
Get-Process HWWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
$helperDir = Join-Path $env:ProgramFiles 'HWWidget'
if ((Test-Path $helperDir) -or (Test-Path (Join-Path $env:ProgramData 'HWWidget'))) {
    # The CPU temperature helper runs as SYSTEM, so removing it needs 1 UAC prompt.
    $cmd = "schtasks /End /TN 'HW Widget CPU temperature helper' 2>`$null; Get-Process HWWidgetSensor -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500; " +
           "schtasks /Delete /F /TN 'HW Widget CPU temperature helper' 2>`$null; Remove-Item -Recurse -Force '$helperDir', '$(Join-Path $env:ProgramData 'HWWidget')' -ErrorAction SilentlyContinue"
    $p = Start-Process powershell -ArgumentList '-NoProfile', '-Command', $cmd -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ErrorAction SilentlyContinue
    if ($p -and $p.ExitCode -eq 0) { Write-Host '✅ CPU temperature helper removed.' -ForegroundColor Green } else { Write-Host '⚠ CPU temperature helper not removed (admin prompt declined).' -ForegroundColor Yellow }
}
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HWWidget' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA 'HWWidget') -ErrorAction SilentlyContinue
Write-Host '✅ HW Widget removed.' -ForegroundColor Green