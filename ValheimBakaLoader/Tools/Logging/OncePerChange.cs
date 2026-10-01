using System;
using System.Collections.Concurrent;

namespace ValheimBakaLoader.Tools.Logging
{
    /// <summary>
    /// "Is this worth saying out loud?" reduced to one word, for a line that is written every time
    /// something RUNS rather than every time something CHANGES.
    /// <para>
    /// WHY THIS EXISTS. BakaLoader does a lot of work on a timer. A scheduled restart window asks
    /// BepInEx whether there is anything to do every few hours and gets the same answer every
    /// time; every start path builds its server options through one method and asks the same world
    /// for its header several times inside one start; the roster poller reconnects to RCON every
    /// five seconds for as long as a world is up. A line written at each of those is a line a host
    /// reads hundreds of times, and the owner's application log has twice been almost entirely one
    /// of them. The answer is always the same shape: a change of state is news and is written at
    /// Information or Debug, and a repeat of the state already written is dropped a level.
    /// </para>
    /// <para>
    /// The shape was written out by hand in the bridge twice, which is twice it could be written
    /// slightly differently and once it could be left off a sibling branch: 1.2.6 gated the three
    /// endings where a restart window looked at the install and left it alone, and not the three
    /// where the window never got that far, so two windows a minute apart both wrote the deferral
    /// at Information. One class, driven by its own tests, so the next caller gets the version
    /// that has been proven rather than the version that was remembered.
    /// </para>
    /// <para>
    /// The SUBJECT is compared without case, because it is a profile name or a world name and
    /// Windows does not distinguish those by case. The STATE is compared exactly, because it is
    /// written by the caller out of a reason code and a version and a case change in one of those
    /// is a different fact.
    /// </para>
    /// </summary>
    public sealed class OncePerChange
    {
        private readonly ConcurrentDictionary<string, string> Held = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Records what this subject is saying now and answers whether it is DIFFERENT from what it
        /// said last time. The first answer about any subject is always true: nothing has been said
        /// about it yet, so the first time is news.
        /// </summary>
        /// <param name="subject">whose state this is: a profile name, a world, an address.</param>
        /// <param name="state">the state itself, as the caller words it.</param>
        /// <returns>true when this is news and belongs at the louder level.</returns>
        public bool Changed(string subject, string state)
        {
            var key = subject ?? "";
            var now = state ?? "";
            var was = Held.TryGetValue(key, out var held) ? held : null;
            Held[key] = now;
            return !string.Equals(was, now, StringComparison.Ordinal);
        }

        /// <summary>What was last recorded about a subject, or null when nothing has been.</summary>
        public string Last(string subject) =>
            Held.TryGetValue(subject ?? "", out var held) ? held : null;

        /// <summary>
        /// Drops what was recorded about a subject, so the next thing it says is news again. For a
        /// subject that has gone away rather than changed: a realm that was deleted, a world whose
        /// folder is no longer there.
        /// </summary>
        public bool Forget(string subject) => Held.TryRemove(subject ?? "", out _);
    }
}
