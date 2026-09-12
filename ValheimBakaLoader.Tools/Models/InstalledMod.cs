using System;

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

        /// <summary>
        /// When the latest Thunderstore release of this mod was published, or null when it
        /// has not been looked up or the index carried no date. Compared against the game's
        /// last update to hint whether a mod's newest release predates it.
        /// </summary>
        public DateTime? LatestReleasedUtc { get; set; }

        /// <summary>
        /// Full names ("Author-ModName") of the mods this one declares a dependency on, read
        /// from the manifest's <c>dependencies</c> array with the trailing version stripped.
        /// Never null; empty when the manifest declared none or could not be read.
        /// </summary>
        public string[] Dependencies { get; set; } = Array.Empty<string>();

        /// <summary>Optional Thunderstore dependency string ("Author-Mod-1.0.0"), if known.</summary>
        public string DependencyString { get; set; }

        /// <summary>Absolute path to the mod's folder on disk.</summary>
        public string PluginDirectory { get; set; }

        /// <summary>Optional website URL from the manifest ("website_url").</summary>
        public string Website { get; set; }

        /// <summary>
        /// Namespace of the Thunderstore package this mod was matched to, or null when the
        /// index had no entry for it: a loose DLL, a hand-built folder, or a package that is
        /// not in the Valheim community. Never derived from the folder name. It is only set
        /// from a package the index actually returned, so a row that carries it really does
        /// have a page to open.
        /// </summary>
        public string ThunderstoreNamespace { get; set; }

        /// <summary>
        /// Name of the matched Thunderstore package, or null when nothing matched. Set
        /// together with <see cref="ThunderstoreNamespace"/> and never on its own.
        /// </summary>
        public string ThunderstoreName { get; set; }

        /// <summary>
        /// True when <see cref="LatestVersion"/> is a strictly higher semver than
        /// <see cref="InstalledVersion"/>. Returns false when either side is missing
        /// or when the installed version is "unknown" (manifest-less mods).
        /// </summary>
        public bool UpdateAvailable => SemVer.IsNewer(LatestVersion, InstalledVersion);
    }
}
