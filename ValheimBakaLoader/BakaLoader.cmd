@echo off
rem Starts BakaLoader without its launcher, for when an antivirus has taken
rem ValheimBakaLoader.exe out of this folder. The app is ValheimBakaLoader.dll beside this
rem file, and the dotnet host Microsoft signs runs it exactly as the launcher does.
rem dotnet opens a console window; BakaLoader closes it as soon as it starts.
setlocal
set "HERE=%~dp0"
set "DOTNET="
if defined ProgramW6432 if exist "%ProgramW6432%\dotnet\dotnet.exe" set "DOTNET=%ProgramW6432%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET for /f "delims=" %%D in ('where dotnet.exe 2^>nul') do if not defined DOTNET set "DOTNET=%%D"
if not defined DOTNET goto nodotnet
"%DOTNET%" --list-runtimes 2>nul | findstr /b /c:"Microsoft.WindowsDesktop.App 6." >nul || goto nodotnet
start "" /d "%HERE%." "%DOTNET%" "%HERE%ValheimBakaLoader.dll"
exit /b 0

:nodotnet
echo BakaLoader needs the .NET 6 Desktop Runtime to start this way: https://dotnet.microsoft.com/download/dotnet/6.0
pause
exit /b 1
