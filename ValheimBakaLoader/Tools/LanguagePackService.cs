using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>Where a pack download has got to. The page reads these names.</summary>
    public static class LanguagePackPhases
    {
        public const string Resolving = "resolving";
        public const string Downloading = "downloading";
        public const string Verifying = "verifying";
        public const string Installing = "installing";
        public const string Done = "done";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }

    /// <summary>
    /// Why a pack download ended the way it did, as a stable id rather than a sentence.
    /// <para>
    /// Every one of these is a different thing to tell the host, which is the whole reason
    /// they are separate: "the download stopped receiving data" and "you cancelled the
    /// download" are the same ending to the code and two different sentences to a person.
    /// The English for each lives in the catalog under the same id.
    /// </para>
    /// </summary>
    public static class LanguagePackReasons
    {
        /// <summary>Another pack is already downloading. One at a time, app wide.</summary>
        public const string Busy = "lang.reason.busy";

        /// <summary>No bytes arrived for the stall window, so the download was let go.</summary>
        public const string Stalled = "lang.reason.stalled";

        /// <summary>The host stopped it, and nothing on disk changed.</summary>
        public const string Cancelled = "lang.reason.cancelled";

        /// <summary>The bytes did not weigh or hash what the manifest published.</summary>
        public const string Integrity = "lang.reason.integrity";

        /// <summary>The pack is bigger than this app will fetch.</summary>
        public const string TooLarge = "lang.reason.tooLarge";

        /// <summary>The pack names an app newer than this one.</summary>
        public const string TooOld = "lang.reason.tooOld";

        /// <summary>The release page could not be reached at all.</summary>
        public const string Offline = "lang.reason.offline";

        /// <summary>The manifest carries no pack for this language.</summary>
        public const string NoPack = "lang.reason.noPack";

        /// <summary>A code that is not one of the app's languages.</summary>
        public const string UnknownCode = "lang.reason.unknownCode";

        /// <summary>English ships inside the app and is never downloaded.</summary>
        public const string BuiltIn = "lang.reason.builtIn";

        /// <summary>An unattended fetch, with update checking turned off.</summary>
        public const string ChecksOff = "lang.reason.checksOff";

        /// <summary>The zip opened but is not a language pack: a file missing, or the wrong language.</summary>
        public const string Contents = "lang.reason.contents";

        /// <summary>Everything checked out and the folder still could not be put in place.</summary>
        public const string Install = "lang.reason.install";
    }

    /// <summary>
    /// What the quiet post-update fetch did, as the word the page shows beside the status.
    /// </summary>
    public static class LanguageQuietFetch
    {
        /// <summary>Nothing needed doing: English, or the pack for this version is already here.</summary>
        public const string None = "none";

        /// <summary>A fetch was wanted and update checking is off, so nothing was asked of anybody.</summary>
        public const string SkippedChecksOff = "skipped:checksOff";

        /// <summary>A fetch was wanted and a download was already running.</summary>
        public const string SkippedBusy = "skipped:busy";

        /// <summary>A fetch was started. It says nothing about whether it worked.</summary>
        public const string Started = "started";
    }

    /// <summary>One push from a pack download, shaped like every other byte progress in the app.</summary>
    public sealed class LanguagePackProgress
    {
        public string Code { get; set; }

        /// <summary>One of <see cref="LanguagePackPhases"/>.</summary>
        public string Phase { get; set; }

        /// <summary>Minus one while there is no number yet, which the bar reads as indeterminate.</summary>
        public int Percent { get; set; } = -1;

        public long BytesDone { get; set; }

        public long BytesTotal { get; set; }

        /// <summary>The reason id on a failed or cancelled push, otherwise null.</summary>
        public string MessageId { get; set; }

        /// <summary>The values that sentence interpolates, by name. Null when it names none.</summary>
        public IReadOnlyDictionary<string, object> MessageParams { get; set; }
    }

    /// <summary>How a pack download ended. Every ending is one of these; nothing throws.</summary>
    public sealed class LanguagePackResult
    {
        public bool Ok { get; set; }

        public bool Cancelled { get; set; }

        public string Code { get; set; }

        /// <summary>The app version the pack was cut for, which is the folder it landed in.</summary>
        public string AppVersion { get; set; }

        /// <summary>Where it landed, when it landed.</summary>
        public string Folder { get; set; }

        /// <summary>Why not, when not. One of <see cref="LanguagePackReasons"/>.</summary>
        public string ReasonId { get; set; }

        public IReadOnlyDictionary<string, object> ReasonParams { get; set; }
    }

    /// <summary>A pack that is on disk right now, read from its own pack.json.</summary>
    public sealed class LanguagePackInstall
    {
        public string Code { get; set; }

        public string Version { get; set; }

        public int Catalog { get; set; }

        public int Keys { get; set; }

        public int Translated { get; set; }

        /// <summary>"machine" or "reviewed", as the pack describes itself.</summary>
        public string Status { get; set; }

        public string Folder { get; set; }

        /// <summary>True when this pack was cut for the version of the app that is running.</summary>
        public bool MatchesApp { get; set; }

        /// <summary>The faces this pack uses, already pointed at the shared font store.</summary>
        public IReadOnlyList<LanguagePackFont> Fonts { get; set; }
    }

    /// <summary>One row of the globe menu, before the page draws it.</summary>
    public sealed class LanguagePackEntry
    {
        public string Code { get; set; }

        public string NativeName { get; set; }

        public string EnglishName { get; set; }

        public string Bcp47 { get; set; }

        public string FontStackKey { get; set; }

        /// <summary>True for English, which ships inside the app.</summary>
        public bool BuiltIn { get; set; }

        /// <summary>True when any version of this pack is on disk.</summary>
        public bool Installed { get; set; }

        public string InstalledVersion { get; set; }

        /// <summary>True when the installed pack was cut for the running app version.</summary>
        public bool MatchesApp { get; set; }

        /// <summary>True when the manifest names a pack for this language.</summary>
        public bool Available { get; set; }

        /// <summary>The published size of the pack, from the manifest. Zero when unknown.</summary>
        public long Bytes { get; set; }

        public int Catalog { get; set; }

        public int Keys { get; set; }

        public int Translated { get; set; }

        public string Status { get; set; }

        public string MinAppVersion { get; set; }
    }

    /// <summary>Where the manifest came from, so the menu can say what it knows and what it does not.</summary>
    public sealed class LanguageManifestState
    {
        public bool Ok { get; set; }

        public bool FromCache { get; set; }

        public DateTime? CheckedUtc { get; set; }

        /// <summary>A reason id when it could not be read, otherwise null.</summary>
        public string ErrorId { get; set; }

        /// <summary>The release tag the manifest was published on.</summary>
        public string ReleaseTag { get; set; }

        /// <summary>The app version the manifest was cut for.</summary>
        public string AppVersion { get; set; }
    }

    /// <summary>Everything the globe menu needs in one answer.</summary>
    public sealed class LanguageCatalogListing
    {
        /// <summary>The version of the app that is running.</summary>
        public string AppVersion { get; set; }

        public IReadOnlyList<LanguagePackEntry> Languages { get; set; }

        public LanguageManifestState Manifest { get; set; }
    }

    /// <summary>One face inside a pack.</summary>
    public sealed class LanguagePackFont
    {
        /// <summary>
        /// Inside a pack zip this is the path in the archive ("fonts/Name.woff2"). Once the
        /// pack is installed it is the path in the shared store ("_fonts/&lt;sha256&gt;.woff2"),
        /// relative to the languages folder, which is exactly what the page's /lang/ URLs are
        /// built from.
        /// </summary>
        [JsonProperty("file")] public string File { get; set; }

        [JsonProperty("family")] public string Family { get; set; }

        [JsonProperty("weight")] public string Weight { get; set; }

        [JsonProperty("style")] public string Style { get; set; }

        [JsonProperty("unicodeRange", NullValueHandling = NullValueHandling.Ignore)]
        public string UnicodeRange { get; set; }

        [JsonProperty("sha256")] public string Sha256 { get; set; }
    }

    /// <summary>
    /// pack.json: what a pack says about itself, carried inside the zip so an installed
    /// folder can be read without the release it came from.
    /// </summary>
    public class LanguagePackFile
    {
        [JsonProperty("schema")] public int Schema { get; set; } = 1;

        [JsonProperty("code")] public string Code { get; set; }

        /// <summary>The app version this pack was cut for.</summary>
        [JsonProperty("appVersion")] public string AppVersion { get; set; }

        [JsonProperty("asset")] public string Asset { get; set; }

        [JsonProperty("catalog")] public int Catalog { get; set; }

        [JsonProperty("nativeName")] public string NativeName { get; set; }

        [JsonProperty("englishName")] public string EnglishName { get; set; }

        [JsonProperty("keys")] public int Keys { get; set; }

        [JsonProperty("translated")] public int Translated { get; set; }

        /// <summary>"machine" or "reviewed".</summary>
        [JsonProperty("status")] public string Status { get; set; }

        [JsonProperty("minAppVersion")] public string MinAppVersion { get; set; }

        [JsonProperty("fonts")] public List<LanguagePackFont> Fonts { get; set; }

        /// <summary>
        /// The licence files that came with the fonts, in the shared store. Written when the
        /// pack is installed; absent inside the zip, where the licence sits at fonts/OFL.txt.
        /// </summary>
        [JsonProperty("licenses", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Licenses { get; set; }
    }

    /// <summary>
    /// One language in lang-manifest.json: everything pack.json says, plus the three things
    /// that describe the zip rather than its contents.
    /// </summary>
    public sealed class LanguageManifestEntry : LanguagePackFile
    {
        /// <summary>
        /// The address the bytes come from, used exactly as published. The app never builds
        /// one: a name spelled "v1.2.0" in one place and "1.2.0" in another is a silent 404
        /// that reads to the host as "that language does not exist".
        /// </summary>
        [JsonProperty("url")] public string Url { get; set; }

        [JsonProperty("bytes")] public long Bytes { get; set; }

        [JsonProperty("sha256")] public string Sha256 { get; set; }
    }

    /// <summary>lang-manifest.json, one per release.</summary>
    public sealed class LanguageManifest
    {
        [JsonProperty("schema")] public int Schema { get; set; }

        [JsonProperty("appVersion")] public string AppVersion { get; set; }

        [JsonProperty("generatedUtc")] public DateTime GeneratedUtc { get; set; }

        [JsonProperty("catalog")] public int Catalog { get; set; }

        [JsonProperty("languages")] public List<LanguageManifestEntry> Languages { get; set; }
    }

    /// <summary>The held copy of the last manifest read, with what it takes to ask again cheaply.</summary>
    public sealed class LanguageManifestCache
    {
        [JsonProperty("schema")] public int Schema { get; set; } = 1;

        [JsonProperty("url")] public string Url { get; set; }

        [JsonProperty("etag")] public string ETag { get; set; }

        [JsonProperty("fetchedUtc")] public DateTime FetchedUtc { get; set; }

        [JsonProperty("releaseTag")] public string ReleaseTag { get; set; }

        /// <summary>The app version that was running when this was fetched.</summary>
        [JsonProperty("appVersion")] public string AppVersion { get; set; }

        [JsonProperty("body")] public LanguageManifest Body { get; set; }
    }

    /// <summary>Raised when the interface language actually changed, so every open window follows.</summary>
    public sealed class LanguageChangedEventArgs : EventArgs
    {
        public string Code { get; init; }

        public string Version { get; init; }
    }

    public interface ILanguagePackService
    {
        /// <summary>
        /// The most a pack may weigh. Settable so the suite can prove the cap with a small
        /// file, the way MaxHexiumDownloadBytes is.
        /// </summary>
        long MaxLanguagePackBytes { get; set; }

        /// <summary>True while a pack is being fetched. One at a time, across the whole app.</summary>
        bool IsBusy { get; }

        /// <summary>Raised after a language actually changed, for every window to follow.</summary>
        event EventHandler<LanguageChangedEventArgs> LanguageChanged;

        /// <summary>
        /// Every language the app knows, merged with what is on disk and with the manifest.
        /// Never throws: an offline machine still gets a complete, usable list.
        /// </summary>
        Task<LanguageCatalogListing> ListAsync(CancellationToken ct = default);

        /// <summary>The pack for the running app version, or null.</summary>
        LanguagePackInstall Installed(string code);

        /// <summary>The newest pack on disk for this language whatever version it was cut for, or null.</summary>
        LanguagePackInstall InstalledAny(string code);

        /// <summary>
        /// Fetches, checks and installs a pack. Never throws: every ending comes back as a
        /// result. Progress may be null, which is what the quiet path passes.
        /// </summary>
        Task<LanguagePackResult> DownloadAsync(
            string code,
            IProgress<LanguagePackProgress> progress = null,
            CancellationToken ct = default,
            bool userInitiated = true);

        /// <summary>
        /// Asks a running download to stop, and answers whether that was honoured. True while
        /// it is resolving, downloading or verifying; false once the folder has begun to move
        /// into place, because stopping a move halfway is worse than finishing it.
        /// </summary>
        bool Cancel(string code);

        /// <summary>
        /// The word the page shows for the quiet fetch, worked out without doing anything.
        /// The bridge answers lang.status with this and starts the fetch itself.
        /// </summary>
        string QuietFetchDecision(bool prefsCheckForUpdates, string savedLanguage, string runningVersion);

        /// <summary>
        /// After an app update, fetch the pack for the version now running, without a bar, a
        /// toast or a splash step. Does nothing for English, nothing when the folder is already
        /// there, and nothing when update checking is off. Answers the same word
        /// <see cref="QuietFetchDecision"/> does.
        /// </summary>
        Task<string> EnsureCurrentQuietlyAsync(
            bool prefsCheckForUpdates,
            string savedLanguage,
            string runningVersion,
            CancellationToken ct = default);

        /// <summary>
        /// Empties the staging folder, keeps the newest two versions of each language plus the
        /// one the app is running, and drops font files no remaining pack names. Answers how
        /// many things were removed. Never throws.
        /// </summary>
        int PruneOnBoot();

        /// <summary>Raises <see cref="LanguageChanged"/>. The bridge calls this after it writes the pref.</summary>
        void NotifyLanguageChanged(string code, string version);
    }

    /// <summary>
    /// Fetches, checks and installs the language packs, and owns everything about where they
    /// live on disk.
    /// <para>
    /// The download is modelled on DownloadHexiumFileAsync, which is the careful one in this
    /// tree: cap the declared length before reading a body, cap the running total so a server
    /// that declares nothing still cannot fill the disk, and touch nothing that is already
    /// installed until every check has passed. It adds the two things that one lacks, an
    /// IProgress and a CancellationToken, and one thing nothing in the tree has: a stall
    /// timeout instead of a whole-exchange timeout, because a five minute ceiling on the whole
    /// call is wrong for a pack on a household connection and right for nothing.
    /// </para>
    /// <para>
    /// Every path and the clock are properties rather than constants, so the suite drives the
    /// whole of this against a temporary folder and never against a real install.
    /// </para>
    /// </summary>
    public sealed class LanguagePackService : ILanguagePackService
    {
        /// <summary>The manifest's file name on a release. Found by name, never composed.</summary>
        public const string ManifestAssetName = "lang-manifest.json";

        private const string PackFileName = "pack.json";
        private const string StringsFileName = "strings.json";
        private const string StagingFolderName = ".staging";
        private const string FontsFolderName = "_fonts";
        private const string ManifestCacheFileName = "manifest-cache.json";
        private const int CopyBufferBytes = 81920;
        private const long ReportEveryBytes = 65536;

        private readonly IGitHubClient GitHub;
        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IUserPreferencesProvider Prefs;
        private readonly IAnalyticsService Analytics;
        private readonly IApplicationLogger Logger;

        private readonly object Gate = new();
        private Run Current;

        public LanguagePackService(
            IGitHubClient gitHub,
            IHttpClientProvider httpClientProvider,
            IUserPreferencesProvider prefs,
            IAnalyticsService analytics,
            IApplicationLogger logger)
        {
            GitHub = gitHub;
            HttpClientProvider = httpClientProvider;
            Prefs = prefs;
            Analytics = analytics;
            Logger = logger;
        }

        /// <summary>
        /// Where the packs live. Beside userprefs.json, because the install folder may be read
        /// only and a self update robocopies over it.
        /// </summary>
        public string RootFolder { get; set; } =
            Environment.ExpandEnvironmentVariables(Properties.Resources.LanguagesFolderPath);

        /// <summary>The version of the app these packs are for.</summary>
        public string AppVersion { get; set; } = AssemblyHelper.GetApplicationVersion();

        /// <summary>The clock, so a cache age can be driven rather than waited out.</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>
        /// The most a pack may weigh, 80 MB by default. A subset CJK face is a few megabytes,
        /// so this is roomy for every pack the project means to cut and still small enough
        /// that a mistake at the other end cannot fill a server box's system drive.
        /// </summary>
        public long MaxLanguagePackBytes { get; set; } = 80L * 1024 * 1024;

        /// <summary>
        /// How long the download may receive nothing before it is let go. The client's own
        /// timeout is left infinite for this call, because it covers the whole exchange
        /// including the body and would end a download that is going perfectly well.
        /// </summary>
        public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long a held manifest is taken at its word before the release page is asked
        /// again. Six hours, the same floor the software update check keeps, because
        /// unauthenticated GitHub calls are limited per address and this app sits on a box
        /// for weeks.
        /// </summary>
        public TimeSpan ManifestCacheTtl { get; set; } = TimeSpan.FromHours(6);

        /// <summary>
        /// Called on the download thread at the exact moment the pack starts moving into
        /// place, which is the moment a cancel stops being honest. The suite stands here to
        /// prove that; nothing in the app sets it.
        /// </summary>
        internal Action<string> BeforePlacing { get; set; }

        public event EventHandler<LanguageChangedEventArgs> LanguageChanged;

        public bool IsBusy
        {
            get { lock (Gate) return Current != null; }
        }

        private string StagingRoot => Path.Combine(RootFolder, StagingFolderName);

        private string FontsRoot => Path.Combine(RootFolder, FontsFolderName);

        private string ManifestCachePath => Path.Combine(RootFolder, ManifestCacheFileName);

        public void NotifyLanguageChanged(string code, string version) =>
            LanguageChanged?.Invoke(this, new LanguageChangedEventArgs { Code = code, Version = version });

        // ------------------------------------------------------------------ what is on disk

        public LanguagePackInstall Installed(string code) => ReadInstall(LanguageCodes.Normalize(code), AppVersion);

        public LanguagePackInstall InstalledAny(string code)
        {
            var normalized = LanguageCodes.Normalize(code);
            if (normalized == null) return null;

            foreach (var version in VersionsOnDisk(normalized))
            {
                var install = ReadInstall(normalized, version);
                if (install != null) return install;
            }

            return null;
        }

        /// <summary>Every version folder for a language, newest first.</summary>
        private List<string> VersionsOnDisk(string code)
        {
            var folder = Path.Combine(RootFolder, code);
            if (!Directory.Exists(folder)) return new List<string>();

            try
            {
                return Directory.EnumerateDirectories(folder)
                    .Select(Path.GetFileName)
                    // A folder set aside during a replace is not a version. It is swept on boot.
                    .Where(name => !string.IsNullOrEmpty(name) && !name.Contains(".old-", StringComparison.Ordinal))
                    .OrderByDescending(name => name, new VersionOrder())
                    .ToList();
            }
            catch (Exception e)
            {
                Logger.Warning("Could not list the installed packs for {0}: {1}", code, e.Message);
                return new List<string>();
            }
        }

        private LanguagePackInstall ReadInstall(string code, string version)
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(version)) return null;

            var folder = Path.Combine(RootFolder, code, version);
            var pack = ReadPackFile(Path.Combine(folder, PackFileName));
            if (pack == null) return null;
            if (!File.Exists(Path.Combine(folder, StringsFileName))) return null;

            return new LanguagePackInstall
            {
                Code = code,
                Version = version,
                Catalog = pack.Catalog,
                Keys = pack.Keys,
                Translated = pack.Translated,
                Status = pack.Status,
                Folder = folder,
                MatchesApp = string.Equals(version, AppVersion, StringComparison.OrdinalIgnoreCase),
                Fonts = pack.Fonts ?? new List<LanguagePackFont>(),
            };
        }

        private LanguagePackFile ReadPackFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<LanguagePackFile>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Logger.Warning("Could not read {0}: {1}", path, e.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ the menu's answer

        public async Task<LanguageCatalogListing> ListAsync(CancellationToken ct = default)
        {
            var state = new LanguageManifestState { ErrorId = LanguagePackReasons.Offline };
            LanguageManifest manifest = null;

            try
            {
                var resolved = await ResolveManifestAsync(allowNetwork: true, ct);
                state = resolved.State;
                manifest = resolved.Manifest;
            }
            catch (Exception e)
            {
                // ListAsync is what the globe menu calls. It has no failure mode: the built-in
                // list plus what is on disk is already a usable menu, and the manifest only
                // ever adds byte counts and coverage to rows that are drawn either way.
                Logger.Warning("The language manifest could not be read: {0}", e.Message);
            }

            var rows = new List<LanguagePackEntry>();
            foreach (var known in LanguageCodes.Known)
            {
                var entry = manifest?.Languages?.FirstOrDefault(l =>
                    string.Equals(LanguageCodes.Normalize(l?.Code), known.Code, StringComparison.Ordinal));
                var install = InstalledAny(known.Code);

                rows.Add(new LanguagePackEntry
                {
                    Code = known.Code,
                    NativeName = known.NativeName,
                    EnglishName = known.EnglishName,
                    Bcp47 = known.Bcp47,
                    FontStackKey = known.FontStackKey,
                    BuiltIn = known.BuiltIn,
                    Installed = known.BuiltIn || install != null,
                    InstalledVersion = known.BuiltIn ? AppVersion : install?.Version,
                    MatchesApp = known.BuiltIn || (install?.MatchesApp ?? false),
                    Available = known.BuiltIn || entry != null,
                    Bytes = entry?.Bytes ?? 0,
                    Catalog = entry?.Catalog ?? install?.Catalog ?? 0,
                    Keys = entry?.Keys ?? install?.Keys ?? 0,
                    Translated = entry?.Translated ?? install?.Translated ?? 0,
                    Status = entry?.Status ?? install?.Status,
                    MinAppVersion = entry?.MinAppVersion,
                });
            }

            return new LanguageCatalogListing
            {
                AppVersion = AppVersion,
                Languages = rows,
                Manifest = state,
            };
        }

        // ------------------------------------------------------------------ the quiet fetch

        public string QuietFetchDecision(bool prefsCheckForUpdates, string savedLanguage, string runningVersion)
        {
            var code = LanguageCodes.Normalize(savedLanguage);
            if (code == null || LanguageCodes.IsEnglish(code)) return LanguageQuietFetch.None;

            var version = string.IsNullOrWhiteSpace(runningVersion) ? AppVersion : runningVersion.Trim();
            if (ReadInstall(code, version) != null) return LanguageQuietFetch.None;

            // The switch that says whether BakaLoader may reach out at all has the last word
            // on an unattended fetch. A host who opens the globe and picks a language has
            // asked out loud, and that path does not come through here.
            if (!prefsCheckForUpdates) return LanguageQuietFetch.SkippedChecksOff;
            if (IsBusy) return LanguageQuietFetch.SkippedBusy;

            return LanguageQuietFetch.Started;
        }

        public async Task<string> EnsureCurrentQuietlyAsync(
            bool prefsCheckForUpdates,
            string savedLanguage,
            string runningVersion,
            CancellationToken ct = default)
        {
            var decision = QuietFetchDecision(prefsCheckForUpdates, savedLanguage, runningVersion);
            if (decision != LanguageQuietFetch.Started) return decision;

            var code = LanguageCodes.Normalize(savedLanguage);

            // No progress, so no events: the host sees their language with the keys the older
            // pack has, and the new keys quietly stop being English if this works.
            var result = await DownloadAsync(code, progress: null, ct, userInitiated: false);
            if (!result.Ok)
            {
                Logger.Information(
                    "The pack for {0} was not refreshed after the update ({1}). The pack already on disk is still in use.",
                    code, result.ReasonId ?? "no reason given");
            }

            return LanguageQuietFetch.Started;
        }

        // ------------------------------------------------------------------ cancel

        public bool Cancel(string code)
        {
            lock (Gate)
            {
                var run = Current;
                if (run == null) return false;

                // A named code has to be the one that is running. Nothing is a wildcard, which
                // is what the app shutdown path wants; a code the app does not know is not.
                if (!string.IsNullOrWhiteSpace(code))
                {
                    var normalized = LanguageCodes.Normalize(code);
                    if (!string.Equals(normalized, run.Code, StringComparison.Ordinal)) return false;
                }

                // Once the folder has begun to move into place there is nothing honest to
                // answer but false. The window is milliseconds wide and a half moved pack is
                // worse than a pack the host did not want.
                if (run.PastCancel) return false;

                run.CancelRequested = true;
                try { run.Cancellation.Cancel(); } catch (Exception) { /* already gone */ }
                return true;
            }
        }

        // ------------------------------------------------------------------ the download

        public async Task<LanguagePackResult> DownloadAsync(
            string code,
            IProgress<LanguagePackProgress> progress = null,
            CancellationToken ct = default,
            bool userInitiated = true)
        {
            var normalized = LanguageCodes.Normalize(code);
            if (normalized == null) return Refuse(code, LanguagePackReasons.UnknownCode, progress);
            if (LanguageCodes.IsEnglish(normalized)) return Refuse(normalized, LanguagePackReasons.BuiltIn, progress);

            if (!userInitiated && !ChecksAllowed())
                return Refuse(normalized, LanguagePackReasons.ChecksOff, progress);

            Run run;
            lock (Gate)
            {
                if (Current != null) return Refuse(normalized, LanguagePackReasons.Busy, progress);

                run = new Run
                {
                    Code = normalized,
                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct),
                };
                Current = run;
            }

            try
            {
                return await InstallAsync(run, progress, ct);
            }
            catch (Exception e)
            {
                // DownloadAsync never throws. Anything that got this far is a bug rather than
                // an ending, so it is logged loudly and answered as a plain failure.
                Logger.Error(e, "The download of the {0} language pack ended in an unexpected way.", normalized);
                return Fail(normalized, LanguagePackReasons.Install, progress);
            }
            finally
            {
                lock (Gate) Current = null;
                try { run.Cancellation.Dispose(); } catch (Exception) { /* already gone */ }
            }
        }

        private async Task<LanguagePackResult> InstallAsync(
            Run run, IProgress<LanguagePackProgress> progress, CancellationToken callerToken)
        {
            var code = run.Code;
            var token = run.Cancellation.Token;
            string staging = null;

            Report(progress, code, LanguagePackPhases.Resolving, -1, 0, 0);

            try
            {
                var resolved = await ResolveManifestAsync(allowNetwork: true, token);
                if (resolved.Manifest == null)
                    return Fail(code, resolved.State.ErrorId ?? LanguagePackReasons.Offline, progress);

                var entry = resolved.Manifest.Languages?.FirstOrDefault(l =>
                    string.Equals(LanguageCodes.Normalize(l?.Code), code, StringComparison.Ordinal));
                if (entry == null) return Fail(code, LanguagePackReasons.NoPack, progress);

                var version = FirstNotBlank(entry.AppVersion, resolved.Manifest.AppVersion);
                if (string.IsNullOrWhiteSpace(version)) return Fail(code, LanguagePackReasons.NoPack, progress);

                // A pack that needs a newer app than this one is refused before a byte is
                // asked for, and by comparing versions rather than text.
                if (!string.IsNullOrWhiteSpace(entry.MinAppVersion) &&
                    AssemblyHelper.CompareVersion(entry.MinAppVersion, AppVersion) > 0)
                {
                    return Fail(code, LanguagePackReasons.TooOld, progress,
                        ("minAppVersion", entry.MinAppVersion), ("appVersion", AppVersion));
                }

                if (MaxLanguagePackBytes > 0 && entry.Bytes > MaxLanguagePackBytes)
                {
                    return Fail(code, LanguagePackReasons.TooLarge, progress,
                        ("bytes", entry.Bytes), ("cap", MaxLanguagePackBytes));
                }

                // The catalogue did not change, so there are no new words to fetch. A patch
                // release that fixed a crash must not cost four CJK downloads.
                var shortcut = TryUnchangedCatalog(run, entry, version, progress);
                if (shortcut != null) return shortcut;

                if (string.IsNullOrWhiteSpace(entry.Url)) return Fail(code, LanguagePackReasons.NoPack, progress);

                Directory.CreateDirectory(StagingRoot);
                staging = Path.Combine(StagingRoot, $"{code}-{version}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(staging);

                var zipPath = Path.Combine(staging, "pack.zip");
                var downloaded = await StreamToFileAsync(run, entry, zipPath, progress, token);
                if (downloaded.ReasonId != null) return Fail(code, downloaded.ReasonId, progress, downloaded.Params);

                Report(progress, code, LanguagePackPhases.Verifying, 100, downloaded.Bytes, downloaded.Bytes);

                // Nothing on disk that the app reads has been touched yet, and nothing will be
                // until both of these hold.
                if (entry.Bytes > 0 && downloaded.Bytes != entry.Bytes)
                {
                    Logger.Warning(
                        "The {0} pack weighed {1} bytes and the manifest published {2}, so it was not installed.",
                        code, downloaded.Bytes, entry.Bytes);
                    return Fail(code, LanguagePackReasons.Integrity, progress);
                }

                if (!string.IsNullOrWhiteSpace(entry.Sha256) &&
                    !string.Equals(downloaded.Sha256, entry.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("The {0} pack did not match its published checksum, so it was not installed.", code);
                    return Fail(code, LanguagePackReasons.Integrity, progress);
                }

                token.ThrowIfCancellationRequested();

                var unpacked = Path.Combine(staging, "unpacked");
                try
                {
                    // ExtractToDirectory refuses an entry that resolves outside the destination,
                    // which is the zip slip defence the mod installer leans on too.
                    ZipFile.ExtractToDirectory(zipPath, unpacked);
                }
                catch (Exception e)
                {
                    Logger.Warning("The {0} pack could not be unpacked: {1}", code, e.Message);
                    return Fail(code, LanguagePackReasons.Contents, progress);
                }

                TryDelete(zipPath);

                var checkedPack = ValidateUnpacked(code, version, unpacked);
                if (checkedPack.ReasonId != null) return Fail(code, checkedPack.ReasonId, progress);

                token.ThrowIfCancellationRequested();

                var fonts = StoreFonts(code, unpacked, checkedPack.Pack);
                if (fonts != null) return Fail(code, fonts, progress);

                WritePackFile(Path.Combine(unpacked, PackFileName), checkedPack.Pack);

                // From here the cancel would be a lie, so it stops being offered first and the
                // move happens second.
                lock (Gate) run.PastCancel = true;
                Report(progress, code, LanguagePackPhases.Installing, 100, downloaded.Bytes, downloaded.Bytes);
                BeforePlacing?.Invoke(code);

                var folder = Place(code, version, unpacked);
                if (folder == null) return Fail(code, LanguagePackReasons.Install, progress);

                RecordInstall(code, version);
                Report(progress, code, LanguagePackPhases.Done, 100, downloaded.Bytes, downloaded.Bytes);

                Logger.Information("Installed the {0} language pack for {1}.", code, version);
                return new LanguagePackResult { Ok = true, Code = code, AppVersion = version, Folder = folder };
            }
            catch (OperationCanceledException)
            {
                return Cancelled(run, progress, callerToken);
            }
            catch (Exception e)
            {
                Logger.Error(e, "The {0} language pack could not be installed.", code);
                return Fail(code, LanguagePackReasons.Install, progress);
            }
            finally
            {
                // Whatever happened, the staging folder goes. A cancel before the move leaves
                // nothing behind at all.
                if (staging != null) TryDeleteDirectory(staging);
            }
        }

        /// <summary>
        /// The manifest says this language's catalogue is the one already on disk, so the pack
        /// is placed by copying the folder the app already has. Answers null when that does not
        /// apply, and in that case not one request has been made for an asset.
        /// </summary>
        private LanguagePackResult TryUnchangedCatalog(
            Run run, LanguageManifestEntry entry, string version, IProgress<LanguagePackProgress> progress)
        {
            var code = run.Code;

            // The pack for this exact version is already here. That is only the end of the
            // story while the catalogue it holds is the one the manifest names: a pack cut
            // again for the same version with new words in it has to be fetched, or a host
            // who asks for it again is told yes and shown the same missing lines.
            var here = ReadInstall(code, version);
            if (here != null)
            {
                if (entry.Catalog > 0 && here.Catalog != entry.Catalog) return null;

                Report(progress, code, LanguagePackPhases.Done, 100, 0, 0);
                return new LanguagePackResult { Ok = true, Code = code, AppVersion = version, Folder = here.Folder };
            }

            var have = InstalledAny(code);
            if (have == null || have.Catalog <= 0 || entry.Catalog <= 0 || have.Catalog != entry.Catalog) return null;

            var staging = Path.Combine(StagingRoot, $"{code}-{version}-{Guid.NewGuid():N}");

            try
            {
                lock (Gate) run.PastCancel = true;
                Report(progress, code, LanguagePackPhases.Installing, 100, 0, 0);
                BeforePlacing?.Invoke(code);

                Directory.CreateDirectory(StagingRoot);
                CopyDirectory(have.Folder, staging);

                // The copy is the pack for this version now, so it says so about itself.
                var pack = ReadPackFile(Path.Combine(staging, PackFileName));
                if (pack != null)
                {
                    pack.AppVersion = version;
                    WritePackFile(Path.Combine(staging, PackFileName), pack);
                }

                var folder = Place(code, version, staging);
                if (folder == null) return Fail(code, LanguagePackReasons.Install, progress);

                Logger.Information(
                    "The {0} catalogue is unchanged at revision {1}, so the pack from {2} was reused for {3}.",
                    code, entry.Catalog, have.Version, version);

                Report(progress, code, LanguagePackPhases.Done, 100, 0, 0);
                return new LanguagePackResult { Ok = true, Code = code, AppVersion = version, Folder = folder };
            }
            catch (Exception e)
            {
                Logger.Warning("The held {0} pack could not be reused: {1}", code, e.Message);
                return Fail(code, LanguagePackReasons.Install, progress);
            }
            finally
            {
                // Either the copy moved into place, in which case this is already gone, or it
                // did not, in which case a half copied folder is not left for the boot sweep.
                TryDeleteDirectory(staging);
            }
        }

        private sealed class Downloaded
        {
            public long Bytes;
            public string Sha256;
            public string ReasonId;
            public (string Name, object Value)[] Params = Array.Empty<(string, object)>();
        }

        /// <summary>
        /// The bytes, into a file, with the digest taken as they are written so the file is
        /// never read twice.
        /// </summary>
        private async Task<Downloaded> StreamToFileAsync(
            Run run,
            LanguageManifestEntry entry,
            string destinationPath,
            IProgress<LanguagePackProgress> progress,
            CancellationToken token)
        {
            var code = run.Code;
            var cap = MaxLanguagePackBytes;
            var expected = entry.Bytes;

            using var client = HttpClientProvider.CreateClient();

            // Infinite on purpose. HttpClient.Timeout covers the whole exchange including a
            // streamed body, so a five minute ceiling ends a large pack on a household link
            // that is downloading perfectly well. The stall window below is the real guard.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimBakaLoader");

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (StallTimeout > TimeSpan.Zero) stall.CancelAfter(StallTimeout);

            // The address is the one the manifest published, used exactly as it stands.
            using var response = await client.GetAsync(
                entry.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning(
                    "The {0} pack answered {1} ({2}).", code, (int)response.StatusCode, response.ReasonPhrase);
                return new Downloaded { ReasonId = LanguagePackReasons.Offline };
            }

            if (response.Content.Headers.ContentLength is { } declared && cap > 0 && declared > cap)
            {
                return new Downloaded
                {
                    ReasonId = LanguagePackReasons.TooLarge,
                    Params = new (string, object)[] { ("bytes", declared), ("cap", cap) },
                };
            }

            Report(progress, code, LanguagePackPhases.Downloading, expected > 0 ? 0 : -1, 0, expected);

            using var sha = SHA256.Create();
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var destination = File.Create(destinationPath);

            var buffer = new byte[CopyBufferBytes];
            long total = 0;
            long lastReport = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, stall.Token)) > 0)
            {
                // Bytes arrived, so the stall window starts again. Slow and moving is fine;
                // moving nothing at all for a minute is not.
                if (StallTimeout > TimeSpan.Zero) stall.CancelAfter(StallTimeout);
                token.ThrowIfCancellationRequested();

                total += read;
                if (cap > 0 && total > cap)
                {
                    return new Downloaded
                    {
                        ReasonId = LanguagePackReasons.TooLarge,
                        Params = new (string, object)[] { ("bytes", total), ("cap", cap) },
                    };
                }

                sha.TransformBlock(buffer, 0, read, null, 0);
                await destination.WriteAsync(buffer, 0, read, CancellationToken.None);

                // One report per 64 KB, never one per read. A slow connection hands back a few
                // hundred bytes at a time, and every report crosses to the UI thread for a bar
                // that can show a hundred states.
                if (total - lastReport >= ReportEveryBytes || (expected > 0 && total >= expected))
                {
                    lastReport = total;
                    Report(
                        progress, code, LanguagePackPhases.Downloading,
                        expected > 0 ? (int)Math.Min(100, total * 100 / expected) : -1, total, expected);
                }
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return new Downloaded { Bytes = total, Sha256 = Convert.ToHexString(sha.Hash).ToLowerInvariant() };
        }

        private sealed class Unpacked
        {
            public LanguagePackFile Pack;
            public string ReasonId;
        }

        /// <summary>
        /// A zip that opened is not yet a language pack. It has to carry both files, both have
        /// to parse, and the catalogue inside has to say it is the language that was asked for.
        /// </summary>
        private Unpacked ValidateUnpacked(string code, string version, string folder)
        {
            var pack = ReadPackFile(Path.Combine(folder, PackFileName));
            if (pack == null) return new Unpacked { ReasonId = LanguagePackReasons.Contents };

            var stringsPath = Path.Combine(folder, StringsFileName);
            if (!File.Exists(stringsPath)) return new Unpacked { ReasonId = LanguagePackReasons.Contents };

            string declared;
            try
            {
                var catalog = JsonConvert.DeserializeObject<CatalogHead>(File.ReadAllText(stringsPath));
                // The catalogs in the tree write _meta.language; a manifest cut by hand is as
                // likely to write _meta.lang. Either names the language, and a pack that names
                // neither has not said what it is.
                declared = FirstNotBlank(catalog?.Meta?.Lang, catalog?.Meta?.Language);
            }
            catch (Exception e)
            {
                Logger.Warning("The {0} pack's strings could not be read: {1}", code, e.Message);
                return new Unpacked { ReasonId = LanguagePackReasons.Contents };
            }

            if (!string.Equals(declared?.Trim(), code, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning(
                    "The pack fetched for {0} says it holds {1}, so it was not installed.", code, declared ?? "nothing");
                return new Unpacked { ReasonId = LanguagePackReasons.Contents };
            }

            if (!string.IsNullOrWhiteSpace(pack.Code) &&
                !string.Equals(LanguageCodes.Normalize(pack.Code), code, StringComparison.Ordinal))
            {
                return new Unpacked { ReasonId = LanguagePackReasons.Contents };
            }

            pack.Code = code;
            pack.AppVersion = version;
            return new Unpacked { Pack = pack };
        }

        /// <summary>
        /// Moves each face into the shared store under its own digest and rewrites the pack to
        /// point there. Two languages that use the same face then keep one copy of it, and a
        /// pack that is reinstalled writes no font bytes at all. Answers a reason id on a
        /// mismatch, otherwise null.
        /// </summary>
        private string StoreFonts(string code, string folder, LanguagePackFile pack)
        {
            if (pack.Fonts == null || pack.Fonts.Count == 0) return null;

            Directory.CreateDirectory(FontsRoot);

            foreach (var font in pack.Fonts)
            {
                if (string.IsNullOrWhiteSpace(font?.File)) return LanguagePackReasons.Contents;

                var relative = font.File.Replace('/', Path.DirectorySeparatorChar);
                var staged = Path.Combine(folder, relative);
                if (!File.Exists(staged))
                {
                    Logger.Warning("The {0} pack names {1} and does not carry it.", code, font.File);
                    return LanguagePackReasons.Contents;
                }

                var digest = FileDigest(staged);
                if (!string.IsNullOrWhiteSpace(font.Sha256) &&
                    !string.Equals(digest, font.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("A font in the {0} pack did not match the digest the pack published.", code);
                    return LanguagePackReasons.Integrity;
                }

                var extension = Path.GetExtension(staged);
                if (string.IsNullOrEmpty(extension)) extension = ".woff2";

                var stored = Path.Combine(FontsRoot, digest + extension);
                if (File.Exists(stored)) TryDelete(staged);
                else File.Move(staged, stored);

                font.Sha256 = digest;
                font.File = FontsFolderName + "/" + digest + extension;
            }

            // The licence travels with the fonts, under its own digest so one text is kept once.
            var licences = new List<string>();
            var fontsDir = Path.Combine(folder, "fonts");
            if (Directory.Exists(fontsDir))
            {
                foreach (var licence in Directory.EnumerateFiles(fontsDir, "*.txt"))
                {
                    var digest = FileDigest(licence);
                    var stored = Path.Combine(FontsRoot, "OFL-" + digest + ".txt");
                    if (File.Exists(stored)) TryDelete(licence);
                    else File.Move(licence, stored);

                    licences.Add(FontsFolderName + "/OFL-" + digest + ".txt");
                }

                TryDeleteDirectory(fontsDir);
            }

            if (licences.Count > 0) pack.Licenses = licences;
            return null;
        }

        /// <summary>
        /// The move into place. Directory.Move is atomic inside one volume, and the app data
        /// folder is one volume, so the version either exists whole or does not exist. A folder
        /// already sitting there is moved aside first and deleted afterwards, and a delete that
        /// fails is survivable: the pack works and the boot sweep will get it.
        /// </summary>
        private string Place(string code, string version, string source)
        {
            var target = Path.Combine(RootFolder, code, version);
            string aside = null;

            try
            {
                Directory.CreateDirectory(Path.Combine(RootFolder, code));

                if (Directory.Exists(target))
                {
                    aside = target + ".old-" + Guid.NewGuid().ToString("N");
                    Directory.Move(target, aside);
                }

                Directory.Move(source, target);
            }
            catch (Exception e)
            {
                Logger.Error(e, "The {0} pack for {1} could not be put in place.", code, version);

                // Put back whatever was there before this tried.
                if (aside != null && !Directory.Exists(target))
                {
                    try { Directory.Move(aside, target); }
                    catch (Exception restore) { Logger.Error(restore, "The previous {0} pack is at {1}.", code, aside); }
                }

                return null;
            }

            if (aside != null) TryDeleteDirectory(aside);
            return target;
        }

        private void RecordInstall(string code, string version)
        {
            try
            {
                Analytics?.Record(new AnalyticsEvent
                {
                    Kind = "lang",
                    Mod = code,
                    ToVersion = version,
                });
            }
            catch (Exception e)
            {
                Logger.Warning("The language pack install was not recorded in the journal: {0}", e.Message);
            }
        }

        // ------------------------------------------------------------------ the manifest

        private sealed class Resolution
        {
            public LanguageManifest Manifest;
            public LanguageManifestState State = new();
        }

        /// <summary>
        /// The manifest for the running version, from the held copy when that is still fresh
        /// and from the release page otherwise. The release is found first by the tag for this
        /// version and then, when that release carries no manifest, by the newest release at
        /// or below this version that does.
        /// </summary>
        private async Task<Resolution> ResolveManifestAsync(bool allowNetwork, CancellationToken ct)
        {
            var cached = ReadManifestCache();
            var fresh = cached != null &&
                        string.Equals(cached.AppVersion, AppVersion, StringComparison.OrdinalIgnoreCase) &&
                        ManifestCacheTtl > TimeSpan.Zero &&
                        UtcNow() - cached.FetchedUtc < ManifestCacheTtl;

            if (!allowNetwork || fresh)
            {
                if (cached?.Body == null)
                {
                    return new Resolution
                    {
                        State = new LanguageManifestState { Ok = false, ErrorId = LanguagePackReasons.Offline },
                    };
                }

                return new Resolution
                {
                    Manifest = cached.Body,
                    State = new LanguageManifestState
                    {
                        Ok = true,
                        FromCache = true,
                        CheckedUtc = cached.FetchedUtc,
                        ReleaseTag = cached.ReleaseTag,
                        AppVersion = cached.Body.AppVersion,
                    },
                };
            }

            var release = await FindReleaseWithManifestAsync();
            if (release == null)
            {
                // Nothing to read. A held copy is better than nothing, and its age is reported
                // rather than hidden.
                if (cached?.Body != null)
                {
                    return new Resolution
                    {
                        Manifest = cached.Body,
                        State = new LanguageManifestState
                        {
                            Ok = true,
                            FromCache = true,
                            CheckedUtc = cached.FetchedUtc,
                            ReleaseTag = cached.ReleaseTag,
                            AppVersion = cached.Body.AppVersion,
                        },
                    };
                }

                return new Resolution
                {
                    State = new LanguageManifestState { Ok = false, ErrorId = LanguagePackReasons.Offline },
                };
            }

            var asset = release.Asset(ManifestAssetName);
            var url = asset?.BrowserDownloadUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                return new Resolution
                {
                    State = new LanguageManifestState { Ok = false, ErrorId = LanguagePackReasons.NoPack },
                };
            }

            try
            {
                using var client = HttpClientProvider.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimBakaLoader");

                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                // A conditional request keeps the common case free against the rate limit: the
                // manifest changes on release day and on no other day.
                if (cached != null &&
                    string.Equals(cached.Url, url, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(cached.ETag) &&
                    EntityTagHeaderValue.TryParse(cached.ETag, out var tag))
                {
                    request.Headers.IfNoneMatch.Add(tag);
                }

                using var response = await client.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.NotModified && cached?.Body != null)
                {
                    cached.FetchedUtc = UtcNow();
                    cached.AppVersion = AppVersion;
                    cached.ReleaseTag = release.TagName;
                    WriteManifestCache(cached);

                    return new Resolution
                    {
                        Manifest = cached.Body,
                        State = new LanguageManifestState
                        {
                            Ok = true,
                            FromCache = true,
                            CheckedUtc = cached.FetchedUtc,
                            ReleaseTag = release.TagName,
                            AppVersion = cached.Body.AppVersion,
                        },
                    };
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new Resolution
                    {
                        State = new LanguageManifestState { Ok = false, ErrorId = LanguagePackReasons.Offline },
                    };
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                var manifest = JsonConvert.DeserializeObject<LanguageManifest>(body);
                if (manifest?.Languages == null)
                {
                    return new Resolution
                    {
                        State = new LanguageManifestState { Ok = false, ErrorId = LanguagePackReasons.NoPack },
                    };
                }

                var now = UtcNow();
                WriteManifestCache(new LanguageManifestCache
                {
                    Url = url,
                    ETag = response.Headers.ETag?.ToString(),
                    FetchedUtc = now,
                    ReleaseTag = release.TagName,
                    AppVersion = AppVersion,
                    Body = manifest,
                });

                return new Resolution
                {
                    Manifest = manifest,
                    State = new LanguageManifestState
                    {
                        Ok = true,
                        FromCache = false,
                        CheckedUtc = now,
                        ReleaseTag = release.TagName,
                        AppVersion = manifest.AppVersion,
                    },
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Logger.Warning("The language manifest could not be fetched: {0}", e.Message);
                return new Resolution
                {
                    State = new LanguageManifestState { Ok = false, ErrorId = LanguagePackReasons.Offline },
                };
            }
        }

        private async Task<GitHubRelease> FindReleaseWithManifestAsync()
        {
            try
            {
                var exact = await GitHub.GetReleaseByTagAsync("v" + AppVersion);
                if (exact?.Asset(ManifestAssetName) != null) return exact;

                // No packs were cut for this version, so the newest ones at or below it are
                // the right ones. The pack is then simply older than the app, which the menu
                // says out loud rather than treating as a failure.
                var releases = await GitHub.GetReleasesAsync();
                if (releases == null) return null;

                return releases
                    .Where(r => r.Asset(ManifestAssetName) != null)
                    .Where(r => AssemblyHelper.CompareVersion(TagVersion(r.TagName), AppVersion) <= 0)
                    .OrderByDescending(r => TagVersion(r.TagName), new VersionOrder())
                    .FirstOrDefault();
            }
            catch (Exception e)
            {
                Logger.Warning("The release page could not be read: {0}", e.Message);
                return null;
            }
        }

        private static string TagVersion(string tag)
        {
            var trimmed = tag?.Trim() ?? "";
            return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? trimmed[1..] : trimmed;
        }

        private LanguageManifestCache ReadManifestCache()
        {
            try
            {
                if (!File.Exists(ManifestCachePath)) return null;
                return JsonConvert.DeserializeObject<LanguageManifestCache>(File.ReadAllText(ManifestCachePath));
            }
            catch (Exception e)
            {
                Logger.Warning("The held language manifest could not be read: {0}", e.Message);
                return null;
            }
        }

        private void WriteManifestCache(LanguageManifestCache cache)
        {
            try
            {
                Directory.CreateDirectory(RootFolder);
                File.WriteAllText(ManifestCachePath, JsonConvert.SerializeObject(cache, Formatting.Indented));
            }
            catch (Exception e)
            {
                Logger.Warning("The language manifest could not be held: {0}", e.Message);
            }
        }

        // ------------------------------------------------------------------ the boot sweep

        public int PruneOnBoot()
        {
            var removed = 0;

            try
            {
                if (!Directory.Exists(RootFolder)) return 0;

                // Staging is transient by definition: anything in it is the remains of a run
                // that did not finish.
                if (Directory.Exists(StagingRoot))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(StagingRoot))
                    {
                        if (TryDeleteAny(entry)) removed++;
                    }
                }

                foreach (var language in LanguageCodes.Known.Where(l => !l.BuiltIn))
                {
                    var folder = Path.Combine(RootFolder, language.Code);
                    if (!Directory.Exists(folder)) continue;

                    var versions = VersionsOnDisk(language.Code);
                    var keep = new HashSet<string>(versions.Take(2), StringComparer.OrdinalIgnoreCase) { AppVersion };

                    foreach (var name in Directory.EnumerateDirectories(folder).Select(Path.GetFileName))
                    {
                        if (string.IsNullOrEmpty(name) || keep.Contains(name)) continue;
                        if (TryDeleteDirectory(Path.Combine(folder, name))) removed++;
                    }
                }

                removed += PruneFonts();
            }
            catch (Exception e)
            {
                // A sweep is housekeeping. It never gets in the way of the app starting.
                Logger.Warning("The language folder could not be swept: {0}", e.Message);
            }

            return removed;
        }

        private int PruneFonts()
        {
            if (!Directory.Exists(FontsRoot)) return 0;

            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var language in LanguageCodes.Known.Where(l => !l.BuiltIn))
            {
                foreach (var version in VersionsOnDisk(language.Code))
                {
                    var pack = ReadPackFile(Path.Combine(RootFolder, language.Code, version, PackFileName));
                    if (pack == null) continue;

                    foreach (var font in pack.Fonts ?? new List<LanguagePackFont>())
                    {
                        if (!string.IsNullOrWhiteSpace(font?.File)) referenced.Add(Path.GetFileName(font.File));
                    }

                    foreach (var licence in pack.Licenses ?? new List<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(licence)) referenced.Add(Path.GetFileName(licence));
                    }
                }
            }

            var removed = 0;
            foreach (var file in Directory.EnumerateFiles(FontsRoot))
            {
                if (referenced.Contains(Path.GetFileName(file))) continue;
                if (TryDelete(file)) removed++;
            }

            return removed;
        }

        // ------------------------------------------------------------------ small helpers

        private sealed class Run
        {
            public string Code;
            public CancellationTokenSource Cancellation;
            public volatile bool CancelRequested;

            /// <summary>Set the moment the pack starts moving into place, and never unset.</summary>
            public volatile bool PastCancel;
        }

        /// <summary>The head of a catalog file, which is all this side reads of it.</summary>
        private sealed class CatalogHead
        {
            [JsonProperty("_meta")] public CatalogMeta Meta { get; set; }

            public sealed class CatalogMeta
            {
                [JsonProperty("lang")] public string Lang { get; set; }

                [JsonProperty("language")] public string Language { get; set; }

                [JsonProperty("catalog")] public int Catalog { get; set; }
            }
        }

        /// <summary>Orders version-shaped folder names as versions rather than as text.</summary>
        private sealed class VersionOrder : IComparer<string>
        {
            public int Compare(string x, string y)
            {
                var compared = AssemblyHelper.CompareVersion(x, y);
                return compared == -2 ? string.CompareOrdinal(x, y) : compared;
            }
        }

        private bool ChecksAllowed()
        {
            try
            {
                return Prefs?.LoadPreferences()?.CheckForUpdates ?? true;
            }
            catch (Exception e)
            {
                Logger.Warning("The update switch could not be read, so nothing was fetched: {0}", e.Message);
                return false;
            }
        }

        private LanguagePackResult Cancelled(
            Run run, IProgress<LanguagePackProgress> progress, CancellationToken callerToken)
        {
            // A cancel and a stall end the same way in the code and are two different things
            // to say to a host, so which one happened is decided here rather than guessed.
            var asked = run.CancelRequested || callerToken.IsCancellationRequested;
            var reason = asked ? LanguagePackReasons.Cancelled : LanguagePackReasons.Stalled;

            Report(progress, run.Code, asked ? LanguagePackPhases.Cancelled : LanguagePackPhases.Failed, -1, 0, 0, reason);

            return new LanguagePackResult
            {
                Ok = false,
                Cancelled = asked,
                Code = run.Code,
                ReasonId = reason,
            };
        }

        private LanguagePackResult Refuse(
            string code, string reasonId, IProgress<LanguagePackProgress> progress) =>
            Fail(code, reasonId, progress);

        private LanguagePackResult Fail(
            string code,
            string reasonId,
            IProgress<LanguagePackProgress> progress,
            params (string Name, object Value)[] args)
        {
            var map = Params(args);
            Report(progress, code, LanguagePackPhases.Failed, -1, 0, 0, reasonId, map);

            return new LanguagePackResult { Ok = false, Code = code, ReasonId = reasonId, ReasonParams = map };
        }

        private static IReadOnlyDictionary<string, object> Params((string Name, object Value)[] args)
        {
            if (args == null || args.Length == 0) return null;

            var map = new Dictionary<string, object>(args.Length, StringComparer.Ordinal);
            foreach (var (name, value) in args)
            {
                if (!string.IsNullOrWhiteSpace(name)) map[name.Trim()] = value;
            }

            return map.Count == 0 ? null : map;
        }

        private static void Report(
            IProgress<LanguagePackProgress> progress,
            string code,
            string phase,
            int percent,
            long done,
            long total,
            string messageId = null,
            IReadOnlyDictionary<string, object> messageParams = null)
        {
            progress?.Report(new LanguagePackProgress
            {
                Code = code,
                Phase = phase,
                Percent = percent,
                BytesDone = done,
                BytesTotal = total,
                MessageId = messageId,
                MessageParams = messageParams,
            });
        }

        private static string FirstNotBlank(params string[] values) =>
            values?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

        private static string FileDigest(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        private void WritePackFile(string path, LanguagePackFile pack) =>
            File.WriteAllText(path, JsonConvert.SerializeObject(pack, Formatting.Indented));

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var child in Directory.EnumerateDirectories(source))
            {
                CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
            }
        }

        private bool TryDeleteAny(string path)
        {
            if (Directory.Exists(path)) return TryDeleteDirectory(path);
            return TryDelete(path);
        }

        private bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception e)
            {
                Logger.Warning("Could not delete {0}: {1}", path, e.Message);
                return false;
            }
        }

        private bool TryDeleteDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return false;
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception e)
            {
                Logger.Warning("Could not delete {0}: {1}", path, e.Message);
                return false;
            }
        }
    }
}
