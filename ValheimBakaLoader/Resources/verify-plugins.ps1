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

# -Recurse because the bundled DLLs live one level down (Resources\Commander\*.dll and so
# on), and a server's BepInEx\plugins is nested the same way. Pointing at either of those
# without it used to enumerate nothing and still report success.
foreach ($dll in (Get-ChildItem -LiteralPath $PluginDir -Filter *.dll -Recurse -File | Sort-Object FullName)) {
  $examined++
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

Write-Host ""

# A gate that checked nothing must never read as a pass. This is the whole reason the
# script exists: exit 0 has to mean "these DLLs were inspected and they fit".
if ($examined -eq 0) {
  Write-Host ("VERIFY FAILED: no DLLs found under {0}" -f $PluginDir)
  exit 1
}

if ($fail -gt 0) { Write-Host ("VERIFY FAILED: {0} problem(s) in {1} assembly(ies)" -f $fail, $examined); exit 1 }
Write-Host ("VERIFY OK: {0} assembly(ies) examined; every game member reference, ConsoleCommand ctor and Harmony string target resolves against the supplied Managed folder." -f $examined)
exit 0
