// BakaLoader KillAll v1.6.0 - compiled against SERVER assembly_valheim
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
using System;
using System.Collections.Concurrent;
using BepInEx;
using BepInEx.Logging;

namespace BakaLoaderKillAll
{
    [BepInPlugin("com.baka.killall", "BakaLoader KillAll", PluginVersion)]
    public class KillAllPlugin : BaseUnityPlugin
    {
        private const string PluginVersion = "1.6.0";

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

            Log.LogInfo("BakaLoader KillAll v" + PluginVersion + " loaded. 'baka_killall' command registered.");
        }

        private void Update()
        {
            PendingSweep sweep;
            while (Pending.TryDequeue(out sweep))
            {
                string reply;
                try
                {
                    reply = KillAllSweep.Run(sweep.Tokens, delegate(string message) { Log.LogWarning(message); });
                }
                catch (Exception ex)
                {
                    reply = "KillAll failed: " + ex.Message;
                    Log.LogError("KillAll failed: " + ex.Message + "\n" + ex.StackTrace);
                }

                Log.LogInfo(reply);

                // The console the host typed at, when it is still there to answer to. The
                // sweep already happened either way, so this can never be what fails it.
                try
                {
                    if (sweep.Context != null) sweep.Context.AddString(reply);
                }
                catch (Exception ex)
                {
                    Log.LogDebug("Could not print the KillAll reply to the console: " + ex.Message);
                }
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
