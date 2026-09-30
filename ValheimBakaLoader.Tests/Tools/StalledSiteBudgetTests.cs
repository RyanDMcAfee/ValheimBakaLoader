using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The same stalled-site shape as <see cref="BoundedDownloadTests"/>, driven through the
    /// real clients that used to be blind to it: Hexium's index read and its untimed lock, and
    /// the restart hooks that hold a world down while they wait.
    /// </summary>
    [Collection(WallClockCollection.Name)]
    public class StalledSiteBudgetTests : BaseTest
    {
        /// <summary>Headers, then a body that never produces a byte.</summary>
        private sealed class StallProvider : IHttpClientProvider
        {
            private int Count;

            public int Requests => Volatile.Read(ref Count);

            public HttpClient CreateClient() => new(new Handler(this), disposeHandler: true);

            private sealed class Handler : HttpMessageHandler
            {
                private readonly StallProvider Owner;

                public Handler(StallProvider owner) => Owner = owner;

                protected override Task<HttpResponseMessage> SendAsync(
                    HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    Interlocked.Increment(ref Owner.Count);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = request,
                        Content = new StreamContent(new NeverStream()),
                    });
                }
            }
        }

        private sealed class NeverStream : System.IO.Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }
        }

        private HexiumClient Stalled()
        {
            var client = new HexiumClient(new StallProvider(), GetService<IApplicationLogger>());
            client.IndexBudget = new DownloadBudget
            {
                HeaderTimeout = TimeSpan.FromMilliseconds(400),
                TotalTimeout = TimeSpan.FromSeconds(2),
                IdleTimeout = TimeSpan.FromMilliseconds(400),
            };
            client.LockWait = TimeSpan.FromMilliseconds(300);
            return client;
        }

        /// <summary>
        /// The 120 second RequestTimeout on the Hexium client was an HttpClient.Timeout, spent
        /// by the time the first byte of the body was asked for, and the body was read by a raw
        /// loop with no token at all. The read never ended, so the 429 back-off and the failure
        /// memo behind it never engaged and the mod scan holding it never returned.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_stalled_index_read_ends_rather_than_running_for_ever()
        {
            var client = Stalled();

            var watch = Stopwatch.StartNew();
            var lookup = await client.LookupAsync("Owner-Mod");
            watch.Stop();

            Assert.Null(lookup.Package);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15),
                "a stalled Hexium index read took " + watch.Elapsed);

            // And it failed, so the memo is written and the callers behind it are answered out
            // of it rather than each paying for their own attempt.
            Assert.False(lookup.IndexAvailable);
            Assert.False(string.IsNullOrWhiteSpace(lookup.Error));
        }

        /// <summary>
        /// The one index slot was taken with a bare WaitAsync, so every caller queued behind a
        /// stalled read waited out the whole of somebody else's stall. A five row scan taken
        /// during one was a five row scan that never came back.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_caller_behind_a_stalled_index_read_does_not_wait_it_out()
        {
            var client = Stalled();
            client.IndexBudget.TotalTimeout = TimeSpan.FromSeconds(4);
            client.IndexBudget.IdleTimeout = TimeSpan.FromSeconds(4);

            var holding = client.RefreshAsync();
            await Task.Delay(300);
            Assert.False(holding.IsCompleted, "the refresh answered, so it never held the slot");

            var watch = Stopwatch.StartNew();
            Assert.Null(await client.GetPackageAsync("Owner-Mod"));
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3),
                "the caller queued behind the stalled read for " + watch.Elapsed);

            await holding;
        }

        // ------------------------------------------------------------- the restart hooks

        /// <summary>
        /// Both restart hooks promise in their own words that a failure is logged and the
        /// restart carries on, and both kept that promise only for a THROW. The server is
        /// already stopped when these run and the relaunch sits behind the await, so one hook
        /// that HUNG left the world down with nothing after "Applying pending mod updates".
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_hook_that_never_answers_is_given_up_on_rather_than_waited_out()
        {
            var said = (string)null;
            var watch = Stopwatch.StartNew();

            var finished = await LaunchBudget.WithinAsync(
                () => Task.Delay(Timeout.Infinite),
                TimeSpan.FromMilliseconds(300),
                line => said = line,
                "The mod update step");

            watch.Stop();

            Assert.False(finished);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5),
                "the wait on a hung hook took " + watch.Elapsed);
            Assert.NotNull(said);
            Assert.Contains("The mod update step", said);
        }

        [Fact(Timeout = 60000)]
        public async Task A_hook_that_answers_inside_its_budget_is_waited_for_in_full()
        {
            var ran = false;
            var finished = await LaunchBudget.WithinAsync(
                async () => { await Task.Delay(30); ran = true; },
                TimeSpan.FromSeconds(5));

            Assert.True(finished);
            Assert.True(ran);
        }

        /// <summary>
        /// A hook that FAILS still fails: only the wait is bounded. A budget that swallowed a
        /// real exception would hide the failures the hooks were written to report.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_hook_that_throws_still_throws()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => LaunchBudget.WithinAsync(
                () => Task.FromException(new InvalidOperationException("the pack was not there")),
                TimeSpan.FromSeconds(5)));
        }

        /// <summary>
        /// The restart road really uses the budget, on both hooks, and through the form of
        /// it that STOPS the hook rather than walking away from it. RestartHookStopTests
        /// drives the resume step itself and proves the relaunch waits for a hook that was
        /// told to stop; this one names the call the resume step makes.
        /// </summary>
        [Fact]
        public void Both_restart_hooks_are_run_under_the_budget()
        {
            var source = AppSourceTree.Read("ValheimBakaLoader", "Game", "ValheimServer.cs");

            Assert.Contains("Tools.LaunchBudget.StopWithinAsync(\n                        ApplyLoaderUpdate, RestartHookBudget", source);
            Assert.Contains("Tools.LaunchBudget.StopWithinAsync(\n                        ApplyModUpdates, RestartHookBudget", source);
        }

        /// <summary>
        /// The language pack download already had the stall window, which is the right guard and
        /// the one that catches the failure; what it had no ceiling over was a body that keeps
        /// trickling, because the stall window is re-armed on every read and so can never also be
        /// a total. The two clocks are separate now and the stall one is linked inside the total,
        /// so whichever runs out first ends the read.
        /// </summary>
        [Fact]
        public void A_language_pack_download_carries_a_ceiling_as_well_as_a_stall_window()
        {
            var source = AppSourceTree.Read("ValheimBakaLoader", "Tools", "LanguagePackService.cs");

            Assert.Contains("public TimeSpan TotalDownloadTimeout", source);
            Assert.Contains("whole.CancelAfter(TotalDownloadTimeout)", source);
            Assert.Contains("CreateLinkedTokenSource(whole.Token)", source);
        }

        /// <summary>And so does the splash, which is the road that used to hold the window shut.</summary>
        [Fact]
        public void Every_launch_step_carries_a_clock()
        {
            var source = AppSourceTree.Read("ValheimBakaLoader", "Forms", "SplashForm.cs");

            Assert.Contains("LaunchBudget.WithinAsync(", source);
            Assert.Contains("budget ?? LaunchBudget.PerStep", source);
        }
    }
}
