#if VALHEIM_PLUGIN
// BakaLoader KillAll: the sweep itself.
//
// WHY THIS FILE IS FENCED OFF
// It names Valheim's own types, which only exist on a machine with the dedicated server
// installed, so only Resources\build-plugins.ps1 compiles it. That script passes
// /define:VALHEIM_PLUGIN; BakaLoader's own project has no game assemblies and globs this
// folder like any other, so without the fence the app would stop building the moment this
// file landed. Its pure companion BakaKillAllPlan.cs carries no fence on purpose: that is
// the half the test suite reads.
//
// WHY THE SWEEP WALKS ZDOs AND NOT CHARACTERS
// The command used to call Character.GetAllCharacters() and kill what came back. On a
// LISTEN server that is every creature in the world, because the host's own process holds
// them all. On a DEDICATED server it is very nearly nothing: the server instantiates only
// what sits inside its own reference position, and every creature around a player is
// instantiated and OWNED by that player's client. On 2026-09-19 the command answered
// "0 hostiles slain, 162 spared" with four players online, hostiles standing next to them
// and a summoned Eikthyr alive in the world. Nothing was broken in the kill itself: the
// server was simply looking in a list that creatures around players are never in.
//
// So the sweep reads the world's own record instead. Every object in a Valheim world,
// loaded by somebody or loaded by nobody, has a ZDO in ZDOMan, and a ZDO carries its prefab
// and its owner. From there:
//
//   * the prefab hash is resolved once through ZNetScene and remembered, which answers
//     "is this a creature, is it a player, and what faction is it" without an instance;
//   * tamed is read off the ZDO (ZDOVars.s_tamed), which is where Character.Awake reads it
//     from in the first place, because there is no Character here to ask;
//   * the damage is sent to whoever OWNS the creature, exactly the way Commander already
//     damages a player: a routed RPC_Damage at the owner's peer id and the creature's ZDOID.
//     Character.RPC_Damage returns immediately unless the process running it is the owner,
//     so sending it anywhere else does nothing at all.
//
// A creature whose zone is loaded by nobody has no owner and cannot be damaged by anyone.
// It is counted as out of reach and left alone. It is NOT destroyed: destroying a boss ZDO
// would leave the world's active-boss counter stuck and drop nothing, and destroying
// anything else robs the players of the drop they were owed.
//
// WHY THE SWEEP ANSWERS BEFORE IT FINISHES
// The first version ran the whole walk inside the one call that produced the RCON reply.
// Commander stops waiting for a command at CommandTimeoutMs (4500, under BakaLoader's own
// five second client give-up) and answers "Error: command timed out (server main thread
// busy)", but the queued work carries straight on in Update() and does every bit of its
// job. So the one outcome that command could produce was a LIE: the host reads that it
// failed, and meanwhile every hostile on the server dies. On a world with several hundred
// thousand records the walk is not the only thing in that budget either; a save, a zone
// generation or a queue of earlier commands all spend the same 4500ms.
//
// There were two ways out and only one of them is honest.
//
// Slicing the walk across frames on its own makes it WORSE. It bounds how long the main
// thread is held at a time, which is the right thing for the server, but the reply is
// waiting on wall clock, and spreading the same work over a hundred frames multiplies the
// wall clock by however long a frame is. A walk that took 400ms in one go and beat the
// timeout comes back seconds later in slices and does not.
//
// So the sweep answers first. Start() resolves everything a typo could get wrong, takes the
// snapshot, and then runs ONE slice inline: a world small enough to finish inside a few
// milliseconds still answers with its whole "KillAll complete" line over RCON, which is
// every world a closed test will ever run on. Anything bigger answers "KillAll started: N
// candidates" straight away, which is true when it is said, and Pump() carries the walk on
// at a few milliseconds a frame until the real result line reaches the server log. Only
// once the reply stopped waiting on the walk was slicing free to do its own job, which is
// keeping the main thread responsive, and that is why both are here rather than one.
//
// The one piece that cannot be sliced is taking the snapshot, because a snapshot taken over
// several frames is not a snapshot: the index would be free to change under the walk, and
// enumerating a dictionary that is being written to throws. It is one pass over the world's
// records and it keeps only the ones inside the scope the host asked for, so what it leaves
// behind is a few hundred entries rather than a copy of the world.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BakaLoaderKillAll
{
    /// <summary>
    /// One implementation of baka_killall, compiled into both companion plugins so neither
    /// needs the other to be installed and neither can drift from the other. Commander calls
    /// it for an RCON line; BakaKillAll calls it for a line typed at the server console.
    /// </summary>
    internal static class KillAllSweep
    {
        /// <summary>
        /// How much of a frame one slice of a sweep may spend. The clock is read after each
        /// candidate rather than before, so a slice always makes at least one candidate's
        /// worth of progress and a sweep can never stall. It is a floor on progress and not a
        /// ceiling on time: one candidate whose death spawns a pile of loot takes what it
        /// takes, and the slice is over as soon as it returns.
        /// </summary>
        private const long SliceMilliseconds = 3;

        /// <summary>What a prefab is, worked out once and remembered for the session.</summary>
        private struct PrefabFacts
        {
            public bool IsCharacter;
            public bool IsPlayer;
            public KillAllFaction Faction;
        }

        private static readonly Dictionary<int, PrefabFacts> Facts = new Dictionary<int, PrefabFacts>();

        /// <summary>
        /// The candidates of the sweep in flight, and nothing else.
        /// <para>
        /// It holds only creature records inside the scope the host asked for, never a copy of
        /// the whole world: that is what makes "N candidates" a number a host can read, what
        /// keeps a list that outlives the frame small, and what leaves the sliced half with
        /// only the expensive work in it.
        /// </para>
        /// <para>
        /// It is non empty only while <see cref="_running"/> is set, and every path that ends
        /// a sweep clears it in a finally. A throw inside the walk used to leave it holding a
        /// reference to every matching record for the life of the process.
        /// </para>
        /// </summary>
        private static readonly List<ZDO> Snapshot = new List<ZDO>();

        /// <summary>The sweep in flight, or null when nothing is running.</summary>
        private static Run _running;

        private static bool _indexProbed;
        private static FieldInfo _indexField;

        /// <summary>One sweep, from the moment its snapshot is taken to its result line.</summary>
        private sealed class Run
        {
            public KillAllScope Scope;
            public string Subject = "";
            public long Session;

            /// <summary>How far along <see cref="Snapshot"/> the walk has got.</summary>
            public int Index;

            /// <summary>How many candidates the snapshot held when it was taken.</summary>
            public int Matched;

            public int Slain;
            public int Unreachable;
            public int Spared;
            public int UnknownFactions;

            /// <summary>Handed a diagnostic. May be null.</summary>
            public Action<string> Warn;

            /// <summary>Handed the result line when the sweep outlives its reply. May be null.</summary>
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
        /// Runs everything a baka_killall can get wrong, takes the snapshot and walks as much
        /// of it as one slice allows. Returns the whole "KillAll complete" line when the sweep
        /// finished inside that slice, and the "KillAll started" line when it did not, in
        /// which case <paramref name="report"/> is handed the result line later by
        /// <see cref="Pump"/>. Must be called on the Unity main thread: both callers queue the
        /// line and drain the queue in Update() for that reason.
        /// <para>
        /// <paramref name="warn"/> may be null; it is only ever handed a diagnostic.
        /// </para>
        /// </summary>
        internal static string Start(string[] tokens, Action<string> warn, Action<string> report)
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null ||
                ZNetScene.instance == null || ZDOMan.instance == null)
                return "Error: server not ready (world still loading)";

            // A second sweep would walk a snapshot the first one is holding, double every
            // count and strike half the world twice.
            if (_running != null) return KillAllPlan.AlreadyRunning(_running.Remaining);

            var request = KillAllPlan.Parse(tokens);
            if (!request.IsValid) return request.Error;

            // ---- the radius form: resolve the player first, so a typo never starts a sweep ----
            var centre = Vector3.zero;
            var radiusSquared = 0f;
            var subject = "";

            if (request.Scope == KillAllScope.NearPlayer)
            {
                var peer = FindPeer(request.PlayerName);
                if (peer == null) return "Error: player '" + request.PlayerName + "' not found";

                centre = peer.m_refPos;
                radiusSquared = request.Radius * request.Radius;
                subject = string.IsNullOrEmpty(peer.m_playerName) ? request.PlayerName : peer.m_playerName;
            }

            // ---- the named form: resolve the prefab first, for the same reason ----
            var wantedHash = 0;

            if (request.Scope == KillAllScope.OnePrefab)
            {
                string resolved;
                var prefab = FindPrefab(request.PrefabName, out resolved);
                if (prefab == null)
                    return KillAllPlan.Reply(0, 0, 0, KillAllNote.NoSuchPrefab, request.PrefabName, 0);

                subject = resolved;
                wantedHash = ZNetScene.instance.GetPrefabHash(prefab);

                var named = FactsFor(wantedHash, warn);
                if (!named.IsCharacter ||
                    KillAllPlan.ShouldSpare(named.IsPlayer, false, named.Faction))
                    return KillAllPlan.Reply(0, 0, 0, KillAllNote.PrefabIsNotHostile, subject, 0);
            }

            // Built BEFORE the snapshot is taken, on purpose. Everything between a successful
            // collect and _running being set has to be incapable of throwing: a throw in
            // there would leave a full snapshot behind with no sweep to own it or free it,
            // which is the very leak the finally below exists to prevent.
            var run = new Run
            {
                Scope = request.Scope,
                Subject = subject,
                Session = ZDOMan.GetSessionID(),
                Warn = warn,
                Report = report,
            };

            var collected = false;
            try
            {
                string error;
                if (!Collect(request.Scope, wantedHash, centre, radiusSquared, warn, out error))
                    return error;

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

            run.Matched = Snapshot.Count;
            _running = run;

            // The first slice runs here rather than next frame, so a world small enough to
            // finish inside it answers with its whole result line instead of a promise. An
            // empty snapshot lands here too and finishes at once, which is how "No Fenring is
            // in the world right now" stays a complete answer.
            var finished = Advance(run);
            return finished ?? KillAllPlan.Started(run.Matched);
        }

        /// <summary>
        /// Carries the sweep in flight forward by one slice, and hands its report the result
        /// line when it lands. Both plugins call this from Update() every frame; it returns
        /// immediately when nothing is running.
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
                // The sweep already happened. Losing the line that says so must not also
                // throw out of Update(), where Unity would log it on every frame afterwards.
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
                while (run.Index < Snapshot.Count)
                {
                    Step(run, Snapshot[run.Index]);
                    run.Index++;

                    if (clock.ElapsedMilliseconds >= SliceMilliseconds) break;
                }

                if (run.Index < Snapshot.Count) return null;

                over = true;
                return KillAllPlan.Reply(run.Slain, run.Unreachable, run.Spared,
                    NoteFor(run), run.Subject, run.UnknownFactions);
            }
            catch (Exception ex)
            {
                // Whatever it managed before the throw is real work that really happened, so
                // it is reported rather than swallowed.
                over = true;
                return KillAllPlan.StoppedEarly(run.Slain, run.Unreachable, run.Spared, ex.Message);
            }
            finally
            {
                // EVERY path that ends a sweep frees the snapshot, the throwing one included.
                // A list of live records that outlives the sweep keeps every one of them
                // reachable for the life of the process, and on a big world that is a lot of
                // memory held for no reason at all.
                if (over)
                {
                    Snapshot.Clear();
                    _running = null;
                }
            }
        }

        /// <summary>One candidate: spare it, or send it the hit and say whether it landed.</summary>
        private static void Step(Run run, ZDO zdo)
        {
            // A record can die between the snapshot and its turn, which is ordinary: the
            // sweep's own kills take records out of the world while the walk is still going.
            if (zdo == null || !zdo.IsValid()) return;

            var facts = FactsFor(zdo.GetPrefab(), run.Warn);

            // A player ZDO is never asked anything else and never touched. Everything else is
            // asked whether it was tamed, which is a fact of the creature rather than of its
            // prefab: a tamed wolf is still a ForestMonsters wolf.
            var tamed = !facts.IsPlayer && ReadTamed(zdo);

            if (KillAllPlan.ShouldSpare(facts.IsPlayer, tamed, facts.Faction))
            {
                run.Spared++;
                if (facts.Faction == KillAllFaction.Unknown) run.UnknownFactions++;
                return;
            }

            if (Strike(zdo, run.Session, run.Warn)) run.Slain++;
            else run.Unreachable++;
        }

        /// <summary>The one extra sentence the result line carries, when the counts would mislead.</summary>
        private static KillAllNote NoteFor(Run run)
        {
            if (run.Scope == KillAllScope.OnePrefab && run.Matched == 0) return KillAllNote.NoneInWorld;
            if (run.Scope == KillAllScope.NearPlayer && run.Slain + run.Unreachable == 0) return KillAllNote.NoneInRange;
            return KillAllNote.None;
        }

        // ------------------------------------------------------------------
        //  The world's own record
        // ------------------------------------------------------------------

        /// <summary>
        /// Every ZDO the server holds that the sweep is actually going to act on, copied into a
        /// list of our own before a single one is touched. The copy is the point: damaging a
        /// creature the server itself owns runs the kill inside this same call, and a death can
        /// take its object out of the world while the walk is still going. A snapshot cannot be
        /// invalidated underneath us.
        /// <para>
        /// The scope is applied HERE rather than in the walk, which is what lets a host be told
        /// how many candidates a sweep has in front of it before it starts, and what keeps the
        /// list that outlives the frame down to the creatures in scope rather than every record
        /// in the world.
        /// </para>
        /// <para>
        /// ZDOMan keeps that record in a private field, so it is reached by name through
        /// Harmony's AccessTools rather than compiled in. That is deliberate: a name looked
        /// up this way is exactly what verify-plugins.ps1 resolves against the installed game
        /// before a release, so a rename in a game update fails the gate instead of turning
        /// into a sweep that silently finds nothing.
        /// </para>
        /// </summary>
        private static bool Collect(KillAllScope scope, int wantedHash, Vector3 centre,
            float radiusSquared, Action<string> warn, out string error)
        {
            Snapshot.Clear();

            if (!_indexProbed)
            {
                _indexProbed = true;
                _indexField = AccessTools.DeclaredField(typeof(ZDOMan), "m_objectsByID");
            }

            if (_indexField == null)
            {
                error = "Error: this build of Valheim keeps its world objects somewhere KillAll " +
                        "cannot read, so nothing was touched. The plugin needs rebuilding against it.";
                return false;
            }

            var index = _indexField.GetValue(ZDOMan.instance) as IDictionary;
            if (index == null)
            {
                error = "Error: the world's object index could not be read, so nothing was touched.";
                return false;
            }

            foreach (var value in index.Values)
            {
                var zdo = value as ZDO;
                if (zdo == null || !zdo.IsValid()) continue;

                var hash = zdo.GetPrefab();
                if (scope == KillAllScope.OnePrefab && hash != wantedHash) continue;

                if (!FactsFor(hash, warn).IsCharacter) continue;

                if (scope == KillAllScope.NearPlayer &&
                    (zdo.GetPosition() - centre).sqrMagnitude > radiusSquared) continue;

                Snapshot.Add(zdo);
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Whether the creature behind this record was tamed. Character.Awake reads the very
        /// same value out of the very same place, which is why it can be trusted with no
        /// Character in hand.
        /// </summary>
        private static bool ReadTamed(ZDO zdo)
        {
            try
            {
                return zdo.GetBool(ZDOVars.s_tamed, false);
            }
            catch
            {
                return true; // if in doubt, do not kill
            }
        }

        /// <summary>
        /// What a prefab is, asked of ZNetScene once per prefab and remembered. A dedicated
        /// server holds every prefab the game and its mods registered, whether or not one of
        /// them is standing in the world, so this answers for a creature nobody has loaded.
        /// </summary>
        private static PrefabFacts FactsFor(int hash, Action<string> warn)
        {
            PrefabFacts facts;
            if (Facts.TryGetValue(hash, out facts)) return facts;

            var name = "";

            facts = new PrefabFacts();
            try
            {
                var prefab = ZNetScene.instance.GetPrefab(hash);
                if (prefab != null)
                {
                    name = prefab.name;
                    var character = prefab.GetComponent<Character>();
                    if (character != null)
                    {
                        facts.IsCharacter = true;
                        facts.IsPlayer = character.IsPlayer();
                        facts.Faction = Mirror(character.m_faction);
                    }
                }
            }
            catch
            {
                facts = new PrefabFacts(); // unreadable prefab: not a target
            }

            // Said once per prefab, because this is cached for the session. It is the only
            // warning the sweep raises on its own, and it is the one that matters: a faction
            // the game grew and this plugin has not met is spared, so a hostile can survive
            // every sweep with nothing but this line to say why.
            if (facts.IsCharacter && facts.Faction == KillAllFaction.Unknown && warn != null)
                warn("KillAll does not know the faction of " + (name.Length > 0 ? name : hash.ToString()) +
                     ", so it is left standing. Name it in BakaKillAllSweep.Mirror to make it a target.");

            Facts[hash] = facts;
            return facts;
        }

        /// <summary>
        /// The game's faction, named across to this plugin's own copy of the list.
        /// <para>
        /// By name and never by number: the game's enum is a plain ordered list, and a
        /// version that inserts a faction in the middle would renumber everything after it.
        /// A faction the game drops fails this switch at compile time, which is the loud
        /// outcome. A faction the game adds arrives here as Unknown, and KillAllPlan spares
        /// it and says so in the reply.
        /// </para>
        /// </summary>
        private static KillAllFaction Mirror(Character.Faction faction)
        {
            switch (faction)
            {
                case Character.Faction.Players: return KillAllFaction.Players;
                case Character.Faction.AnimalsVeg: return KillAllFaction.AnimalsVeg;
                case Character.Faction.ForestMonsters: return KillAllFaction.ForestMonsters;
                case Character.Faction.Undead: return KillAllFaction.Undead;
                case Character.Faction.Demon: return KillAllFaction.Demon;
                case Character.Faction.MountainMonsters: return KillAllFaction.MountainMonsters;
                case Character.Faction.SeaMonsters: return KillAllFaction.SeaMonsters;
                case Character.Faction.PlainsMonsters: return KillAllFaction.PlainsMonsters;
                case Character.Faction.Boss: return KillAllFaction.Boss;
                case Character.Faction.MistlandsMonsters: return KillAllFaction.MistlandsMonsters;
                case Character.Faction.Dverger: return KillAllFaction.Dverger;
                case Character.Faction.PlayerSpawned: return KillAllFaction.PlayerSpawned;
                case Character.Faction.TrainingDummy: return KillAllFaction.TrainingDummy;
                case Character.Faction.DeepNorth: return KillAllFaction.DeepNorth;
                default: return KillAllFaction.Unknown;
            }
        }

        // ------------------------------------------------------------------
        //  Landing the blow
        // ------------------------------------------------------------------

        /// <summary>
        /// Sends a lethal hit to whoever owns the creature, and says whether anybody was
        /// there to receive it.
        /// <para>
        /// Character.RPC_Damage drops the hit unless the process running it owns the object,
        /// so the peer id has to be the owner and nobody else. An object with no owner is in a
        /// zone nobody has loaded: there is no process anywhere running that creature, so
        /// there is nothing to damage and it is reported out of reach rather than counted as
        /// a kill. Same for an owner id that belongs to a peer who has since left.
        /// </para>
        /// </summary>
        private static bool Strike(ZDO zdo, long session, Action<string> warn)
        {
            try
            {
                if (!zdo.HasOwner()) return false;

                var owner = zdo.GetOwner();
                if (owner == session)
                {
                    // The server owns it, so the hit is delivered inside this process and it
                    // needs the instance to deliver it to.
                    if (ZNetScene.instance.FindInstance(zdo) == null) return false;
                }
                else
                {
                    var peer = ZNet.instance.GetPeer(owner);
                    if (peer == null || !peer.IsReady()) return false;
                }

                var hit = new HitData();
                hit.m_damage.m_damage = 1e10f;
                hit.m_point = zdo.GetPosition();
                hit.m_dodgeable = false;
                hit.m_blockable = false;

                // Character.RPC_Damage(HitData). HitData serializes natively over a routed
                // RPC, which is the same road Commander's dmg command takes for a player.
                ZRoutedRpc.instance.InvokeRoutedRPC(owner, zdo.m_uid, "RPC_Damage", hit);
                return true;
            }
            catch (Exception ex)
            {
                if (warn != null) warn("KillAll could not reach " + zdo.m_uid + ": " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        //  Looking things up by the name a host typed
        // ------------------------------------------------------------------

        /// <summary>
        /// A connected player by name, exact first and then ignoring case.
        /// <para>
        /// Both halves require the peer to be READY, which is what the game's own
        /// ZNet.GetPeerByPlayerName requires of every peer it walks past. The fallback did not,
        /// and a lookup that answers where the game's own refuses is a lookup that hands back a
        /// peer the game does not consider to be in the world yet. ZNetPeer.IsReady() is
        /// m_uid != 0, and m_uid, m_playerName and m_refPos are all written together at the end
        /// of the PeerInfo handshake: until then m_refPos is Vector3.zero, so a sweep centred on
        /// such a peer clears a radius around the WORLD ORIGIN rather than around the player the
        /// host named, and it does it without a word of complaint because a peer was found.
        /// </para>
        /// </summary>
        private static ZNetPeer FindPeer(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var peer = ZNet.instance.GetPeerByPlayerName(name);
            if (peer != null) return peer;

            foreach (var p in ZNet.instance.GetPeers())
            {
                if (p != null && p.IsReady() &&
                    string.Equals(p.m_playerName, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            }

            return null;
        }

        /// <summary>
        /// A prefab by the name a host typed. The game's own lookup is a hash of the exact
        /// spelling, so "eikthyr" finds nothing; the list is walked once, ignoring case, when
        /// that happens. The name that comes back out is the game's spelling, so the reply
        /// reads the way the wiki does rather than the way the host typed it.
        /// </summary>
        private static GameObject FindPrefab(string name, out string resolved)
        {
            resolved = name;
            if (string.IsNullOrEmpty(name)) return null;

            var scene = ZNetScene.instance;

            var exact = scene.GetPrefab(name);
            if (exact != null)
            {
                resolved = exact.name;
                return exact;
            }

            var prefabs = scene.m_prefabs;
            if (prefabs == null) return null;

            for (var i = 0; i < prefabs.Count; i++)
            {
                var candidate = prefabs[i];
                if (candidate == null) continue;
                if (!string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase)) continue;

                resolved = candidate.name;
                return candidate;
            }

            return null;
        }
    }
}
#endif
