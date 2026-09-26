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
| 7 | `kick <name>` | Player disconnected, reply "Kicked: \<name\>" naming the player the server found. A name nobody answers to is refused instead: "Error: no player named '\<name\>' is online", and nobody is disconnected |
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
| 25 | A player whose name is **not ASCII** joins (Greek, Cyrillic or Japanese letters), then `playerlist` | Their name comes back in their own letters in the Saga log and in the Players table, not as `?` and not as a run of Latin-1 characters. This is the whole encoding fix: the app reads the server's output as UTF-8 now. Note the machine's Windows language, because the damage this replaced was done in whatever code page that machine uses |
| 26 | Kick that same player from the Players table (right click, Kick) | They are disconnected, and the toast names them in their own letters. The app sends the platform id (`Steam_7656...`), so this has to work even for a player whose name was stored broken by an older BakaLoader: the id is what the server matched on, and the reply carries the name back |
| 27 | With that player still on, kick from `Send console command...` by typing their name with one letter wrong | "Error: no player named '\<what you typed\>' is online" and nobody is disconnected. Before 1.2.0 this answered "Kicked: \<what you typed\>" and the host was told a player had been thrown off who was standing right there |
| 28 | Kick a player whose name is not ASCII while the roster still shows the **old broken spelling** (an install upgraded from 1.1.x, before they rejoin) | They are still kicked: the id goes out first. Then let them rejoin and check the Players table holds ONE row for the character, in their own letters, rather than the broken spelling beside the good one |

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
- [ ] The same log shows `BakaLoader Commander v1.7.0` and its listening line,
      `BakaLoader Spawn Helper v1.5.0 loaded - 'baka_spawn' command registered.` and
      `BakaLoader KillAll v1.7.0 loaded. 'baka_killall' command registered.`
      A missing registration line is the tell that the constructor threw again.
      Read the version numbers, not just the presence of the lines: an old DLL left in
      `BepInEx/plugins` announces the old number and behaves the old way.
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

## Spawned things no longer count as cheating

Valheim 1.0 stamps everything its own `spawn` command conjures as summoned through
cheating. An item carrying that stamp says "This item was summoned through cheating
means." in its tooltip, and while one sits in a player's inventory that player's
achievement progress is paused. A creature carrying it hands the stamp on to whatever it
drops when it dies. Both BakaLoader plugins copied that behaviour verbatim, so a host
replacing somebody's lost axe quietly cost that player their achievements.

From Commander 1.6.0 and Spawn Helper 1.5.0 nothing BakaLoader spawns is stamped. It is one
config entry per plugin, `[Spawning] MarkSpawnedAsCheated`, default `false`, in
`BepInEx/config/com.baka.commander.cfg` and `BepInEx/config/com.baka.spawnhelper.cfg`.

**Either entry only takes effect at the next server start.** BepInEx reads a .cfg once,
while the server is starting, and does not watch it afterwards, so editing one while the
server runs changes nothing until you stop and start it.

**Both entries survive, and both are yours to set.** BakaLoader rewrites
`com.baka.commander.cfg` from the server profile on every launch, and from BakaLoader 1.2.0
it carries the whole `[Spawning]` section across that rewrite, every line inside it as you
wrote it, comments and all, the way it has always carried `BindAddress`. Only the header
line comes back in the app's own spelling, which is what BepInEx writes anyway and which
carries no setting. So a value written there by hand is still in the file when BepInEx
reads it at the next start, and it is the value the plugin binds. A file with no
`[Spawning]` section keeps none, and the plugin writes its own default entry out on that
start. `com.baka.spawnhelper.cfg` is not rewritten at all and holds its value the same
way. Spawns issued through the app go to Commander whenever
Commander holds the RCON port, which is the arrangement for this pass, so the Commander
entry is the one governing them. (In the coexistence case, where the AviiNL trio won the
port, an app spawn ends up reaching the Spawn Helper's own console command instead, so its
entry is the one in force.) Read the two steps at the end of this section before you set
anything.

