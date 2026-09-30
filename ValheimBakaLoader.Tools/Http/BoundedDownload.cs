using Serilog;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ValheimBakaLoader.Tools.Http
{
    /// <summary>
    /// The clocks one download is held to.
    /// <para>
    /// HttpClient.Timeout is not one of them, and that is the whole reason this type
    /// exists. That timeout is released the moment the response headers are in, so a
    /// site that answers its headers and then stops sending the body is not covered by
    /// it at all: the read sits there for the life of the process. A five minute ceiling
    /// on the client looks like a guard and is not one.
    /// </para>
    /// <para>
    /// Three clocks replace it. The header deadline bounds the wait for the first line
    /// back. The total deadline bounds the whole body however well it is arriving. The
    /// idle window bounds a body that has stopped arriving, and it is re-armed on every
    /// read, so a large pack on a slow household link is never cut off for being slow.
    /// </para>
    /// </summary>
    public sealed class DownloadBudget
    {
        /// <summary>How long the headers of the request may take to arrive.</summary>
        public TimeSpan HeaderTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>The longest the whole download may take, headers and body together.</summary>
        public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>The longest the body may go without a single byte arriving.</summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>The most the body may weigh, or zero for no cap.</summary>
        public long MaxBytes { get; set; }

        /// <summary>A copy with the same clocks, so a caller may adjust one without touching the original.</summary>
        public DownloadBudget Copy() => new()
        {
            HeaderTimeout = HeaderTimeout,
            TotalTimeout = TotalTimeout,
            IdleTimeout = IdleTimeout,
            MaxBytes = MaxBytes,
        };
    }

    /// <summary>
    /// A read-only wrapper that fails the read when nothing has arrived for a while.
    /// Each Read is given its own deadline, so a stream that keeps delivering never
    /// trips it however long the whole download takes, and one that stops delivering is
    /// over in <c>Idle</c> rather than never.
    /// <para>
    /// This is the shape ThunderstoreClient's own watchdog proved on the stalled host in
    /// issue 18, lifted out so every download in the app can be held to it.
    /// </para>
    /// </summary>
    public sealed class IdleWatchdogStream : Stream
    {
        private readonly Stream Inner;
        private readonly TimeSpan Idle;
        private readonly CancellationToken Outer;
        private readonly ILogger Tracer;
        private readonly string What;
        private bool Tripped;

        public IdleWatchdogStream(
            Stream inner, TimeSpan idle, CancellationToken outer, ILogger tracer = null, string what = "download")
        {
            Inner = inner;
            Idle = idle;
            Outer = outer;
            Tracer = tracer;
            What = string.IsNullOrWhiteSpace(what) ? "download" : what;
        }

        /// <summary>How many bytes of the body have arrived so far.</summary>
        public long BytesRead { get; private set; }

        /// <summary>Whether the watchdog gave up on a body that had stopped arriving.</summary>
        public bool Stalled => Tripped;

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

        // The synchronous path, which a StreamReader over this stream will drive.
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken token)
        {
            using var deadline = BoundedDownload.Deadline(Idle, Outer);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, token);
            try
            {
                var read = await Inner.ReadAsync(buffer.AsMemory(offset, count), linked.Token).ConfigureAwait(false);
                BytesRead += read;
                return read;
            }
            catch (OperationCanceledException) when (deadline.Token.IsCancellationRequested && !Outer.IsCancellationRequested)
            {
                Trip();
                throw new TimeoutException(StallSentence());
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken token = default)
        {
            using var deadline = BoundedDownload.Deadline(Idle, Outer);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, token);
            try
            {
                var read = await Inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                BytesRead += read;
                return read;
            }
            catch (OperationCanceledException) when (deadline.Token.IsCancellationRequested && !Outer.IsCancellationRequested)
            {
                Trip();
                throw new TimeoutException(StallSentence());
            }
        }

        /// <summary>
        /// What the host reads when a body stopped arriving. It names the wait and how far
        /// the download had got, because that is what tells a slow site apart from a
        /// connection that died in the middle of the body.
        /// </summary>
        private string StallSentence() =>
            "The " + What + " stopped arriving: nothing came for "
            + Math.Max(1, (int)Idle.TotalSeconds) + " seconds after "
            + WireTrace.Count(BytesRead) + " bytes.";

        /// <summary>The one line the watchdog writes, and it writes it once.</summary>
        private void Trip()
        {
            if (Tripped) return;
            Tripped = true;
            WireTrace.Say(Tracer, StallSentence());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Inner?.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Inner != null) await Inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The one place in the app a download is given its clocks. Every remote read that
    /// streams a body (the self-update asset, the BepInEx pack, a mod package, a language
    /// pack, the mods BakaLoader needs, the Hexium index) goes through here, so a site
    /// that answers headers and then stops sending costs that read its own budget rather
    /// than the whole app.
    /// </summary>
    public static class BoundedDownload
    {
        /// <summary>
        /// A token that gives up after <paramref name="after"/>, linked to whatever budget
        /// the caller is already working inside, so the tighter of the two wins.
        /// </summary>
        public static CancellationTokenSource Deadline(TimeSpan after, CancellationToken outer)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(outer);
            if (after > TimeSpan.Zero) source.CancelAfter(after);
            return source;
        }

        /// <summary>
        /// A client whose own timeout is out of the way, because every stage below carries
        /// its own. Leaving HttpClient.Timeout set would end a large pack on a household
        /// link that is downloading perfectly well, and it never covered the stall anyway.
        /// </summary>
        public static HttpClient Unbounded(HttpClient client)
        {
            if (client != null) client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        /// <summary>
        /// The response headers, under the header deadline. The response is returned with
        /// its body unread, so the caller may check the status, the declared length and
        /// the final address before a byte of it is touched.
        /// </summary>
        public static async Task<HttpResponseMessage> HeadersAsync(
            HttpClient client, Uri url, DownloadBudget budget, CancellationToken outer)
        {
            using var headers = Deadline(budget.HeaderTimeout, outer);
            try
            {
                return await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headers.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (headers.IsCancellationRequested && !outer.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Nothing came back from " + Address(url) + " within "
                    + Math.Max(1, (int)budget.HeaderTimeout.TotalSeconds) + " seconds.");
            }
        }

        /// <summary>
        /// The body of a response already in hand, written to a file under the total
        /// deadline and the idle watchdog, stopping the moment it passes the cap. Answers
        /// how many bytes were written.
        /// </summary>
        public static async Task<long> BodyToFileAsync(
            HttpResponseMessage response,
            string destinationPath,
            DownloadBudget budget,
            CancellationToken outer,
            ILogger tracer = null,
            string what = "download",
            Func<long, Task> onChunk = null)
        {
            using var whole = Deadline(budget.TotalTimeout, outer);

            await using var raw = await response.Content.ReadAsStreamAsync(whole.Token).ConfigureAwait(false);
            await using var watched = new IdleWatchdogStream(raw, budget.IdleTimeout, whole.Token, tracer, what);
            await using var destination = File.Create(destinationPath);

            var cap = budget.MaxBytes;
            var buffer = new byte[81920];
            long total = 0;
            int read;

            try
            {
                while ((read = await watched.ReadAsync(buffer, 0, buffer.Length, whole.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (cap > 0 && total > cap)
                        throw new IOException(
                            "That " + what + " passed the size BakaLoader will fetch ("
                            + WireTrace.Count(cap) + " bytes), so it was stopped.");

                    await destination.WriteAsync(buffer, 0, read, CancellationToken.None).ConfigureAwait(false);
                    if (onChunk != null) await onChunk(total).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (whole.IsCancellationRequested && !outer.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The " + what + " did not finish within "
                    + Math.Max(1, (int)budget.TotalTimeout.TotalSeconds) + " seconds ("
                    + WireTrace.Count(total) + " bytes arrived).");
            }

            return total;
        }

        /// <summary>
        /// The whole of it for the plain case: headers under the header deadline, the body
        /// under the total deadline and the idle watchdog, into a file. Answers how many
        /// bytes were written so the caller can hold them against a published size.
        /// </summary>
        public static async Task<long> ToFileAsync(
            HttpClient client,
            string url,
            string destinationPath,
            DownloadBudget budget,
            CancellationToken outer = default,
            ILogger tracer = null,
            string what = "download")
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address))
                throw new IOException("That download address could not be read: " + url);

            Unbounded(client);

            using var response = await HeadersAsync(client, address, budget, outer).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var cap = budget.MaxBytes;
            if (response.Content.Headers.ContentLength is { } declared && cap > 0 && declared > cap)
                throw new IOException(
                    "That " + what + " is larger than BakaLoader will fetch ("
                    + WireTrace.Count(declared) + " bytes against a limit of " + WireTrace.Count(cap) + ").");

            return await BodyToFileAsync(response, destinationPath, budget, outer, tracer, what).ConfigureAwait(false);
        }

        /// <summary>The address as the trace writes every address: scheme, host and path, and nothing else.</summary>
        private static string Address(Uri uri) => uri == null ? "that address" : WireTrace.Address(uri);
    }
}
