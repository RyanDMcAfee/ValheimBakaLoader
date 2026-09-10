# Prove the bundled BepInEx companion plugins still fit the game they will run against.
#
# A plugin can compile cleanly, load cleanly, and still be broken: C# bakes optional
# arguments and constant values into the CALL SITE, and Harmony finds many targets by
# string name. A game update that inserts an optional parameter, turns a field into a
# constant, or renames a private method leaves a DLL that only fails when the affected
# code actually runs, which on a dedicated server can be days later and only for one
# command. This script reads the compiled DLLs with Mono.Cecil and checks every one of
# those seams against a real Managed folder before anyone ships them.
#
# What it checks, per DLL:
#   1. every member and type reference into the game or BepInEx still resolves
#   2. Terminal.ConsoleCommand constructor call sites resolve, and it prints the arity
#      (13 on Valheim 1.0, 12 before it)
#   3. nothing reads ZRoutedRpc.Everybody as a field (it is a constant from 1.0)
#   4. every [HarmonyPatch(typeof(X), "name")] and AccessTools lookup names a member
#      that exists
#   5. when BakaLoaderMaxPlayers is present, the constants its transpilers rewrite are
#      still in the game's own IL where the plugin looks for them, asked the way each
#      transpiler asks it: the admission cap is the first sbyte after the
#      GetNrOfPlayers() call and its value is then checked, and each lobby cap is the
#      first sbyte carrying 11
#
# It exits non-zero on any problem, so it can gate a release. Run it against the DLLs
# built by build-plugins.ps1, pointed at the same server install:
#
#   powershell -ExecutionPolicy Bypass -File verify-plugins.ps1 `
#       -PluginDir  "<a folder holding the built plugin DLLs>" `
#       -ManagedDir "D:\SteamLibrary\steamapps\common\Valheim dedicated server\valheim_server_Data\Managed" `
#       -CoreDir    "D:\SteamLibrary\steamapps\common\Valheim dedicated server\BepInEx\core"
#
# Sanity check for the gate itself: run it once against the PREVIOUS shipped DLLs. On
# Valheim 1.0 it must fail on the 12-argument constructor and the Everybody field read.

param(
  [Parameter(Mandatory=$true)][string]$PluginDir,
  [Parameter(Mandatory=$true)][string]$ManagedDir,
  [Parameter(Mandatory=$true)][string]$CoreDir
)
$ErrorActionPreference = "Stop"

function Require-File([string] $path, [string] $what) {
  if (-not (Test-Path -LiteralPath $path)) { throw "$what not found: $path" }
}

# A wrong ManagedDir or CoreDir silently changes what the resolver can see, and every
# reference then "resolves" against nothing. Refuse the run instead.
Require-File (Join-Path $ManagedDir "assembly_valheim.dll") "assembly_valheim.dll"
Require-File (Join-Path $CoreDir "BepInEx.dll") "BepInEx.dll"
Require-File (Join-Path $CoreDir "0Harmony.dll") "0Harmony.dll"

# Mono.Cecil ships inside BepInEx. Load a copy rather than the file in place, so this
# never holds a lock on a server folder.
$cecilSource = Join-Path $CoreDir "Mono.Cecil.dll"
if (-not (Test-Path -LiteralPath $cecilSource)) { throw "Mono.Cecil.dll not found in $CoreDir" }
$cecilDir = Join-Path $env:TEMP "baka-verify-cecil"
if (-not (Test-Path -LiteralPath $cecilDir)) { New-Item -ItemType Directory -Path $cecilDir | Out-Null }
$cecil = Join-Path $cecilDir "Mono.Cecil.dll"
# Once loaded, the copy is locked for the life of the process, so only refresh it when
# it is absent or stale. Running the script twice in one session must not fail here.
$needsCopy = -not (Test-Path -LiteralPath $cecil)
if (-not $needsCopy) {
  $needsCopy = (Get-Item -LiteralPath $cecil).Length -ne (Get-Item -LiteralPath $cecilSource).Length
}
if ($needsCopy) {
  try { Copy-Item -LiteralPath $cecilSource -Destination $cecil -Force }
  catch { if (-not (Test-Path -LiteralPath $cecil)) { throw } }
}
Add-Type -Path $cecil

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($ManagedDir)
$resolver.AddSearchDirectory($CoreDir)
$resolver.AddSearchDirectory($PluginDir)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver

