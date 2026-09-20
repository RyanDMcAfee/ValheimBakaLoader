using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// The world-generation vocabulary the dedicated server accepts on its
    /// command line (-preset / -modifier / -setkey). Every string here is a
    /// game-defined identifier and must match Valheim exactly.
    /// </summary>
    public static class WorldGen
    {
        /// <summary>Difficulty bundles; mutually exclusive with individual modifiers.</summary>
        public static readonly IReadOnlyList<string> Presets = new[]
        {
            "normal", "casual", "easy", "hard", "hardcore", "immersive", "hammer",
        };

        /// <summary>Each tweakable dial, mapped to the values the game allows for it.</summary>
        public static readonly IReadOnlyDictionary<string, string[]> Modifiers = new Dictionary<string, string[]>
        {
            ["combat"] = new[] { "veryeasy", "easy", "hard", "veryhard" },
            ["deathpenalty"] = new[] { "casual", "veryeasy", "easy", "hard", "hardcore" },
            ["resources"] = new[] { "muchless", "less", "more", "muchmore", "most" },
            ["raids"] = new[] { "none", "muchless", "less", "more", "muchmore" },
            ["portals"] = new[] { "casual", "hard", "veryhard" },
        };

        /// <summary>Boolean world switches (the old console "keys").</summary>
        public static readonly IReadOnlyList<string> Switches = new[]
        {
            "nobuildcost", "playerevents", "passivemobs", "nomap", "fire",
        };

        /// <summary>
        /// The starting keys the game itself writes into a world for one dial value: the dial,
        /// then the value, then every key that value stands for. A value not listed here writes
        /// no keys at all, which is what Normal is on every dial.
        /// <para>
        /// This table exists because a dial is not stored anywhere in a world file. The game
        /// keeps only the keys, so reading a world's own settings back means recognising the
        /// key SET a dial value writes, and BakaLoader has to be able to do that on a world it
        /// did not make (<see cref="WorldKeyImport"/>). The page draws the same sets as fine
        /// print under each dial, and a test holds this table against that copy so the two
        /// cannot drift.
        /// </para>
        /// <para>
        /// Keys are spelled the way the game writes them into the header: all lower case, and a
        /// value-carrying key as one string with a single space in it ("resourcerate 150"). The
        /// game splits on that space itself, both when it replaces a key of the same name and
        /// when it reads a value back, so the space is part of the format and not a separator
        /// BakaLoader invented.
        /// </para>
        /// </summary>
        public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> DialKeys =
            new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.Ordinal)
            {
                ["combat"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["veryeasy"] = new[] { "playerdamage 125", "enemydamage 50", "enemyspeedsize 90" },
                    ["easy"] = new[] { "playerdamage 110", "enemydamage 75", "enemyspeedsize 95" },
                    ["hard"] = new[] { "playerdamage 85", "enemydamage 150", "enemyspeedsize 110", "enemyleveluprate 120" },
                    ["veryhard"] = new[] { "playerdamage 70", "enemydamage 200", "enemyspeedsize 120", "enemyleveluprate 140" },
                },
                ["deathpenalty"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["casual"] = new[] { "deathkeepequip", "skillreductionrate 15" },
                    ["veryeasy"] = new[] { "skillreductionrate 15" },
                    ["easy"] = new[] { "skillreductionrate 50" },
                    ["hard"] = new[] { "deathdeleteunequipped", "skillreductionrate 150" },
                    ["hardcore"] = new[] { "deathdeleteitems", "deathskillsreset" },
                },
                ["resources"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["muchless"] = new[] { "resourcerate 50" },
                    ["less"] = new[] { "resourcerate 75" },
                    ["more"] = new[] { "resourcerate 150" },
                    ["muchmore"] = new[] { "resourcerate 200" },
                    ["most"] = new[] { "resourcerate 300" },
                },
                ["raids"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["none"] = new[] { "eventrate 0" },
                    ["muchless"] = new[] { "eventrate 200" },
                    ["less"] = new[] { "eventrate 150" },
                    ["more"] = new[] { "eventrate 60" },
                    ["muchmore"] = new[] { "eventrate 30" },
                },
                ["portals"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["casual"] = new[] { "teleportall" },
                    ["hard"] = new[] { "nobossportals" },
                    ["veryhard"] = new[] { "noportals" },
                },
            };

        /// <summary>
        /// The name half of a starting key: everything before the first space, lower case.
        /// "resourcerate 150" names resourcerate, and so does "resourcerate 300", which is why
        /// the game replaces one with the other rather than keeping both.
        /// </summary>
        public static string KeyName(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            var trimmed = key.Trim();
            var space = trimmed.IndexOf(' ');
            return (space < 0 ? trimmed : trimmed.Substring(0, space)).ToLowerInvariant();
        }

        /// <summary>
        /// The game's own summary line. When the world-modifier screen writes keys after a dial has
        /// been moved, or after a preset button, it also writes one "preset ..." key describing the
        /// whole choice, for its own menu to read back. A world whose only change is a toggle gets
        /// none. It is a label for the other keys rather than a setting of its own, so nothing here
        /// ever imports one or sends one back to the game.
        /// </summary>
        public static bool IsPresetSummary(string key)
            => string.Equals(KeyName(key), "preset", StringComparison.Ordinal);

        /// <summary>
        /// One of the five boolean switches, whatever case it arrived in. A switch is the bare
        /// name and nothing else: "nomap" is one and "nomap 5" is a value key that happens to
        /// share the name, so it is carried through rather than drawn as a toggle.
        /// </summary>
        public static bool IsSwitch(string key)
        {
            var normalised = (key ?? "").Trim().ToLowerInvariant();
            return normalised.Length > 0 && Switches.Contains(normalised, StringComparer.Ordinal);
        }
    }
}
