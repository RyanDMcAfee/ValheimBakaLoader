using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    public interface IThunderstoreClient
    {
        /// <summary>
        /// Looks up the latest published version of a Thunderstore package.
        /// Returns null when the package cannot be found (e.g. wrong namespace,
        /// not in the Valheim community) or the index is unreachable.
        /// </summary>
        Task<ThunderstorePackage> GetLatestAsync(string author, string modName);
    }

    /// <summary>
    /// Read-only client for the Thunderstore Valheim-community package index.
    /// Used to check whether a newer version of a locally-installed mod exists.
    /// Does not download or install anything.
    ///
    /// Strategy: instead of one request per installed mod against the
    /// experimental per-package endpoint (which fans out to ~67 calls, is
    /// rate-limited, and has been observed returning 500/504 server-side
    /// outages), this fetches the community-wide v1 index ONCE
    /// (<see cref="V1IndexUrl"/>, a single CDN-cached response) and resolves
    /// every mod from an in-memory dictionary. The index is stream-parsed so
    /// only each package's latest version is retained, keeping memory low
    /// despite the large payload.
    ///
    /// The parsed index is cached statically for <see cref="CacheTtl"/> and
    /// guarded by a single-flight lock so concurrent scans share one fetch.
    /// </summary>
    public class ThunderstoreClient : RestClient, IThunderstoreClient
    {
        private const string UserAgent = "ValheimBakaLoader";

        // Valheim-community v1 package index. Returns the full package list as a
        // single JSON array, newest version first within each package's versions[].
        private const string V1IndexUrl = "https://valheim.thunderstore.io/api/v1/package/";

        // --- Static index cache (survives across scans) ---
        private static Dictionary<string, ThunderstorePackage> Index;
        private static DateTime IndexFetchedUtc = DateTime.MinValue;
        private static readonly SemaphoreSlim IndexLock = new(1, 1);
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

        public ThunderstoreClient(IRestClientContext context) : base(context)
        {
        }

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
                // First fetch failed and nothing cached: caller treats null as
                // "couldn't check" (no false update prompt).
                return null;
            }

            var key = $"{author}-{modName}";
            if (index.TryGetValue(key, out var package))
                return package;

            Logger.Information("Thunderstore package not in Valheim index: {0}-{1}", author, modName);
            return null;
        }

        /// <summary>
        /// Returns the cached index when fresh, otherwise fetches a new one.
        /// Single-flight: only one fetch runs at a time; concurrent callers wait
        /// and reuse the result. On fetch failure the previously-cached index
        /// (possibly stale) is returned so a transient blip doesn't wipe results.
        /// </summary>
        private async Task<Dictionary<string, ThunderstorePackage>> EnsureIndexAsync()
        {
            if (Index != null && DateTime.UtcNow - IndexFetchedUtc < CacheTtl)
                return Index;

            await IndexLock.WaitAsync();
            try
            {
                // Double-check after acquiring the lock (another caller may have
                // refreshed the index while we were waiting).
                if (Index != null && DateTime.UtcNow - IndexFetchedUtc < CacheTtl)
                    return Index;

                var fresh = await FetchIndexAsync();
                if (fresh != null)
                {
                    Index = fresh;
                    IndexFetchedUtc = DateTime.UtcNow;
                }

                // If fetch failed, Index keeps its prior value (stale or null).
                return Index;
            }
            finally
            {
                IndexLock.Release();
            }
        }

        /// <summary>
        /// Streams the v1 index and builds a lookup keyed by "{owner}-{name}"
        /// (case-insensitive). Only versions[0] (the latest) is kept per package.
        /// Returns null on any transport/HTTP/parse failure.
        /// </summary>
        private async Task<Dictionary<string, ThunderstorePackage>> FetchIndexAsync()
        {
            try
            {
                using var client = Context.HttpClientProvider.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(120);
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                using var response = await client.GetAsync(V1IndexUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warning("Thunderstore v1 index fetch failed: HTTP {0}", (int)response.StatusCode);
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var streamReader = new StreamReader(stream);
                using var jsonReader = new JsonTextReader(streamReader);

                var serializer = new JsonSerializer();
                var map = new Dictionary<string, ThunderstorePackage>(StringComparer.OrdinalIgnoreCase);

                // Iterate the top-level array, deserializing one package object at
                // a time so the whole 150MB+ payload is never held in memory.
                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType != JsonToken.StartObject)
                        continue;

                    var pkg = serializer.Deserialize<V1Package>(jsonReader);
                    if (pkg?.Versions == null || pkg.Versions.Count == 0)
                        continue;
                    if (string.IsNullOrEmpty(pkg.Owner) || string.IsNullOrEmpty(pkg.Name))
                        continue;

                    map[$"{pkg.Owner}-{pkg.Name}"] = new ThunderstorePackage
                    {
                        Namespace = pkg.Owner,
                        Name = pkg.Name,
                        Latest = pkg.Versions[0],
                    };
                }

                Logger.Information("Thunderstore v1 index loaded: {0} packages.", map.Count);
                return map;
            }
            catch (Exception e)
            {
                Logger.Warning(e, "Thunderstore v1 index fetch error.");
                return null;
            }
        }

        /// <summary>
        /// Minimal projection of a v1 index entry. Reuses
        /// <see cref="ThunderstorePackageVersion"/> for the versions array
        /// (version_number / download_url / website_url / full_name).
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
