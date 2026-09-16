using System;
using System.Collections.Generic;
using System.Threading;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// A window that can hold live servers and knows how to put them down before it goes.
    /// <para>
    /// The self update needs every one of them closed, not just the one the host clicked in, so
    /// it needs something it can ask them all through. The window's own graceful path is what
    /// answers: it stops each server, waits for the world to save, and only then closes.
    /// </para>
    /// </summary>
    public interface ISelfUpdateClosable
    {
        /// <summary>
        /// Asks this window, on its own UI thread, to stop everything it is holding and close.
        /// Posted rather than run here and now: the caller is usually mid RPC or mid restart
        /// hook, and both of those have to finish before the app starts going down.
        /// </summary>
        void BeginShutDownAndClose();
    }

    /// <summary>
    /// Closing the WHOLE application once a newer BakaLoader has been staged.
    /// <para>
    /// The watchdog that swaps the files is launched the moment staging succeeds, and it waits
    /// two minutes for this process to exit before it starts writing. Closing one window is not
    /// the same as exiting: BakaLoader opens one window per auto-start profile, and the app
    /// stays up while any of them remain. The window the host clicked in closed, the process
    /// carried on running, and two minutes later the watchdog wrote the new release over a
    /// running install and then failed on the locked exe. Half old, half new, no relaunch.
    /// </para>
    /// <para>
    /// So the staged update asks EVERY window to close, and says out loud that the process is
    /// meant to end: the exit is not allowed to rest on one window happening to be the last one.
    /// </para>
    /// </summary>
    public sealed class AppShutdown
    {
        /// <summary>
        /// The one the app uses. The two callers are a WebView RPC and a server restart hook,
        /// neither of which holds anything of its own, and the answer belongs to the process
        /// rather than to either of them.
        /// </summary>
        public static AppShutdown Current { get; } = new();

        private int Asked;

        /// <summary>True once a staged self update has asked the application to close.</summary>
        public bool ExitRequested => Volatile.Read(ref Asked) != 0;

        /// <summary>
        /// Asks every open window to stop its servers and close, and makes sure the process
        /// really ends afterwards.
        /// <para>
        /// The pieces are handed in rather than reached for, so the rule can be proved without
        /// a message loop: <paramref name="windows"/> is every window that can hold a server,
        /// <paramref name="markExitRequested"/> tells the form that outlives them all to end the
        /// application when the last one goes, and <paramref name="exitNow"/> is the answer when
        /// there is no window to close at all.
        /// </para>
        /// </summary>
        /// <returns>
        /// True when this call is the one that started the shutdown. A second call is a no-op
        /// and answers false: the first one is already closing everything.
        /// </returns>
        public bool ShutDownAllWindowsForSelfUpdate(
            IEnumerable<ISelfUpdateClosable> windows,
            Action markExitRequested,
            Action exitNow,
            Action<Exception, string> onError = null)
        {
            if (Interlocked.Exchange(ref Asked, 1) != 0) return false;

            // Said before anything closes: a window can go down between the first ask and the
            // last, and the form counting them has to already know how this ends.
            Run(markExitRequested, onError, "Could not arm the application exit for the staged update");

            var asked = 0;
            foreach (var window in windows ?? Array.Empty<ISelfUpdateClosable>())
            {
                if (window == null) continue;
                if (Run(() => window.BeginShutDownAndClose(), onError,
                        "Could not ask a window to close for the staged update"))
                {
                    asked++;
                }
            }

            // Nothing to close, or nothing that could be asked. Either way there is no window
            // left to end the application on its way out, so it ends here instead.
            if (asked == 0) Run(exitNow, onError, "Could not close the application for the staged update");

            return true;
        }

        /// <summary>
        /// Runs one step of the shutdown, reporting rather than throwing. One window that will
        /// not take the message must not stop the others being asked, and must not leave the
        /// process running under a watchdog that is already counting down.
        /// </summary>
        private static bool Run(Action step, Action<Exception, string> onError, string what)
        {
            if (step == null) return false;

            try
            {
                step();
                return true;
            }
            catch (Exception ex)
            {
                try { onError?.Invoke(ex, what); } catch { }
                return false;
            }
        }
    }
}
