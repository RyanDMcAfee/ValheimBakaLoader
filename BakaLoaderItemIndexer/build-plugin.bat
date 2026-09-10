@echo off
REM ============================================================================
REM  Shim. The item indexer used to have a build recipe of its own here (an
REM  MSBuild project with its own reference list) alongside the one in
REM  ValheimBakaLoader\Resources\build-plugins.ps1. Two recipes for one DLL meant
REM  whichever ran last silently decided what shipped, and the two did not
REM  reference the same assemblies. There is now one recipe, and this file
REM  forwards to it.
REM
REM  build-plugins.ps1 builds all five bundled plugins, the item indexer
REM  included, straight into ValheimBakaLoader\Resources\<plugin>\, so no copy
REM  step is needed afterwards.
REM
REM  Requires: a Valheim Dedicated Server install that already has BepInEx, plus
REM            the Roslyn csc.exe from Visual Studio Build Tools.
REM
REM  Usage:
REM     build-plugin.bat "D:\SteamLibrary\steamapps\common\Valheim dedicated server"
REM     build-plugin.bat "<server folder>" "<full path to csc.exe>"
REM ============================================================================

setlocal

set "VALHEIM_INSTALL=%~1"
if "%VALHEIM_INSTALL%"=="" set "VALHEIM_INSTALL=C:\SteamLibrary\steamapps\common\Valheim dedicated server"

set "BUILDER=%~dp0..\ValheimBakaLoader\Resources\build-plugins.ps1"

echo Using Valheim install: "%VALHEIM_INSTALL%"
echo Forwarding to: "%BUILDER%"

if not exist "%BUILDER%" (
  echo.
  echo ERROR: build-plugins.ps1 not found at "%BUILDER%".
  exit /b 1
)

if not exist "%VALHEIM_INSTALL%\valheim_server_Data\Managed\assembly_valheim.dll" (
  echo.
  echo ERROR: Could not find assembly_valheim.dll under "%VALHEIM_INSTALL%".
  echo Pass the correct server path as the first argument, e.g.:
  echo    build-plugin.bat "D:\Steam\steamapps\common\Valheim dedicated server"
  exit /b 1
)

if not exist "%VALHEIM_INSTALL%\BepInEx\core\BepInEx.dll" (
  echo.
  echo ERROR: BepInEx is not installed under "%VALHEIM_INSTALL%\BepInEx\core".
  echo Install BepInEx on the dedicated server first, then re-run this script.
  exit /b 1
)

if "%~2"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%BUILDER%" -ManagedDir "%VALHEIM_INSTALL%\valheim_server_Data\Managed" -CoreDir "%VALHEIM_INSTALL%\BepInEx\core"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%BUILDER%" -ManagedDir "%VALHEIM_INSTALL%\valheim_server_Data\Managed" -CoreDir "%VALHEIM_INSTALL%\BepInEx\core" -Csc "%~2"
)

if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

echo.
echo Done. The plugins are in ValheimBakaLoader\Resources\.
echo Now run ValheimBakaLoader\Resources\verify-plugins.ps1 against the same
echo server folder, then rebuild BakaLoader so the DLLs are bundled.
endlocal
