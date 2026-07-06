using System.Collections.Generic;

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
    }
}
