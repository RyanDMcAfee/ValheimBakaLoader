# BakaLoaderCommander - Closed-Test Checklist

Commander is **compile-verified only** (built against the live server's own
`assembly_valheim.dll` / `assembly_utils.dll`). No command has been executed
against a running server. Run this checklist on a **closed test server**
(throwaway world, no real players) before trusting any command.

## Setup

1. Start BakaLoader v0.9.14+ with a throwaway world profile, RCON enabled.
2. Confirm auto-install: `BepInEx/plugins/BakaLoaderCommander/BakaLoaderCommander.dll`
   exists and `BepInEx/config/com.baka.commander.cfg` has the profile's Port/Password.
3. **Trio-removed test (the main event):** temporarily move `AviiNL-RCON`,
   `JereKuusela-Server_devcommands`, and `JereKuusela-Rcon_Commands` out of
   `BepInEx/plugins/`, restart the server via BakaLoader.
   - Expect: BepInEx console logs `BakaLoader Commander ... listening on 127.0.0.1:<port>`.
   - Expect: app UI shows RCON/devcommands features **ungated** (no "missing mods" banner).
4. Join the server with a test character (needed for player-targeted commands).

## Auth handshake

- [ ] Wrong password → app/RCON client reports login failure (reply id −1, "Login Failed").
- [ ] Correct password → "Login Success".
- [ ] Empty password in profile → any password accepted (AviiNL parity).

## Commands (run each via the app UI, exactly as the app sends them)

| # | Command sent | Expected |
|---|---|---|
| 1 | `broadcast center hello world` | Message appears **center-screen** for the joined player; RCON reply "Broadcasting message: hello world" |
| 2 | `playerlist` | One line per player: `{name}/{steamId}/{charId} (x, z, y)` - app's Players table must parse position (right-click → teleport target list populates) |
| 3 | `dmg <name> -1000000` (Heal button) | Player healed to full; **verify it does NOT damage** - negative-damage heal semantics were never live-confirmed, this is the riskiest item |
| 4 | `dmg <name> 1000000` (Smite button) | Player dies |
| 5 | `tp <name> x,z,y` (coords from another player's position) | Player teleports; verify axis order correct (x=east, z=north, y=height) - wrong order = player under/above ground |
| 6 | `tp <name> <otherPlayerName>` | Player teleports to the other player |
| 7 | `kick <name>` | Player disconnected |
| 8 | `baka_spawn Boar x,z,y 3 2` (Spawn X at) | 3 level-2 boars appear at/near the point, snapped to ground |
| 9 | `baka_killall` | Spawned hostile creatures die; **players survive** |
| 10 | `baka_killall` with friendlies present (spawn a Deer; tame a boar; dvergr if reachable) | **Tamed pets, passive animals, and dvergr allies SURVIVE**; only hostile-faction mobs die. Reply: "N hostiles slain, M spared (players, pets & allies)" |

Also test a spawn with a **modded prefab** from the item picker (verifies
ZNetScene hash lookup against the modded ObjectDB).

## Edge cases

- [ ] Command sent while world still loading → reply "Error: server not ready (world still loading)", no crash.
- [ ] Unknown command (e.g. `sleep`) → forwarded to console, reply "Forwarded to console: ...".
- [ ] Player name containing a space → dmg/tp/kick still resolve the target.

## Coexistence test

- [ ] Restore the AviiNL trio into plugins alongside Commander, restart.
  - One plugin wins the port bind; the other logs a warning and stays dormant.
  - **Either way, every command above must still work** (both are wire- and
    command-compatible). Check BepInEx log to see which one bound.
- [ ] Remove trio again → Commander binds → everything still works.

## Config persistence

- [ ] Edit `BindAddress = 0.0.0.0` in `com.baka.commander.cfg`, restart server via
  BakaLoader → the app rewrites Port/Password but **BindAddress stays 0.0.0.0**.

## Sign-off

When all items pass, the third-party trio can be permanently removed from the
live server, and the "heal/smite/tp/kick UNVERIFIED" memory note can be cleared.
