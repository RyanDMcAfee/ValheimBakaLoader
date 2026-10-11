#if VALHEIM_PLUGIN
// BakaLoader Commander: baka_cleardrops, the sweep that clears items lying on the ground.
//
// WHY THIS FILE IS FENCED OFF
// It names Valheim's own types, which only exist on a machine with the dedicated server
// installed, so only Resources\build-plugins.ps1 compiles it. That script passes
// /define:VALHEIM_PLUGIN; BakaLoader's own project has no game assemblies and globs this folder
// like any other, so without the fence the app would stop building the moment this file
// landed. Its pure companion BakaClearDropsPlan.cs carries no fence on purpose: that is the
// half the test suite reads.
//
// WHY IT WALKS THE WORLD'S RECORDS AND NOT THE LOADED OBJECTS
// For the reason BakaKillAllSweep.cs gives at length: on a dedicated server the objects around
// every player are instantiated in that player's process, not in the server's, so a list of
// what the server has loaded is very nearly empty while the ground around every base is
// littered. Every object in the world has a ZDO in ZDOMan whether anybody has it loaded or
// not, and the same walk CleanseSweep and KillAllSweep take over m_objectsByID reaches all of
// them.
//
// WHAT COUNTS AS A GROUND ITEM, worked out once per prefab
// The prefab carries ItemDrop, and none of the things that make an ItemDrop more than a thing
// lying about: a Fish swimming in the sea is an ItemDrop, an egg somebody left to hatch is one
// with EggGrow on it, and a modded piece or creature can carry one too. So a prefab that also
// holds Character, Fish, EggGrow, Piece, WearNTear, Container, ItemStand, ArmorStand, Ship or
// Vagon is never a candidate, and nor is anything without ItemDrop at all. What an item stand or
// an armour stand is HOLDING is not an object in the world, it is data on the stand's own
// record, so it is out of reach of this walk by construction. The same goes for the contents of
// every chest.
//
// WHY THE SNAPSHOT HOLDS IDS AND NOT RECORDS
// The walk spans frames, and ZDOMan hands a destroyed record back to its pool, where the very
// same object can come back out as a DIFFERENT record a frame later. A list of ZDO references
// held across frames could end up pointing at somebody's wall. So the snapshot holds each
// item's ZDOID and its prefab hash, every turn looks the record up again by id, and a record
// that has gone (picked up, despawned, cleared by somebody else) or that now carries another
// prefab is passed over without a count.
//
// HOW AN ITEM IS REMOVED
// The game only destroys a record its own process owns (ZDOMan.DestroyZDO and
// ZNetScene.Destroy both check IsOwner), so the server claims ownership first. Then an item the
// server has instantiated goes through ZNetScene.Destroy, which takes the instance down with
// the record, and one that is a record only goes through ZDOMan.DestroyZDO. Either way the
// destroy is broadcast to every client on the next ZDOMan update and the id goes on the server's
// dead list, so a client that still had it loaded cannot send it back.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BakaLoaderClearDrops;
using HarmonyLib;
using UnityEngine;

namespace BakaLoaderCommander
{
    /// <summary>
    /// baka_cleardrops, walked a slice at a time the way KillAllSweep walks. Commander calls
    /// <see cref="Start"/> for an RCON line and drives <see cref="Pump"/> from Update().
    /// </summary>
    internal static class ClearDropsSweep
    {
        /// <summary>
        /// How much of a frame one slice may spend. Read after each item rather than before, so
        /// a slice always makes at least one item's worth of progress and a sweep cannot stall.
        /// </summary>
        private const long SliceMilliseconds = 4;

        /// <summary>What a prefab is, worked out once and remembered for the session.</summary>
        private struct PrefabFacts
        {
            public bool IsGroundItem;
            public ClearDropsItemKind Kind;
        }

        private static readonly Dictionary<int, PrefabFacts> Facts = new Dictionary<int, PrefabFacts>();

        /// <summary>One ground item as the snapshot saw it.</summary>
        private struct Candidate
        {
            public ZDOID Id;
            public int Prefab;
        }

        /// <summary>
        /// The ground items of the sweep in flight, and nothing else. Non empty only while
        /// <see cref="_running"/> is set, and every path that ends a sweep clears it.
        /// </summary>
        private static readonly List<Candidate> Snapshot = new List<Candidate>();

        /// <summary>Where every connected player stood at the top of the current slice.</summary>
        private static readonly List<Vector3> Players = new List<Vector3>();

        private static Run _running;

        private static bool _indexProbed;
        private static FieldInfo _indexField;

        /// <summary>One sweep, from the moment its snapshot is taken to its result line.</summary>
        private sealed class Run
        {
            public ClearDropsRequest Request;
            public long Session;

