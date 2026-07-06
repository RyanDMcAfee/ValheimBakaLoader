namespace ValheimBakaLoader.Tools.Models
{
    /// <summary>
    /// Represents a locally-installed BepInEx/Thunderstore mod parsed from a
    /// Thunderstore-style <c>manifest.json</c>. The author is derived from the
    /// parent folder name (which follows the <c>Author-ModName</c> convention)
    /// because <c>manifest.json</c> does not reliably carry the author.
    /// </summary>
    public class InstalledMod
    {
        /// <summary>The mod author / Thunderstore namespace (from the folder name).</summary>
        public string Author { get; set; }

        /// <summary>The mod name (from the folder name).</summary>
        public string ModName { get; set; }

        /// <summary>The Thunderstore-style full name, "Author-ModName".</summary>
        public string FullName => $"{Author}-{ModName}";

        /// <summary>Installed semantic version, taken from manifest "version_number".</summary>
        public string InstalledVersion { get; set; }

        /// <summary>Latest available version from Thunderstore, or null if not yet checked / unknown.</summary>
        public string LatestVersion { get; set; }

        /// <summary>Optional Thunderstore dependency string ("Author-Mod-1.0.0"), if known.</summary>
        public string DependencyString { get; set; }

        /// <summary>Absolute path to the mod's folder on disk.</summary>
        public string PluginDirectory { get; set; }

        /// <summary>Optional website URL from the manifest ("website_url").</summary>
        public string Website { get; set; }

        /// <summary>
        /// True when <see cref="LatestVersion"/> is a strictly higher semver than
        /// <see cref="InstalledVersion"/>. Returns false when either side is missing
        /// or when the installed version is "unknown" (manifest-less mods).
        /// </summary>
        public bool UpdateAvailable => SemVer.IsNewer(LatestVersion, InstalledVersion);
    }
}
