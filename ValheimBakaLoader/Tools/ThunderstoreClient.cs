using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// What a read of the package index came to: when it was read, which of the two
    /// addresses answered, how many packages came back, and whether this call went out
    /// to the site at all or reused a copy that had only just been read.
    /// </summary>
    public sealed class ThunderstoreIndexState
    {
        /// <summary>When the held index was read, or null when nothing has been read.</summary>
        public DateTime? FetchedUtc { get; init; }

        /// <summary>
        /// Which address answered: <see cref="ThunderstoreClient.ListingIndexSource"/>
        /// when the listing index and its chunks answered, and
        /// <see cref="ThunderstoreClient.V1Source"/> when the full listing stood in.
        /// Null when nothing has been read.
        /// </summary>
        public string Source { get; init; }

        /// <summary>How many packages the held index holds.</summary>
        public int PackageCount { get; init; }

        /// <summary>True when this call actually went out and read the index again.</summary>
        public bool Fetched { get; init; }

        /// <summary>
        /// True when the call was refused the trip because the index had only just been
        /// read. The answer the host sees is the one that was read moments ago, which is
        /// the point: a second press of Scan has nothing newer to find.
        /// </summary>
        public bool ReusedFresh { get; init; }
    }

    /// <summary>
    /// What asking Thunderstore about one package came to. "The site answered and has no
    /// such package" and "the site did not answer" are different things, and a host
    /// reading a row has to be told which one happened: the first is a package that has
    /// been pulled, the second is a bad minute on the internet.
    /// </summary>
    public sealed class ThunderstoreLiveLookup
    {
        public ThunderstorePackage Package { get; init; }

        /// <summary>True when Thunderstore answered at all, including with a plain no.</summary>
        public bool Answered { get; init; }

        public bool Found => Package != null;
    }

    public interface IThunderstoreClient
    {
        /// <summary>
        /// Looks up the latest published version of a Thunderstore package.
        /// Returns null when the package cannot be found (e.g. wrong namespace,
        /// not in the Valheim community) or the index is unreachable.
        /// </summary>
        Task<ThunderstorePackage> GetLatestAsync(string author, string modName);

        /// <summary>
        /// Asks Thunderstore about this one package directly, right now, rather than
        /// reading a held index. This is the answer the package's own page shows, so it
        /// is never behind. Returns null when the package is unknown or the site did not
        /// answer; never throws.
        /// </summary>
        Task<ThunderstorePackage> GetLiveAsync(string author, string modName);

        /// <summary>
        /// The same question with enough detail to tell a package that has been pulled
        /// from a site that did not answer. Never throws.
        /// </summary>
        Task<ThunderstoreLiveLookup> LookupLiveAsync(string author, string modName);

        /// <summary>
        /// Reads the index again even when the held copy is still inside its window.
        /// A second call within <see cref="ThunderstoreClient.ForceCooldown"/> is refused
        /// the trip and reuses what was just read, so pressing Scan twice does not knock
        /// on the site twice. Never throws.
        /// </summary>
        Task<ThunderstoreIndexState> RefreshAsync();

        /// <summary>When the held index was read, or null when nothing has been read yet.</summary>
        DateTime? IndexFetchedUtc { get; }

        /// <summary>Which of the two addresses the held index came from, or null.</summary>
        string IndexSource { get; }
    }

    /// <summary>
    /// Read-only client for the Thunderstore Valheim package index. Used to check
    /// whether a newer version of a locally-installed mod exists. Does not download
    /// or install anything.
    ///
    /// <para>
    /// There are two addresses that answer with the whole community, and they are not
    /// equally fresh. The full listing (<see cref="V1IndexUrl"/>) is one enormous JSON
    /// array that Thunderstore builds on a timer and serves from a cache, so it can sit
    /// hours behind what a package's own page shows: a host watching a mod get three
    /// releases in an afternoon saw none of them. The listing index
    /// (<see cref="ListingIndexUrl"/>) hands back a short list of chunk addresses, each
    /// one named after the hash of its contents, and those are rebuilt every few minutes.
    /// A chunk therefore can never be stale, because a changed chunk is a different
    /// address. This reads the listing index first and keeps the full listing as the
    /// fallback for when it or any of its chunks does not answer.
    /// </para>
    ///
    /// <para>
    /// The parsed index is held for <see cref="CacheTtl"/> and guarded by a single-flight
    /// lock so concurrent scans share one read. It is held per instance, not statically:
    /// the app registers one of these as a singleton, and a test gets a clean one.
    /// <see cref="RefreshAsync"/> is what the Scan button uses, and it walks past the
    /// window on purpose.
    /// </para>
    /// </summary>
    public class ThunderstoreClient : RestClient, IThunderstoreClient
    {
        /// <summary>
        /// The community listing index: a short document naming the chunk files that
        /// together make up the package list. Redirects to a content-addressed blob.
        /// </summary>
        public const string ListingIndexUrl = "https://thunderstore.io/c/valheim/api/v1/package-listing-index/";

        /// <summary>
        /// The full Valheim-community listing: every package in one JSON array, newest
        /// version first within each package's versions[]. The fallback, because it is a
        /// snapshot that can be hours old.
        /// </summary>
        public const string V1IndexUrl = "https://valheim.thunderstore.io/api/v1/package/";

        /// <summary>The per-package address, which always answers with the current release.</summary>
        public const string PackageUrlFormat = "https://thunderstore.io/api/experimental/package/{0}/{1}/";

        /// <summary>Thunderstore's own registrable domain. Nothing else is ever contacted.</summary>
        public const string RootHost = "thunderstore.io";

        /// <summary>The name for the fresh path, as the interface shows it and tests assert it.</summary>
        public const string ListingIndexSource = "listing-index";

        /// <summary>The name for the fallback path.</summary>
        public const string V1Source = "v1";

        private const string UserAgentName = "ValheimBakaLoader";

        /// <summary>How long a read of the index stands for an ordinary background check.</summary>
        public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

        /// <summary>
        /// The shortest gap between two deliberate refreshes. A host who presses Scan
        /// twice in a row gets one trip to the site, not two.
        /// </summary>
        public static readonly TimeSpan ForceCooldown = TimeSpan.FromSeconds(60);

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

        // --- Held index (per instance; the app holds one of these) ---
        private Dictionary<string, ThunderstorePackage> Index;
        private DateTime? FetchedUtc;
        private string Source;
        private readonly SemaphoreSlim IndexLock = new(1, 1);

        public ThunderstoreClient(IRestClientContext context) : base(context)
        {
        }

        /// <summary>Test seam: the clock the window and the cooldown read.</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>How many times this client has actually read the index. Read by tests.</summary>
        public int FetchCount { get; private set; }

        /// <summary>The most of the listing-index document itself that will be read.</summary>
        public long MaxDocumentBytes { get; set; } = 8L * 1024 * 1024;

        /// <summary>The most of one packed chunk that will be read off the wire.</summary>
        public long MaxChunkBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>The most one chunk is allowed to unpack to.</summary>
        public long MaxUnpackedBytes { get; set; } = 256L * 1024 * 1024;

        /// <summary>The most chunk addresses the listing index may name.</summary>
        public int MaxChunks { get; set; } = 256;

        public DateTime? IndexFetchedUtc => FetchedUtc;

        public string IndexSource => Source;

        public async Task<ThunderstorePackage> GetLatestAsync(string author, string modName)
        {
            if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(modName))
            {
                Logger.Information("Thunderstore lookup skipped: missing author or mod name.");
                return null;
            }

            var index = await EnsureIndexAsync();
            if (index == null)
            {
                // First read failed and nothing is held: the caller treats null as
                // "couldn't check" (no false update prompt).
                return null;
            }

            var key = $"{author}-{modName}";
            if (index.TryGetValue(key, out var package))
                return package;

            Logger.Information("Thunderstore package not in Valheim index: {0}-{1}", author, modName);
            return null;
        }

        public async Task<ThunderstorePackage> GetLiveAsync(string author, string modName) =>
            (await LookupLiveAsync(author, modName)).Package;

        public async Task<ThunderstoreLiveLookup> LookupLiveAsync(string author, string modName)
        {
            // Nothing to ask about, so nothing is asked and nobody answered.
            if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(modName))
                return new ThunderstoreLiveLookup { Answered = false };

            try
            {
                using var client = NewClient();

                var url = string.Format(PackageUrlFormat, author.Trim(), modName.Trim());
                if (!IsThunderstoreAddress(url))
                {
                    Logger.Warning("Refused a package address that is not Thunderstore's: {0}", url);
                    return new ThunderstoreLiveLookup { Answered = false };
                }

                using var response = await client.GetAsync(url);

                // A plain no is an answer, and the only one that means the package is not
                // there. Anything else is the site failing to speak, which says nothing
                // about the package at all.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return new ThunderstoreLiveLookup { Answered = true };

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Information("Thunderstore did not answer about {0}-{1} (HTTP {2}).",
                        author, modName, (int)response.StatusCode);
                    return new ThunderstoreLiveLookup { Answered = false };
                }

                var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                var latest = body["latest"];
                var version = latest?.Value<string>("version_number");
                if (string.IsNullOrWhiteSpace(version))
                {
                    // It answered, with something this cannot read. Treating that as "the
                    // package is gone" would be a guess, so it is not treated as anything.
                    Logger.Warning("Thunderstore answered about {0}-{1} with no version in it.", author, modName);
                    return new ThunderstoreLiveLookup { Answered = false };
                }

                return new ThunderstoreLiveLookup
                {
                    Answered = true,
                    Package = new ThunderstorePackage
                    {
                        Namespace = body.Value<string>("namespace") ?? author,
                        Name = body.Value<string>("name") ?? modName,
                        Latest = latest.ToObject<ThunderstorePackageVersion>(),
                    },
                };
            }
            catch (Exception e)
            {
                Logger.Warning(e, "Thunderstore live lookup for {0}-{1} did not answer.", author, modName);
                return new ThunderstoreLiveLookup { Answered = false };
            }
        }

        public async Task<ThunderstoreIndexState> RefreshAsync()
        {
            await IndexLock.WaitAsync();
            try
            {
                // Only just read: there is nothing newer to find, and asking again would
                // only be a second knock on the door.
                if (Index != null && FetchedUtc is { } read && UtcNow() - read < ForceCooldown)
                    return State(fetched: false, reusedFresh: true);

                var fresh = await FetchIndexAsync();
                return State(fetched: fresh, reusedFresh: false);
            }
            catch (Exception e)
            {
                Logger.Warning(e, "Thunderstore index refresh failed.");
                return State(fetched: false, reusedFresh: false);
            }
            finally
            {
                IndexLock.Release();
            }
        }

        private ThunderstoreIndexState State(bool fetched, bool reusedFresh) => new()
        {
            FetchedUtc = FetchedUtc,
            Source = Source,
            PackageCount = Index?.Count ?? 0,
            Fetched = fetched,
            ReusedFresh = reusedFresh,
        };

        /// <summary>
        /// The held index while it is inside its window, otherwise one read shared by
        /// every caller waiting on it. A failed read leaves whatever was held in place,
        /// so a blip does not wipe a scan's results.
        /// </summary>
        private async Task<Dictionary<string, ThunderstorePackage>> EnsureIndexAsync()
        {
            if (IsFresh()) return Index;

            await IndexLock.WaitAsync();
            try
            {
                // Somebody else may have read it while this call waited.
                if (IsFresh()) return Index;

                await FetchIndexAsync();
                return Index;
            }
            finally
            {
                IndexLock.Release();
            }
        }

        private bool IsFresh() =>
            Index != null && FetchedUtc is { } read && UtcNow() - read < CacheTtl;

        /// <summary>
        /// Reads the index: the listing index first, the full listing when that path does
        /// not answer in full. Records what was read and where it came from. Returns true
        /// when something was read; on failure the previously held index stands.
        /// </summary>
        private async Task<bool> FetchIndexAsync()
        {
            FetchCount++;

            var (fresh, source) = await FetchViaListingIndexAsync();
            if (fresh == null)
            {
                Logger.Information("Thunderstore listing index did not answer in full; reading the full listing instead.");
                fresh = await FetchViaV1Async();
                source = V1Source;
            }

            if (fresh == null) return false;

            Index = fresh;
            FetchedUtc = UtcNow();
            Source = source;
            return true;
        }

        /// <summary>
        /// Reads the listing index and every chunk it names. One chunk that does not
        /// answer fails the whole path, because half a package list would read as though
        /// mods had been delisted. Returns null when that happens.
        /// </summary>
        private async Task<(Dictionary<string, ThunderstorePackage> Index, string Source)> FetchViaListingIndexAsync()
        {
            try
            {
                using var client = NewClient();

                var document = await GetBytesAsync(client, ListingIndexUrl, MaxDocumentBytes);
                if (document == null) return (null, null);

                var chunkUrls = ReadChunkList(Unpack(document, MaxUnpackedBytes));
                if (chunkUrls == null || chunkUrls.Count == 0)
                {
                    Logger.Warning("Thunderstore listing index named no chunks.");
                    return (null, null);
                }

                if (chunkUrls.Count > MaxChunks)
                {
                    Logger.Warning("Thunderstore listing index named {0} chunks, which is more than the {1} allowed.",
                        chunkUrls.Count, MaxChunks);
                    return (null, null);
                }

                var map = new Dictionary<string, ThunderstorePackage>(StringComparer.OrdinalIgnoreCase);

                foreach (var chunkUrl in chunkUrls)
                {
                    // Every address is checked before it is fetched, so a listing index
                    // that named somewhere else could not send BakaLoader there.
                    if (!IsThunderstoreAddress(chunkUrl))
                    {
                        Logger.Warning("Refused a listing-index chunk that is not on Thunderstore: {0}", chunkUrl);
                        return (null, null);
                    }

                    var packed = await GetBytesAsync(client, chunkUrl, MaxChunkBytes);
                    if (packed == null) return (null, null);

                    if (!ReadPackagesInto(map, Unpack(packed, MaxUnpackedBytes)))
                        return (null, null);
                }

                Logger.Information("Thunderstore listing index read: {0} packages over {1} chunks.",
                    map.Count, chunkUrls.Count);
                return (map, ListingIndexSource);
            }
            catch (Exception e)
            {
                Logger.Warning(e, "Thunderstore listing index read failed.");
                return (null, null);
            }
        }

        /// <summary>
        /// Streams the full listing and builds a lookup keyed by "{owner}-{name}"
        /// (case-insensitive). Only versions[0] (the latest) is kept per package.
        /// Returns null on any transport/HTTP/parse failure.
        /// </summary>
        private async Task<Dictionary<string, ThunderstorePackage>> FetchViaV1Async()
        {
            try
            {
                using var client = NewClient();

                using var response = await client.GetAsync(V1IndexUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warning("Thunderstore full listing fetch failed: HTTP {0}", (int)response.StatusCode);
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var streamReader = new StreamReader(stream);
                using var jsonReader = new JsonTextReader(streamReader);

                var map = new Dictionary<string, ThunderstorePackage>(StringComparer.OrdinalIgnoreCase);

                // Iterate the top-level array, deserializing one package object at a time
                // so the whole 150MB+ payload is never held in memory.
                if (!ReadPackagesInto(map, jsonReader)) return null;

                Logger.Information("Thunderstore full listing loaded: {0} packages.", map.Count);
                return map;
            }
            catch (Exception e)
            {
                Logger.Warning(e, "Thunderstore full listing fetch error.");
                return null;
            }
        }

        private HttpClient NewClient()
        {
            var client = Context.HttpClientProvider.CreateClient();
            client.Timeout = RequestTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentName);
            return client;
        }

        /// <summary>
        /// The body at an address, capped, with redirects followed.
        /// <para>
        /// The control that holds is the landed-host check below: whatever the response
        /// came from, its address has to be Thunderstore's or the body is dropped unread.
        /// That is what runs in the app, where the transport follows the redirect itself
        /// and this only ever sees the 200 the blob answered with. The hand-rolled hop
        /// below is for a transport that hands the 3xx back instead, and there the address
        /// is also checked before the next request goes out. Either way nothing is sent
        /// anywhere but Thunderstore, and no credentials are sent at all.
        /// </para>
        /// </summary>
        private async Task<byte[]> GetBytesAsync(HttpClient client, string url, long cap, int maxHops = 4)
        {
            var current = url;

            for (var hop = 0; hop <= maxHops; hop++)
            {
                if (!IsThunderstoreAddress(current))
                {
                    Logger.Warning("Refused an address that is not on Thunderstore: {0}", current);
                    return null;
                }

                using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead);

                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location == null)
                    {
                        Logger.Warning("Thunderstore answered {0} with nowhere to go: {1}", status, current);
                        return null;
                    }

                    current = (location.IsAbsoluteUri ? location : new Uri(new Uri(current), location)).ToString();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warning("Thunderstore fetch failed: HTTP {0} for {1}", status, current);
                    return null;
                }

                // Where the request actually landed. This is the check that holds in the
                // app, where the transport follows the redirect itself and the body
                // arrives from the blob address without this code seeing the 3xx at all.
                var landed = response.RequestMessage?.RequestUri?.ToString();
                if (!string.IsNullOrWhiteSpace(landed) && !IsThunderstoreAddress(landed))
                {
                    Logger.Warning("A Thunderstore fetch landed somewhere else and was dropped: {0}", landed);
                    return null;
                }

                return await ReadCappedAsync(response, cap);
            }

            Logger.Warning("Thunderstore redirected more times than allowed for {0}", url);
            return null;
        }

        private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, long cap)
        {
            using var buffer = new MemoryStream();
            await using var raw = await response.Content.ReadAsStreamAsync();

            var chunk = new byte[81920];
            int read;
            while ((read = await raw.ReadAsync(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > cap)
                    throw new IOException($"A Thunderstore response passed {cap} bytes, so it was not read.");

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        /// <summary>
        /// The bytes as they are meant to be read. The listing index and its chunks come
        /// down as raw gzip with no header saying so, so the two bytes at the front are
        /// what decide rather than anything the server claimed.
        /// </summary>
        private static byte[] Unpack(byte[] raw, long cap)
        {
            if (raw == null || raw.Length < 2) return raw;
            if (raw[0] != 0x1f || raw[1] != 0x8b) return raw;

            using var source = new MemoryStream(raw);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var unpacked = new MemoryStream();

            var chunk = new byte[81920];
            int read;
            while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (unpacked.Length + read > cap)
                    throw new IOException($"A Thunderstore chunk unpacked past {cap} bytes, so it was not read.");

                unpacked.Write(chunk, 0, read);
            }

            return unpacked.ToArray();
        }

        /// <summary>The chunk addresses the listing-index document names, or null when it is not that.</summary>
        private List<string> ReadChunkList(byte[] json)
        {
            if (json == null) return null;

            try
            {
                return JsonConvert.DeserializeObject<List<string>>(
                    System.Text.Encoding.UTF8.GetString(json));
            }
            catch (Exception e)
            {
                Logger.Warning(e, "Thunderstore listing index could not be read as a list of chunks.");
                return null;
            }
        }

        private bool ReadPackagesInto(Dictionary<string, ThunderstorePackage> map, byte[] json)
        {
            if (json == null) return false;

            try
            {
                using var stream = new MemoryStream(json);
                using var streamReader = new StreamReader(stream);
                using var jsonReader = new JsonTextReader(streamReader);
                return ReadPackagesInto(map, jsonReader);
            }
            catch (Exception e)
            {
                Logger.Warning(e, "A Thunderstore chunk could not be read.");
                return false;
            }
        }

        /// <summary>
        /// Reads one array of package objects into the lookup. The chunks and the full
        /// listing carry the same shape, so both paths land here and everything
        /// downstream sees the same model whichever one answered. Merging is by exact
        /// full name; a later entry for a name already read wins, which is what makes
        /// reading chunk after chunk into one map safe.
        /// <para>
        /// Each body is judged on its own contents and nothing else. The lookup is shared
        /// across chunks, so a verdict that read it would let the first chunk vouch for
        /// every chunk after it, and a chunk that came back holding an error document or
        /// an empty list would pass as read. That is how a thirteenth of the community
        /// would go missing from a list this then published as complete, with every mod in
        /// it marked as no longer on Thunderstore. So: it has to be an array of packages,
        /// and it has to have held at least one.
        /// </para>
        /// </summary>
        private bool ReadPackagesInto(Dictionary<string, ThunderstorePackage> map, JsonTextReader jsonReader)
        {
            var serializer = new JsonSerializer();
            var packagesRead = 0;

            // A 200 is not an answer on its own. An error document is an object, and a
            // list of packages is an array, and only the second one is a package list.
            if (!jsonReader.Read() || jsonReader.TokenType != JsonToken.StartArray)
            {
                Logger.Warning("A Thunderstore package list did not begin with an array of packages.");
                return false;
            }

            while (jsonReader.Read())
            {
                // Only the objects sitting directly inside the top-level array are
                // packages; anything nested is read by the deserializer itself.
                if (jsonReader.TokenType != JsonToken.StartObject) continue;

                var pkg = serializer.Deserialize<V1Package>(jsonReader);
                if (pkg?.Versions == null || pkg.Versions.Count == 0) continue;
                if (string.IsNullOrEmpty(pkg.Owner) || string.IsNullOrEmpty(pkg.Name)) continue;

                map[$"{pkg.Owner}-{pkg.Name}"] = new ThunderstorePackage
                {
                    Namespace = pkg.Owner,
                    Name = pkg.Name,
                    Latest = pkg.Versions[0],
                };

                packagesRead++;
            }

            // What THIS body held, never what the lookup holds: the lookup already has
            // the chunks read before this one in it. Counting packages rather than objects
            // also keeps an array of something else from passing for a package list.
            if (packagesRead == 0)
                Logger.Warning("A Thunderstore package list came back with no packages in it.");

            return packagesRead > 0;
        }

        /// <summary>
        /// True when an address is one of Thunderstore's: https, and either the domain
        /// itself or a name under it at a label boundary, so "gcdn.thunderstore.io" is
        /// allowed while "thunderstore.io.example.com" and "notthunderstore.io" are not.
        /// </summary>
        public static bool IsThunderstoreAddress(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;

            return IsThunderstoreHost(uri.Host);
        }

        /// <summary>True when a host name belongs to Thunderstore at a label boundary.</summary>
        public static bool IsThunderstoreHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;

            var trimmed = host.Trim().TrimEnd('.').ToLowerInvariant();
            return trimmed == RootHost || trimmed.EndsWith("." + RootHost, StringComparison.Ordinal);
        }

        /// <summary>
        /// Minimal projection of an index entry. Both the chunks and the full listing
        /// carry this shape, and it reuses <see cref="ThunderstorePackageVersion"/> for
        /// the versions array (version_number / download_url / website_url / full_name).
        /// </summary>
        private class V1Package
        {
            [JsonProperty("full_name")]
            public string FullName { get; set; }

            [JsonProperty("owner")]
            public string Owner { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("versions")]
            public List<ThunderstorePackageVersion> Versions { get; set; }
        }
    }
}
