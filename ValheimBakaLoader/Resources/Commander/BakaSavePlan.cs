// BakaLoader Commander: the half of baka_save that needs no game at all.
//
// Plain C# for the same reason BakaClearDropsPlan.cs is. It names no Unity, BepInEx or Valheim
// type, so it compiles into BakaLoaderCommander.dll through Resources\build-plugins.ps1 AND into
// BakaLoader itself through the app project's ordinary source glob. The second way is what lets
// the app read the reply with the very words the plugin writes, and lets the suite hold the
// decision and the reply text to account with no dedicated server installed. KEEP THIS FILE FREE
// OF GAME TYPES.
//
// WHY THE COMMAND EXISTS
// BakaLoader asks for a save before it closes a server. Up to 1.2.12 it sent the game's own
// "save" console command, which Commander could only forward to Console.TryRunCommand. On a
// dedicated server that throws a NullReferenceException, so the save was never asked for and a
// stop only ever got the game's own shutdown save. baka_save calls the game's save directly, the
// same call the dedicated server's own save timer makes (Game.UpdateSaving:
// ZNet.instance.Save(sync: false, saveOtherPlayerProfiles: true, waitForNextFrame: true)), and the
// save itself then prints the game's "World save (5/5) done" line the app already waits for.
//
// ZNet.Save returns nothing and skips the save with only a warning in the log when the world did
// not load cleanly or a session flag forbids it, so the plugin checks those same conditions first
// and says which one it hit, rather than answering "started" for a save that will never run.
namespace BakaLoaderSave
{
    /// <summary>The conditions ZNet.Save checks, read off the game by the plugin.</summary>
    internal struct SaveState
    {
        /// <summary>ZNet.instance and ZRoutedRpc.instance both exist.</summary>
        public bool ServerReady;
        /// <summary>ZNet.instance.IsServer(): only a server has a world to save.</summary>
        public bool IsServer;
        /// <summary>ZNet.World is not null.</summary>
        public bool WorldLoaded;
        /// <summary>SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld).</summary>
        public bool WorldSaveBlocked;
        /// <summary>ZNet.m_loadError, ZoneSystem.SkipSaving() or DungeonDB.SkipSaving().</summary>
        public bool SkipsSaving;
        /// <summary>ZNet.EnoughDiskSpaceAvailable, which the save timer checks before it saves.</summary>
        public bool EnoughDiskSpace;
    }

    internal static class SavePlan
    {
        public const string Verb = "baka_save";

        /// <summary>The one reply that means the save was asked for. The save line follows it.</summary>
        public const string StartedReply = "save: started";

        /// <summary>Every reply that means no save was asked for starts with this, then the reason.</summary>
        public const string RefusedPrefix = "save: refused";

        /// <summary>
        /// The reply for a state, or null when the save may go ahead (the plugin then starts it
        /// and answers <see cref="StartedReply"/>). Checked in the order ZNet.Save would fail.
        /// </summary>
        public static string Refusal(SaveState state)
        {
            if (!state.ServerReady) return Refused("the server is not ready (world still loading)");
            if (!state.IsServer) return Refused("this game instance is not the server");
            if (!state.WorldLoaded) return Refused("no world is loaded");
            if (state.WorldSaveBlocked) return Refused("the game has blocked world saves for this session");
            if (state.SkipsSaving) return Refused("the world did not load cleanly, so the game will not save it");
            if (!state.EnoughDiskSpace) return Refused("there is not enough free disk space to save the world");
            return null;
        }

        /// <summary>The reply when starting the save threw.</summary>
        public static string Failed(string message)
        {
            return Refused("starting the save failed: " + (string.IsNullOrWhiteSpace(message) ? "no reason given" : message.Trim()));
        }

        public static string Refused(string reason)
        {
            return RefusedPrefix + " " + reason;
        }
    }
}