            public int Index;
            public int Scanned;

            public int Removed;
            public int KeptGear;
            public int KeptNear;
            public int Unreadable;

            public Action<string> Warn;
            public Action<string> Report;

            public int Remaining
            {
                get { return Snapshot.Count - Index; }
            }
        }

        // ------------------------------------------------------------------
        //  Starting one
        // ------------------------------------------------------------------

        /// <summary>
        /// Parses the line, takes the snapshot and walks as much of it as one slice allows.
        /// Returns the whole result line when the sweep finished inside that slice, and the
        /// "started" line when it did not, in which case <paramref name="report"/> is handed the
        /// result later by <see cref="Pump"/>. Must be called on the Unity main thread; Commander
        /// drains its RCON queue in Update() for that reason. Never throws.
        /// </summary>
        internal static string Start(string[] tokens, Action<string> warn, Action<string> report)
        {
            try
            {
                if (ZNet.instance == null || ZNetScene.instance == null || ZDOMan.instance == null)
                    return ClearDropsPlan.NotReady;

                // A second sweep over the same ground would count every item twice.
                if (_running != null) return ClearDropsPlan.AlreadyRunning(_running.Remaining);

                var request = ClearDropsPlan.Parse(tokens);
                if (!request.IsValid) return request.Error;

                if (!_indexProbed)
                {
                    _indexProbed = true;
                    _indexField = AccessTools.DeclaredField(typeof(ZDOMan), "m_objectsByID");
                }

                if (_indexField == null) return ClearDropsPlan.NoIndex;

                // IDictionary, as the other two sweeps read it.
                var index = _indexField.GetValue(ZDOMan.instance) as IDictionary;
                if (index == null) return ClearDropsPlan.NoIndex;

                // Built BEFORE the snapshot, so nothing between a finished collect and _running
                // being set can throw and leave a snapshot behind with no sweep to free it.
                var run = new Run
                {
                    Request = request,
                    Session = ZDOMan.GetSessionID(),
                    Warn = warn,
                    Report = report,
                };

                var collected = false;
                try
                {
                    run.Scanned = Collect(index, warn);
                    collected = true;
                }
                catch (Exception ex)
                {
                    return "Error: the world's object index could not be read (" + ex.Message +
                           "), so nothing was touched.";
                }
                finally
                {
                    if (!collected) Snapshot.Clear();
                }

                _running = run;

                // The first slice runs here, so a world small enough to finish inside it answers
                // with its whole result line rather than a promise.
                var finished = Advance(run);
                return finished ?? ClearDropsPlan.Started(Snapshot.Count, run.Scanned);
            }
            catch (Exception ex)
            {
                return "Error: cleardrops could not start: " + ex.Message;
            }
        }

        /// <summary>
        /// Carries the sweep in flight forward by one slice and hands its report the result line
        /// when it lands. Called from Update() every frame; returns at once when nothing runs.
        /// </summary>
        internal static void Pump()
        {
            var run = _running;
            if (run == null) return;

            // Advance clears _running when the sweep ends, so the report is taken first.
            var report = run.Report;

            var reply = Advance(run);
            if (reply == null || report == null) return;

            try
            {
                report(reply);
            }
            catch
            {
                // The sweep already happened. Losing the line that says so must not also throw
                // out of Update(), where Unity would log it on every frame afterwards.
            }
        }

        /// <summary>
        /// One slice of the walk. Returns null while there is more to do, and the line to send
        /// when the sweep is over, however it ended.
        /// </summary>
        private static string Advance(Run run)
        {
            var clock = Stopwatch.StartNew();
            var over = false;

            try
            {
                // Players move between slices, and a host who is standing beside a stack must
                // find it still there however long the walk took to reach it.
                ReadPlayers();

                while (run.Index < Snapshot.Count)
                {
                    Step(run, Snapshot[run.Index]);
                    run.Index++;

                    if (clock.ElapsedMilliseconds >= SliceMilliseconds) break;
                }

                if (run.Index < Snapshot.Count) return null;

                over = true;
                return ClearDropsPlan.Reply(run.Removed, run.KeptGear, run.KeptNear, run.Scanned, run.Unreadable);
            }
            catch (Exception ex)
            {
                over = true;
                return ClearDropsPlan.StoppedEarly(run.Removed, run.KeptGear, run.KeptNear, run.Scanned,
                    run.Unreadable, ex.Message);
            }
            finally
            {
                if (over)
                {
                    Snapshot.Clear();
                    Players.Clear();
                    _running = null;
                }
            }
        }

