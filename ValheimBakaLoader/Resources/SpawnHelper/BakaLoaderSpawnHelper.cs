// BakaLoader Spawn Helper - headless-server-safe spawn via main-thread dispatch.
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
//   level   - 0-based star level for creatures (0=base, 1=1star, 2=2star; default 0)
//
// EXAMPLES:
//   baka_spawn Boar 123.4,567.8,90.1
//   baka_spawn Lox 200,100,50 3 2

using System;
using System.Collections.Concurrent;
using System.Globalization;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace BakaLoaderSpawnHelper
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SpawnHelperPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.baka.spawnhelper";
        private const string PluginName = "BakaLoader Spawn Helper";
        private const string PluginVersion = "1.1.0";

        private static ManualLogSource Log;

        private struct SpawnRequest
        {
            public string Prefab;
            public float X, Y, Z; // Unity coords: X=east/west, Y=up/down, Z=north/south
            public int Amount;
            public int Level; // 0-based: 0=base, 1=1star, 2=2star
        }

        private static readonly ConcurrentQueue<SpawnRequest> PendingSpawns = new ConcurrentQueue<SpawnRequest>();

        private void Awake()
        {
            Log = Logger;

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

                    args.Context.AddString($"Queued spawn: {amount}x {prefabName} (level {level}) at ({xVal:F1}, {zVal:F1}, {yVal:F1})");
                },
                isCheat: true,
                isNetwork: false,
                onlyServer: true
            );

            Log.LogInfo("BakaLoader Spawn Helper loaded - 'baka_spawn' command registered.");
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

            int spawned = 0;
            for (int i = 0; i < req.Amount; i++)
            {
                // Small random offset so multiple spawns don't stack exactly
                var offset = Vector3.zero;
                if (req.Amount > 1)
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

                spawned++;
            }

            Log.LogInfo($"Spawned {spawned}x {req.Prefab} (level {req.Level}) at ({req.X:F1}, {req.Z:F1}, {req.Y:F1})");
        }
    }
}
