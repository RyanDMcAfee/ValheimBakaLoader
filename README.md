<h1 align="center">Valheim BakaLoader</h1>

<p align="center">
  <em>A Windows app that runs your Valheim dedicated server. Start it, keep the mods current, restart on a schedule, see who is online, and look at a map of your actual world.</em><br>
  <sub>Free, self-hosted on your own PC, no command line. Works with Valheim 1.0.</sub>
</p>

<p align="center">
  <a href="https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/latest"><img src="https://img.shields.io/github/v/release/RyanDMcAfee/ValheimBakaLoader?color=FF7A1A&style=flat-square&label=version" alt="version"></a>
  <a href="https://github.com/RyanDMcAfee/ValheimBakaLoader/releases"><img src="https://img.shields.io/github/downloads/RyanDMcAfee/ValheimBakaLoader/total?color=2E8B57&style=flat-square" alt="downloads"></a>
  <a href="#requirements"><img src="https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-5B7A8C?style=flat-square" alt="platform"></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/6.0"><img src="https://img.shields.io/badge/.NET-6.0-512BD4?style=flat-square" alt=".NET 6"></a>
  <a href="https://github.com/RyanDMcAfee/ValheimBakaLoader/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-source--available-E8A93C?style=flat-square" alt="license"></a>
</p>

<p align="center">
  <a href="https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/latest"><b>Download the latest release</b></a>, unzip it anywhere, run <code>ValheimBakaLoader.exe</code>. A short setup wizard runs the first time.
</p>

<p align="center">
  <a href="img/bl-hearth.png"><img src="img/bl-hearth.png" width="820" alt="The BakaLoader dashboard"></a>
</p>

> Fan-made. Not affiliated with Iron Gate or Coffee Stain. Use at your own risk, and keep backups of worlds you care about.

**Full documentation is in the [wiki](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki).** This page is the short version.

---

## Quick start

1. [Download the latest release](https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/latest), unzip it, run `ValheimBakaLoader.exe`. The wizard finds your Valheim Dedicated Server install and your worlds.
2. On the Settings screen give the server a name and a password; the default port suits most people.
3. Pick an existing world, or type a name for a new one and choose a seed.
4. Choose how people join. Public lists it in the in-game browser, crossplay gives a code that works from any platform.
5. Press Start. When the status says Running, press **Copy join info** and send it to your friends.

Friends outside your network need UDP 2456 and 2457 forwarded on your router and allowed through Windows Firewall, unless you use crossplay.

That first Start press is where BakaLoader asks whether it should look after BepInEx, the loader every mod runs under. Nothing is written to your loader unless you answer yes or press the button yourself.

In-game broadcasts, the restart countdown and the player actions go over RCON, through small plugins BakaLoader puts beside the server at every start. Everything else works without them.

Longer version: [Install and first run](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Install-and-first-run).

---

## What it does

Nine screens, most with a Norse name as a caption you can switch off. A header on every one shows the server, its state, uptime and players online.

| Screen | What it is for |
|---|---|
| [**Dashboard**](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Dashboard-%28Hearth%29) (Hearth) | Status, who is online, next save, addresses, CPU and RAM, a tail of the log, and the Upkeep card. |
| [**Players**](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Players-%28Vikings%29) (Vikings) | The roster, sortable, with a right-click menu: promote, allow, kick, ban, heal, teleport, spawn. |
| [**Mods**](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Mods) | Scans your mods against Thunderstore, updates one or all, installs from a pasted link, and looks after [BepInEx](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/BepInEx) once you say yes. |
| [**Settings**](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Settings-%28World%29) (World) | Name, password, port, world and seed, crossplay, backups, restart rules, RCON, folders, [world modifiers](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/World-modifiers). |
| [**Map**](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Map-%28Atlas%29) (Atlas) | Drawn from its seed with no map mods, plus fog of war, portals, builds and a forecast. |
| [**Configs**](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Configs-%28Runes%29) (Runes) | Your mods' config files, edited in the app, with a search and a find box. |
| [**Log**](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Log-%28Saga%29) (Saga) | The live server log with levels, search, pause on scroll, and a console line. |
| [**Discord**](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Discord-%28Herald%29) (Herald) | One status post in a channel that edits itself as the server changes. |
| [**Statistics**](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Statistics-%28Skald%29) (Skald) | Uptime, sessions, deaths and mod history, counted on your machine only. |

- **Running the server.** Start, stop and restart by hand, on a schedule with a warning in game, when the last player leaves, or after a crash, and the server process cannot linger after BakaLoader closes. [Running the server](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Running-the-server)
- **More than one server.** Each profile can have its own install, mods and save folder, and switching is one click. [Multiple servers](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Multiple-servers)
- **Backups.** Every backup layer for every world in one place, and a restore copies the live world aside first. [Worlds, backups and restore](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Worlds-backups-and-restore)
- **Updating BakaLoader.** It asks GitHub for a newer release at launch and every six hours, says what installing would do first, and never installs while a server is running. [Updating BakaLoader](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Updating-BakaLoader)
- **The condition bar.** A waiting server update, a held start or a failed backup sits above the page with its action until you deal with it. [The condition bar](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/The-condition-bar)

---

## What is new in 1.2.1 and 1.2.2

