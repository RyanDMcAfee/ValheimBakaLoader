using System;
using System.Globalization;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The name of one FACT about an unattended BepInEx write, so a notice the host has
    /// closed can be recognised when the same fact is recorded again.
    /// <para>
    /// WHY THIS EXISTS. A scheduled restart window runs every few hours, and every window
    /// asks BepInEx whether there is anything to do. On an install whose loader BakaLoader
    /// did not put down, every one of those windows declines for the same reason, records
    /// the same refusal, and the page stood the same bar up again: the owner's log carried
    /// eight identical "was left as it was at the restart window (foreign)" lines over two
    /// days, and he had to close the bar after every launch. The fact had not changed since
    /// the first time it was said.
    /// </para>
    /// <para>
    /// So a refusal is named by what it IS (the outcome, the reason, the pack on offer and
    /// the version on disk) and by nothing else. Two windows over an unchanged install name
    /// the same fact and the second one says nothing. A write or a heal is an EVENT rather
    /// than a standing fact, so the moment belongs in its name: each new one is worth its
    /// own notice, and closing yesterday's does not close today's.
    /// </para>
    /// <para>
    /// A FAILURE is named the same way a refusal is, and that is worth saying out loud
    /// because it was an event for one batch and the bug came straight back. A window that
    /// throws throws for a reason, and the reasons that reach here are standing ones: a
    /// loader file something else holds open, a note naming a pack the site has taken down.
    /// Every window fails on the same reason, against the same pack, on the same install,
    /// and a key with the clock in it would have made each of those a new fact and stood the
    /// bar up again after every restart. Nothing is lost by it: the moment the reason, the
    /// pack on offer or the version on disk changes, the key changes with it and the host
    /// reads the new one.
    /// </para>
    /// </summary>
    public static class BepInExNoticeKey
    {
        /// <summary>The outcomes whose key carries the moment, because each one is an event.</summary>
        private static bool IsEvent(string outcome)
            => string.Equals(outcome, "written", StringComparison.Ordinal)
            || string.Equals(outcome, "healed", StringComparison.Ordinal);

        /// <summary>
        /// The key for one recorded outcome, or null when there is no outcome to name.
        /// </summary>
        /// <param name="outcome">written | healed | refused | failed.</param>
        /// <param name="reason">The skip reason, or the id of the refusal that was thrown.</param>
        /// <param name="version">The pack that was on offer, when the reason is about a pack.</param>
        /// <param name="installedVersion">The loader version on disk here.</param>
        /// <param name="whenUtc">
        /// When it happened. Only part of the key for the two event outcomes; a refusal on a
        /// rule, and a failure on one, are standing facts and their keys must not move with
        /// the clock, or every window would name a new fact and the bar would come back
        /// exactly as it did before.
        /// </param>
        public static string Of(
            string outcome, string reason, string version, string installedVersion, DateTime whenUtc)
        {
            if (string.IsNullOrWhiteSpace(outcome)) return null;

            var moment = IsEvent(outcome)
                ? whenUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : "";

            // The separator is a character none of the four parts can hold: a reason is an id,
            // a version is a version, and a profile name never reaches this string at all. Two
            // different facts could otherwise share one key by splitting differently, which
            // would silence a notice the host has never seen.
            return string.Join("|",
                outcome ?? "",
                reason ?? "",
                version ?? "",
                installedVersion ?? "",
                moment);
        }

        /// <summary>
        /// Whether the host has already closed a notice for this exact fact.
        /// <para>
        /// Both sides null-safe on purpose: an install that has never closed one holds no key,
        /// and an outcome that names no fact is never "seen".
        /// </para>
        /// </summary>
        public static bool Seen(string key, string seenKey)
            => !string.IsNullOrEmpty(key)
            && !string.IsNullOrEmpty(seenKey)
            && string.Equals(key, seenKey, StringComparison.Ordinal);
    }
}
