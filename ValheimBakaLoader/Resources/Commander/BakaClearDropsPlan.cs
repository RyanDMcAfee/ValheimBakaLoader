// BakaLoader Commander: the half of baka_cleardrops that needs no game at all.
//
// This file is deliberately plain C#, for the same reason BakaKillAllPlan.cs is. It names no
// Unity type, no BepInEx type and no Valheim type, so it compiles two ways without a single
// conditional: into BakaLoaderCommander.dll through Resources\build-plugins.ps1, and into
// BakaLoader itself through the app project's ordinary source glob. The second way is the
// point: the test suite reaches these types through the assembly's InternalsVisibleTo and holds
// the argument parsing, the keep rule and the reply text to account on every build, on a
// machine with no dedicated server installed.
//
// Its companion BakaClearDropsSweep.cs is the half that touches the game, and that one is
// fenced off behind the VALHEIM_PLUGIN symbol because the app has no game assemblies to compile
// it against. KEEP THIS FILE FREE OF GAME TYPES: a using of UnityEngine here stops BakaLoader
// itself from building, and ClearDropsPlanTests fails the moment one appears.
//
// WHAT THE COMMAND IS FOR
// Every stack a player drops, every bone pile a troll leaves behind and every resin a greydwarf
// coughs up is an object in the world, and it stays one until somebody picks it up. On a
// long-lived server that is thousands of records nobody wants, all of them saved and all of
// them sent to whoever walks past. This clears them, and only them, with two things kept by
// default because losing either is a loss a host would notice: anything that looks like
// somebody's gear, and anything lying close to a player who is online right now.
using System;
using System.Globalization;
using System.Text;

namespace BakaLoaderClearDrops
{
    /// <summary>
    /// A mirror of the game's ItemDrop.ItemData.ItemType, held here so the keep rule can be
    /// tested on a machine that has no game assemblies to load.
    /// <para>
    /// The mapping is done BY NAME in the sweep, never by number, exactly as KillAll mirrors the
    /// factions: a game update that reorders the real enum cannot quietly move a type from one
    /// side of the rule to the other. A type the game has and this list has not becomes
    /// <see cref="Unknown"/>, and the rule counts that as gear, so a new kind of weapon is kept
    /// rather than deleted. A type this list has and the game has not is a compile error in the
    /// sweep, which is the loud half of the deal.
    /// </para>
    /// </summary>
    internal enum ClearDropsItemKind
    {
        Unknown = 0,
        None,
        Material,
        Consumable,
        OneHandedWeapon,
        Bow,
        Shield,
        Helmet,
        Chest,
        Ammo,
        Customization,
        Legs,
        Hands,
        Trophy,
        TwoHandedWeapon,
        Torch,
        Misc,
        Shoulder,
        Utility,
        Tool,
        AttachAtgeir,
        Fish,
        TwoHandedWeaponLeft,
        AmmoNonEquipable,
        Trinket
    }

    /// <summary>What happens to one item on the ground.</summary>
    internal enum ClearDropsDecision
    {
        Remove,

        /// <summary>Weapons, armour, tools, shields and the rest, or anything upgraded or crafted.</summary>
        KeepGear,

        /// <summary>Lying within the radius of somebody who is online.</summary>
        KeepNearPlayer,

        /// <summary>Its record could not be read, so nobody knows what it is.</summary>
        KeepUnreadable
    }

    /// <summary>One parsed baka_cleardrops, or the sentence to send back instead of running it.</summary>
    internal sealed class ClearDropsRequest
    {
        /// <summary>Keep gear. On unless the host said keepgear=0.</summary>
        public bool KeepGear = true;

        /// <summary>
        /// Metres around every connected player inside which nothing is cleared. Zero turns the
        /// rule off, which is what the dialog sends when its switch is down.
        /// </summary>
        public float Radius = ClearDropsPlan.DefaultRadius;

        /// <summary>Null when the request is runnable, otherwise the whole reply to send.</summary>
        public string Error;

        public bool IsValid
        {
            get { return Error == null; }
        }

