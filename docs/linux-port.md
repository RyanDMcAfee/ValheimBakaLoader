# BakaLoader on Linux: a plan (1.2.18)

Planning only, no port code. Every file:line here is repo-relative and was read from the tree
at 1.2.17 (the C# side has not moved since, apart from the 1.2.18 config pairing and the
section preference, neither of which touches a Windows seam). Counts leave out
`ValheimBakaLoader.Tests/` unless they say otherwise. Written after the 1.2.18 motion sweep and
sidebar work, so the page it describes is the one with five halls and folding sections.

README.md:115 says it today: "No Linux build and no Docker image. It is a Windows app built
around the Windows server binary." This is what it would take to change that sentence.

## 1. Summary

- About 72k lines of C# (`ValheimBakaLoader` + `ValheimBakaLoader.Tools`) and a 17.7k line page
  (`WebUI/app.js`). The game logic (profiles, worlds, mods, BepInEx, players, RCON, Discord,
  Atlas) is plain .NET and mostly ports as is.
- The Windows surface is concentrated: the window and bridge (WinForms + WebView2), the
  registry (Steam path, Run key), process control (job object, taskkill), NTFS junctions and
  hard links, steamcmd.exe with an Authenticode check, the self-updater and frozen launcher,
  System.Drawing in the Atlas renderer, and hard-coded Windows paths.
- The page talks to the host through one seam, `window.chrome.webview` (app.js:3-33): 134 RPC
  methods, 36 push events, 6 window messages. A WebSocket can replace it without touching the
  rest of the page.
- Recommendation: shape (b), a headless service (ASP.NET Core Kestrel under systemd) serving
  the same WebUI to the host's browser behind a login and HTTPS. Most Linux Valheim hosts are
  VPSes with no desktop. Rough size: 9 to 16 weeks for one developer.
- Retarget to .NET 10 in the same move. The app is net6.0-windows (ValheimBakaLoader.csproj:5);
  .NET 6 is out of support and .NET 8 support ends in November 2026.

## 2. Windows dependencies, grouped

### 2.1 Window and UI host (WinForms + WebView2)

- ValheimBakaLoader.csproj:5-6 `net6.0-windows`, `UseWindowsForms`; :107 `Microsoft.Web.WebView2`.
- Program.cs:25-33, 67: `[STAThread]`, `SetHighDpiMode`, `Application.Run(SplashForm)`.
- Forms/BlendWindow.cs:559-609: creates the WebView2 environment, maps virtual hosts
  `app.baka`, `atlas.baka`, `lang.baka` to folders, hooks `WebMessageReceived`.
- Forms/BlendWindow.cs:764 `OnWebMessageReceived` (dispatch), :1014 `PostWebMessageAsJson`.
- Forms/BlendWindow.cs:841-952 `win.*` title bar handling; :204-208 user32 `ReleaseCapture` and
  `SendMessage(WM_NCLBUTTONDOWN)`; Tools/Theming/ThemeManager.cs:22-35 dwmapi dark title bar.
- WinForms is referenced in 11 source files: Program.cs, Forms/BlendWindow.cs,
  Forms/BlendWindow.Bridge.cs, Forms/SplashForm*.cs, Tools/ExceptionHandler.cs,
  Tools/StartupHelper.cs, Tools/ManagedUpdater.cs, Tools/Theming/ThemeManager.cs,
  Tools/FormExtensions.cs, Tools/FormProvider.cs.

The bridge is not its own class: `Forms/BlendWindow.Bridge.cs` (12,591 lines) is a partial of
the Form, and its header (Bridge.cs:22-27) says handlers run on the UI thread "so
WinForms-affine services are safe to touch directly". That hidden serialization is a real cost
(5.1).

Native dialogs raised by the host:

- `TaskDialog`: Bridge.cs:923-940 (another BakaLoader owns the server), :1131 (close held during
  an update), :1176-1193 (close with vikings online).
- `MessageBox`: Bridge.cs:947 (adopt or kill an orphan server), BlendWindow.cs:550 (WebView2
  failed), SplashForm.cs:495 (.NET too old), Tools/ExceptionHandler.cs:46.
