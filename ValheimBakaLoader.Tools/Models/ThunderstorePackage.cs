using System;
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

        /// <summary>
        /// When this version was published to Thunderstore, from the API's
        /// <c>date_created</c> field. Null when the response carried no date.
        /// Used to tell whether a mod's newest release predates the last game update.
        /// </summary>
        [JsonProperty("date_created")]
        public DateTime? DateCreated { get; set; }

        /// <summary>
        /// How many bytes the published archive weighs, when the listing carries it. Read by
        /// the BepInEx installer, which checks what it fetched against what the site said
        /// before it unpacks anything over a folder a server runs from. Null when the
        /// response did not name a size, which is not a failure: the check is simply skipped.
        /// </summary>
        [JsonProperty("file_size")]
        public long? FileSize { get; set; }

        /// <summary>
        /// The archive's hash, when the listing carries one. Thunderstore does not publish it
        /// today, so this is normally null and the size is the only check there is; a listing
        /// that starts carrying one is then checked with no further change.
        /// </summary>
        [JsonProperty("sha256")]
        public string Sha256 { get; set; }
    }
}
