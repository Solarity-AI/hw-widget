@echo off
rem Removes HW Widget: its Startup shortcut (and the Run value older versions used), its settings and,
rem with 1 admin prompt, the optional CPU temperature helper. No PowerShell.
setlocal
set "DIR=%LOCALAPPDATA%\HWWidget"
set "APPDIR=%LOCALAPPDATA%\Programs\HW Widget"
echo Removing HW Widget...
taskkill /IM HWWidget.exe /F >nul 2>&1
set "EXE=%APPDIR%\HWWidget.exe"
if exist "%EXE%" goto run
set "EXE=%DIR%\HWWidget.exe"
if exist "%EXE%" goto run
set "EXE=%~dp0windows\bin\HWWidget.exe"
if exist "%EXE%" goto run
rem The installed exe is gone (for example removed by an antivirus): build a temporary one from this folder.
rem call expands %% signs a second time, so the paths reach build.cmd through variables (a folder name may hold %).
set "BUILD=%~dp0windows\build.cmd"
call "%%BUILD%%" "%%EXE%%" >nul
if errorlevel 1 goto manual
:run
start "" /wait "%EXE%" --uninstall
set "RC=%errorlevel%"
goto files
:manual
set "RC=0"
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v HWWidget /f >nul 2>&1
del "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\HW Widget.lnk" >nul 2>&1
if exist "%ProgramFiles%\HWWidget\HWWidgetSensor.exe" set "RC=5"
:files
if exist "%DIR%" rmdir /s /q "%DIR%"
if exist "%APPDIR%" rmdir /s /q "%APPDIR%"
if "%RC%"=="5" goto helperleft
echo OK   HW Widget removed.
goto end
:helperleft
echo WARN HW Widget removed, but the CPU temperature helper is still installed. Run this again and click Yes on the admin prompt.
:end
pause