- `OpenFileDialog` / `FolderBrowserDialog`: Bridge.cs:8634, 8658, via `RunOnUiThread` (:10378).

Linux: (b) replaces all of it with Kestrel plus the user's browser, (a) with Photino.NET. Title
bar and DWM code is dropped. Host dialogs become page modals (4.2).

### 2.2 Registry

| Where | What | Linux |
|---|---|---|
| Bridge.cs:11569-11570 | `HKCU\Software\Valve\Steam\SteamPath` for setup detection | probe `~/.steam/steam`, `~/.local/share/Steam`, Flatpak `~/.var/app/com.valvesoftware.Steam/.local/share/Steam`, Snap `~/snap/steam/common/.local/share/Steam` |
| Tools/ServerBuildTracker.cs:529-530 | same key, "is this a Steam client library" | same probe list |
| Tools/RunKeyRegistry.cs:1, 67, 99, 145-146 | reads HKCU and HKLM Run key, writes HKCU | systemd unit (2.4) |
| Tools/StartupHelper.cs:375-694 | Start with Windows switch, 14 registry calls | same |

Nothing else. `Registry.TryAdd` in ValheimBakaLoader.Tools/Processes/ProcessRegistry.cs:44 is a
ConcurrentDictionary named Registry.

### 2.3 Windows Defender

No Defender API call and no exclusion anywhere in the app (no `MpPreference`, no
`ExclusionPath`). Defender appears only as release tooling, scripts/release/defender_scan.ps1
(`MpCmdRun.exe -ScanType 3 -DisableRemediation`), defender_scan.sh:15 (via `cygpath`),
defender_scan_selfcheck.ps1, and as history in comments (Program.cs:174, Tools/LauncherRoute.cs:30,
Tools/StartupHelper.cs:97, Tools/ManagedUpdater.cs:18-26, Bridge.cs:1027). Nothing to port.

### 2.4 Start with Windows

The Upkeep switch writes the HKCU Run key (StartupHelper.cs:568, 635) and reasons about
`conhost.exe` (:144). The page has a selftest (`scripts/ui/start_with_windows_selftest.js`) and
the catalog has the `windows.note.*` sentences.

Linux: a systemd unit. For a system service (`User=valheim`) the switch becomes status plus
the command to run, since enabling needs root. For a user unit (`systemctl --user enable` plus
`loginctl enable-linger valheim`) the service can flip it itself. The system unit is the
common VPS shape.

### 2.5 NTFS junctions, reparse points, hard links

"junction" has 61 hits and `ReparsePoint` 23 across 11 source files. The core is per-realm
install isolation:

- Tools/InstallIsolationService.cs:819-833 makes junctions with `cmd.exe /c mklink /J`.
- InstallIsolationService.cs:733, 856-858 hard-links loose files with kernel32 `CreateHardLink`,
  guarded by `SameVolume` (:717) which compares `Path.GetPathRoot`, i.e. drive letters.
- InstallIsolationService.cs:607-637 `DeleteTreeJunctionSafe` unlinks reparse points with a
  non-recursive `Directory.Delete` and never follows them.
- Readers that skip reparse points: Bridge.cs:9300-9304, Forms/ConfigFiles.cs:76-169,
  Tools/BepInExService.cs:2827-2832, Tools/PackagePlacement.cs:128, 162, Tools/ModSwitch.cs:120,
  Tools/ModRemovalService.cs:172, Tools/CharacterCopy.cs:253, Tools/GaleProfileConfigs.cs:245,
  Tools/ModRemovalGate.cs:134 (`ResolveLinkTarget`).

Linux:

- Junction becomes `Directory.CreateSymbolicLink`; no shell-out.
- Hard link becomes libc `link()` via P/Invoke, falling back to copy on `EXDEV`. `SameVolume`
  must go: every Linux path root is `/`, so it always says "same volume".
- .NET reports `FileAttributes.ReparsePoint` for symlinks on Unix, so the skip checks hold.
  Must prove with a test that a non-recursive `Directory.Delete` on a directory symlink removes
  only the link on Linux; if not, use `File.Delete` on the link. A mistake here deletes the
  shared 1.5 GB game data.
- Confirm Unity finds a symlinked `valheim_server_Data` beside a hard-linked binary.

