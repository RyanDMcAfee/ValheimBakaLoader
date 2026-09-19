// BakaLoader Spawn Helper v1.5.0 - headless-server-safe spawn via main-thread dispatch.
//
// WHY THIS EXISTS:
// WEC's "spawn_object" crashes dedicated servers because RCON commands execute on a
// ThreadPool socket-callback thread. Object.Instantiate() called from a non-main thread
// can't access the graphics device context, even on a headless server (the main thread
// has a null-safe graphics stub; background threads have nothing → "Graphics device is null"
// → native crash). The normal spawn system (SpawnSystem/ZNetScene) works fine because it
// runs on the main thread during FixedUpdate.
//
// This plugin registers "baka_spawn" which queues the instantiation to run on Update()
// (the main Unity thread), avoiding the crash entirely.
//
// COMMAND:
//   baka_spawn <prefab> <x,z,y> [amount] [level]
//
//   prefab  - exact prefab name (e.g. Boar, SwordIron, Wood)
//   x,z,y   - absolute world coords in Valheim's display order (matches playerlist output)
//   amount  - number to spawn (default 1)
//   level   - 0-based star level for creatures (0=base, 1=1star, 2=2star; default 0),
//             or 1-based quality for items (1=base, 3=a quality 3 tool; 0 leaves it alone)
//
// Stackable items arrive as stacks rather than as one loose drop per unit, so 6 meads are
// one pile of 6 and 150 wood is three stacks of 50.
//
// EXAMPLES:
//   baka_spawn Boar 123.4,567.8,90.1
//   baka_spawn Lox 200,100,50 3 2
//   baka_spawn PickaxeBronze 200,100,50 1 3
//
// CHEATED MARK (v1.5.0):
// Valheim 1.0 shows "This item was summoned through cheating means." on anything its own
// spawn command conjured, and pauses a player's achievement progress while such an item
// sits in their inventory. This plugin does NOT mark its spawns any more. The behaviour is
// one config entry, Spawning/MarkSpawnedAsCheated, default false, and the rule behind it
// lives in BakaSpawnMark.cs so the suite can hold it to account without a game installed.
// The entry is read off disk once, while the server is starting. BepInEx 5.4 keeps no
// watcher on the .cfg, so changing the setting while the server runs does nothing until
// the next start.
// SCOPE: this entry governs the spawns THIS plugin's console command serves, and nothing
// else. BakaLoader's own spawn button goes over RCON to Commander whenever Commander is the
// plugin holding the port, which is the normal case, and Commander then reads its own copy
// of the entry out of com.baka.commander.cfg - a file BakaLoader rewrites on every start,
// which is why the mark cannot presently be turned on for those spawns. See the KNOWN GAP
// note in ..\Commander\BakaLoaderCommander.cs.

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using BakaLoaderSpawn;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace BakaLoaderSpawnHelper
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SpawnHelperPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.baka.spawnhelper";
        private const string PluginName = "BakaLoader Spawn Helper";
        private const string PluginVersion = "1.5.0";

        private static ManualLogSource Log;

        // Bound in Awake, read from MarkAsSpawnedIn, which is static because the spawn loop
        // that calls it is. Held as the entry rather than as a copied bool only so the value
        // lives in one place. That is not a way to pick up a live .cfg edit: BepInEx 5.4
        // parses the file once, while the ConfigFile is being constructed, and keeps no
        // watcher on it, so a setting changed mid-session takes effect at the next start.
        private static ConfigEntry<bool> CfgMarkSpawnedAsCheated;

        private struct SpawnRequest
        {
            public string Prefab;
            public float X, Y, Z; // Unity coords: X=east/west, Y=up/down, Z=north/south
            public int Amount;
            // Creatures: 0-based star level (0=base, 1=1star, 2=2star).
            // Items: 1-based quality (0 leaves the prefab's own quality alone).
            public int Level;
        }

        private static readonly ConcurrentQueue<SpawnRequest> PendingSpawns = new ConcurrentQueue<SpawnRequest>();

        private void Awake()
        {
            Log = Logger;

            // The section, the key, the default and the wording all come from SpawnMark so the
            // two plugins that serve baka_spawn cannot bind the same setting under different
            // names, and so the suite can pin all four without a dedicated server to run.
            CfgMarkSpawnedAsCheated = Config.Bind(
                SpawnMark.ConfigSection,
                SpawnMark.ConfigKey,
                SpawnMark.ConfigDefault,
                SpawnMark.ConfigDescription);

            new Terminal.ConsoleCommand(
                "baka_spawn",
                "baka_spawn <prefab> <x,z,y> [amount] [level] - Headless-safe spawn (main-thread dispatch)",
                delegate(Terminal.ConsoleEventArgs args)
                {
                    // args.Args[0] = "baka_spawn", [1] = prefab, [2] = coords, [3] = amount, [4] = level
                    if (args.Length < 3)
                    {
                        args.Context.AddString("Usage: baka_spawn <prefab> <x,z,y> [amount] [level]");
                        return;
                    }

                    var prefabName = args[1];

                    // Parse coords - format is "x,z,y" (Valheim display order from playerlist)
                    var coordStr = args[2];
                    var parts = coordStr.Split(',');
                    if (parts.Length != 3)
                    {
                        args.Context.AddString("Error: coords must be x,z,y (3 comma-separated values)");
                        return;
                    }

                    if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float xVal) ||
                        !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float zVal) ||
                        !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float yVal))
                    {
                        args.Context.AddString("Error: could not parse coordinates as numbers");
                        return;
                    }

                    int amount = 1;
                    if (args.Length > 3 && !int.TryParse(args[3], out amount))
                        amount = 1;
                    // Match the BakaLoader UI's QuantityField max (9999). Large creature
                    // spawns can tank server perf - the UI is the intended safety rail.
                    amount = Mathf.Clamp(amount, 1, 9999);

                    int level = 0;
                    if (args.Length > 4 && !int.TryParse(args[4], out level))
                        level = 0;
                    level = Mathf.Clamp(level, 0, 10);

                    PendingSpawns.Enqueue(new SpawnRequest
                    {
                        Prefab = prefabName,
                        X = xVal,
                        Y = yVal, // height (vertical)
                        Z = zVal, // north/south
                        Amount = amount,
                        Level = level
                    });

                    args.Context.AddString(level > 0
                        ? $"Queued spawn: {amount}x {prefabName} at level or quality {level}, placed at ({xVal:F1}, {zVal:F1}, {yVal:F1})"
                        : $"Queued spawn: {amount}x {prefabName}, placed at ({xVal:F1}, {zVal:F1}, {yVal:F1})");
                },
                // isCheat MUST stay false. From Valheim 1.0 the game refuses to run any
                // cheat-flagged console command unless the world is already flagged as
                // cheated, and running one flags the profile as having used cheats. This
                // command is a server-operator tool, so it is registered as a plain
                // server-only command and never taints the world or the achievements.
                isCheat: false,
                isNetwork: false,
                onlyServer: true,
                // hideBehindDevCommands is new in Valheim 1.0 and sits between
                // allowInDevBuild and optionsFetcher. Every argument here is named, so the
                // insertion cannot silently shift a value into the wrong slot.
                hideBehindDevCommands: false
            );

            Log.LogInfo("BakaLoader Spawn Helper v" + PluginVersion + " loaded - 'baka_spawn' command registered.");
        }

        private void Update()
        {
            SpawnRequest req;
            while (PendingSpawns.TryDequeue(out req))
            {
                try
                {
                    ExecuteSpawn(req);
                }
                catch (Exception ex)
                {
                    Log.LogError($"Spawn failed for {req.Prefab}: {ex.Message}");
                }
            }
        }

        private void ExecuteSpawn(SpawnRequest req)
        {
            var zns = ZNetScene.instance;
            if (zns == null)
            {
                Log.LogWarning("ZNetScene not ready - spawn skipped");
                return;
            }

            // Resolve prefab from ZNetScene (includes all mod-added prefabs)
            int hash = req.Prefab.GetStableHashCode();
            var prefab = zns.GetPrefab(hash);
            if (prefab == null)
            {
                // Try by name string as fallback
                prefab = zns.GetPrefab(req.Prefab);
            }
            if (prefab == null)
            {
                Log.LogWarning($"Prefab '{req.Prefab}' not found in ZNetScene");
                return;
            }

            // A stackable item spawns as stacks rather than as one loose drop per unit, so the
            // number of objects and the number of units are two different counts from here on.
            bool isItem = prefab.GetComponent<ItemDrop>() != null;
            int maxStack = MaxStackSizeOf(prefab);
            int drops = maxStack > 1 ? Mathf.CeilToInt(req.Amount / (float)maxStack) : req.Amount;

            int remaining = req.Amount;
            int units = 0;
            int objects = 0;
            int quality = 0;

            for (int i = 0; i < drops; i++)
            {
                // Small random offset so multiple spawns don't stack exactly
                var offset = Vector3.zero;
                if (drops > 1)
                {
                    offset = new Vector3(
                        UnityEngine.Random.Range(-1f, 1f),
                        0f,
                        UnityEngine.Random.Range(-1f, 1f)
                    );
                }

                // Unity Vector3: (x, y, z) where y=up
                var pos = new Vector3(req.X + offset.x, req.Y, req.Z + offset.z);

                // Snap to ground height if available
                float groundHeight;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out groundHeight))
                {
                    // Use ground height if it's reasonable (not way below the supplied y)
                    if (groundHeight > pos.y - 5f)
                        pos.y = groundHeight;
                }

                var obj = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
                if (obj == null) continue;

                MarkAsSpawnedIn(obj);

                var item = obj.GetComponent<ItemDrop>();
                if (item != null)
                {
                    int stack = maxStack > 1 ? Mathf.Clamp(remaining, 1, maxStack) : 1;
                    remaining -= stack;
                    quality = SetUpSpawnedItem(obj, item, req.Level, stack);
                    units += stack;
                }
                else
                {
                    // Set creature level (Valheim levels: 1=base, 2=1star, 3=2star)
                    if (req.Level > 0)
                    {
                        var character = obj.GetComponent<Character>();
                        if (character != null)
                        {
                            // SetLevel expects 1-indexed: 1=base, 2=1star, etc.
                            character.SetLevel(req.Level + 1);
                        }
                    }
                    units++;
                }

                objects++;
            }

            string text = $"Spawned {units}x {req.Prefab}";
            if (maxStack > 1 && units > objects)
                text += " as " + objects + (objects == 1 ? " stack" : " stacks");
            // A clamped quality says so. The host asked for 5 and got 4 because that is all a
            // bronze axe has, and a line that just says "at quality 4" looks like the request
            // was misread rather than met as far as the item allows.
            if (quality > 0)
            {
                text += " at quality " + quality;
                if (quality < req.Level) text += " (the most this item allows)";
            }
            else if (!isItem && req.Level > 0)
            {
                text += " at level " + req.Level;
            }

            Log.LogInfo($"{text}, placed at ({req.X:F1}, {req.Z:F1}, {req.Y:F1})");
        }

        /// <summary>
        /// The item's own maximum stack size, or 1 for anything that is not a stackable item.
        /// Read off the prefab before the loop because it decides how many objects to make.
        /// </summary>
        private static int MaxStackSizeOf(GameObject prefab)
        {
            try
            {
                var item = prefab.GetComponent<ItemDrop>();
                if (item == null || item.m_itemData == null || item.m_itemData.m_shared == null) return 1;
                return Mathf.Max(1, item.m_itemData.m_shared.m_maxStackSize);
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>
        /// Finishes a freshly conjured item the way the game's own spawn command finishes one:
        /// full durability, and the requested quality when one was asked for. Quality is set
        /// FIRST because maximum durability grows with it (ItemData.GetMaxDurability(quality)
        /// is m_maxDurability plus m_durabilityPerLevel per level above 1), so a quality 4 axe
        /// handed out at quality 1 durability would arrive visibly worn. The stack size is ours
        /// rather than vanilla's: the console command has a player to hand items to, and this
        /// one drops them on the ground, where 150 separate wood drops is a lag spike and three
        /// stacks of 50 is what the player wanted. Returns the quality that was applied, or 0.
        /// </summary>
        private static int SetUpSpawnedItem(GameObject obj, ItemDrop item, int level, int stack)
        {
            int applied = 0;
            try
            {
                var data = item.m_itemData;
                if (data == null) return 0;

                if (level > 0)
                {
                    int maxQuality = 1;
                    if (data.m_shared != null) maxQuality = Mathf.Max(1, data.m_shared.m_maxQuality);

                    // Most things in the game have no upgrade track at all: a mead, a pile of
                    // wood, a trophy. Setting a quality on one is a write with nothing behind
                    // it, and saying "at quality 1" about it is chatter about a property the
                    // item does not have, so neither happens. Anything that does upgrade is
                    // clamped to its own ceiling rather than to a hardcoded vanilla 4, because
                    // modded items go past it.
                    if (maxQuality > 1)
                    {
                        applied = Mathf.Clamp(level, 1, maxQuality);
                        item.SetQuality(applied);
                    }
                }

                data.m_durability = data.GetMaxDurability();

                if (stack > 1)
                {
                    int maxStack = 1;
                    if (data.m_shared != null) maxStack = Mathf.Max(1, data.m_shared.m_maxStackSize);
                    data.m_stack = Mathf.Clamp(stack, 1, maxStack);
                }

                // An ItemDrop keeps its truth in its ZDO, and SaveToZDO is the public path the
                // game's own private Save() takes. Only the owner may write it, and the index
                // is passed as vanilla passes it (-1 = the drop's own slot, not an inventory
                // one), written out so a game update that inserts a parameter fails the plugin
                // verifier instead of silently landing the value in the wrong slot.
                var view = obj.GetComponent<ZNetView>();
                if (view != null && view.IsValid() && view.IsOwner())
                    ItemDrop.SaveToZDO(data, view.GetZDO(), -1);
            }
            catch (Exception ex)
            {
                // Never lose the spawn over the bookkeeping.
                Log.LogWarning("Could not finish the spawned item: " + ex.Message);
            }
            return applied;
        }

        /// <summary>
        /// Applies the per-object bookkeeping the game's own "spawn" command applies, minus the
        /// one part of it a host asked not to have.
        /// <para>
        /// ItemDrop.OnCreateNew runs on every spawn whatever the setting says, because it does
        /// two jobs: it records the world level the item was made at, and it writes the cheated
        /// flag. Skipping the call to avoid the flag would leave every conjured item carrying
        /// whatever world level happened to be on the prefab. So the call stays and the flag is
        /// handed to it, true or false, exactly as SpawnMark.ShouldMark decides.
        /// </para>
        /// <para>
        /// The ZDO key is written either way for the same reason: a creature's drops read
        /// ZDOVars.s_cheated off its record, and an explicit false is the only thing that says
        /// "not cheated" rather than "nobody looked". The console command itself stays
        /// uncheat-flagged; that is a separate thing, and this marks the objects, not the
        /// console.
        /// </para>
        /// </summary>
        private static void MarkAsSpawnedIn(GameObject obj)
        {
            var mark = ShouldMarkSpawn();

            // Three guards rather than one, because the two records are independent and the
            // second one is not optional. OnCreateNew is also what stamps the item's world
            // level, so a ZDO write that throws must not be allowed to carry that call down
            // with it and leave the item on whatever level sat on the prefab. Never lose the
            // spawn over either piece of bookkeeping.
            try
            {
                var view = obj.GetComponent<ZNetView>();
                if (view != null && view.IsValid())
                    view.GetZDO().Set(ZDOVars.s_cheated, mark);
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not record the cheated flag on the spawned object: " + ex.Message);
            }

            try
            {
                ItemDrop.OnCreateNew(obj, mark);
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not stamp the spawned item's world level: " + ex.Message);
            }
        }

        /// <summary>
        /// The host's answer, run through <see cref="SpawnMark.ShouldMark"/>. Read through the
        /// entry rather than a copied bool so the value lives in one place. A .cfg edited while
        /// the server runs is not picked up here; BepInEx reads that file only at startup, so
        /// such a change needs a restart. Null only while Awake has not run, which a queued
        /// spawn cannot outrun, and the default answers for it if it ever did. A throw lands on
        /// the default too: an unreadable setting must not decide to mark.
        /// </summary>
        private static bool ShouldMarkSpawn()
        {
            try
            {
                var wanted = CfgMarkSpawnedAsCheated != null
                    ? CfgMarkSpawnedAsCheated.Value
                    : SpawnMark.ConfigDefault;
                return SpawnMark.ShouldMark(wanted, CheatChecksBypassed());
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not read the spawn mark setting, using the default: " + ex.Message);
                return SpawnMark.ShouldMark(SpawnMark.ConfigDefault, false);
            }
        }

        // PlayerProfile.s_bypassCheatChecks was a plain static FIELD until Valheim 1.0.12
        // (build 25253791) turned it into a static PROPERTY. A compiled field read is an
        // ldsfld against a member that no longer exists, and Mono raises that
        // MissingFieldException when it JITs the method holding the read - and it raises it at
        // that method's CALL SITE, so a try/catch written inside the method holding the read
        // never runs at all. The whole spawn loop died after the first object with no level,
        // no quality and one lonely item on the ground. Asking the live assembly what the
        // member is today survives both shapes and any future third one.
        private static bool _bypassProbed;
        private static PropertyInfo _bypassProperty;
        private static FieldInfo _bypassField;

        /// <summary>
        /// Reads PlayerProfile.s_bypassCheatChecks without compiling a reference to it.
        /// Property first, then field, looked up once and cached. False when it is neither,
        /// which reads as "the bypass is off" and leaves the decision entirely with the host's
        /// setting, the same way vanilla treats a server running without the bypass.
        /// </summary>
        private static bool CheatChecksBypassed()
        {
            try
            {
                if (!_bypassProbed)
                {
                    _bypassProbed = true;
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    var property = typeof(PlayerProfile).GetProperty("s_bypassCheatChecks", flags);
                    if (property != null && property.CanRead && property.PropertyType == typeof(bool))
                        _bypassProperty = property;
                    else
                        _bypassField = typeof(PlayerProfile).GetField("s_bypassCheatChecks", flags);
                }

                if (_bypassProperty != null) return (bool)_bypassProperty.GetValue(null, null);
                if (_bypassField != null && _bypassField.FieldType == typeof(bool)) return (bool)_bypassField.GetValue(null);
            }
            catch (Exception ex)
            {
                try { Log.LogDebug("Could not read the cheat check bypass: " + ex.Message); } catch { }
            }
            return false;
        }
    }
}