**Items and creatures spawned before this version keep their mark.** The flag lives inside
each object, in the item's own saved data and on the creature's world record, not in this
setting, so turning the entry off cannot reach back and clear anything already handed out.
A player who still has achievements paused has to drop and destroy the old item.

### Entry off (the default, so leave both .cfg files alone for this pass)

Walk this half on a character carrying **no** previously spawned items, and swinging a
weapon that was not spawned either. Vanilla spreads the mark on its own once one marked
object is in play: a clean stack merged into an already marked stack of the same item
comes out marked, and a creature killed by a player holding a marked weapon is recorded
as cheated, which then reaches its drops. Either would read as a failure here and neither
would be the plugin's doing.

- [ ] `baka_spawn SwordIron <x,z,y>` from the app. Pick the sword up and read its tooltip:
      there must be **no** "summoned through cheating means" line on it.
- [ ] With that sword in the inventory, open the achievements screen. Progress must be
      **running**, not paused, and no cheated-item popup may appear.
- [ ] `baka_spawn Wood <x,z,y> 150` (three stacks of 50) and
      `baka_spawn PickaxeBronze <x,z,y> 1 3` (a quality 3 tool). Same check on each: no
      cheated line, on the stack and on the upgraded tool alike.
- [ ] `baka_spawn Boar <x,z,y>`, then kill it. The meat and hide it drops carry **no**
      cheated line either. This is the path the ZDO key drives, so it is the one that
      proves the creature arm and not just the item arm.
- [ ] `baka_spawn Lox <x,z,y> 1 2` (a two star creature), kill it, same check on its drops.
- [ ] Type `baka_spawn Coins <x,z,y> 100` at the server console window rather than through
      the app, so the Spawn Helper's own command is walked and not only Commander's.

### Entry on, then restart

- [ ] Stop the server. Set `MarkSpawnedAsCheated = true` under `[Spawning]` in
      **`com.baka.spawnhelper.cfg`**, start the server, `baka_spawn SwordIron <x,z,y>` at the
      server console. The tooltip **must** carry the cheated line now, and holding it must
      pause achievement progress. That is the escape hatch working.
- [ ] **The Commander side, which is the one an app spawn reads.** Stop the server, set
      `MarkSpawnedAsCheated = true` under `[Spawning]` in **`com.baka.commander.cfg`**, start
      the server, and open that file again **before** you spawn anything. The `[Spawning]`
      section must **still be there** with `true` in it, and `BindAddress` must still hold
      whatever you had it at: BakaLoader rewrites the rest of the file from the server
      profile on every launch and carries those two across untouched. Now spawn through the
      app. The tooltip **must** carry the cheated line. A section that has gone, or a clean
      tooltip after setting it, means the section did not survive the rewrite and is the
      thing to report.
- [ ] Writing the value while the server runs still changes nothing until the next start:
      BepInEx read the file at startup and keeps no watcher on it. Stop and start the server
      after any edit rather than expecting a running server to pick one up.
- [ ] Set both back to `false` and restart before signing off, so the live server ends the
      pass on the default.

## The 2026-09-19 kick, and a player whose name is not English

Two faults, one walk. BakaLoader read the server's console output with the machine's own
code page rather than as UTF-8, so a player named with two Greek letters was learned,
stored and kicked as four Latin-1 characters nobody answers to; and `CmdKick` handed its
text to `ZNet.Kick` and answered `Kicked: <text>` without ever checking that anybody
matched, so a kick that reached no one read as done.

The app sends the platform id first now (`Steam_<id>`), falls back to the name, and the
plugin resolves the peer itself before it claims anything. You need one player whose name
is NOT plain English for this: Greek, Cyrillic, Japanese or an accented Latin name all
work, and the name must be set on the character, not on the account.

- [ ] That player joins. The roster row shows their name **in their own letters**, and the
      Saga log's join line does too. Question marks, or a row of accented capitals where a
      word should be, is the encoding fault and nothing below it is worth running yet.
- [ ] `playerlist` over RCON names them the same way.
- [ ] Kick them from the row menu. They are disconnected, and the toast reads
      **Kicked `<their name>`**, spelled the way they spell it.