### 2.6 Self-updater and frozen launcher

- ValheimBakaLoader.csproj:24 runs `post-publish.bat` (signtool with a cert thumbprint).
- ValheimBakaLoader.csproj:27-97 freezes `Resources/host/ValheimBakaLoader.exe` (the 1.2.6
  apphost) over the SDK's, so Defender never sees a new hash; :85-86 pins it to net6.0-windows.
- ValheimBakaLoader/BakaLoader.cmd: fallback start through `dotnet.exe`; Program.cs:160,
  168-169 calls kernel32 `FreeConsole` for that route. Tools/LauncherRoute.cs:48, 54, 57 name
  `ValheimBakaLoader.exe`, `BakaLoader.cmd`, `dotnet.exe`.
- Tools/ManagedUpdater.cs: `--apply-update`, relies on Windows letting a mapped image be
  renamed (:28-32), shows a WinForms `UpdaterWindow` (:1060).
- Tools/AppUpdateService.cs:343-353, 750-752 picks `ValheimBakaLoader-<version>-win-x64.zip`.

Linux: none of the launcher, signtool or .cmd applies. Ship a `linux-x64` tarball (tar keeps
the exec bit, zip does not). Self-update for a service: unpack into `versions/<ver>/`, check
SHA256SUMS (supported since 1.2.13), swap a `current` symlink, exit, let systemd restart it.
No locked-image dance is needed. The refusal while a server runs (`SelfUpdateRefusal`,
Bridge.cs:4416) carries over. Add a switch to turn self-update off for package or container
installs.

### 2.7 Scripts (.bat, .cmd, PowerShell)

- BakaLoader.cmd and post-publish.bat: drop on Linux.
- BakaLoaderItemIndexer/build-plugin.bat:54-56 and Resources/build-plugins.ps1:33 (hard-coded
  `C:\Program Files (x86)\...\Roslyn\csc.exe`), Resources/verify-plugins.ps1: build tooling
  for the plugins. The DLLs are platform-neutral, so keep building them on Windows, with an
  optional SDK-style project for `dotnet build` anywhere.
- scripts/release/defender_scan.*: stays a Windows release gate.

The app starts no PowerShell at runtime (removed in 1.2.13, ManagedUpdater.cs:18-26).

### 2.8 Process and console handling

| Where | What | Linux |
|---|---|---|
| ValheimBakaLoader.Tools/Processes/ChildProcessTracker.cs:22-79 | kernel32 job object, KILL_ON_JOB_CLOSE, servers die with the app | systemd cgroup (`KillMode=mixed`) plus a graceful stop on SIGTERM. `PR_SET_PDEATHSIG` is unreliable from .NET (fires when the forking thread exits) |
| ProcessRegistry.cs:128-131 | `taskkill /pid N` without /F so the server saves | libc `kill(pid, SIGINT)`; the Linux server saves and quits on Ctrl-C. Check SIGTERM too |
| Game/ValheimServer.cs:1204, 1242; Tools/BepInExService.cs:2374-2380 | `GetProcessesByName("valheim_server")` + `MainModule` | binary is `valheim_server.x86_64`; match `/proc/<pid>/exe`, since the kernel truncates names to 15 chars |
| Bridge.cs:1005-1013 | finds another BakaLoader by process name | `flock` on a lock file |
| ValheimServer.cs:1816-1824 | `PriorityClass = AboveNormal` | needs CAP_SYS_NICE (already caught); `Nice=-5` in the unit |
| Program.cs:160, 168 | `FreeConsole` | drop |
| ValheimBakaLoader.Tools/TextRepair.cs:288, 296 | kernel32 `GetACP` | already guarded by `IsOSPlatform(Windows)`; no change |

All 10 DllImport lines in shipped source: kernel32 FreeConsole (Program.cs:168), user32 x2
(BlendWindow.cs:204-208), wintrust WinVerifyTrust (Tools/SteamCmdRunner.cs:430), dwmapi
(ThemeManager.cs:22), kernel32 CreateHardLink (InstallIsolationService.cs:856), kernel32 GetACP
(TextRepair.cs:296), kernel32 job object x3 (ChildProcessTracker.cs:72-79). No `AttachConsole`,
`GenerateConsoleCtrlEvent`, WMI, named pipes, mutexes, `runas` or `WindowsIdentity` in shipped
source. Server commands go over RCON to the bundled Commander plugin on loopback TCP, which is
portable.

