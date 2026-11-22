# Checks the build and the exe's own install and uninstall steps against scratch folders, without touching the
# real Startup folder, registry or helper. Run: powershell -File windows\tests\scripts-test.ps1
$fails = 0
function Check([string]$name, [bool]$ok) { if ($ok) { Write-Host "PASS $name" } else { Write-Host "FAIL $name"; $script:fails++ } }
$win = Split-Path $PSScriptRoot
# TC-145 and the normal unzip name: the build works from folders with brackets, spaces, parentheses and &.
# QA-17: an & with no space next to it needs quotes of its own on the way to cmd.
foreach ($name in @("hwwidget-[1]-$PID", "hw widget-main (1) & co-$PID", "hwwidget&co-$PID")) {
    $box = Join-Path ([IO.Path]::GetTempPath()) $name
    [IO.Directory]::CreateDirectory((Join-Path $box 'src')) | Out-Null
    foreach ($f in @('build.cmd', 'build.ps1', 'app.manifest') + @(Get-ChildItem -LiteralPath (Join-Path $win 'src') -Filter *.cs | ForEach-Object { "src\$($_.Name)" })) {
        [IO.File]::Copy((Join-Path $win $f), (Join-Path $box $f))
    }
    $exe = Join-Path $box 'bin\HWWidget.exe'
    $log = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $box 'build.ps1') -Out $exe 2>&1 | Out-String
    Check "build: folder '$name' builds" (($LASTEXITCODE -eq 0) -and (Test-Path -LiteralPath $exe))
    Check "build: folder '$name' prints no cmd errors" ($log -notmatch 'cannot find|not recognized|syntax is incorrect')
    if ($name -like 'hw widget*' -and (Test-Path -LiteralPath $exe)) {
        $startup = Join-Path $box 'Startup'
        $lnk = Join-Path $startup 'HW Widget.lnk'
        Set-Content -LiteralPath (Join-Path $box 'bin\selftest.txt') '99 passed, 0 failed'
        $p = Start-Process -FilePath $exe -ArgumentList '--install', '--startup', "`"$startup`"", '--no-launch' -Wait -PassThru
        Check 'install: exit code 0' ($p.ExitCode -eq 0)
        $last = Get-Content -LiteralPath (Join-Path $box 'bin\selftest.txt') -Tail 1
        Check 'install: a fresh self-test report passed' (($last -match '^\d+ passed, 0 failed$') -and ($last -ne '99 passed, 0 failed'))
        $target = if (Test-Path -LiteralPath $lnk) { (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath } else { '' }
        Check 'install: Startup shortcut points at the exe' ($target -eq $exe)
        $p = Start-Process -FilePath $exe -ArgumentList '--uninstall', '--startup', "`"$startup`"", '--keep-helper' -Wait -PassThru
        Check 'uninstall: exit code 0 and shortcut gone' (($p.ExitCode -eq 0) -and -not (Test-Path -LiteralPath $lnk))
    }
    Remove-Item -LiteralPath $box -Recurse -Force -ErrorAction SilentlyContinue
}
# The installer notices a window that runs inside an app's private file copy (MSIX virtualization), where an
# install is invisible at sign-in and dies with the app, and installs through Task Scheduler instead.
$installer = Join-Path (Split-Path $win) 'Install HW Widget (Windows).cmd'
$fake = Join-Path ([IO.Path]::GetTempPath()) "hwwidget-appdata-$PID"
[IO.Directory]::CreateDirectory((Join-Path $fake 'Packages\Some.App_x\LocalCache\Local')) | Out-Null
$realAppData = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = $fake
    $out = & cmd /c "`"$installer`" --check-sandbox" 2>&1 | Out-String
    Check 'installer: a plain window is not sandboxed' (($LASTEXITCODE -eq 1) -and ($out.Trim() -eq 'not sandboxed'))
    # Inside the app, the probe the installer writes to %LOCALAPPDATA% shows up in the package's LocalCache too.
    Set-Content -LiteralPath (Join-Path $fake 'Packages\Some.App_x\LocalCache\Local\HWWidget-sandbox-probe.tmp') 'x'
    $out = & cmd /c "`"$installer`" --check-sandbox" 2>&1 | Out-String
    Check 'installer: a window inside an app package is sandboxed' (($LASTEXITCODE -eq 0) -and ($out.Trim() -eq 'sandboxed'))
    Check 'installer: the sandbox probe is cleaned up' (-not (Test-Path -LiteralPath (Join-Path $fake 'HWWidget-sandbox-probe.tmp')))
} finally { $env:LOCALAPPDATA = $realAppData; Remove-Item -LiteralPath $fake -Recurse -Force -ErrorAction SilentlyContinue }
$text = [IO.File]::ReadAllText($installer)
Check 'installer: builds before it stops the running widget' ($text.IndexOf('call "%%BUILD%%" "%%NEW%%"') -gt 0 -and $text.IndexOf('call "%%BUILD%%" "%%NEW%%"') -lt $text.IndexOf('taskkill'))
$untext = [IO.File]::ReadAllText((Join-Path (Split-Path $win) 'Uninstall HW Widget (Windows).cmd'))
# QA-07: call expands % twice, so a folder name holding % broke the path when it was written straight into call.
Check 'installer and uninstaller: build.cmd is called through variables' (($text + $untext) -notmatch 'call "%~dp0')
# QA-09: a new exe that fails its self-test is rolled back to the old one, which restarts.
Check 'installer: the old exe is kept until the self-test passes' ($text.IndexOf('copy /y "%EXE%" "%OLD%"') -gt 0 -and $text.IndexOf('copy /y "%EXE%" "%OLD%"') -lt $text.IndexOf('taskkill'))
Check 'installer: the old-folder exe is deleted only after the self-test passed' ($text.IndexOf('del "%OLDEXE%"') -gt $text.IndexOf('goto selftest'))
Check 'installer: a failed self-test puts the old exe back' ($text -match ':selftest[\s\S]*?move /y "%OLD%" "%EXE%"[\s\S]*?start "" "%EXE%"')
# QA-08: the installer says "running" after a Startup failure, so the widget must start before that exit code.
$installCs = [IO.File]::ReadAllText((Join-Path $win 'src\Install.cs'))
Check 'install: the widget starts before a Startup failure returns' ($installCs.IndexOf('Process.Start(exe)') -gt 0 -and $installCs.IndexOf('Process.Start(exe)') -lt $installCs.IndexOf('return StartupFailed'))
Check 'installer: a failed swap restarts the installed widget' ($text -match ':swapfail[\s\S]*?start "" "%EXE%"')
Check 'installer: a sandboxed window installs through Task Scheduler' ($text -match 'goto outside' -and $text -match 'schtasks /Create')