- **Updating from inside the app works again.** From 1.2.0 the updater could pick a language pack instead of the app and give up, so hosts on 1.2.0 or 1.2.1 got a download error and stayed where they were. It now picks the app by name. [Updating BakaLoader](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Updating-BakaLoader)
- **Language packs load.** A pack you pick from the globe is read back and used, in Russian, Japanese, Simplified Chinese or Traditional Chinese. [Languages](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Languages)
- **The five world switches are in the app**: No build cost, Player based raids, Passive enemies, No map and Fire hazards, under the difficulty dials on Settings. [World modifiers](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/World-modifiers)
- **A world that already has settings keeps them.** The first time BakaLoader starts a world it has not met, it reads that world's own settings instead of clearing them. [World modifiers](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/World-modifiers)
- **Tables keep sorting** after you switch servers. [Players (Vikings)](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Players-%28Vikings%29)
- **Nothing is asked of Thunderstore about BepInEx until you have said yes** to BakaLoader looking after it. [BepInEx](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/BepInEx)

If you are on 1.2.0 or 1.2.1, the in-app update works now, or you can download the latest zip from the [GitHub release page](https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/latest). Every release before this one is on the wiki [Release notes](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Release-notes).
---

## Valheim 1.0

Worlds are folders in 1.0 and converting an old one cannot be undone, so BakaLoader lists and backs up both formats and warns you before the first save that rewrites one. The admin, ban and allow lists changed format too, and BakaLoader writes both forms, so a file of bare Steam ids does not read as everyone being banned. See [World file formats](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/World-file-formats) and [Server updates and the launch guard](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Server-updates-and-the-launch-guard).

---

## Requirements

- Windows 10 or 11, 64-bit.
- [.NET 6 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/6.0). Windows prompts you on first run if it is missing.
- Valheim Dedicated Server, free in your Steam library under Tools. The wizard can point at any install, including one managed by SteamCMD.

No Linux build and no Docker image. It is a Windows app built around the Windows server binary.

---

## Security and privacy

BakaLoader starts a server process, edits files in your Valheim folders, and talks to a few services. [Privacy and network](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Privacy-and-network) is the long version of everything below.

- **Who it contacts.** `thunderstore.io` for the mod index, a single package when you ask, and downloads you asked for, BepInEx included. Inside every restart window it asks that site which BepInEx pack is current, whatever you answered, naming the package and nothing about you. `api.github.com` and GitHub downloads for BakaLoader updates and language packs. `api.ipify.org` for the public address in your join info. Valve's address for steamcmd and the Steam client for a server update. Your own Discord webhook if you set one up, and a DNS lookup if you use the domain wizard. With Hexium and the heartbeat below, that is the list.
- **Hexium is off unless you turn it on.** While **Also check Hexium** is off in Upkeep, BakaLoader does not open a connection to that site at all. With it on, your machine reads its index about four times an hour, and the site says it keeps request logs for up to ninety days. [Hexium](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Hexium)
- **Language packs are requests you made.** An English window asks for nothing. Opening the globe or the Upkeep card reads the release page, and pressing a language downloads that pack. The one request BakaLoader makes on its own is fetching a pack after it updates itself while you are reading another language, which **Check for BakaLoader updates** stops.
- **Telemetry.** An anonymous heartbeat goes out about every five minutes while the app is open: a one-way hashed device id, the app version, and whether a server is running. No addresses, names, passwords, world data or player data. Turn it off with **Share anonymous usage stats** in Upkeep.
- **What it writes.** Its own settings, statistics and logs under `%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\`, with language packs in a `languages` folder beside them that an update cannot take. Your server's BepInEx folders when you install, update or remove mods and when it puts its own plugins in at each server start, and the admin, ban and allow lists when you use the player actions. BepInEx itself only once you have said yes or pressed the button yourself, with your plugins, patchers and mod configs untouched and everything replaced kept in `BepInEx\.bakaloader-bepinex-backups`. Backups and restores inside your Valheim save folder, always as copies, never in place.
- **Passwords.** Your server password and RCON password are stored in plain text in `userprefs.json`.
- **The build is not code-signed**, so SmartScreen may warn you on first run. The source is in this repository and every release is built from it.

---

## When something goes wrong

Start with [Troubleshooting](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/Troubleshooting): a server that will not start, players seeing "Incompatible version", everyone refused as banned since 1.0, a mod that will not show up. The [FAQ](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/FAQ) has the short answers.

Logs are in `%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\logs\`, per day for the app and per session for the server.

If none of that covers it, open an issue with the log lines around the problem, or post in [Discussions](https://github.com/RyanDMcAfee/ValheimBakaLoader/discussions).

## Mod authors

If you want your mod's items in the spawn picker or its config handled better, open an issue with the Thunderstore link. [For mod authors](https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/For-mod-authors) says what BakaLoader needs from a mod and where the bundled plugins' command names and Harmony targets are listed. It is a solo project and does not take code contributions.

## License

Free to download and use under the BakaLoader Source-Available License: run it for any server, personal or community, and read the source. Redistribution and derivative works are not permitted. See [LICENSE](LICENSE).

Valheim is a trademark of Iron Gate AB. This is an independent, unofficial tool.
