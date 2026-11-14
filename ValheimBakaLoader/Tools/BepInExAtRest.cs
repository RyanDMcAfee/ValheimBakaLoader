using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ValheimBakaLoader.Tools
{
    /// <summary>How one moment with every server down came out.</summary>
    public enum BepInExAtRestEnding
    {
        /// <summary>The moment is not one a write may start at (a relaunch, or Windows about to power off).</summary>
        NotAllowed,

        /// <summary>A queued yes was there, so it was the whole step.</summary>
        Queue,

        /// <summary>No newer pack is known, so nothing was asked of anybody.</summary>
        NothingKnown,

        /// <summary>Another server on this BepInEx is still up, so nothing was asked either.</summary>
        OthersUp,

        /// <summary>The restart window's unattended step ran.</summary>
        Unattended,
    }

    /// <summary>What one moment with every server down needs from whoever runs it.</summary>
    public sealed class BepInExAtRestSteps
    {
        /// <summary>
        /// Whether a write may start at this moment at all. A stop asks
        /// <see cref="BepInExQueue.FiresAtStop"/>, the same rule the queue keeps; a start is
        /// always a moment.
        /// </summary>
        public bool Allowed { get; init; } = true;

        /// <summary>Whether a queued yes is on disk.</summary>
        public Func<bool> Queued { get; init; }

        /// <summary>Runs the queued yes. Null when the caller already ran it (a start runs it first).</summary>
        public Func<Task> RunQueue { get; init; }

        /// <summary>The newer pack already known here, or null. See <see cref="BepInExAtRest.KnownNewer"/>.</summary>
        public Func<string> NewerKnown { get; init; }

        /// <summary>Every OTHER server holding this BepInEx right now, profiles and processes alike.</summary>
        public Func<IReadOnlyList<string>> OthersUp { get; init; }

        /// <summary>The restart window's unattended step: consent, plan, refusals, write slot, records.</summary>
        public Func<Task> RunUnattended { get; init; }
    }

    /// <summary>
    /// The two moments besides a restart window when every server on one BepInEx is down: the
    /// stop of the last one, and a start with nothing else up (owner decision D2 in 1.3.3).
    /// <para>
    /// WHY THIS EXISTS (issue 31, the 2026-11-03 follow-up). An update the app already knew about
    /// went in only at a restart window, and a restart window only writes when every OTHER server
    /// on the install is down. Two servers that restart on schedules of their own are never both
    /// down in a window, so the pack waited for ever while the host stopped and started each of
    /// them by hand. These moments are the ones that host makes every week.
    /// </para>
    /// <para>
    /// Nothing here asks Thunderstore anything unless a newer pack is already known, so a stop and
    /// a start cost nothing on an install that is current. A queued yes is the host's own press
    /// and is the whole of the step when there is one, exactly as in a window.
    /// </para>
    /// </summary>
    public static class BepInExAtRest
    {
        public static async Task<BepInExAtRestEnding> RunAsync(BepInExAtRestSteps steps)
        {
            if (!steps.Allowed) return BepInExAtRestEnding.NotAllowed;

            if (steps.Queued?.Invoke() == true)
            {
                if (steps.RunQueue != null) await steps.RunQueue().ConfigureAwait(false);
                return BepInExAtRestEnding.Queue;
            }

            if (string.IsNullOrWhiteSpace(steps.NewerKnown?.Invoke())) return BepInExAtRestEnding.NothingKnown;

            // Asked here as well as inside the plan: the plan asks the site first and only then
            // finds the other server up, and a stop of the first of two servers would reach out
            // for a write that cannot happen.
            if ((steps.OthersUp?.Invoke() ?? Array.Empty<string>()).Count > 0) return BepInExAtRestEnding.OthersUp;

            await steps.RunUnattended().ConfigureAwait(false);
            return BepInExAtRestEnding.Unattended;
        }

        /// <summary>
        /// Waits, up to <paramref name="bound"/>, for the step a stop started. A start that
        /// arrives while that step is writing must not launch a server over a loader that is
        /// half replaced, so it waits for the write to end and then decides for itself.
        /// True when the step ended inside the bound.
        /// </summary>
        public static bool WaitFor(Task pending, TimeSpan bound)
        {
            if (pending == null || pending.IsCompleted) return true;
            try { return pending.Wait(bound); }
            catch (AggregateException) { return true; }
        }

        /// <summary>
        /// The newer pack this window already knows about, or null. Known means one of three
        /// things, none of which needed a request to learn: a window deferred it (or Check
        /// BepInEx now found it) and it is waiting; a window or a check left a pack alone because
        /// it was still inside its 72 hour wait, and that wait is now over; or a write of a known
        /// pack failed for a reason that passes (a held file, a site that did not answer).
        /// <para>
        /// A pack still inside its wait is NOT known for this purpose: the write would refuse it
        /// on the same rule, so a stop that asked the site about it would ask for nothing.
        /// Install now is the host's own press and takes it before the wait is over.
        /// </para>
        /// </summary>
        public static string KnownNewer(string waiting, string outcome, string reason, string version,
            DateTime? eligibleUtc, DateTime nowUtc)
        {
            if (!string.IsNullOrWhiteSpace(waiting)) return waiting.Trim();
            if (string.IsNullOrWhiteSpace(version)) return null;

            if (string.Equals(outcome, "refused", StringComparison.Ordinal)
                && string.Equals(reason, BepInExSkipReason.Soak, StringComparison.Ordinal))
                return eligibleUtc == null || eligibleUtc.Value <= nowUtc ? version.Trim() : null;

            if (string.Equals(outcome, "failed", StringComparison.Ordinal) && reason != null
                && ((IList<string>)BepInExQueue.KeepIds).Contains(reason))
                return version.Trim();

            return null;
        }
    }
}
