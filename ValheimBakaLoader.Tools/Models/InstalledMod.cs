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

        /// <summary>
        /// Absolute path to the mod's patcher folder (<c>BepInEx/patchers/{Author-ModName}</c>)
        /// when it ships one, or null when it has no patcher part. A mod may carry a plugins
        /// folder, a patcher folder, or both; patcher folders load their DLL early in BepInEx
        /// startup, so a removal has to take this folder as well or the mod keeps loading.
        /// </summary>
        public string PatcherDirectory { get; set; }

        /// <summary>
        /// True when this mod exists ONLY as a patcher (it has a <see cref="PatcherDirectory"/>
        /// but no plugins folder). Patcher-only mods are removable, but BakaLoader's install and
        /// update path is plugins-oriented, so the UI does not offer to update them.
        /// </summary>
        public bool IsPatcher { get; set; }

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
        /// True when a package list that was actually read had no entry for this mod.
        /// <para>
        /// It says "the site answered and this was not in it", which is not the same as
        /// "nobody could be asked": it is only ever set from an index that came back, so a
        /// site that is down leaves it false and the row says nothing. An author who pulls
        /// a package, or a package that has been taken down, is what turns it true, and the
        /// next scan that finds the package again turns it off. Nothing acts on it: no
        /// file is removed, nothing is rolled back, the row simply says so on hover.
        /// </para>
        /// </summary>
        public bool NotListedOnThunderstore { get; set; }

        /// <summary>
        /// Where this copy of the mod came from, when BakaLoader installed it from
        /// somewhere other than Thunderstore and left a note saying so: "hexium", or
        /// null for everything else. Only ever set from a note BakaLoader wrote and
        /// still believes (see <c>ModSourceMarkerFile</c>), never guessed.
        /// </summary>
        public string InstalledSource { get; set; }

        /// <summary>True when this copy was installed from Hexium by BakaLoader.</summary>
        public bool IsHexiumInstalled =>
            string.Equals(InstalledSource, "hexium", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The highest version Hexium offers for this exact package, or null when the
        /// host has not turned that source on, Hexium has no such package, or the
        /// lookup did not answer.
        /// </summary>
        public string HexiumLatestVersion { get; set; }

        /// <summary>Owner as Hexium spells it, set only from a package Hexium answered with.</summary>
        public string HexiumOwner { get; set; }

        /// <summary>Package name as Hexium spells it, set with <see cref="HexiumOwner"/> and never alone.</summary>
        public string HexiumName { get; set; }

        /// <summary>
        /// True when <see cref="LatestVersion"/> is a strictly higher semver than
        /// <see cref="InstalledVersion"/>. Returns false when either side is blank, and
        /// false when the latest version cannot be read at all.
        /// <para>
        /// An installed version of "unknown", which is what a manifest-less folder gets,
        /// does NOT answer false: a version nobody can read sorts lowest, so any readable
        /// Thunderstore release ranks above it and the row offers the update. That is
        /// deliberate, and the sentence that used to stand here claimed the opposite.
        /// </para>
        /// <para>
        /// A copy installed from Hexium always answers false, whatever Thunderstore
        /// holds. This is the one flag "Update all", the waiting-updates count and the
        /// unattended restart path all read, so a mod the host deliberately took from
        /// the other site is never quietly replaced with the Thunderstore build.
        /// Swapping back is offered as its own action, and it asks first.
        /// </para>
        /// </summary>
        public bool UpdateAvailable => !IsHexiumInstalled && SemVer.IsNewer(LatestVersion, InstalledVersion);

        /// <summary>
        /// True when Hexium has a version worth telling the host about: higher than
        /// what is installed, and higher than Thunderstore's newest as well, so the
        /// mark only ever appears when the other site really is ahead. A package
        /// Thunderstore does not carry at all satisfies the second half.
        /// </summary>
        public bool HexiumNewer =>
            !string.IsNullOrWhiteSpace(HexiumLatestVersion)
            && SemVer.IsNewer(HexiumLatestVersion, InstalledVersion)
            && (string.IsNullOrWhiteSpace(LatestVersion) || SemVer.IsNewer(HexiumLatestVersion, LatestVersion));

        /// <summary>
        /// True for a Hexium-installed copy that Thunderstore has since moved past.
        /// The row says so and offers the swap; nothing acts on it by itself.
        /// </summary>
        public bool ThunderstoreNewer =>
            IsHexiumInstalled && SemVer.IsNewer(LatestVersion, InstalledVersion);
    }
}