- [ ] Kick somebody who is NOT on the server: type `kick Nobody` into
      `Send console command...`. The answer printed in the Saga log is
      `Error: no player named 'Nobody' is online` and nobody is disconnected. A typed
      command raises no toast; the worded toast is the row menu's, so also kick a player
      from the Players row menu just after they have left and check that toast says nobody
      of that name is on the server. It must NOT say kicked.
- [ ] Ban that same non-English player while they are online. They are thrown off, the ban
      holds at their next attempt, and the Saga log says kicked rather than "the kick
      reached nobody".
- [ ] Heal, smite, teleport and spawn at still reach them by name, which is the half of
      this that never went through an id.
- [ ] Open `players-cache.json` in `%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader`
      after the walk. Their name is stored in their own letters. A cache written by an older
      build is repaired at the next launch, so a host upgrading should see their old broken
      rows come back to life rather than doubling: one row per player, not two.

## Taking the old marks back off (1.2.3, `baka_cleanse`)

The section above says that items and creatures spawned before 1.2.0 keep their mark and
that nothing in the game ever takes one off. `baka_cleanse` is the way back, and this is the
walk for it. **Everything here needs a world with marks already on it**, so do the first
three steps on a throwaway world with 1.1.2's plugins, then put 1.2.3's in and carry on.

Plugin versions for this pass: Commander 1.8.1, KillAll 1.8.1.

### Making a world with marks in it (on 1.1.2's plugins)

- [ ] `baka_spawn SwordIron <x,z,y>`, pick it up, and read the tooltip: it MUST carry the
      "summoned through cheating means" line. Without that line the rest of this walk proves
      nothing.
- [ ] Put the sword in a chest. Put a spawned stack of `Wood` in the same chest.
- [ ] `baka_spawn Boar <x,z,y>` and kill it with the marked sword, so the drops are marked
      the way vanilla spreads it. Leave the drops on the ground.
- [ ] Put an `Ore` into a smelter and something into a cooking station, both spawned, so the
      queue flags are set as well.
- [ ] Build a piece out of spawned materials.

### The refusal (1.2.3's plugins, one player still on)

- [ ] With one player connected, press **Clear cheat marks** in the Players hall. The toast
      must name that player and say the server has to be empty. Nothing in the world changes.
- [ ] Same command over RCON: the reply is
      `Error: 1 player is still connected (<name>). Cheat marks can only be cleared on an empty server.`
- [ ] Ask the player to log out. With the server empty, the command goes through.

### The sweep

- [ ] The confirm dialog says all four things before it runs: what it clears, that players
      have to put what they carry into a chest first and take it back out afterwards, that it
      needs an empty server, and that a character the game has flagged for console use cannot
      be cleared by anyone.
- [ ] Press it. The toast reads `Cheat marks cleared · N world objects · M containers · K items`
      and all three numbers are above zero on the world built above.
- [ ] The result line is in the Saga log as well as in the toast.
- [ ] Log back in. The sword out of the chest has **no** cheated line. The wood stack has
      none. The boar drops on the ground have none. The built piece is clean. The smelter and
      the cooking station finish their queues and what comes out is clean.
- [ ] The achievements screen is **running** again for that character, with nothing marked in
      its inventory.
- [ ] Run it a second time on the same world. It must answer
      `Cleanse complete: nothing in this world carries a cheat mark.` and the toast must say so
      rather than reading like a fresh sweep.

### What it cannot do, which is checked here so nobody reports it as a bug

- [ ] A marked item carried in a player's own inventory, never put in a chest, is STILL
      marked afterwards. That inventory lives in the character file on their machine and no
      server command can reach it. The confirm says so; check that it does.
- [ ] A character the game has flagged for console use stays flagged. BakaLoader never writes
      to a character file.
- [ ] On a server running an older Commander, `baka_cleanse` answers `Unknown command: ...`
      and the toast says the plugin is older than the command rather than claiming a cleanse.