### 2.9 Paths

- Tools/AppDataRoot.cs:41, 46: `%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader` and
  `...\LocalLow\IronGate\Valheim`; :68-69 build the same from `SpecialFolder.UserProfile`.
  "LocalLow" has 28 hits. The `BAKALOADER_DATA_DIR`, `BAKALOADER_VALHEIM_SAVE_DIR`,
  `BAKALOADER_UPDATE_DIR` overrides there are already the right seam for a service.
- Properties/Resources.cs:26 default server path under `%ProgramFiles(x86)%\Steam`; :39-42
  build paths with `+ @"\logs"` and the like.
- `valheim_server.exe` constants: Tools/PathCheck.cs:107, ServerBuildTracker.cs:125; extension
  check Game/ValheimPathExtensions.cs:15; suffix check Bridge.cs:8921. 23 source hits, 146 in tests.
- Setup detection, Bridge.cs:11549-11603: registry, `libraryfolders.vdf`, then every fixed
  drive for `Program Files (x86)\Steam`, `SteamLibrary`, `Steam`, `Games\Steam`.
- SteamCmdRunner.cs:261 `TrimEnd('\\')`; ServerBuildTracker.cs `SamePath` trims `'\\'` and
  ignores case.
- About 59 verbatim literals in shipped code hold backslash paths; most become `Path.Combine`.
- 441 `OrdinalIgnoreCase` uses. Many are path or folder-name compares (`BepInEx`, `plugins`,
  `worlds_local`) that can merge two distinct folders on a case-sensitive file system. Each
  path compare needs a look; no blanket change.
- The page: WebUI/app.js:15353, 15449-15450 Windows example paths, 17430 mock data.
  WebUI/i18n/en.json has about 50 strings naming Windows, Explorer or `valheim_server.exe`.

| Thing | Windows today | Linux |
|---|---|---|
| BakaLoader data | `...\LocalLow\BakaLoader\ValheimBakaLoader` | `~/.local/share/bakaloader`, or `/var/lib/bakaloader` for the system service |
| Cache, steamcmd, update work | `%LOCALAPPDATA%` | `~/.cache/bakaloader` (`LocalApplicationData` maps to `~/.local/share`) |
| Valheim saves | `...\LocalLow\IronGate\Valheim` | `~/.config/unity3d/IronGate/Valheim`; BakaLoader passes `-savedir`, so per-realm folders work the same |
| Server binary | `valheim_server.exe` | `valheim_server.x86_64` |

### 2.10 steamcmd

- SteamCmdRunner.cs:154-156 fetches `steamcmd.zip`, expects only `steamcmd.exe`; :319, 400-431
  refuse it unless `WinVerifyTrust` says Valve signed it; :196 keeps it under LocalAppData.
- SteamCmdRunner.cs:261-262: `+force_install_dir "<dir>" +login anonymous +app_update 896660 validate +quit`.
- Tools/ServerUpdateService.cs:191-194 and Bridge.cs:8547: `steam://validate/896660`,
  `steam://install/896660`, `steam://open/downloads` for client-owned installs.

Linux: `steamcmd_linux.tar.gz` (`steamcmd.sh` + `linux32/`, needs i386 `lib32gcc-s1`) or the
distro `steamcmd` package. No Authenticode equivalent, so trust is HTTPS from Valve's CDN or
the distro package. Add `+@sSteamCmdForcePlatformType linux` so a Linux host never gets the
Windows depot; keep `+force_install_dir` before `+login`. Hide `steam://` links on a headless
box and tell a host with a client-owned install to update through the client or move to steamcmd.

### 2.11 Everything else

- **System.Drawing**: Tools/Atlas/MapRenderer.cs:98-125 writes map PNGs with `Bitmap`,
  `LockBits`, `ImageFormat.Png`. Windows-only from .NET 6, throws from .NET 7. Replace with a
  small PNG encoder (`ZLibStream` + CRC32, about 60 lines) or SkiaSharp.
