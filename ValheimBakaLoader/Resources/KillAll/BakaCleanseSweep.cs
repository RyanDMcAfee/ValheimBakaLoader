#if VALHEIM_PLUGIN
// BakaLoader: baka_cleanse, the sweep that takes the cheat marks back off a world.
//
// WHY THIS FILE IS FENCED OFF
// It names Valheim's own types, which only exist on a machine with the dedicated server
// installed, so only Resources\build-plugins.ps1 compiles it. That script passes
// /define:VALHEIM_PLUGIN; BakaLoader's own project has no game assemblies and globs this
// folder like any other, so without the fence the app would stop building the moment this
// file landed. Its pure companion BakaCleansePlan.cs carries no fence on purpose: that is
// the half the test suite reads, and it is also where the byte walking lives.
//
// WHERE THE MARK ACTUALLY LIVES, which is four places and not one
//   1. On the object itself, as the ZDO bool ZDOVars.s_cheated. Creatures, pieces, crafting
//      stations, containers and the locations the world spawns all carry it there.
//   2. In a station's queue, as ZDOVars.s_cheatedQueued. A smelter and a fermenter use the
//      key itself; a cooking station uses the key plus the slot number (decompiled
//      assembly_valheim, CookingStation.SetSlot at 122434), so a run of them is cleared.
//   3. Inside a container, as bit 0 of the last byte of every item record in the items blob
//      under ZDOVars.s_items.
//   4. Inside one item on the ground, on an armour stand or on an item stand, in the same
//      last byte of the one record under ZDOVars.s_itemData, or under "<slot>_itemData" for
//      a stand that holds several (ItemDrop.SaveToZDO at 70622-70642).
// Clearing only the first of those would cure about a quarter of it and would read, to a
// host, as a command that does not work.
//
// WHAT IT CANNOT REACH, and says so in its own answer
// A player's own inventory is not in the world at all. Player.Save writes it into that
// player's character file on their own machine (Player.Save at 14088-14097), and the only
// two callers of Inventory.Save in the whole game are that one and Container.Save. So a
// host has to put their backpack into a chest and take it back out afterwards, and the
// confirm dialog in the app says exactly that. A character the game has flagged for console
// use is the same kind of thing and cannot be cleared by anybody.
//
// WHY IT REFUSES WHILE ANYBODY IS CONNECTED
// A container rewrites its own contents from the ZDO only when its data revision has moved
// AND nobody has it open: Container.Load returns false while m_inUse is true (121592). A
// chest somebody is standing in would take the rewrite and then have it written straight
// back over by the next save from their client. An empty server has no such race in it.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BakaLoaderKillAll
{
    /// <summary>
    /// The cheat-mark sweep. One pass over every ZDO in the world, on the main thread,
    /// answered with the counts. Both plugins hold it: BakaLoaderCommander calls it for an
    /// RCON line, BakaKillAll for a line typed at the server console.
    /// </summary>
    internal static class CleanseSweep
    {
        /// <summary>
        /// How many cooking-station slots are cleared. Vanilla's largest has five; a run to
        /// eight covers a mod that added a couple without reading a prefab per ZDO.
        /// </summary>
        private const int MaxQueuedSlots = 8;

        /// <summary>
        /// How many "&lt;slot&gt;_itemData" keys a stand is checked for. Vanilla's armour
        /// stand has seven; ten leaves room and costs one dictionary lookup each.
        /// </summary>
        private const int MaxStandSlots = 10;

        private static bool _indexProbed;
        private static FieldInfo _indexField;

        private static int[] _standKeys;

        /// <summary>
        /// Runs the sweep, or refuses. Never throws: every caller is a command line, and a
        /// command that throws tells the host nothing.
        /// </summary>
        internal static string Run(Action<string> log)
        {
            try
            {
                if (ZNet.instance == null || ZDOMan.instance == null) return CleansePlan.NotReady;

                // Nobody on the server, and the names of whoever is, because "wait until it
                // is empty" is not something a host can act on without them.
                var names = new List<string>();
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || !peer.IsReady()) continue;
                    names.Add(string.IsNullOrEmpty(peer.m_playerName) ? "(unnamed)" : peer.m_playerName);
                }

                if (names.Count > 0) return CleansePlan.Connected(names.Count, string.Join(", ", names.ToArray()));

                if (!_indexProbed)
                {
                    _indexProbed = true;
                    _indexField = AccessTools.DeclaredField(typeof(ZDOMan), "m_objectsByID");
                }

                if (_indexField == null) return CleansePlan.NoIndex;

                // IDictionary rather than Dictionary<ZDOID, ZDO>, exactly as the kill-all
                // sweep reads it: a generic instantiation over the game's own ZDOID would
                // have to be compiled against it.
                var index = _indexField.GetValue(ZDOMan.instance) as IDictionary;
                if (index == null) return CleansePlan.NoIndex;

                if (_standKeys == null)
                {
                    _standKeys = new int[MaxStandSlots];
                    for (var slot = 0; slot < MaxStandSlots; slot++)
                        _standKeys[slot] = (slot + "_itemData").GetStableHashCode();
                }

                var zdosCleared = 0;
                var containers = 0;
                var items = 0;

                // One uninterrupted pass. Nothing here adds or removes a ZDO, so the walk
                // cannot invalidate the enumerator the way the kill-all sweep's own kills
                // would; the snapshot that sweep takes is for a different reason than this.
                foreach (var value in index.Values)
                {
                    var zdo = value as ZDO;
                    if (zdo == null || !zdo.IsValid()) continue;

                    var touched = false;

                    // 1. The object's own mark.
                    if (zdo.GetBool(ZDOVars.s_cheated, false))
                    {
                        zdo.RemoveBool(ZDOVars.s_cheated);
                        zdosCleared++;
                        touched = true;
                    }

                    // 2. The station queues. Slot zero IS the bare key, so a smelter and a
                    //    fermenter are covered by the same run. Only a key that is really
                    //    there and really true is removed, which is what keeps a hash that
                    //    happens to land here from losing an unrelated value.
                    for (var slot = 0; slot < MaxQueuedSlots; slot++)
                    {
                        var key = ZDOVars.s_cheatedQueued + slot;
                        if (!zdo.GetBool(key, false)) continue;
                        zdo.RemoveBool(key);
                        zdosCleared++;
                        touched = true;
                    }

                    // 3. A container's contents.
                    var blob = zdo.GetByteArray(ZDOVars.s_items);
                    if (blob != null)
                    {
                        byte[] rewritten;
                        int cleared;
                        if (CleanseBlob.ClearInventory(blob, out rewritten, out cleared) && cleared > 0)
                        {
                            zdo.Set(ZDOVars.s_items, rewritten);
                            containers++;
                            items += cleared;
                            touched = true;
                        }
                    }

                    // 4. One item on the ground, and each slot of a stand.
                    if (ClearItemBlob(zdo, ZDOVars.s_itemData, ref items)) touched = true;
                    for (var slot = 0; slot < MaxStandSlots; slot++)
                        if (ClearItemBlob(zdo, _standKeys[slot], ref items)) touched = true;

                    // A removal does not move the data revision on its own, so the sector is
                    // marked here: that is what puts the change in front of the next save and
                    // of anybody who connects after it.
                    if (touched) ZDOMan.instance.SetDirtySector(zdo);
                }

                var reply = (zdosCleared == 0 && containers == 0 && items == 0)
                    ? CleansePlan.NothingToDo()
                    : CleansePlan.Reply(zdosCleared, containers, items);

                // Said in the log as well as answered, because a world big enough for the
                // pass to outlast the RCON client's patience still finishes, and the counts
                // are the only way a host can tell that it did.
                if (log != null) log(reply);
                return reply;
            }
            catch (Exception ex)
            {
                var said = "Error: the cleanse could not finish: " + ex.Message;
                if (log != null) log(said);
                return said;
            }
        }

        /// <summary>One item blob under one key, cleared in place. True when anything changed.</summary>
        private static bool ClearItemBlob(ZDO zdo, int key, ref int items)
        {
            var blob = zdo.GetByteArray(key);
            if (blob == null) return false;

            byte[] rewritten;
            int cleared;
            if (!CleanseBlob.ClearItemData(blob, out rewritten, out cleared) || cleared == 0) return false;

            zdo.Set(key, rewritten);
            items += cleared;
            return true;
        }
    }
}
#endif
