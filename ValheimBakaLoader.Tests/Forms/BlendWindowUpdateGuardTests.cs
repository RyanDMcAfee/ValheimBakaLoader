using System;
using System.IO;
using System.Runtime.CompilerServices;
using ValheimBakaLoader.Forms;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// What the bridge does around a running server update: it asks the service about a cancel
    /// rather than deciding for itself, and it holds the window X while files are being written.
    /// <para>
    /// Cancel used to cancel the bridge's own token, which the service linked into the steamcmd
    /// run. That stopped BakaLoader awaiting steamcmd without stopping steamcmd: the run carried
    /// on rewriting the install, the operation failed with a raw progress line as its reason, and
    /// the per install lock came off while files were still being written, so the next click
    /// could start a second one. The service no longer links that token in at all, and the two
    /// cancellation answers are now its own: the bridge asks and reports what it hears.
    /// </para>
    /// </summary>
    public class BlendWindowUpdateGuardTests
    {
        // ---------------------------------------------------------------- cancel

        /// <summary>
        /// Both cancellation answers belong to the update service: whether a cancel will be
        /// honoured is what <c>ServerUpdates.Cancel</c> returns, and whether an operation ended
        /// in one is <c>ServerUpdateResult.Cancelled</c>. The bridge used to work both out for
        /// itself from the last progress phase that reached this window, which is a copy of the
        /// truth: a phase that arrived late, or not at all, turned a cancel into a red failure
        /// row and told the host a cancel had taken when nothing had stopped.
        /// <para>
        /// A second copy of a rule is the thing this guards against, so the gate is on the
        /// source rather than on a value: the bridge must not compare a phase to Cancelled
        /// anywhere, and it must read the two answers straight off the service.
        /// </para>
        /// </summary>
        [Fact]
        public void The_bridge_never_works_cancellation_out_for_itself()
        {
            var source = File.ReadAllText(BridgeSourcePath());

            Assert.DoesNotContain("ServerUpdatePhase.Cancelled", source);
            Assert.DoesNotContain("CancelWouldBeHonoured", source);
            Assert.DoesNotContain("EndedInCancellation", source);

            // And it does ask the service both questions.
            Assert.Contains("ServerUpdates.Cancel(exe)", source);
            Assert.Contains("result.Cancelled", source);
        }

        /// <summary>
        /// The bridge source, found from this file's own compile-time path so the gate still
        /// reads it when the build output sits somewhere else entirely. Same shape as the Atlas
        /// group's source gate.
        /// </summary>
        private static string BridgeSourcePath([CallerFilePath] string thisFile = "")
        {
            // <repo>/ValheimBakaLoader.Tests/Forms/BlendWindowUpdateGuardTests.cs
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile), "..", ".."));
            return Path.Combine(repo, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
        }

        // ---------------------------------------------------------------- the close guard

        [Fact]
        public void With_nothing_being_rewritten_the_window_closes_as_it_always_did()
        {
            Assert.Equal(
                BlendWindow.UpdateCloseChoice.Close,
                BlendWindow.DecideUpdateClose(false, false, null, Now));
        }

        [Fact]
        public void The_first_close_click_during_an_update_waits_for_it()
        {
            Assert.Equal(
                BlendWindow.UpdateCloseChoice.Wait,
                BlendWindow.DecideUpdateClose(true, false, null, Now));
        }

        [Fact]
        public void A_second_click_within_ten_seconds_is_honoured()
        {
            var asked = Now.AddSeconds(-4);

            Assert.Equal(
                BlendWindow.UpdateCloseChoice.Force,
                BlendWindow.DecideUpdateClose(true, false, asked, Now));
        }

        [Fact]
        public void A_second_click_is_refused_while_steamcmd_is_mid_write()
        {
            // Closing takes the app's child processes with it, so this would kill steamcmd mid
            // file and leave an install that is neither the old build nor the new one.
            var asked = Now.AddSeconds(-4);

            Assert.Equal(
                BlendWindow.UpdateCloseChoice.Wait,
                BlendWindow.DecideUpdateClose(true, true, asked, Now));
        }

        [Fact]
        public void A_click_long_after_the_first_one_is_a_first_click_again()
        {
            var asked = Now.AddSeconds(-40);

            Assert.Equal(
                BlendWindow.UpdateCloseChoice.Wait,
                BlendWindow.DecideUpdateClose(true, false, asked, Now));
        }

        [Fact]
        public void The_close_notice_says_the_window_will_close_by_itself()
        {
            Assert.Equal(
                "An update is running. BakaLoader will close when it finishes.",
                BlendWindow.UpdateCloseMessage);
        }

        private static DateTime Now => new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

        // ---------------------------------------------------------------- the self-update close

        /// <summary>
        /// A staged self-update has to close the WHOLE application, and both of the two paths
        /// that stage one have to do it.
        /// <para>
        /// Staging arms a watchdog that waits two minutes for this process to exit and then
        /// writes the new release over the install whether it has exited or not. BakaLoader opens
        /// one window per auto-start profile and stays up while any of them remain, so a caller
        /// that closed its own window left the process running: the swap went over a live install
        /// and then failed on the locked exe, leaving it half old and half new, with no relaunch
        /// and nothing said. Both callers now go through the application-wide close.
        /// </para>
        /// </summary>
        [Fact]
        public void Both_self_update_callers_close_every_window_and_not_just_their_own()
        {
            var source = File.ReadAllText(BridgeSourcePath());

            var rpc = Slice(source, "RegisterRpc(\"app.selfUpdateNow\"", "return new { ok = true };");
            Assert.True(ClosesEveryWindow(rpc),
                "app.selfUpdateNow closes only its own window, so the app stays up under the watchdog");

            var hook = Slice(source, "session.Server.CheckForAppUpdateOnRestart = async () =>", "return true;");
            Assert.True(ClosesEveryWindow(hook),
                "the restart hook closes only its own window, so the app stays up under the watchdog");

            // And the application-wide close is the thing that reaches every window and then
            // makes sure the process really ends.
            var close = Slice(source, "private void RunSelfUpdateShutdown()", "}");
            Assert.Contains("ShutDownAllWindowsForSelfUpdate", close);
            Assert.Contains("OpenServerWindows()", close);
            Assert.Contains("RequestExitForSelfUpdate", close);
            Assert.Contains("Application.Exit", close);
        }

        /// <summary>
        /// The same check against the shape this replaced, so the guard is known to catch it. The
        /// text below is what both callers used to say, verbatim: one window, closed on its own,
        /// while the other windows kept the process alive right through the file swap.
        /// </summary>
        [Fact]
        public void The_guard_turns_down_the_single_window_close_it_replaced()
        {
            const string preFix = @"
                // An update is staged; close the app so the watchdog can take over.
                BeginInvoke(new Action(ShutDownAllServersThenClose));
                return new { ok = true };";

            Assert.False(ClosesEveryWindow(preFix));
        }

        /// <summary>
        /// Whether one self-update caller closes the whole application or only the window it was
        /// clicked in. Written once so the live source and the shape it replaced are held to the
        /// same rule.
        /// </summary>
        private static bool ClosesEveryWindow(string callerBody)
            => callerBody.Contains("CloseEveryWindowForSelfUpdate", StringComparison.Ordinal)
                && !callerBody.Contains("ShutDownAllServersThenClose", StringComparison.Ordinal);

        /// <summary>The source between one marker and the first end marker after it.</summary>
        private static string Slice(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, "BlendWindow.Bridge.cs no longer contains " + from);

            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            Assert.True(end > start, "BlendWindow.Bridge.cs no longer contains " + to + " after " + from);

            return source[start..(end + to.Length)];
        }
    }
}
