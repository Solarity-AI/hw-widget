# Stops HW Widget and removes it, its startup entry and its settings.
Get-Process HWWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HWWidget' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA 'HWWidget') -ErrorAction SilentlyContinue
Write-Host '✅ HW Widget removed.' -ForegroundColor Green