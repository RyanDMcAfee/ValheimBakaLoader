@echo off
REM ============================================================================
REM  Build the BakaLoaderItemIndexer companion plugin and copy the DLL into the
REM  main BakaLoader app's bundled Resources so it can auto-install it onto the
REM  Valheim server's BepInEx/plugins folder.
REM
REM  Requires: .NET SDK + a Valheim Dedicated Server install that already has
REM            BepInEx installed (the plugin references the game + BepInEx DLLs).
REM
REM  Usage:
REM     build-plugin.bat "D:\SteamLibrary\steamapps\common\Valheim dedicated server"
REM
REM  If you omit the path, set VALHEIM_INSTALL below to your server folder.
REM ============================================================================

setlocal

set "VALHEIM_INSTALL=%~1"
if "%VALHEIM_INSTALL%"=="" set "VALHEIM_INSTALL=C:\SteamLibrary\steamapps\common\Valheim dedicated server"

echo Using Valheim install: "%VALHEIM_INSTALL%"

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

dotnet build "%~dp0BakaLoaderItemIndexer.csproj" -c Release -p:VALHEIM_INSTALL="%VALHEIM_INSTALL%"
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

set "OUTDLL=%~dp0bin\Release\BakaLoaderItemIndexer.dll"
set "DESTDIR=%~dp0..\ValheimBakaLoader\Resources\ItemIndexer"

if not exist "%OUTDLL%" (
  echo ERROR: expected output not found: "%OUTDLL%"
  exit /b 1
)

if not exist "%DESTDIR%" mkdir "%DESTDIR%"
copy /Y "%OUTDLL%" "%DESTDIR%\BakaLoaderItemIndexer.dll"

echo.
echo Done. Plugin copied to: %DESTDIR%\BakaLoaderItemIndexer.dll
echo Rebuild BakaLoader so the DLL is bundled, then start your server once to
echo generate BepInEx\items.json.
endlocal
