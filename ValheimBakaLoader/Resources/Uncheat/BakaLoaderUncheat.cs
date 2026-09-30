// BakaLoaderUncheat v1.0.0 - takes the cheat mark off what YOUR character is carrying.
//
// WHY A CLIENT PLUGIN AT ALL. BakaLoader 1.0.9 to 1.1.2 marked everything it spawned as
// summoned through cheating means, and Valheim spreads that mark on its own: a marked item
// merged into a stack marks the stack, crafting with a marked ingredient marks what comes out,
// a marked weapon marks what it kills and what that drops. New spawns have been clean since
// 1.2.0 and the server-side sweep (baka_cleanse) takes the marks off the WORLD, but a player's
// own inventory is not in the world. It lives in the character file on their own machine, which
// is why the cleanse says out loud that it cannot reach it, and why the only thing that can is
// something running on the client.
//
// This is that. It goes in the CLIENT's BepInEx/plugins folder, runs once per character, clears
// the mark off everything that character is carrying or wearing, writes one line saying what it
// did, stamps the character, and never runs again for it.
//
// WHAT IT DOES NOT DO. It does not patch anything about spawning, it does not talk to a server,
// it does not read or write a file of its own, and it does nothing at all on a dedicated server.
// The only thing it changes is a bool on items that are already in the player's hands.
//
// The decisions are all in BakaUncheatPlan.cs, which names no Valheim type and is compiled into
// BakaLoader itself: the suite drives the stamp rule, the generation rule and the sweep over a
// synthetic inventory without a game installed. This file is the half that touches the game, and
// it is fenced on VALHEIM_PLUGIN so the app's own build never sees it.

#if VALHEIM_PLUGIN

using System;
using System.Collections.Generic;
using BakaLoaderUncheatPlan;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace BakaLoaderUncheat
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class UncheatPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.baka.uncheat";
        private const string PluginName = "BakaLoader Uncheat";
        private const string PluginVersion = "1.0.0";

        private static ManualLogSource Log;

        /// <summary>
        /// The generation the sweep runs at. Read once while the client is starting, the way
        /// BepInEx 5.4 reads every entry: it keeps no watcher on the .cfg, so a number changed
        /// while the game is running takes effect at the next start.
        /// </summary>
        private static ConfigEntry<int> CfgGeneration;

        private void Awake()
        {
            Log = Logger;

            CfgGeneration = Config.Bind(
                UncheatPlan.ConfigSection,
                UncheatPlan.ConfigKey,
                UncheatPlan.ConfigDefault,
                UncheatPlan.ConfigDescription);

            // One patch, and it is a postfix on the moment a player finishes coming into the
            // world. Not Awake and not Start: at both of those the inventory has not been read
            // off the character file yet, so a sweep there would look at an empty list, clear
            // nothing, and stamp the character as done.
            new Harmony(PluginGuid).PatchAll(typeof(SweepOnSpawn));
        }

        /// <summary>
        /// The sweep, after the local player is loaded.
        /// </summary>
        [HarmonyPatch(typeof(Player), "OnSpawned")]
        private static class SweepOnSpawn
        {
            private static void Postfix(Player __instance)
            {
                try
                {
                    Sweep(__instance);
                }
                catch (Exception problem)
                {
                    // A postfix that throws is a postfix Harmony logs on every call, and this
                    // one is on the road into the world. Nothing here is worth a player not
                    // being able to load their character.
                    if (Log != null) Log.LogWarning("The uncheat sweep did not run: " + problem.Message);
                }
            }
        }

        /// <summary>
        /// One character, swept once. Everything that decides is in UncheatPlan; this reads the
        /// game and writes back to it.
        /// </summary>
        private static void Sweep(Player player)
        {
            // A dedicated server has no local character to sweep, so there is nothing here for
            // this plugin to do and it says so rather than going quiet. ZNet.IsDedicated is an
            // instance method, so the instance is checked first: this runs on the road into the
            // world and ZNet.instance is there by then, but a null here would be an exception
            // in front of a player's character loading.
            if (ZNet.instance != null && ZNet.instance.IsDedicated())
            {
                if (Log != null) Log.LogInfo(UncheatPlan.NothingToDoOnAServer);
                return;
            }

            // The LOCAL player and nobody else. Every other Player object on a client is a peer,
            // whose inventory is not this machine's to rewrite.
            if (player == null || !ReferenceEquals(player, Player.m_localPlayer)) return;

            var generation = CfgGeneration == null ? UncheatPlan.ConfigDefault : CfgGeneration.Value;

            var customData = player.m_customData;
            if (customData == null) return;

            string stamp;
            customData.TryGetValue(UncheatPlan.StampKey, out stamp);

            if (!UncheatPlan.ShouldRun(stamp, generation))
            {
                if (Log != null) Log.LogInfo(UncheatPlan.AlreadyDone(stamp));
                return;
            }

            var inventory = player.GetInventory();
            if (inventory == null) return;

            // GetAllItems already holds what is being worn, because an equipped item in Valheim
            // is an ordinary inventory item with a flag on it. The equipped list is read anyway
            // and merged by reference, so a build where that stopped being true still sweeps
            // what the player is wearing rather than quietly missing it.
            var items = new List<ItemDrop.ItemData>();
            var carried = inventory.GetAllItems();
            if (carried != null) items.AddRange(carried);

            var worn = inventory.GetEquippedItems();
            if (worn != null)
                foreach (var item in worn)
                    if (item != null && !items.Contains(item)) items.Add(item);

            // THE PLAN'S OWN LOOP, not a second copy of it. This hand-rolled the same walk and
            // the same two counters beside a tested one, so the tested code was not the shipped
            // code: a rule anybody changed in the plan would have gone on passing while this
            // went on doing whatever it did. The plan is handed how to read and clear the mark
            // on the game's own item and does the counting itself.
            int looked;
            var cleared = UncheatPlan.ClearAll(
                items, item => item.m_cheated, item => item.m_cheated = false, out looked);

            // The stamp goes down whether anything was cleared or not: a character with no marks
            // on it has been looked at, and looking again on every load would be the same work
            // for the same answer for ever.
            customData[UncheatPlan.StampKey] = UncheatPlan.Stamp(generation);

            if (Log != null) Log.LogInfo(UncheatPlan.Reply(cleared, looked, generation));
        }
    }
}

#endif
