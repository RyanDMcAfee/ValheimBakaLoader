using System;
using System.Diagnostics;
using System.Linq;
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
    /// Issue 18: a stalled network costs seconds, not ninety minutes.
    /// <para>
    /// A host's machine could not reach thunderstore.io through .NET at all. curl on the same
    /// box worked. One timeout of a hundred and twenty seconds covered every request, a failed
    /// read remembered nothing, and every one of their twenty-two installed mods asked for the
    /// index in turn behind one lock: listing index, wait it out, full listing, wait it out,
    /// hand the lock on. The scan took an hour and a half and came back with nothing.
    /// </para>
    /// <para>
    /// Nothing here opens a connection. The handler below answers no request at all, ever, which
    /// is the shape of the failure exactly, and the list of addresses it writes down is the proof
    /// that the callers after the first one never went out.
    /// </para>
    /// </summary>
    [Collection(WallClockCollection.Name)]
    public class ThunderstoreStalledNetworkTests : BaseTest
    {
        /// <summary>
        /// A transport that accepts a request and never answers it. It returns only when the
        /// caller's own deadline cancels the call, which is the thing under test.
        /// </summary>
        private sealed class SilentHandler : HttpMessageHandler
        {
            private int Count;

            public int Requests => Volatile.Read(ref Count);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Count);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        }

        private sealed class SilentProvider : IHttpClientProvider
        {
            public SilentHandler Handler { get; } = new();

            public HttpClient CreateClient() => new(Handler, disposeHandler: false);
        }

        /// <summary>A client whose stage deadlines are short enough to run in a test.</summary>
        private ThunderstoreClient NewClient(SilentProvider provider, DateTime now)
        {
            var context = new RestClientContext(GetService<Serilog.ILogger>(), provider);
            return new ThunderstoreClient(context)
            {
                UtcNow = () => now,
                HeaderTimeout = TimeSpan.FromMilliseconds(120),
                DocumentTimeout = TimeSpan.FromMilliseconds(150),
                ListingIdleTimeout = TimeSpan.FromMilliseconds(120),
                ListingTotalTimeout = TimeSpan.FromMilliseconds(200),
                RefreshBudget = TimeSpan.FromSeconds(2),
            };
        }

        [Fact]
        public async Task A_scan_of_twenty_two_mods_over_a_dead_network_is_over_in_seconds()
        {
            var provider = new SilentProvider();
            var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
            var client = NewClient(provider, now);

            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 22; i++)
                Assert.Null(await client.GetLatestAsync("Owner", "Mod" + i));
            watch.Stop();

            // The whole point of the memo. Before it, this was twenty-two trips out; with it,
            // the first caller pays for one attempt down each of the two paths and the other
            // twenty-one are answered out of the memo without a request.
            Assert.True(provider.Handler.Requests <= 2,
                "the stalled site was asked " + provider.Handler.Requests + " times for one scan");
            Assert.Equal(1, client.FetchCount);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
                "a scan over a dead network took " + watch.Elapsed);

            // And the memo says what happened and for how long it stands.
            Assert.NotNull(client.LastFailure);
            Assert.Equal("timeout", client.LastFailure.Reason);
            Assert.Equal(1, client.LastFailure.Streak);
            Assert.Equal(ThunderstoreClient.BackoffSteps[0], client.LastFailure.BackOff);
            Assert.Equal(now + ThunderstoreClient.BackoffSteps[0], client.LastFailure.RetryAtUtc);
        }

        [Fact]
        public async Task A_second_lookup_inside_the_window_makes_no_request()
        {
            var provider = new SilentProvider();
            var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
            var client = NewClient(provider, now);

            Assert.Null(await client.GetLatestAsync("Owner", "First"));
            var spent = provider.Handler.Requests;
            Assert.True(spent > 0, "the first lookup never went out at all");

            // Twenty-nine seconds later, still inside the first backoff step.
            client.UtcNow = () => now.AddSeconds(29);
            Assert.Null(await client.GetLatestAsync("Owner", "Second"));

            Assert.Equal(spent, provider.Handler.Requests);
            Assert.Equal(1, client.FetchCount);
        }

        [Fact]
        public async Task The_window_runs_out_and_the_next_failure_backs_off_further()
        {
            var provider = new SilentProvider();
            var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
            var client = NewClient(provider, now);

            Assert.Null(await client.GetLatestAsync("Owner", "First"));
            Assert.Equal(1, client.FetchCount);

            // Past the thirty seconds: one more attempt is allowed, and it fails too.
            client.UtcNow = () => now.AddSeconds(31);
            Assert.Null(await client.GetLatestAsync("Owner", "Second"));

            Assert.Equal(2, client.FetchCount);
            Assert.Equal(2, client.LastFailure.Streak);
            Assert.Equal(ThunderstoreClient.BackoffSteps[1], client.LastFailure.BackOff);
        }

        [Fact]
        public async Task The_backoff_steps_are_the_ones_the_release_promises_and_the_last_is_a_cap()
        {
            Assert.Equal(
                new[] { 30.0, 60.0, 120.0, 300.0, 600.0, 900.0 },
                ThunderstoreClient.BackoffSteps.Select(s => s.TotalSeconds).ToArray());

            var provider = new SilentProvider();
            var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
            var client = NewClient(provider, now);

            // Eight failures in a row, each one past the previous window.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                client.UtcNow = () => now;
                Assert.Null(await client.GetLatestAsync("Owner", "Mod" + attempt));
                now = client.LastFailure.RetryAtUtc.AddSeconds(1);
            }

            Assert.Equal(8, client.FetchCount);
            Assert.Equal(ThunderstoreClient.BackoffSteps[^1], client.LastFailure.BackOff);
        }

        [Fact]
        public async Task A_press_of_scan_puts_the_backoff_down()
        {
            var provider = new SilentProvider();
            var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
            var client = NewClient(provider, now);

            Assert.Null(await client.GetLatestAsync("Owner", "First"));
            Assert.Equal(1, client.FetchCount);

            // Inside the window, so a lookup would be refused the trip. Scan is the host
            // saying "try it now", and it goes out.
            await client.RefreshAsync();

            Assert.Equal(2, client.FetchCount);
            // It failed again, so a new window opens, and it opens at the first step rather
            // than carrying the old streak forward.
            Assert.Equal(1, client.LastFailure.Streak);
        }

        [Fact]
        public void A_warning_carrying_an_exception_names_the_exception_type()
        {
            var logger = GetService<IApplicationLogger>();

            var thrown = new HttpRequestException(
                "An error occurred while sending the request.",
                new System.Net.Sockets.SocketException(10060));

            logger.Warning(thrown, "Thunderstore listing index read failed.");

            var line = logger.LogBuffer.LastOrDefault(l => l.Contains("listing index read failed"));
            Assert.NotNull(line);
            // The type, and the reason underneath it. The old pipeline rendered the message
            // and dropped the exception, so a host reading this line learned nothing at all.
            Assert.Contains(nameof(HttpRequestException), line);
            Assert.Contains(nameof(System.Net.Sockets.SocketException), line);
        }

        // ------------------------------------------------------------------ the one read slot

        /// <summary>
        /// The memo alone did not finish the job, because it only ever stood in front of the
        /// callers who arrived AFTER a read had failed. The ones who arrive while the stalling
        /// read is still stalling used to queue on a bare wait for the one read slot, with no
        /// deadline of their own, and wait out the whole of somebody else's stalled request.
        /// Those are exactly the callers a scan is made of.
        /// <para>
        /// Here a refresh takes the slot and holds it for four seconds. The lookup that arrives
        /// behind it gives up on the queue in a fraction of that and is answered.
        /// </para>
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_caller_behind_a_stalled_read_does_not_wait_it_out()
        {
            var provider = new SilentProvider();
            var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

            var client = new ThunderstoreClient(
                new RestClientContext(GetService<Serilog.ILogger>(), provider))
            {
                UtcNow = () => now,
                HeaderTimeout = TimeSpan.FromSeconds(4),
                DocumentTimeout = TimeSpan.FromSeconds(4),
                ListingIdleTimeout = TimeSpan.FromSeconds(4),
                ListingTotalTimeout = TimeSpan.FromSeconds(4),
                RefreshBudget = TimeSpan.FromSeconds(8),
                LockWait = TimeSpan.FromMilliseconds(300),
            };

            var holding = client.RefreshAsync();

            // Let the refresh take the slot. It is stalling on the wire, not finishing.
            await Task.Delay(300);
            Assert.False(holding.IsCompleted, "the refresh answered, so it never held the slot");

            var watch = Stopwatch.StartNew();
            Assert.Null(await client.GetLatestAsync("Owner", "Mod"));
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2),
                "the caller queued behind the stalled read for " + watch.Elapsed);

            // The turn that never came is a failed read like any other, so the memo is written
            // and the callers behind this one are answered out of it instead of queueing too.
            Assert.NotNull(client.LastFailure);

            await holding;
        }

        /// <summary>
        /// And the memo gates the PER-PACKAGE page as well as the index. It never used to: the
        /// window was read inside EnsureIndexAsync only, and Update all asks the package page
        /// for every mod on the list. Twenty-two mods on a machine that cannot reach the site
        /// was twenty-two page requests, each waiting out the document timeout, with the memo
        /// saying all along that the site was down.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task Inside_the_window_a_live_lookup_makes_no_request()
        {
            var provider = new SilentProvider();
            var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
            var client = NewClient(provider, now);

            // One failed read, which is what opens the window.
            Assert.Null(await client.GetLatestAsync("Owner", "First"));
            var spent = provider.Handler.Requests;
            Assert.True(spent > 0, "the first lookup never went out at all");
            Assert.NotNull(client.LastFailure);

            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 22; i++)
            {
                var live = await client.LookupLiveAsync("Owner", "Mod" + i);
                Assert.False(live.Answered);
            }
            watch.Stop();

            Assert.Equal(spent, provider.Handler.Requests);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2),
                "twenty-two live lookups inside the window took " + watch.Elapsed);
        }
    }
}
