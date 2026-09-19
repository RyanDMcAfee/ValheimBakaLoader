// BakaLoader spawning: the one rule that decides whether a conjured object carries the
// game's "summoned through cheating means" mark.
//
// This file is deliberately plain C#. It names no Unity type, no BepInEx type and no
// Valheim type, so it compiles three ways without a single conditional: into
// BakaLoaderSpawnHelper.dll and into BakaLoaderCommander.dll through
// Resources\build-plugins.ps1, and into BakaLoader itself through the app project's
// ordinary source glob. That third way is the point: the test suite can reach these
// members through the assembly's InternalsVisibleTo and hold the rule, the config key and
// its default to account on every build, on a machine with no dedicated server installed.
//
// It sits beside the Spawn Helper because that is where baka_spawn was written; Commander
// absorbed the command later and compiles the same file, so the two plugins can never
// drift into marking spawns differently from one another.
//
// KEEP THIS FILE FREE OF GAME TYPES: a using of UnityEngine here stops BakaLoader itself
// from building, and CompanionPluginSourceTests fails the moment one appears.
namespace BakaLoaderSpawn
{
    /// <summary>
    /// Whether a spawned object is stamped as cheated, and the config entry the host sets
    /// it with. Everything here is a pure function of its arguments or a constant, which is
    /// what makes the rule testable at all: the spawn loop itself can only be exercised
    /// against a running dedicated server.
    /// </summary>
    internal static class SpawnMark
    {
        /// <summary>The config section both plugins bind the entry under.</summary>
        internal const string ConfigSection = "Spawning";

        /// <summary>The config key both plugins bind. Changing it silently resets every host.</summary>
        internal const string ConfigKey = "MarkSpawnedAsCheated";

        /// <summary>
        /// OFF. A host handing somebody a replacement axe should not have to cost that
        /// player their achievements to do it, so BakaLoader leaves the mark off unless it
        /// is asked for. Vanilla's own spawn command is the other way round, which is why
        /// the entry exists rather than the behaviour simply being changed.
        /// </summary>
        internal const bool ConfigDefault = false;

        /// <summary>
        /// What the host reads in BepInEx\config. Written as joined lines rather than as a
        /// verbatim string so a checkout with CRLF endings cannot push carriage returns into
        /// the .cfg file, which BepInEx writes one "## " line at a time.
        /// <para>
        /// It has to carry the restart, because this text is the only thing standing between a
        /// host and the one way the entry looks broken: BepInEx 5.4 parses a .cfg once, while
        /// the ConfigFile is being constructed, and keeps no watcher on the file, so a host who
        /// edits this while the server runs and then spawns sees nothing change.
        /// </para>
        /// </summary>
        internal const string ConfigDescription =
            "Mark everything this plugin spawns as summoned through cheating.\n" +
            "Valheim stamps that flag on every object its own spawn command conjures. An item carrying it\n" +
            "says \"This item was summoned through cheating means.\" in its tooltip, and while one sits in a\n" +
            "player's inventory that player's achievement progress is paused. A creature carrying it hands\n" +
            "the flag on to whatever it drops when it dies.\n" +
            "BakaLoader leaves the flag off by default so a host replacing lost gear does not quietly cost\n" +
            "somebody their achievements. Set this to true to spawn the way the game's own console command\n" +
            "does.\n" +
            "Changing this takes effect the next time the server starts: BepInEx reads this file once,\n" +
            "while the server is starting, and does not watch it afterwards.\n" +
            "Items and creatures that were spawned before you changed this keep the mark they were given:\n" +
            "it is stored inside each object, not read back from this setting.";

        /// <summary>
        /// True when the object about to be conjured should carry the cheated mark.
        /// <para>
        /// Two things have to agree. The host has to have asked for it, and the game's own
        /// cheat check bypass has to be off. Vanilla writes plain "not bypassed"; this adds
        /// the host's answer in front of it, so turning the entry on restores exactly what
        /// the game does and leaving it off marks nothing at all.
        /// </para>
        /// <para>
        /// The bypass half is kept rather than dropped because a server running with cheat
        /// checks bypassed has already decided none of this counts, and writing the mark
        /// there would be noise on objects nothing will ever read it off.
        /// </para>
        /// </summary>
        /// <param name="configMarkSpawned">What the host set <see cref="ConfigKey"/> to.</param>
        /// <param name="bypassed">PlayerProfile.s_bypassCheatChecks as the live game reports it.</param>
        internal static bool ShouldMark(bool configMarkSpawned, bool bypassed)
        {
            if (!configMarkSpawned) return false;
            return !bypassed;
        }
    }
}
