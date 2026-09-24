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
# This is the ONE recipe for all five bundled plugins, the item indexer included. Its source
# lives in its own folder outside Resources, but it is built here with the same compiler and the
# same reference set as the other four, so a game update can never leave one of them built
# against a different assembly set than the rest. Pass -SkipItemIndexer to leave it alone.
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
    [switch] $SkipItemIndexer,
    # Kept so an older command line still runs. The indexer is built by default now, so this
    # switch no longer changes anything.
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

# Source is a LIST, because two commands are each served by two plugins and there is one
# shared file behind each. BakaKillAllPlan.cs and BakaKillAllSweep.cs are compiled into
# Commander as well as into KillAll, and BakaSpawnMark.cs into Commander as well as into
# SpawnHelper, so whichever of a pair a host has installed answers the command the same way
# and neither needs the other to be there. A shared file can never be left out of one of a
# pair without this list saying so.
$plugins = @(
    @{ Dir = "Commander";   Source = @("BakaLoaderCommander.cs",
                                       "..\KillAll\BakaKillAllPlan.cs",
                                       "..\KillAll\BakaKillAllSweep.cs",
                                       "..\KillAll\BakaCleansePlan.cs",
                                       "..\KillAll\BakaCleanseSweep.cs",
                                       "..\SpawnHelper\BakaSpawnMark.cs"); Out = "BakaLoaderCommander.dll" },
    @{ Dir = "KillAll";     Source = @("BakaKillAll.cs",
                                       "BakaKillAllPlan.cs",
                                       "BakaKillAllSweep.cs",
                                       "BakaCleansePlan.cs",
                                       "BakaCleanseSweep.cs");           Out = "BakaKillAll.dll" },
    @{ Dir = "SpawnHelper"; Source = @("BakaLoaderSpawnHelper.cs",
                                       "BakaSpawnMark.cs");               Out = "BakaLoaderSpawnHelper.dll" },
    @{ Dir = "MaxPlayers";  Source = @("BakaLoaderMaxPlayers.cs");        Out = "BakaLoaderMaxPlayers.dll" }
)

if ($IncludeItemIndexer) {
    Write-Host "-IncludeItemIndexer is no longer needed: the item indexer is built by default."
}

if ($SkipItemIndexer) {
    Write-Host "Skipping the item indexer because -SkipItemIndexer was passed."
} else {
    # The item indexer's source lives outside Resources (it has its own project folder), but the
    # DLL is bundled under Resources\ItemIndexer just like the others, and it is built here so all
    # five come out of one compiler against one reference set.
    $plugins += @{ Dir = "ItemIndexer"; Source = @("..\..\..\BakaLoaderItemIndexer\Plugin.cs"); Out = "BakaLoaderItemIndexer.dll" }
}

$failed = @()

foreach ($p in $plugins) {
    $srcDir = Join-Path $root $p.Dir
    if (-not (Test-Path -LiteralPath $srcDir)) { New-Item -ItemType Directory -Path $srcDir | Out-Null }

    $sources = @()
    foreach ($s in @($p.Source)) {
        $source = Join-Path $srcDir $s
        Require-File $source "source for $($p.Dir)"
        $sources += (Resolve-Path -LiteralPath $source).Path
    }

    $target = if ($OutDir -ne "") { Join-Path $OutDir $p.Out } else { Join-Path $srcDir $p.Out }
    $targetDir = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Path $targetDir | Out-Null }

    # A response file keeps the reference list off the command line, which Windows would
    # otherwise truncate.
    $rsp = Join-Path $env:TEMP ("baka_" + $p.Dir + ".rsp")
    # /noconfig has to go on the command line: csc warns (CS2023) and ignores it inside a
    # response file, which would silently pull in the machine's default references.
    # VALHEIM_PLUGIN is what tells BakaKillAllSweep.cs it is being compiled by THIS script
    # rather than globbed into BakaLoader's own project. The sweep names Valheim's types, the
    # app has no game assemblies, and the file lives under Resources like every other plugin
    # source, so the fence is how the app keeps building. Define it for every plugin, not just
    # the two that use it: a plugin that grows a game-fenced file later must not have to
    # remember this.
    $lines = @(
        "/nostdlib+",
        "/target:library",
        "/optimize+",
        "/debug-",
        "/langversion:latest",
        "/warn:4",
        "/deterministic",
        "/utf8output",
        "/define:VALHEIM_PLUGIN",
        "/out:`"$target`""
    )
    $lines += ($refs | ForEach-Object { "/reference:`"$_`"" })
    $lines += ($sources | ForEach-Object { "`"$_`"" })
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
