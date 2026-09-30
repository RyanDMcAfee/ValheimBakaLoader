using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Http;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The one shape every download in this app used to be blind to: a site that answers its
    /// HEADERS and then stops sending the body.
    /// <para>
    /// HttpClient.Timeout is released the moment the headers are in, so a five minute ceiling
    /// on the client is not a bound on a streamed body at all. Every download in the app was
    /// written that way, and the worst of them sat on the splash screen: the self-update check
    /// is a launch step, the window opens once every step has finished, and a stalled asset
    /// meant no window, no server, and nothing to do but end the app from Task Manager.
    /// </para>
    /// <para>
    /// The handlers below never open a connection. StallAfterHeaders answers headers and then
    /// hands back a body that produces nothing at all, which is the failure exactly.
    /// </para>
    /// </summary>
    [Collection(WallClockCollection.Name)]
    public class BoundedDownloadTests : BaseTest, IDisposable
    {
        private readonly string Temp = Path.Combine(
            Path.GetTempPath(), "bakaloader-bounded-" + Guid.NewGuid().ToString("N"));

        public BoundedDownloadTests() => Directory.CreateDirectory(Temp);

        public void Dispose()
        {
            try { Directory.Delete(Temp, recursive: true); } catch { /* best effort */ }
        }

        private string File(string name) => Path.Combine(Temp, name);

        // ------------------------------------------------------------------ the transports

        /// <summary>A body that has arrived in full, for the control case.</summary>
        internal sealed class BytesHandler : HttpMessageHandler
        {
            private readonly byte[] Payload;

            public BytesHandler(byte[] payload) => Payload = payload;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(Payload),
                });
        }

        /// <summary>
        /// Headers, then a body that never produces a byte. This is the shape a real stalled
        /// site takes, and the one a client timeout does not cover.
        /// </summary>
        internal sealed class StallAfterHeadersHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StreamContent(new SilentStream()),
                });
        }

        /// <summary>Headers that never arrive at all.</summary>
        internal sealed class SilentHeadersHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        }

        /// <summary>A stream that answers every read by never answering.</summary>
        private sealed class SilentStream : Stream
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
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

            public override async Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken token)
            {
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken token = default)
            {
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }
        }

        internal sealed class OneHandlerProvider : IHttpClientProvider
        {
            private readonly HttpMessageHandler Handler;

            public OneHandlerProvider(HttpMessageHandler handler) => Handler = handler;

            public HttpClient CreateClient() => new(Handler, disposeHandler: false);
        }

        private static DownloadBudget Short() => new()
        {
            HeaderTimeout = TimeSpan.FromMilliseconds(400),
            TotalTimeout = TimeSpan.FromSeconds(4),
            IdleTimeout = TimeSpan.FromMilliseconds(400),
        };

        // ------------------------------------------------------------------ the bounds

        [Fact(Timeout = 60000)]
        public async Task A_body_that_stops_arriving_ends_in_the_idle_window_not_never()
        {
            using var handler = new StallAfterHeadersHandler();
            using var client = new HttpClient(handler, disposeHandler: false)
            {
                // The five minute ceiling every one of these downloads used to lean on. It is
                // set here deliberately: the point of the test is that this number is not what
                // ends the read, and before the budget existed nothing was.
                Timeout = TimeSpan.FromMinutes(5),
            };

            var watch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<Exception>(() => BoundedDownload.ToFileAsync(
                client, "https://example.invalid/pack.zip", File("stalled.zip"), Short()));
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
                "a stalled body took " + watch.Elapsed + " to give up");
        }

        [Fact(Timeout = 60000)]
        public async Task The_sentence_a_stalled_body_raises_names_the_wait_and_how_far_it_got()
        {
            using var handler = new StallAfterHeadersHandler();
            using var client = new HttpClient(handler, disposeHandler: false);

            var raised = await Assert.ThrowsAsync<TimeoutException>(() => BoundedDownload.ToFileAsync(
                client, "https://example.invalid/pack.zip", File("stalled2.zip"), Short(),
                what: "self-update download"));

            // A host reading the log has to be able to tell a slow site from a connection that
            // died in the middle of the body, and how many bytes had arrived is what says which.
            Assert.Contains("self-update download", raised.Message);
            Assert.Contains("stopped arriving", raised.Message);
            Assert.Contains("0 bytes", raised.Message);
        }

        [Fact(Timeout = 60000)]
        public async Task Headers_that_never_arrive_end_in_the_header_window()
        {
            using var handler = new SilentHeadersHandler();
            using var client = new HttpClient(handler, disposeHandler: false);

            var watch = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(() => BoundedDownload.ToFileAsync(
                client, "https://example.invalid/pack.zip", File("silent.zip"), Short()));
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5),
                "a silent site took " + watch.Elapsed + " to give up");
        }

        [Fact(Timeout = 60000)]
        public async Task A_body_that_arrives_is_written_whole_and_its_byte_count_answered()
        {
            var payload = new byte[64 * 1024 + 7];
            new Random(7).NextBytes(payload);

            using var handler = new BytesHandler(payload);
            using var client = new HttpClient(handler, disposeHandler: false);

            var path = File("good.zip");
            var written = await BoundedDownload.ToFileAsync(
                client, "https://example.invalid/pack.zip", path, Short());

            Assert.Equal(payload.LongLength, written);
            Assert.Equal(payload, System.IO.File.ReadAllBytes(path));
        }

        [Fact(Timeout = 60000)]
        public async Task A_body_past_the_cap_is_stopped_rather_than_written()
        {
            var payload = new byte[200 * 1024];
            using var handler = new BytesHandler(payload);
            using var client = new HttpClient(handler, disposeHandler: false);

            var budget = Short();
            budget.MaxBytes = 4096;

            await Assert.ThrowsAsync<IOException>(() => BoundedDownload.ToFileAsync(
                client, "https://example.invalid/pack.zip", File("big.zip"), budget));
        }

        /// <summary>
        /// The watchdog is re-armed on every read, so a body that keeps arriving is never cut
        /// off for being slow. Without this the fix would be a new defect: a large pack on a
        /// household link would die at the idle window however well it was downloading.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_slow_body_that_keeps_arriving_is_never_cut_off()
        {
            using var handler = new TrickleHandler(chunks: 8, gap: TimeSpan.FromMilliseconds(120));
            using var client = new HttpClient(handler, disposeHandler: false);

            var budget = new DownloadBudget
            {
                HeaderTimeout = TimeSpan.FromSeconds(2),
                TotalTimeout = TimeSpan.FromSeconds(20),
                // Shorter than the whole download takes, longer than one gap between chunks.
                IdleTimeout = TimeSpan.FromMilliseconds(500),
            };

            var written = await BoundedDownload.ToFileAsync(
                client, "https://example.invalid/pack.zip", File("trickle.zip"), budget);

            Assert.Equal(8 * 1024, written);
        }

        /// <summary>A body that arrives a little at a time, with a gap between each piece.</summary>
        private sealed class TrickleHandler : HttpMessageHandler
        {
            private readonly int Chunks;
            private readonly TimeSpan Gap;

            public TrickleHandler(int chunks, TimeSpan gap)
            {
                Chunks = chunks;
                Gap = gap;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StreamContent(new TrickleStream(Chunks, Gap)),
                });
        }

        private sealed class TrickleStream : Stream
        {
            private readonly TimeSpan Gap;
            private int Left;

            public TrickleStream(int chunks, TimeSpan gap)
            {
                Left = chunks;
                Gap = gap;
            }

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
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

            public override async Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken token)
                => await ReadAsync(buffer.AsMemory(offset, count), token);

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken token = default)
            {
                if (Left <= 0) return 0;
                await Task.Delay(Gap, token);
                Left--;

                var take = Math.Min(1024, buffer.Length);
                buffer.Span.Slice(0, take).Fill(1);
                return take;
            }
        }
    }
}
