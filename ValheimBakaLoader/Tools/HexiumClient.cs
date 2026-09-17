using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// What a Hexium lookup answered: the package when the site has one, whether
    /// the index could be read at all, and a sentence to show the host when it
    /// could not. "Not found" and "could not ask" are different answers, and the
    /// install path has to tell them apart.
    /// </summary>
    public sealed class HexiumLookup
    {
        public HexiumPackage Package { get; init; }

        public bool IndexAvailable { get; init; }

        public string Error { get; init; }

        public bool Found => Package != null;
    }

    public interface IHexiumClient
    {
        /// <summary>
        /// The Hexium package with this exact full name ("Owner-Name"), or null
        /// when the site has no such package or the index could not be read.
        /// Never throws, so a mod scan is never broken by Hexium being down.
        /// </summary>
        Task<HexiumPackage> GetPackageAsync(string fullName);

        /// <summary>
        /// The same lookup, with enough detail to tell the host why an install
        /// could not go ahead. Never throws.
        /// </summary>
        Task<HexiumLookup> LookupAsync(string fullName);
    }

    /// <summary>
    /// Read-only client for the Hexium Valheim package index. Hexium is a second,
    /// separate mod site: whoever runs it is not named on the site, its accounts are
    /// Discord sign-ins, and a name there is not proof of the same person's name on
    /// Thunderstore. BakaLoader only ever talks to it when the host has turned the
    /// "Also check Hexium" switch on, and this class is the only thing in the app
    /// that opens a connection to it.
    /// <para>
    /// The whole index is fetched once and held for <see cref="CacheTtl"/>, so a
    /// machine with the switch on reaches hexium.gg about four times an hour at most
    /// while BakaLoader is open, however many mods are installed. The cache is per
    /// instance, not static, so a test gets a clean client and the app gets one
    /// shared singleton.
    /// </para>
    /// <para>
    /// Failures never throw and never wipe what is already held: a blip leaves the
    /// previous answer standing. A 429 is honoured to the second the site asks for
    /// through Retry-After; every other failure backs off on its own doubling
    /// schedule, so a site that is down is not hammered.
    /// </para>
    /// </summary>
    public class HexiumClient : IHexiumClient
    {
        /// <summary>
        /// The Valheim index. The community listing index is deliberately not used:
        /// it answers with every game's packages, not this one's.
        /// </summary>
        public const string V1IndexUrl = "https://valheim.hexium.gg/api/v1/package/";

        /// <summary>Hexium's own registrable domain. Nothing else is ever contacted.</summary>
        public const string RootHost = "hexium.gg";

        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(1);

        private readonly IHttpClientProvider HttpClientProvider;
        private readonly Logging.IApplicationLogger Logger;
        private readonly SemaphoreSlim IndexLock = new(1, 1);

        private Dictionary<string, HexiumPackage> Index;
        private DateTime IndexFetchedUtc = DateTime.MinValue;
        private DateTime HoldOffUntilUtc = DateTime.MinValue;
        private int ConsecutiveFailures;
        private string LastError;

        public HexiumClient(IHttpClientProvider httpClientProvider, Logging.IApplicationLogger logger)
        {
            HttpClientProvider = httpClientProvider;
            Logger = logger;
        }

        /// <summary>
        /// How many times this client has actually gone out to the network. Read by
        /// the tests that prove one fetch per cache window, and that a source left
        /// off makes no request at all.
        /// </summary>
        public int FetchCount { get; private set; }

        /// <summary>Test seam: the clock the cache and the back-off read.</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public async Task<HexiumPackage> GetPackageAsync(string fullName) =>
            (await LookupAsync(fullName)).Package;

        public async Task<HexiumLookup> LookupAsync(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return new HexiumLookup { IndexAvailable = false, Error = "No mod name to look up." };
            }

            Dictionary<string, HexiumPackage> index;
            try
            {
                index = await EnsureIndexAsync();
            }
            catch (Exception e)
            {
                // Belt to the brace: nothing below throws, and neither does this.
                Logger.Warning(e, "Hexium index lookup failed.");
                return new HexiumLookup { IndexAvailable = false, Error = HexiumUnreachableMessage() };
            }

            if (index == null)
            {
                return new HexiumLookup { IndexAvailable = false, Error = HexiumUnreachableMessage() };
            }

            index.TryGetValue(fullName.Trim(), out var package);
            return new HexiumLookup { Package = package, IndexAvailable = true };
        }

        /// <summary>A plain sentence naming the site, for the host to read.</summary>
        private string HexiumUnreachableMessage() =>
            string.IsNullOrWhiteSpace(LastError)
                ? "Hexium did not answer. Try again in a little while."
                : $"Hexium did not answer ({LastError}). Try again in a little while.";

        /// <summary>
        /// The held index while it is fresh, otherwise one fetch shared by every
        /// caller waiting on it. A failed fetch leaves whatever was held in place.
        /// </summary>
        private async Task<Dictionary<string, HexiumPackage>> EnsureIndexAsync()
        {
            if (IsFresh()) return Index;

            await IndexLock.WaitAsync();
            try
            {
                // Somebody else may have refreshed it while this call waited.
                if (IsFresh()) return Index;

                // A site that asked to be left alone is left alone: no request goes
                // out until the moment it named, and the held answer stands.
                if (UtcNow() < HoldOffUntilUtc) return Index;

                var fresh = await FetchIndexAsync();
                if (fresh != null)
                {
                    Index = fresh;
                    IndexFetchedUtc = UtcNow();
                    ConsecutiveFailures = 0;
                    HoldOffUntilUtc = DateTime.MinValue;
                    LastError = null;
                }

                return Index;
            }
            finally
            {
                IndexLock.Release();
            }
        }

        private bool IsFresh() => Index != null && UtcNow() - IndexFetchedUtc < CacheTtl;

        /// <summary>
        /// Reads the index into a lookup keyed by exact full name. Matching is by
        /// full name and nothing else: the two sites hold packages whose names differ
        /// only by capitals, and an id from one site means nothing on the other.
        /// Returns null on any failure, having recorded how long to wait.
        /// </summary>
        private async Task<Dictionary<string, HexiumPackage>> FetchIndexAsync()
        {
            try
            {
                FetchCount++;

                using var client = HttpClientProvider.CreateClient();
                client.Timeout = RequestTimeout;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent());
                client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

                using var response = await client.GetAsync(V1IndexUrl, HttpCompletionOption.ResponseHeadersRead);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = ReadRetryAfter(response) ?? NextBackoff();
                    HoldOffUntilUtc = UtcNow() + wait;
                    ConsecutiveFailures++;
                    LastError = "the site asked for a pause";
                    Logger.Information("Hexium asked for a pause of {0}; no further request until then.", wait);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    RecordFailure($"HTTP {(int)response.StatusCode}");
                    return null;
                }

                // The index arrives compressed, because BakaLoader asked for it that way.
                // Unpacking is done here rather than left to the transport: the app hands
                // out plain clients, so a body that came back packed would otherwise reach
                // the reader as bytes it could make no sense of.
                using var stream = await ReadBodyAsync(response);
                using var streamReader = new StreamReader(stream);
                using var jsonReader = new JsonTextReader(streamReader);

                var serializer = new JsonSerializer();
                var map = new Dictionary<string, HexiumPackage>(StringComparer.Ordinal);
                var objectsRead = 0;

                while (jsonReader.Read())
                {
                    // Only the objects sitting directly in the top-level array are
                    // packages; anything nested is read by the deserializer itself.
                    if (jsonReader.TokenType != JsonToken.StartObject) continue;
                    objectsRead++;

                    var package = serializer.Deserialize<HexiumPackage>(jsonReader);
                    if (package == null) continue;
                    if (string.IsNullOrWhiteSpace(package.Owner) || string.IsNullOrWhiteSpace(package.Name)) continue;

                    var key = string.IsNullOrWhiteSpace(package.FullName)
                        ? $"{package.Owner}-{package.Name}"
                        : package.FullName.Trim();

                    // Exact-name keys, case and all. Where two packages would share a
                    // key the first one read keeps it rather than being overwritten.
                    if (!map.ContainsKey(key)) map[key] = package;
                }

                Logger.Information("Hexium index loaded: {0} packages (of {1} objects read).", map.Count, objectsRead);
                return map;
            }
            catch (Exception e)
            {
                RecordFailure(e.Message);
                Logger.Warning(e, "Hexium index fetch error.");
                return null;
            }
        }

        /// <summary>
        /// The most of a response body this will hold. The index runs to a few megabytes
        /// packed; this leaves room for it to grow many times over and still refuses a
        /// body that never ends.
        /// </summary>
        public long MaxIndexBytes { get; set; } = 128L * 1024 * 1024;

        /// <summary>
        /// The response body as readable text, unpacked when it came packed.
        /// <para>
        /// Only gzip is asked for, so that is the case that matters, but a server that
        /// answers with deflate is handled too: HTTP has meant both the zlib wrapping and
        /// the bare stream under that name for thirty years, so the first two bytes decide
        /// which one arrived rather than a guess.
        /// </para>
        /// </summary>
        private async Task<Stream> ReadBodyAsync(HttpResponseMessage response)
        {
            var buffer = new MemoryStream();
            await using (var raw = await response.Content.ReadAsStreamAsync())
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await raw.ReadAsync(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > MaxIndexBytes)
                        throw new IOException($"The Hexium index passed {MaxIndexBytes} bytes, so it was not read.");

                    buffer.Write(chunk, 0, read);
                }
            }

            buffer.Position = 0;

            var encoding = "";
            foreach (var value in response.Content.Headers.ContentEncoding)
            {
                if (!string.IsNullOrWhiteSpace(value)) encoding = value.Trim().ToLowerInvariant();
            }

            if (encoding is "gzip" or "x-gzip")
                return new GZipStream(buffer, CompressionMode.Decompress);

            if (encoding == "deflate")
                return LooksLikeZlib(buffer)
                    ? new ZLibStream(buffer, CompressionMode.Decompress)
                    : new DeflateStream(buffer, CompressionMode.Decompress);

            // Nothing named, or something nobody asked for: read it as it came.
            return buffer;
        }

        /// <summary>
        /// True when a stream opens with a zlib header. The first byte carries the
        /// compression method in its low nibble and the window size above it, and the two
        /// bytes together are a multiple of 31. Leaves the stream where it found it.
        /// </summary>
        private static bool LooksLikeZlib(MemoryStream buffer)
        {
            if (buffer.Length < 2) return false;

            var start = buffer.Position;
            var first = buffer.ReadByte();
            var second = buffer.ReadByte();
            buffer.Position = start;

            if (first < 0 || second < 0) return false;
            if ((first & 0x0F) != 8) return false;

            return ((first << 8) | second) % 31 == 0;
        }

        private void RecordFailure(string reason)
        {
            ConsecutiveFailures++;
            LastError = reason;
            HoldOffUntilUtc = UtcNow() + NextBackoff();
        }

        /// <summary>Doubling wait, one minute upward, capped at the cache window.</summary>
        private TimeSpan NextBackoff()
        {
            var steps = Math.Min(ConsecutiveFailures, 8);
            var ticks = FirstBackoff.Ticks * (long)Math.Pow(2, Math.Max(0, steps - 1));
            return ticks <= 0 || ticks > MaxBackoff.Ticks ? MaxBackoff : TimeSpan.FromTicks(ticks);
        }

        /// <summary>
        /// The pause a 429 asked for, as either a count of seconds or a date.
        /// Null when the response named neither, or named something unusable.
        /// </summary>
        private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers?.RetryAfter;
            if (retryAfter == null) return null;

            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero) return delta;

            if (retryAfter.Date is { } date)
            {
                var wait = date.UtcDateTime - UtcNow();
                if (wait > TimeSpan.Zero) return wait;
            }

            return null;
        }

        /// <summary>
        /// The name BakaLoader gives when it knocks, with a link to the project so
        /// whoever reads the site's logs can see exactly what this is.
        /// </summary>
        public static string UserAgent() =>
            $"BakaLoader/{AssemblyHelper.GetApplicationVersion()} (+https://github.com/RyanDMcAfee/ValheimBakaLoader)";

        /// <summary>
        /// True when an address belongs to Hexium: the domain itself, or a name
        /// under it at a label boundary. "notheximum.gg" and "hexium.gg.example.com"
        /// are both refused.
        /// </summary>
        public static bool IsHexiumHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;

            var trimmed = host.Trim().TrimEnd('.').ToLowerInvariant();
            return trimmed == RootHost || trimmed.EndsWith("." + RootHost, StringComparison.Ordinal);
        }
    }
}
