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
    }

    /// <summary>One server profile and the install it runs from, as the guard reads them.</summary>
    public sealed class BepInExProfileInstall
    {
        public string ProfileName { get; init; }

        public string ServerExePath { get; init; }

        /// <summary>True when that profile's server is up right now.</summary>
        public bool Running { get; init; }
    }

    /// <summary>What BakaLoader can say about the BepInEx in one base install.</summary>
    public sealed class BepInExStatus
    {
        /// <summary>The base install root the answer is about.</summary>
        public string BaseFolder { get; init; }

        /// <summary>True when BepInEx/core/BepInEx.dll is there.</summary>
        public bool Installed { get; init; }

        /// <summary>True when BakaLoader's own install note is at the root.</summary>
        public bool MaintainedByBakaLoader { get; init; }

        /// <summary>The Thunderstore pack version, from the note. Null without one.</summary>
        public string PackVersion { get; init; }

        /// <summary>The file version of BepInEx.dll, read when there is no note.</summary>
        public string CoreFileVersion { get; init; }

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

    /// <summary>What an install came to.</summary>
    public sealed class BepInExInstallResult
    {
        public bool Installed { get; init; }

        /// <summary>True when there was a BepInEx here already and this replaced it.</summary>
        public bool Replaced { get; init; }

        public string Version { get; init; }

        public string Package { get; init; }

        public string Source { get; init; }

        /// <summary>The version that was here before, when there was one.</summary>
        public string PreviousVersion { get; init; }

        /// <summary>How many already-provisioned isolated installs were given the sharing they lacked.</summary>
        public int IsolatedInstallsLinked { get; init; }
    }

    public interface IBepInExService
    {
        /// <summary>
        /// What BakaLoader can say about the BepInEx in one base install. Reads only.
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
            CancellationToken cancellationToken = default);

        /// <summary>
        /// The same write, for the package the install note names rather than the default, and
        /// with the same twice-asked <paramref name="profiles"/>.
        /// </summary>
        Task<BepInExInstallResult> UpdateAsync(string baseExePath,
            IEnumerable<BepInExProfileInstall> profiles,
            IProgress<BepInExProgress> progress = null,
            CancellationToken cancellationToken = default);

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

        /// <summary>The one thing that proves an archive is a loader and not a mod.</summary>
        public const string CoreAssemblyName = "BepInEx.dll";

        /// <summary>The config the pack ships, written only when the install has none.</summary>
        public const string ShippedConfigName = "BepInEx.cfg";

        /// <summary>Where a replaced core is kept, under BepInEx so no scan ever reads it.</summary>
        public const string BackupDirName = ".bakaloader-bepinex-backups";

        /// <summary>
        /// How many replaced cores are kept. Each one is a full copy of the old core, and with
        /// maintenance on the update runs unattended, so without a number here the folder grows
        /// by a couple of megabytes per pack bump forever in a place no scan reads and nobody
        /// looks. Three is enough to step back from an update that went wrong and past the one
        /// before it; older than that and the pack it came from is long gone from Thunderstore's
        /// current listing anyway.
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
            if (!File.Exists(System.IO.Path.Combine(root, "winhttp.dll"))) return null;
            if (!File.Exists(System.IO.Path.Combine(root, "BepInEx", "core", CoreAssemblyName))) return null;

            return root;
        }

        /// <summary>The address of one named build, built by BakaLoader rather than accepted.</summary>
        public static string ConstructedDownloadUrl(string owner, string name, string version) =>
            $"https://thunderstore.io/package/download/{owner}/{name}/{version}/";

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

            var core = System.IO.Path.Combine(baseDir, "BepInEx", "core", CoreAssemblyName);
            var installed = File.Exists(core);
            var marker = BepInExMarkerFile.Read(baseDir);

            return new BepInExStatus
            {
                BaseFolder = baseDir,
                Installed = installed,
                MaintainedByBakaLoader = marker != null,
                PackVersion = marker?.Version,
                Package = marker?.Package,
                Source = marker?.Source,
                InstalledUtc = marker?.InstalledUtc,
                CoreFileVersion = marker == null && installed ? FileVersionOf(core) : null,
                WrongLocationFolder = WrongLocationFolderIn(pluginsDir),
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
            CancellationToken cancellationToken = default)
            => InstallCoreAsync(baseExePath, url, null, profiles, progress, cancellationToken);

        public Task<BepInExInstallResult> UpdateAsync(string baseExePath,
            IEnumerable<BepInExProfileInstall> profiles,
            IProgress<BepInExProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            var baseDir = InstallRootOf(baseExePath);
            var package = BepInExMarkerFile.Read(baseDir)?.Package;
            return InstallCoreAsync(baseExePath, null, package, profiles, progress, cancellationToken);
        }

        private async Task<BepInExInstallResult> InstallCoreAsync(
            string baseExePath, string url, string package,
            IEnumerable<BepInExProfileInstall> profiles,
            IProgress<BepInExProgress> progress,
            CancellationToken cancellationToken)
        {
            var baseDir = InstallRootOf(baseExePath);
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
                throw new HostFacingException("bepinex.noServerPath",
                    "Set a valid server .exe path before installing BepInEx.");

            // Asked before a byte is fetched. Writing BepInEx means writing through the
            // junctions and hard links every other install on this base shares, and a server
            // that is up has those files mapped.
            var blocked = ProfilesBlockingWrite(baseExePath, profiles);
            if (blocked.Count > 0)
                throw new HostFacingException("bepinex.serversRunning",
                    "BakaLoader counts these servers as loading this same BepInEx, so nothing is written "
                    + "while one of them is up. Still up: " + string.Join(", ", blocked),
                    ("names", string.Join(", ", blocked)));

            var (owner, name) = SplitPackage(package);
            Report(progress, "resolving", 2, null);

            string version = null;
            long? declaredSize = null;
            string declaredSha = null;
            string address;

            if (string.IsNullOrWhiteSpace(url))
            {
                var listed = await ResolveListingAsync(owner, name);
                version = listed.Version;
                declaredSize = listed.FileSize;
                declaredSha = listed.Sha256;
                address = ConstructedDownloadUrl(owner, name, version);
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
                    throw new HostFacingException("bepinex.offline",
                        "BepInEx could not be fetched: " + e.Message, ("detail", e.Message));
                }

                Report(progress, "checking", 45, version);

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

                Report(progress, "installing", 70, version);

                // Asked AGAIN, here, with the archive on disk and not one byte written yet.
                // The first ask was before the download, and a fifty megabyte fetch on a slow
                // line is minutes: a host who started a realm in that window would otherwise
                // have its BepInEx/core cleared and rewritten underneath it. The caller's
                // sequence is re-enumerated for this, which is why the two entry points say so.
                blocked = ProfilesBlockingWrite(baseExePath, profiles);
                if (blocked.Count > 0)
                    throw new HostFacingException("bepinex.serversRunning",
                        "BakaLoader counts these servers as loading this same BepInEx, so nothing is written "
                        + "while one of them is up. Still up: " + string.Join(", ", blocked),
                        ("names", string.Join(", ", blocked)));

                var before = BepInExMarkerFile.Read(baseDir)?.Version;
                var replaced = File.Exists(System.IO.Path.Combine(baseDir, "BepInEx", "core", CoreAssemblyName));

                var entries = CopyByAllowList(loaderRoot, baseDir);

                BepInExMarkerFile.Write(baseDir, new BepInExMarker
                {
                    Schema = BepInExMarkerFile.CurrentSchema,
                    Writer = BepInExMarkerFile.WriterPrefix + " " + AssemblyHelper.GetApplicationVersion(),
                    Package = owner + "-" + name,
                    Version = version ?? VersionFromArchive(loaderRoot) ?? "unknown",
                    InstalledUtc = DateTime.UtcNow,
                    Source = address,
                    Files = entries,
                });

                Report(progress, "linking", 90, version);
                var linked = LinkExistingIsolatedInstalls(baseExePath, baseDir);

                Report(progress, "done", 100, version);

                Logger.Information("BepInEx {0} written into {1} ({2} file(s), {3} isolated install(s) linked).",
                    version ?? "from a link", baseDir, entries.Count, linked);

                return new BepInExInstallResult
                {
                    Installed = true,
                    Replaced = replaced,
                    Version = version ?? VersionFromArchive(loaderRoot),
                    Package = owner + "-" + name,
                    Source = address,
                    PreviousVersion = before,
                    IsolatedInstallsLinked = linked,
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

            var blocked = ProfilesBlockingWrite(baseExePath, profiles);
            if (blocked.Count > 0)
                throw new HostFacingException("bepinex.serversRunning",
                    "BakaLoader counts these servers as loading this same BepInEx, so nothing is written "
                    + "while one of them is up. Still up: " + string.Join(", ", blocked),
                    ("names", string.Join(", ", blocked)));

            Directory.Delete(wrong, recursive: true);
            Logger.Information("Removed the mis-placed BepInEx folder at {0}.", wrong);
            return Task.FromResult(true);
        }

        // ------------------------------------------------------------------ the copy itself

        /// <summary>
        /// Writes the allow list out of the pack's own folder into the install root, and
        /// answers what it wrote.
        /// <para>
        /// The loose root files are copied IN PLACE, never deleted first: on an install that
        /// has provisioned isolated profiles those files are hard links, and deleting one
        /// would break the link and leave every other profile on a stale loader. A copy over
        /// the top rewrites the shared content, which is exactly what sharing is for.
        /// </para>
        /// <para>
        /// The core folder is the other way round, because a core carrying a file the new pack
        /// does not ship would load it: back it up, clear it, copy. If the copy falls over
        /// half way the backup goes straight back, so a failed update never leaves an install
        /// that cannot start.
        /// </para>
        /// </summary>
        private List<BepInExMarkerFileEntry> CopyByAllowList(string loaderRoot, string baseDir)
        {
            var written = new List<BepInExMarkerFileEntry>();

            foreach (var fileName in RootFileAllowList)
            {
                var source = System.IO.Path.Combine(loaderRoot, fileName);
                if (!File.Exists(source)) continue;

                var destination = System.IO.Path.Combine(baseDir, fileName);
                CopyInPlace(source, destination);
                written.Add(Entry(baseDir, destination));
            }

            var sourceCore = System.IO.Path.Combine(loaderRoot, "BepInEx", "core");
            var destinationCore = System.IO.Path.Combine(baseDir, "BepInEx", "core");
            string backup = null;

            if (Directory.Exists(sourceCore))
            {
                if (Directory.Exists(destinationCore) && Directory.EnumerateFileSystemEntries(destinationCore).Any())
                    backup = BackupCore(baseDir, destinationCore);

                try
                {
                    Directory.CreateDirectory(destinationCore);
                    ClearDirectory(destinationCore);

                    // Every file under the pack's core, subfolders and all. BepInEx 5's core is
                    // flat today, so this changes nothing about the pack that ships now; what it
                    // stops is a pack that grows a folder (a net472 tier, a runtimes tier)
                    // installing a core missing those assemblies while the note beside it claims
                    // a complete write. "The core is replaced whole" has to be what the code does.
                    foreach (var file in Directory.GetFiles(sourceCore, "*", SearchOption.AllDirectories))
                    {
                        var relative = System.IO.Path.GetRelativePath(sourceCore, file);
                        var destination = System.IO.Path.Combine(destinationCore, relative);

                        var folder = System.IO.Path.GetDirectoryName(destination);
                        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

                        CopyCoreFile(file, destination);
                        written.Add(Entry(baseDir, destination));
                    }
                }
                catch (Exception e)
                {
                    if (backup != null)
                    {
                        Logger.Warning("Writing the BepInEx core failed ({0}); putting the old one back.", e.Message);
                        try
                        {
                            ClearDirectory(destinationCore);
                            CopyDirectory(backup, destinationCore);
                        }
                        catch (Exception restore)
                        {
                            Logger.Error(restore, "Could not put the old BepInEx core back.");
                        }
                    }
                    else
                    {
                        // A FIRST install has no previous core to put back, and half a core is
                        // worse than no core: BepInEx.dll present with its preloader missing is
                        // an install the loader looks into and starts nothing out of, with
                        // nothing on disk saying why. Leave the folder as this found it.
                        Logger.Warning("Writing the BepInEx core failed ({0}); clearing the half-written core.",
                            e.Message);
                        try { ClearDirectory(destinationCore); }
                        catch (Exception clear) { Logger.Error(clear, "Could not clear the half-written core."); }
                    }
                    throw;
                }
            }

            // Gale's rule, and the right one: the pack ships a default config, and r2modman
            // overwrites a host's edited one with it on every update. Only write it when the
            // install has none.
            var sourceCfg = System.IO.Path.Combine(loaderRoot, "BepInEx", "config", ShippedConfigName);
            var destinationCfg = System.IO.Path.Combine(baseDir, "BepInEx", "config", ShippedConfigName);
            if (File.Exists(sourceCfg) && !File.Exists(destinationCfg))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destinationCfg));
                File.Copy(sourceCfg, destinationCfg, overwrite: false);
                written.Add(Entry(baseDir, destinationCfg));
            }

            return written;
        }

        /// <summary>
        /// One file of the core, copied. Virtual for one reason: the half-written core is
        /// the failure that has to put the old one back, and there is no way to make
        /// <see cref="File.Copy(string,string,bool)"/> fall over half way from the outside.
        /// A test overrides this and the restore path is driven for real rather than
        /// reasoned about.
        /// </summary>
        protected virtual void CopyCoreFile(string source, string destination)
            => File.Copy(source, destination, overwrite: true);

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

        private static string BackupCore(string baseDir, string core)
        {
            var root = System.IO.Path.Combine(baseDir, "BepInEx", BackupDirName, "core");
            Directory.CreateDirectory(root);

            var backup = System.IO.Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var n = 2;
            while (Directory.Exists(backup)) backup = System.IO.Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + n++);

            CopyDirectory(core, backup);
            PruneCoreBackups(root, backup);
            return backup;
        }

        /// <summary>
        /// Keeps the newest <see cref="KeptCoreBackups"/> copies under the core backup folder and
        /// deletes the rest. The one just written is never a candidate, whatever its name sorts
        /// as. Best effort throughout: a backup that will not delete is a tidiness problem, never
        /// a reason to fail an install that has already succeeded.
        /// </summary>
        private static void PruneCoreBackups(string root, string keep)
        {
            try
            {
                var existing = Directory.GetDirectories(root)
                    .Where(d => !string.Equals(d, keep, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => new DirectoryInfo(d).Name, StringComparer.OrdinalIgnoreCase)
                    .Skip(System.Math.Max(0, KeptCoreBackups - 1))
                    .ToList();

                foreach (var old in existing)
                {
                    try { Directory.Delete(old, recursive: true); }
                    catch { /* a backup that will not go is not worth failing an install over */ }
                }
            }
            catch { /* best effort */ }
        }

        private static BepInExMarkerFileEntry Entry(string baseDir, string file) => new()
        {
            Path = RelativeTo(baseDir, file),
            Sha256 = Sha256Of(file),
        };

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

        // ------------------------------------------------------------------ plumbing

        private sealed class ListedBuild
        {
            public string Version { get; init; }
            public long? FileSize { get; init; }
            public string Sha256 { get; init; }
        }

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

            if (package?.Latest == null)
            {
                try { package = await Thunderstore.GetLatestAsync(owner, name); }
                catch (Exception e) { Logger.Debug("The package index did not answer either: {0}", e.Message); }
            }

            var version = package?.LatestVersion;
            if (string.IsNullOrWhiteSpace(version))
                throw new HostFacingException("bepinex.offline",
                    "Thunderstore did not answer, so BepInEx could not be fetched.");

            return new ListedBuild
            {
                Version = version,
                FileSize = package.Latest?.FileSize,
                Sha256 = package.Latest?.Sha256,
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

        private static void ClearDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;

            foreach (var file in Directory.GetFiles(directory))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
                File.Delete(file);
            }
            foreach (var dir in Directory.GetDirectories(directory))
            {
                Directory.Delete(dir, recursive: true);
            }
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
