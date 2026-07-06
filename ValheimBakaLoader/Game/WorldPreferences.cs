using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// Saved world-generation choices for one world, keyed by world name.
    /// Round-trips to disk through <see cref="WorldPreferencesFile"/>.
    /// </summary>
    public class WorldPreferences : INamedEntry
    {
        string INamedEntry.EntryName => WorldName;

        public string WorldName { get; set; }

        public DateTime LastSaved { get; set; } = DateTime.UnixEpoch;

        public string Preset { get; set; }

        public Dictionary<string, string> Modifiers { get; set; } = new();

        public HashSet<string> Keys { get; set; } = new();

        public static WorldPreferences FromFile(WorldPreferencesFile file)
        {
            var defaults = new WorldPreferences();
            if (file is null) return defaults;

            return new WorldPreferences
            {
                WorldName = file.WorldName,
                LastSaved = file.LastSaved ?? defaults.LastSaved,
                Preset = file.Preset,
                Modifiers = file.Modifiers ?? new(),
                Keys = file.Keys?.ToHashSet() ?? new(),
            };
        }

        public WorldPreferencesFile ToFile() => new()
        {
            WorldName = WorldName,
            LastSaved = LastSaved,
            Preset = Preset,
            Modifiers = Modifiers,
            Keys = Keys?.ToList(),
        };
    }
}