        /// <summary>One item: look it up again, decide, and clear it or count why not.</summary>
        private static void Step(Run run, Candidate candidate)
        {
            // Looked up by id every time, never held: see the header for why a held record can
            // turn into somebody's wall between two frames.
            var zdo = ZDOMan.instance.GetZDO(candidate.Id);
            if (zdo == null || !zdo.IsValid()) return;
            if (zdo.GetPrefab() != candidate.Prefab) return;

            var facts = FactsFor(candidate.Prefab, run.Warn);
            if (!facts.IsGroundItem) return;

            var readable = ReadItem(zdo, out var quality, out var hasCrafter);
            var decision = ClearDropsPlan.Decide(run.Request, readable, facts.Kind, quality, hasCrafter,
                NearestPlayerSquared(zdo.GetPosition()));

            switch (decision)
            {
                case ClearDropsDecision.KeepUnreadable:
                    run.Unreadable++;
                    return;
                case ClearDropsDecision.KeepGear:
                    run.KeptGear++;
                    return;
                case ClearDropsDecision.KeepNearPlayer:
                    run.KeptNear++;
                    return;
            }

            if (Remove(zdo, run.Session, run.Warn)) run.Removed++;
        }

        // ------------------------------------------------------------------
        //  The world's own record
        // ------------------------------------------------------------------

        /// <summary>
        /// Every ground item in the world, by id and prefab, copied before a single one is
        /// touched. Returns how many valid records the walk passed over, which is the "scanned"
        /// count on the reply: the number that says the whole world was looked at, not only the
        /// part somebody had loaded.
        /// </summary>
        private static int Collect(IDictionary index, Action<string> warn)
        {
            Snapshot.Clear();
            var scanned = 0;

            foreach (var value in index.Values)
            {
                var zdo = value as ZDO;
                if (zdo == null || !zdo.IsValid()) continue;
                scanned++;

                var hash = zdo.GetPrefab();
                if (!FactsFor(hash, warn).IsGroundItem) continue;

                Snapshot.Add(new Candidate { Id = zdo.m_uid, Prefab = hash });
            }

            return scanned;
        }

        /// <summary>
        /// Quality and crafter off the item's own record, the way the game reads them back when it
        /// loads the item (ItemDrop.LoadFromZDO). An item with no data on its record has never
        /// been changed from its prefab, which is quality 1 with no crafter, and that is what the
        /// game itself would load. False only when the data is there and will not come apart.
        /// </summary>
        private static bool ReadItem(ZDO zdo, out int quality, out bool hasCrafter)
        {
            quality = 1;
            hasCrafter = false;

            try
            {
                var data = new ItemDrop.ItemData();
                ItemDrop.LoadFromZDO(data, zdo);
                quality = data.m_quality;
                hasCrafter = data.m_crafterID != 0L;
                return true;
            }
            catch
            {
                return false; // if in doubt, leave it lying there
            }
        }

        /// <summary>
        /// The squared distance from a point to the nearest connected player, or infinity when
        /// nobody is on. Ready peers only: until a peer's handshake is done its position is the
        /// world origin, and a radius around that is a radius around nobody.
        /// </summary>
        private static float NearestPlayerSquared(Vector3 at)
        {
            var nearest = float.PositiveInfinity;
            for (var i = 0; i < Players.Count; i++)
            {
                var d = (Players[i] - at).sqrMagnitude;
                if (d < nearest) nearest = d;
            }

            return nearest;
        }