# Behaviour tests on copies of the two .cmd scripts. In each copy taskkill, schtasks and reg call a stub exe that only
# logs its arguments; %LOCALAPPDATA%, %APPDATA% and %ProgramFiles% point at scratch folders and windows\build.cmd is a
# stub too, so nothing reaches the real widget, scheduled tasks, Startup folder or registry. The same stub stands in
# for HWWidget.exe and returns the exit code a test asks for; "previous version" exes are a second build of the stub.
$root = Join-Path ([IO.Path]::GetTempPath()) "hwwidget-cmd-$PID"
$stubs = Join-Path $root 'stubs'
[IO.Directory]::CreateDirectory($stubs) | Out-Null
[IO.File]::WriteAllText((Join-Path $stubs 'stub.cs'), @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

// Test stand-in for taskkill, schtasks, reg, the build and HWWidget.exe; its file name is its role. It logs every call
// to %HWW_T_STATE%\log.txt and writes or starts files only inside the test's scratch folder (%HWW_T_ROOT%).
static class Stub
{
    static string Env(string name) { return Environment.GetEnvironmentVariable(name) ?? ""; }

    static bool Inside(string path)
    {
        try
        {
            string root = Path.GetFullPath(Env("HWW_T_ROOT")).TrimEnd('\\') + "\\";
            return Env("HWW_T_ROOT").Length > 0 && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void Log(string line)
    {
        for (int i = 0; i < 50; i++)
        {
            try { File.AppendAllText(Path.Combine(Env("HWW_T_STATE"), "log.txt"), line + "\r\n", Encoding.UTF8); return; }
            catch (IOException) { Thread.Sleep(100); }
        }
    }

    static int Code(string name) { int rc; return int.TryParse(Env(name), out rc) ? rc : 0; }

    static int Main(string[] args)
    {
        string self = Environment.GetCommandLineArgs()[0];
        string role = Path.GetFileNameWithoutExtension(self).ToLowerInvariant();
        Log(role + "|" + self + "|" + string.Join("|", args));
        if (role == "build") return Build(args.Length > 0 ? args[0] : "");
        if (role == "schtasks") return Schtasks(args);
        if (role == "hwwidget" && args.Length > 0 && args[0] == "--install") return Code("HWW_T_INSTALL_RC");
        if (role == "hwwidget" && args.Length > 0 && args[0] == "--uninstall") return Code("HWW_T_UNINSTALL_RC");
        return 0;
    }

    // HWW_T_BUILD: "fail" fails every build; "programs" fails builds into Programs\HW Widget; "programs-nofile" also
    // reports success elsewhere without leaving the exe (as when an antivirus removes a fresh exe at once).
    static int Build(string output)
    {
        string mode = Env("HWW_T_BUILD");
        if (!Inside(output)) { Log("refused|" + output); return 1; }
        if (mode == "fail") return 1;
        bool programs = output.IndexOf("\\Programs\\HW Widget\\", StringComparison.OrdinalIgnoreCase) >= 0;
        if (mode.StartsWith("programs") && programs) return 1;
        if (mode == "programs-nofile") return 0;
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.Copy(Path.Combine(Env("HWW_T_STUBS"), "HWWidget.exe"), output, true);
        return 0;
    }

    // /Create keeps the /TR value and a copy of the wrapper it names (the installer deletes the wrapper later); /Run
    // starts that program the way Task Scheduler does: no shell, a new hidden console, in System32, and waits for it.
    static int Schtasks(string[] args)
    {
        string state = Env("HWW_T_STATE"), saved = Path.Combine(state, "tr.txt");
        int tr = Array.IndexOf(args, "/TR");
        if (tr >= 0 && tr + 1 < args.Length)
        {
            File.WriteAllText(saved, args[tr + 1], Encoding.UTF8);
            string wrapper = args[tr + 1].Trim('"');
            if (Inside(wrapper) && File.Exists(wrapper)) File.Copy(wrapper, Path.Combine(state, "wrapper.txt"), true);
            return 0;
        }
        if (Array.IndexOf(args, "/Run") < 0 || !File.Exists(saved)) return 0;
        string program = File.ReadAllText(saved, Encoding.UTF8).Trim('"');
        if (!Inside(program) || !File.Exists(program)) return 0;
        ProcessStartInfo p = new ProcessStartInfo(program);
        p.UseShellExecute = false;
        p.CreateNoWindow = true;
        p.WorkingDirectory = Environment.SystemDirectory;
        using (Process t = Process.Start(p)) t.WaitForExit(60000);
        return 0;
    }
}
'@)
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
foreach ($n in 'stub', 'old') { & $csc /nologo /target:winexe "/out:$(Join-Path $stubs "$n.exe")" (Join-Path $stubs 'stub.cs') | Out-Null }
foreach ($n in 'taskkill', 'schtasks', 'reg', 'build', 'HWWidget') { Copy-Item -LiteralPath (Join-Path $stubs 'stub.exe') -Destination (Join-Path $stubs "$n.exe") }
# A "previous version" is a second build of the stub: same behaviour, other bytes. (A copy with bytes added after its
# end would also differ, but an antivirus can refuse to start such a file.)
$oldExe = Join-Path $stubs 'old.exe'
$oldHash = (Get-FileHash -LiteralPath $oldExe).Hash
if ($oldHash -eq (Get-FileHash -LiteralPath (Join-Path $stubs 'stub.exe')).Hash) { throw 'The two stub builds are identical.' }
function Set-Old([string]$path) { [IO.Directory]::CreateDirectory((Split-Path $path)) | Out-Null; Copy-Item -LiteralPath $oldExe -Destination $path -Force }
function Test-Old([string]$path) { (Test-Path -LiteralPath $path) -and ((Get-FileHash -LiteralPath $path).Hash -eq $oldHash) }
# A scenario: its own scratch %LOCALAPPDATA%, %APPDATA%, %ProgramFiles% and stub log, as the environment for a copy.
# Windows sets %ProgramFiles% in every new process from ProgramW6432 (64-bit) or ProgramFiles(x86) (32-bit).
function New-Scenario([string]$name) {
    $s = Join-Path $root $name
    foreach ($d in 'lad\Programs\HW Widget', 'appdata', 'pf') { [IO.Directory]::CreateDirectory((Join-Path $s $d)) | Out-Null }
    $pf = Join-Path $s 'pf'
    @{ HWW_T_ROOT = $root; HWW_T_STATE = $s; HWW_T_STUBS = $stubs; LOCALAPPDATA = (Join-Path $s 'lad'); APPDATA = (Join-Path $s 'appdata')
       ProgramFiles = $pf; ProgramW6432 = $pf; 'ProgramFiles(x86)' = $pf }
}
# Copies one .cmd into $box with taskkill, schtasks and reg pointed at the stubs, next to a stub windows\build.cmd.
# It refuses a copy that still names one of the real tools.
function New-Copy([string]$box, [string]$script) {
    [IO.Directory]::CreateDirectory((Join-Path $box 'windows')) | Out-Null
    $t = [IO.File]::ReadAllText((Join-Path (Split-Path $win) $script))
    $t = $t -replace '\btaskkill\b', '"%HWW_T_STUBS%\taskkill.exe"' -replace '\bschtasks\b', '"%HWW_T_STUBS%\schtasks.exe"' -replace '\breg delete\b', '"%HWW_T_STUBS%\reg.exe" delete'
    $rest = $t.Replace('"%HWW_T_STUBS%\taskkill.exe"', '').Replace('"%HWW_T_STUBS%\schtasks.exe"', '').Replace('"%HWW_T_STUBS%\reg.exe"', '')
    if ($rest -match 'taskkill|schtasks|\breg\b') { throw "The copy of $script still names a real tool." }
    $copy = Join-Path $box $script
    [IO.File]::WriteAllText($copy, $t, [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $box 'windows\build.cmd'), "@echo off`r`n`"%HWW_T_STUBS%\build.exe`" `"%~1`"`r`nexit /b %errorlevel%`r`n", [Text.Encoding]::ASCII)
    $copy
}
# Stops a process this script started and every process started under it, found by parent id and start time. A
# leftover child (a ping in a wait loop) would otherwise keep this script's output open after it ends. An
# HWWidget.exe outside the scratch folder is never stopped.
function Stop-Tree([int]$id) {
    $all = @(Get-CimInstance Win32_Process)
    $tree = @($all | Where-Object { $_.ProcessId -eq $id })
    for ($i = 0; $i -lt $tree.Count; $i++) {
        $tree += @($all | Where-Object { $_.ParentProcessId -eq $tree[$i].ProcessId -and $_.CreationDate -ge $tree[$i].CreationDate })
    }
    foreach ($t in $tree) {
        if ($t.Name -eq 'HWWidget.exe' -and -not ("$($t.ExecutablePath)".StartsWith($root, 'OrdinalIgnoreCase'))) { continue }
        try { (Get-Process -Id $t.ProcessId -ErrorAction Stop).Kill() } catch { }
    }
}
# Runs a copy with the scenario's environment and no input, so pause returns at once. Returns its output, plus
# TIMEOUT when it had to be stopped (with everything it started).
function Invoke-Copy([string]$copy, [string]$arguments, [hashtable]$v, [int]$seconds = 60) {
    $out = Join-Path $v.HWW_T_STATE 'out.txt'
    $psi = New-Object Diagnostics.ProcessStartInfo $env:ComSpec, ('/d /v:off /c ""' + $copy + '" ' + $arguments + ' < nul > "' + $out + '" 2>&1"')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables.Remove('HWW_OUTSIDE')
    foreach ($k in $v.Keys) { $psi.EnvironmentVariables[$k] = [string]$v[$k] }
    $p = [Diagnostics.Process]::Start($psi)
    $timedOut = -not $p.WaitForExit($seconds * 1000)
    if ($timedOut) { Stop-Tree $p.Id; $p.WaitForExit() }
    $text = ''
    if (Test-Path -LiteralPath $out) {
        # After a stop, the copy's last child (a ping in its wait loop) can still hold the file for a second.
        $fs = New-Object IO.FileStream $out, 'Open', 'Read', 'ReadWrite, Delete'
        try { $text = (New-Object IO.StreamReader $fs).ReadToEnd() } finally { $fs.Dispose() }
    }
    if ($timedOut) { $text += "`r`nTIMEOUT after $seconds s" }
    $text
}
# A stub can be appending to the log at this moment, so it is read with sharing.
function Get-Log([hashtable]$v) {
    $f = Join-Path $v.HWW_T_STATE 'log.txt'
    if (-not (Test-Path -LiteralPath $f)) { return @() }
    $fs = New-Object IO.FileStream $f, 'Open', 'Read', 'ReadWrite, Delete'
    try { $text = (New-Object IO.StreamReader $fs, ([Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
    @($text -split "`r?`n" | Where-Object { $_ -ne '' })
}
# A restart uses start without /wait, so its log line can come a moment later.
function Wait-Log([hashtable]$v, [string]$line) {
    for ($i = 0; $i -lt 50; $i++) { if ((Get-Log $v) -contains $line) { return $true }; Start-Sleep -Milliseconds 100 }
    $false
}
function Started([hashtable]$v, [string]$exe) { @(Get-Log $v | Where-Object { $_.StartsWith("hwwidget|$exe|", 'OrdinalIgnoreCase') }).Count -gt 0 }
function CheckRun([string]$name, [bool]$ok, [string]$out, [hashtable]$v) {
    Check $name $ok
    if (-not $ok) { Write-Host ('    ' + ((($out.Trim() + "`n-- stub log:`n" + ((Get-Log $v) -join "`n")) -split "`r?`n") -join "`n    ")) }
}
$installerName = 'Install HW Widget (Windows).cmd'
$uninstallerName = 'Uninstall HW Widget (Windows).cmd'
$warnStartup = 'WARN HW Widget is running, but the Startup shortcut could not be created.'
$warnHelper = 'WARN CPU temperature helper not installed'
try {
    # TC-246 (QA-02): any exit code but 0, 4, 7 and 8 (a crash, a stopped exe) rolls back like a failed self-test.
    foreach ($rc in 2, -532462766) {
        $v = New-Scenario "rc$rc"
        $copy = New-Copy (Join-Path $v.HWW_T_STATE 'repo') $installerName
        $exe = Join-Path $v.LOCALAPPDATA 'Programs\HW Widget\HWWidget.exe'
        $oldDirExe = Join-Path $v.LOCALAPPDATA 'HWWidget\HWWidget.exe'
        Set-Old $exe; Set-Old $oldDirExe
        $v.HWW_T_INSTALL_RC = "$rc"
        $out = Invoke-Copy $copy '' $v
        $ok = ($out -match "exit code $rc") -and (Test-Old $exe) -and (Test-Old $oldDirExe) -and (Wait-Log $v "hwwidget|$exe|")
        CheckRun "TC-246 installer: exit code $rc puts the previous version back, restarts it and prints the code" $ok $out $v
    }
    # TC-246 (QA-03): after the switch to windows\bin, a failed self-test or swap restarts the previous version that
    # still sits in Programs\HW Widget (windows\bin has no previous copy to put back).
    foreach ($case in @(@('selftest', 'programs', 'Self-test failed'), @('swap', 'programs-nofile', 'Could not replace'))) {
        $v = New-Scenario "fallback-$($case[0])"
        $copy = New-Copy (Join-Path $v.HWW_T_STATE 'repo') $installerName
        $prev = Join-Path $v.LOCALAPPDATA 'Programs\HW Widget\HWWidget.exe'
        Set-Old $prev
        $v.HWW_T_BUILD = $case[1]; $v.HWW_T_INSTALL_RC = '3'
        $out = Invoke-Copy $copy '' $v
        $ok = ($out -match 'Installing into this folder instead') -and ($out -match $case[2]) -and (Test-Old $prev) -and (Wait-Log $v "hwwidget|$prev|")
        CheckRun "TC-246 installer: a failed $($case[0]) after the switch to windows\bin restarts the previous version" $ok $out $v
    }
    # TC-310 (QA-06): exit code 8 = Startup shortcut and helper both failed; the widget runs, so both warnings show
    # and the rollback copies go.
    $v = New-Scenario 'rc8'
    $copy = New-Copy (Join-Path $v.HWW_T_STATE 'repo') $installerName
    $exe = Join-Path $v.LOCALAPPDATA 'Programs\HW Widget\HWWidget.exe'
    $oldDirExe = Join-Path $v.LOCALAPPDATA 'HWWidget\HWWidget.exe'
    Set-Old $exe; Set-Old $oldDirExe
    $v.HWW_T_INSTALL_RC = '8'
    $out = Invoke-Copy $copy '--cpu-temp' $v
    $ok = $out.Contains($warnStartup) -and $out.Contains($warnHelper) -and ($out -notmatch 'ERROR') -and
        -not (Test-Path -LiteralPath (Join-Path $v.LOCALAPPDATA 'Programs\HW Widget\HWWidget.old.exe')) -and
        -not (Test-Path -LiteralPath $oldDirExe) -and (Test-Path -LiteralPath $exe) -and -not (Test-Old $exe)
    CheckRun 'TC-310 installer: exit code 8 shows both warnings and keeps the new version' $ok $out $v
    # TC-312 (QA-26): with --cpu-temp the install just put the new helper in place, so no "keeps its old version" note.
    foreach ($arg in '--cpu-temp', '--CPU-TEMP', '') {
        $v = New-Scenario "note$($arg.Length)$($arg -ceq '--CPU-TEMP')"
        $copy = New-Copy (Join-Path $v.HWW_T_STATE 'repo') $installerName
        [IO.Directory]::CreateDirectory((Join-Path $v.ProgramFiles 'HWWidget')) | Out-Null
        Set-Content -LiteralPath (Join-Path $v.ProgramFiles 'HWWidget\HWWidgetSensor.exe') 'x'
        $out = Invoke-Copy $copy $arg $v
        $note = $out -match 'keeps its old version'
        if ($arg) { CheckRun "TC-312 installer: no old-helper note after $arg" (($out -match 'OK   HW Widget installed') -and -not $note) $out $v }
        else { CheckRun 'TC-312 installer: the old-helper note still shows without --cpu-temp' (($out -match 'OK   HW Widget installed') -and $note) $out $v }
    }
    # TC-248 (QA-08): the install outside the app starts even on battery power (schtasks /Run /I).
    $v = New-Scenario 'battery'
    $copy = New-Copy (Join-Path $v.HWW_T_STATE 'repo') $installerName
    [IO.Directory]::CreateDirectory((Join-Path $v.LOCALAPPDATA 'Packages\Some.App_x\LocalCache\Local')) | Out-Null
    Set-Content -LiteralPath (Join-Path $v.LOCALAPPDATA 'Packages\Some.App_x\LocalCache\Local\HWWidget-sandbox-probe.tmp') 'x'
    $out = Invoke-Copy $copy '' $v
    $run = @(Get-Log $v | Where-Object { $_ -like 'schtasks|*|/Run|*' })
    CheckRun 'TC-248 installer: the outside-app install runs its task with /I' (($run.Count -eq 1) -and ($run[0] -like '*|/I|*') -and ($out -match 'OK   HW Widget installed')) $out $v
    # TC-250 (QA-10, QA-11): the Task Scheduler path works from folders named with &, ^, %41, ! and non-ASCII letters.
    # QA-10: schtasks gets the wrapper's exact path in /TR. QA-11: the wrapper holds no path (only ASCII, not the
    # folder name) and, started like the task, runs the installer with its arguments and writes the done marker.
    $i = 0
    foreach ($name in @("hw widget & co-$PID", "a^b-$PID", "p%41q-$PID", "a!b-$PID", ('M' + [char]0xFC + 'ller ' + [char]0x416 + "-$PID"))) {
        $i++
        $v = New-Scenario "tr$i"
        $box = Join-Path $v.HWW_T_STATE $name
        $copy = New-Copy $box $installerName
        [IO.Directory]::CreateDirectory((Join-Path $v.LOCALAPPDATA 'Packages\Some.App_x\LocalCache\Local')) | Out-Null
        Set-Content -LiteralPath (Join-Path $v.LOCALAPPDATA 'Packages\Some.App_x\LocalCache\Local\HWWidget-sandbox-probe.tmp') 'x'
        $out = Invoke-Copy $copy '--cpu-temp' $v
        $trFile = Join-Path $v.HWW_T_STATE 'tr.txt'
        $tr = if (Test-Path -LiteralPath $trFile) { [IO.File]::ReadAllText($trFile) } else { '' }
        $wrap = Join-Path $box 'windows\bin\install-outside.cmd'
        CheckRun "TC-250 installer: /TR names the wrapper exactly in folder '$name'" ($tr -ceq "`"$wrap`"") "/TR: $tr`n$out" $v
        $wrapFile = Join-Path $v.HWW_T_STATE 'wrapper.txt'
        [byte[]]$bytes = @()
        if (Test-Path -LiteralPath $wrapFile) { $bytes = [IO.File]::ReadAllBytes($wrapFile) }
        $wrapText = [Text.Encoding]::ASCII.GetString($bytes)
        $exe = Join-Path $v.LOCALAPPDATA 'Programs\HW Widget\HWWidget.exe'
        $ok = ($bytes.Length -gt 0) -and -not ($bytes | Where-Object { $_ -gt 127 }) -and -not $wrapText.Contains($name) -and
            ((Get-Log $v) -contains "hwwidget|$exe|--install|--cpu-temp") -and ($out -match 'OK   HW Widget installed') -and ($out -notmatch 'TIMEOUT')
        CheckRun "TC-250 installer: the wrapper holds no path and runs the install in folder '$name'" $ok "wrapper:`n$wrapText`n$out" $v
    }
    # TC-249 (QA-04): an --uninstall that did not finish (exit code other than 0 and 5) gets the manual cleanup.
    foreach ($rc in 2, 0) {
        $v = New-Scenario "un$rc"
        $copy = New-Copy (Join-Path $v.HWW_T_STATE 'repo') $uninstallerName
        Set-Old (Join-Path $v.LOCALAPPDATA 'Programs\HW Widget\HWWidget.exe')
        $lnk = Join-Path $v.APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\HW Widget.lnk'
        [IO.Directory]::CreateDirectory((Split-Path $lnk)) | Out-Null
        Set-Content -LiteralPath $lnk 'x'
        $v.HWW_T_UNINSTALL_RC = "$rc"
        $out = Invoke-Copy $copy '' $v
        $manual = @(Get-Log $v | Where-Object { $_ -like 'reg|*|delete|*\CurrentVersion\Run|/v|HWWidget|/f' }).Count -eq 1
        if ($rc) { CheckRun "TC-249 uninstaller: exit code $rc from --uninstall runs the manual cleanup" ($manual -and -not (Test-Path -LiteralPath $lnk)) $out $v }
        else { CheckRun 'TC-249 uninstaller: exit code 0 skips the manual cleanup' ((-not $manual) -and ($out -match 'OK   HW Widget removed')) $out $v }
    }
    # TC-249 (QA-04): a start Windows refuses ("Access is denied", as when an antivirus blocks the exe) also sets 5.
    # Without the helper's exe that 5 is not the widget's answer, so the manual cleanup runs.
    $v = New-Scenario 'un-denied'
    $copy = New-Copy (Join-Path $v.HWW_T_STATE 'repo') $uninstallerName
    $exe = Join-Path $v.LOCALAPPDATA 'Programs\HW Widget\HWWidget.exe'
    Set-Old $exe
    $acl = Get-Acl -LiteralPath $exe
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ([Security.Principal.WindowsIdentity]::GetCurrent().User), 'ExecuteFile', 'Deny'))
    Set-Acl -LiteralPath $exe $acl
    $lnk = Join-Path $v.APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\HW Widget.lnk'
    [IO.Directory]::CreateDirectory((Split-Path $lnk)) | Out-Null
    Set-Content -LiteralPath $lnk 'x'
    $out = Invoke-Copy $copy '' $v
    $manual = @(Get-Log $v | Where-Object { $_ -like 'reg|*|delete|*\CurrentVersion\Run|/v|HWWidget|/f' }).Count -eq 1
    CheckRun 'TC-249 uninstaller: a refused start (Access is denied) runs the manual cleanup' ((-not (Started $v $exe)) -and $manual -and
        -not (Test-Path -LiteralPath $lnk) -and ($out -match 'OK   HW Widget removed')) $out $v
    # The same refused start with the helper installed still removes the startup entries, then warns about the helper.
    $v = New-Scenario 'un-denied-helper'
    $copy = New-Copy (Join-Path $v.HWW_T_STATE 'repo') $uninstallerName
    $exe = Join-Path $v.LOCALAPPDATA 'Programs\HW Widget\HWWidget.exe'
    Set-Old $exe
    $acl = Get-Acl -LiteralPath $exe
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ([Security.Principal.WindowsIdentity]::GetCurrent().User), 'ExecuteFile', 'Deny'))
    Set-Acl -LiteralPath $exe $acl
    [IO.Directory]::CreateDirectory((Join-Path $v.ProgramFiles 'HWWidget')) | Out-Null
    Set-Content -LiteralPath (Join-Path $v.ProgramFiles 'HWWidget\HWWidgetSensor.exe') 'x'
    $lnk = Join-Path $v.APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\HW Widget.lnk'
    [IO.Directory]::CreateDirectory((Split-Path $lnk)) | Out-Null
    Set-Content -LiteralPath $lnk 'x'
    $out = Invoke-Copy $copy '' $v
    $manual = @(Get-Log $v | Where-Object { $_ -like 'reg|*|delete|*\CurrentVersion\Run|/v|HWWidget|/f' }).Count -eq 1
    CheckRun 'TC-249 uninstaller: a refused start with the helper installed still removes the startup entries and warns' ((-not (Started $v $exe)) -and $manual -and
        -not (Test-Path -LiteralPath $lnk) -and ($out -match 'WARN HW Widget removed, but the CPU temperature helper is still installed')) $out $v
    # TC-311 (QA-09): v1.0 and v1.1 in %LOCALAPPDATA%\HWWidget ignore --uninstall and would just open the widget, so
    # that exe never starts; a temporary build uninstalls instead (or the manual cleanup if the build fails), and the
    # old folder still goes.
    foreach ($build in '', 'fail') {
        $v = New-Scenario "unold$($build.Length)"
        $box = Join-Path $v.HWW_T_STATE 'repo'
        $copy = New-Copy $box $uninstallerName
        $old = Join-Path $v.LOCALAPPDATA 'HWWidget\HWWidget.exe'
        Set-Old $old
        $v.HWW_T_BUILD = $build
        $out = Invoke-Copy $copy '' $v
        $gone = -not (Test-Path -LiteralPath (Join-Path $v.LOCALAPPDATA 'HWWidget'))
        if ($build) {
            $manual = @(Get-Log $v | Where-Object { $_ -like 'reg|*|delete|*\CurrentVersion\Run|/v|HWWidget|/f' }).Count -eq 1
            CheckRun 'TC-311 uninstaller: with only an old-folder exe and a failed build, the manual cleanup runs' ((-not (Started $v $old)) -and $manual -and $gone) $out $v
        } else {
            $built = Join-Path $box 'windows\bin\HWWidget.exe'
            CheckRun 'TC-311 uninstaller: an old-folder exe never starts; a temporary build uninstalls' ((-not (Started $v $old)) -and ((Get-Log $v) -contains "hwwidget|$built|--uninstall") -and $gone) $out $v
        }
    }
} catch {
    # An error that stops these tests half-way must fail the run, not end it quietly with the checks so far.
    Check "behaviour tests on copies stopped early: $($_.Exception.Message)" $false
} finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "$fails failed"
exit $fails