$gameAsms = @("assembly_guiutils","assembly_googleanalytics","Unity.TextMeshPro","assembly_valheim","assembly_utils","Splatform","com.rlabrecque.steamworks.net","UnityEngine.CoreModule","UnityEngine","0Harmony","BepInEx")

$fail = 0
$examined = 0
$maxPlayersSeen = $false

# Finds a method with a body by type name and method name, ignoring namespaces (the game
# assembly has none for these) and nested types.
function Get-GameMethod($asm, [string] $typeName, [string] $methodName) {
  foreach ($t in $asm.MainModule.GetTypes()) {
    if ($t.Name -ne $typeName) { continue }
    foreach ($m in $t.Methods) {
      if ($m.Name -eq $methodName -and $m.HasBody) { return $m }
    }
  }
  return $null
}

# The index of the first ldc.i4.s at or after $from, or -1. This is the admission-cap
# transpiler's own rule: it takes the first sbyte after the GetNrOfPlayers() call and then
# checks the value, so the gate has to look the same way round.
function Find-SbyteConstant($instructions, [int] $from) {
  for ($i = $from; $i -lt $instructions.Count; $i++) {
    if ($instructions[$i].OpCode.Name -eq "ldc.i4.s") { return $i }
  }
  return -1
}

# The index of the first ldc.i4.s carrying exactly $value at or after $from, or -1. This is
# the lobby-cap transpiler's own rule (ReplaceLobbyCap skips any other sbyte on the way and
# rewrites the first one that is 11), and the gate has to ask the same question. Asking for
# the first sbyte of any value instead would fail a build the plugin still patches correctly
# the moment the game emits some other constant earlier in the method, and would pass a build
# where the cap has moved off 11 while an earlier constant happens to be 11.
function Find-SbyteConstantWithValue($instructions, [int] $from, [int] $value) {
  for ($i = $from; $i -lt $instructions.Count; $i++) {
    if ($instructions[$i].OpCode.Name -ne "ldc.i4.s") { continue }
    if ([int]$instructions[$i].Operand -eq $value) { return $i }
  }
  return -1
}

