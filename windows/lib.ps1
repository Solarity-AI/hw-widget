# Shared by install.ps1 and uninstall.ps1, and tested by tests/scripts-test.ps1.

# Runs the self-test of $exe into a fresh $report. Returns the summary line on success, throws otherwise,
# so an old report from an earlier build can never pass the gate.
function Invoke-SelfTestGate([string]$exe, [string]$report) {
    Remove-Item $report -Force -ErrorAction SilentlyContinue
    Start-Process $exe -ArgumentList '--selftest', "`"$report`"" -Wait
    if (-not (Test-Path $report)) { throw 'the self-test wrote no report' }
    $last = Get-Content $report -Tail 1
    if ($last -notmatch '^\d+ passed, 0 failed$') { throw "$last (details in $report)" }
    return $last
}

# The closing line of uninstall.ps1: honest about a helper that is still installed.
function Get-UninstallSummary([bool]$helperLeft) {
    if ($helperLeft) { return @('⚠ HW Widget removed, but the CPU temperature helper is still installed. Run uninstall.ps1 again and click Yes on the admin prompt.', 'Yellow') }
    return @('✅ HW Widget removed.', 'Green')
}
