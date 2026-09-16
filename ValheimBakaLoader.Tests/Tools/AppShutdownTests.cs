using System;
using System.Collections.Generic;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Closing the application once a newer BakaLoader has been staged.
    /// <para>
    /// The gate that decides whether the update may go at all is right, and this is the half
    /// that happens afterwards. Staging arms a watchdog that waits two minutes for this process
    /// to exit and then writes the new release over the install whether it has exited or not.
    /// BakaLoader opens one window per auto-start profile and stays up while any of them remain,
    /// so closing the one window the host clicked in left the process running: the swap went
    /// over a live install and then failed on the locked exe, leaving it half old and half new
    /// with no relaunch and nothing said.
    /// </para>
    /// </summary>
    public class AppShutdownTests
    {
        [Fact]
        public void Every_window_is_asked_to_stop_its_servers_and_close()
        {
            var a = new FakeWindow();
            var b = new FakeWindow();
            var c = new FakeWindow();
            var exits = 0;
            var armed = 0;

            var started = new AppShutdown().ShutDownAllWindowsForSelfUpdate(
                new ISelfUpdateClosable[] { a, b, c }, () => armed++, () => exits++);

            Assert.True(started);
            Assert.True(a.Asked);
            Assert.True(b.Asked);
            Assert.True(c.Asked);

            // The windows close themselves, so nothing ends the application here: the form that
            // outlives them does it when the last one has gone, and it is told to expect that.
            Assert.Equal(1, armed);
            Assert.Equal(0, exits);
        }

        /// <summary>
        /// The window count is not the thing that guarantees the exit. With no window open there
        /// is nothing to close and nothing that will ever report a close, so the shutdown ends
        /// the application itself rather than leaving the watchdog to write over a live process.
        /// </summary>
        [Fact]
        public void With_no_window_open_the_application_is_ended_directly()
        {
            var exits = 0;
            var armed = 0;

            var started = new AppShutdown().ShutDownAllWindowsForSelfUpdate(
                Array.Empty<ISelfUpdateClosable>(), () => armed++, () => exits++);

            Assert.True(started);
            Assert.Equal(1, armed);
            Assert.Equal(1, exits);
        }

        [Fact]
        public void A_null_list_is_the_same_as_no_window_at_all()
        {
            var exits = 0;

            Assert.True(new AppShutdown().ShutDownAllWindowsForSelfUpdate(null, null, () => exits++));
            Assert.Equal(1, exits);
        }

        /// <summary>
        /// One window that will not take the message must not keep the others open, and must not
        /// leave the process up under a watchdog that is already counting down. A shutdown where
        /// nothing could be asked falls back to ending the application.
        /// </summary>
        [Fact]
        public void A_window_that_throws_does_not_stop_the_others_being_asked()
        {
            var bad = new FakeWindow { Throws = true };
            var good = new FakeWindow();
            var errors = new List<string>();
            var exits = 0;

            new AppShutdown().ShutDownAllWindowsForSelfUpdate(
                new ISelfUpdateClosable[] { bad, good, null }, null, () => exits++,
                (ex, what) => errors.Add(what));

            Assert.True(good.Asked);
            Assert.Single(errors);

            // One window did take it, so that window's close is what ends the application.
            Assert.Equal(0, exits);
        }

        [Fact]
        public void With_every_window_refusing_the_message_the_application_still_ends()
        {
            var exits = 0;

            new AppShutdown().ShutDownAllWindowsForSelfUpdate(
                new ISelfUpdateClosable[] { new FakeWindow { Throws = true } }, null, () => exits++);

            Assert.Equal(1, exits);
        }

        /// <summary>
        /// Two windows can reach this at once (the dialog in one, a restart hook in another), and
        /// a second pass over a list of windows that are already closing is a second Close on
        /// forms that are half gone. The first ask is the one that counts.
        /// </summary>
        [Fact]
        public void The_second_ask_is_a_no_op()
        {
            var window = new FakeWindow();
            var shutdown = new AppShutdown();

            Assert.False(shutdown.ExitRequested);
            Assert.True(shutdown.ShutDownAllWindowsForSelfUpdate(
                new ISelfUpdateClosable[] { window }, null, null));
            Assert.True(shutdown.ExitRequested);
            Assert.Equal(1, window.Asks);

            Assert.False(shutdown.ShutDownAllWindowsForSelfUpdate(
                new ISelfUpdateClosable[] { window }, null, null));
            Assert.Equal(1, window.Asks);
        }

        /// <summary>The app reaches this through one instance, so the two callers share the flag.</summary>
        [Fact]
        public void The_application_has_one_of_these()
        {
            Assert.NotNull(AppShutdown.Current);
            Assert.Same(AppShutdown.Current, AppShutdown.Current);
        }

        private sealed class FakeWindow : ISelfUpdateClosable
        {
            public int Asks { get; private set; }

            public bool Asked => Asks > 0;

            public bool Throws { get; init; }

            public void BeginShutDownAndClose()
            {
                if (Throws) throw new InvalidOperationException("this window has no handle");
                Asks++;
            }
        }
    }
}
