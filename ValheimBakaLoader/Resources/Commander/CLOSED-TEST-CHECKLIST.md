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
| 9 | `baka_killall` | Spawned hostile creatures die; **players survive**. Reply: "KillAll complete: N hostiles slain, U out of reach, S spared (players, pets & allies)" |
| 10 | `baka_killall` with friendlies present (spawn a Deer; tame a boar; dvergr if reachable) | **Tamed pets, passive animals, and dvergr allies SURVIVE**; only hostile-faction mobs die |
| 11 | `baka_killall` with a **second player standing far from the first**, hostiles next to each | Hostiles near **both** players die. This is the 2026-09-19 bug: the old sweep asked this process which creatures existed, and creatures around players belong to those players' clients, so it answered "0 slain" |
| 12 | `baka_killall Eikthyr` after summoning Eikthyr | Eikthyr dies and nothing else does |
| 13 | `baka_killall eikthyr` (lower case) | Same result: the name is matched ignoring case |
| 14 | `baka_killall Fenring` with no Fenring anywhere | "No Fenring is in the world right now." |
| 15 | `baka_killall Eikthyyr` (misspelled) | "There is no creature called 'Eikthyyr' in this game." |
| 16 | `baka_killall Deer` | "Deer is not a hostile creature, so KillAll leaves it where it stands." Nothing dies |
| 17 | `baka_killall near <player> 30` with hostiles inside and outside 30m | Only the ones inside die; the far ones are untouched and are not counted |
| 18 | `baka_killall near <player> 30` with nothing hostile that close | "Nothing hostile was standing inside that radius around \<player\>." |
| 19 | `baka_killall near <two word name> 30` | Resolves the player whose name holds a space |
| 20 | `baka_killall near <player> soon` / `baka_killall near <player> 0` | "Error: the radius has to be a number of metres above zero, like 50." Nothing dies |
| 21 | `baka_killall Boar extra` | The usage line. Nothing dies |
| 22 | A boss summoned and killed by `baka_killall` | The boss dies **the ordinary way**: it drops its trophy, the power stone lights up, and the world's boss counter moves. Nothing is deleted outright |
| 23 | **Tame a boar and run `baka_killall` within two seconds** | The boar **survives**. Tamed is read off the server's copy of the world record, and that copy is written by the owning client, so a tame that has only just happened is the one moment the flag might not have arrived yet. A boar that dies here is a replication race, not a faction mistake: report how many seconds it took and whether the taming effect was still playing |
| 24 | Two `baka_killall` in a row, the second sent before the first answers | The second is refused with "KillAll is already running: N candidates still to go", and the counts in the first one's result line are its own. Two sweeps over one snapshot would double every count and strike half the world twice |

Also test a spawn with a **modded prefab** from the item picker (verifies
ZNetScene hash lookup against the modded ObjectDB).

### Hostile dvergr are spared too, and that is on purpose

The spare rule reads a creature's faction and whether it was tamed, and the whole `Dverger`
faction is spared. The game does not make a dvergr hostile by changing its faction: in
`Character.IsEnemy` a dvergr and a player are not enemies either way round, and the hostile
ones turn on you through `BaseAI.IsAggravated()`, which is a flag on the live instance and
not a fact the world record carries. So the **dvergr rogues and mages in a Mistlands
infested mine survive a sweep**, exactly like the friendly dvergr standing outside it.

That is the deliberate cost of never killing a dvergr ally, and it is the one place the
command knowingly leaves a hostile standing. Do not report it as a bug on the closed
server. Naming the prefab does not get round it either: the named form asks the same spare
rule of the prefab before it starts, so it answers "... is not a hostile creature, so
KillAll leaves it where it stands" and nothing dies. An infested mine is cleared by hand.

- [ ] Confirm it, so nobody rediscovers it on the live server: stand in an infested mine
      with the rogues awake, run `baka_killall`, and check they are alive afterwards and
      counted among the spared.

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

## Valheim 1.0 recheck

Valheim 1.0 broke two things these plugins rely on, and both faults only surface at
runtime, so neither a clean build nor a server that starts without complaint proves
anything on its own.

`Terminal.ConsoleCommand` gained a new optional argument in the MIDDLE of its
parameter list, which means every DLL compiled before 1.0 calls a constructor that no
longer exists and throws MissingMethodException inside Awake. That killed `baka_spawn`
and `baka_killall` outright: the plugin loaded, the command never registered.
`ZRoutedRpc.Everybody` stopped being a field and became a constant, so Commander's
compiled field read threw MissingFieldException the first time somebody ran
`broadcast`. Everything else kept working, which is exactly what makes it easy to miss.

The fix was to rebuild all of the bundled plugins against the 1.0 server assemblies and
to write the broadcast target as a plain 0 instead of reading the field. The commands
are also registered as ordinary server-only commands now rather than cheat commands,
because from 1.0 the game refuses to run a cheat-flagged command unless the world is
already flagged as cheated.

