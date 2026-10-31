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

        /// <summary>
        /// The manifest's dependencies exactly as it lists them ("Author-ModName-1.0.0"), so the
        /// lowest version each one asks for is not lost the way <see cref="Dependencies"/> loses
        /// it. Never null; empty when the manifest declared none or there is no manifest.
        /// </summary>
        public string[] DeclaredDependencies { get; set; } = Array.Empty<string>();

        /// <summary>
        /// What this mod needs that the realm does not give it, worked out by the scan from the
        /// manifests on disk alone (no site is asked). Never null; empty for a mod that has all
        /// it asked for and for a hand copy with no manifest.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<DependencyNeed> MissingDependencies { get; set; }
            = Array.Empty<DependencyNeed>();

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

        /// <summary>
        /// The plugin GUIDs this mod's DLLs declare in [BepInPlugin], read by the scan. BepInEx
        /// names config files after them, so they pair a config with its mod when the file name
        /// looks nothing like the package. Never null; empty when no DLL could be read.
        /// </summary>
        public string[] PluginGuids { get; set; } = Array.Empty<string>();

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
        /// Where this copy of the mod came from, when BakaLoader installed it and left a note
        /// saying so: "hexium" or "thunderstore", or null for a copy put there by hand or by
        /// a build older than the note. Only ever set from a note BakaLoader wrote and still
        /// believes (see <c>ModSourceMarkerFile</c>), never guessed. Only "hexium" changes
        /// what the row does; "thunderstore" is there so the page can say when it went in.
        /// </summary>
        public string InstalledSource { get; set; }

        /// <summary>
        /// When this copy went in, in UTC: the time on BakaLoader's own note when there is one
        /// it believes, otherwise the mod folder's creation time, otherwise null.
        /// </summary>
        public DateTime? InstalledUtc { get; set; }

        /// <summary>
        /// True when the mod is switched off: its plugin folder (and its patcher folder, when
        /// it has one) holds at least one <c>*.dll.old</c> and no <c>*.dll</c>. That is the
        /// r2modman and Gale convention, and BepInEx loads <c>*.dll</c> only.
        /// </summary>
        public bool Disabled { get; set; }

        /// <summary>
        /// "managed" when the files sit where a mod manager would put them, "nested" when the
        /// plugin folder still carries a plugins, patchers, BepInEx or config folder from a
        /// whole-zip install by an older build, null when it does not apply (a patcher-only row).
        /// </summary>
        public string Layout { get; set; }

        /// <summary>True when this copy was installed from Hexium by BakaLoader.</summary>
        public bool IsHexiumInstalled =>
            string.Equals(InstalledSource, "hexium", StringComparison.OrdinalIgnoreCase);

        /// <summary>True when this copy was installed by BakaLoader from a package zip on this PC (1.3.2).</summary>
        public bool IsLocalInstalled =>
            string.Equals(InstalledSource, "local", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when no Thunderstore update may replace this copy: it came from Hexium, or from a
        /// zip on this PC. The one test every update path asks, so a test build is never swapped
        /// for the published one behind the host's back.
        /// </summary>
        public bool HoldsOffThunderstore => IsHexiumInstalled || IsLocalInstalled;

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
        /// A copy installed from Hexium, or from a zip on this PC, always answers false,
        /// whatever Thunderstore holds (<see cref="HoldsOffThunderstore"/>). This is the one
        /// flag "Update all", the waiting-updates count and the unattended restart path all
        /// read, so a mod the host deliberately took from somewhere else is never quietly
        /// replaced with the Thunderstore build. Swapping a Hexium copy back is offered as
        /// its own action, and it asks first; a zip copy goes back by adding the mod's
        /// Thunderstore link.
        /// </para>
        /// </summary>
        public bool UpdateAvailable => !HoldsOffThunderstore && SemVer.IsNewer(LatestVersion, InstalledVersion);

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
