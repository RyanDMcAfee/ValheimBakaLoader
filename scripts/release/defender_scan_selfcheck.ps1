# A self check for defender_scan.ps1 that needs no test framework. Run it from anywhere:
#
#   powershell -NoProfile -File scripts/release/defender_scan_selfcheck.ps1
#
# 1. The real scanner over the repo's README must say CLEAN and PASS and exit 0.
# 2. A path that does not exist must exit non zero, never PASS.
# 3. A stand in scanner that answers with a detection, in the layout MpCmdRun prints, must
#    give FLAGGED with the threat name and exit 1. Nothing is written that Defender would flag:
#    the stand in only prints text, into a temp folder that is removed afterwards.
# 4. A stand in that writes to stderr and exits 1 must give ERROR, the summary line and exit 2
#    (exit 1 means flagged, and a broken scanner is not a detection).
# 5. A stand in that writes a harmless warning to stderr, then "found no threats" and exits 0,
#    must still give ERROR and exit 2: stderr with no detection is a reply nobody checked.
# 6. A stand in that never finishes, run with a short -TimeoutSeconds, must be stopped, give
#    ERROR and exit 2 well before it would have ended, and leave no process behind.
# 7. An empty folder must give ERROR and exit 2, never PASS on nothing.
$gate = Join-Path $PSScriptRoot 'defender_scan.ps1'
$readme = (Resolve-Path (Join-Path $PSScriptRoot '..\..\README.md')).ProviderPath
$bad = 0
function Check($name, $ok) {
    Write-Output ("{0}  {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name)
    if (-not $ok) { $script:bad++ }
}

$out = & powershell -NoProfile -File $gate $readme | Out-String; $code = $LASTEXITCODE
Check 'a clean file exits 0' ($code -eq 0)
Check 'a clean file prints CLEAN and PASS' ($out -match "CLEAN\s+.*README\.md" -and $out -match 'DEFENDER GATE: PASS')

$out = & powershell -NoProfile -File $gate (Join-Path $env:TEMP 'no-such-baka-file.bin') | Out-String; $code = $LASTEXITCODE
Check 'a missing path exits non zero' ($code -ne 0 -and $out -match 'DEFENDER GATE: FAIL')

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('baka-defender-selfcheck-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $target = Join-Path $tmp 'Sample.exe'
    [IO.File]::WriteAllText($target, 'not a program')
    $fake = Join-Path $tmp 'FakeMpCmdRun.cmd'
    $lines = @(
        '@echo off', 'echo Scan starting...', 'echo Scan finished.',
        "echo Scanning $tmp found 1 threats.",
        'echo ^<===========================LIST OF DETECTED THREATS==========================^>',
        'echo ----------------------------- Threat information ------------------------------',
        'echo Threat                  : Trojan:Win32/Bearfoos.B!ml',
        'echo Resources               : 1 total',
        "echo     file                : $target",
        'exit /b 2')
    [IO.File]::WriteAllLines($fake, $lines)
    $out = & powershell -NoProfile -File $gate $tmp -Scanner $fake | Out-String; $code = $LASTEXITCODE
    Check 'a detection exits 1' ($code -eq 1)
    Check 'a detection prints FLAGGED with the threat name' ($out -match 'FLAGGED\s+.*Sample\.exe\s+Trojan:Win32/Bearfoos\.B!ml' -and $out -match 'DEFENDER GATE: FAIL')

    $fake = Join-Path $tmp 'BrokenMpCmdRun.cmd'
    [IO.File]::WriteAllLines($fake, @('@echo off', 'echo boom 1>&2', 'exit /b 1'))
    $out = & powershell -NoProfile -File $gate $tmp -Scanner $fake | Out-String; $code = $LASTEXITCODE
    Check 'stderr and exit 1 from the scanner exits 2, not 1' ($code -eq 2)
    Check 'stderr and exit 1 prints ERROR with the stderr text and the gate line' ($out -match 'ERROR\s+' -and $out -match 'boom' -and $out -match 'DEFENDER GATE: FAIL')

    $fake = Join-Path $tmp 'NoisyMpCmdRun.cmd'
    [IO.File]::WriteAllLines($fake, @('@echo off', 'echo warning: signatures are a day old 1>&2', "echo Scanning $tmp found no threats.", 'exit /b 0'))
    $out = & powershell -NoProfile -File $gate $tmp -Scanner $fake | Out-String; $code = $LASTEXITCODE
    Check 'stderr then a clean reply exits 2' ($code -eq 2)
    Check 'stderr then a clean reply prints ERROR and the gate line' ($out -match 'ERROR\s+' -and $out -match 'signatures are a day old' -and $out -match 'DEFENDER GATE: FAIL')

    # ping stands in for a scan that never ends; the odd count makes its command line ours alone.
    $fake = Join-Path $tmp 'StuckMpCmdRun.cmd'
    [IO.File]::WriteAllLines($fake, @('@echo off', 'echo Scan starting...', 'ping -n 47 127.0.0.1 >nul', "echo Scanning $tmp found no threats.", 'exit /b 0'))
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $out = & powershell -NoProfile -File $gate $tmp -Scanner $fake -TimeoutSeconds 3 | Out-String; $code = $LASTEXITCODE
    $watch.Stop()
    $left = @(Get-CimInstance Win32_Process -Filter "Name = 'PING.EXE'" | Where-Object { $_.CommandLine -match '-n 47 127\.0\.0\.1' })
    Check 'a stuck scanner exits 2' ($code -eq 2)
    Check 'a stuck scanner prints ERROR and the gate line' ($out -match 'ERROR\s+.*did not finish' -and $out -match 'DEFENDER GATE: FAIL')
    Check ('a stuck scanner is stopped at the timeout ({0:N1}s)' -f $watch.Elapsed.TotalSeconds) ($watch.Elapsed.TotalSeconds -lt 30)
    Check 'a stuck scanner leaves no process behind' ($left.Count -eq 0)
    $left | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    $empty = Join-Path $tmp 'empty'
    New-Item -ItemType Directory -Path $empty | Out-Null
    $out = & powershell -NoProfile -File $gate $empty | Out-String; $code = $LASTEXITCODE
    Check 'an empty folder exits 2' ($code -eq 2)
    Check 'an empty folder prints ERROR and FAIL, never PASS' ($out -match 'ERROR\s+' -and $out -match 'DEFENDER GATE: FAIL' -and $out -notmatch 'DEFENDER GATE: PASS')
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force
}

Write-Output $(if ($bad) { "SELF CHECK: FAIL ($bad)" } else { 'SELF CHECK: PASS' })
exit $(if ($bad) { 1 } else { 0 })