- **DPAPI / ProtectedData**: not used. Server password and Discord webhook URLs sit in JSON
  prefs. On Linux make the data folder 0700 and files 0600.
- **Firewall (netsh)**: not used; BakaLoader never opened ports. Document UDP 2456-2457 and the
  web UI's TCP port; `net.diagnose` can report what it sees.
- **Tray icon**: none. **DeviceId** (Tools/AssemblyHelper.cs:102-104, MAC + machine name):
  works on Linux, hash differs.
- **BepInEx loader file**: BepInExService.cs:671-678 allow-lists only `winhttp.dll`,
  `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`, and calls doorstop_libs and the
  shell scripts "Linux and macOS only". "Installed" means `winhttp.dll` is present
  (BepInExService.cs:258-265, BepInExLoadCheck.cs:28, BepInExUnattended.cs:59, 88,
  InstallIsolationService.cs:547). See 3.2.
- **Working directory**: `AddBackgroundProcess` (ProcessRegistry.cs:90-110) sets none. Windows
  does not care; Linux does, because `./linux64` and doorstop paths are relative.
- **Tests**: ValheimBakaLoader.Tests is net6.0-windows, 224 .cs files, full of Windows paths
  (`valheim_server.exe` 146, junction 59, LocalLow 41, cmd.exe 16). The UI selftests already
  run on ubuntu-latest (.github/workflows/default.yml:38); the xunit job is windows-latest (:15).

## 3. The Valheim side on Linux

### 3.1 Server binary and launch

App 896660 has a Linux depot: `valheim_server.x86_64`, `valheim_server_Data/`,
`linux64/steamclient.so`, `start_server.sh`. Starting the binary directly, BakaLoader must do
what the script does: set `LD_LIBRARY_PATH=./linux64:$LD_LIBRARY_PATH`, set `SteamAppId=892970`
(already done, ValheimServer.cs:1802), and run from the install folder. Arguments stay as they
are (ValheimServer.cs:3615-3629). Stop is SIGINT. The output comes from the same Unity code,
but every line parser in ValheimServer.cs (joins, deaths, saves, invite code) needs one real
Linux run to confirm.

### 3.2 BepInEx on Linux

`denikson-BepInExPack_Valheim` ships `doorstop_libs/libdoorstop_x64.so`,
`start_server_bepinex.sh` and `start_game_bepinex.sh` beside `BepInEx/`. No `winhttp.dll`
proxy on Linux; the loader goes in through `LD_PRELOAD`. The server script sets:

- Doorstop 3: `DOORSTOP_ENABLE=TRUE`, `DOORSTOP_INVOKE_DLL_PATH=./BepInEx/core/BepInEx.Preloader.dll`.
- Doorstop 4: `DOORSTOP_ENABLED=1`, `DOORSTOP_TARGET_ASSEMBLY=./BepInEx/core/BepInEx.Preloader.dll`.
- Both: `LD_LIBRARY_PATH=./doorstop_libs:./linux64:$LD_LIBRARY_PATH`, `LD_PRELOAD=libdoorstop_x64.so:$LD_PRELOAD`.

Plan: do not run the pack's script (it bakes server arguments into the file). Set the
environment on `ProcessStartInfo`, picking doorstop 3 or 4 from `.doorstop_version`, which
Tools/BepInExDoorstop.cs already reads (:28-31, 187). On Linux, `RootFileAllowList` gains
`doorstop_libs/` and drops `winhttp.dll`; "Installed" becomes `BepInEx/core/BepInEx.dll` plus
`doorstop_libs/libdoorstop_x64.so`. Config stays in `BepInEx/config`, plugins in
`BepInEx/plugins`. Mods that ship `bepinex/plugins` or `Plugins` work on Windows by accident;
Tools/PackagePlacement.cs must match archive entries ignoring case but write exact names.

### 3.3 Saves, config, Steam layout

- Saves: `~/.config/unity3d/IronGate/Valheim`, worlds in `worlds_local/*.db` and `*.fwl`,
  `adminlist.txt`, `bannedlist.txt`, `permittedlist.txt` beside them. With `-savedir` BakaLoader
  owns this; a service puts realms under `/var/lib/bakaloader/realms/<name>` so one folder
  backs up everything. `worlds_local` (26 source hits) needs no change beyond the root.