        public bool KeepNearPlayers
        {
            get { return Radius > 0f; }
        }
    }

    /// <summary>
    /// Argument parsing, the keep rule and the reply text. Everything here is a pure function of
    /// its arguments, which is what makes the rules testable at all: the sweep itself can only be
    /// exercised against a running dedicated server.
    /// </summary>
    internal static class ClearDropsPlan
    {
        /// <summary>The verb, as the window sends it and as the reply opens.</summary>
        internal const string Verb = "baka_cleardrops";

        /// <summary>
        /// What every result line opens with. The window reads replies by their first words, so
        /// this is the one spelling of it and every line below starts from it.
        /// </summary>
        internal const string ReplyPrefix = "cleardrops:";

        /// <summary>
        /// The verb that asks how far a sweep has got. A world big enough to outlast the RCON
        /// answer used to put its result in the BepInEx log and nowhere else, and on a server
        /// with BepInEx's console logging off (the default BakaLoader ships) nobody ever read it:
        /// a host cleared five thousand items and was told nothing at all. The window now asks
        /// this every two seconds after a "started" line until it answers with the result.
        /// </summary>
        internal const string StatusVerb = "baka_cleardrops_status";

        /// <summary>
        /// The opening of the status line while a sweep is still walking. The three sliced
        /// sweeps all answer their status verb in the one shape, "&lt;verb&gt;: running: scanned
        /// X of Z, removed N so far", so the window reads all three with one expression.
        /// </summary>
        internal const string RunningPrefix = ReplyPrefix + " running:";

        /// <summary>
        /// Thirty two metres is about the distance a player can see a dropped stack on the ground
        /// from, and comfortably wider than a workbench's reach. A stack somebody put down on
        /// purpose is almost always that close to them.
        /// </summary>
        internal const float DefaultRadius = 32f;

        /// <summary>
        /// The widest radius the command takes. Anything bigger is a whole region of the map, and
        /// a number like that is far more likely to be a typo than a wish.
        /// </summary>
        internal const float MaxRadius = 1000f;

        /// <summary>
        /// The separator between clauses on the reply line, a middle dot. Spelled as an escape so
        /// the file stays plain ASCII for every compiler that reads it.
        /// </summary>
        internal const string Dot = " \u00B7 ";

        internal const string Usage =
            "Usage: baka_cleardrops [keepgear=0|1] [radius=<metres>]. Both are optional: gear is kept " +
            "and nothing within 32 metres of a player is touched unless you say otherwise. radius=0 " +
            "clears right up to the players' feet.";

        internal const string BadRadius =
            "Error: the radius has to be a number of metres from 0 to 1000, like 32.";

        internal const string BadKeepGear =
            "Error: keepgear takes 1 to keep gear on the ground or 0 to clear it too.";

        internal const string NotReady = "Error: server not ready (world still loading)";

        internal const string NoIndex =
            "Error: this build of Valheim keeps its world objects somewhere cleardrops cannot read, " +
            "so nothing was touched. The plugin needs rebuilding against it.";

        /// <summary>
        /// Reads one console or RCON line, already split on spaces with the empty pieces dropped,
        /// and with the verb still sitting at index 0 the way Commander hands it over.
        /// <para>
        /// Every argument is key=value, in any order, each at most once. A word this does not
        /// know is a usage error rather than something to guess at: a command that deletes things
        /// must never run on a reading of the line the host did not mean.
        /// </para>
        /// </summary>
        internal static ClearDropsRequest Parse(string[] tokens)
        {
            var request = new ClearDropsRequest();
            if (tokens == null || tokens.Length <= 1) return request;

            var sawKeepGear = false;
            var sawRadius = false;

            for (var i = 1; i < tokens.Length; i++)
            {
                var token = tokens[i] ?? "";
                var equals = token.IndexOf('=');
                if (equals <= 0)
                {
                    request.Error = Usage;
                    return request;
                }

                var key = token.Substring(0, equals);
                var value = token.Substring(equals + 1);

                if (string.Equals(key, "keepgear", StringComparison.OrdinalIgnoreCase))
                {
                    if (sawKeepGear)
                    {
                        request.Error = Usage;
                        return request;
                    }

                    sawKeepGear = true;

                    bool keep;
                    if (!TryReadSwitch(value, out keep))
                    {
                        request.Error = BadKeepGear;
                        return request;
                    }

                    request.KeepGear = keep;
                    continue;
                }

                if (string.Equals(key, "radius", StringComparison.OrdinalIgnoreCase))
                {
                    if (sawRadius)
                    {
                        request.Error = Usage;
                        return request;
                    }

                    sawRadius = true;

                    float radius;
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out radius)
                        || float.IsNaN(radius) || float.IsInfinity(radius)
                        || radius < 0f || radius > MaxRadius)
                    {
                        request.Error = BadRadius;
                        return request;
                    }

                    request.Radius = radius;
                    continue;
                }

