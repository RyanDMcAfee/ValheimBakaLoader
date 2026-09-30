// What the client companion decides, with no game in the room.
//
// WHY THIS FILE IS SEPARATE FROM THE PLUGIN. Everything here is arithmetic and strings: does
// this character still need the sweep, what goes in the stamp afterwards, which items change,
// and what the one log line says. None of it names a Valheim type, so it is compiled into
// BakaLoader itself as well as into the plugin, and the suite drives the real decisions over a
// synthetic inventory on a machine with no game installed. The half that touches the game is
// in BakaLoaderUncheat.cs behind a VALHEIM_PLUGIN fence.
//
// This is the same split BakaCleansePlan.cs and BakaCleanseSweep.cs are written in, for the
// same reason.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace BakaLoaderUncheatPlan
{
    /// <summary>
    /// One item as the plan sees it: a name for the log, whether it carries the cheated mark,
    /// and whether it is being worn. The plugin hands it real <c>ItemDrop.ItemData</c>; the
    /// suite hands it these.
    /// </summary>
    internal sealed class MarkedItem
    {
        internal MarkedItem(string name, bool cheated, bool equipped = false)
        {
            Name = name;
            Cheated = cheated;
            Equipped = equipped;
        }

        internal string Name { get; }

        /// <summary>The mark itself. Cleared in place by <see cref="UncheatPlan.ClearAll"/>.</summary>
        internal bool Cheated { get; set; }

        /// <summary>Worn rather than carried. Both are in the inventory; this is for the line.</summary>
        internal bool Equipped { get; }
    }

    /// <summary>
    /// The rules the client companion runs by.
    /// <para>
    /// WHAT IT IS FOR. A player's own inventory is not in the world: it lives in the character
    /// file on their own machine, which is why the server-side cleanse says out loud that it
    /// cannot reach it. Anything BakaLoader 1.0.9 to 1.1.2 spawned into somebody's hands is
    /// still marked on their client, still pausing their achievements, and no amount of work on
    /// the server will change that. This is the other half: it runs on the client, once, and
    /// takes the marks off what that character is carrying.
    /// </para>
    /// <para>
    /// ONCE, AND ONLY ONCE. A sweep that ran on every spawn would be a plugin quietly rewriting
    /// a character file every time somebody loaded in, for ever, over items that were already
    /// clean. So the character carries a stamp saying the sweep has been here, and the stamp
    /// holds the GENERATION it ran at. Raising the generation in the config is how a host asks
    /// for it again, which is the only thing that would justify a second pass.
    /// </para>
    /// </summary>
    internal static class UncheatPlan
    {
        /// <summary>
        /// The key the stamp is written under, in the character's own customData.
        /// <para>
        /// Named for the mod and versioned, the way every mod that stamps a character names its
        /// own key: customData is one dictionary shared by everything on the client, so a key
        /// like "uncheat" or "done" is a collision waiting for the next mod that thinks of it.
        /// The v1 is the STAMP's shape rather than the plugin's version: a later plugin that
        /// wrote something else in here would write it under v2 and leave this alone, so a host
        /// who rolls back is not handed a stamp their build cannot read.
        /// </para>
        /// </summary>
        internal const string StampKey = "baka.uncheat.v1";

        /// <summary>Where the generation lives in the plugin's own .cfg.</summary>
        internal const string ConfigSection = "Uncheat";

        /// <summary>The key inside that section.</summary>
        internal const string ConfigKey = "Generation";

        /// <summary>The generation a fresh install runs at.</summary>
        internal const int ConfigDefault = 1;

        /// <summary>What the .cfg says about it, in one sentence a host can act on.</summary>
        internal const string ConfigDescription =
            "The sweep runs once per character and writes down the generation it ran at. "
            + "Raise this number to run it again on every character, which is only worth doing "
            + "if something has marked your items since.";

        /// <summary>
        /// Whether this character still needs the sweep.
        /// <para>
        /// It does when there is no stamp, when the stamp is not a number (a hand-edited file,
        /// or another mod's value under a key it should not have used), and when the stamp names
        /// a generation older than the one asked for. A stamp at or above the asked-for
        /// generation is the sweep having already been here.
        /// </para>
        /// </summary>
        internal static bool ShouldRun(string stamp, int generation)
        {
            if (string.IsNullOrWhiteSpace(stamp)) return true;

            int ran;
            if (!int.TryParse(stamp.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ran))
                return true;

            return ran < generation;
        }

        /// <summary>What goes in the stamp once the sweep has run at this generation.</summary>
        internal static string Stamp(int generation) =>
            generation.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Takes the mark off everything carrying one, and answers how many that was and how
        /// many were looked at. The list is walked once and changed in place, because the
        /// caller's list holds the game's own item objects and a copy would change nothing.
        /// <para>
        /// GENERIC, and that is the point of it. This rule was tested here over
        /// <see cref="MarkedItem"/> and the PLUGIN hand-rolled the same loop over the game's own
        /// <c>ItemDrop.ItemData</c>, counted its own looked, and shipped. Two loops, one of them
        /// tested: the tested code was not the shipped code, which is the only way a sweep can
        /// pass every test and clear nothing. The plugin hands in how to read and clear the mark
        /// on its own type and the loop itself is this one, for both of them.
        /// </para>
        /// <para>
        /// LOOKED counts everything that was not null, marked or not, because "looked at 40 and
        /// cleared none" and "found nothing to look at" are different facts and the one line the
        /// plugin writes has to tell them apart.
        /// </para>
        /// </summary>
        internal static int ClearAll<T>(
            IEnumerable<T> items, Func<T, bool> marked, Action<T> clear, out int looked)
        {
            looked = 0;
            if (items == null || marked == null || clear == null) return 0;

            var cleared = 0;
            foreach (var item in items)
            {
                if (item == null) continue;
                looked++;
                if (!marked(item)) continue;
                clear(item);
                cleared++;
            }

            return cleared;
        }

        /// <summary>The same sweep over the plan's own item, for a caller with nothing to count.</summary>
        internal static int ClearAll(IEnumerable<MarkedItem> items)
        {
            int looked;
            return ClearAll(items, out looked);
        }

        /// <summary>The same sweep over the plan's own item, counting what it looked at.</summary>
        internal static int ClearAll(IEnumerable<MarkedItem> items, out int looked)
            => ClearAll(items, item => item.Cheated, item => item.Cheated = false, out looked);

        /// <summary>
        /// The one line the plugin writes. It names what changed and what it looked at, because
        /// "cleared 0" and "did not run" are different facts and a host reading a client log
        /// wants to know which one they are looking at.
        /// </summary>
        internal static string Reply(int cleared, int looked, int generation)
        {
            if (looked <= 0)
                return "Nothing in this character's inventory to look at, so no cheat marks were cleared."
                    + " Stamped generation " + Stamp(generation) + ".";

            if (cleared == 0)
                return "Looked at " + looked + (looked == 1 ? " item" : " items")
                    + " and none of them carried a cheat mark. Stamped generation "
                    + Stamp(generation) + ", so this character is not checked again.";

            return "Cleared the cheat mark from " + cleared + (cleared == 1 ? " item" : " items")
                + " out of " + looked + ". Stamped generation " + Stamp(generation)
                + ", so this character is not checked again.";
        }

        /// <summary>What the plugin says when the stamp already covers the generation asked for.</summary>
        internal static string AlreadyDone(string stamp) =>
            "This character was already swept at generation " + (stamp ?? "?")
            + ", so nothing was touched. Raise Generation in the config to sweep again.";

        /// <summary>What it says on a dedicated server, which has no character to sweep.</summary>
        internal const string NothingToDoOnAServer =
            "This is a dedicated server, so there is no character inventory here and this plugin "
            + "does nothing. It belongs on the clients.";
    }
}