One consequence worth knowing before you roll this out. The constructor argument that
1.0 inserted is baked into the call site at compile time, so a plugin can match the old
game or the new one but not both. These DLLs are built for 1.0, which means `baka_spawn`
and `baka_killall` will throw MissingMethodException on a server still running the June
2026 build. Update the server first, then the plugins. Commander itself is not affected:
its broadcast now uses a plain 0 rather than a compiled field read, and that is correct
on every version.

Run this on a closed 1.0 server after the rebuild.

- [ ] Start the server, then read `BepInEx/LogOutput.log` end to end. There must be no
      MissingMethodException and no MissingFieldException anywhere in it.
- [ ] The same log shows `BakaLoader Commander v1.5.0` and its listening line,
      `BakaLoader Spawn Helper v1.2.0 loaded - 'baka_spawn' command registered.` and
      `BakaLoader KillAll v1.7.0 loaded. 'baka_killall' command registered.`
      A missing registration line is the tell that the constructor threw again.
- [ ] `broadcast center hello` puts the message on a joined player's screen and replies
      "Broadcasting message: hello". This is the one that used to throw, and it threw on
      first use rather than at load, so it has to be actually run.
- [ ] `baka_spawn Boar <x,z,y>` and `baka_killall` sent over RCON both take effect
      (Commander answers these itself, without going through the console).
- [ ] Type `baka_spawn` and `baka_killall` directly into the server console window. Both
      must run instead of answering with the confirm-cheat message.
- [ ] The world stays clean: nothing in the log says the world or the profile was
      flagged as cheated or modded, and players keep their achievements.
- [ ] Repeat the whole command table above once on 1.0. The rebuild changed which
      constructor overload is called, so every command is worth one more pass.

## The 2026-09-19 kill-all rewrite

`baka_killall` used to ask this process which creatures existed
(`Character.GetAllCharacters()`). On a listen server that is the whole world. On a
DEDICATED server it is very nearly nothing, because the server instantiates only what
sits inside its own reference position and every creature standing around a player is
instantiated and owned by that player's client. Live on 2026-09-19 with four players on
and hostiles beside them, the command answered "0 hostiles slain, 162 spared" and a
summoned Eikthyr walked away.

The sweep walks the world's own object records now (every object in a Valheim world has
one, loaded by somebody or loaded by nobody), works out from the prefab whether a record
is a creature and what faction it belongs to, reads "tamed" from the record itself, and
sends the killing hit to whoever OWNS the creature. Three things to watch on the closed
server:

- [ ] **The out-of-reach count behaves.** A creature in a zone nobody has loaded has no
      owner, so nobody anywhere is running it and nothing can damage it. It is counted out
      of reach and left alone rather than counted as a kill, which is the honest answer and
      the opposite of what the old command did. A nonzero count is ordinary on a world
      players have walked across. What to check is that it MOVES the right way: stand next
      to a creature that was counted out of reach, run the sweep again, and it must die and
      leave that count.
- [ ] **Nothing is destroyed outright.** Kills go through the game's own damage road, so
      drops land and the boss counter moves. If anything ever vanishes without dropping,
      the sweep is destroying records and that is a defect.
- [ ] **The BepInEx log carries no "faction this KillAll does not know yet" line and the
      reply carries no such sentence.** If it does, Valheim has grown a faction the plugin
      has not met. Those are SPARED on purpose rather than killed, so the sweep is safe but
      incomplete until the new faction is named in `BakaKillAllSweep.Mirror`.

## The sweep no longer outlasts its own answer

Commander stops waiting for a command at 4500ms, under BakaLoader's own five second client
give-up, and then answers "Error: command timed out (server main thread busy)". It has no
way to call the work back: `Update()` carries straight on and finishes the sweep. So the
one thing a long sweep could tell a host was a lie, that it had failed, while every hostile
on the server died.

The sweep answers first now. It resolves everything a typo could get wrong, takes its
snapshot, and walks as much of it as three milliseconds allow before it replies:

- A world small enough to finish inside that slice answers with its whole
  **"KillAll complete: ..."** line, which is what every row of the table above expects and
  what any closed test world will do.
- A world too big answers **"KillAll started: N candidates"**, and the real
  "KillAll complete" line reaches the **BepInEx server log** (and the server console, when
  the command was typed there) when the walk lands. The rest of the walk runs a few
  milliseconds a frame, so the server keeps ticking while it happens.

A candidate is one creature record inside the scope that was asked for. It is not the
number of records in the world: the snapshot keeps only creatures, and only the ones inside
the named prefab or the radius, so the number a host reads is the number that decides how
long the sweep takes.

- [ ] "KillAll started" is **not** a failure. Read it once on the closed server if you can
      make a world big enough, and confirm the "KillAll complete" line turns up in
      `BepInEx/LogOutput.log` afterwards with counts that add up.
- [ ] Watch the server tick rate while a sweep runs. It must not freeze. One long frame
      when the snapshot is taken is expected and cannot be avoided; a stall for the length
      of the whole walk is a defect.
- [ ] No sweep ever answers "Error: command timed out" again. If one does, the snapshot
      pass itself is what ran long, and that is worth a line in the report with the world's
      size.

## Sign-off

When all items pass, the third-party trio can be permanently removed from the
live server, and the "heal/smite/tp/kick UNVERIFIED" memory note can be cleared.
