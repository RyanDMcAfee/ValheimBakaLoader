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
        public const string PluginVersion = "1.2.0";

        /// <summary>
        /// How many entries the file on disk currently holds. A later pass only rewrites when
        /// it has MORE than this, which is what lets a plugin that adds prefabs in a later
        /// ObjectDB.Awake postfix still make it into the catalog. Zero means nothing written.
        /// </summary>
        private static int _writtenCount;

        private void Awake()
        {
            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(IndexerPlugin));
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        // ObjectDB.Awake runs once the item database is populated. We additionally require
        // ZNetScene for creature prefabs, so we try after both have initialised.
        //
        // Both Awake methods are private on the game's shipped assemblies, so they are named
        // by string rather than nameof - the same approach BakaLoaderMaxPlayers uses. Naming
        // them any other way does not compile against the raw, non-publicized server DLLs.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        private static void OnObjectDbAwake()
        {
            TryWriteCatalog();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static void OnZNetSceneAwake()
        {
            TryWriteCatalog();
        }

        private static void TryWriteCatalog()
        {
            var odb = ObjectDB.instance;
            if (odb == null || odb.m_items == null || odb.m_items.Count == 0) return;

            // Both halves or nothing. Awake order across ObjectDB and ZNetScene is Unity
            // component order, not a guarantee, so writing on whichever fires first can pin a
            // catalog with every creature missing - and the picker would never show one again.
            if (ZNetScene.instance == null || ZNetScene.instance.m_prefabs == null) return;

            try
            {
                var entries = new List<CatalogEntry>();
                CollectItems(odb, entries);
                var items = entries.Count;
                CollectCreatures(entries);
                var creatures = entries.Count - items;

                if (entries.Count == 0) return;

                // Another plugin adding prefabs in a later postfix is exactly the case this
                // catalog exists for, so a bigger catalog always replaces a smaller one.
                if (entries.Count <= _writtenCount) return;

                var path = Path.Combine(Paths.BepInExRootPath, "items.json");
                WriteAtomically(path, Serialize(entries));

                _writtenCount = entries.Count;
                Debug.Log($"[{PluginName}] Wrote {entries.Count} entries ({items} items, {creatures} creatures) to {path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{PluginName}] Failed to write items.json: {e.Message}");
            }
        }

        /// <summary>
        /// Writes through a sibling temp file and then renames, so BakaLoader can never read a
        /// half-written catalog and cache it.
        /// </summary>
        private static void WriteAtomically(string path, string content)
        {
            var temp = path + ".tmp";

            try
            {
                File.WriteAllText(temp, content, Encoding.UTF8);

                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
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
                // Hands is the one entry here that IsEquipable does NOT return true for. It is a
                // real gear slot that mods use for gauntlets, so it is kept on purpose.
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                // The game's own test for equipment is ItemData.IsEquipable, and these three
                // were missing from this list while it returns true for all of them. The caller
                // only keeps a quality value for "Equipment", so a utility item or a trinket
                // with qualities lost the picker's quality box, and arrows were filed as plain
                // items. Utility is the Megingjord and the Wishbone; Ammo is every arrow, bolt
                // and thrown missile.
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.Trinket:
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
