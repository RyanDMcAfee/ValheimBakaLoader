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

        /// <summary>
        /// Whether the listing marks this package deprecated, when it said either way. Null
        /// means nobody answered the question, which is not the same as a no: the BepInEx
        /// installer only holds an unattended write back on a listing that actually said yes.
        /// </summary>
        [JsonProperty("is_deprecated")]
        public bool? IsDeprecated { get; set; }

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
        /// the BepInEx installer, which checks what it fetched against it before it unpacks
        /// anything over a folder a server runs from.
        /// <para>
        /// The package page leaves this out. The community listing carries it, so the
        /// installer asks the index for the size of the very version the page named rather
        /// than fetching an archive nothing vouched for; with a size from neither, an
        /// unattended write is refused and a manual one says in the log that what came down
        /// was checked against nothing.
        /// </para>
        /// </summary>
        [JsonProperty("file_size")]
        public long? FileSize { get; set; }

        /// <summary>
        /// The archive's hash, when the listing carries one. Neither endpoint publishes it
        /// today, so this is normally null and the size above is the check that runs; a
        /// listing that starts carrying one is then checked too with no further change.
        /// </summary>
        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        /// <summary>
        /// Whether the site still serves this version, exactly as the listing spells it. The
        /// package endpoint carries it today; a version somebody PULLED reads false.
        /// <see cref="IsActive"/> is the one to read.
        /// </summary>
        [JsonProperty("is_active")]
        public bool? IsActiveRaw { get; set; }

        /// <summary>
        /// Whether this version is still one the site offers. A listing that said nothing
        /// counts as yes, the same way a listing that said nothing about deprecation does:
        /// holding every unattended write back on a field the site might rename tomorrow
        /// would stop the window working altogether. Only a listing that actually said no
        /// stops a write.
        /// </summary>
        [JsonIgnore]
        public bool IsActive => IsActiveRaw ?? true;
    }
}