# -Recurse because the bundled DLLs live one level down (Resources\Commander\*.dll and so
# on), and a server's BepInEx\plugins is nested the same way. Pointing at either of those
# without it used to enumerate nothing and still report success.
foreach ($dll in (Get-ChildItem -LiteralPath $PluginDir -Filter *.dll -Recurse -File | Sort-Object FullName)) {
  $examined++
  if ($dll.Name -eq "BakaLoaderMaxPlayers.dll") { $maxPlayersSeen = $true }
  Write-Host ""
  Write-Host ("=== {0} ===" -f $dll.Name)
  $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll.FullName, $rp)

  # ---- 1. every member reference into a game/BepInEx assembly must still resolve ----
  $unresolved = New-Object System.Collections.Generic.List[string]
  $checked = 0
  foreach ($mr in $asm.MainModule.GetMemberReferences()) {
    $scope = $mr.DeclaringType.Scope.Name -replace '\.dll$',''
    if ($gameAsms -notcontains $scope) { continue }
    $checked++
    $r = $null
    try { $r = $mr.Resolve() } catch { $r = $null }
    if ($null -eq $r) { $unresolved.Add(("{0}  ->  {1}" -f $scope, $mr.FullName)) }
  }
  Write-Host ("  member refs into game/BepInEx assemblies: {0} checked, {1} unresolved" -f $checked, $unresolved.Count)
  foreach ($u in $unresolved) { Write-Host ("    UNRESOLVED  " + $u); $fail++ }

  # ---- 2. type references must resolve too ----
  $badTypes = New-Object System.Collections.Generic.List[string]
  foreach ($tr in $asm.MainModule.GetTypeReferences()) {
    $scope = $tr.Scope.Name -replace '\.dll$',''
    if ($gameAsms -notcontains $scope) { continue }
    $r = $null
    try { $r = $tr.Resolve() } catch { $r = $null }
    if ($null -eq $r) { $badTypes.Add(("{0} -> {1}" -f $scope, $tr.FullName)) }
  }
  Write-Host ("  type refs unresolved: {0}" -f $badTypes.Count)
  foreach ($b in $badTypes) { Write-Host ("    UNRESOLVED TYPE  " + $b); $fail++ }

  # ---- 3. ConsoleCommand ctor call sites: report the exact arity used ----
  foreach ($t in $asm.MainModule.GetTypes()) {
    foreach ($m in $t.Methods) {
      if (-not $m.HasBody) { continue }
      foreach ($i in $m.Body.Instructions) {
        $op = $i.Operand
        if ($op -is [Mono.Cecil.MethodReference] -and $op.DeclaringType.FullName -eq "Terminal/ConsoleCommand" -and $op.Name -eq ".ctor") {
          $res = $op.Resolve()
          Write-Host ("  ConsoleCommand ctor call in {0}.{1}: {2} args, resolves={3}" -f $t.Name, $m.Name, $op.Parameters.Count, ($null -ne $res))
          if ($null -eq $res) { $fail++ }
        }
        if ($op -is [Mono.Cecil.FieldReference] -and $op.Name -eq "Everybody") {
          Write-Host ("  FOUND field access to Everybody in {0}.{1} (BAD)" -f $t.Name, $m.Name)
          $fail++
        }
      }
    }
  }

  # ---- 4. Harmony string targets: [HarmonyPatch(typeof(X), "name")] + AccessTools.DeclaredMethod ----
  foreach ($t in $asm.MainModule.GetTypes()) {
    $attrOwners = @()
    $attrOwners += ,@($t.Name, $t.CustomAttributes)
    foreach ($mm in $t.Methods) { $attrOwners += ,@(($t.Name + "." + $mm.Name), $mm.CustomAttributes) }
    foreach ($pair in $attrOwners) {
      $ownerName = $pair[0]
      foreach ($ca in $pair[1]) {
        if ($ca.AttributeType.Name -ne "HarmonyPatch") { continue }
        $args = @($ca.ConstructorArguments)
        if ($args.Count -lt 2) { continue }
        if (-not ($args[0].Value -is [Mono.Cecil.TypeReference])) { continue }
        if (-not ($args[1].Value -is [string])) { continue }
        $td = $args[0].Value.Resolve()
        $name = [string]$args[1].Value
        $hit = $false
        if ($null -ne $td) { $hit = @($td.Methods | Where-Object { $_.Name -eq $name }).Count -gt 0 }
        Write-Host ("  HarmonyPatch(typeof({0}), `"{1}`") on {2}: {3}" -f $args[0].Value.Name, $name, $ownerName, $(if ($hit) {"OK"} else {"MISSING"}))
        if (-not $hit) { $fail++ }
      }
    }
    foreach ($m in $t.Methods) {
      if (-not $m.HasBody) { continue }
      $ins = @($m.Body.Instructions)
      for ($k = 0; $k -lt $ins.Count; $k++) {
        $op = $ins[$k].Operand
        if (-not ($op -is [Mono.Cecil.MethodReference])) { continue }
        if ($op.DeclaringType.Name -ne "AccessTools") { continue }
        if ($op.Name -notmatch "^Declared(Method|Field|PropertyGetter)$|^(Method|Field)$") { continue }
        # walk back for: ldtoken <type> ; ldstr <name>
        $typeRef = $null; $nameStr = $null
        for ($j = $k - 1; $j -ge 0 -and $j -ge $k - 8; $j--) {
          if ($ins[$j].OpCode.Name -eq "ldstr" -and $null -eq $nameStr) { $nameStr = [string]$ins[$j].Operand }
          if ($ins[$j].OpCode.Name -eq "ldtoken" -and $null -eq $typeRef -and $ins[$j].Operand -is [Mono.Cecil.TypeReference]) { $typeRef = $ins[$j].Operand }
        }
        if ($null -eq $typeRef -or $null -eq $nameStr) { continue }
        $td = $typeRef.Resolve()
        $hit = $false
        if ($null -ne $td) {
          $hit = (@($td.Methods | Where-Object { $_.Name -eq $nameStr }).Count -gt 0) -or (@($td.Fields | Where-Object { $_.Name -eq $nameStr }).Count -gt 0)
        }
        Write-Host ("  AccessTools.{0}(typeof({1}), `"{2}`") in {3}.{4}: {5}" -f $op.Name, $typeRef.Name, $nameStr, $t.Name, $m.Name, $(if ($hit) {"OK"} else {"MISSING"}))
        if (-not $hit) { $fail++ }
      }
    }
  }

  $asm.Dispose()
}

