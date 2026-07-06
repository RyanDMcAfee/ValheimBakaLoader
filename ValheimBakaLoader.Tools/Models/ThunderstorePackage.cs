using Newtonsoft.Json;

namespace ValheimBakaLoader.Tools.Models
{
    /// <summary>
    /// Trimmed view of the Thunderstore experimental "package" endpoint response:
    /// <c>https://thunderstore.io/api/experimental/package/{namespace}/{name}/</c>
    /// Only the fields needed for version-checking are mapped.
    /// </summary>
    public class ThunderstorePackage
    {
        [JsonProperty("namespace")]
        public string Namespace { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("latest")]
        public ThunderstorePackageVersion Latest { get; set; }

        /// <summary>Convenience accessor for the latest version number, if present.</summary>
        [JsonIgnore]
        public string LatestVersion => Latest?.VersionNumber;

        /// <summary>Convenience accessor for the latest download URL, if present.</summary>
        [JsonIgnore]
        public string DownloadUrl => Latest?.DownloadUrl;
    }

    /// <summary>
    /// The "latest" object nested inside a Thunderstore package response.
    /// </summary>
    public class ThunderstorePackageVersion
    {
        [JsonProperty("version_number")]
        public string VersionNumber { get; set; }

        [JsonProperty("download_url")]
        public string DownloadUrl { get; set; }

        [JsonProperty("website_url")]
        public string WebsiteUrl { get; set; }

        [JsonProperty("full_name")]
        public string FullName { get; set; }
    }
}
