using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ValheimBakaLoader.Tools.Models
{
    /// <summary>
    /// One package from the Hexium Valheim v1 index
    /// (<c>https://valheim.hexium.gg/api/v1/package/</c>). The JSON shape is
    /// field for field the same as the Thunderstore v1 index, so the same reading
    /// works for both, but the two sites are separate places with separate accounts
    /// and BakaLoader never treats a name on one as the same person on the other.
    /// <para>
    /// Two things about this index are not true of Thunderstore's and are handled
    /// here rather than at the call site: the versions array is NOT sorted, and the
    /// dates are import stamps rather than publication dates, so nothing reads them.
    /// </para>
    /// </summary>
    public class HexiumPackage
    {
        [JsonProperty("owner")]
        public string Owner { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("full_name")]
        public string FullName { get; set; }

        [JsonProperty("package_url")]
        public string PackageUrl { get; set; }

        [JsonProperty("is_deprecated")]
        public bool IsDeprecated { get; set; }

        [JsonProperty("has_nsfw_content")]
        public bool HasNsfwContent { get; set; }

        [JsonProperty("categories")]
        public List<string> Categories { get; set; }

        [JsonProperty("versions")]
        public List<HexiumPackageVersion> Versions { get; set; }

        /// <summary>
        /// The versions still on offer, in the order the index happened to list
        /// them. Never null, and never a version the site has withdrawn.
        /// </summary>
        [JsonIgnore]
        public IEnumerable<HexiumPackageVersion> ActiveVersions =>
            (Versions ?? new List<HexiumPackageVersion>())
                .Where(v => v != null && v.IsActive && !string.IsNullOrWhiteSpace(v.VersionNumber));

        /// <summary>
        /// The highest full release on offer, worked out by comparison rather than
        /// read off the top of the list, because the list is not sorted. Null when
        /// the package has only pre-releases.
        /// </summary>
        [JsonIgnore]
        public HexiumPackageVersion LatestStable =>
            Highest(ActiveVersions.Where(v => !SemVer.IsPreRelease(v.VersionNumber)));

        /// <summary>The highest version on offer, pre-releases included.</summary>
        [JsonIgnore]
        public HexiumPackageVersion LatestAny => Highest(ActiveVersions);

        /// <summary>The named version, or null when the site does not offer it.</summary>
        public HexiumPackageVersion Find(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;

            return ActiveVersions.FirstOrDefault(v =>
                string.Equals(v.VersionNumber?.Trim(), version.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The version worth telling the host about, given what they have installed.
        /// Full releases always count. Pre-releases only count for somebody already
        /// running a pre-release, and then only ones on the same release line or a
        /// later one, so a stable install is never nudged onto a beta.
        /// </summary>
        public HexiumPackageVersion LatestFor(string installedVersion)
        {
            if (!SemVer.IsPreRelease(installedVersion)) return LatestStable;

            var candidates = ActiveVersions.Where(v =>
                !SemVer.IsPreRelease(v.VersionNumber)
                || SemVer.CompareCore(v.VersionNumber, installedVersion) >= 0);

            return Highest(candidates);
        }

        private static HexiumPackageVersion Highest(IEnumerable<HexiumPackageVersion> versions)
        {
            HexiumPackageVersion best = null;
            foreach (var version in versions)
            {
                if (best == null || SemVer.Compare(version.VersionNumber, best.VersionNumber) > 0)
                    best = version;
            }

            return best;
        }
    }

    /// <summary>
    /// One published build inside a <see cref="HexiumPackage"/>. The download
    /// address is opaque (<c>https://cdn.hexium.gg/upload/{id}/{version}.zip</c>)
    /// and is always used exactly as the index gave it, never rebuilt from parts.
    /// </summary>
    public class HexiumPackageVersion
    {
        [JsonProperty("version_number")]
        public string VersionNumber { get; set; }

        [JsonProperty("full_name")]
        public string FullName { get; set; }

        [JsonProperty("download_url")]
        public string DownloadUrl { get; set; }

        [JsonProperty("website_url")]
        public string WebsiteUrl { get; set; }

        [JsonProperty("dependencies")]
        public List<string> Dependencies { get; set; }

        [JsonProperty("file_size")]
        public long? FileSize { get; set; }

        /// <summary>
        /// The site's own "still on offer" flag. Absent means yes: a row that never
        /// carried the key is a live row, not a withdrawn one.
        /// </summary>
        [JsonProperty("is_active")]
        public bool? IsActiveRaw { get; set; }

        [JsonIgnore]
        public bool IsActive => IsActiveRaw ?? true;
    }
}
