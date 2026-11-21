@echo off
rem Compiles HWWidget.exe with the C# compiler that ships inside Windows (.NET Framework 4.x). No SDK, no admin.
rem Usage: build.cmd [output exe]   (default: windows\bin\HWWidget.exe)
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc
set "OUT=%~f1"
if "%~1"=="" set "OUT=%~dp0bin\HWWidget.exe"
for %%I in ("%OUT%") do set "OUTDIR=%%~dpI"
if not exist "%OUTDIR%" mkdir "%OUTDIR%"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /nowarn:1690 "/out:%OUT%" "/win32manifest:%~dp0app.manifest" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll "%~dp0src\*.cs"
if errorlevel 1 goto failed
echo OK   Built "%OUT%"
exit /b 0
:nocsc
echo ERROR .NET Framework 4 compiler not found
exit /b 1
:failed
echo ERROR Build failed
exit /b 1