                request.Error = Usage;
                return request;
            }

            return request;
        }

        /// <summary>1, 0, true, false, on and off, ignoring case. Nothing else.</summary>
        private static bool TryReadSwitch(string value, out bool on)
        {
            on = false;
            if (string.IsNullOrEmpty(value)) return false;

            switch (value.ToLowerInvariant())
            {
                case "1":
                case "true":
                case "on":
                    on = true;
                    return true;
                case "0":
                case "false":
                case "off":
                    on = false;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether an item counts as somebody's gear: weapons, armour, tools, shields, utility
        /// items and trinkets by their type, and ANYTHING that was upgraded past its first level
        /// or carries a crafter's name, whatever its type. A torch is gear because it is carried
        /// and worn down like a tool. A type this plugin has not met is gear too: the two
        /// mistakes are not the same size, and an unfamiliar thing left lying on the ground is
        /// one a host can see and pick up, while a modded weapon that a sweep deleted is gone.
        /// </summary>
        internal static bool IsGear(ClearDropsItemKind kind, int quality, bool hasCrafter)
        {
            if (quality > 1) return true;
            if (hasCrafter) return true;

            switch (kind)
            {
                case ClearDropsItemKind.OneHandedWeapon:
                case ClearDropsItemKind.TwoHandedWeapon:
                case ClearDropsItemKind.TwoHandedWeaponLeft:
                case ClearDropsItemKind.Bow:
                case ClearDropsItemKind.AttachAtgeir:
                case ClearDropsItemKind.Shield:
                case ClearDropsItemKind.Helmet:
                case ClearDropsItemKind.Chest:
                case ClearDropsItemKind.Legs:
                case ClearDropsItemKind.Hands:
                case ClearDropsItemKind.Shoulder:
                case ClearDropsItemKind.Utility:
                case ClearDropsItemKind.Trinket:
                case ClearDropsItemKind.Tool:
                case ClearDropsItemKind.Torch:
                case ClearDropsItemKind.Unknown:
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a point lies inside the radius of a player, given the squared distance to the
        /// nearest one. Squared so the sweep never takes a square root per item, and
        /// <see cref="float.PositiveInfinity"/> when nobody is online.
        /// </summary>
        internal static bool WithinRadius(float nearestSquared, float radius)
        {
            if (radius <= 0f) return false;
            if (float.IsNaN(nearestSquared)) return false;
            return nearestSquared <= radius * radius;
        }

        /// <summary>
        /// The decision for one item on the ground, in the order the counts are kept: an item
        /// nobody could read is kept whatever the switches say, gear before nearness, and only
        /// then is it cleared.
        /// <para>
        /// Unreadable comes first and ignores both switches on purpose. A host who turned "keep
        /// gear" off asked for everything they can see to go, and an item whose record would not
        /// come apart is not one anybody can say they saw.
        /// </para>
        /// </summary>
        internal static ClearDropsDecision Decide(ClearDropsRequest request, bool readable,
            ClearDropsItemKind kind, int quality, bool hasCrafter, float nearestPlayerSquared)
        {
            if (!readable) return ClearDropsDecision.KeepUnreadable;

            var keepGear = request == null || request.KeepGear;
            var radius = request == null ? DefaultRadius : request.Radius;

            if (keepGear && IsGear(kind, quality, hasCrafter)) return ClearDropsDecision.KeepGear;
            if (WithinRadius(nearestPlayerSquared, radius)) return ClearDropsDecision.KeepNearPlayer;

            return ClearDropsDecision.Remove;
        }

        /// <summary>
        /// The counts, written once and used by the finished line and the one that fell over.
        /// <para>
        /// The order is the order the window reads them in and must not move: removed, then gear,
        /// then near players, then what was scanned. The unreadable clause is on the end and only
        /// there when something was unreadable, because on a healthy world it is always zero and
        /// a zero that is always there is a clause nobody reads.
        /// </para>
        /// </summary>
        internal static string Counts(int removed, int keptGear, int keptNear, int scanned, int unreadable)
        {
            var text = new StringBuilder();
            text.Append("removed ").Append(removed);
            text.Append(Dot).Append("kept ").Append(keptGear).Append(" gear");
            text.Append(Dot).Append("kept ").Append(keptNear).Append(" near players");
            text.Append(Dot).Append("scanned ").Append(scanned).Append(scanned == 1 ? " object" : " objects");
            if (unreadable > 0)
                text.Append(Dot).Append("kept ").Append(unreadable).Append(" unreadable");
            return text.ToString();
        }

        /// <summary>The whole reply of a sweep that finished.</summary>
        internal static string Reply(int removed, int keptGear, int keptNear, int scanned, int unreadable)
        {
            return ReplyPrefix + " " + Counts(removed, keptGear, keptNear, scanned, unreadable);
        }

        /// <summary>
        /// What a sweep says the moment it starts when it is too big to finish inside the answer,
        /// for the reason KillAllPlan.Started gives: the RCON client stops waiting long before a
        /// big world is walked, and a host must never be told a command failed while it goes on
        /// and does its work.
        /// </summary>
        internal static string Started(int items, int scanned)
        {
            return ReplyPrefix + " started on " + items + (items == 1 ? " ground item" : " ground items") +
                   " out of " + scanned + (scanned == 1 ? " object" : " objects") +
                   ". The result line follows in the server log when the sweep finishes, and " +
                   StatusVerb + " says how far it has got.";
        }

        /// <summary>
        /// What the status verb answers while a sweep is walking: how many of the ground items
        /// it has looked at, out of how many, and how many of those it has cleared.
        /// </summary>
        internal static string StatusRunning(int scanned, int total, int removed)
        {
            return RunningPrefix + " scanned " + scanned + " of " + total + ", removed " + removed + " so far";
        }

        /// <summary>
        /// What the status verb answers when nothing has run since the server came up. The
        /// window meets it when the server restarted under a sweep it was waiting on.
        /// </summary>
        internal static string StatusIdle()
        {
            return ReplyPrefix + " nothing is running. Run " + Verb +
                   " to clear the items lying on the ground.";
        }

        /// <summary>The answer to a second baka_cleardrops while the first is still walking.</summary>
        internal static string AlreadyRunning(int remaining)
        {
            return ReplyPrefix + " already running, " + remaining +
                   (remaining == 1 ? " ground item" : " ground items") +
                   " still to go. Wait for its result line before starting another.";
        }

        /// <summary>
        /// What a sweep says when something threw part way through. The counts it reached are real
        /// work that really happened, so they are reported rather than thrown away, and the line
        /// says it stopped rather than calling itself a result.
        /// </summary>
        internal static string StoppedEarly(int removed, int keptGear, int keptNear, int scanned,
            int unreadable, string fault)
        {
            var reason = string.IsNullOrEmpty(fault) ? "no reason given" : fault;
            return ReplyPrefix + " stopped early: " + reason + ". Up to that point: " +
                   Counts(removed, keptGear, keptNear, scanned, unreadable) + ".";
        }
    }
}
