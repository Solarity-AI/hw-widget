# Dev and CI wrapper around build.cmd, so they compile exactly the way the installer does.
param([string]$Out = (Join-Path $PSScriptRoot 'bin\HWWidget.exe'))
$cmd = Join-Path $PSScriptRoot 'build.cmd'
& $env:ComSpec /d /c call $cmd $Out
exit $LASTEXITCODE
