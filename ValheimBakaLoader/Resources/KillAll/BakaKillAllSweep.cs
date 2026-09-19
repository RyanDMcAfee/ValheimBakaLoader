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
using System;
using System.Collections;
using System.Collections.Generic;
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
        /// <summary>What a prefab is, worked out once and remembered for the session.</summary>
        private struct PrefabFacts
        {
            public bool IsCharacter;
            public bool IsPlayer;
            public KillAllFaction Faction;
        }

        private static readonly Dictionary<int, PrefabFacts> Facts = new Dictionary<int, PrefabFacts>();

        /// <summary>
        /// Reused between sweeps so a world with hundreds of thousands of objects does not hand
        /// the garbage collector a fresh list every time somebody clears the map.
        /// </summary>
        private static readonly List<ZDO> Snapshot = new List<ZDO>();

        private static bool _indexProbed;
        private static FieldInfo _indexField;

        /// <summary>
        /// Runs one baka_killall and returns the whole reply. Must be called on the Unity main
        /// thread: both callers queue the line and drain the queue in Update() for that reason.
        /// <paramref name="warn"/> may be null; it is only ever handed a diagnostic.
        /// </summary>
        internal static string Run(string[] tokens, Action<string> warn)
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null ||
                ZNetScene.instance == null || ZDOMan.instance == null)
                return "Error: server not ready (world still loading)";

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

            string error;
            if (!TryCollect(Snapshot, out error)) return error;

            var session = ZDOMan.GetSessionID();
            var slain = 0;
            var unreachable = 0;
            var spared = 0;
            var matched = 0;
            var unknownFactions = 0;

            for (var i = 0; i < Snapshot.Count; i++)
            {
                var zdo = Snapshot[i];
                if (zdo == null || !zdo.IsValid()) continue;

                var hash = zdo.GetPrefab();
                if (request.Scope == KillAllScope.OnePrefab && hash != wantedHash) continue;

                var facts = FactsFor(hash, warn);
                if (!facts.IsCharacter) continue;

                if (request.Scope == KillAllScope.NearPlayer &&
                    (zdo.GetPosition() - centre).sqrMagnitude > radiusSquared) continue;

                matched++;

                // A player ZDO is never asked anything else and never touched. Everything
                // else is asked whether it was tamed, which is a fact of the creature rather
                // than of its prefab: a tamed wolf is still a ForestMonsters wolf.
                var tamed = !facts.IsPlayer && ReadTamed(zdo);

                if (KillAllPlan.ShouldSpare(facts.IsPlayer, tamed, facts.Faction))
                {
                    spared++;
                    if (facts.Faction == KillAllFaction.Unknown) unknownFactions++;
                    continue;
                }

                if (Strike(zdo, session, warn)) slain++;
                else unreachable++;
            }

            Snapshot.Clear();

            var note = KillAllNote.None;
            if (request.Scope == KillAllScope.OnePrefab && matched == 0) note = KillAllNote.NoneInWorld;
            else if (request.Scope == KillAllScope.NearPlayer && slain + unreachable == 0) note = KillAllNote.NoneInRange;

            return KillAllPlan.Reply(slain, unreachable, spared, note, subject, unknownFactions);
        }

        // ------------------------------------------------------------------
        //  The world's own record
        // ------------------------------------------------------------------

        /// <summary>
        /// Every ZDO the server holds, copied into a list of our own before a single one is
        /// touched. The copy is the point: damaging a creature the server itself owns runs
        /// the kill inside this same call, and a death can take its object out of the world
        /// while the walk is still going. A snapshot cannot be invalidated underneath us.
        /// <para>
        /// ZDOMan keeps that record in a private field, so it is reached by name through
        /// Harmony's AccessTools rather than compiled in. That is deliberate: a name looked
        /// up this way is exactly what verify-plugins.ps1 resolves against the installed game
        /// before a release, so a rename in a game update fails the gate instead of turning
        /// into a sweep that silently finds nothing.
        /// </para>
        /// </summary>
        private static bool TryCollect(List<ZDO> into, out string error)
        {
            into.Clear();

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
                if (zdo != null) into.Add(zdo);
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

        /// <summary>A connected player by name, exact first and then ignoring case.</summary>
        private static ZNetPeer FindPeer(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var peer = ZNet.instance.GetPeerByPlayerName(name);
            if (peer != null) return peer;

            foreach (var p in ZNet.instance.GetPeers())
            {
                if (p != null && string.Equals(p.m_playerName, name, StringComparison.OrdinalIgnoreCase))
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
