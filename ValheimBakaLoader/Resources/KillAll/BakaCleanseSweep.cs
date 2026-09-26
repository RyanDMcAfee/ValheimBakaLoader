#if VALHEIM_PLUGIN
// BakaLoader: baka_cleanse, the sweep that takes the cheat marks back off a world.
//
// WHY THIS FILE IS FENCED OFF
// It names Valheim's own types, which only exist on a machine with the dedicated server
// installed, so only Resources\build-plugins.ps1 compiles it. That script passes
// /define:VALHEIM_PLUGIN; BakaLoader's own project has no game assemblies and globs this
// folder like any other, so without the fence the app would stop building the moment this
// file landed. Its pure companion BakaCleansePlan.cs carries no fence on purpose: that is
// the half the test suite reads, and it is also where the byte walking and the walk's own
// slicing live.
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
//
// WHY IT IS SLICED, from 1.2.4
// Until then the whole pass ran inside one call. On a long-lived world that pass outlasts
// the RCON client's patience, so the host was told the command had timed out while the
// cleanse carried on and did every bit of its work; and because it held the Unity main
// thread for the whole of it, the server did not tick while it ran. The walk now takes a
// few milliseconds a frame (CleanseWalk in BakaCleansePlan.cs), baka_cleanse answers the
// moment it knows what it is about to walk, and baka_cleanse_status says how far it has
// got. The empty-server check is still the start check, and it is asked again at the top of
// every slice: somebody who connects mid sweep ends it, and the counts so far are reported.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BakaLoaderKillAll
{
    /// <summary>
    /// The cheat-mark sweep, walked a slice at a time. Both plugins hold it:
    /// BakaLoaderCommander calls it for an RCON line, BakaKillAll for a line typed at the
    /// server console, and both drive <see cref="Pump"/> from Update().
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
        /// The records of the sweep in flight, and nothing else. Non empty only while
        /// <see cref="_running"/> is set, and every path that ends a sweep clears it in a
        /// finally: a list of live records that outlives its sweep keeps every one of them
        /// reachable for the life of the process.
        /// </summary>
        private static readonly List<ZDO> Snapshot = new List<ZDO>();

        /// <summary>The sweep in flight, or null when nothing is running.</summary>
        private static CleanseRun _running;

        /// <summary>
        /// The last result line, kept until the next start so baka_cleanse_status can answer
        /// with it. A host whose RCON client gave up waiting asks the status verb, and "it
        /// finished, here are the counts" has to still be there when they do.
        /// </summary>
        private static string _lastResult;

        private static Action<string> _log;
        private static Action<string> _report;

        /// <summary>
        /// The name of the process-wide mark that says a cleanse is walking, whichever plugin
        /// started it.
        /// <para>
        /// BakaLoaderCommander and BakaKillAll each COMPILE this file, so each holds its own
        /// copy of every static above: a baka_cleanse typed at the server console and one
        /// sent from the window over RCON are two sweeps over one world, and neither can see
        /// the other. Two sweeps over two snapshots of the same records count every cleared
        /// mark twice and rewrite half the containers under each other. The AppDomain both
        /// plugins are loaded into is the one thing they share, so the mark lives there.
        /// </para>
        /// <para>
        /// What is put in the slot is a Func&lt;int&gt; rather than a number, because a number
        /// would go stale the moment the walk moved: whichever plugin is holding it answers
        /// how much of ITS walk is left when it is asked. The delegate type comes out of the
        /// runtime rather than out of either plugin, which is what lets the other one read it.
        /// </para>
        /// </summary>
        internal const string RunningSlot = "baka.cleanse.running";

        /// <summary>True while THIS plugin is the one holding <see cref="RunningSlot"/>.</summary>
        private static bool _claimed;

        /// <summary>
        /// Runs everything a baka_cleanse can get wrong, takes the snapshot and walks as much
        /// of it as one slice allows. Returns the whole result line when the sweep finished
        /// inside that slice, the refusal when it never started, and the "Cleanse started"
        /// line when it is still going, in which case <paramref name="report"/> is handed the
        /// result line later by <see cref="Pump"/>. Must be called on the Unity main thread:
        /// both callers drain their queue in Update() for that reason.
        /// <para>
        /// Never throws: every caller is a command line, and a command that throws tells the
        /// host nothing.
        /// </para>
        /// </summary>
        internal static string Start(Action<string> log, Action<string> report)
        {
            try
            {
                if (ZNet.instance == null || ZDOMan.instance == null) return CleansePlan.NotReady;

                // A second sweep would walk a snapshot the first one is holding and count
                // every cleared mark twice.
                if (_running != null) return CleansePlan.AlreadyRunning(_running.Remaining);

                // And the same again for a sweep the OTHER plugin started. One world, one
                // sweep, whichever of the two verbs it came in through.
                var elsewhere = RemainingElsewhere();
                if (elsewhere >= 0) return CleansePlan.AlreadyRunning(elsewhere);

                // Nobody on the server, and the names of whoever is, because "wait until it
                // is empty" is not something a host can act on without them.
                int connected;
                var names = PeerNames(out connected);
                if (connected > 0) return CleansePlan.Connected(connected, names);

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

                // A copy of the world's records before a single one is touched. The walk now
                // spans frames, and ZDOMan's own dictionary is free to gain and lose entries
                // between two of them: enumerating it across a frame boundary would throw the
                // moment anything in the world changed.
                Snapshot.Clear();
                var collected = false;
                try
                {
                    foreach (var value in index.Values)
                    {
                        var zdo = value as ZDO;
                        if (zdo == null || !zdo.IsValid()) continue;
                        Snapshot.Add(zdo);
                    }

                    collected = true;
                }
                catch (Exception ex)
                {
                    return "Error: the world's object index could not be read (" + ex.Message +
                           "), so nothing was touched.";
                }
                finally
                {
                    // A collect that did not finish must not leave records behind it.
                    if (!collected) Snapshot.Clear();
                }

                var run = new CleanseRun();
                run.Total = Snapshot.Count;

                _running = run;
                _log = log;
                _report = report;
                _lastResult = null;

                // Claimed before the first slice, so a second verb arriving between this and
                // the end of that slice is refused rather than allowed to start beside it.
                Claim();

                // The first slice runs here rather than next frame, so a world small enough to
                // finish inside it answers with its whole result line exactly as 1.2.3 did. An
                // empty world lands here too and finishes at once.
                var finished = Advance(run);
                if (finished == null) return CleansePlan.Started(run.Total);

                _lastResult = finished;
                Tell(log, finished);
                return finished;
            }
            catch (Exception ex)
            {
                var said = "Error: the cleanse could not finish: " + ex.Message;
                Tell(log, said);
                return said;
            }
        }

        /// <summary>
        /// Carries the sweep in flight forward by one slice, and hands the log and the report
        /// the result line when it lands. Both plugins call this from Update() every frame; it
        /// returns immediately when nothing is running.
        /// </summary>
        internal static void Pump()
        {
            var run = _running;
            if (run == null) return;

            // Advance clears _running when the sweep ends, so both handlers are taken first.
            var log = _log;
            var report = _report;

            var reply = Advance(run);
            if (reply == null) return;

            _lastResult = reply;
            Tell(log, reply);
            Tell(report, reply);
        }

        /// <summary>
        /// What baka_cleanse_status answers: idle, how far a running sweep has got, or the
        /// result of the last one, which is kept until the next start.
        /// <para>
        /// It reads plain fields and touches nothing in the game, so it can be answered on the
        /// thread the question arrived on rather than queued for the main thread. That is the
        /// whole point of it: a host whose RCON client gave up waiting asks this and gets an
        /// answer now.
        /// </para>
        /// </summary>
        internal static string Status()
        {
            var run = _running;
            if (run != null) return CleansePlan.StatusRunning(run.Index, run.Total);

            var last = _lastResult;
            return string.IsNullOrEmpty(last) ? CleansePlan.StatusIdle() : last;
        }

        /// <summary>
        /// One slice of the walk. Returns null while there is more to do, and the line to
        /// send when the sweep is over, however it ended.
        /// </summary>
        private static string Advance(CleanseRun run)
        {
            var clock = Stopwatch.StartNew();
            var over = false;

            try
            {
                var finished = CleanseWalk.Advance(
                    run,
                    delegate(int at) { Step(run, Snapshot[at]); },
                    delegate { return clock.ElapsedMilliseconds >= CleanseWalk.SliceMilliseconds; },
                    delegate { return PeersArrived(run); });

                if (!finished) return null;

                over = true;

                if (run.Stopped)
                    return CleansePlan.StoppedForPeers(
                        run.PeerCount, run.PeerNames,
                        run.ZdosCleared, run.ContainersRewritten, run.ItemsCleared);

                return (run.ZdosCleared == 0 && run.ContainersRewritten == 0 && run.ItemsCleared == 0)
                    ? CleansePlan.NothingToDo()
                    : CleansePlan.Reply(run.ZdosCleared, run.ContainersRewritten, run.ItemsCleared);
            }
            catch (Exception ex)
            {
                // Whatever it managed before the throw is real work that really happened, so
                // the counts go with the reason rather than being swallowed.
                over = true;
                return "Error: the cleanse could not finish: " + ex.Message + ". Up to that point: "
                       + CleansePlan.Counts(run.ZdosCleared, run.ContainersRewritten, run.ItemsCleared) + ".";
            }
            finally
            {
                // EVERY path that ends a sweep frees the snapshot, the throwing one included.
                if (over)
                {
                    Snapshot.Clear();
                    _running = null;

                    // And the two handlers. Pump() takes them into locals before it calls
                    // this, so clearing them here cannot cost the result line its listeners.
                    // One of them is the console Terminal the command arrived on, and a
                    // static holding that after the sweep is done holds it for the life of
                    // the server.
                    _log = null;
                    _report = null;

                    Release();
                }
            }
        }

        /// <summary>
        /// Puts this plugin's name on the process-wide mark, so the other one refuses to start
        /// a second sweep over the same world. Never throws: a mark that will not be written
        /// is a worse arrangement than the one before it, not a reason to refuse a cleanse.
        /// </summary>
        private static void Claim()
        {
            try
            {
                AppDomain.CurrentDomain.SetData(RunningSlot, (Func<int>)RemainingHere);
                _claimed = true;
            }
            catch (Exception)
            {
                _claimed = false;
            }
        }

        /// <summary>Takes the mark back off, and only ever the one this plugin put there.</summary>
        private static void Release()
        {
            if (!_claimed) return;
            _claimed = false;
            try { AppDomain.CurrentDomain.SetData(RunningSlot, null); }
            catch (Exception) { }
        }

        /// <summary>How much of THIS plugin's walk is left, which is what the mark answers.</summary>
        private static int RemainingHere()
        {
            var run = _running;
            return run == null ? 0 : run.Remaining;
        }

        /// <summary>
        /// How much is left of a sweep the OTHER plugin is walking, or -1 when nothing else
        /// holds the mark. A mark held by something that cannot be asked answers zero, which
        /// still refuses the second sweep: that is the half that matters.
        /// </summary>
        private static int RemainingElsewhere()
        {
            if (_claimed) return -1;

            try
            {
                var held = AppDomain.CurrentDomain.GetData(RunningSlot);
                if (held == null) return -1;

                var ask = held as Func<int>;
                return ask == null ? 0 : ask();
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Whether somebody has connected since the sweep started. True ends it, and the names
        /// are written onto the run so the reply can say who: "run it again when the server is
        /// empty" is not an instruction a host can act on without them.
        /// </summary>
        private static bool PeersArrived(CleanseRun run)
        {
            int connected;
            var names = PeerNames(out connected);
            if (connected <= 0) return false;

            run.PeerCount = connected;
            run.PeerNames = names;
            return true;
        }

        /// <summary>Everybody on the server who is ready, by name, and how many of them.</summary>
        private static string PeerNames(out int connected)
        {
            connected = 0;
            var names = new List<string>();

            try
            {
                if (ZNet.instance == null) return "";

                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || !peer.IsReady()) continue;
                    names.Add(string.IsNullOrEmpty(peer.m_playerName) ? "(unnamed)" : peer.m_playerName);
                }
            }
            catch (Exception)
            {
                // A peer list that will not come apart is not a reason to say the server is
                // empty. Nothing is reported and the slice goes ahead, exactly as a sweep that
                // started on an empty server always did.
                return "";
            }

            connected = names.Count;
            return string.Join(", ", names.ToArray());
        }

        /// <summary>One record: every place a mark can be, cleared.</summary>
        private static void Step(CleanseRun run, ZDO zdo)
        {
            // A record can stop being valid between the snapshot and its turn, which is
            // ordinary on a world that is still saving.
            if (zdo == null || !zdo.IsValid()) return;

            var touched = false;

            // 1. The object's own mark.
            if (zdo.GetBool(ZDOVars.s_cheated, false))
            {
                zdo.RemoveBool(ZDOVars.s_cheated);
                run.ZdosCleared++;
                touched = true;
            }

            // 2. The station queues. Slot zero IS the bare key, so a smelter and a fermenter
            //    are covered by the same run. Only a key that is really there and really true
            //    is removed, which is what keeps a hash that happens to land here from losing
            //    an unrelated value.
            for (var slot = 0; slot < MaxQueuedSlots; slot++)
            {
                var key = ZDOVars.s_cheatedQueued + slot;
                if (!zdo.GetBool(key, false)) continue;
                zdo.RemoveBool(key);
                run.ZdosCleared++;
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
                    run.ContainersRewritten++;
                    run.ItemsCleared += cleared;
                    touched = true;
                }
            }

            // 4. One item on the ground, and each slot of a stand.
            var items = run.ItemsCleared;
            if (ClearItemBlob(zdo, ZDOVars.s_itemData, ref items)) touched = true;
            for (var slot = 0; slot < MaxStandSlots; slot++)
                if (ClearItemBlob(zdo, _standKeys[slot], ref items)) touched = true;
            run.ItemsCleared = items;

            // A removal does not move the data revision on its own, so the sector is marked
            // here: that is what puts the change in front of the next save and of anybody who
            // connects after it.
            if (touched) ZDOMan.instance.SetDirtySector(zdo);
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

        /// <summary>
        /// Hands a line to a listener that may not be there, and never lets a line about the
        /// sweep be what fails the sweep. Pump() runs inside Update(), where a throw would be
        /// logged by Unity on every frame afterwards.
        /// </summary>
        private static void Tell(Action<string> listener, string line)
        {
            if (listener == null || string.IsNullOrEmpty(line)) return;
            try { listener(line); }
            catch (Exception) { }
        }
    }
}
#endif
