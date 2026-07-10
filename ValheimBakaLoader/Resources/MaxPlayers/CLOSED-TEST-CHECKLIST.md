# BakaLoaderMaxPlayers — closed-server test checklist

Run these on a closed/test server (never the live one). The plugin replaces the
third-party Azumatt-MaxPlayerCount mod from 0.9.34 on; BakaLoader migrates old
installs automatically the first time the server starts (or when you save a
max-players change while the server is stopped).

## 1. Fresh install (no old mod present)

- [ ] With the server STOPPED, set Max Players to a value above 10 in the World
      hall (e.g. 15) and hit Save Config.
- [ ] Confirm `BepInEx/plugins/BakaLoaderMaxPlayers/BakaLoaderMaxPlayers.dll`
      now exists.
- [ ] Confirm `BepInEx/config/com.baka.maxplayers.cfg` contains `MaxPlayers = 15`.
- [ ] Start the server. In the BepInEx console/log, look for
      `BakaLoader MaxPlayers` loading with no errors and no
      "could not find the expected player-cap constant" warnings.
- [ ] The mods list in the Mods hall should NOT show BakaLoaderMaxPlayers
      (it's an app-managed companion, hidden like KillAll/Commander).

## 2. Cap actually raised

- [ ] With MaxPlayers = 15 and the server running, connect more than 10 players
      (or as many as you can muster past 10). The 11th join must be admitted.
- [ ] Steam server browser should report the raised max-player count.
- [ ] If crossplay is on (PlayFab backend), confirm joins above 10 work there too.

## 3. Cap actually enforced

- [ ] Set MaxPlayers = 2 (the plugin allows lowering below vanilla), restart,
      and confirm the 3rd concurrent join is rejected with "server is full".

## 4. Migration from the old Azumatt mod

- [ ] On an install that still has `BepInEx/plugins/Azumatt-MaxPlayerCount/`
      and `BepInEx/config/Azumatt.MaxPlayerCount.cfg` (say `MaxPlayerCount = 14`):
      start the server through BakaLoader once.
- [ ] Confirm the Azumatt folder and its cfg are GONE, the BakaLoaderMaxPlayers
      folder exists, and `com.baka.maxplayers.cfg` says `MaxPlayers = 14`
      (the old value was adopted).
- [ ] World hall shows 14 in Max Players after the migration.
- [ ] Deferred variant: with the old mod installed and the server RUNNING,
      save a new count (e.g. 16). Nothing should be deleted yet (old DLL is
      file-locked) and `Azumatt.MaxPlayerCount.cfg` should now say 16. Stop and
      start the server — migration completes and `com.baka.maxplayers.cfg`
      carries the 16.

## 5. Config change semantics

- [ ] Change MaxPlayers while the server is running: value applies on the NEXT
      start (the patches bake the number in at load), matching the UI note.

## 6. Uninstall sanity

- [ ] Delete `BepInEx/plugins/BakaLoaderMaxPlayers/` while stopped, set Max
      Players back to 10, start: server runs vanilla with the 10 cap and
      BakaLoader does not reinstall the plugin (it's install-on-demand only).
