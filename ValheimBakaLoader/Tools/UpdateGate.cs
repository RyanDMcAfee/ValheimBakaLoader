using System.Collections.Generic;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The one rule that decides whether BakaLoader may replace itself right now.
    /// <para>
    /// Updating the app means closing the app, and the Valheim server is this process's own
    /// child, so anything that is up, on its way up, or on its way back up would go down with
    /// it. The rule is written here, away from any window, because the answer has to cover the
    /// whole application: a window that only looks at its own profiles gives the wrong answer
    /// the moment a second profile auto-starts into a second window.
    /// </para>
    /// </summary>
    public static class UpdateGate
    {
        /// <summary>
        /// Whether one server counts as busy. Stopped is not the same as finished: a crash with
        /// auto restart on lands the status on Stopped with the relaunch already scheduled, and
        /// a launch the guard is still thinking about has no status of its own at all. Both of
        /// those are a server that is about to be running again, so both count.
        /// </summary>
        public static bool IsBusy(ServerStatus status, bool relaunchPending)
            => status != ServerStatus.Stopped || relaunchPending;

        /// <summary>Whether this session's server counts as busy.</summary>
        public static bool IsBusy(ServerSession session)
        {
            var server = session?.Server;
            return server != null && IsBusy(server.Status, server.RelaunchPending);
        }

        /// <summary>
        /// Whether ANY of the given sessions counts as busy. Handed the whole application's
        /// sessions, this is the answer the self update reads. An empty list is not busy, which
        /// is what a test building a bridge with no windows sees.
        /// </summary>
        public static bool AnyServerBusy(IEnumerable<ServerSession> sessions)
        {
            if (sessions == null) return false;

            foreach (var session in sessions)
            {
                if (IsBusy(session)) return true;
            }

            return false;
        }
    }
}
