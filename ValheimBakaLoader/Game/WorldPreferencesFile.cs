using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// On-disk shape of one world entry inside userprefs.json. Values are
    /// nullable/optional so missing keys fall back to defaults on load. JSON
    /// key names are a compatibility contract - never rename them.
    /// </summary>
    public class WorldPreferencesFile
    {
        [JsonProperty("worldName")] public string WorldName { get; set; }
        [JsonProperty("lastSaved")] public DateTime? LastSaved { get; set; }

        // -- World generation --

        [JsonProperty("preset")] public string Preset { get; set; }
        [JsonProperty("modifiers")] public Dictionary<string, string> Modifiers { get; set; }
        [JsonProperty("keys")] public List<string> Keys { get; set; }
    }
}