- Client libraries: `~/.steam/steam` usually links to `~/.local/share/Steam`; libraries in
  `steamapps/libraryfolders.vdf` and `config/libraryfolders.vdf` (same VDF, forward slashes);
  server at `<library>/steamapps/common/Valheim dedicated server/valheim_server.x86_64` with
  `steamapps/appmanifest_896660.acf`. ServerBuildTracker's two layout rules (:284-330) hold
  once the names are right.
- steamcmd installs keep `steamapps/appmanifest_896660.acf` inside the install, as on Windows.
  A headless VPS is almost always steamcmd.

### 3.4 Bundled server plugins

Under `ValheimBakaLoader/Resources/`: Commander (61 KB), ItemIndexer (10 KB), KillAll (31 KB),
MaxPlayers (12 KB), SpawnHelper (15 KB), plus the client-only Uncheat (11 KB). I read every
.cs source beside them and `BakaLoaderItemIndexer/Plugin.cs`:

- No `DllImport`, `System.Windows`, `Microsoft.Win32`, registry, `Process` use, or backslash
  path literals.
- Commander (BakaLoaderCommander.cs:118, 170, 246-263) runs a `TcpListener` RCON server on
  loopback by default; portable under Mono.
- ItemIndexer (Plugin.cs:83-112) writes `items.json` with `Path.Combine(Paths.BepInExRootPath, ...)`
  and `File.Replace`, which Mono supports on Linux.

