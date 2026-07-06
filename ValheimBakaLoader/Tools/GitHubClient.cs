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
            var request = Get($"{Resources.UrlGithubApi}/releases")
                .WithHeader("User-Agent", "ValheimBakaLoader"); // GitHub rejects UA-less requests

            var releases = await request.SendAsync<GitHubRelease[]>()
                ?? throw new Exception("GitHub did not answer the release query.");

            // "Latest" = the newest published, real (not draft/prerelease)
            // release that actually ships files.
            return releases
                .Where(r => !r.Draft && !r.Prerelease)
                .Where(r => r.Assets is { Length: > 0 })
                .OrderByDescending(r => r.PublishedAt)
                .FirstOrDefault();
        }
    }

    public class GitHubRelease
    {
        [JsonProperty("tag_name")] public string TagName { get; set; }
        [JsonProperty("published_at")] public DateTime PublishedAt { get; set; }
        [JsonProperty("draft")] public bool Draft { get; set; }
        [JsonProperty("prerelease")] public bool Prerelease { get; set; }
        [JsonProperty("assets")] public GitHubReleaseAsset[] Assets { get; set; }
    }

    public class GitHubReleaseAsset
    {
        [JsonProperty("browser_download_url")] public string BrowserDownloadUrl { get; set; }
    }
}
