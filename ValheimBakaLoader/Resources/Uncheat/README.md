# BakaLoaderUncheat

Takes the "summoned through cheating means" mark off the items **your own character** is
carrying. It runs on the client, once per character, and then leaves you alone.

## Why you would want it

BakaLoader 1.0.9 to 1.1.2 marked everything it spawned as cheated, and Valheim spreads that
mark on its own: a marked item merged into a stack marks the whole stack, crafting with a
marked ingredient marks what comes out, and a marked weapon marks what it kills and what
that drops. While a marked item is in your inventory, Valheim pauses your achievement
progress.

BakaLoader 1.2.0 stopped marking new spawns, and a host can clear the marks off the world
with `baka_cleanse` on an empty server. What that cannot reach is your inventory: a
character's items live in the character file on your own machine and never touch the world
save. This plugin is the other half.

## How to use it

1. Install BepInEx on your **game client**, the same way you would for any client mod.
2. Put `BakaLoaderUncheat.dll` in `BepInEx/plugins`.
3. Start Valheim and load the character you want cleaned.

That is all of it. There is nothing to press, and nothing you have to configure: the sweep runs
the moment the character finishes loading, writes one line into `BepInEx/LogOutput.log` saying
what it cleared, and marks the character so it never runs again for it. There IS one setting, and
it is only for running the sweep a second time; see **Running it again** below.

You can take the plugin out again afterwards. The marks stay off.

## What it changes, and what it does not

It clears one flag on the items that character is carrying or wearing. Nothing else: it does
not touch the world, it does not talk to a server, it does not change how anything spawns, and
on a dedicated server it does nothing at all and says so in the log.

The only things it writes are the ones BepInEx writes for every plugin: its own config file at
`BepInEx/config/com.baka.uncheat.cfg`, which BepInEx creates the first time the plugin loads, and
its line in `BepInEx/LogOutput.log`. It writes no files of its own beyond those, and nothing
anywhere near your world or your save folder.

## Running it again

The sweep writes down which **generation** it ran at, under the key `baka.uncheat.v1` on the
character. To make it sweep every character again, open
`BepInEx/config/com.baka.uncheat.cfg` and raise `Generation` by one, then restart the game.
BepInEx reads that file while the game is starting, so a change takes effect at the next
start rather than straight away.

## Where it came from

It ships beside [BakaLoader](https://github.com/RyanDMcAfee/ValheimBakaLoader), a Windows manager
for Valheim dedicated servers, and the full write-up is on its wiki under **Client
companion**.
