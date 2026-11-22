# Dev and CI wrapper around build.cmd, so they compile exactly the way the installer does.
param([string]$Out = (Join-Path $PSScriptRoot 'bin\HWWidget.exe'))
$cmd = Join-Path $PSScriptRoot 'build.cmd'
# QA-17: quote both paths for cmd, which splits an unquoted & even with no space next to it.
& $env:ComSpec /d /c "call `"$cmd`" `"$Out`""
exit $LASTEXITCODE