- [ ] A marked item carried in a player's own inventory is still marked, which is the line
      above; nothing below changes that.

## The sliced sweep (1.2.4, plugins 1.8.1)

Until 1.2.4 the cleanse was one uninterrupted pass on the Unity main thread. On a
long-lived world that pass outlasts the RCON client's patience, so the host was told the
command had timed out while it carried on and did every bit of its work, and the server did
not tick for as long as the pass took: anybody connected would have been frozen and anybody
connecting would have been refused. The walk now takes about eight milliseconds a frame,
`baka_cleanse` answers the moment it knows what it is about to walk, and
`baka_cleanse_status` says how far it has got.

**This needs a big world.** A few hours of play is not enough; a world that has been lived
in for weeks, or one whose `.db` is over a couple of hundred megabytes, is what shows it.
A small world finishes inside the first slice and answers exactly as 1.2.3 did, which is the
first thing to check.

### A small world still behaves the way it always did

- [ ] On a fresh or small world, press **Clear cheat marks**. The toast is the counts, or
      the "nothing carries a cheat mark" one, and it lands at once. Nothing says "started".
- [ ] Over RCON, `baka_cleanse` on that world answers with the whole
      `Cleanse complete: ...` line in one reply.

### A big world answers first and finishes afterwards

- [ ] Over RCON, `baka_cleanse` on the big world answers
      `Cleanse started: N objects to check.` within a second or two, with N in the tens or
      hundreds of thousands. Write N down.
- [ ] While it runs, `baka_cleanse_status` answers `Cleanse running: X of N objects checked.`
      and X goes UP between two reads.
- [ ] While it runs, the server keeps ticking: a second RCON command (`playerlist`) answers
      normally, and the server window's own output does not stop.
- [ ] Press **Clear cheat marks** in the Players hall on that same big world. The window
      waits, and when the sweep lands the toast is the counts, exactly as on a small world.
      It must NOT say the command failed or timed out.
- [ ] After it lands, `baka_cleanse_status` still answers with the counts, and goes on doing
      so until another cleanse is started.
- [ ] `baka_cleanse_status` on a server where nothing has been run answers
      `Cleanse status: nothing is running.`
- [ ] Start a second `baka_cleanse` while the first is still walking. It is refused with
      `Cleanse is already running: M objects still to check.` and the counts of the first one
      are unaffected.
- [ ] While the window is waiting on the big world, the **Clear cheat marks** button is
      greyed out and a second press does nothing. It comes back the moment the toast lands,
      whichever way the sweep ended.
- [ ] Send that second `baka_cleanse` from the console while the window is waiting. The
      window's toast for it says a cleanse is already running and how much is left, and NOT
      that the server answered with something this version does not recognise.
- [ ] Restart the server while the window is waiting on a big world. The window says nothing
      is running and to run it again on an empty server, in those words, rather than reading
      the idle status as an unknown shape.

### Somebody walking in mid sweep

- [ ] Start the sweep on the big world with the server empty, then have a player connect
      while `baka_cleanse_status` still says running.
- [ ] The sweep stops. `baka_cleanse_status` and the server log say
      `Cleanse stopped: 1 player connected while it was running (<name>). Up to that point: ...
      Run it again when the server is empty.`
- [ ] The toast in the window says the same thing and does NOT read like a finished cleanse.
- [ ] Ask them to log out and run it again. A second run walks the WHOLE world again from
      the start, which is what it is meant to do: it is not carrying on from where it stopped,
      and the marks the first run already cleared are simply not found this time.
- [ ] Nothing was half-written: the containers it did rewrite are clean, and the ones it did
      not are exactly as they were.

### The ten-minute ceiling

- [ ] If the sweep somehow runs past ten minutes, the window stops asking and the toast says
      the cleanse is still running and that its counts land in the server log. Nothing about
      that is a failure: check the server log afterwards and the counts are there. Note the
      world size and the time if this happens at all.

## Sign-off

When all items pass, the third-party trio can be permanently removed from the
live server, and the "heal/smite/tp/kick UNVERIFIED" memory note can be cleared.
