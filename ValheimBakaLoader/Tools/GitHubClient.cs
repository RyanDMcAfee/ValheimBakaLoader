using Newtonsoft.Json;
using System;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tools.Http;

namespace ValheimBakaLoader.Tools
{
    public interface IGitHubClient
    {
        Task<GitHubRelease> GetLatestReleaseAsync();

        /// <summary>
        /// Every published release, newest first, with drafts and prereleases left out.
        /// The self-updater only ever wanted the top one; a language pack is keyed to an
        /// app version, and the running app is frequently not on the newest release, so
        /// "the newest release at or below the version I am running" needs the list.
        /// </summary>
        Task<GitHubRelease[]> GetReleasesAsync();

        /// <summary>
        /// One release by its tag, or null when there is no such release and when the
        /// question never got through. A pack is published on the release for the version
        /// it was cut for, so this is the first thing a pack lookup asks.
        /// </summary>
        Task<GitHubRelease> GetReleaseByTagAsync(string tag);
    }

    /// <summary>
    /// Minimal GitHub Releases reader for the self-updater. Only the fields
    /// the updater needs are modeled; see
    /// https://docs.github.com/en/rest/releases for the full schema.
    /// </summary>
    public class GitHubClient : RestClient, IGitHubClient
    {
        public GitHubClient(IRestClientContext context) : base(context)
        {
        }

        public async Task<GitHubRelease> GetLatestReleaseAsync()
        {
            var releases = await SendReleasesAsync()
                ?? throw new Exception("GitHub did not answer the release query.");

            // "Latest" = the newest published, real (not draft/prerelease)
            // release that actually ships files.
            return Published(releases).FirstOrDefault();
        }

        public async Task<GitHubRelease[]> GetReleasesAsync()
        {
            var releases = await SendReleasesAsync();

            // Unlike GetLatestReleaseAsync, a question that never got through is answered
            // with null rather than a throw: every caller of this one is a language pack
            // lookup, and those never throw at the host.
            return releases == null ? null : Published(releases).ToArray();
        }

        public async Task<GitHubRelease> GetReleaseByTagAsync(string tag)
        {
            var clean = tag?.Trim();
            if (string.IsNullOrEmpty(clean)) return null;

            // The tag goes straight into the path, so anything that could steer the request
            // somewhere else is refused here rather than escaped. Every tag this app asks
            // for is built from its own version, and a version is letters, digits and these.
            if (!clean.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+')) return null;

            var request = Get($"{Resources.UrlGithubApi}/releases/tags/{clean}")
                .WithHeader("User-Agent", "ValheimBakaLoader") // GitHub rejects UA-less requests
                // A tag nobody published answers 404, and that is the ordinary case for a
                // version whose release page is not up yet: the pack check falls back to the
                // newest release that carries packs and carries on. It used to write a red
                // failure line into the log a host reads, for a request the app expects to
                // fail, and then said nothing about the fallback at all.
                .WhenMissingSay("No release page yet for this version; using the newest pack available.");

            return await request.SendAsync<GitHubRelease>();
        }

        /// <summary>How many releases one page asks for. A hundred is GitHub's ceiling.</summary>
        internal const int ReleasePageSize = 100;

        /// <summary>
        /// How many pages the reader will walk before it stops and says so.
        /// <para>
        /// The list used to be one bare call, which GitHub answers with THIRTY releases and no
        /// hint that there are more. The pack lookup's whole contract is "the newest release at
        /// or below the version I am running", and that reaches DOWN: a rolling window of
        /// thirty would one day answer "no packs for your version" to a host whose packs are
        /// published and sitting on release thirty-one.
        /// </para>
        /// </summary>
        internal static int MaxReleasePages { get; set; } = 5;

        private async Task<GitHubRelease[]> SendReleasesAsync()
        {
            var all = new System.Collections.Generic.List<GitHubRelease>();

            for (var page = 1; page <= MaxReleasePages; page++)
            {
                var request = Get($"{Resources.UrlGithubApi}/releases?per_page={ReleasePageSize}&page={page}")
                    .WithHeader("User-Agent", "ValheimBakaLoader"); // GitHub rejects UA-less requests

                var batch = await request.SendAsync<GitHubRelease[]>();

                // The first page failing is the whole query failing, which is what the callers
                // already handle. A later page failing leaves the pages already read standing:
                // a short list is better than no list, and it is the same list the app had
                // before paging was asked for at all.
                if (batch == null) return page == 1 ? null : all.ToArray();

                all.AddRange(batch);

                // A page that came back short is the last page there is.
                if (batch.Length < ReleasePageSize) return all.ToArray();
            }

            Logger?.Warning(
                "GitHub has more releases than the first {0} pages this reader walks, so anything older "
                + "than that is not being looked at.", MaxReleasePages);

            return all.ToArray();
        }

        private static IOrderedEnumerable<GitHubRelease> Published(GitHubRelease[] releases) =>
            releases
                .Where(r => r != null && !r.Draft && !r.Prerelease)
                .Where(r => r.Assets is { Length: > 0 })
                .OrderByDescending(r => r.PublishedAt);
    }

    public class GitHubRelease
    {
        [JsonProperty("tag_name")] public string TagName { get; set; }

        /// <summary>
        /// The release's own page on github.com, which is where the notes live. GitHub
        /// returns this per release; the API base in Resources is not browsable, so this
        /// is the only honest source for a "read what changed" link.
        /// </summary>
        [JsonProperty("html_url")] public string HtmlUrl { get; set; }

        [JsonProperty("published_at")] public DateTime PublishedAt { get; set; }
        [JsonProperty("draft")] public bool Draft { get; set; }
        [JsonProperty("prerelease")] public bool Prerelease { get; set; }
        [JsonProperty("assets")] public GitHubReleaseAsset[] Assets { get; set; }

        /// <summary>
        /// The asset with this file name, or null when the release does not carry one.
        /// <para>
        /// Looking an asset up by name is the whole reason the app never has to compose a
        /// download address: the release lists what it published, and the app reads the
        /// address off the entry it found. Names are compared without regard to case
        /// because a published file name is not a case sensitive identity.
        /// </para>
        /// </summary>
        public GitHubReleaseAsset Asset(string fileName)
        {
            if (Assets == null || string.IsNullOrWhiteSpace(fileName)) return null;

            return Assets.FirstOrDefault(a =>
                a != null && string.Equals(a.Name, fileName.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public class GitHubReleaseAsset
    {
        [JsonProperty("browser_download_url")] public string BrowserDownloadUrl { get; set; }

        /// <summary>
        /// The published file name. The self-updater never needed it, because it takes the
        /// first asset whose address ends in .zip; a release that also carries four language
        /// packs has to be able to pick one file out by name.
        /// </summary>
        [JsonProperty("name")] public string Name { get; set; }

        /// <summary>
        /// The size GitHub reports, in bytes. Read only for a sanity check beside the byte
        /// count the manifest carries, which is the one the download is held to.
        /// </summary>
        [JsonProperty("size")] public long Size { get; set; }

        /// <summary>
        /// The digest GitHub publishes for the uploaded file, written as "sha256:" and
        /// sixty-four hex characters. Older releases carry nothing here, so it is read
        /// beside the size rather than instead of it: the size catches a body that arrived
        /// short, and the digest is the only thing that catches one that arrived at the
        /// right length with the wrong bytes in it.
        /// </summary>
        [JsonProperty("digest")] public string Digest { get; set; }
    }
}