# ---- 5. the game constants the MaxPlayers transpilers rewrite must still be there ----
#
# Checks 1 to 4 all ask the same question: does this NAME still resolve. A hand-written
# transpiler asks a different one, because it assumes a SHAPE in the target method's IL.
# BakaLoaderMaxPlayers finds the admission cap by taking the first sbyte constant after the
# GetNrOfPlayers() call and rewrites it only when it holds the vanilla 10, and it finds each
# PlayFab lobby cap by taking the first sbyte 11. A game update can move those constants or
# change their values without renaming one member, which every check above would wave through
# while the plugin silently stops raising the cap. So read the real IL and confirm each
# constant is still exactly where the plugin will go looking for it.
if ($maxPlayersSeen) {
  Write-Host ""
  Write-Host "=== MaxPlayers transpiler targets in the game's own IL ==="
  $gameAsm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ManagedDir "assembly_valheim.dll"), $rp)
  try {
    # The admission cap: ZNet.RPC_PeerInfo, first sbyte constant after the call.
    $m = Get-GameMethod $gameAsm "ZNet" "RPC_PeerInfo"
    if ($null -eq $m) {
      Write-Host "  ZNet.RPC_PeerInfo: MISSING"
      $fail++
    } else {
      $ins = @($m.Body.Instructions)
      $callAt = -1
      for ($i = 0; $i -lt $ins.Count; $i++) {
        $op = $ins[$i].Operand
        if ($op -is [Mono.Cecil.MethodReference] -and $op.Name -eq "GetNrOfPlayers") { $callAt = $i; break }
      }
      if ($callAt -lt 0) {
        Write-Host "  ZNet.RPC_PeerInfo: no call to GetNrOfPlayers - the admission cap CANNOT be raised"
        $fail++
      } else {
        $capAt = Find-SbyteConstant $ins ($callAt + 1)
        if ($capAt -lt 0) {
          Write-Host "  ZNet.RPC_PeerInfo: no sbyte constant after GetNrOfPlayers - the admission cap CANNOT be raised"
          $fail++
        } else {
          # 10 is BakaLoaderMaxPlayers.VanillaAdmissionCap. The two have to agree: the plugin
          # refuses to rewrite anything else, so a change here without a change there would
          # pass this gate and then quietly leave the cap alone on a live server.
          $expected = 10
          $value = [int]$ins[$capAt].Operand
          $ok = ($value -eq $expected)
          Write-Host ("  ZNet.RPC_PeerInfo admission cap after GetNrOfPlayers(): {0} (expected {1}) {2}" -f $value, $expected, $(if ($ok) {"OK"} else {"MISMATCH"}))
          if (-not $ok) { $fail++ }
        }
      }
    }

    # The PlayFab lobby caps. 11 is what BakaLoaderMaxPlayers.ReplaceLobbyCap looks for:
    # vanilla's 10 plus the one slot the dedicated server itself takes. The search is by
    # value, exactly as the plugin searches, because CreateAndJoinNetwork already carries a
    # second sbyte (15, the peer connectivity options) and the order of the two is the game's
    # to change.
    $expectedLobby = 11
    foreach ($name in @("CreateLobby", "CreateAndJoinNetwork")) {
      $lm = Get-GameMethod $gameAsm "ZPlayFabMatchmaking" $name
      if ($null -eq $lm) {
        Write-Host ("  ZPlayFabMatchmaking.{0}: MISSING" -f $name)
        $fail++
        continue
      }
      $lins = @($lm.Body.Instructions)
      $at = Find-SbyteConstantWithValue $lins 0 $expectedLobby
      if ($at -lt 0) {
        Write-Host ("  ZPlayFabMatchmaking.{0}: no ldc.i4.s {1} in the method, so the lobby cap CANNOT be raised" -f $name, $expectedLobby)
        $fail++
        continue
      }
      Write-Host ("  ZPlayFabMatchmaking.{0} lobby cap: {1} found at instruction {2} OK" -f $name, $expectedLobby, $at)
    }
  }
  finally {
    $gameAsm.Dispose()
  }
}

Write-Host ""

# A gate that checked nothing must never read as a pass. This is the whole reason the
# script exists: exit 0 has to mean "these DLLs were inspected and they fit".
if ($examined -eq 0) {
  Write-Host ("VERIFY FAILED: no DLLs found under {0}" -f $PluginDir)
  exit 1
}

if ($fail -gt 0) { Write-Host ("VERIFY FAILED: {0} problem(s) in {1} assembly(ies)" -f $fail, $examined); exit 1 }
Write-Host ("VERIFY OK: {0} assembly(ies) examined; every game member reference, ConsoleCommand ctor and Harmony string target resolves against the supplied Managed folder, and every constant the transpilers rewrite is still in place." -f $examined)
exit 0
