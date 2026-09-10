# Build the bundled BepInEx companion plugins against a real Valheim Dedicated Server install.
#
# The plugins are plain Mono class libraries compiled with the Roslyn csc.exe that ships with
# Visual Studio Build Tools. They are NOT part of ValheimBakaLoader.sln because they reference
# the game's own assemblies, which only exist on a machine that has the dedicated server.
#
# Every argument the game's Terminal.ConsoleCommand constructor takes is optional, and C# bakes
# the defaults in at the CALL SITE. That is why a game update which inserts a new optional
# parameter (Valheim 1.0 added hideBehindDevCommands at index 8) breaks a shipped DLL with
# MissingMethodException even though the source never changed. The cure is always: recompile
# against the new Managed folder, then check the emitted IL.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build-plugins.ps1 `
#       -ManagedDir "D:\SteamLibrary\steamapps\common\Valheim dedicated server\valheim_server_Data\Managed" `
#       -CoreDir    "D:\SteamLibrary\steamapps\common\Valheim dedicated server\BepInEx\core"
#
# Add -OutDir to write the DLLs somewhere else than next to their sources (useful for a dry run
# that must not touch the bundled binaries).
#
# Building is only half of it. verify-plugins.ps1 in this folder reads the finished DLLs
# and checks every call into the game still resolves; run it before shipping anything.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ManagedDir,
    [Parameter(Mandatory = $true)][string] $CoreDir,
    [string] $Csc = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
    [string] $OutDir = "",
    [switch] $IncludeItemIndexer
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Require-File([string] $path, [string] $what) {
    if (-not (Test-Path -LiteralPath $path)) { throw "$what not found: $path" }
}

Require-File $Csc "Roslyn csc.exe"
Require-File (Join-Path $ManagedDir "assembly_valheim.dll") "assembly_valheim.dll"
Require-File (Join-Path $CoreDir "BepInEx.dll") "BepInEx.dll"

# Reference every managed assembly the game ships plus BepInEx and Harmony. Over-referencing is
# harmless for csc and saves chasing one more UnityEngine module every time a plugin grows.
$refs = @()
$refs += Get-ChildItem -LiteralPath $ManagedDir -Filter *.dll | ForEach-Object { $_.FullName }
$refs += (Join-Path $CoreDir "BepInEx.dll")
$refs += (Join-Path $CoreDir "0Harmony.dll")

$plugins = @(
    @{ Dir = "Commander";   Source = "BakaLoaderCommander.cs";     Out = "BakaLoaderCommander.dll" },
    @{ Dir = "KillAll";     Source = "BakaKillAll.cs";             Out = "BakaKillAll.dll" },
    @{ Dir = "SpawnHelper"; Source = "BakaLoaderSpawnHelper.cs";   Out = "BakaLoaderSpawnHelper.dll" },
    @{ Dir = "MaxPlayers";  Source = "BakaLoaderMaxPlayers.cs";    Out = "BakaLoaderMaxPlayers.dll" }
)

if ($IncludeItemIndexer) {
    # The item indexer's source lives outside Resources (it has its own project folder), but the
    # DLL is bundled under Resources\ItemIndexer just like the others.
    $plugins += @{ Dir = "ItemIndexer"; Source = "..\..\..\BakaLoaderItemIndexer\Plugin.cs"; Out = "BakaLoaderItemIndexer.dll" }
}

$failed = @()

foreach ($p in $plugins) {
    $srcDir = Join-Path $root $p.Dir
    if (-not (Test-Path -LiteralPath $srcDir)) { New-Item -ItemType Directory -Path $srcDir | Out-Null }

    $source = Join-Path $srcDir $p.Source
    Require-File $source "source for $($p.Dir)"

    $target = if ($OutDir -ne "") { Join-Path $OutDir $p.Out } else { Join-Path $srcDir $p.Out }
    $targetDir = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Path $targetDir | Out-Null }

    # A response file keeps the reference list off the command line, which Windows would
    # otherwise truncate.
    $rsp = Join-Path $env:TEMP ("baka_" + $p.Dir + ".rsp")
    # /noconfig has to go on the command line: csc warns (CS2023) and ignores it inside a
    # response file, which would silently pull in the machine's default references.
    $lines = @(
        "/nostdlib+",
        "/target:library",
        "/optimize+",
        "/debug-",
        "/langversion:latest",
        "/warn:4",
        "/deterministic",
        "/utf8output",
        "/out:`"$target`""
    )
    $lines += ($refs | ForEach-Object { "/reference:`"$_`"" })
    $lines += "`"$((Resolve-Path -LiteralPath $source).Path)`""
    Set-Content -LiteralPath $rsp -Value $lines -Encoding utf8

    Write-Host "Building $($p.Out) ..."
    & $Csc /noconfig "@$rsp"
    if ($LASTEXITCODE -ne 0) {
        $failed += $p.Out
        Write-Host "  FAILED (csc exit $LASTEXITCODE)"
    } else {
        $info = Get-Item -LiteralPath $target
        $md5 = (Get-FileHash -LiteralPath $target -Algorithm MD5).Hash.ToLower()
        Write-Host ("  ok  {0} bytes  md5 {1}" -f $info.Length, $md5)
    }
}

if ($failed.Count -gt 0) {
    throw ("Build failed for: " + ($failed -join ", "))
}

Write-Host "All plugins built."
Write-Host ""
Write-Host "Now run verify-plugins.ps1 against these DLLs and the same server folder."
Write-Host "A clean compile does not prove the plugins fit the game they will run on."
