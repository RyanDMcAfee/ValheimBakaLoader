namespace ValheimBakaLoader.Tools.Models
{
    /// <summary>
    /// One mod another mod says it needs and this realm cannot give it as things stand: not
    /// there at all, there but older than the version asked for, or there and switched off.
    /// A dependency that is there, on and new enough never becomes one of these.
    /// </summary>
    public sealed class DependencyNeed
    {
        /// <summary>"Owner-Name", the way Thunderstore and the plugin folder both spell it.</summary>
        public string FullName { get; init; }

        /// <summary>The lowest version asked for, or null when the entry named none.</summary>
        public string MinVersion { get; init; }

        /// <summary>"missing", "older" or "off" (see ModDependencies).</summary>
        public string State { get; init; }

        /// <summary>The version on disk for an older or switched-off one; null for a missing one.</summary>
        public string InstalledVersion { get; init; }

        /// <summary>Which mod asked for it first, as "Owner-Name". A dependency of a dependency names the one in between.</summary>
        public string RequiredBy { get; init; }

        /// <summary>The package name alone, which is what the page puts in front of a host.</summary>
        public string Name
        {
            get
            {
                var full = FullName ?? "";
                var hyphen = full.IndexOf('-');
                return hyphen > 0 && hyphen < full.Length - 1 ? full.Substring(hyphen + 1) : full;
            }
        }
    }
}
