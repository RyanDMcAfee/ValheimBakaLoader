// BakaLoader KillAll v1.8.0 - compiled against SERVER assembly_valheim
//
// Registers "baka_killall" as an in-game console command, so a host standing at the
// server's own console window can clear hostiles without going anywhere near RCON.
// Commander answers the same command name over its own RCON socket. Both of them run the
// SAME sweep: KillAllSweep in BakaKillAllSweep.cs is compiled into both DLLs, so either
// plugin works installed on its own and neither can drift from the other.
//
// Until 1.6.0 this plugin walked Character.GetAllCharacters() and killed what came back.
// On a dedicated server that list holds only what the server itself instantiated near its
// own reference position, and every creature standing around a player is instantiated and
// owned by that player's client, so the command reported a tidy "0 slain" while the world
// was full of monsters. The sweep now walks the world's own object records instead and
// sends the damage to whoever owns each creature. See BakaKillAllSweep.cs for the whole of
// it.
//
// From 1.7.0 the sweep answers before it finishes. A world small enough to walk inside one
// slice still prints its whole "KillAll complete" line at once; a bigger one prints
// "KillAll started: N candidates" and the result follows when the walk lands, because a
// sweep that outran Commander's RCON timeout used to be reported as a failure while it
// went on and killed everything. Update() drives the rest of the walk a few milliseconds
// at a time.
using System;
using System.Collections.Concurrent;
using BepInEx;
using BepInEx.Logging;

namespace BakaLoaderKillAll
{
    [BepInPlugin("com.baka.killall", "BakaLoader KillAll", PluginVersion)]
    public class KillAllPlugin : BaseUnityPlugin
    {
        private const string PluginVersion = "1.8.0";

        private static ManualLogSource Log;

        private sealed class PendingSweep
        {
            public string[] Tokens;
            public Terminal Context;
        }

        // A dedicated server reads its console on a thread of its own, and every call into
        // the game has to happen on the Unity main thread or a headless server dies with
        // "Graphics device is null". So the line is parked here and drained in Update().
        private static readonly ConcurrentQueue<PendingSweep> Pending = new ConcurrentQueue<PendingSweep>();

        /// <summary>A baka_cleanse typed at the console, waiting for the main thread.</summary>
        private static readonly ConcurrentQueue<Terminal> PendingCleanse = new ConcurrentQueue<Terminal>();

        private void Awake()
        {
            Log = Logger;

            new Terminal.ConsoleCommand(
                "baka_killall",
                "baka_killall [<PrefabName> | near <player> <radius>]: kill hostiles. " +
                "Players, pets and allies are always spared.",
                delegate(Terminal.ConsoleEventArgs args)
                {
                    Pending.Enqueue(new PendingSweep
                    {
                        Tokens = Tokenize(args.FullLine),
                        Context = args.Context
                    });

                    // Never let the acknowledgement be what loses the command: the sweep is
                    // already queued by the time anything is printed.
                    if (args.Context != null)
                        args.Context.AddString("KillAll queued. The count lands here when it is done.");
                },
                // isCheat MUST stay false. From Valheim 1.0 the game refuses to run any
                // cheat-flagged console command unless the world is already flagged as
                // cheated, and running one flags the profile as having used cheats. This
                // command is a server-operator tool, so it is registered as a plain
                // server-only command and never taints the world or the achievements.
                isCheat: false,
                isNetwork: false,
                onlyServer: true,
                // hideBehindDevCommands is new in Valheim 1.0 and sits between
                // allowInDevBuild and optionsFetcher. Every argument here is named, so the
                // insertion cannot silently shift a value into the wrong slot.
                hideBehindDevCommands: false
            );

            // The other half of the same story. 1.0.9 to 1.1.2 marked every spawn as
            // cheated, the game spreads that mark on its own, and nothing in the game ever
            // takes one back off. This is the way back, and it is registered here rather
            // than in Commander so a host with only the console can reach it.
            new Terminal.ConsoleCommand(
                "baka_cleanse",
                "baka_cleanse: clear the cheat marks BakaLoader 1.0.9 to 1.1.2 left on spawned things. "
                + "Needs an empty server. Anything a player is carrying is on their own machine and is not touched.",
                delegate(Terminal.ConsoleEventArgs args)
                {
                    PendingCleanse.Enqueue(args.Context);
                    if (args.Context != null)
                        args.Context.AddString("Cleanse queued. The counts land here when it is done.");
                },
                // isCheat stays false for the same reason baka_killall's does: from Valheim
                // 1.0 a cheat-flagged console command refuses to run unless the world is
                // already flagged, and running one flags the profile. A command whose whole
                // job is to take cheat marks off must not leave one behind.
                isCheat: false,
                isNetwork: false,
                onlyServer: true,
                hideBehindDevCommands: false
            );

            Log.LogInfo("BakaLoader KillAll v" + PluginVersion + " loaded. 'baka_killall' and 'baka_cleanse' commands registered.");
        }

        private void Update()
        {
            PendingSweep sweep;
            while (Pending.TryDequeue(out sweep))
            {
                // Held in a local of its own so the closure below captures THIS line's
                // console rather than whatever the loop variable holds when the sweep lands,
                // which may be several frames later and several commands on.
                var context = sweep.Context;

                string reply;
                try
                {
                    reply = KillAllSweep.Start(
                        sweep.Tokens,
                        delegate(string message) { Log.LogWarning(message); },
                        delegate(string line) { Announce(line, context); });
                }
                catch (Exception ex)
                {
                    reply = "KillAll failed: " + ex.Message;
                    Log.LogError("KillAll failed: " + ex.Message + "\n" + ex.StackTrace);
                }

                Announce(reply, context);
            }

            Terminal cleanseContext;
            while (PendingCleanse.TryDequeue(out cleanseContext))
            {
                var where = cleanseContext;
                string said;
                try
                {
                    // One pass, on this thread, with the counts in the answer. The server is
                    // empty by the time it runs, so a frame it takes to itself costs nobody.
                    said = CleanseSweep.Run(null);
                }
                catch (Exception ex)
                {
                    said = "Cleanse failed: " + ex.Message;
                    Log.LogError("Cleanse failed: " + ex.Message + "\n" + ex.StackTrace);
                }

                Announce(said, where);
            }

            // A sweep too big to finish inside its answer carries on here, a few
            // milliseconds a frame, and announces its result line when it lands.
            KillAllSweep.Pump();
        }

        /// <summary>
        /// Puts a line in the server log, and in the console the host typed at when it is
        /// still there to answer to. The sweep already happened either way, so neither of
        /// these can ever be what fails it.
        /// </summary>
        private static void Announce(string line, Terminal context)
        {
            Log.LogInfo(line);

            try
            {
                if (context != null) context.AddString(line);
            }
            catch (Exception ex)
            {
                Log.LogDebug("Could not print the KillAll reply to the console: " + ex.Message);
            }
        }

        /// <summary>
        /// The typed line split the way Commander splits an RCON line, verb still at index 0.
        /// The game's own Terminal splits on every space and keeps the empty pieces, so two
        /// spaces between a name and a radius would hand the parser a blank word.
        /// </summary>
        private static string[] Tokenize(string line)
        {
            return (line ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
