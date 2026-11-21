@echo off
rem Installs HW Widget for the current user: compiles it into %LOCALAPPDATA%\Programs\HW Widget, then the widget
rem itself runs its self-test, adds a shortcut to your Startup folder and starts. No PowerShell, no admin, no downloads.
rem If Windows or an antivirus refuses that folder, it builds into windows\bin inside this folder instead.
rem Add --cpu-temp to also install the CPU temperature helper (1 admin prompt).
rem --check-sandbox only reports whether this window runs inside an app's private file copy (exit 0 = it does).
setlocal
if "%~1"=="--check-sandbox" goto checksandbox
rem A window started by a Store/MSIX app (for example a terminal inside the Claude desktop app) sees a private copy
rem of %LOCALAPPDATA% and %APPDATA%: an install from there is invisible to Windows at sign-in and dies with the
rem app. Run the install through Task Scheduler instead, which starts it outside the app.
call :sandboxed
if not errorlevel 1 goto outside
rem Versions up to 1.2.1 installed into %LOCALAPPDATA%\HWWidget (still the settings folder). An antivirus that once
rem removed that exe can keep refusing new files at the same path, so the app now lives in its own folder.
set "OLDEXE=%LOCALAPPDATA%\HWWidget\HWWidget.exe"
set "DIR=%LOCALAPPDATA%\Programs\HW Widget"
set "EXE=%DIR%\HWWidget.exe"
set "NEW=%DIR%\HWWidget.new.exe"
echo Installing HW Widget...
rem Build next to the old exe first: a failed build must leave the running widget alone.
call "%~dp0windows\build.cmd" "%NEW%"
if not errorlevel 1 goto built
rem An antivirus can lock a fresh exe while it scans it: wait and try once more before giving up.
echo      Retrying in 3 seconds...
call :wait 3
call "%~dp0windows\build.cmd" "%NEW%"
if not errorlevel 1 goto built
del "%NEW%" >nul 2>&1
echo      That folder is blocked. Installing into this folder instead.
set "DIR=%~dp0windows\bin"
set "EXE=%~dp0windows\bin\HWWidget.exe"
set "NEW=%~dp0windows\bin\HWWidget.new.exe"
call "%~dp0windows\build.cmd" "%NEW%"
if errorlevel 1 goto buildfail
:built
taskkill /IM HWWidget.exe /F >nul 2>&1
move /y "%NEW%" "%EXE%" >nul 2>&1
if not errorlevel 1 goto swapped
rem The old widget can hold its exe for a moment after taskkill.
call :wait 3
move /y "%NEW%" "%EXE%" >nul 2>&1
if errorlevel 1 goto swapfail
:swapped
if /i not "%EXE%"=="%OLDEXE%" del "%OLDEXE%" >nul 2>&1
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
echo ERROR Self-test failed. Details: "%DIR%\selftest.txt"
goto end
:startup
echo WARN HW Widget is running, but the Startup shortcut could not be created. Turn on "Start with Windows" in its menu.
goto end
:helper
echo OK   HW Widget installed and running.
echo WARN CPU temperature helper not installed (admin prompt declined or failed).
goto end
:buildfail
del "%NEW%" >nul 2>&1
echo ERROR Could not build "%EXE%". Your installed widget was left as it was.
echo      If the message above says "Access is denied", your antivirus is blocking the file: add an exception
echo      for this folder, then run the installer again.
goto end
:swapfail
del "%NEW%" >nul 2>&1
echo ERROR Could not replace "%EXE%": it is still in use. Restarting the installed widget.
if exist "%EXE%" start "" "%EXE%"
goto end
:fail
echo ERROR Install failed (exit code %RC%).
goto end

:outside
set "RUN=%~dp0windows\bin"
if not exist "%RUN%" mkdir "%RUN%"
set "LOG=%RUN%\install-outside.log"
set "WRAP=%RUN%\install-outside.cmd"
set "TASK=HW Widget install"
del "%LOG%" >nul 2>&1
> "%WRAP%" echo @echo off
>> "%WRAP%" echo set "HWW_OUTSIDE=1"
>> "%WRAP%" echo call "%~f0" %* ^> "%LOG%" 2^>^&1
>> "%WRAP%" echo echo HWW-INSTALL-DONE^>^> "%LOG%"
echo This window runs inside an app's private copy of your user folders; installing through Task Scheduler instead...
schtasks /Create /TN "%TASK%" /TR "\"%WRAP%\"" /SC ONCE /ST 00:00 /F >nul 2>&1
if errorlevel 1 goto outsidefail
schtasks /Run /TN "%TASK%" >nul 2>&1
if errorlevel 1 goto outsidefail
set /a "TRIES=0"
:outsidewait
call :wait 1
findstr /c:"HWW-INSTALL-DONE" "%LOG%" >nul 2>&1
if not errorlevel 1 goto outsidedone
set /a "TRIES+=1"
if %TRIES% lss 600 goto outsidewait
schtasks /Delete /TN "%TASK%" /F >nul 2>&1
echo WARN The install is still running after 10 minutes. Its output: "%LOG%"
goto end
:outsidedone
schtasks /Delete /TN "%TASK%" /F >nul 2>&1
del "%WRAP%" >nul 2>&1
findstr /v /c:"HWW-INSTALL-DONE" "%LOG%"
goto end
:outsidefail
schtasks /Delete /TN "%TASK%" /F >nul 2>&1
del "%WRAP%" >nul 2>&1
echo ERROR Could not start the install outside the app. Open this folder in File Explorer and double-click
echo      "%~nx0" instead.
goto end

:checksandbox
call :sandboxed
if errorlevel 1 (
    echo not sandboxed
    exit /b 1
)
echo sandboxed
exit /b 0

rem Exit code 0 when a new file written to %LOCALAPPDATA% lands in an app package's LocalCache instead (MSIX file
rem virtualization). The probe name is fixed so a leftover from an earlier run is removed first.
:sandboxed
if defined HWW_OUTSIDE exit /b 1
if not exist "%LOCALAPPDATA%\Packages\" exit /b 1
set "PROBE=HWWidget-sandbox-probe.tmp"
set "HIT="
del "%LOCALAPPDATA%\%PROBE%" >nul 2>&1
(echo probe) > "%LOCALAPPDATA%\%PROBE%" 2>nul
for /d %%P in ("%LOCALAPPDATA%\Packages\*") do if exist "%%~fP\LocalCache\Local\%PROBE%" set "HIT=1"
del "%LOCALAPPDATA%\%PROBE%" >nul 2>&1
if defined HIT exit /b 0
exit /b 1

rem Waits N seconds. ping, not timeout: timeout fails at once when input is redirected.
:wait
ping -n %~1 127.0.0.1 >nul 2>&1
ping -n 2 127.0.0.1 >nul 2>&1
exit /b 0

:end
if not defined HWW_OUTSIDE pause
