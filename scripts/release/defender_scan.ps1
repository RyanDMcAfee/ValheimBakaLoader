# Ask Windows Defender what it thinks of a release BEFORE it goes out, without letting it act.
#
#   powershell -NoProfile -File scripts/release/defender_scan.ps1 <file or folder> [more...]
#   bash scripts/release/defender_scan.sh BuildStaging1212 "$ZIP"
#
# WHY IT EXISTS. On 2026-10-11 Defender's cloud model named the frozen launcher
# Trojan:Win32/Bearfoos.B!ml during the 1.2.11 self update, took it out of the live install
# every time it came back, and the app and the server stayed down. Nothing in the release
# recipe had asked Defender anything first. This asks.
#
# WHAT IT DOES. Runs Defender's own command line scanner, MpCmdRun.exe, as a custom scan
# (-ScanType 3) of each path with -DisableRemediation. With that switch Defender takes no
# action on what it finds, writes no event, shows nothing in Windows Security, ignores the
# machine's file exclusions (so an exclusion on the build box cannot hide a detection) and
# looks inside zips. It changes no Defender setting and quarantines nothing. It prints one
# line per file, CLEAN or FLAGGED with the threat name, then DEFENDER GATE: PASS or FAIL.
#
# WHAT IT CANNOT SEE. A !ml verdict like Bearfoos is often scored from what a file DOES or
# where it came from (a download, a script that writes an exe and starts it), and a static
# scan of the same bytes can come back clean. So this also reads Defender's detection
# history on this machine (read only) and prints a HISTORY line for any scanned file whose
# name Defender has flagged before. -FailOnHistory turns those lines into a failure.
#
# WHEN IT IS NOT SURE. Each scan gets -TimeoutSeconds (15 minutes by default); a scan that
# runs past it is stopped along with anything it started. That, anything on the scanner's
# stderr without a detection, a reply it cannot read, and a folder with no files in it all
# count as scan errors, so the gate fails instead of passing on something it never saw.
#
# Exit codes: 0 nothing flagged, 1 something flagged, 2 the scan itself failed.
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)][string[]]$Path,
    [switch]$FailOnHistory,
    [string]$Scanner = (Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'),
    [ValidateRange(1, 86400)][int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'

if (-not $Path -or $Path.Count -eq 0) {
    Write-Output 'usage: defender_scan.ps1 <file or folder> [more...] [-FailOnHistory] [-TimeoutSeconds 900]'
    exit 2
}

# Stops a process and everything it started, children first, so a stuck scan cannot leave a
# grandchild holding the output pipes open.
function Stop-Tree([int]$Id) {
    foreach ($c in @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $Id" -ErrorAction SilentlyContinue)) {
        Stop-Tree ([int]$c.ProcessId)
    }
    Stop-Process -Id $Id -Force -ErrorAction SilentlyContinue
}

# Runs the scanner without PowerShell's native command handling: under 5.1 a stderr line from
# '& exe 2>&1' with ErrorActionPreference Stop is a terminating error, which used to crash the
# gate with exit 1 (the flagged code) and no summary line. stdout and stderr come back apart.
function Invoke-Scanner([string]$Exe, [string]$Target, [int]$Seconds) {
    # A trailing backslash would escape the closing quote ("C:\" reads as C:").
    $arg = if ($Target.EndsWith('\')) { $Target + '\' } else { $Target }
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = '-Scan -ScanType 3 -File "' + $arg + '" -DisableRemediation'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $proc = [Diagnostics.Process]::Start($psi)
    $so = $proc.StandardOutput.ReadToEndAsync()
    $se = $proc.StandardError.ReadToEndAsync()
    $timedOut = -not $proc.WaitForExit($Seconds * 1000)
    if ($timedOut) {
        Stop-Tree $proc.Id
        [void]$proc.WaitForExit(10000)
    } else {
        $proc.WaitForExit()  # lets the redirected streams finish
    }
    [void][Threading.Tasks.Task]::WaitAll(@($so, $se), 10000)
    $split = { param($t) if ($t.IsCompleted -and -not $t.IsFaulted) { @($t.Result -split '\r?\n' | Where-Object { $_.Trim() }) } else { @() } }
    [pscustomobject]@{
        Out      = @(& $split $so)
        Err      = @(& $split $se)
        Code     = if ($timedOut) { $null } else { $proc.ExitCode }
        TimedOut = $timedOut
    }
}
if (-not (Test-Path -LiteralPath $Scanner)) {
    Write-Output "ERROR    Defender's scanner is not at $Scanner, so nothing was checked"
    Write-Output 'DEFENDER GATE: FAIL'
    exit 2
}

$flagged = 0; $errors = 0; $history = 0; $scanned = @()

foreach ($p in $Path) {
    if (-not (Test-Path -LiteralPath $p)) {
        Write-Output "ERROR    $p does not exist"
        $errors++; continue
    }
    $full = (Resolve-Path -LiteralPath $p).ProviderPath
    $files = if (Test-Path -LiteralPath $full -PathType Container) {
        @(Get-ChildItem -LiteralPath $full -Recurse -File -Force | ForEach-Object { $_.FullName } | Sort-Object)
    } else { @($full) }
    if ($files.Count -eq 0) {
        Write-Output "ERROR    $full has no files in it, so there is nothing to check (was the build copied?)"
        $errors++; continue
    }

    $run = Invoke-Scanner $Scanner $full $TimeoutSeconds
    $out = $run.Out; $code = $run.Code
    if ($run.TimedOut) {
        Write-Output "ERROR    $full did not finish scanning within $TimeoutSeconds second(s), so the scan was stopped and the result is unknown"
        @($run.Out + $run.Err) | ForEach-Object { Write-Output "         $_" }
        $errors++; continue
    }

    # The detection list reads "Threat : <name>" followed by "file : <path>" or
    # "containerfile : <zip>" lines, with "zip->inner" for a file found inside an archive.
    $hits = @(); $threat = $null
    foreach ($line in $out) {
        if ($line -match '^\s*Threat\s*:\s*(.+?)\s*$') { $threat = $Matches[1]; continue }
        if ($threat -and $line -match '^\s*(file|containerfile)\s*:\s*(.+?)\s*$') {
            $hits += [pscustomobject]@{ Threat = $threat; Resource = $Matches[2] }
        }
    }
    $foundNone = [bool]($out | Where-Object { $_ -match 'found no threats' })

    if ($code -ne 0 -and $hits.Count -eq 0) {
        Write-Output "ERROR    $full could not be scanned (MpCmdRun exit $code):"
        @($out + $run.Err) | ForEach-Object { Write-Output "         $_" }
        $errors++; continue
    }
    if ($run.Err.Count -gt 0 -and $hits.Count -eq 0) {
        Write-Output "ERROR    $full scanned, but Defender's scanner also wrote to stderr, so its clean answer is not trusted:"
        @($out + $run.Err) | ForEach-Object { Write-Output "         $_" }
        $errors++; continue
    }
    if (-not $foundNone -and $hits.Count -eq 0) {
        Write-Output "ERROR    $full scanned but Defender's reply was not understood:"
        $out | ForEach-Object { Write-Output "         $_" }
        $errors++; continue
    }
    # A detection stands whatever else the scanner said; show the rest so nothing is hidden.
    $run.Err | ForEach-Object { Write-Output "NOTE     scanner stderr: $_" }

    $matched = @{}
    foreach ($f in $files) {
        $scanned += $f
        $mine = @($hits | Where-Object {
            ($_.Resource -split '->')[0].Trim() -ieq $f
        })
        if ($mine.Count -gt 0) {
            $names = ($mine | ForEach-Object { $_.Threat } | Select-Object -Unique) -join ', '
            $inner = ($mine | ForEach-Object { $_.Resource } | Where-Object { $_ -match '->' } | Select-Object -Unique) -join ', '
            $tail = if ($inner) { "  (inside: $inner)" } else { '' }
            Write-Output "FLAGGED  $f  $names$tail"
            $mine | ForEach-Object { $matched[$_.Resource] = $true }
            $flagged++
        } else {
            Write-Output "CLEAN    $f"
        }
    }
    # A detection that names a path this run did not list still counts.
    foreach ($h in $hits) {
        if (-not $matched.ContainsKey($h.Resource)) {
            Write-Output "FLAGGED  $($h.Resource)  $($h.Threat)"
            $matched[$h.Resource] = $true; $flagged++
        }
    }
}

# Read only: what Defender has already said about files with these names on this machine.
$past = @()
try {
    $threatNames = @{}
    foreach ($t in @(Get-MpThreat -ErrorAction Stop)) { $threatNames[[string]$t.ThreatID] = $t.ThreatName }
    foreach ($d in @(Get-MpThreatDetection -ErrorAction Stop)) {
        foreach ($r in @($d.Resources)) {
            $leaf = Split-Path -Leaf ((($r -replace '^[a-z]+:_', '') -split '\|')[0] -replace '^.*->', '')
            $past += [pscustomobject]@{ Leaf = $leaf; When = $d.InitialDetectionTime; Name = $threatNames[[string]$d.ThreatID] }
        }
    }
} catch {
    Write-Output "NOTE     Defender's detection history could not be read: $($_.Exception.Message)"
}
foreach ($leaf in ($scanned | ForEach-Object { Split-Path -Leaf $_ } | Select-Object -Unique)) {
    # One detection lists a zip, the exe inside it and the download it came from, so count
    # detections, not the resources inside them.
    $seen = @($past | Where-Object { $_.Leaf -ieq $leaf } | Sort-Object When -Descending -Unique)
    if ($seen.Count -gt 0) {
        $last = $seen[0]
        Write-Output ("HISTORY  {0} was flagged on this PC {1} time(s) before, last {2:yyyy-MM-dd HH:mm} as {3}. A clean static scan does not clear a verdict Defender scored from behaviour." -f $leaf, $seen.Count, $last.When, $last.Name)
        $history++
    }
}

foreach ($f in ($scanned | Where-Object { $_ -match '\.exe$' })) {
    $sig = (Get-AuthenticodeSignature -LiteralPath $f).Status
    if ($sig -ne 'Valid') { Write-Output "NOTE     $(Split-Path -Leaf $f) is not code signed ($sig), the one thing that would settle these verdicts for good" }
}

if ($scanned.Count -eq 0 -and $flagged -eq 0 -and $errors -eq 0) {
    Write-Output 'ERROR    no file was checked, so this run cannot vouch for anything'
    $errors++
}

$fail = $flagged -gt 0 -or $errors -gt 0 -or ($FailOnHistory -and $history -gt 0)
Write-Output ("{0} file(s) checked, {1} flagged, {2} with past detections, {3} scan error(s)" -f $scanned.Count, $flagged, $history, $errors)
$note = if ($history -gt 0 -and -not $fail) { ' (the scan is clean; read the HISTORY lines before shipping)' } else { '' }
Write-Output ('DEFENDER GATE: ' + $(if ($fail) { 'FAIL' } else { 'PASS' }) + $note)
if ($errors -gt 0) { exit 2 }
if ($fail) { exit 1 }
exit 0