They should load on the Linux server as they are: one proof run, no rebuild. The installers
(Tools/*Installer.cs) only copy into `BepInEx/plugins` and port with the path work.

## 4. Two shapes

### 4.1 How the page talks to the host

`WebUI/app.js:3-33` builds `Native` on `window.chrome?.webview`: `call(method, params)` posts
`{id, method, params}` and resolves on `{id, ok, result, error, errorId, errorParams}`;
`post()` is fire-and-forget (only `win.*`); `on(event, fn)` takes `{event, data}`;
`available` is false with no host, which drives the mock mode. `rpc()` (app.js:408) wraps
`Native.call` and is used about 150 times, plus 34 direct calls. The host injects
`window.BAKA_VERSION` and `window.BAKA_LANG` before the document exists and builds image and
font URLs on `https://atlas.baka/` (Bridge.cs:6605, 6707) and `https://lang.baka/`
(Bridge.cs:10485, 10504).

- **134 RPC methods** (Bridge.cs 122, BlendWindow.Gale.cs 8, GaleConfigs.cs 2, TestCopy.cs 2).
  mods 15, server 12, players 11, gale 10, profiles 9, shell 7, servers 6, bepinex 6, worlds 5,
  setup 5, lang 5, paths 4, discord 4, and 22 smaller ones.
- **36 push events**, every one listened to: 34 app events (`server.status`, `servers.changed`,
  `log.server`, `log.app`, `server.updateProgress`, `bepinex.progress`, `mods.updateProgress`,
  `atlas.renderProgress`, ...) and 2 window events (`win.state`, `window.active`).
- **6 window messages**: `win.minimize`, `win.maximize`, `win.close`, `win.dragStart`,
  `win.dragRestore`, `win.resizeStart`.

### 4.2 What reaches the desktop, and its web replacement

| Method | Today | Web |
|---|---|---|
| shell.open (Bridge.cs:8505) | opens saveData, serverDir, config, plugins, logs, appData in Explorer | return the path, page shows it with a copy button; config files are already editable via `config.read` / `config.write` |
| shell.openUrl (:8540), openAppRelease (:8569), openHexium (:8588), openThunderstore (:8607) | `Process.Start` with UseShellExecute | host still builds the URL from a closed keyword and returns it; page calls `window.open`. Hide `steam://open/downloads` on Linux |
| shell.pickFile (:8626), shell.pickFolder (:8650) | Windows dialogs | server-side folder browser: new `fs.list` RPC, one level at a time, rendered as a modal |
| Host TaskDialog / MessageBox (2.1) | orphan, close guards, other instance | `host.ask` push event answered by a `host.answer` RPC, or fixed service policy (adopt orphans, no window to close) |
| Clipboard, 9 sites (app.js:3737, 4075, 8608, 8950, 12280, 13211, 14853, ...) | `navigator.clipboard.writeText` | works on HTTPS and `http://localhost`, fails on plain HTTP over a LAN; add an `execCommand('copy')` fallback |

### 4.3 WebSocket transport

Same frames, different pipe. The service exposes authenticated `GET /ws`; page to host
`{id, method, params}`, host to page `{id, ok, ...}` or `{event, data}`. In app.js, `Native`
uses `window.chrome?.webview` when present and otherwise a WebSocket adapter with the same
`addEventListener('message')` / `postMessage` shape, about 40 lines at the top of the file.
Add reconnect with backoff; on reconnect reject pending calls and re-run the initial loads
(`servers.list`, `app.updateStatus`, the active profile). Add `Native.windowed` (false in a
browser) to hide the title bar and skip `win.*`; derive `window.active` from
`visibilitychange`. Serve `BAKA_VERSION` and `BAKA_LANG` by rewriting `index.html` or from a
`/boot.js` loaded first. Make the atlas and lang URL prefixes a host setting (`/atlas/`,
`/lang/` on the service, static file middleware on the same folders). Events go to every open
socket; RPCs are serialized (5.1).

### 4.4 Shape (a): desktop window

Photino.NET (WebKitGTK) is the realistic option; Avalonia has no free first-party Linux webview
today. Cost: 2 to 3 weeks on top of the shared work, since Photino's
`window.external.sendMessage` / `receiveMessage` and custom scheme handlers map onto the
bridge. Gains: closest to the Windows app, GTK pickers. Problems: needs a desktop session that
most hosts lack; WebKitGTK is not Chromium and the page has only ever been tested in WebView2;
custom-scheme secure-context and clipboard rules differ; distros ship different WebKitGTK ABIs.
Serves Linux desktop users hosting on their own PC, a small group.

### 4.5 Shape (b): headless service (recommended)

Kestrel in the same process as the game logic, run by systemd, serving `WebUI/`. Cost: 3 to 5
weeks on top of the shared work.

- Login: one admin account. Password from `bakaloader passwd`, or a one-time setup token in
  the journal on first start. Cookie auth (HttpOnly, Secure, SameSite=Strict), idle expiry,
  login rate limit. The WebSocket upgrade needs the cookie and a matching `Origin`.
- HTTPS: default bind `127.0.0.1` and an SSH tunnel, which needs no certificate. A public bind
  requires TLS: a self-signed cert made on first run (fingerprint printed), a supplied cert and
  key, or a documented Caddy / nginx reverse proxy with Let's Encrypt.
- A login is full control of the service account. The surface already has `config.write`,
  `server.command`, `mods.addFromUrl`, and `profiles.save` with a free `ServerExePath` the app
  then runs. Run as an unprivileged `valheim` user, refuse any `ServerExePath` not named
  `valheim_server.x86_64`, never bind publicly without TLS and a password.
- Unit sketch: `User=valheim`, `WorkingDirectory=/opt/bakaloader/current`,
  `Environment=BAKALOADER_DATA_DIR=/var/lib/bakaloader`, `KillMode=mixed`, `TimeoutStopSec=180`,
  `Restart=on-failure`, `Nice=-5`. On SIGTERM, SIGINT each server, wait for the save line, exit.
- Bonus: the same host could run on Windows behind a switch for remote management.

### 4.6 Recommendation

Build (b). It fits where Linux servers run, the page needs only the transport shim and the
desktop replacements above, and the groundwork (host-agnostic bridge, platform seam) is what
(a) would need first anyway. Revisit (a) if Linux desktop users ask; by then it is a Photino
window on the same bridge.

## 5. Plan

### 5.1 Phases

1. **Retarget and split** (1 to 2 weeks). .NET 10. Split `ValheimBakaLoader.Core` (`net10.0`, no
   WinForms) from a thin `net10.0-windows` WinForms + WebView2 host. The frozen launcher is
   pinned to net6.0-windows (csproj:85-86), so it must be regenerated, re-frozen and run
   through the Defender gate on purpose.
2. **Bridge extraction** (1 to 2 weeks). Move the 134 handlers out of the `BlendWindow` partial
   into a `BridgeHost` owning `RegisterRpc`, `PostEvent` and dispatch, with an `IHostUi` seam
   for pickers, open-folder, open-URL and prompts. Replace the implicit UI-thread
   serialization with an explicit single-reader channel, so handlers still run one at a time
   when two tabs send at once.
3. **Platform seam** (2 to 3 weeks). `IPlatform` with Windows and Linux implementations: data
   and save roots, server exe name, Steam roots, graceful stop, kill-with-parent, dir link,
   hard link, start-on-boot, steamcmd fetch and trust. Sweep the 59 backslash literals and the
   path compares. Replace System.Drawing in MapRenderer.
4. **Linux game side** (1 to 2 weeks). Launch environment and working directory, SIGINT stop,
   `/proc` orphan detection, doorstop BepInEx rules, steamcmd Linux, symlink isolation.
5. **Kestrel host** (2 to 3 weeks). Static WebUI, `/ws`, atlas and lang routes, auth, TLS,
   lock file, systemd unit, SIGTERM handling, CLI (`passwd`, `status`).
6. **Page changes** (1 to 2 weeks). Transport shim, `Native.windowed`, folder browser, path
   display for shell.open, URL return for the shell.open* family, host-question modal,
   clipboard fallback, Linux wording in about 50 catalog strings and every language pack,
   Linux example paths, node selftests for the shim.
7. **Packaging and update** (1 to 2 weeks). `linux-x64` tarball, install script (user, folders,
   unit), SHA256SUMS, symlink self-update, Linux CI job for the portable xunit tests. Docker
   image later.
8. **Soak** (1 to 2 weeks). Ubuntu 24.04 and Debian 12 VPS; vanilla and a real mod list; two
   isolated realms; crossplay; restarts, updates, a reboot, `kill -9` of BakaLoader.

About 9 to 16 weeks for one developer. Phases 1 to 3 also improve the Windows build and can
ship to Windows users before any Linux release.

### 5.2 Risks

- **Deleting through a link.** Isolation's safe delete depends on .NET unlinking a directory
  symlink without recursing. Wrong once means shared game files gone. Test before enabling.
- **Hard links and updates.** steamcmd replaces files, so a realm's hard-linked binary can
  stay old while symlinked data is new. Check Windows today; re-link after each base update.
- **Concurrency.** Handlers assume one UI thread; losing that quietly races profile saves and
  server start and stop.
- **Security.** The bridge was built for a local, trusted page. On a network it is remote
  control of a shell account. Auth, TLS and the `ServerExePath` rule are not optional.
- **Graceful stop.** Confirm SIGINT save-and-quit on the current build, and that the stop
  timeout outlasts a large world save.
- **Log parsing.** Joins, deaths and saves key on server output; a Linux difference breaks
  features without an error.
- **Case sensitivity.** 441 ignore-case compares, plus mod archives with odd folder casing.
- **Translations.** About 50 strings change meaning in every language pack.
- **Tests.** 224 test files assume Windows paths; without splitting them the Linux build ships
  with thin coverage.
- **Two products.** Every later feature is built and tested twice; the platform seam and a
  Linux CI job are what keep that bearable.

### 5.3 What stays Windows-only

- The WinForms + WebView2 window, custom title bar, DWM dark title bar.
- Start with Windows (Run key) and its notes.
- The frozen launcher, `BakaLoader.cmd`, signtool, the Defender release gate.
- `WinVerifyTrust` on steamcmd.exe; `winhttp.dll` BepInEx loading; the Windows server binary.
- `steam://` links to a Steam client (shape (a) could keep them on a Linux desktop).
- Building the bundled plugins with `build-plugins.ps1` (the DLLs run on both).

## 6. Open questions

- System service or per-user service as the default?
- Should the service install steamcmd itself (i386 libs need root) or require it?
- Docker image in the first Linux release, or after?
- Kestrel host on Windows too (remote management), and in which release?
- Supported distros: Ubuntu 22.04 and 24.04, Debian 12? glibc floor?