        /// <summary>Where every ready peer stands right now.</summary>
        private static void ReadPlayers()
        {
            Players.Clear();
            if (ZNet.instance == null) return;

            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady()) continue;
                Players.Add(peer.m_refPos);
            }
        }

        /// <summary>
        /// Takes one item out of the world, and says whether it went. The server claims it first,
        /// because the game destroys only what the process asking owns.
        /// </summary>
        private static bool Remove(ZDO zdo, long session, Action<string> warn)
        {
            try
            {
                if (!zdo.IsOwner()) zdo.SetOwner(session);

                var view = ZNetScene.instance.FindInstance(zdo);
                if (view != null)
                {
                    ZNetScene.instance.Destroy(view.gameObject);
                    return true;
                }

                ZDOMan.instance.DestroyZDO(zdo);
                return true;
            }
            catch (Exception ex)
            {
                if (warn != null) warn("cleardrops could not remove " + zdo.m_uid + ": " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        //  What a prefab is
        // ------------------------------------------------------------------

        /// <summary>
        /// The components that make an ItemDrop prefab something other than a thing lying about.
        /// See the header for each of them.
        /// </summary>
        private static readonly Type[] NeverTouched =
        {
            typeof(Character), typeof(Fish), typeof(EggGrow), typeof(Piece), typeof(WearNTear),
            typeof(Container), typeof(ItemStand), typeof(ArmorStand), typeof(Ship), typeof(Vagon),
        };

        /// <summary>
        /// What a prefab is, asked of ZNetScene once per prefab and remembered. A prefab that will
        /// not come apart is not a ground item, so the sweep leaves it alone.
        /// </summary>
        private static PrefabFacts FactsFor(int hash, Action<string> warn)
        {
            PrefabFacts facts;
            if (Facts.TryGetValue(hash, out facts)) return facts;

            facts = new PrefabFacts();
            var name = "";

            try
            {
                var prefab = ZNetScene.instance.GetPrefab(hash);
                if (prefab != null)
                {
                    name = prefab.name;
                    var drop = prefab.GetComponent<ItemDrop>();
                    if (drop != null)
                    {
                        var protectedThing = false;
                        for (var i = 0; i < NeverTouched.Length; i++)
                        {
                            if (prefab.GetComponent(NeverTouched[i]) == null) continue;
                            protectedThing = true;
                            break;
                        }

                        if (!protectedThing)
                        {
                            facts.IsGroundItem = true;
                            var shared = drop.m_itemData == null ? null : drop.m_itemData.m_shared;
                            facts.Kind = shared == null ? ClearDropsItemKind.Unknown : Mirror(shared.m_itemType);
                        }
                    }
                }
            }
            catch
            {
                facts = new PrefabFacts(); // unreadable prefab: not a target
            }

            // Said once per prefab, because this is cached for the session. An item type this
            // plugin has not met is kept as gear, so the line is how the gap gets reported.
            if (facts.IsGroundItem && facts.Kind == ClearDropsItemKind.Unknown && warn != null)
                warn("cleardrops does not know the item type of " + (name.Length > 0 ? name : hash.ToString()) +
                     ", so it is kept as gear. Name it in BakaClearDropsSweep.Mirror to give it a rule.");

            Facts[hash] = facts;
            return facts;
        }

        /// <summary>
        /// The game's item type, named across to this plugin's own copy of the list. By name and
        /// never by number, for the reason KillAllSweep.Mirror gives.
        /// </summary>
        private static ClearDropsItemKind Mirror(ItemDrop.ItemData.ItemType type)
        {
            switch (type)
            {
                case ItemDrop.ItemData.ItemType.None: return ClearDropsItemKind.None;
                case ItemDrop.ItemData.ItemType.Material: return ClearDropsItemKind.Material;
                case ItemDrop.ItemData.ItemType.Consumable: return ClearDropsItemKind.Consumable;
                case ItemDrop.ItemData.ItemType.OneHandedWeapon: return ClearDropsItemKind.OneHandedWeapon;
                case ItemDrop.ItemData.ItemType.Bow: return ClearDropsItemKind.Bow;
                case ItemDrop.ItemData.ItemType.Shield: return ClearDropsItemKind.Shield;
                case ItemDrop.ItemData.ItemType.Helmet: return ClearDropsItemKind.Helmet;
                case ItemDrop.ItemData.ItemType.Chest: return ClearDropsItemKind.Chest;
                case ItemDrop.ItemData.ItemType.Ammo: return ClearDropsItemKind.Ammo;
                case ItemDrop.ItemData.ItemType.Customization: return ClearDropsItemKind.Customization;
                case ItemDrop.ItemData.ItemType.Legs: return ClearDropsItemKind.Legs;
                case ItemDrop.ItemData.ItemType.Hands: return ClearDropsItemKind.Hands;
                case ItemDrop.ItemData.ItemType.Trophy: return ClearDropsItemKind.Trophy;
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon: return ClearDropsItemKind.TwoHandedWeapon;
                case ItemDrop.ItemData.ItemType.Torch: return ClearDropsItemKind.Torch;
                case ItemDrop.ItemData.ItemType.Misc: return ClearDropsItemKind.Misc;
                case ItemDrop.ItemData.ItemType.Shoulder: return ClearDropsItemKind.Shoulder;
                case ItemDrop.ItemData.ItemType.Utility: return ClearDropsItemKind.Utility;
                case ItemDrop.ItemData.ItemType.Tool: return ClearDropsItemKind.Tool;
                case ItemDrop.ItemData.ItemType.Attach_Atgeir: return ClearDropsItemKind.AttachAtgeir;
                case ItemDrop.ItemData.ItemType.Fish: return ClearDropsItemKind.Fish;
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft: return ClearDropsItemKind.TwoHandedWeaponLeft;
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable: return ClearDropsItemKind.AmmoNonEquipable;
                case ItemDrop.ItemData.ItemType.Trinket: return ClearDropsItemKind.Trinket;
                default: return ClearDropsItemKind.Unknown;
            }
        }
    }
}
#endif
