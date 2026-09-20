using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>One file BakaLoader wrote into the install, and what it held when it did.</summary>
    public class BepInExMarkerFileEntry
    {
        /// <summary>Path relative to the install root, written with forward slashes.</summary>
        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }
    }

    /// <summary>
    /// The note BakaLoader leaves at the root of an install whose BepInEx it put there.
    /// <para>
    /// It exists because a correct BepInEx install leaves nothing behind that names the pack
    /// it came from. The pack's own <c>manifest.json</c> is a top level entry in the archive
    /// and every reference installer deliberately refuses to copy it, so after a by the book
    /// install the only version on disk is the framework's own assembly version, which the
    /// pack does not follow: pack 5.4.2350 ships BepInEx 5.4.23.5, and the pack moves on its
    /// own whenever the community changes something around it. Without this file there is no
    /// answer to "is there a newer one" except a guess.
    /// </para>
    /// </summary>
    public class BepInExMarker
    {
        /// <summary>The shape this build writes, and the only one it believes.</summary>
        [JsonProperty("schema")]
        public int Schema { get; set; }

        /// <summary>Who wrote the note, for example "BakaLoader 1.2.0".</summary>
        [JsonProperty("writer")]
        public string Writer { get; set; }

        /// <summary>The package, as Thunderstore names it: "denikson-BepInExPack_Valheim".</summary>
        [JsonProperty("package")]
        public string Package { get; set; }

        /// <summary>The pack version, which is not the BepInEx version.</summary>
        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("installedUtc")]
        public DateTime InstalledUtc { get; set; }

        /// <summary>The address the archive was fetched from.</summary>
        [JsonProperty("source")]
        public string Source { get; set; }

        /// <summary>Every file this install wrote, with what it held when it was written.</summary>
        [JsonProperty("files")]
        public List<BepInExMarkerFileEntry> Files { get; set; } = new();
    }

    /// <summary>Reads and writes the install note at the root of a server install.</summary>
    public static class BepInExMarkerFile
    {
        public const string FileName = ".bakaloader-bepinex.json";

        /// <summary>
        /// What a note is renamed to when the files under it stopped matching what it recorded.
        /// It is kept rather than deleted: it is the only record of which pack was last put
        /// here, and a host asking what happened to their install deserves to be able to read it.
        /// </summary>
        public const string StaleFileName = FileName + ".stale";

        /// <summary>The shape this build writes, and the only one it believes.</summary>
        public const int CurrentSchema = 1;

        /// <summary>Every writer name starts with this, and a note that does not is refused.</summary>
        public const string WriterPrefix = "BakaLoader";

        /// <summary>The note at an install root, or null when there is none or it will not read.</summary>
        public static BepInExMarker Read(string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot)) return null;

            try
            {
                var path = System.IO.Path.Combine(installRoot, FileName);
                if (!File.Exists(path)) return null;

                var marker = JsonConvert.DeserializeObject<BepInExMarker>(File.ReadAllText(path));
                return IsTrusted(marker) ? marker : null;
            }
            catch
            {
                // An unreadable note is no note.
                return null;
            }
        }

        /// <summary>
        /// True when a note may be believed: BakaLoader wrote it, this build knows the shape,
        /// and it names a package and a version. A file somebody dropped in by hand cannot
        /// talk BakaLoader into believing an install it never made.
        /// </summary>
        public static bool IsTrusted(BepInExMarker marker)
        {
            if (marker == null) return false;
            if (marker.Schema != CurrentSchema) return false;
            if (string.IsNullOrWhiteSpace(marker.Writer)) return false;
            if (!marker.Writer.TrimStart().StartsWith(WriterPrefix, StringComparison.Ordinal)) return false;
            if (string.IsNullOrWhiteSpace(marker.Package)) return false;
            return !string.IsNullOrWhiteSpace(marker.Version);
        }

        /// <summary>Writes the note at an install root, replacing any note already there.</summary>
        public static void Write(string installRoot, BepInExMarker marker)
        {
            if (string.IsNullOrWhiteSpace(installRoot) || marker == null) return;

            Directory.CreateDirectory(installRoot);
            var path = System.IO.Path.Combine(installRoot, FileName);
            File.WriteAllText(path, JsonConvert.SerializeObject(marker, Formatting.Indented));
        }

        /// <summary>
        /// Puts the note aside under <see cref="StaleFileName"/> and leaves the install without
        /// one, which is how BakaLoader gives up ownership of a folder somebody else has
        /// written to. Best effort: a note that will not move is still a note that no longer
        /// describes the files, and every reader checks the files.
        /// </summary>
        public static void SetAside(string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot)) return;

            try
            {
                var path = System.IO.Path.Combine(installRoot, FileName);
                if (!File.Exists(path)) return;

                var aside = System.IO.Path.Combine(installRoot, StaleFileName);
                File.Move(path, aside, overwrite: true);
            }
            catch
            {
                // Nothing above here depends on the move having worked.
            }
        }
    }

    /// <summary>One server profile and the install it runs from, as the guard reads them.</summary>
    public sealed class BepInExProfileInstall
    {
        public string ProfileName { get; init; }

        public string ServerExePath { get; init; }

        /// <summary>True when that profile's server is up right now.</summary>
        public bool Running { get; init; }
    }

    /// <summary>
    /// What a caller wants from a write beyond the files themselves: whether anybody is
    /// watching, and which of the refusals they have already answered.
    /// <para>
    /// It is one object rather than a run of boolean parameters because every one of these is
    /// a decision the host made, and a decision the host made belongs somewhere it can be read
    /// back at the call site rather than counted off against a signature.
    /// </para>
    /// </summary>
    public sealed class BepInExWriteOptions
    {
        /// <summary>
        /// True for a write nobody asked for at this moment: the restart window and the
        /// loader-before-start step. An unattended write never answers a question on the
        /// host's behalf; where a manual write would ask, this one records why it stopped.
        /// </summary>
        public bool Unattended { get; init; }

        /// <summary>
        /// True when the host has been shown both versions and said yes to going backwards.
        /// </summary>
        public bool AllowDowngrade { get; init; }

        /// <summary>
        /// True when the host has been shown that the core here is not one BakaLoader knows
        /// and said to write over it anyway.
        /// </summary>
        public bool OverUnrecognised { get; init; }

        /// <summary>
        /// True when the host has been shown that the BepInEx here is not one BakaLoader put
        /// in, and said to write over it anyway. Any install with no trusted note counts,
        /// which includes one BakaLoader has given up ownership of.
        /// </summary>
        public bool OverOutside { get; init; }

        /// <summary>
        /// True when the host has been shown that another mod manager drives this install, and
        /// said to write over it anyway.
        /// </summary>
        public bool OverDrivenElsewhere { get; init; }

        /// <summary>
        /// True when the host has been shown that the core here is not a BepInEx 5, and said
        /// to write the pack over it anyway.
        /// </summary>
        public bool OverForeign { get; init; }

        /// <summary>
        /// True when the host has been told that the pack this install was written from is no
        /// longer one Thunderstore serves, and said to take the pack it does serve instead.
        /// <para>
        /// Without it a write over an install with files missing is a REPAIR, and a repair
        /// deliberately fetches the exact version the note names rather than the newest one.
        /// Once that version is taken down there is nothing at that address any more, so every
        /// repair from then on fetches the same 404 and the install stays broken: the only way
        /// out is a pack that IS served, and moving a host onto a newer loader is a decision
        /// that belongs to them. This is that decision, carried on the call that follows it.
        /// </para>
        /// </summary>
        public bool TakeCurrentPack { get; init; }

        /// <summary>A write somebody pressed a button for, with nothing yet answered.</summary>
        public static BepInExWriteOptions Manual { get; } = new();

        /// <summary>
        /// A write inside a restart window or before a start. Every override is false and
        /// cannot be anything else: each one stands for a question the host answered, and
        /// there is nobody at the keyboard on this path to have answered one.
        /// </summary>
        public static BepInExWriteOptions Window { get; } = new() { Unattended = true };

        /// <summary>
        /// True when this call carries an answer to a question the host was meant to be asked.
        /// An unattended write that carried one would be a window answering on their behalf.
        /// </summary>
        public bool CarriesAnAnswer =>
            AllowDowngrade || OverUnrecognised || OverOutside || OverDrivenElsewhere || OverForeign
            || TakeCurrentPack;
    }

    /// <summary>What BakaLoader can say about the BepInEx in one base install.</summary>
    public sealed class BepInExStatus
    {
        /// <summary>The base install root the answer is about.</summary>
        public string BaseFolder { get; init; }

        /// <summary>
        /// True when this install would actually load mods: BepInEx/core/BepInEx.dll is there
        /// AND winhttp.dll is beside the server. Both halves are needed and neither is enough.
        /// The loose file is the whole mechanism on Windows: without it the game never looks
        /// at the core at all, and an antivirus quarantining it is the ordinary way an install
        /// that looks complete stops loading anything.
        /// </summary>
        public bool Installed { get; init; }

        /// <summary>True when winhttp.dll is beside the server executable.</summary>
        public bool LoaderFilePresent { get; init; }

        /// <summary>
        /// True when BepInEx/core/BepInEx.dll is on disk, whether or not anything can read a
        /// version out of it and whether or not the loader file beside the server is still
        /// there. This is the plain "there is a loader here" fact, and it is the one the
        /// outside clamp asks: a core whose assembly carries no readable version made
        /// <see cref="CoreVersion"/> null, and a write used to go over it with no question.
        /// </summary>
        public bool CoreFilePresent { get; init; }

        /// <summary>True when BakaLoader's own install note is at the root.</summary>
        public bool MaintainedByBakaLoader { get; init; }

        /// <summary>The Thunderstore pack version, from the note. Null without one.</summary>
        public string PackVersion { get; init; }

        /// <summary>
        /// The file version of BepInEx.dll, read when there is no note. Unchanged: the row has
        /// always used this field to mean "the only version there is, because nobody recorded
        /// a pack". <see cref="CoreVersion"/> is the one to read when the question is what the
        /// assembly on disk actually says.
        /// </summary>
        public string CoreFileVersion { get; init; }

        /// <summary>
        /// The file version of BepInEx.dll whenever there is one to read, note or no note.
        /// </summary>
        public string CoreVersion { get; init; }

        /// <summary>The package the note names, for example "denikson-BepInExPack_Valheim".</summary>
        public string Package { get; init; }

        /// <summary>Where the note says the files came from.</summary>
        public string Source { get; init; }

        /// <summary>When the note says they were written.</summary>
        public DateTime? InstalledUtc { get; init; }

        /// <summary>
        /// A denikson-BepInExPack_Valheim folder sitting under BepInEx/plugins, which is a
        /// mis-targeted install doing nothing. Null when there is none.
        /// </summary>
        public string WrongLocationFolder { get; init; }

        /// <summary>
        /// What the host's doorstop_config.ini names as the assembly to load, exactly as the
        /// file spells it. Null when there is no such file or no such setting.
        /// </summary>
        public string DoorstopTarget { get; init; }

        /// <summary>
        /// True when that target is not the BepInEx beside this server, which is what an
        /// install another mod manager drives looks like.
        /// </summary>
        public bool DrivenElsewhere { get; init; }

        /// <summary>
        /// True when the BepInEx.dll here is not a 5.x one, so the pack BakaLoader installs is
        /// not the framework this install runs.
        /// </summary>
        public bool ForeignCore { get; init; }

        /// <summary>
        /// True when BepInEx/core is there and BepInEx.dll is not: an install that will load
        /// nothing, whoever made it.
        /// </summary>
        public bool Damaged { get; init; }

        /// <summary>
        /// True when that damaged core holds files BakaLoader did not put there. Nothing is
        /// installed or cleared automatically over one of these.
        /// </summary>
        public bool Unrecognised { get; init; }

        /// <summary>
        /// Files the note lists that are not on disk any more, by the path the note names
        /// them. Empty when the install is whole or when there is no note.
        /// </summary>
        public IReadOnlyList<string> MissingFiles { get; init; } = Array.Empty<string>();

        /// <summary>
        /// True when the note that WAS here no longer described the files, so BakaLoader gave
        /// up ownership of this install on this read. The note is kept beside it as
        /// <see cref="BepInExMarkerFile.StaleFileName"/>.
        /// </summary>
        public bool Drifted { get; init; }

        /// <summary>The stamp of the newest backup that could be put back, or null.</summary>
        public string NewestBackup { get; init; }

        /// <summary>
        /// The stamp of the backup holding the loader the host had before BakaLoader, or null
        /// when this install was never adopted. It is never pruned, so it is still there long
        /// after the three ordinary ones have rolled over, and it is the only one that can
        /// answer "put my own BepInEx back".
        /// </summary>
        public string AdoptionBackup { get; init; }

        /// <summary>The core version that backup holds, when its mark recorded one.</summary>
        public string AdoptionBackupCoreVersion { get; init; }

        /// <summary>True when a write mark was left behind by a write that did not finish.</summary>
        public bool InterruptedWrite { get; init; }

        /// <summary>
        /// True when BepInEx/core here is a link to another folder, which is what an isolated
        /// profile's core is. A write belongs on the install the link points at.
        /// </summary>
        public bool CoreIsJunction { get; init; }

        /// <summary>Every profile that runs from this base install, isolated ones included.</summary>
        public IReadOnlyList<string> SharingProfiles { get; init; } = Array.Empty<string>();

        /// <summary>Those of them whose server is up right now.</summary>
        public IReadOnlyList<string> RunningProfiles { get; init; } = Array.Empty<string>();
    }

    /// <summary>One step of an install, as the progress event carries it.</summary>
    public sealed class BepInExProgress
    {
        /// <summary>resolving | downloading | checking | extracting | installing | linking | done</summary>
        public string Phase { get; init; }

        /// <summary>Zero to one hundred.</summary>
        public int Percent { get; init; }

        /// <summary>The pack version this is about, once it is known.</summary>
        public string Version { get; init; }
    }

    /// <summary>Why a write that could have happened did not.</summary>
    public static class BepInExSkipReason
    {
        /// <summary>The install here is newer than the pack the site offers.</summary>
        public const string Newer = "newer";

        /// <summary>Another mod manager's doorstop config drives this install.</summary>
        public const string DrivenElsewhere = "drivenElsewhere";

        /// <summary>The core here is not the framework this pack carries.</summary>
        public const string Foreign = "foreign";

        /// <summary>There is a core here BakaLoader does not recognise.</summary>
        public const string Unrecognised = "unrecognised";

        /// <summary>Somebody else has written over the files the note recorded.</summary>
        public const string Drift = "drift";

        /// <summary>The pack the site offers is a pre-release, which nobody asked to try.</summary>
        public const string PreRelease = "prerelease";

        /// <summary>The listing marks the package deprecated.</summary>
        public const string Deprecated = "deprecated";

        /// <summary>
        /// The listing says the site no longer serves this version: somebody pulled it.
        /// </summary>
        public const string Pulled = "pulled";

        /// <summary>
        /// The pack has not been listed long enough for a write nobody is watching.
        /// <see cref="BepInExInstallResult.EligibleUtc"/> carries when it will have been.
        /// </summary>
        public const string Soak = "soak";

        /// <summary>
        /// Neither Thunderstore endpoint said what the archive weighs, so there is nothing to
        /// check the download against and no window unpacks it.
        /// </summary>
        public const string Unverified = "unverified";

        /// <summary>
        /// The pack a repair fetched does not hold, file for file, what the install note
        /// recorded when BakaLoader wrote that version here, so there was nothing safe to put
        /// back.
        /// </summary>
        public const string RepairMismatch = "repairMismatch";
    }

    /// <summary>What an install came to.</summary>
    public sealed class BepInExInstallResult
    {
        public bool Installed { get; init; }

        /// <summary>True when there was a BepInEx here already and this replaced it.</summary>
        public bool Replaced { get; init; }

        public string Version { get; init; }

        public string Package { get; init; }

        public string Source { get; init; }

        /// <summary>
        /// What was here before, as a host would name it: the pack version out of the note
        /// when there was a note, and the core assembly's own file version when there was not.
        /// <para>
        /// The fallback is the whole point. An install BakaLoader did not make has no pack
        /// version anywhere on disk, so on the commonest write of all (the first adoption)
        /// this field was null and every sentence built from it said the loader had been
        /// replaced by nothing. The two numbers are not the same kind of number, and
        /// <see cref="PreviousPackVersion"/> and <see cref="PreviousCoreVersion"/> are there
        /// for anything that has to tell them apart.
        /// </para>
        /// </summary>
        public string PreviousVersion { get; init; }

        /// <summary>The pack version the note named before the write, or null when there was no note.</summary>
        public string PreviousPackVersion { get; init; }

        /// <summary>How many already-provisioned isolated installs were given the sharing they lacked.</summary>
        public int IsolatedInstallsLinked { get; init; }

        /// <summary>
        /// How many loose loader files were refreshed in isolated profiles that hold COPIES of
        /// them rather than hard links. A profile on another volume follows the base's core
        /// through a junction but keeps its own winhttp.dll, so without this pass it runs a
        /// new core under an old loader file after every write.
        /// </summary>
        public int ProfileLoaderFilesRefreshed { get; init; }

        /// <summary>
        /// When the pack this write was offered becomes old enough for an unattended write,
        /// in UTC. Set only on a <see cref="BepInExSkipReason.Soak"/> skip, so the row can say
        /// when the wait ends rather than only that there is one.
        /// </summary>
        public DateTime? EligibleUtc { get; init; }

        /// <summary>
        /// True when this install was somebody else's before and now carries BakaLoader's note.
        /// </summary>
        public bool Adopted { get; init; }

        /// <summary>
        /// True when the pack matched the disk byte for byte, so only the note was written. The
        /// commonest case of all, and the one with no file risk in it at all.
        /// </summary>
        public bool NothingChanged { get; init; }

        /// <summary>
        /// True when the note already named the current pack and the files still matched it, so
        /// nothing was fetched, backed up or rewritten.
        /// </summary>
        public bool AlreadyCurrent { get; init; }

        /// <summary>True when the write was not done and <see cref="SkipReason"/> says why.</summary>
        public bool Skipped { get; init; }

        /// <summary>One of <see cref="BepInExSkipReason"/>, or null.</summary>
        public string SkipReason { get; init; }

        /// <summary>The stamp of the backup this write put the replaced files into, or null.</summary>
        public string BackupStamp { get; init; }

        /// <summary>True when this was a restore rather than a fresh set of files.</summary>
        public bool Restored { get; init; }

        /// <summary>True when a write that did not finish was put back before this answer.</summary>
        public bool Healed { get; init; }

        /// <summary>
        /// True when the host's doorstop_config.ini could not drive the loader that went in, so
        /// the pack's own copy was written and the host's went into the backup.
        /// </summary>
        public bool DoorstopReplaced { get; init; }

        /// <summary>The file version of the BepInEx.dll that was here before the write.</summary>
        public string PreviousCoreVersion { get; init; }

        /// <summary>The file version of the BepInEx.dll that is here now.</summary>
        public string CoreVersion { get; init; }
    }

    public interface IBepInExService
    {
        /// <summary>
        /// What BakaLoader can say about the BepInEx in one base install. Reads only, with one
        /// exception it is worth knowing about: an install whose note no longer describes its
        /// files has that note set aside here, because ownership is a claim and a claim that
        /// has stopped being true has to stop being made.
        /// </summary>
        /// <param name="baseExePath">The base install's server .exe. Hoisted if it is not one.</param>
        /// <param name="pluginsDir">The profile's plugins folder, for the wrong-location check.</param>
        /// <param name="profiles">Every known profile and the install it runs from.</param>
        BepInExStatus Status(string baseExePath, string pluginsDir = null,
            IEnumerable<BepInExProfileInstall> profiles = null);

        /// <summary>
        /// Puts BepInEx into the base install, from the pinned pack unless a link is given.
        /// Refuses while any server sharing that base is up.
        /// <para>
        /// <paramref name="profiles"/> is ENUMERATED TWICE, once before the download and once
        /// with the archive on disk and nothing written yet. Hand in a sequence that answers
        /// freshly each time (the bridge hands in an iterator over the live session registry)
        /// and a server started during the fetch is seen; hand in a list and the second ask is
        /// the first one's answer again.
        /// </para>
        /// </summary>
        Task<BepInExInstallResult> InstallAsync(string baseExePath, string url,
            IEnumerable<BepInExProfileInstall> profiles,
            IProgress<BepInExProgress> progress = null,
            CancellationToken cancellationToken = default,
            BepInExWriteOptions options = null);

        /// <summary>
        /// The same write, for the package the install note names rather than the default, and
        /// with the same twice-asked <paramref name="profiles"/>.
        /// </summary>
        Task<BepInExInstallResult> UpdateAsync(string baseExePath,
            IEnumerable<BepInExProfileInstall> profiles,
            IProgress<BepInExProgress> progress = null,
            CancellationToken cancellationToken = default,
            BepInExWriteOptions options = null);

        /// <summary>
        /// Puts a backup back: the newest one, or the one a stamp names. Same refusals, same
        /// pre-flight and the same swap as a write, because putting an old core back is a write
        /// like any other.
        /// </summary>
        Task<BepInExInstallResult> RestoreAsync(string baseExePath,
            IEnumerable<BepInExProfileInstall> profiles, string stamp = null,
            IProgress<BepInExProgress> progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Finishes a write that did not: puts the backup the write mark names back and takes
        /// the mark away. Answers null when there was nothing to finish.
        /// </summary>
        BepInExInstallResult HealInterruptedWrite(string baseExePath,
            IEnumerable<BepInExProfileInstall> profiles);

        /// <summary>
        /// The newest pack version the site offers, or null when it did not answer. Never throws.
        /// </summary>
        Task<string> LatestVersionAsync(string package = null);

        /// <summary>
        /// Removes a denikson-BepInExPack_Valheim folder that was installed into the plugins
        /// folder by mistake. Touches nothing else.
        /// </summary>
        Task<bool> RemoveWrongLocationAsync(string pluginsDir,
            IEnumerable<BepInExProfileInstall> profiles, string baseExePath);
    }

    /// <summary>
    /// Installs and keeps BepInEx, the framework every mod loads under.
    /// <para>
    /// Three facts shape all of this. First, BepInEx is ONE thing per base install and not one
    /// per profile: an isolated install shares <c>BepInEx/core</c> and <c>BepInEx/patchers</c>
    /// with the base through directory junctions and hard-links the loose loader files beside
    /// the executable, so writing BepInEx anywhere writes it everywhere. Second, the pack
    /// archive is two tier, a metadata layer over a single folder whose CONTENTS are what go
    /// into the install root, so a normal mod install would bury the whole framework three
    /// levels under the plugins folder where it does nothing. Third, nothing a correct install
    /// leaves on disk records the pack version, which is why every install here writes its own
    /// note.
    /// </para>
    /// <para>
    /// What it writes is an ALLOW list and never a deny list: four loose files at the root,
    /// the whole of <c>BepInEx/core</c>, and <c>BepInEx/config/BepInEx.cfg</c> only when there
    /// is not one already. A pack that grows a new top level file writes nothing new, which is
    /// the opposite of the rule the isolation copier uses and is deliberate: that one is
    /// provisioning a copy of an install the host already has, this one is unpacking an
    /// archive off the internet into the folder the server runs from.
    /// </para>
    /// <para>
    /// The other half of the job is that most hosts already HAVE BepInEx, put there by hand or
    /// by another manager, so adopting one is the ordinary path and not the exception. That is
    /// why nothing here deletes before it has proved it can write, why the core is swapped
    /// rather than cleared and refilled, why the host's own <c>doorstop_config.ini</c> is kept,
    /// and why an install that already matches the pack gets a note and not a single changed
    /// byte.
    /// </para>
    /// </summary>
    public class BepInExService : IBepInExService
    {
        /// <summary>The package BakaLoader installs unless a host names another.</summary>
        public const string DefaultPackageOwner = "denikson";

        /// <summary>The package BakaLoader installs unless a host names another.</summary>
        public const string DefaultPackageName = "BepInExPack_Valheim";

        /// <summary>The same package, as Thunderstore writes it in one piece.</summary>
        public const string DefaultPackage = DefaultPackageOwner + "-" + DefaultPackageName;

        /// <summary>
        /// The loose files from the pack's own folder that go to the install root, and the
        /// only ones. doorstop_libs and the two shell scripts are Linux and macOS only, and
        /// the archive's own metadata belongs to Thunderstore, not to the install.
        /// </summary>
        public static readonly string[] RootFileAllowList =
        {
            "winhttp.dll",
            "doorstop_config.ini",
            ".doorstop_version",
            "changelog.txt",
        };

        /// <summary>The loose file the game itself loads, and the whole mechanism on Windows.</summary>
        public const string LoaderFileName = "winhttp.dll";

        /// <summary>The one thing that proves an archive is a loader and not a mod.</summary>
        public const string CoreAssemblyName = "BepInEx.dll";

        /// <summary>The config the pack ships, written only when the install has none.</summary>
        public const string ShippedConfigName = "BepInEx.cfg";

        /// <summary>Where a replaced core is kept, under BepInEx so no scan ever reads it.</summary>
        public const string BackupDirName = ".bakaloader-bepinex-backups";

        /// <summary>
        /// The major version of the BepInEx this pack is. A core whose own assembly does not
        /// say 5 is a different framework, and writing a 5.x pack over it would leave an
        /// install running neither.
        /// </summary>
        public const int SupportedCoreMajor = 5;

        /// <summary>
        /// How many backups are kept. Each one holds a full copy of the files a write
        /// replaced, and with maintenance on the update runs unattended, so without a number
        /// here the folder grows by a couple of megabytes per pack bump forever in a place no
        /// scan reads and nobody looks. Three is enough to step back from an update that went
        /// wrong and past the one before it; older than that and the pack it came from is long
        /// gone from Thunderstore's current listing anyway.
        /// </summary>
        public const int KeptCoreBackups = 3;

        /// <summary>
        /// The most an archive is allowed to weigh. The real pack is under a megabyte, and
        /// this is what stops a link that answers with a hundred gigabytes.
        /// </summary>
        public long MaxDownloadBytes { get; set; } = 50L * 1024 * 1024;

        /// <summary>How many redirects a download may follow before it gives up.</summary>
        public int MaxDownloadRedirects { get; set; } = 5;

        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

        private readonly IThunderstoreClient Thunderstore;
        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IInstallIsolationService Isolation;
        private readonly IApplicationLogger Logger;

        public BepInExService(
            IThunderstoreClient thunderstore,
            IHttpClientProvider httpClientProvider,
            IInstallIsolationService isolation,
            IApplicationLogger logger)
        {
            Thunderstore = thunderstore;
            HttpClientProvider = httpClientProvider;
            Isolation = isolation;
            Logger = logger;
        }

        // ------------------------------------------------------------------ pure rules

        /// <summary>
        /// The folder this server runs from, which is where its loader files sit. Pure and
        /// string only, so it can be asked about a path that is not on this machine.
        /// <para>
        /// Note what this is NOT: it does not hoist an isolated install back to its base. A
        /// path alone cannot say which of an instances root's siblings the base is, because
        /// the isolation service roots every instance at
        /// <c>&lt;parent of the base folder&gt;/.bakaloader-instances</c> and records nothing
        /// in the name. The caller resolves the base (the bridge does it from the profile's
        /// own preferences) and hands it in; this only ever says where a given exe lives.
        /// </para>
        /// </summary>
        public static string InstallRootOf(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return null;

            try
            {
                var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(exePath));
                return string.IsNullOrWhiteSpace(dir) ? null : dir;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Every instances root this install's BepInEx could be shared through, most specific
        /// first. An isolated install sits directly inside exactly one and answers with that
        /// one; a base install answers with the root beside it AND the shared fallback root
        /// under LocalApplicationData, because those are the two places
        /// <c>InstallIsolationService.GetInstancesRoot</c> provisions into and it picks the
        /// second whenever the first cannot be written.
        /// <para>
        /// That second answer is the whole point. A server under Program Files without
        /// administrator rights has no writable folder beside it, so its instances land in the
        /// shared fallback root; a rule that compared only the root beside the base then said
        /// those instances shared nothing with it, while the provisioning code went on
        /// junctioning their <c>BepInEx/core</c> straight at that very base. The refusal and
        /// the linking have to read the same map, and this is it.
        /// </para>
        /// <para>
        /// This is exactly as precise as the isolation model is, and no more. Two separate base
        /// installs side by side under one parent folder answer with the same first key,
        /// because the isolation service would put both of their instances in the same root and
        /// cannot tell them apart there either. The cost of that is a refusal that names one
        /// server too many, which is the safe direction to be wrong in.
        /// </para>
        /// </summary>
        public static IReadOnlyList<string> InstallGroupKeys(string exePath)
        {
            var dir = InstallRootOf(exePath);
            if (string.IsNullOrWhiteSpace(dir)) return Array.Empty<string>();

            try
            {
                var parent = Directory.GetParent(dir);
                if (parent == null) return new[] { Trimmed(dir) };

                // Already inside an instances root: that root IS the key, and the only one. A
                // path cannot say which of that root's siblings the base is, and inside the
                // shared fallback root the base may not be a sibling at all.
                if (string.Equals(parent.Name, InstallIsolationService.InstancesRootName,
                        StringComparison.OrdinalIgnoreCase))
                    return new[] { Trimmed(parent.FullName) };

                return new[]
                {
                    Trimmed(System.IO.Path.Combine(parent.FullName, InstallIsolationService.InstancesRootName)),
                    Trimmed(InstallIsolationService.FallbackInstancesRoot),
                };
            }
            catch
            {
                return new[] { Trimmed(dir) };
            }
        }

        /// <summary>
        /// The first of <see cref="InstallGroupKeys"/>: the instances root an install sits in,
        /// or the one a base install would provision into beside itself.
        /// </summary>
        public static string InstallGroupKey(string exePath)
        {
            var keys = InstallGroupKeys(exePath);
            return keys.Count == 0 ? null : keys[0];
        }

        private static string Trimmed(string path) =>
            path?.TrimEnd(System.IO.Path.DirectorySeparatorChar);

        /// <summary>
        /// True when an install sits directly inside the shared LocalApplicationData fallback
        /// instances root, which is the one root whose members cannot be traced to a base by
        /// their path.
        /// </summary>
        private static bool LivesInFallbackRoot(IReadOnlyList<string> keys) =>
            keys.Count == 1 && InstallIsolationService.IsFallbackRoot(keys[0]);

        /// <summary>True when two installs write through the same BepInEx.</summary>
        public static bool SharesBase(string baseExePath, string otherExePath)
        {
            var a = InstallGroupKeys(baseExePath);
            var b = InstallGroupKeys(otherExePath);
            if (a.Count == 0 || b.Count == 0) return false;

            if (string.Equals(a[0], b[0], StringComparison.OrdinalIgnoreCase)) return true;

            // The shared fallback root holds instances of EVERY base whose own parent was not
            // writable, and nothing in a path says which base one of them came from. So an
            // install sitting in it counts as a member of every base install, which
            // over-reports and never under-reports: the refusal names one server too many
            // rather than letting BakaLoader clear and rewrite a BepInEx/core it junctioned
            // under a live world. Two base installs are NOT joined by this: neither of them
            // lives in that root, so their own first keys still decide.
            var aIsBase = a.Count > 1;
            var bIsBase = b.Count > 1;

            return (aIsBase && LivesInFallbackRoot(b)) || (bIsBase && LivesInFallbackRoot(a));
        }

        /// <summary>
        /// The names of the running servers that share this base install, in the order they
        /// were given. Empty means the write is safe.
        /// <para>
        /// This is the whole of the refusal, and it is a rule rather than an inline check
        /// because it is asked in three places that must agree: the manual install, the manual
        /// update, and the unattended window that applies one without anybody watching.
        /// </para>
        /// </summary>
        public static IReadOnlyList<string> ProfilesBlockingWrite(
            string baseExePath, IEnumerable<BepInExProfileInstall> profiles)
        {
            if (profiles == null) return Array.Empty<string>();

            return profiles
                .Where(p => p != null && p.Running && SharesBase(baseExePath, p.ServerExePath))
                .Select(p => string.IsNullOrWhiteSpace(p.ProfileName) ? "?" : p.ProfileName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>True when nothing sharing this base install is up.</summary>
        public static bool IsSafeToWrite(string baseExePath, IEnumerable<BepInExProfileInstall> profiles)
            => ProfilesBlockingWrite(baseExePath, profiles).Count == 0;

        /// <summary>
        /// The servers that are UP but that BakaLoader did not start, named by the folder they
        /// run from.
        /// <para>
        /// <see cref="ProfilesBlockingWrite"/> only ever knows the profiles this app carries. A
        /// host who launched the server from the Steam library, from a shortcut, or from a
        /// scheduled task on boot has a live process holding the very files a write replaces,
        /// and nothing in the profile list says so. The pre-flight would stop the write anyway,
        /// but "BepInEx.Preloader.dll is held open" is a worse sentence than the one that names
        /// the server, so the process list is asked first.
        /// </para>
        /// <para>
        /// A folder name rather than a profile name, because a process has no profile: for an
        /// isolated install it IS the profile name, and for a base install it is the install
        /// folder, which is what a host recognises.
        /// </para>
        /// </summary>
        public static IReadOnlyList<string> ForeignServersBlockingWrite(
            string baseExePath, IEnumerable<string> runningImagePaths)
        {
            if (runningImagePaths == null) return Array.Empty<string>();

            return runningImagePaths
                .Where(path => !string.IsNullOrWhiteSpace(path) && SharesBase(baseExePath, path))
                .Select(path => System.IO.Path.GetFileName(InstallRootOf(path) ?? string.Empty))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The single top level folder an archive must have, or null when its shape is not the
        /// pack's. A loader archive is exactly one folder holding <c>winhttp.dll</c> and
        /// <c>BepInEx/core/BepInEx.dll</c>; anything else is a mod, a repackaged something, or
        /// a link that went somewhere else entirely, and none of those may be unpacked over
        /// the folder the server runs from.
        /// </summary>
        public static string LoaderRootOf(string extractedRoot)
        {
            if (string.IsNullOrWhiteSpace(extractedRoot) || !Directory.Exists(extractedRoot)) return null;

            var folders = Directory.GetDirectories(extractedRoot);
            if (folders.Length != 1) return null;

            var root = folders[0];
            if (!File.Exists(System.IO.Path.Combine(root, LoaderFileName))) return null;
            if (!File.Exists(System.IO.Path.Combine(root, "BepInEx", "core", CoreAssemblyName))) return null;

            return root;
        }

        /// <summary>The address of one named build, built by BakaLoader rather than accepted.</summary>
        public static string ConstructedDownloadUrl(string owner, string name, string version) =>
            $"https://thunderstore.io/package/download/{owner}/{name}/{version}/";

        /// <summary>
        /// True when an assembly's own file version says this is not a BepInEx 5. A version
        /// nobody can read is not evidence of anything and never answers true.
        /// </summary>
        public static bool IsForeignCoreVersion(string fileVersion)
        {
            if (string.IsNullOrWhiteSpace(fileVersion)) return false;

            var digits = new string(fileVersion.Trim().TrimStart('v', 'V').TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) return false;

            return !int.TryParse(digits, out var major) || major != SupportedCoreMajor;
        }

        // ------------------------------------------------------------------ status

        public BepInExStatus Status(string baseExePath, string pluginsDir = null,
            IEnumerable<BepInExProfileInstall> profiles = null)
        {
            var baseDir = InstallRootOf(baseExePath);
            var list = (profiles ?? Enumerable.Empty<BepInExProfileInstall>())
                .Where(p => p != null && SharesBase(baseExePath, p.ServerExePath))
                .ToList();

            if (string.IsNullOrWhiteSpace(baseDir))
            {
                return new BepInExStatus
                {
                    BaseFolder = null,
                    Installed = false,
                    WrongLocationFolder = WrongLocationFolderIn(pluginsDir),
                };
            }

            var coreDir = System.IO.Path.Combine(baseDir, "BepInEx", "core");
            var coreDll = System.IO.Path.Combine(coreDir, CoreAssemblyName);
            var loaderFile = System.IO.Path.Combine(baseDir, LoaderFileName);

            var coreDirThere = Directory.Exists(coreDir);
            var coreDllThere = File.Exists(coreDll);
            var loaderThere = File.Exists(loaderFile);
            var coreHoldsFiles = coreDirThere && HoldsSomething(coreDir);

            var marker = BepInExMarkerFile.Read(baseDir);

            // A write in flight, or one that stopped and has not been put back yet. Either way
            // the loader files are mid-move, and the note cannot be read back against them:
            // half of a swap looks exactly like somebody else having written here, and
            // ownership would be dropped over BakaLoader's own work. Dropping it is a one-way
            // door (the stale note keeps the install Outside on every later read), so the
            // comparison simply does not run while the mark is there.
            var writeInFlight = BepInExWriteSentinel.Present(baseDir);

            // The files the note recorded, read back. A file that CHANGED means another tool
            // owns this install now; a file that went MISSING is an install to repair. They are
            // told apart here because the answers are opposites.
            var check = writeInFlight ? new BepInExFileCheck() : BepInExIntegrity.Against(baseDir, marker);
            if (marker != null && check.Drifted)
            {
                Logger?.Information(
                    "The BepInEx note at {0} no longer describes the files ({1}), so BakaLoader has stopped "
                    + "counting this install as one it looks after.", baseDir, string.Join(", ", check.Changed));
                BepInExMarkerFile.SetAside(baseDir);
                marker = null;
            }

            // Ownership stays given up. The note that was set aside is the durable record of
            // that, so an install that has one and no live note reads as drifted on every
            // later read as well; without this the row would say so once and never again, and
            // the very next unattended window would adopt the install straight back off the
            // other tool. A successful write takes the stale note away again.
            var drifted = marker == null && File.Exists(
                System.IO.Path.Combine(baseDir, BepInExMarkerFile.StaleFileName));

            var coreVersion = coreDllThere ? FileVersionOf(coreDll) : null;
            var doorstopTarget = BepInExDoorstop.TargetOf(baseDir);

            return new BepInExStatus
            {
                BaseFolder = baseDir,
                Installed = coreDllThere && loaderThere,
                LoaderFilePresent = loaderThere,
                CoreFilePresent = coreDllThere,
                MaintainedByBakaLoader = marker != null,
                PackVersion = marker?.Version,
                Package = marker?.Package,
                Source = marker?.Source,
                InstalledUtc = marker?.InstalledUtc,
                CoreFileVersion = marker == null && coreDllThere ? coreVersion : null,
                CoreVersion = coreVersion,
                WrongLocationFolder = WrongLocationFolderIn(pluginsDir),
                DoorstopTarget = doorstopTarget,
                DrivenElsewhere = BepInExDoorstop.DrivenElsewhere(baseDir, doorstopTarget),
                ForeignCore = coreDllThere && IsForeignCoreVersion(coreVersion),
                Damaged = coreDirThere && !coreDllThere,
                Unrecognised = coreDirThere && !coreDllThere && coreHoldsFiles && marker == null,
                MissingFiles = drifted ? Array.Empty<string>() : check.Missing,
                Drifted = drifted,
                NewestBackup = BepInExBackups.Newest(baseDir)?.Stamp,
                AdoptionBackup = BepInExBackups.Adoption(baseDir)?.Stamp,
                AdoptionBackupCoreVersion = BepInExBackups.Adoption(baseDir)?.CoreVersion,
                InterruptedWrite = BepInExWriteSentinel.StaleStamp(baseDir) != null,
                CoreIsJunction = IsReparsePoint(coreDir),
                SharingProfiles = list
                    .Select(p => p.ProfileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                RunningProfiles = list
                    .Where(p => p.Running)
                    .Select(p => p.ProfileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        /// <summary>
        /// The pack folder somebody unpacked into the plugins folder by mistake, or null.
        /// </summary>
        public static string WrongLocationFolderIn(string pluginsDir)
        {
            if (string.IsNullOrWhiteSpace(pluginsDir)) return null;

            try
            {
                var wrong = System.IO.Path.Combine(pluginsDir, DefaultPackage);
                return Directory.Exists(wrong) ? wrong : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The file version of an assembly, or null when it will not read.</summary>
        public static string FileVersionOf(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var version = info.FileVersion;
                return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
            }
            catch
            {
                return null;
            }
        }

        public async Task<string> LatestVersionAsync(string package = null)
        {
            var (owner, name) = SplitPackage(package);
            try
            {
                var live = await Thunderstore.LookupLiveAsync(owner, name);
                if (live?.Package?.LatestVersion is { } version && !string.IsNullOrWhiteSpace(version))
                    return version;

                var indexed = await Thunderstore.GetLatestAsync(owner, name);
                return indexed?.LatestVersion;
            }
            catch (Exception e)
            {
                Logger.Debug("Could not read the BepInEx pack version: {0}", e.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ install

        public Task<BepInExInstallResult> InstallAsync(string baseExePath, string url,
            IEnumerable<BepInExProfileInstall> profiles,
            IProgress<BepInExProgress> progress = null,
            CancellationToken cancellationToken = default,
            BepInExWriteOptions options = null)
            => InstallCoreAsync(baseExePath, url, null, profiles, progress, options, cancellationToken);

        public Task<BepInExInstallResult> UpdateAsync(string baseExePath,
            IEnumerable<BepInExProfileInstall> profiles,
            IProgress<BepInExProgress> progress = null,
            CancellationToken cancellationToken = default,
            BepInExWriteOptions options = null)
        {
            var baseDir = InstallRootOf(baseExePath);
            var package = BepInExMarkerFile.Read(baseDir)?.Package;
            return InstallCoreAsync(baseExePath, null, package, profiles, progress, options, cancellationToken);
        }

        private async Task<BepInExInstallResult> InstallCoreAsync(
            string baseExePath, string url, string package,
            IEnumerable<BepInExProfileInstall> profiles,
            IProgress<BepInExProgress> progress,
            BepInExWriteOptions options,
            CancellationToken cancellationToken)
        {
            options ??= BepInExWriteOptions.Manual;

            // A window has nobody at the keyboard, so it cannot be carrying an answer to a
            // question somebody was asked. Every refusal below asks the unattended question
            // first, so this changes nothing about what happens; it is here so the object a
            // caller handed in cannot go on claiming otherwise further down.
            if (options.Unattended && options.CarriesAnAnswer) options = BepInExWriteOptions.Window;

            var baseDir = InstallRootOf(baseExePath);
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
                throw new HostFacingException("bepinex.noServerPath",
                    "Set a valid server .exe path before installing BepInEx.");

            // Asked before a byte is fetched. Writing BepInEx means writing through the
            // junctions and hard links every other install on this base shares, and a server
            // that is up has those files mapped.
            RefuseWhileServersAreUp(baseExePath, profiles);

            var status = Status(baseExePath, null, profiles);

            // Four ways an install says "this is not yours to write to". Every one of them is
            // refused on BOTH paths: the window records why it stopped, and the button is
            // refused HERE rather than at the page.
            //
            // That second half is the point. A dialog the page shows is a manner, not a clamp:
            // the RPC is there to be called, and a page that forgets to ask, or a call that
            // never went through the page at all, would write over somebody else's install on
            // one request. So each refusal has a flag beside it that the page only sends after
            // the confirm naming that exact consequence, and an unattended write can never
            // carry one.
            if (status.DrivenElsewhere)
            {
                if (options.Unattended) return Skipped(status, BepInExSkipReason.DrivenElsewhere);

                if (!options.OverDrivenElsewhere)
                    throw new HostFacingException("bepinex.drivenElsewhere",
                        "Another mod manager drives the BepInEx beside this server, so nothing was "
                        + "written over it.",
                        ("target", status.DoorstopTarget));
            }

            if (status.ForeignCore)
            {
                if (options.Unattended) return Skipped(status, BepInExSkipReason.Foreign);

                if (!options.OverForeign)
                    throw new HostFacingException("bepinex.foreignCore",
                        $"The BepInEx here ({status.CoreVersion}) is not the framework this pack carries, "
                        + "so nothing was written over it.",
                        ("installed", status.CoreVersion));
            }

            // An install BakaLoader gave up ownership of is never taken back without somebody
            // pressing something. Two tools each adopting what the other wrote is the loop this
            // stops. On the manual path it is caught by the outside clamp below, because a
            // drifted install is one with no trusted note.
            if (options.Unattended && status.Drifted) return Skipped(status, BepInExSkipReason.Drift);

            // A core BakaLoader does not recognise is never cleared on a guess.
            if (status.Unrecognised && !options.OverUnrecognised)
            {
                if (options.Unattended) return Skipped(status, BepInExSkipReason.Unrecognised);

                throw new HostFacingException("bepinex.unrecognisedCore",
                    "There is a BepInEx core beside this server that BakaLoader does not recognise, "
                    + "so nothing was written over it.");
            }

            // And the one that covers the rest: a loader that is HERE and that BakaLoader did
            // not put here. The window adopts one of those at the next restart because the host
            // answered the question that offered exactly that; a button press has answered
            // nothing until the confirm says what is on disk and what would replace it.
            // Nothing installed at all is not this: there is no install to write over.
            // The core FILE, not a version read out of it. A core whose BepInEx.dll carries no
            // readable version resource answers null to CoreVersion, and asking that question
            // let a manual install write straight over somebody else's loader with no confirm
            // at all. Whether the file is there is the fact; whether anything can read a number
            // out of it is a detail of the file.
            var somethingIsHere = status.Installed || status.Damaged || status.CoreFilePresent;
            if (somethingIsHere && !status.MaintainedByBakaLoader
                && !options.Unattended && !options.OverOutside)
                throw new HostFacingException("bepinex.outsideUnconfirmed",
                    "BakaLoader did not put the BepInEx that is beside this server, so nothing was "
                    + "written over it without being asked first.",
                    ("installed", status.CoreVersion));

            if (status.CoreIsJunction)
                throw new HostFacingException("bepinex.coreIsJunction",
                    "This install's BepInEx core is a link to another folder, so the write belongs on the "
                    + "install it points at.");

            var (owner, name) = SplitPackage(package);
            Report(progress, "resolving", 2, null);

            // A REPAIR rather than an update: BakaLoader's own install, with files the note
            // lists gone from disk. It is a different job and it answers two questions
            // differently. The files that went were written out of the pack the note names, so
            // that is the pack they come back from: fetching "latest" would move a host to a
            // newer loader on the strength of an antivirus taking one file, without anybody
            // choosing it. And the rules that hold a restart back from a NEW pack have nothing
            // to say here, because this pack is not new to this install: it is the one already
            // running on it. Leaving the soak in front of a repair was what made the antivirus
            // case, the commonest breakage there is, wait three days to be put right.
            //
            // An archive the host pasted a link to is not one of these. They named that file
            // themselves, and the version the note holds is not the one they asked for, so the
            // rules below have nothing to say about it and neither has the note.
            //
            // Neither is a write the host asked to take the CURRENT pack. A repair's whole point
            // is the pack the note names, and when Thunderstore has stopped serving that pack
            // there is no putting it back: every repair fetches the same 404 and the row offers
            // the same repair again. TakeCurrentPack is the host saying they would rather have
            // the pack the site does serve, which is an ordinary update, so this write stops
            // being a repair and goes down the update path from here.
            var repairing = string.IsNullOrWhiteSpace(url)
                && !options.TakeCurrentPack
                && status.MaintainedByBakaLoader
                && status.MissingFiles.Count > 0
                && !status.Drifted
                && !string.IsNullOrWhiteSpace(status.PackVersion);

            string version = null;
            long? declaredSize = null;
            string declaredSha = null;
            string address;

            // What the site says is current, kept beside the version actually being fetched. On
            // a repair the two part company (the note's pack is fetched, not the newest one),
            // and if that fetch fails this is the pack the host would be offered instead.
            string siteVersion = null;

            if (string.IsNullOrWhiteSpace(url))
            {
                var listed = await ResolveListingAsync(owner, name);
                version = listed.Version;
                siteVersion = listed.Version;
                declaredSize = listed.FileSize;
                declaredSha = listed.Sha256;
                address = ConstructedDownloadUrl(owner, name, version);

                // Already on this pack, with the files still holding what the note recorded.
                // There is nothing to fetch, nothing to back up and nothing to rewrite, and
                // saying so is a better answer than doing all three to arrive at the same bytes.
                if (status.MaintainedByBakaLoader
                    && string.Equals(status.PackVersion, version, StringComparison.OrdinalIgnoreCase)
                    && status.Installed
                    && status.MissingFiles.Count == 0)
                {
                    return new BepInExInstallResult
                    {
                        Installed = true,
                        Replaced = false,
                        AlreadyCurrent = true,
                        NothingChanged = true,
                        Version = version,
                        Package = owner + "-" + name,
                        Source = status.Source,
                        PreviousVersion = status.PackVersion,
                        PreviousPackVersion = status.PackVersion,
                        PreviousCoreVersion = status.CoreVersion,
                        CoreVersion = status.CoreVersion,
                    };
                }

                // The repair fetches the version the note names rather than the newest one. The
                // size and the digest travel with a particular archive, so they are only kept
                // when the listing happens to be describing that same version; where it is not
                // they are dropped here, and the archive is held to the NOTE's own digests
                // instead once it is unpacked. That check is below, it is not optional, and it
                // is what a repair leans on rather than a number off a listing that has moved on.
                if (repairing && !string.Equals(version, status.PackVersion, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Information(
                        "BepInEx here is missing {0} file(s) from pack {1}, so pack {1} is what goes back "
                        + "rather than the {2} the site now offers.",
                        status.MissingFiles.Count, status.PackVersion, version);

                    version = status.PackVersion;
                    declaredSize = null;
                    declaredSha = null;
                    address = ConstructedDownloadUrl(owner, name, version);
                }

                // Asked AFTER the already-current answer and BEFORE a byte is fetched: a host
                // who is already on the current pack is told so whatever the listing says
                // about it, and a pack a window may not write is refused without a download.
                // A repair is not asked at all: every one of these rules is about whether a
                // pack is fit to go ONTO this install, and this one is already on it.
                var refusal = repairing ? null : BepInExPackPolicy.RefusalFor(
                    options.Unattended, version, listed.Deprecated,
                    listed.DateCreated, listed.FileSize, DateTime.UtcNow, listed.Pulled);

                if (refusal != null)
                {
                    Logger.Information(
                        "The BepInEx pack the site offers ({0}) was left for the host to decide on ({1}), "
                        + "so the install was left as it was.", version, refusal);

                    return Skipped(status, refusal, version,
                        refusal == BepInExSkipReason.Soak
                            ? BepInExPackPolicy.EligibleAt(listed.DateCreated)
                            : null);
                }

                // A manual write with nothing to check against still goes ahead, because the
                // host asked for it, but the log says what was and was not verified rather
                // than leaving "checking" on the progress bar standing for something that did
                // not happen. A repair is no longer one of those: the note beside the install
                // holds a digest for the loader files BakaLoader could read as it wrote them
                // out of this very version, and the archive is held to every one of those
                // digests before anything moves.
                if (declaredSize == null && string.IsNullOrWhiteSpace(declaredSha))
                {
                    if (repairing)
                        Logger.Information(
                            "Nothing the site says about pack {0} names a size, so what comes down is "
                            + "checked against the digests the note recorded for it instead.", version);
                    else
                        Logger.Warning(
                            "Neither Thunderstore answer said what pack {0} weighs, so what came down was not "
                            + "checked against anything before it was unpacked.", version);
                }
                else if (listed.SizeFromIndex)
                    Logger.Debug(
                        "The package page gave no size for pack {0}, so the community index's was used.",
                        version);
            }
            else
            {
                address = url.Trim();
                if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed)
                    || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
                    throw new HostFacingException("bepinex.notALoader",
                        "That link is not an address BakaLoader can fetch a loader from.");
            }

            Report(progress, "downloading", 10, version);

            var tempZip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bakaloader-bepinex-{Guid.NewGuid():N}.zip");
            var tempExtract = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bakaloader-bepinex-{Guid.NewGuid():N}");

            try
            {
                long written;
                try
                {
                    written = await DownloadAsync(new Uri(address), tempZip, cancellationToken);
                }
                catch (HostFacingException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    // A REPAIR that could not fetch its own pack is not the same failure as a
                    // bad minute on the internet, and it must not be filed as one. The repair
                    // asks for the exact version the install note names, and Thunderstore only
                    // serves the versions it still lists: once that pack is taken down, every
                    // restart from here on fetches a 404 and the install stays broken. The
                    // ordinary offline reason is deliberately swallowed by the unattended
                    // recorder, so this had no way of ever reaching the host. It has one now,
                    // and it carries both versions so the row can offer the pack that IS served.
                    if (repairing)
                        throw new HostFacingException("bepinex.repairPackGone",
                            $"BepInEx {version} is the pack this install was written from, and it could not be "
                            + "fetched, so nothing was put back.",
                            ("noted", version), ("offered", siteVersion), ("detail", e.Message));

                    throw new HostFacingException("bepinex.offline",
                        "BepInEx could not be fetched: " + e.Message, ("detail", e.Message));
                }

                Report(progress, "checking", 45, version);

                // What the SITE said about this archive, when it said anything. A repair very
                // often arrives here with neither number: the listing has moved on to a newer
                // pack and nothing on it describes the one the note names. That is no longer a
                // hole, because the same archive is held to the note's own digests below, but
                // a size that IS known is still worth comparing and is still compared.
                if (declaredSize.HasValue && declaredSize.Value > 0 && written != declaredSize.Value)
                    throw new HostFacingException("bepinex.integrity",
                        "The BepInEx download did not match what the site listed, so nothing was written.");

                if (!string.IsNullOrWhiteSpace(declaredSha))
                {
                    var actual = Sha256Of(tempZip);
                    if (!string.Equals(actual, declaredSha.Trim(), StringComparison.OrdinalIgnoreCase))
                        throw new HostFacingException("bepinex.integrity",
                            "The BepInEx download did not match what the site listed, so nothing was written.");
                }

                Report(progress, "extracting", 55, version);

                Directory.CreateDirectory(tempExtract);
                try
                {
                    // ExtractToDirectory turns down an entry that resolves outside the folder
                    // it was pointed at, which is the zip slip guard this relies on.
                    ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);
                }
                catch (HostFacingException) { throw; }
                catch (Exception e)
                {
                    throw new HostFacingException("bepinex.notALoader",
                        "That archive would not open as a BepInEx pack: " + e.Message, ("detail", e.Message));
                }

                var loaderRoot = LoaderRootOf(tempExtract)
                    ?? throw new HostFacingException("bepinex.notALoader",
                        "That archive is not a BepInEx pack, so nothing was written.");

                // The repair's own check, and the reason a repair no longer needs a size off a
                // listing. Nothing the site says covers the version the note names once
                // Thunderstore has moved on from it, so what came down used to be unpacked on
                // trust. The note is what ends that: it holds a SHA256 for the loader files
                // BakaLoader could read as it wrote them out of this very version, and the
                // archive is held to every one of those digests here, with the pack's bytes on
                // one side and the note's digests on the other. What is on disk is not the
                // question, because half of those files are the ones that went missing.
                //
                // Every way an archive can differ refuses. One file the archive does not carry,
                // one that is a different file, one the note recorded with no digest, or one in
                // the archive that will not read, means this cannot be shown to be the pack
                // that was written here. So does one the archive carries that the note never
                // recorded: the core is copied WHOLE and the note is rebuilt off that folder
                // afterwards, so an addition nobody compared would be written into the folder
                // the loader resolves assemblies from and then recorded with its own digest,
                // and every reading after that would call the install untouched.
                //
                // The note is read again here rather than carried down from the status at the
                // top, because the download in between is minutes on a slow line. A note that
                // vouches for nothing leaves the archive held to nothing, and this write was
                // called a repair on the strength of one that did vouch for files, so that is a
                // refusal too rather than a fall-through.
                //
                // Either way the repair stops before the pre-flight and before a byte moves.
                if (repairing)
                {
                    var note = BepInExMarkerFile.Read(baseDir);
                    var wrong = PackAgainstNote(loaderRoot, note);
                    var vouchedFor = NoteVouchesForALoaderFile(note);

                    if (wrong.Count > 0 || !vouchedFor)
                    {
                        // Which of the two it was, because they ask a host for different things.
                        if (!vouchedFor)
                            Logger.Warning(
                                "The note beside this install holds a digest for no loader file at all, so "
                                + "there was nothing to hold the BepInEx {0} that came down to and the "
                                + "repair put nothing back.", version);
                        else
                            Logger.Warning(
                                "The BepInEx {0} that came down is not what the note recorded, on {1} "
                                + "file(s): {2}. The repair put nothing back.",
                                version, wrong.Count, string.Join("; ", wrong));

                        if (options.Unattended)
                            return Skipped(status, BepInExSkipReason.RepairMismatch, version);

                        throw new HostFacingException("bepinex.repairMismatch",
                            $"The BepInEx {version} that came down does not match, file for file, what "
                            + "BakaLoader recorded when it installed that version, so nothing was put back.",
                            ("version", version));
                    }
                }

                Report(progress, "installing", 70, version);

                // Asked AGAIN, here, with the archive on disk and not one byte written yet.
                // The first ask was before the download, and a fifty megabyte fetch on a slow
                // line is minutes: a host who started a realm in that window would otherwise
                // have its BepInEx/core cleared and rewritten underneath it. The caller's
                // sequence is re-enumerated for this, which is why the two entry points say so.
                RefuseWhileServersAreUp(baseExePath, profiles);

                var before = BepInExMarkerFile.Read(baseDir)?.Version;
                var installedCore = System.IO.Path.Combine(baseDir, "BepInEx", "core", CoreAssemblyName);
                var replaced = File.Exists(installedCore);
                var previousCoreVersion = replaced ? FileVersionOf(installedCore) : null;

                // The pack's own core assembly, read from the archive rather than from the
                // version number the listing carries: the pack version and the framework
                // version are different numbers and only one of them can be compared with what
                // is on disk.
                var packCoreVersion = FileVersionOf(
                    System.IO.Path.Combine(loaderRoot, "BepInEx", "core", CoreAssemblyName));

                if (IsDowngrade(previousCoreVersion, packCoreVersion))
                {
                    if (options.Unattended)
                    {
                        Logger.Information(
                            "The BepInEx here ({0}) is newer than the current pack ({1}), so it was left alone.",
                            previousCoreVersion, packCoreVersion);
                        return Skipped(status, BepInExSkipReason.Newer, packCoreVersion);
                    }

                    if (!options.AllowDowngrade)
                        throw new HostFacingException("bepinex.newer",
                            $"The BepInEx here ({previousCoreVersion}) is newer than the current pack "
                            + $"({packCoreVersion}), so nothing was written.",
                            ("installed", previousCoreVersion), ("pack", packCoreVersion));
                }

                // Nothing the pack would write differs from what is here. Adopt by note alone:
                // the commonest case for a host who already had BepInEx, and the one where the
                // safest thing to do with the files is not to touch them.
                if (replaced && BepInExIntegrity.PackMatchesDisk(loaderRoot, baseDir))
                {
                    var adoptedEntries = EntriesFromDisk(baseDir);

                    BepInExMarkerFile.Write(baseDir, new BepInExMarker
                    {
                        Schema = BepInExMarkerFile.CurrentSchema,
                        Writer = BepInExMarkerFile.WriterPrefix + " " + AssemblyHelper.GetApplicationVersion(),
                        Package = owner + "-" + name,
                        Version = version ?? VersionFromArchive(loaderRoot) ?? "unknown",
                        InstalledUtc = DateTime.UtcNow,
                        Source = address,
                        Files = adoptedEntries,
                    });
                    DropStaleNote(baseDir);

                    Report(progress, "linking", 90, version);
                    var linkedOnly = LinkExistingIsolatedInstalls(baseExePath, baseDir);
                    var refreshedOnly = RefreshProfileLoaderFiles(baseExePath, baseDir, doorstopReplaced: false);
                    Report(progress, "done", 100, version);

                    Logger.Information(
                        "BepInEx at {0} already held the current pack, so only the note was written.", baseDir);

                    return new BepInExInstallResult
                    {
                        Installed = true,
                        Replaced = false,
                        NothingChanged = true,
                        Adopted = before == null,
                        Version = version ?? VersionFromArchive(loaderRoot),
                        Package = owner + "-" + name,
                        Source = address,
                        PreviousVersion = before ?? previousCoreVersion,
                        PreviousPackVersion = before,
                        PreviousCoreVersion = previousCoreVersion,
                        CoreVersion = previousCoreVersion,
                        IsolatedInstallsLinked = linkedOnly,
                        ProfileLoaderFilesRefreshed = refreshedOnly,
                    };
                }

                // The same framework version but not the same bytes is a core somebody patched
                // or pinned on purpose. Writing the stock pack over it would undo that quietly.
                //
                // An install with a file MISSING is never one of those, and this is the guard
                // it used to die on. The digest comparison above answers "not the same bytes"
                // just as readily for a file that is gone as for one that was edited, and on a
                // repair the version on disk always equals the pack's, because BakaLoader wrote
                // it from that pack: without the missing-files clause here every unattended
                // repair refused itself and the row was told the core was somebody else's work.
                if (replaced && previousCoreVersion != null && packCoreVersion != null
                    && string.Equals(previousCoreVersion, packCoreVersion, StringComparison.OrdinalIgnoreCase)
                    && status.MissingFiles.Count == 0
                    && options.Unattended)
                    return Skipped(status, BepInExSkipReason.Foreign, packCoreVersion);

                // "adopting" is the write that takes an install BakaLoader did not make into
                // its care: there is something here and no trusted note beside it. The backup
                // that write leaves is the only copy of the host's own loader there will ever
                // be, so it is marked and never pruned.
                var write = Written(() => WriteLoader(loaderRoot, baseDir, adopting: replaced && before == null));

                var noteIsDown = false;

                try
                {
                    // Inside Written() as well, and for a sharper reason than the file moves
                    // above. The loader is ALREADY IN by this line; a note that will not write
                    // (something holding it open, a folder gone read only) used to come out of
                    // here as a raw .NET sentence with no id for the page to word, and its
                    // English has to be the one that does not claim the install was left as it
                    // was, because it was not.
                    Written<object>(() =>
                    {
                        WriteNote(baseDir, new BepInExMarker
                        {
                            Schema = BepInExMarkerFile.CurrentSchema,
                            Writer = BepInExMarkerFile.WriterPrefix + " " + AssemblyHelper.GetApplicationVersion(),
                            Package = owner + "-" + name,
                            Version = version ?? VersionFromArchive(loaderRoot) ?? "unknown",
                            InstalledUtc = DateTime.UtcNow,
                            Source = address,
                            Files = write.Entries,
                        });
                        DropStaleNote(baseDir);
                        noteIsDown = true;
                        return null;
                    });
                }
                finally
                {
                    // The note is in, so the write is finished as far as anything reading this
                    // install is concerned. The mark comes off here, BEFORE the config copy
                    // below, which is why that copy failing is not a failed install.
                    //
                    // The other way out is a note the retries could not write, and there the
                    // mark STAYS. What is on disk then is BakaLoader's own fresh loader under
                    // the OLD note, which describes the files it just replaced: a status read
                    // that compared the two would call this write somebody else's work and set
                    // the note aside, and that is a ONE WAY DOOR, because the file it leaves
                    // keeps the install Outside on every read afterwards. The mark is what
                    // tells every later read not to judge these files against that note, and
                    // the heal takes it off as soon as the note can be put right.
                    if (noteIsDown) write.Sentinel.Dispose();
                    else write.Sentinel.Abandon();
                }

                CopyShippedConfig(loaderRoot, baseDir);

                Report(progress, "linking", 90, version);
                var linked = LinkExistingIsolatedInstalls(baseExePath, baseDir);
                var refreshed = RefreshProfileLoaderFiles(baseExePath, baseDir, write.DoorstopReplaced);

                Report(progress, "done", 100, version);

                var moved = version ?? VersionFromArchive(loaderRoot);

                // The line the host reads afterwards. It names what was replaced by what and
                // where what it replaced went, because "BepInEx was updated" answers neither
                // of the two questions a host asks the morning after an unattended write.
                Logger.Information(
                    "BepInEx at {0} went from {1} to {2} ({3} file(s)); the files it replaced are in "
                    + "BepInEx\\{4}\\{5}. {6} isolated install(s) linked, {7} loader file(s) refreshed.",
                    baseDir,
                    before ?? previousCoreVersion ?? "nothing",
                    moved ?? "a pack from a link",
                    write.Entries.Count,
                    BackupDirName,
                    write.BackupStamp ?? "(nothing was replaced)",
                    linked,
                    refreshed);

                return new BepInExInstallResult
                {
                    Installed = true,
                    Replaced = replaced,
                    Adopted = replaced && before == null,
                    Version = moved,
                    Package = owner + "-" + name,
                    Source = address,
                    PreviousVersion = before ?? previousCoreVersion,
                    PreviousPackVersion = before,
                    PreviousCoreVersion = previousCoreVersion,
                    CoreVersion = packCoreVersion,
                    BackupStamp = write.BackupStamp,
                    DoorstopReplaced = write.DoorstopReplaced,
                    IsolatedInstallsLinked = linked,
                    ProfileLoaderFilesRefreshed = refreshed,
                };
            }
            finally
            {
                TryDelete(tempZip);
                TryDeleteDirectory(tempExtract);
            }
        }

        public Task<bool> RemoveWrongLocationAsync(string pluginsDir,
            IEnumerable<BepInExProfileInstall> profiles, string baseExePath)
        {
            var wrong = WrongLocationFolderIn(pluginsDir);
            if (wrong == null)
                throw new HostFacingException("bepinex.noWrongFolder",
                    "There is no mis-placed BepInEx folder to remove.");

            RefuseWhileServersAreUp(baseExePath, profiles);

            Directory.Delete(wrong, recursive: true);
            Logger.Information("Removed the mis-placed BepInEx folder at {0}.", wrong);
            return Task.FromResult(true);
        }

        // ------------------------------------------------------------------ restore and heal

        public Task<BepInExInstallResult> RestoreAsync(string baseExePath,
            IEnumerable<BepInExProfileInstall> profiles, string stamp = null,
            IProgress<BepInExProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            var baseDir = InstallRootOf(baseExePath);
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
                throw new HostFacingException("bepinex.noServerPath",
                    "Set a valid server .exe path before restoring BepInEx.");

            RefuseWhileServersAreUp(baseExePath, profiles);

            var backup = BepInExBackups.Find(baseDir, stamp);
            if (backup?.CoreFolder == null || !Directory.Exists(backup.CoreFolder))
                throw new HostFacingException("bepinex.noBackup",
                    "There is no BepInEx backup here to put back.");

            Report(progress, "installing", 40, null);
            var result = PutBackupBack(baseExePath, baseDir, backup, healed: false);
            Report(progress, "done", 100, null);

            return Task.FromResult(result);
        }

        public BepInExInstallResult HealInterruptedWrite(string baseExePath,
            IEnumerable<BepInExProfileInstall> profiles)
        {
            var baseDir = InstallRootOf(baseExePath);
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) return null;

            var stamp = BepInExWriteSentinel.StaleStamp(baseDir);
            if (stamp == null) return null;

            var coreDll = System.IO.Path.Combine(baseDir, "BepInEx", "core", CoreAssemblyName);

            // The mark is only ever left behind by a write that stopped somewhere, and the one
            // place stopping costs the host anything is the single rename between the old core
            // going into the backup and the new one coming in. An install whose core is there
            // came through that window: taking its core away again to put an older one back
            // would be the damage, not the repair. So the mark comes off and nothing moves.
            if (File.Exists(coreDll))
            {
                // One thing first. A mark over a whole core is also what a write whose NOTE
                // would not write leaves behind, and there the note beside these files still
                // describes the ones they replaced. Clearing the mark would let the very next
                // read compare the two and call BakaLoader's own write somebody else's work,
                // which is the one way door the mark is standing in front of. So the note is
                // settled first, and if it cannot be (whatever stopped it being written is
                // very often still holding it) the mark stays and the next read tries again.
                if (!SettleNoteAgainstDisk(baseDir)) return null;

                BepInExWriteSentinel.Clear(baseDir);
                Logger.Debug("A BepInEx write mark was left at {0}, but the core is whole.", baseDir);
                return null;
            }

            var backup = BepInExBackups.Find(baseDir, stamp);
            if (backup?.CoreFolder == null || !Directory.Exists(backup.CoreFolder))
            {
                BepInExWriteSentinel.Clear(baseDir);
                Logger.Warning(
                    "A BepInEx write at {0} did not finish and the backup it named ({1}) is not there.",
                    baseDir, stamp);
                return null;
            }

            RefuseWhileServersAreUp(baseExePath, profiles);

            Logger.Information("A BepInEx write at {0} did not finish; putting backup {1} back.", baseDir, stamp);
            return PutBackupBack(baseExePath, baseDir, backup, healed: true);
        }

        /// <summary>
        /// The restore itself, shared by the button and by the heal. It never starts by
        /// clearing anything: the core that is there now is moved into a backup of its own, and
        /// only then does the old one move in. So a restore that falls over leaves both copies
        /// on disk rather than neither.
        /// </summary>
        private BepInExInstallResult PutBackupBack(
            string baseExePath, string baseDir, BepInExBackup backup, bool healed)
        {
            var bepDir = System.IO.Path.Combine(baseDir, "BepInEx");
            var coreDir = System.IO.Path.Combine(bepDir, "core");

            if (IsReparsePoint(coreDir))
                throw new HostFacingException("bepinex.coreIsJunction",
                    "This install's BepInEx core is a link to another folder, so the write belongs on the "
                    + "install it points at.");

            var rootFiles = backup.RootFolder != null && Directory.Exists(backup.RootFolder)
                ? Directory.GetFiles(backup.RootFolder).Select(System.IO.Path.GetFileName).ToList()
                : new List<string>();

            ProveWritable(baseDir, coreDir, rootFiles);

            var previousCoreVersion = FileVersionOf(System.IO.Path.Combine(coreDir, CoreAssemblyName));
            var fresh = BepInExBackups.Begin(baseDir, DateTime.Now);
            var staged = System.IO.Path.Combine(bepDir, ".bakaloader-bepinex-core-" + Guid.NewGuid().ToString("N"));

            using (BepInExWriteSentinel.Hold(baseDir, fresh.Stamp))
            {
                Written<object>(() =>
                {
                    try
                    {
                        CopyDirectory(backup.CoreFolder, staged);
                        SwapCoreIn(coreDir, staged, fresh);

                        foreach (var name in rootFiles)
                        {
                            var source = System.IO.Path.Combine(backup.RootFolder, name);
                            var destination = System.IO.Path.Combine(baseDir, name);

                            if (File.Exists(destination)) BackUpRootFile(destination, fresh, name);
                            CopyInPlace(source, destination);
                        }
                    }
                    catch
                    {
                        TryDeleteDirectory(staged);
                        DropEmptyBackup(fresh);
                        throw;
                    }

                    return null;
                });

                // A restored core is not the pack the old note named, and there is nothing on
                // disk that says which pack a backup came from. So the note is rewritten from
                // what is actually here, with the version left unknown: the install stays one
                // BakaLoader looks after, the digests describe the real files, and the next
                // window moves it on to the current pack the ordinary way.
                //
                // Inside Written() for the same reason the write path's note is: the files have
                // already moved by this line, so a note that will not write must come out as a
                // refusal the page has words for rather than as a raw .NET sentence.
                Written<object>(() =>
                {
                    var keptPackage = BepInExMarkerFile.Read(baseDir)?.Package ?? DefaultPackage;

                    WriteNote(baseDir, new BepInExMarker
                    {
                        Schema = BepInExMarkerFile.CurrentSchema,
                        Writer = BepInExMarkerFile.WriterPrefix + " " + AssemblyHelper.GetApplicationVersion(),
                        Package = keptPackage,
                        Version = "unknown",
                        InstalledUtc = DateTime.UtcNow,
                        Source = "backup " + backup.Stamp,
                        Files = EntriesFromDisk(baseDir),
                    });
                    DropStaleNote(baseDir);
                    return null;
                });
            }

            // Putting a backup back over an install whose core had already gone replaces
            // nothing, so the folder this restore opened for it is empty and is not a backup.
            var keptFresh = HoldsSomething(fresh.Folder);
            if (keptFresh) BepInExBackups.Prune(baseDir, fresh.Stamp, KeptCoreBackups);
            else DropEmptyBackup(fresh);

            var linked = LinkExistingIsolatedInstalls(baseExePath, baseDir);

            // A restore puts the loose loader files back too, so a profile holding copies of
            // them is as far out of step now as it was after the write this undoes.
            var refreshed = RefreshProfileLoaderFiles(baseExePath, baseDir,
                doorstopReplaced: rootFiles.Any(IsDoorstopConfig));

            Logger.Information(
                "The BepInEx backup {0} was put back into {1}: the core went from {2} back to {3}, and "
                + "what was here is in BepInEx\\{4}\\{5}.",
                backup.Stamp, baseDir,
                previousCoreVersion ?? "nothing",
                FileVersionOf(System.IO.Path.Combine(coreDir, CoreAssemblyName)) ?? "a core with no version in it",
                BackupDirName,
                keptFresh ? fresh.Stamp : "(nothing was replaced)");

            return new BepInExInstallResult
            {
                Installed = File.Exists(System.IO.Path.Combine(coreDir, CoreAssemblyName)),
                Replaced = true,
                Restored = true,
                Healed = healed,
                Version = "unknown",
                Package = BepInExMarkerFile.Read(baseDir)?.Package ?? DefaultPackage,
                Source = "backup " + backup.Stamp,
                PreviousVersion = previousCoreVersion,
                PreviousCoreVersion = previousCoreVersion,
                CoreVersion = FileVersionOf(System.IO.Path.Combine(coreDir, CoreAssemblyName)),
                BackupStamp = keptFresh ? fresh.Stamp : null,
                IsolatedInstallsLinked = linked,
                ProfileLoaderFilesRefreshed = refreshed,
            };
        }

        // ------------------------------------------------------------------ the write itself

        /// <summary>What one write did, beyond the files it left behind.</summary>
        private sealed class WriteOutcome
        {
            public List<BepInExMarkerFileEntry> Entries { get; init; }
            public string BackupStamp { get; init; }

            /// <summary>
            /// True when that backup holds the loader the host had before BakaLoader, which is
            /// the one copy of it there will ever be.
            /// </summary>
            public bool AdoptionBackup { get; init; }

            public bool DoorstopReplaced { get; init; }
            public IBepInExWriteMark Sentinel { get; init; }
        }

        /// <summary>
        /// Writes the allow list out of the pack's own folder into the install root, and
        /// answers what it wrote.
        /// <para>
        /// Nothing is removed before it has been proved removable. The pre-flight opens every
        /// file this is about to touch for reading AND writing with no sharing at all, then
        /// closes it again: a core assembly another process has loaded is open with
        /// <c>FileShare.Read</c>, which copies perfectly well and refuses to be deleted, so a
        /// writer that checks by copying finds out half way through. That list is every file
        /// under the core, every root file the pack will write over, AND every root file the
        /// pack does not ship that will be moved aside further down. A file in it that will not
        /// open means nothing has been written and the refusal names the file.
        /// </para>
        /// <para>
        /// The core is then SWAPPED rather than cleared: the new one is unpacked into a folder
        /// beside it, the old one is RENAMED into the backup (which is also how it gets backed
        /// up, with no second copy), and the new one is renamed into its place. An isolated
        /// profile's junction points at the PATH, so it follows the new folder without being
        /// touched. If the second rename fails the first one goes straight back.
        /// </para>
        /// <para>
        /// The loose root files go afterwards, each copied IN PLACE and never deleted first: on
        /// an install with isolated profiles those files are hard links, and deleting one would
        /// break the link and leave every other profile on a stale loader.
        /// </para>
        /// </summary>
        private WriteOutcome WriteLoader(string loaderRoot, string baseDir, bool adopting)
        {
            var bepDir = System.IO.Path.Combine(baseDir, "BepInEx");
            var coreDir = System.IO.Path.Combine(bepDir, "core");
            var sourceCore = System.IO.Path.Combine(loaderRoot, "BepInEx", "core");

            if (IsReparsePoint(coreDir))
                throw new HostFacingException("bepinex.coreIsJunction",
                    "This install's BepInEx core is a link to another folder, so the write belongs on the "
                    + "install it points at.");

            var doorstopReplaced = ShouldReplaceDoorstop(loaderRoot, baseDir);

            var willReplace = RootFileAllowList
                .Where(fileName => File.Exists(System.IO.Path.Combine(loaderRoot, fileName)))
                .Where(fileName => doorstopReplaced || !IsDoorstopConfig(fileName))
                .ToList();

            // The other half of what this write touches at the root: a loader file the NEW pack
            // does not ship, which is MOVED into the backup further down so what is left is one
            // coherent set. A move is every bit as much a write to that file as a copy over it,
            // and it happens AFTER the core has already been swapped: without it in the
            // pre-flight, a held .doorstop_version came out as a failure whose sentence said
            // the install had been left as it was while the core, winhttp.dll and
            // doorstop_config.ini had all been replaced. The host's own doorstop_config.ini is
            // never moved by that rule, so it is not proved for it here either.
            var willMove = RootFileAllowList
                .Where(fileName => !File.Exists(System.IO.Path.Combine(loaderRoot, fileName)))
                .Where(fileName => !IsDoorstopConfig(fileName))
                .ToList();

            ProveWritable(baseDir, coreDir, willReplace.Concat(willMove));

            var backup = BepInExBackups.Begin(baseDir, DateTime.Now);
            var staged = System.IO.Path.Combine(bepDir, ".bakaloader-bepinex-core-" + Guid.NewGuid().ToString("N"));
            var written = new List<BepInExMarkerFileEntry>();
            var sentinel = BepInExWriteSentinel.Hold(baseDir, backup.Stamp);

            try
            {
                if (Directory.Exists(sourceCore))
                {
                    Directory.CreateDirectory(staged);

                    // Every file under the pack's core, subfolders and all. BepInEx 5's core is
                    // flat today, so this changes nothing about the pack that ships now; what it
                    // stops is a pack that grows a folder (a net472 tier, a runtimes tier)
                    // installing a core missing those assemblies while the note beside it claims
                    // a complete write. "The core is replaced whole" has to be what the code does.
                    foreach (var file in Directory.GetFiles(sourceCore, "*", SearchOption.AllDirectories))
                    {
                        var relative = System.IO.Path.GetRelativePath(sourceCore, file);
                        var destination = System.IO.Path.Combine(staged, relative);

                        var folder = System.IO.Path.GetDirectoryName(destination);
                        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

                        CopyCoreFile(file, destination);
                    }

                    SwapCoreIn(coreDir, staged, backup);

                    foreach (var file in Directory.GetFiles(coreDir, "*", SearchOption.AllDirectories))
                        written.Add(Entry(baseDir, file));
                }

                // The root files, after the core is in and only then: a loader file beside a
                // core that never arrived is an install that starts nothing.
                foreach (var fileName in RootFileAllowList)
                {
                    var source = System.IO.Path.Combine(loaderRoot, fileName);
                    var destination = System.IO.Path.Combine(baseDir, fileName);

                    if (!File.Exists(source))
                    {
                        // A loader file the new pack does not ship, still sitting here from the
                        // old one. It goes into the backup so what is left is one coherent set:
                        // a stale .doorstop_version beside a newer loader is exactly the kind of
                        // mismatch that makes a working install stop working. The host's own
                        // doorstop_config.ini is never moved away by this rule: section D keeps
                        // that file, and keeping it means keeping it even when the pack is odd.
                        if (File.Exists(destination) && !IsDoorstopConfig(fileName))
                            MoveRootFileToBackup(destination, backup, fileName);
                        continue;
                    }

                    // The host's doorstop_config.ini drives the loader and is theirs. It is
                    // only replaced when it cannot drive the loader going in.
                    if (IsDoorstopConfig(fileName) && !doorstopReplaced)
                        continue;

                    if (File.Exists(destination)) BackUpRootFile(destination, backup, fileName);

                    CopyInPlace(source, destination);
                    written.Add(Entry(baseDir, destination));
                }
            }
            catch
            {
                sentinel.Dispose();
                TryDeleteDirectory(staged);
                DropEmptyBackup(backup);
                throw;
            }

            // A first install replaced nothing, so its backup folder is empty. An empty folder
            // is not a backup and must not be counted as one, or the row would offer a restore
            // that put nothing back.
            var kept = HoldsSomething(backup.Folder);

            // The write that brought an install BakaLoader did not make into its care is the
            // only one whose backup holds a loader BakaLoader did not write, and it is the only
            // copy of it that will ever exist: the host cannot fetch their own hand made core
            // back off Thunderstore. So it is marked, and the keep-three rule steps over it.
            // Keeping three drops the OLDEST first, which is exactly this one.
            var adoptionBackup = kept && adopting;
            if (adoptionBackup)
                BepInExAdoptionMark.Write(backup.Folder, DateTime.UtcNow,
                    FileVersionOf(System.IO.Path.Combine(backup.CoreFolder, CoreAssemblyName)));

            if (kept) BepInExBackups.Prune(baseDir, backup.Stamp, KeptCoreBackups);
            else DropEmptyBackup(backup);

            return new WriteOutcome
            {
                Entries = written,
                BackupStamp = kept ? backup.Stamp : null,
                AdoptionBackup = adoptionBackup,
                DoorstopReplaced = doorstopReplaced,
                Sentinel = sentinel,
            };
        }

        /// <summary>
        /// Runs the part of a write that moves files, and makes sure whatever comes out of it
        /// is a refusal the page has words for.
        /// <para>
        /// Everything this guards already refuses in named sentences where it can see the
        /// problem coming: a locked file, a core that is a link, a server that is up. What is
        /// left is the file system saying no in the middle of a rename, and a raw .NET
        /// sentence reaching a toast tells a host nothing they can act on. The id is the same
        /// whichever step it was, and the .NET message rides along as a named slot for the log
        /// and the debug console.
        /// </para>
        /// </summary>
        private static T Written<T>(Func<T> write)
        {
            try
            {
                return write();
            }
            catch (HostFacingException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                throw new HostFacingException("bepinex.writeFailed",
                    "BepInEx could not be written here: " + e.Message, ("detail", e.Message));
            }
        }

        /// <summary>
        /// The swap. Two renames on one volume, with the first one undone when the second one
        /// fails, so there is no moment where the only copy of the core is half written.
        /// </summary>
        private static void SwapCoreIn(string coreDir, string staged, BepInExBackup backup)
        {
            var moved = false;

            if (Directory.Exists(coreDir) && HoldsSomething(coreDir))
            {
                Directory.CreateDirectory(backup.Folder);
                Directory.Move(coreDir, backup.CoreFolder);
                moved = true;
            }
            else if (Directory.Exists(coreDir))
            {
                Directory.Delete(coreDir);
            }

            try
            {
                Directory.Move(staged, coreDir);
            }
            catch
            {
                if (moved) Directory.Move(backup.CoreFolder, coreDir);
                throw;
            }
        }

        /// <summary>
        /// Opens every file this write is about to replace for reading AND writing with no
        /// sharing, then closes it. Anything that will not open means nothing has been written
        /// and the host is told which file it was.
        /// </summary>
        private static void ProveWritable(string baseDir, string coreDir, IEnumerable<string> rootFileNames)
        {
            foreach (var fileName in rootFileNames ?? Array.Empty<string>())
            {
                var path = System.IO.Path.Combine(baseDir, fileName);
                if (File.Exists(path)) ProveOneWritable(path);
            }

            if (!Directory.Exists(coreDir)) return;

            foreach (var file in Directory.GetFiles(coreDir, "*", SearchOption.AllDirectories))
                ProveOneWritable(file);
        }

        private static void ProveOneWritable(string path)
        {
            try
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { /* best effort */ }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception e)
            {
                var name = System.IO.Path.GetFileName(path);
                throw new HostFacingException("bepinex.locked",
                    $"BakaLoader could not get at {name}, so nothing was written. Something has it open: "
                    + "stop every server on this install, close anything reading the folder, and try again.",
                    ("file", name), ("path", path), ("detail", e.Message));
            }
        }

        /// <summary>
        /// Whether the host's doorstop_config.ini has to give way. Only two things say so: the
        /// pack ships a different Doorstop generation from the one on disk, or the file uses the
        /// other section header. Everything else in there is the host's own tuning.
        /// </summary>
        private static bool ShouldReplaceDoorstop(string loaderRoot, string baseDir)
        {
            var packConfig = System.IO.Path.Combine(loaderRoot, BepInExDoorstop.FileName);
            var diskConfig = System.IO.Path.Combine(baseDir, BepInExDoorstop.FileName);

            if (!File.Exists(packConfig)) return false;

            // Nothing to keep: an install with no doorstop config gets the pack's.
            if (!File.Exists(diskConfig)) return true;

            try
            {
                return BepInExDoorstop.GenerationChanged(
                    BepInExDoorstop.VersionTextIn(loaderRoot),
                    BepInExDoorstop.VersionTextIn(baseDir),
                    BepInExDoorstop.SectionInText(File.ReadAllText(packConfig)),
                    BepInExDoorstop.SectionInText(File.ReadAllText(diskConfig)));
            }
            catch
            {
                // A file that will not read is a file that is kept.
                return false;
            }
        }

        private static bool IsDoorstopConfig(string fileName)
            => string.Equals(fileName, BepInExDoorstop.FileName, StringComparison.OrdinalIgnoreCase);

        private static void BackUpRootFile(string path, BepInExBackup backup, string fileName)
        {
            Directory.CreateDirectory(backup.RootFolder);
            File.Copy(path, System.IO.Path.Combine(backup.RootFolder, fileName), overwrite: true);
        }

        private static void MoveRootFileToBackup(string path, BepInExBackup backup, string fileName)
        {
            Directory.CreateDirectory(backup.RootFolder);
            File.Move(path, System.IO.Path.Combine(backup.RootFolder, fileName), overwrite: true);
        }

        /// <summary>
        /// Gale's rule, and the right one: the pack ships a default config, and r2modman
        /// overwrites a host's edited one with it on every update. Only write it when the
        /// install has none.
        /// <para>
        /// It runs AFTER the note, and a failure here is not a failure of the install: the
        /// loader is in and BepInEx writes itself a default config on its first start anyway.
        /// </para>
        /// </summary>
        private void CopyShippedConfig(string loaderRoot, string baseDir)
        {
            try
            {
                var sourceCfg = System.IO.Path.Combine(loaderRoot, "BepInEx", "config", ShippedConfigName);
                var destinationCfg = System.IO.Path.Combine(baseDir, "BepInEx", "config", ShippedConfigName);
                if (!File.Exists(sourceCfg) || File.Exists(destinationCfg)) return;

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destinationCfg));
                File.Copy(sourceCfg, destinationCfg, overwrite: false);
            }
            catch (Exception e)
            {
                Logger.Warning("BepInEx is in, but its first config could not be written: {0}", e.Message);
            }
        }

        /// <summary>
        /// One file of the core, copied. Virtual for one reason: a copy that falls over half
        /// way is the failure the staging folder exists for, and there is no way to make
        /// <see cref="File.Copy(string,string,bool)"/> do that from the outside. A test
        /// overrides this and the path is driven for real rather than reasoned about.
        /// </summary>
        protected virtual void CopyCoreFile(string source, string destination)
            => File.Copy(source, destination, overwrite: true);

        /// <summary>
        /// Every valheim_server process that is up on this machine, by the image it runs. The
        /// real enumeration, and the one seam a test replaces so the suite never reads the
        /// process table.
        /// </summary>
        protected virtual IReadOnlyList<string> RunningServerImagePaths()
        {
            var paths = new List<string>();

            try
            {
                foreach (var process in Process.GetProcessesByName("valheim_server"))
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            var image = process.MainModule?.FileName;
                            if (!string.IsNullOrWhiteSpace(image)) paths.Add(image);
                        }
                    }
                    catch
                    {
                        // A process at another elevation will not say what it runs. It is not
                        // claimed either way: the pre-flight is what stops the write then.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception e)
            {
                Logger?.Debug("Could not read the running server list: {0}", e.Message);
            }

            return paths;
        }

        /// <summary>
        /// The one refusal, asked of both lists: the profiles this app started, and the
        /// valheim_server processes that are up whoever started them.
        /// </summary>
        private void RefuseWhileServersAreUp(string baseExePath, IEnumerable<BepInExProfileInstall> profiles)
        {
            var blocked = ProfilesBlockingWrite(baseExePath, profiles).ToList();

            foreach (var name in ForeignServersBlockingWrite(baseExePath, RunningServerImagePaths()))
                if (!blocked.Contains(name, StringComparer.OrdinalIgnoreCase))
                    blocked.Add(name);

            if (blocked.Count == 0) return;

            throw new HostFacingException("bepinex.serversRunning",
                "BakaLoader counts these servers as loading this same BepInEx, so nothing is written "
                + "while one of them is up. Still up: " + string.Join(", ", blocked),
                ("names", string.Join(", ", blocked)));
        }

        /// <summary>
        /// A copy that keeps the destination's identity: the bytes are written through the
        /// file that is already there, so a hard link from an isolated install follows.
        /// </summary>
        private static void CopyInPlace(string source, string destination)
        {
            var folder = System.IO.Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

            if (File.Exists(destination))
            {
                try { File.SetAttributes(destination, FileAttributes.Normal); } catch { /* best effort */ }
            }

            File.Copy(source, destination, overwrite: true);
        }

        private static BepInExMarkerFileEntry Entry(string baseDir, string file) => new()
        {
            Path = RelativeTo(baseDir, file),
            Sha256 = Sha256Of(file),
        };

        /// <summary>
        /// Everything about an unpacked pack that the note does not account for, each with the
        /// way it is wrong written beside it, ready for a log line. An empty list means the
        /// archive holds every loader file the note vouched for, unchanged, AND holds no loader
        /// file the note never vouched for.
        /// <para>
        /// The PACK's bytes are on one side and the NOTE's digests are on the other. What the
        /// install still has on disk is not the question: on a repair some of these files are
        /// the ones that went, which is why the repair is happening at all.
        /// </para>
        /// <para>
        /// The set is compared in BOTH directions, for the reason
        /// <see cref="BepInExIntegrity.PackMatchesDisk"/> gives for comparing its own set both
        /// ways. A core file the note never named still loads once it is written, because the
        /// core goes in whole, and the note is rebuilt off that folder afterwards: an addition
        /// nobody looked at would be recorded with its own digest and read as BakaLoader's own
        /// work from then on. So an archive is wrong for carrying a loader file as well as for
        /// missing one or holding a different one.
        /// </para>
        /// <para>
        /// An entry the note recorded with NO digest is not a file anything vouched for, and an
        /// archive is not let past on one: BakaLoader writes those itself whenever a file would
        /// not read at the moment the note was built, which is the same antivirus case this
        /// repair exists for. A file in the archive whose bytes will not read counts as wrong
        /// too, which is the opposite call from <see cref="BepInExIntegrity.Against"/> and on
        /// purpose: there an unreadable file must not be read as somebody taking the install
        /// over, here it is a file about to be written that nothing has vouched for.
        /// </para>
        /// <para>
        /// The set of files is the one <see cref="BepInExIntegrity"/> compares elsewhere, so
        /// the host's own BepInEx.cfg, the doorstop config, the changelog and the symbols
        /// beside an assembly are no more the subject here than they are there.
        /// </para>
        /// </summary>
        private static IReadOnlyList<string> PackAgainstNote(string loaderRoot, BepInExMarker marker)
        {
            var (vouched, listed) = NoteLoaderFiles(marker);

            // Sorted so the log reads the same way twice, and a set so a file both walks reach
            // is named once.
            var wrong = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            // What the note named, asked of the archive.
            foreach (var path in listed)
            {
                var inPack = PackPathOf(loaderRoot, path);

                if (inPack == null || !File.Exists(inPack))
                {
                    wrong.Add(path + " is not in the archive");
                    continue;
                }

                if (!vouched.TryGetValue(path, out var recorded))
                {
                    wrong.Add(path + " was recorded with no digest");
                    continue;
                }

                var actual = Sha256Of(inPack);

                if (actual == null) wrong.Add(path + " in the archive would not read");
                else if (!string.Equals(actual, recorded, StringComparison.OrdinalIgnoreCase))
                    wrong.Add(path + " is a different file");
            }

            // And the other way round: what the archive carries, asked of the note. A path the
            // note named has already been answered for above, whichever way it went.
            foreach (var (path, _) in PackLoaderFiles(loaderRoot))
                if (!listed.Contains(path))
                    wrong.Add(path + " is not in the note at all");

            return wrong.ToList();
        }

        /// <summary>
        /// True when the note holds a digest for at least one loader file, which is the least
        /// it has to hold for <see cref="PackAgainstNote"/> to mean anything.
        /// </summary>
        private static bool NoteVouchesForALoaderFile(BepInExMarker marker)
            => NoteLoaderFiles(marker).Vouched.Count > 0;

        /// <summary>
        /// The note's loader-identity entries twice over: the ones it actually recorded a
        /// digest for, keyed by path, and every path it named whether it vouched for it or not.
        /// Both are keyed with forward slashes, the way the note writes them.
        /// </summary>
        private static (Dictionary<string, string> Vouched, HashSet<string> Listed)
            NoteLoaderFiles(BepInExMarker marker)
        {
            var vouched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in marker?.Files ?? Enumerable.Empty<BepInExMarkerFileEntry>())
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Path)) continue;
                if (!BepInExIntegrity.IsLoaderIdentity(entry.Path)) continue;

                var path = entry.Path.Replace('\\', '/').TrimStart('/');
                listed.Add(path);

                if (!string.IsNullOrWhiteSpace(entry.Sha256)) vouched[path] = entry.Sha256.Trim();
            }

            return (vouched, listed);
        }

        /// <summary>
        /// Every loader-identity file an unpacked pack carries, keyed by where it would land
        /// under the install root.
        /// <para>
        /// The core is walked whole, subfolders and all, because that is exactly what the write
        /// copies. The loose files are the allow list's own, filtered to the ones that decide
        /// what loads: the changelog and the host's doorstop config are written too, and
        /// neither is part of the loader's identity here any more than it is anywhere else.
        /// </para>
        /// </summary>
        private static List<(string Path, string File)> PackLoaderFiles(string loaderRoot)
        {
            var found = new List<(string, string)>();
            if (string.IsNullOrWhiteSpace(loaderRoot)) return found;

            var core = System.IO.Path.Combine(loaderRoot, "BepInEx", "core");

            if (Directory.Exists(core))
            {
                foreach (var file in Directory.GetFiles(core, "*", SearchOption.AllDirectories))
                {
                    var relative = RelativeTo(loaderRoot, file);
                    if (BepInExIntegrity.IsLoaderIdentity(relative)) found.Add((relative, file));
                }
            }

            foreach (var fileName in RootFileAllowList)
            {
                if (!BepInExIntegrity.IsLoaderIdentity(fileName)) continue;

                var path = System.IO.Path.Combine(loaderRoot, fileName);
                if (File.Exists(path)) found.Add((fileName, path));
            }

            return found;
        }

        /// <summary>
        /// Where a path the note wrote would sit inside an unpacked pack, or null when it will
        /// not make a path at all.
        /// </summary>
        private static string PackPathOf(string loaderRoot, string relativePath)
        {
            try
            {
                return System.IO.Path.Combine(loaderRoot,
                    relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The note's file list read off the install itself, for a write that moved no loader
        /// bytes: an adoption that found the pack already there, or a restore that put an old
        /// core back.
        /// <para>
        /// The host's <c>doorstop_config.ini</c> is left out on purpose, and so is
        /// <c>BepInEx.cfg</c>. The note says which files THIS install put there, and neither of
        /// those was put there by it: the first is kept whatever happens and the second is only
        /// ever written when an install has none.
        /// </para>
        /// </summary>
        private static List<BepInExMarkerFileEntry> EntriesFromDisk(string baseDir)
        {
            var entries = new List<BepInExMarkerFileEntry>();

            foreach (var fileName in RootFileAllowList)
            {
                if (IsDoorstopConfig(fileName)) continue;

                var path = System.IO.Path.Combine(baseDir, fileName);
                if (File.Exists(path)) entries.Add(Entry(baseDir, path));
            }

            var coreDir = System.IO.Path.Combine(baseDir, "BepInEx", "core");
            if (Directory.Exists(coreDir))
                foreach (var file in Directory.GetFiles(coreDir, "*", SearchOption.AllDirectories))
                    entries.Add(Entry(baseDir, file));

            return entries;
        }

        /// <summary>
        /// Takes away the note that was set aside when ownership was given up. A write that
        /// finished means BakaLoader owns this install again, and a stale note left beside it
        /// would go on saying otherwise on every read.
        /// </summary>
        private static void DropStaleNote(string baseDir)
        {
            try
            {
                var aside = System.IO.Path.Combine(baseDir, BepInExMarkerFile.StaleFileName);
                if (File.Exists(aside)) File.Delete(aside);
            }
            catch { /* best effort */ }
        }

        /// <summary>How many times a note that will not write is tried again, and how long apart.</summary>
        private const int NoteWriteTries = 3;
        private static readonly TimeSpan NoteWriteWait = TimeSpan.FromMilliseconds(120);

        /// <summary>
        /// Writes the install note, trying again before it gives up.
        /// <para>
        /// The thing that stops this file is nearly always something holding it for a moment:
        /// an antivirus reading a file that just appeared, a backup tool, a file browser. The
        /// loader is already in by the time this runs, so the difference between a note that
        /// went down on the second try and one that never did is the difference between a
        /// finished write and an install the next read cannot make sense of. A few tries a
        /// tenth of a second apart cost nothing and cover the whole of that window.
        /// </para>
        /// </summary>
        private void WriteNote(string baseDir, BepInExMarker note)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    BepInExMarkerFile.Write(baseDir, note);
                    return;
                }
                catch (Exception e) when (attempt < NoteWriteTries)
                {
                    Logger?.Debug(
                        "The BepInEx note at {0} would not write ({1}); trying again.", baseDir, e.Message);
                    System.Threading.Thread.Sleep(NoteWriteWait);
                }
            }
        }

        /// <summary>
        /// Makes sure the note beside an install is one the files can be judged against, and
        /// answers whether it is now.
        /// <para>
        /// A note that still describes the files is left alone and the answer is yes, which is
        /// the ordinary case: a write mark over a whole core is nearly always a machine that
        /// died after both renames. A note that describes files a write has since replaced is
        /// TAKEN AWAY, because it is the lesser of two wrongs: an install with no note reads
        /// as one BakaLoader did not make, which one press puts right, and one whose note has
        /// stopped matching reads as one another tool has taken over, which nothing puts right
        /// on its own. A note that will not go leaves the answer no, and the mark stays in
        /// front of the comparison until the next read can try again.
        /// </para>
        /// </summary>
        private bool SettleNoteAgainstDisk(string baseDir)
        {
            try
            {
                var note = System.IO.Path.Combine(baseDir, BepInExMarkerFile.FileName);
                if (!File.Exists(note)) return true;

                // Asked before anything is read out of it. A note something is still holding
                // cannot be judged and cannot be taken away either, and BepInExMarkerFile.Read
                // answers null for one it could not open exactly as it does for one that is
                // not there: reading that as "no note, nothing to settle" would clear the mark
                // and hand the next status read the comparison this is standing in front of.
                using (new FileStream(note, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

                var marker = BepInExMarkerFile.Read(baseDir);
                if (marker == null || !BepInExIntegrity.Against(baseDir, marker).Drifted) return true;

                File.Delete(note);
                Logger.Warning(
                    "BepInEx at {0} was written but its note could not be, so the old note has been taken "
                    + "away: it described the files that were replaced, and leaving it would have read as "
                    + "another tool having written here.", baseDir);
                return true;
            }
            catch (Exception e)
            {
                Logger.Debug(
                    "The BepInEx note at {0} could not be settled yet ({1}), so the write mark stands.",
                    baseDir, e.Message);
                return false;
            }
        }

        private static string RelativeTo(string baseDir, string file)
        {
            try
            {
                return System.IO.Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            }
            catch
            {
                return System.IO.Path.GetFileName(file);
            }
        }

        /// <summary>The hash of one file, lower case hex, or null when it will not read.</summary>
        public static string Sha256Of(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// True when writing the pack would move this install BACKWARDS. Both numbers are file
        /// versions of the framework's own assembly, so they are comparable; either one missing
        /// means there is nothing to compare and nothing to refuse.
        /// </summary>
        public static bool IsDowngrade(string installedCoreVersion, string packCoreVersion)
        {
            if (string.IsNullOrWhiteSpace(installedCoreVersion) || string.IsNullOrWhiteSpace(packCoreVersion))
                return false;

            return SemVer.Compare(installedCoreVersion, packCoreVersion) > 0;
        }

        /// <summary>
        /// The pack version out of an archive whose link the host supplied, read from the
        /// changelog line the pack's own build writes. Null when it is not there, which is
        /// fine: the note then says the version is unknown rather than inventing one.
        /// </summary>
        private static string VersionFromArchive(string loaderRoot)
        {
            try
            {
                var changelog = System.IO.Path.Combine(loaderRoot, "changelog.txt");
                if (!File.Exists(changelog)) return null;

                foreach (var line in File.ReadLines(changelog).Take(40))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        line, @"Thunderstore version to ([0-9]+(?:\.[0-9]+)+)");
                    if (match.Success) return match.Groups[1].Value;
                }
            }
            catch
            {
                // A changelog that will not read simply has no version in it.
            }

            return null;
        }

        private static BepInExInstallResult Skipped(BepInExStatus status, string reason,
            string packVersion = null, DateTime? eligibleUtc = null)
            => new()
            {
                Installed = status.Installed,
                Skipped = true,
                SkipReason = reason,
                Version = packVersion,
                Package = status.Package,
                PreviousVersion = status.PackVersion ?? status.CoreVersion,
                PreviousPackVersion = status.PackVersion,
                PreviousCoreVersion = status.CoreVersion,
                CoreVersion = status.CoreVersion,
                EligibleUtc = eligibleUtc,
            };

        /// <summary>True when a folder holds anything at all.</summary>
        private static bool HoldsSomething(string directory)
        {
            try { return Directory.EnumerateFileSystemEntries(directory).Any(); }
            catch { return false; }
        }

        /// <summary>
        /// True when a folder is a link to somewhere else. Reading it is safe; writing through
        /// it is not, because what is on the other end is somebody else's install.
        /// </summary>
        public static bool IsReparsePoint(string directory)
        {
            try
            {
                var info = new DirectoryInfo(directory);
                return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Takes away a backup folder that a failed write never put anything in.</summary>
        private static void DropEmptyBackup(BepInExBackup backup)
        {
            try
            {
                if (backup?.Folder == null || !Directory.Exists(backup.Folder)) return;
                if (HoldsSomething(backup.Folder)) return;

                Directory.Delete(backup.Folder);
            }
            catch { /* best effort */ }
        }

        // ------------------------------------------------------------------ isolation

        /// <summary>
        /// Every isolated install already provisioned from this base gets the BepInEx sharing
        /// it does not have yet. A profile provisioned before BepInEx existed has no BepInEx
        /// folder at all, because the copier only ever walks folders the base already carries,
        /// and without this it would stay that way forever and quietly run vanilla.
        /// </summary>
        private int LinkExistingIsolatedInstalls(string baseExePath, string baseDir)
        {
            var linked = 0;
            try
            {
                var exeName = System.IO.Path.GetFileName(baseExePath);
                var baseExe = System.IO.Path.Combine(baseDir, exeName ?? string.Empty);
                if (!File.Exists(baseExe)) baseExe = baseExePath;

                foreach (var install in Isolation.ManagedInstallDirectories(baseExe))
                {
                    try
                    {
                        if (Isolation.EnsureSharedBepInEx(baseExe, install)) linked++;
                    }
                    catch (Exception e)
                    {
                        Logger.Warning("Could not share BepInEx into the isolated install {0}: {1}",
                            install, e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warning("Could not look for isolated installs to share BepInEx into: {0}", e.Message);
            }

            return linked;
        }

        /// <summary>
        /// Brings the loose loader files in every isolated profile of this family back into
        /// step with the base, and answers how many files it had to write.
        /// <para>
        /// A profile provisioned on the SAME volume hard-links those files, so the base and the
        /// profile are one file on disk and a write through one is a write through both: there
        /// is nothing to do and the digest comparison below finds nothing to do. A profile on
        /// another volume, or one where the link could not be made, holds COPIES of them while
        /// its <c>BepInEx/core</c> still follows the base through a junction. After a write
        /// that profile runs the NEW core under the OLD winhttp.dll, which is a mismatch
        /// between the two halves of the same loader and the kind that starts a server with no
        /// mods and nothing in the log saying why.
        /// </para>
        /// <para>
        /// A profile whose core is its own real folder is left entirely alone. It is not part
        /// of this install's loader at all, and refreshing the file the game loads while the
        /// core it points at stays where it was would be the very mismatch this exists to
        /// stop. The host's own <c>doorstop_config.ini</c> follows section D here as well: it
        /// is theirs in a profile exactly as it is in the base, and it moves only on the write
        /// that had to replace the base's too.
        /// </para>
        /// </summary>
        private int RefreshProfileLoaderFiles(string baseExePath, string baseDir, bool doorstopReplaced)
        {
            var refreshed = 0;

            try
            {
                var exeName = System.IO.Path.GetFileName(baseExePath);
                var baseExe = System.IO.Path.Combine(baseDir, exeName ?? string.Empty);
                if (!File.Exists(baseExe)) baseExe = baseExePath;

                foreach (var install in Isolation.ManagedInstallDirectories(baseExe))
                {
                    try
                    {
                        // The junction is what says this profile runs the base's loader. Without
                        // it there is nothing here to keep in step.
                        if (!IsReparsePoint(System.IO.Path.Combine(install, "BepInEx", "core"))) continue;

                        foreach (var fileName in RootFileAllowList)
                        {
                            if (IsDoorstopConfig(fileName) && !doorstopReplaced) continue;

                            var source = System.IO.Path.Combine(baseDir, fileName);
                            var destination = System.IO.Path.Combine(install, fileName);

                            // Only a file the profile already has. One it lacks is the linking
                            // pass's to put there, which has just run and knows how to link it.
                            if (!File.Exists(source) || !File.Exists(destination)) continue;

                            var here = Sha256Of(source);
                            var there = Sha256Of(destination);

                            // A file neither can be read is not a file to overwrite on a guess.
                            if (here == null || there == null) continue;
                            if (string.Equals(here, there, StringComparison.OrdinalIgnoreCase)) continue;

                            CopyInPlace(source, destination);
                            refreshed++;
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Warning("Could not refresh the loader files in the isolated install {0}: {1}",
                            install, e.Message);
                    }
                }

                if (refreshed > 0)
                    Logger.Information(
                        "{0} loader file(s) in isolated profiles were brought back into step with {1}.",
                        refreshed, baseDir);
            }
            catch (Exception e)
            {
                Logger.Warning("Could not look for isolated installs to refresh the loader files in: {0}",
                    e.Message);
            }

            return refreshed;
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>
        /// What the site says about the pack that would go in: which version, what it weighs,
        /// when it was published, and whether the package has been marked deprecated.
        /// </summary>
        private sealed class ListedBuild
        {
            public string Version { get; init; }
            public long? FileSize { get; init; }
            public string Sha256 { get; init; }

            /// <summary>When that version was published, or null when neither answer said.</summary>
            public DateTime? DateCreated { get; init; }

            /// <summary>True only when a listing actually said the package is deprecated.</summary>
            public bool Deprecated { get; init; }

            /// <summary>
            /// True only when a listing actually said this version is no longer served
            /// (<c>is_active</c> false), which is what a version somebody has taken down
            /// reads as. A listing that said nothing is not one of these.
            /// </summary>
            public bool Pulled { get; init; }

            /// <summary>True when the size came from the community index rather than the package page.</summary>
            public bool SizeFromIndex { get; init; }
        }

        /// <summary>
        /// The pack to fetch, asked of the package's own page first because that is never
        /// behind, and of the community index for whatever the page left out.
        /// <para>
        /// The page answers with no size and no digest. That is not an oversight this can work
        /// around by trusting the download: it means the size the download IS checked against
        /// has to come from somewhere, and the community index carries one per version. It is
        /// only taken when the index's newest version is the very version the page named,
        /// because a size belongs to one archive and a size from a different build would turn
        /// every download into a failed integrity check.
        /// </para>
        /// </summary>
        private async Task<ListedBuild> ResolveListingAsync(string owner, string name)
        {
            Tools.Models.ThunderstorePackage package = null;

            try
            {
                var live = await Thunderstore.LookupLiveAsync(owner, name);
                package = live?.Package;
            }
            catch (Exception e)
            {
                Logger.Debug("The BepInEx package endpoint did not answer: {0}", e.Message);
            }

            Tools.Models.ThunderstorePackage indexed = null;
            if (package?.Latest == null)
            {
                try { indexed = await Thunderstore.GetLatestAsync(owner, name); }
                catch (Exception e) { Logger.Debug("The package index did not answer either: {0}", e.Message); }
                package = indexed;
            }

            var version = package?.LatestVersion;
            if (string.IsNullOrWhiteSpace(version))
                throw new HostFacingException("bepinex.offline",
                    "Thunderstore did not answer, so BepInEx could not be fetched.");

            var size = package.Latest?.FileSize;
            var sha = package.Latest?.Sha256;
            var created = package.Latest?.DateCreated;
            var deprecated = package.IsDeprecated == true;
            // Only a listing that actually said no. IsActive answers true for one that said
            // nothing, so this is false unless the site really has taken the version down.
            var pulled = package.Latest != null && !package.Latest.IsActive;
            var fromIndex = false;

            // The page said which version, and left out what it weighs. The index knows.
            if (size == null && indexed == null)
            {
                try { indexed = await Thunderstore.GetLatestAsync(owner, name); }
                catch (Exception e) { Logger.Debug("The package index did not answer either: {0}", e.Message); }
            }

            if (size == null && indexed != null
                && string.Equals(indexed.LatestVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                size = indexed.Latest?.FileSize;
                sha ??= indexed.Latest?.Sha256;
                created ??= indexed.Latest?.DateCreated;
                deprecated = deprecated || indexed.IsDeprecated == true;
                pulled = pulled || (indexed.Latest != null && !indexed.Latest.IsActive);
                fromIndex = size != null;
            }

            return new ListedBuild
            {
                Version = version,
                FileSize = size,
                Sha256 = sha,
                DateCreated = created,
                Deprecated = deprecated,
                Pulled = pulled,
                SizeFromIndex = fromIndex,
            };
        }

        private static (string Owner, string Name) SplitPackage(string package)
        {
            if (string.IsNullOrWhiteSpace(package)) return (DefaultPackageOwner, DefaultPackageName);

            var at = package.IndexOf('-');
            if (at <= 0 || at >= package.Length - 1) return (DefaultPackageOwner, DefaultPackageName);

            return (package.Substring(0, at), package.Substring(at + 1));
        }

        private static void Report(IProgress<BepInExProgress> progress, string phase, int percent, string version)
            => progress?.Report(new BepInExProgress { Phase = phase, Percent = percent, Version = version });

        /// <summary>
        /// Streams an archive to a file, stopping the moment it passes the cap. Answers how
        /// many bytes were written.
        /// </summary>
        private async Task<long> DownloadAsync(Uri url, string destinationPath, CancellationToken cancellationToken)
        {
            using var client = HttpClientProvider.CreateClient();
            client.Timeout = DownloadTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimBakaLoader");

            var address = url;
            HttpResponseMessage response = null;

            try
            {
                for (var hop = 0; ; hop++)
                {
                    response?.Dispose();
                    response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    var location = IsRedirect(response) ? response.Headers.Location : null;
                    if (location == null) break;

                    if (hop >= MaxDownloadRedirects)
                        throw new IOException("That download redirected too many times.");

                    address = location.IsAbsoluteUri ? location : new Uri(address, location);
                }

                response.EnsureSuccessStatusCode();

                var cap = MaxDownloadBytes;
                if (response.Content.Headers.ContentLength is { } declared && cap > 0 && declared > cap)
                    throw new HostFacingException("bepinex.tooLarge",
                        "That download is larger than BakaLoader will fetch for a loader.",
                        ("limit", cap));

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = File.Create(destinationPath);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    total += read;
                    if (cap > 0 && total > cap)
                        throw new HostFacingException("bepinex.tooLarge",
                            "That download is larger than BakaLoader will fetch for a loader.",
                            ("limit", cap));

                    await destination.WriteAsync(buffer, 0, read, cancellationToken);
                }

                return total;
            }
            finally
            {
                response?.Dispose();
            }
        }

        private static bool IsRedirect(HttpResponseMessage response)
        {
            var status = (int)response.StatusCode;
            return status is 301 or 302 or 303 or 307 or 308;
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, System.IO.Path.Combine(destinationDir, System.IO.Path.GetFileName(file)), overwrite: true);

            foreach (var dir in Directory.GetDirectories(sourceDir))
                CopyDirectory(dir, System.IO.Path.Combine(destinationDir, System.IO.Path.GetFileName(dir)));
        }

        private void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) { Logger.Debug("Could not delete the temp archive {0}: {1}", path, e.Message); }
        }

        private void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception e) { Logger.Debug("Could not delete the temp folder {0}: {1}", path, e.Message); }
        }
    }
}
