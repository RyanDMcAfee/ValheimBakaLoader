using System;

namespace ValheimBakaLoader.Tools
{
    /// <summary>What a server that has sat empty for its wait should do next.</summary>
    public enum EmptyServerAction
    {
        /// <summary>Nothing: no switch asks for anything, or the server is not up and empty.</summary>
        None,

        /// <summary>Restart it, as "Restart when empty" has always done.</summary>
        Restart,

        /// <summary>Stop it, saving the world first, and leave it stopped.</summary>
        Stop,

        /// <summary>Stop it the same way, then ask Windows to shut down once it has stopped.</summary>
        StopThenWindows,
    }

    /// <summary>
    /// The three empty-server switches of one realm and the one wait they share, read together
    /// so a decision is never made from two different saves.
    /// </summary>
    public sealed record EmptyServerSwitches(bool Restart, bool Stop, bool StopShutsDownWindows, int DelayMinutes);

    /// <summary>One answer of <see cref="EmptyServerPlan.Decide(EmptyServerSwitches, bool, int)"/>.</summary>
    public readonly record struct EmptyServerDecision(EmptyServerAction Action, int DelayMinutes, bool RestartIgnored)
    {
        /// <summary>True for both kinds of stop.</summary>
        public bool Stops => Action is EmptyServerAction.Stop or EmptyServerAction.StopThenWindows;

        /// <summary>True only when the stop is to be followed by Windows going down.</summary>
        public bool ShutsDownWindows => Action == EmptyServerAction.StopThenWindows;
    }

    /// <summary>
    /// The whole of the empty-server decision, with nothing in it that needs a process, a clock
    /// or a disk, so the tests can ask it every question there is.
    /// <para>
    /// Stop wins over Restart. Both are switches a host can leave on at once (a profile written
    /// by hand, or one saved by a page that did not know to turn the other off), and a stop is
    /// the stronger ask: a host who wants the server down for the night does not want it brought
    /// back up a few minutes later. The answer says the restart was set aside, so the log can
    /// say so once.
    /// </para>
    /// <para>
    /// The Windows switch means nothing on its own. It is the second half of the stop and is
    /// read only when the stop switch is on, so a stored "on" left behind under a stop that was
    /// turned off can never shut a machine down.
    /// </para>
    /// </summary>
    public static class EmptyServerPlan
    {
        /// <summary>
        /// The longest wait the plan hands out, in minutes: 24 days, which is inside what a
        /// timer can be asked to wait. A number past it in the box is held to it rather than
        /// left to throw inside the timer, where nobody would see the timer never start.
        /// </summary>
        public const int MaxDelayMinutes = 24 * 24 * 60;

        public static EmptyServerDecision Decide(EmptyServerSwitches switches, bool running, int activePlayers)
        {
            if (switches == null) return new EmptyServerDecision(EmptyServerAction.None, 1, false);
            return Decide(switches.Restart, switches.Stop, switches.StopShutsDownWindows,
                switches.DelayMinutes, running, activePlayers);
        }

        public static EmptyServerDecision Decide(bool restart, bool stop, bool stopShutsDownWindows,
            int delayMinutes, bool running, int activePlayers)
        {
            var delay = Math.Clamp(delayMinutes, 1, MaxDelayMinutes);

            // Only a server that is up with nobody on it has anything to wait for.
            if (!running || activePlayers > 0) return new EmptyServerDecision(EmptyServerAction.None, delay, false);

            if (stop)
            {
                return new EmptyServerDecision(
                    stopShutsDownWindows ? EmptyServerAction.StopThenWindows : EmptyServerAction.Stop,
                    delay,
                    RestartIgnored: restart);
            }

            return restart
                ? new EmptyServerDecision(EmptyServerAction.Restart, delay, false)
                : new EmptyServerDecision(EmptyServerAction.None, delay, false);
        }
    }
}
