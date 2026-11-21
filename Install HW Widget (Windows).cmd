@echo off
rem Installs HW Widget for the current user: compiles it into %LOCALAPPDATA%\HWWidget, then the widget itself
rem runs its self-test, adds a shortcut to your Startup folder and starts. No PowerShell, no admin, no downloads.
rem Add --cpu-temp to also install the CPU temperature helper (1 admin prompt).
setlocal
set "EXE=%LOCALAPPDATA%\HWWidget\HWWidget.exe"
echo Installing HW Widget...
taskkill /IM HWWidget.exe /F >nul 2>&1
call "%~dp0windows\build.cmd" "%EXE%"
if not errorlevel 1 goto built
rem The old widget can hold its exe for a moment after taskkill, and an antivirus can lock a fresh exe while it
rem scans it: wait and try once more before giving up.
echo      Retrying in 3 seconds...
timeout /t 3 /nobreak >nul 2>&1
call "%~dp0windows\build.cmd" "%EXE%"
if errorlevel 1 goto buildfail
:built
start "" /wait "%EXE%" --install %*
set "RC=%errorlevel%"
if "%RC%"=="3" goto selftest
if "%RC%"=="4" goto startup
if "%RC%"=="7" goto helper
if not "%RC%"=="0" goto fail
echo OK   HW Widget installed and running. It sits on the taskbar left of the tray and starts with Windows.
echo      Hover it for details, click to pin the card, right-click for settings.
if exist "%ProgramFiles%\HWWidget\HWWidgetSensor.exe" echo      The CPU temperature helper keeps its old version; right-click the pill, remove it and enable it again to update it.
goto end
:selftest
echo ERROR Self-test failed. Details: "%LOCALAPPDATA%\HWWidget\selftest.txt"
goto end
:startup
echo WARN HW Widget is running, but the Startup shortcut could not be created. Turn on "Start with Windows" in its menu.
goto end
:helper
echo OK   HW Widget installed and running.
echo WARN CPU temperature helper not installed (admin prompt declined or failed).
goto end
:buildfail
echo ERROR Could not build "%EXE%".
echo      If the message above says "Access is denied", your antivirus is blocking the file: add an exception
echo      for "%LOCALAPPDATA%\HWWidget" and this folder, then run the installer again.
goto end
:fail
echo ERROR Install failed (exit code %RC%).
:end
pause
