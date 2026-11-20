# Compiles HWWidget.exe with the C# compiler that ships inside Windows (.NET Framework 4.x). No SDK, no admin.
param([string]$Out = (Join-Path $PSScriptRoot 'bin\HWWidget.exe'))
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Write-Host '❌ .NET Framework 4 compiler not found' -ForegroundColor Red; exit 1 }
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$src = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$args = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', "/out:$Out",
          "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')",
          '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll') + $src
$log = & $csc @args 2>&1
if ($LASTEXITCODE -ne 0) { $log | ForEach-Object { Write-Host $_ -ForegroundColor Red }; Write-Host '❌ Build failed' -ForegroundColor Red; exit 1 }
Write-Host "✅ Built $Out" -ForegroundColor Green
