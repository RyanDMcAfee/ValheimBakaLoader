using System;
using System.Threading;
using System.Threading.Tasks;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// A clock over one piece of launch work.
    /// <para>
    /// The splash screen runs its steps and opens the windows once every one of them has
    /// finished. A step with no clock on it is therefore a step that can hold the app shut
    /// for as long as it likes, and one of them reached out to GitHub: a site that answered
    /// its headers and then stopped sending left the host with no window, no server and
    /// nothing to do but end the app from Task Manager.
    /// </para>
    /// <para>
    /// The work is abandoned rather than cancelled, because the calls behind these steps
    /// carry their own deadlines and take no token from here. What this does is stop
    /// WAITING, which is the whole of what opening a window needs.
    /// </para>
    /// </summary>
    public static class LaunchBudget
    {
        /// <summary>
        /// The longest any one launch step may hold the splash. Generous enough that an
        /// ordinary slow minute on the internet still completes the step, short enough that
        /// a host never sits in front of a window that is not coming.
        /// </summary>
        public static readonly TimeSpan PerStep = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Runs <paramref name="work"/> and waits at most <paramref name="budget"/> for it.
        /// Answers true when the work finished inside the budget, false when the wait was
        /// given up on. A step that ran over gets one line through
        /// <paramref name="sayOverran"/> and the caller carries on without it.
        /// </summary>
        public static async Task<bool> WithinAsync(
            Func<Task> work, TimeSpan budget, Action<string> sayOverran = null, string what = null)
        {
            if (work == null) return true;

            // No budget means no clock, which is what a caller that has its own asks for.
            if (budget <= TimeSpan.Zero)
            {
                await work();
                return true;
            }

            var started = work();

            if (await Task.WhenAny(started, Task.Delay(budget)) == started)
            {
                // Awaited rather than returned, so a step that FAILED still fails here and
                // is reported by whatever handles a failed step. Only the wait is bounded.
                await started;
                return true;
            }

            // Nobody is waiting on it any more, so its exception is read and dropped rather
            // than left to come back as an unobserved one and take the process down.
            _ = started.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

            sayOverran?.Invoke(
                (string.IsNullOrWhiteSpace(what) ? "That startup step" : what)
                + " did not answer within " + Math.Max(1, (int)budget.TotalSeconds)
                + " seconds, so BakaLoader opened without waiting for it.");

            return false;
        }

        /// <summary>
        /// The same clock over work that WRITES INTO THE INSTALL, where giving up on the wait
        /// is not enough on its own.
        /// <para>
        /// <see cref="WithinAsync"/> abandons the work: it stops waiting and the task carries
        /// on. That is right for a read, and wrong for the restart hooks. Those walk the mod
        /// folders one at a time and per mod they back the folder up, CLEAR it and copy the new
        /// package in, and the caller's very next act is to start the server over those same
        /// folders. Windows locks a DLL the instant the server loads it, so a clear that is
        /// still running when the server comes up half succeeds and leaves the mod broken. A
        /// walk that is given up on has to stop replacing folders too.
        /// </para>
        /// <para>
        /// So the work is handed a token. When the clock runs out it is told to stop and then
        /// WAITED FOR, which costs the length of whatever one file copy was in flight rather
        /// than the length of the stall that ran the clock out. Answers true when the work
        /// finished inside its budget, false when it was told to stop.
        /// </para>
        /// </summary>
        public static async Task<bool> StopWithinAsync(
            Func<CancellationToken, Task> work, TimeSpan budget,
            Action<string> sayOverran = null, string what = null)
        {
            if (work == null) return true;

            using var stop = new CancellationTokenSource();
            var started = work(stop.Token);

            // No budget means no clock, which is what a caller that has its own asks for.
            if (budget <= TimeSpan.Zero)
            {
                await started;
                return true;
            }

            if (await Task.WhenAny(started, Task.Delay(budget)) == started)
            {
                // Awaited rather than returned, so work that FAILED still fails here and is
                // reported by whatever handles a failed step. Only the wait was bounded.
                await started;
                return true;
            }

            sayOverran?.Invoke(
                (string.IsNullOrWhiteSpace(what) ? "That restart step" : what)
                + " did not finish within " + Math.Max(1, (int)budget.TotalSeconds)
                + " seconds, so it was told to stop. The restart waits for it to let go of the"
                + " files it was writing.");

            stop.Cancel();

            try
            {
                await started;
            }
            catch (OperationCanceledException)
            {
                // The stop this method asked for. Nothing to report: the line above already
                // said what happened, and a caller's catch would word it as a failure.
            }

            return false;
        }
    }
}
