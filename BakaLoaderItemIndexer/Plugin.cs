using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace BakaLoaderItemIndexer
{
    /// <summary>
    /// Tiny companion plugin for Valheim BakaLoader. After the game's ObjectDB and ZNetScene
    /// are ready it walks every item and creature prefab, resolves their localized name and
    /// description, classifies them, and writes the result to <c>&lt;BepInEx&gt;/items.json</c>.
    /// BakaLoader reads that file to power its mod-aware "Spawn X at" picker.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class IndexerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.bakaloader.itemindexer";
        public const string PluginName = "BakaLoader Item Indexer";
        public const string PluginVersion = "1.0.0";

        private static bool _written;

        private void Awake()
        {
            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(IndexerPlugin));
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        // ObjectDB.Awake runs once the item database is populated. We additionally require
        // ZNetScene for creature prefabs, so we try after both have initialised.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
        private static void OnObjectDbAwake()
        {
            TryWriteCatalog();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        private static void OnZNetSceneAwake()
        {
            TryWriteCatalog();
        }

        private static void TryWriteCatalog()
        {
            if (_written) return;

            var odb = ObjectDB.instance;
            if (odb == null || odb.m_items == null || odb.m_items.Count == 0) return;

            try
            {
                var entries = new List<CatalogEntry>();
                CollectItems(odb, entries);
                CollectCreatures(entries);

                if (entries.Count == 0) return;

                var path = Path.Combine(Paths.BepInExRootPath, "items.json");
                File.WriteAllText(path, Serialize(entries), Encoding.UTF8);

                _written = true;
                Debug.Log($"[{PluginName}] Wrote {entries.Count} entries to {path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{PluginName}] Failed to write items.json: {e.Message}");
            }
        }

        private static void CollectItems(ObjectDB odb, List<CatalogEntry> entries)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var go in odb.m_items)
            {
                if (go == null) continue;

                var drop = go.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) continue;

                var prefab = go.name;
                if (string.IsNullOrEmpty(prefab) || !seen.Add(prefab)) continue;

                var shared = drop.m_itemData.m_shared;
                var name = Localize(shared.m_name, prefab);
                var description = Localize(shared.m_description, string.Empty);

                var hasQuality = shared.m_maxQuality > 1;
                var category = ClassifyItem(shared.m_itemType, hasQuality);

                entries.Add(new CatalogEntry
                {
                    Prefab = prefab,
                    Name = name,
                    Description = description,
                    Category = category,
                    HasQuality = category == "Equipment" && hasQuality,
                    HasLevel = false,
                });
            }
        }

        private static void CollectCreatures(List<CatalogEntry> entries)
        {
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null) return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries) seen.Add(e.Prefab);

            foreach (var go in scene.m_prefabs)
            {
                if (go == null) continue;

                var character = go.GetComponent<Character>();
                if (character == null) continue;

                var prefab = go.name;
                if (string.IsNullOrEmpty(prefab) || !seen.Add(prefab)) continue;

                var name = Localize(character.m_name, prefab);

                entries.Add(new CatalogEntry
                {
                    Prefab = prefab,
                    Name = name,
                    Description = string.Empty,
                    Category = "Creature",
                    HasQuality = false,
                    HasLevel = true,
                });
            }
        }

        private static string ClassifyItem(ItemDrop.ItemData.ItemType type, bool hasQuality)
        {
            switch (type)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                    return "Equipment";
                case ItemDrop.ItemData.ItemType.Material:
                    return "Resource";
                default:
                    return "Item";
            }
        }

        private static string Localize(string token, string fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(token)) return fallback;
                if (Localization.instance == null) return fallback;
                var localized = Localization.instance.Localize(token);
                return string.IsNullOrWhiteSpace(localized) ? fallback : localized.Trim();
            }
            catch
            {
                return fallback;
            }
        }

        private static string Serialize(List<CatalogEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.Append("  {");
                sb.Append("\"prefab\":\"").Append(Escape(e.Prefab)).Append("\",");
                sb.Append("\"name\":\"").Append(Escape(e.Name)).Append("\",");
                sb.Append("\"description\":\"").Append(Escape(e.Description)).Append("\",");
                sb.Append("\"category\":\"").Append(e.Category).Append("\",");
                sb.Append("\"hasQuality\":").Append(e.HasQuality ? "true" : "false").Append(',');
                sb.Append("\"hasLevel\":").Append(e.HasLevel ? "true" : "false");
                sb.Append('}');
                if (i < entries.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("]\n");
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private class CatalogEntry
        {
            public string Prefab;
            public string Name;
            public string Description;
            public string Category;
            public bool HasQuality;
            public bool HasLevel;
        }
    }
}
