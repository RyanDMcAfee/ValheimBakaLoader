using System.Collections.Generic;
using System.Globalization;

namespace ValheimBakaLoader.Tools.Atlas
{
    /// <summary>
    /// Vanilla location prefab names keyed by their save-file hash.
    ///
    /// Up to world version 39 the location list inside a world save stored the
    /// prefab NAME as a string. Valheim 1.0 (world version 41) writes
    /// name.GetStableHashCode() instead (ZoneSystem.Save, decomp_new :112978),
    /// and the names themselves live in Unity-serialized assets rather than in
    /// any assembly, so they cannot be recovered from the save alone. This
    /// table maps the hash back to the name for every location prefab a
    /// vanilla world can contain.
    ///
    /// How the table was built (so it can be rebuilt after a content patch):
    /// the 146 names shared with pre-1.0 saves came from the string-named
    /// location list of a real world version 37 save, and the 32 Deep North
    /// additions came from the shipped 1.0 asset manifest. Every entry was
    /// verified to hash to a location hash actually present in a real 1.0
    /// _main.N.db2, so there are no guessed names here.
    /// </summary>
    public static class WorldLocationNames
    {
        /// <summary>
        /// Every vanilla location prefab name seen in a freshly generated
        /// Valheim 1.0 world (178 of them, all hash-verified).
        /// </summary>
        private static readonly string[] Vanilla =
        {
            "AbandonedLogCabin02", "AbandonedLogCabin03", "AbandonedLogCabin04", "AncientUpgradeStation",
            "AshlandRuins", "BearCave", "BigRockClearing", "BogWitch_Camp",
            "Bonemass", "CharredFortress", "CharredRuins1", "CharredRuins2",
            "CharredRuins3", "CharredRuins4", "CharredStone_Spawner", "CharredTowerRuins1",
            "CharredTowerRuins1_dvergr", "CharredTowerRuins2", "CharredTowerRuins3", "CombatRuin01",
            "Crypt2", "Crypt3", "Crypt4", "DN_Bossroom",
            "DN_gammeltrollFrac01", "DN_gammeltrollFrac02", "DN_hut01", "Dolmen01",
            "Dolmen02", "Dolmen03", "Dragonqueen", "DrakeLorestone",
            "DrakeNest01", "Eikthyrnir", "FaderLocation", "FireHole",
            "FortressRuins", "FrozenShip01_DN", "FrozenShip02_DN", "FrozenShip03_DN",
            "GDKing", "GoblinCamp2", "GoblinHut01", "GoblinHut02",
            "GoblinHut03", "GoblinKing", "Grave1", "Greydwarf_camp1",
            "Hildir_camp", "Hildir_cave", "Hildir_crypt", "Hildir_plainsfortress",
            "IcePond1", "InfestedTree01", "LeviathanLava", "LumberCamp",
            "Mistlands_DvergrBossEntrance1", "Mistlands_DvergrTownEntrance1", "Mistlands_DvergrTownEntrance2", "Mistlands_Excavation1",
            "Mistlands_Excavation2", "Mistlands_Excavation3", "Mistlands_Giant1", "Mistlands_Giant2",
            "Mistlands_GuardTower1_new", "Mistlands_GuardTower1_ruined_new", "Mistlands_GuardTower1_ruined_new2", "Mistlands_GuardTower2_new",
            "Mistlands_GuardTower3_new", "Mistlands_GuardTower3_ruined_new", "Mistlands_Harbour1", "Mistlands_Lighthouse1_new",
            "Mistlands_RoadPost1", "Mistlands_RockSpire1", "Mistlands_Statue1", "Mistlands_Statue2",
            "Mistlands_StatueGroup1", "Mistlands_Swords1", "Mistlands_Swords2", "Mistlands_Swords3",
            "Mistlands_Viaduct1", "Mistlands_Viaduct2", "MorgenHole1", "MorgenHole2",
            "MorgenHole3", "MorkBorg", "MountainCave02", "MountainGrave01",
            "MountainWell1", "NorthMemorialPlace", "NorthVillage", "PlaceofMystery1",
            "PlaceofMystery2", "PlaceofMystery3", "Ruin1", "Ruin2",
            "Ruin3", "Runestone_Ashlands", "Runestone_BlackForest", "Runestone_Boars",
            "Runestone_DeepNorth", "Runestone_Draugr", "Runestone_Greydwarfs", "Runestone_Meadows",
            "Runestone_Mistlands", "Runestone_Mountains", "Runestone_Plains", "Runestone_Swamps",
            "ShipSetting01", "ShipSetting02", "ShipSetting03", "ShipWreck01",
            "ShipWreck01_DN", "ShipWreck02", "ShipWreck02_DN", "ShipWreck03",
            "ShipWreck04", "StartTemple", "StoneCircle", "StoneHenge1",
            "StoneHenge2", "StoneHenge3", "StoneHenge4", "StoneHenge5",
            "StoneHenge6", "StoneHouse3", "StoneHouse4", "StoneTower1",
            "StoneTower3", "StoneTowerRuins03", "StoneTowerRuins04", "StoneTowerRuins05",
            "StoneTowerRuins07", "StoneTowerRuins07_sunk", "StoneTowerRuins08", "StoneTowerRuins08_sunk",
            "StoneTowerRuins09", "StoneTowerRuins09_sunk", "StoneTowerRuins10", "StoneTowerRuins10_sunk",
            "SulfurArch", "SunkenCrypt4", "SwampHut1", "SwampHut1_1",
            "SwampHut2", "SwampHut2_1", "SwampHut3", "SwampHut3_1",
            "SwampHut4", "SwampHut5", "SwampRuin1", "SwampRuin2",
            "SwampWell1", "TarPit1", "TarPit2", "TarPit3",
            "TheHole01", "TrollCave02", "Vendor_BlackForest", "VoltureNest",
            "Waymarker01", "Waymarker02", "WoodFarm1", "WoodHouse1",
            "WoodHouse10", "WoodHouse11", "WoodHouse12", "WoodHouse13",
            "WoodHouse2", "WoodHouse3", "WoodHouse4", "WoodHouse5",
            "WoodHouse6", "WoodHouse7", "WoodHouse8", "WoodHouse9",
            "WoodVillage1", "WoodVillage2",
        };

        private static readonly Dictionary<int, string> ByHash = BuildIndex();

        private static Dictionary<int, string> BuildIndex()
        {
            var map = new Dictionary<int, string>(Vanilla.Length);
            foreach (string name in Vanilla)
            {
                map[FwlWriter.GetStableHashCode(name)] = name;
            }
            return map;
        }

        /// <summary>Number of known vanilla location prefabs.</summary>
        public static int Count => ByHash.Count;

        /// <summary>True when the hash belongs to a known vanilla location prefab.</summary>
        public static bool TryGetName(int hash, out string name)
        {
            return ByHash.TryGetValue(hash, out name);
        }

        /// <summary>
        /// Prefab name for a saved location hash. Unknown hashes (modded
        /// locations, or vanilla content added after this table was built)
        /// come back as the plain decimal hash so nothing is silently lost.
        /// </summary>
        public static string Resolve(int hash)
        {
            if (ByHash.TryGetValue(hash, out string name))
            {
                return name;
            }
            return hash.ToString(CultureInfo.InvariantCulture);
        }
    }
}
