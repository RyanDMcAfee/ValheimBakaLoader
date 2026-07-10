using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ValheimBakaLoader.Tools.Atlas
{
    /// <summary>
    /// Result of scanning a server install for mods that alter worldgen or
    /// weather. Tier 1 mods (Expand World Size/Data) are adapted to via their
    /// parseable configs; Tier 2 mods can't be modeled, so the Atlas UI shows
    /// an honest "may differ" badge listing them.
    /// </summary>
    public sealed class ModCompatResult
    {
        /// <summary>Playable world radius (vanilla 10000).</summary>
        public float WorldRadius = 10000f;

        /// <summary>Total radius including the edge band (vanilla 10500).</summary>
        public float WorldEdge = 10500f;

        /// <summary>Expand World Size "world stretch" factor (vanilla 1).</summary>
        public float WorldStretch = 1f;

        /// <summary>Expand World Size "biome stretch" factor (vanilla 1).</summary>
        public float BiomeStretch = 1f;

        public bool HasExpandWorldSize;
        public bool HasExpandWorldData;

        /// <summary>Expand World Data yaml directory, when present (consumed by the weather engine for env-table overrides).</summary>
        public string ExpandWorldDataDir;

        /// <summary>Mods detected that the renderer/forecast cannot model — shown verbatim in the UI badge.</summary>
        public readonly List<string> Warnings = new List<string>();

        public bool IsVanilla => !HasExpandWorldSize && !HasExpandWorldData && Warnings.Count == 0;
    }

    /// <summary>
    /// Scans BepInEx/plugins of a server install for mods known to change
    /// map/seed generation or weather dynamics, so the Atlas can adapt where
    /// possible and warn honestly where not. No mods are required — a vanilla
    /// or unmodded install returns a clean vanilla result.
    /// </summary>
    public static class ModCompatScanner
    {
        // Tier 2: detected → explicit warning, no adaptation possible.
        private static readonly (string Token, string Warning)[] WarnMods =
        {
            ("bettercontinents", "Better Continents — world layout is custom; the rendered map may not match."),
            ("seasons", "Seasons — weather/environment cycles are modified; forecasts may differ."),
            ("seasonality", "Seasonality — weather/environment visuals are modified; forecasts may differ."),
        };

        public static ModCompatResult Scan(string serverInstallDir)
        {
            var result = new ModCompatResult();
            if (string.IsNullOrWhiteSpace(serverInstallDir))
            {
                return result;
            }

            string pluginsDir = Path.Combine(serverInstallDir, "BepInEx", "plugins");
            string configDir = Path.Combine(serverInstallDir, "BepInEx", "config");
            if (!Directory.Exists(pluginsDir))
            {
                return result; // no BepInEx → pure vanilla
            }

            string[] dlls;
            try
            {
                dlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                return result;
            }

            foreach (string dll in dlls)
            {
                string name = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();

                if (name.Contains("expand_world_size") || name == "expandworldsize")
                {
                    result.HasExpandWorldSize = true;
                    continue;
                }
                if (name.Contains("expand_world_data") || name == "expandworlddata")
                {
                    result.HasExpandWorldData = true;
                    continue;
                }

                foreach (var (token, warning) in WarnMods)
                {
                    if (warning != null && name.Contains(token))
                    {
                        if (!result.Warnings.Contains(warning))
                        {
                            result.Warnings.Add(warning);
                        }
                        break;
                    }
                }
            }

            if (result.HasExpandWorldSize)
            {
                ApplyExpandWorldSize(result, configDir);
            }

            if (result.HasExpandWorldData)
            {
                string ewdDir = Path.Combine(configDir, "expand_world");
                if (Directory.Exists(ewdDir))
                {
                    result.ExpandWorldDataDir = ewdDir;
                    // Biome-layout yaml can redraw the entire biome map — not modelable.
                    string biomesYaml = Path.Combine(ewdDir, "expand_biomes.yaml");
                    if (File.Exists(biomesYaml) && !IsEffectivelyEmpty(biomesYaml))
                    {
                        result.Warnings.Add("Expand World Data — custom biome layout; the rendered map may not match.");
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Reads world radius / edge / stretch factors from expand_world_size.cfg
        /// (BepInEx "Key = Value" format). Missing file or keys → vanilla values.
        /// </summary>
        private static void ApplyExpandWorldSize(ModCompatResult result, string configDir)
        {
            string cfg = Path.Combine(configDir, "expand_world_size.cfg");
            if (!File.Exists(cfg))
            {
                return;
            }

            float radius = 10000f;
            float edgeSize = 500f;
            try
            {
                foreach (string raw in File.ReadLines(cfg))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == '[')
                    {
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    {
                        continue;
                    }
                    switch (key)
                    {
                        case "world radius":
                            radius = f;
                            break;
                        case "world edge size":
                            edgeSize = f;
                            break;
                        case "world stretch":
                            if (f > 0f) result.WorldStretch = f;
                            break;
                        case "biome stretch":
                            if (f > 0f) result.BiomeStretch = f;
                            break;
                    }
                }
            }
            catch (Exception)
            {
                return;
            }

            result.WorldRadius = radius;
            result.WorldEdge = radius + edgeSize;
        }

        private static bool IsEffectivelyEmpty(string yamlPath)
        {
            try
            {
                foreach (string raw in File.ReadLines(yamlPath))
                {
                    string line = raw.Trim();
                    if (line.Length > 0 && line[0] != '#')
                    {
                        return false;
                    }
                }
            }
            catch (Exception)
            {
            }
            return true;
        }
    }
}
