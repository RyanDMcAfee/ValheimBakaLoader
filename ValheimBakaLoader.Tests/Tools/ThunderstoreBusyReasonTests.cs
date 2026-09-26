using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// A press that never left the machine is worded as a press that never left the machine.
    /// <para>
    /// WHY THIS EXISTS. The failed-scan panel words its sentence from the memo's reason, and
    /// two of the places a memo is written are presses that gave up waiting for another
    /// read's turn: they asked the site nothing at all. The memo they write says "busy", and
    /// the page has a sentence for that which tells a host to try again in a moment.
    /// </para>
    /// <para>
    /// The memo used to be written as <c>PendingReason ?? fallbackReason</c>, and
    /// PendingReason belongs to whichever read is still holding the lock: it is set the
    /// moment that read's first stage throws, and consumed when that read finally writes its
    /// own memo. A press that timed out on the lock in between picked it up and was worded as
    /// the network failure the OTHER read had just had. A host who pressed Scan twice was
    /// then told their machine could not reach thunderstore.io, and went looking at their
    /// router for a queue that is inside BakaLoader.
    /// </para>
    /// </summary>
    public class ThunderstoreBusyReasonTests : BaseTest
    {
        /// <summary>
        /// A transport that fails the listing index at once and then HOLDS the full listing
        /// open until it is let go. That hold is the window: the read that is inside it has
        /// already written its reason down and has not yet written its memo.
        /// </summary>
        private sealed class HoldingHandler : HttpMessageHandler
        {
            public readonly TaskCompletionSource<bool> Holding =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public readonly TaskCompletionSource<bool> LetGo =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri?.ToString() ?? "";

                // The first stage, which throws and leaves its reason behind for the memo.
                if (url.Contains("package-listing-index", StringComparison.OrdinalIgnoreCase))
                    throw new System.Net.Sockets.SocketException(10060);

                // The second stage, held open.
                Holding.TrySetResult(true);
                using (cancellationToken.Register(() => LetGo.TrySetResult(true)))
                {
                    await LetGo.Task.ConfigureAwait(false);
                }

                throw new System.Net.Sockets.SocketException(10060);
            }
        }

        private sealed class HoldingProvider : IHttpClientProvider
        {
            public HoldingHandler Handler { get; } = new();

            public HttpClient CreateClient() => new(Handler, disposeHandler: false);
        }

        [Fact]
        public async Task A_press_that_timed_out_waiting_its_turn_is_worded_as_busy_and_not_as_the_network()
        {
            var provider = new HoldingProvider();
            var context = new RestClientContext(GetService<Serilog.ILogger>(), provider);

            var client = new ThunderstoreClient(context)
            {
                // Long enough that the holding stage is still holding while the second press
                // arrives, and short enough that the test cannot hang if it is not.
                HeaderTimeout = TimeSpan.FromSeconds(30),
                RefreshBudget = TimeSpan.FromSeconds(30),
                // The whole point: the second press gives up waiting for the lock.
                LockWait = TimeSpan.FromMilliseconds(50),
            };

            // The read that holds the lock. Its first stage throws, which writes the reason
            // down; its second stage is held open before it can write its memo.
            var held = client.RefreshAsync();
            await provider.Handler.Holding.Task.WaitAsync(TimeSpan.FromSeconds(20));

            // A second press, arriving inside that window. It asks the site nothing.
            await client.RefreshAsync();

            var memo = client.LastFailure;
            Assert.NotNull(memo);
            Assert.Equal("busy", memo.Reason);
            // And it is a name the page has words for, rather than one it has to fall back on.
            Assert.Equal("busy", ThunderstoreClient.ReasonName(memo.Reason));

            // Let the first read finish. Its own memo is its own reason, so the reason it
            // wrote down was not lost by the busy press either.
            provider.Handler.LetGo.TrySetResult(true);
            await held.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.NotEqual("busy", client.LastFailure.Reason);
            Assert.Equal(client.LastFailure.Reason, ThunderstoreClient.ReasonName(client.LastFailure.Reason));
        }

        /// <summary>
        /// Every name a memo can carry is one the page keeps a sentence for, and anything
        /// else is worded as the general one rather than reaching a host as the word it is.
        /// An internal stage name in a slot inside a translated sentence is neither a reason
        /// nor a language anybody reads.
        /// </summary>
        [Theory]
        [InlineData("timeout", "timeout")]
        [InlineData("connect", "connect")]
        [InlineData("tls", "tls")]
        [InlineData("http", "http")]
        [InlineData("read", "read")]
        [InlineData("answered", "answered")]
        [InlineData("busy", "busy")]
        [InlineData("index read", "unknown")]
        [InlineData("SocketException", "unknown")]
        [InlineData("", "unknown")]
        [InlineData(null, "unknown")]
        public void A_reason_name_is_one_of_the_closed_list_or_it_is_unknown(string reason, string expected)
        {
            Assert.Equal(expected, ThunderstoreClient.ReasonName(reason));
        }
    }
}
