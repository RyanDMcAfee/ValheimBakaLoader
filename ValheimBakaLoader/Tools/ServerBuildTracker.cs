using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ValheimBakaLoader.Tools
{
    /// <summary>Where a build identity came from.</summary>
    public enum ServerBuildSource
    {
        /// <summary>No identity could be read at all (the exe is missing or unreadable).</summary>
        Unknown = 0,

        /// <summary>Read out of Steam's own appmanifest_896660.acf.</summary>
        Manifest = 1,

        /// <summary>Hashed off the server binaries, because no Steam manifest was found.</summary>
        Fingerprint = 2,
    }

    /// <summary>
    /// What the Valheim dedicated server on disk is right now: which Steam build it is,
    /// whether Steam has an update queued for it, and a content hash that identifies the
    /// binaries even on installs Steam does not track (steamcmd copies, hand-made folders).
    /// </summary>
    public sealed class ServerBuildInfo
    {
        /// <summary>Steam's build id for the installed depot ("25185644"), or null without a manifest.</summary>
        public string BuildId { get; init; }

        /// <summary>True when Steam has an update downloaded-but-not-applied, or still to fetch.</summary>
        public bool UpdatePending { get; init; }

        /// <summary>Bytes still to download for the pending update. Zero when nothing is pending.</summary>
        public long PendingBytes { get; init; }

        /// <summary>The build Steam means to have installed here, when the manifest named one.</summary>
        public string TargetBuildId { get; init; }

        /// <summary>
        /// Steam's own StateFlags for the install: 4 is "fully installed and nothing queued",
        /// which is the only value that ends a download. Zero without a manifest.
        /// </summary>
        public long StateFlags { get; init; }

        /// <summary>Size of the download Steam last planned, straight out of the manifest.</summary>
        public long BytesToDownload { get; init; }

        /// <summary>How much of that download Steam has fetched so far.</summary>
        public long BytesDownloaded { get; init; }

        /// <summary>
        /// Size of the staging pass Steam runs after the last byte arrives, when the manifest
        /// carried one. The download counters stop moving while this one does, so both are
        /// watched before an update is called stalled.
        /// </summary>
        public long BytesToStage { get; init; }

        /// <summary>How much of that staging pass Steam has done so far.</summary>
        public long BytesStaged { get; init; }

        /// <summary>
        /// SHA-256 over the server exe and assembly_valheim.dll (each file's bytes plus its
        /// length). Null when neither file could be read.
        /// </summary>
        public string Fingerprint { get; init; }

        /// <summary>Full path of the appmanifest that was read, or null when none was found.</summary>
        public string ManifestPath { get; init; }

        /// <summary>
        /// The server executable this info was probed from. Kept so the launch guard can
        /// hash the binaries once when an identity arrives in a different form than the one
        /// that was stored last time. Null on infos built by hand in tests.
        /// </summary>
        public string ExePath { get; init; }

        public ServerBuildSource Source { get; init; }

        /// <summary>Steam's LastUpdated stamp for the install, when the manifest carried one.</summary>
        public DateTime? LastUpdatedUtc { get; init; }

        /// <summary>
        /// The value recorded as "the build we launched": the Steam build id when there is
        /// one, otherwise the binary fingerprint. Null when neither could be read.
        /// </summary>
        public string Identity => !string.IsNullOrWhiteSpace(BuildId) ? BuildId : Fingerprint;

        /// <summary>Short text for logs and Discord posts.</summary>
        public string Describe()
        {
            var id = Identity;
            if (string.IsNullOrWhiteSpace(id)) return "unknown build";
            var label = Source == ServerBuildSource.Manifest ? "build " + id : "fingerprint " + Short(id);
            return UpdatePending ? label + " (update pending)" : label;
        }

        /// <summary>First 12 characters of a fingerprint, which is plenty to tell two apart.</summary>
        public static string Short(string identity)
            => string.IsNullOrWhiteSpace(identity) || identity.Length <= 12 ? identity : identity[..12];
    }

    /// <summary>
    /// Reads the identity of the Valheim dedicated server install a profile points at.
    ///
    /// Steam records the installed build in "steamapps/appmanifest_896660.acf", which sits
    /// either above the install ("steamapps/common/Valheim dedicated server/valheim_server.exe"
    /// -> two levels up) or inside it, which is where steamcmd puts it. Both layouts are
    /// searched by walking up from the executable.
    ///
    /// Installs Steam never touched have no manifest at all, so the fallback is a content
    /// hash of the two files that actually change with a game update: valheim_server.exe and
    /// valheim_server_Data/Managed/assembly_valheim.dll.
    /// </summary>
    public static class ServerBuildTracker
    {
        /// <summary>Valheim Dedicated Server on Steam.</summary>
        public const string SteamAppId = "896660";

        public const string ManifestFileName = "appmanifest_" + SteamAppId + ".acf";

        /// <summary>The dedicated server executable, which is what makes a folder an install.</summary>
        private const string ServerExeName = "valheim_server.exe";

        /// <summary>Both folder separators, so a trailing one never hides a parent folder.</summary>
        private static readonly char[] SlashChars = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        private const string SteamAppsFolderName = "steamapps";

        /// <summary>The folder Steam puts every installed game in, inside a library's steamapps.</summary>
        private const string SteamCommonFolderName = "common";

        /// <summary>The Steam client's own list of libraries, in a library root and in its config folder.</summary>
        private const string LibraryFoldersFileName = "libraryfolders.vdf";

        /// <summary>Steam's StateUpdateRequired bit inside StateFlags (value 6 = installed + update required).</summary>
        private const int StateUpdateRequired = 2;

        // How far up from the executable a steamapps folder is worth looking for. A Steam
        // install needs two levels; a few more costs nothing and covers nested layouts.
        private const int MaxParentLevels = 6;

        // The .acf is Valve's KeyValues text: "key"<tabs>"value", one per line. A tolerant
        // pattern beats a full parser here - it survives spacing changes and new keys.
        private static Regex Key(string name) => new(
            "\"" + Regex.Escape(name) + "\"\\s*\"([^\"]*)\"",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BuildIdKey = Key("buildid");
        private static readonly Regex StateFlagsKey = Key("StateFlags");
        private static readonly Regex BytesToDownloadKey = Key("BytesToDownload");
        private static readonly Regex BytesDownloadedKey = Key("BytesDownloaded");
        private static readonly Regex BytesToStageKey = Key("BytesToStage");
        private static readonly Regex BytesStagedKey = Key("BytesStaged");
        private static readonly Regex LastUpdatedKey = Key("LastUpdated");
        private static readonly Regex TargetBuildIdKey = Key("TargetBuildID");
        private static readonly Regex InstallDirKey = Key("installdir");

        // Every "path" entry in the Steam client's libraryfolders.vdf.
        private static readonly Regex LibraryPathKey = new(
            "\"path\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Probes the install that owns <paramref name="serverExePath"/>. Never throws: an
        /// unreadable install comes back with a null identity, which the launch guard treats
        /// as "nothing known changed".
        /// </summary>
        /// <param name="serverExePath">Full path of valheim_server.exe.</param>
        /// <param name="alwaysFingerprint">
        /// Hash the binaries even when Steam's manifest already answered. Off by default so a
        /// Steam install costs one small text read instead of hashing ~15 MB on every start.
        /// </param>
        public static ServerBuildInfo Probe(string serverExePath, bool alwaysFingerprint = false)
        {
            var manifestPath = FindManifest(serverExePath);

            // Without a manifest the binaries ARE the identity, so hash them then.
            var fingerprint = manifestPath == null || alwaysFingerprint
                ? Fingerprint(serverExePath)
                : null;

            if (manifestPath == null)
            {
                return new ServerBuildInfo
                {
                    BuildId = null,
                    UpdatePending = false,
                    PendingBytes = 0,
                    Fingerprint = fingerprint,
                    ManifestPath = null,
                    ExePath = serverExePath,
                    Source = fingerprint != null ? ServerBuildSource.Fingerprint : ServerBuildSource.Unknown,
                };
            }

            string text;
            try
            {
                text = File.ReadAllText(manifestPath);
            }
            catch
            {
                // The manifest exists but cannot be read (locked by Steam mid-write, or
                // permissions). Hash the binaries instead: the outer "fingerprint" local is
                // null on this path, because a manifest was found, so it has to be computed
                // here. Returning it null would hand the launch guard no identity at all,
                // which reads as "nothing changed" and quietly clears the guard.
                var fallback = fingerprint ?? Fingerprint(serverExePath);
                return new ServerBuildInfo
                {
                    Fingerprint = fallback,
                    ManifestPath = manifestPath,
                    ExePath = serverExePath,
                    Source = fallback != null ? ServerBuildSource.Fingerprint : ServerBuildSource.Unknown,
                };
            }

            var info = ParseManifest(text, manifestPath, fingerprint, serverExePath);

            // A manifest with no readable buildid tells us nothing about identity, so fall
            // back to the binaries rather than reporting an unknown build.
            if (info.BuildId == null && info.Fingerprint == null)
            {
                info = ParseManifest(text, manifestPath, Fingerprint(serverExePath), serverExePath);
            }

            return info;
        }

        /// <summary>
        /// Turns appmanifest text into a build info. Split out from <see cref="Probe"/> so the
        /// parsing can be proved against real .acf files without an install on disk.
        /// </summary>
        public static ServerBuildInfo ParseManifest(
            string manifestText, string manifestPath = null, string fingerprint = null, string serverExePath = null)
        {
            if (manifestText == null) manifestText = string.Empty;

            var buildId = ReadString(manifestText, BuildIdKey);
            var targetBuildId = ReadString(manifestText, TargetBuildIdKey);
            var stateFlags = ReadLong(manifestText, StateFlagsKey) ?? 0;
            var toDownload = ReadLong(manifestText, BytesToDownloadKey) ?? 0;
            var downloaded = ReadLong(manifestText, BytesDownloadedKey) ?? 0;
            var toStage = ReadLong(manifestText, BytesToStageKey) ?? 0;
            var staged = ReadLong(manifestText, BytesStagedKey) ?? 0;

            // BytesToDownload is the SIZE of the last download Steam planned, not what is
            // left of it: a finished install keeps the full number with BytesDownloaded
            // matching. What is still outstanding is the difference.
            var pendingBytes = Math.Max(0, toDownload - downloaded);

            var flagged = (stateFlags & StateUpdateRequired) != 0;
            var retargeted = !string.IsNullOrWhiteSpace(buildId)
                && !string.IsNullOrWhiteSpace(targetBuildId)
                && !string.Equals(buildId, targetBuildId, StringComparison.Ordinal);

            var pending = flagged || pendingBytes > 0 || retargeted;

            return new ServerBuildInfo
            {
                BuildId = string.IsNullOrWhiteSpace(buildId) ? null : buildId,
                UpdatePending = pending,
                PendingBytes = pending ? pendingBytes : 0,
                TargetBuildId = string.IsNullOrWhiteSpace(targetBuildId) ? null : targetBuildId,
                StateFlags = stateFlags,
                BytesToDownload = toDownload,
                BytesDownloaded = downloaded,
                BytesToStage = toStage,
                BytesStaged = staged,
                Fingerprint = fingerprint,
                ManifestPath = manifestPath,
                ExePath = serverExePath,
                Source = string.IsNullOrWhiteSpace(buildId)
                    ? (fingerprint != null ? ServerBuildSource.Fingerprint : ServerBuildSource.Unknown)
                    : ServerBuildSource.Manifest,
                LastUpdatedUtc = ReadUnixSeconds(manifestText, LastUpdatedKey),
            };
        }

        /// <summary>
        /// Walks up from the executable looking for "appmanifest_896660.acf": beside the
        /// install in its own "steamapps" folder (the steamcmd layout), or in the library's
        /// "steamapps" folder two levels up (the Steam library layout). Null when there is none.
        ///
        /// A manifest is only accepted when it really describes THIS install. steamcmd's own
        /// "&lt;install&gt;/steamapps/appmanifest_896660.acf" beside the exe is taken as read;
        /// every manifest found further up has to prove itself by carrying an "installdir"
        /// that matches the folder the exe sits in. Without that rule a second server folder
        /// under the same library (a copy at "common/vds-modded", an isolated instance, a hand
        /// copy in a subfolder) inherits a neighbour's manifest and reports a build it is not
        /// running, and an update aimed at it would go to the neighbour instead.
        /// </summary>
        public static string FindManifest(string serverExePath)
        {
            if (string.IsNullOrWhiteSpace(serverExePath)) return null;

            DirectoryInfo dir;
            try
            {
                dir = new FileInfo(Path.GetFullPath(serverExePath)).Directory;
            }
            catch
            {
                return null;
            }

            var installName = dir?.Name;

            for (var level = 0; dir != null && level <= MaxParentLevels; level++, dir = dir.Parent)
            {
                // The steamcmd layout: "<install>/steamapps/appmanifest_896660.acf", which
                // steamcmd writes beside the exe it just installed.
                var nested = Path.Combine(dir.FullName, SteamAppsFolderName, ManifestFileName);
                if (SafeFileExists(nested) && (level == 0 || InstallDirMatches(nested, installName)))
                {
                    return nested;
                }

                // The Steam library layout: the manifest sits in the steamapps folder itself,
                // two levels above "steamapps/common/<install>/valheim_server.exe".
                if (string.Equals(dir.Name, SteamAppsFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    // Sitting one level under "common" is not proof on its own: a second
                    // server folder beside the real install ("common/vds-modded") sits exactly
                    // there and would otherwise report the neighbour's build and its pending
                    // update. The manifest has to name the folder the executable is in.
                    var here = Path.Combine(dir.FullName, ManifestFileName);
                    if (SafeFileExists(here) && InstallDirMatches(here, installName))
                    {
                        return here;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// True when the manifest names the folder the executable actually sits in. Used to
        /// accept a manifest found somewhere other than the two canonical layouts. An
        /// unreadable manifest answers false: it cannot prove anything.
        /// </summary>
        private static bool InstallDirMatches(string manifestPath, string installFolderName)
        {
            if (string.IsNullOrWhiteSpace(installFolderName)) return false;

            string text;
            try { text = File.ReadAllText(manifestPath); }
            catch { return false; }

            var named = ReadString(text, InstallDirKey);
            return !string.IsNullOrWhiteSpace(named)
                && string.Equals(named, installFolderName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Decides how the install that owns <paramref name="serverExePath"/> got there, which
        /// is what says whether BakaLoader may update it with steamcmd (its own folder) or has
        /// to hand the job to the Steam client (a library the client owns). Pure and fast: it
        /// reads two small text files at most and never hashes anything.
        /// </summary>
        public static ServerInstallInfo ClassifyInstall(string serverExePath)
        {
            var unknown = new ServerInstallInfo { Kind = ServerInstallKind.Unknown };

            if (string.IsNullOrWhiteSpace(serverExePath))
            {
                unknown.Reason = "This profile has no server executable set yet.";
                return unknown;
            }

            string installDir;
            try { installDir = Path.GetDirectoryName(Path.GetFullPath(serverExePath)); }
            catch { installDir = null; }

            unknown.InstallDir = installDir;

            if (installDir == null)
            {
                unknown.Reason = "The server executable path could not be read.";
                return unknown;
            }

            var manifestPath = FindManifest(serverExePath);
            if (manifestPath == null)
            {
                // An isolated instance is a folder BakaLoader made itself, so blaming the host
                // for a hand copy would be wrong twice over. The install that CAN be updated is
                // the one it was provisioned from, so say which one that is.
                unknown.Reason = IsolatedInstanceReason(installDir)
                    ?? "This install has no Steam manifest, so it was copied here by hand rather than installed by Steam or steamcmd.";
                return unknown;
            }

            unknown.ManifestPath = manifestPath;

            var manifestDir = Path.GetDirectoryName(manifestPath);
            if (manifestDir == null)
            {
                unknown.Reason = "The Steam manifest for this install could not be read.";
                return unknown;
            }

            // A Steam client library: the manifest sits in a steamapps folder that carries the
            // client's own libraryfolders.vdf, is listed in the client's library list, or holds
            // the install under "common" the way the client lays every library out.
            var libraryRoot = Path.GetDirectoryName(manifestDir);
            var inLibrary =
                SafeFileExists(Path.Combine(manifestDir, LibraryFoldersFileName))
                || IsUnderSteamCommon(installDir, manifestDir)
                || IsRegisteredSteamLibrary(libraryRoot);

            if (inLibrary)
            {
                return new ServerInstallInfo
                {
                    Kind = ServerInstallKind.SteamLibrary,
                    InstallDir = installDir,
                    ManifestPath = manifestPath,
                    LibraryRoot = libraryRoot,
                };
            }

            // steamcmd writes its own steamapps folder inside the folder it installed into.
            if (SamePath(manifestDir, Path.Combine(installDir, SteamAppsFolderName)))
            {
                return new ServerInstallInfo
                {
                    Kind = ServerInstallKind.Standalone,
                    InstallDir = installDir,
                    ManifestPath = manifestPath,
                };
            }

            unknown.Reason = "The Steam manifest for this install sits somewhere BakaLoader does not recognise, so it cannot tell who owns the folder.";
            return unknown;
        }

        /// <summary>
        /// A plain sentence for an install BakaLoader provisioned itself, naming the base
        /// install it was copied from when that folder can still be found beside it. Null when
        /// the folder is not one of BakaLoader's isolated instances.
        /// </summary>
        private static string IsolatedInstanceReason(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir)) return null;

            string parentName;
            string instancesRoot;
            try
            {
                var parent = Directory.GetParent(installDir.TrimEnd(SlashChars));
                parentName = parent?.Name;
                instancesRoot = parent?.FullName;
            }
            catch
            {
                return null;
            }

            if (!string.Equals(parentName, InstallIsolationService.InstancesRootName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var baseName = FindBaseInstallName(instancesRoot);
            return baseName != null
                ? "This is an isolated copy BakaLoader made from \"" + baseName
                    + "\". Update that install, then provision this one again."
                : "This is an isolated copy BakaLoader made from another install. Update the install it was made from, then provision this one again.";
        }

        /// <summary>
        /// The name of the only server install sitting beside the instances root, which is
        /// where an isolated copy's base lives. Null when there is not exactly one.
        /// </summary>
        private static string FindBaseInstallName(string instancesRoot)
        {
            try
            {
                var siblings = Directory.GetParent(instancesRoot);
                if (siblings == null) return null;

                string found = null;
                foreach (var folder in siblings.EnumerateDirectories())
                {
                    if (string.Equals(folder.Name, InstallIsolationService.InstancesRootName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!SafeFileExists(Path.Combine(folder.FullName, ServerExeName))) continue;
                    if (found != null) return null;   // two candidates name neither
                    found = folder.Name;
                }

                return found;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when the install sits at "&lt;steamapps&gt;/common/&lt;install&gt;", Steam's own shape.</summary>
        private static bool IsUnderSteamCommon(string installDir, string manifestDir)
        {
            try
            {
                var parent = Path.GetDirectoryName(installDir);
                return parent != null
                    && string.Equals(Path.GetFileName(parent), SteamCommonFolderName, StringComparison.OrdinalIgnoreCase)
                    && SamePath(Path.GetDirectoryName(parent), manifestDir);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// True when the Steam client on this machine lists the folder as one of its
        /// libraries. Reads the client's own libraryfolders.vdf; any failure answers false.
        /// </summary>
        private static bool IsRegisteredSteamLibrary(string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot)) return false;

            try
            {
                var steamPath = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (string.IsNullOrWhiteSpace(steamPath)) return false;

                var listing = Path.Combine(steamPath, "config", LibraryFoldersFileName);
                if (!SafeFileExists(listing)) return false;

                var text = File.ReadAllText(listing);
                foreach (Match m in LibraryPathKey.Matches(text))
                {
                    var path = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (SamePath(path, libraryRoot)) return true;
                }
            }
            catch
            {
                // No Steam client, no permission, or a listing we cannot parse: the caller
                // still has the two layout rules above to go on.
            }

            return false;
        }

        /// <summary>Compares two folder paths the way Windows does, trailing slashes and all.</summary>
        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd('\\', '/'),
                    Path.GetFullPath(right).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// SHA-256 over the two files a game update always changes: the server executable and
        /// assembly_valheim.dll. Each file contributes its length as well as its bytes, so a
        /// truncated copy can never hash the same as the real thing. Null when neither file
        /// could be read.
        /// </summary>
        public static string Fingerprint(string serverExePath)
        {
            if (string.IsNullOrWhiteSpace(serverExePath)) return null;

            string exeFull;
            string installDir;
            try
            {
                exeFull = Path.GetFullPath(serverExePath);
                installDir = Path.GetDirectoryName(exeFull);
            }
            catch
            {
                return null;
            }
            if (installDir == null) return null;

            var managedDll = Path.Combine(
                installDir, "valheim_server_Data", "Managed", "assembly_valheim.dll");

            using var sha = SHA256.Create();
            var any = false;

            foreach (var path in new[] { exeFull, managedDll })
            {
                if (!SafeFileExists(path)) continue;
                try
                {
                    var length = new FileInfo(path).Length;
                    // The file's own name and length go into the hash first, so two installs
                    // that differ only in which of the two files exists never collide.
                    var header = Encoding.UTF8.GetBytes(
                        Path.GetFileName(path).ToLowerInvariant() + ":" + length.ToString(CultureInfo.InvariantCulture) + "\n");
                    sha.TransformBlock(header, 0, header.Length, null, 0);

                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                    any = true;
                }
                catch
                {
                    // A file we cannot read simply does not contribute; the other one still
                    // identifies the install well enough to notice a swap.
                }
            }

            if (!any) return null;

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash).ToLowerInvariant();
        }

        // ---------------------------------------------------------------- parsing helpers

        private static string ReadString(string text, Regex key)
        {
            var m = key.Match(text);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static long? ReadLong(string text, Regex key)
        {
            var raw = ReadString(text, key);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static DateTime? ReadUnixSeconds(string text, Regex key)
        {
            var seconds = ReadLong(text, key);
            if (seconds == null || seconds <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime; }
            catch { return null; }
        }

        private static bool SafeFileExists(string path)
        {
            try { return File.Exists(path); }
            catch { return false; }
        }
    }

    /// <summary>What the launch guard decided about starting a server right now.</summary>
    public enum LaunchGuardOutcome
    {
        /// <summary>Nothing changed and nothing is queued: start as usual.</summary>
        Proceed = 0,

        /// <summary>Steam has an update for the server that has not been applied yet.</summary>
        UpdatePending = 1,

        /// <summary>The server on disk is not the build this profile last ran.</summary>
        BuildChanged = 2,
    }

    /// <summary>
    /// The rule that decides whether starting a server needs the host's say-so first.
    /// Pure and side-effect free, so it is the same answer in the UI, in an automatic
    /// restart, and in a test.
    /// </summary>
    public static class LaunchGuard
    {
        /// <param name="current">What the install looks like right now.</param>
        /// <param name="lastLaunchedBuild">The build id (or fingerprint) this profile last started.</param>
        /// <param name="hasWorlds">True when the profile's save folder already holds at least one world.</param>
        public static LaunchGuardOutcome Decide(ServerBuildInfo current, string lastLaunchedBuild, bool hasWorlds)
            => Decide(current, lastLaunchedBuild, null, hasWorlds);

        /// <summary>
        /// The same decision with the second half of the stored identity available. A profile
        /// records both what Steam called the build and what the binaries hashed to, so when
        /// the manifest cannot be read at this start the guard still has a fingerprint to
        /// compare a fingerprint against instead of having to say nothing changed.
        /// </summary>
        /// <param name="current">What the install looks like right now.</param>
        /// <param name="lastLaunchedBuild">The build id (or fingerprint) this profile last started.</param>
        /// <param name="lastLaunchedFingerprint">
        /// The binary fingerprint taken at that same launch, or null for a profile last
        /// launched by a version of BakaLoader that did not record one.
        /// </param>
        /// <param name="hasWorlds">True when the profile's save folder already holds at least one world.</param>
        public static LaunchGuardOutcome Decide(
            ServerBuildInfo current, string lastLaunchedBuild, string lastLaunchedFingerprint, bool hasWorlds)
        {
            // Nothing readable about the install: never block a start on a guess.
            if (current == null) return LaunchGuardOutcome.Proceed;

            // A queued update outranks everything else - starting now runs the OLD build,
            // and players whose game already updated cannot join it.
            if (current.UpdatePending) return LaunchGuardOutcome.UpdatePending;

            var identity = current.Identity;
            if (string.IsNullOrWhiteSpace(identity)) return LaunchGuardOutcome.Proceed;

            if (string.IsNullOrWhiteSpace(lastLaunchedBuild))
            {
                // Nothing was stored under the build id, but a fingerprint may still have been:
                // it is the same launch, recorded twice, so either half is enough to decide on.
                if (!string.IsNullOrWhiteSpace(lastLaunchedFingerprint))
                    return CompareFingerprints(current, lastLaunchedFingerprint);

                // First launch this profile has ever been through the guard. A brand new
                // profile has nothing to lose; one that already owns worlds might, because
                // this could be the first start since a game update.
                return hasWorlds ? LaunchGuardOutcome.BuildChanged : LaunchGuardOutcome.Proceed;
            }

            if (string.Equals(identity, lastLaunchedBuild, StringComparison.OrdinalIgnoreCase))
            {
                return LaunchGuardOutcome.Proceed;
            }

            // The identity is a Steam build id when the manifest could be read and a binary
            // fingerprint when it could not, so it changes shape whenever the manifest starts
            // or stops being readable. That is a change in how we looked, not a change in what
            // is installed, and it must not be reported as a new build on its own. When the
            // stored value and the new one are different kinds, compare like with like: the
            // binaries can always be hashed again, and a build id can only be compared to a
            // build id. With nothing comparable left, say nothing changed rather than invent it.
            if (LooksLikeFingerprint(lastLaunchedBuild) != LooksLikeFingerprint(identity))
            {
                var comparable = LooksLikeFingerprint(lastLaunchedBuild)
                    ? current.Fingerprint ?? ServerBuildTracker.Fingerprint(current.ExePath)
                    : current.BuildId;

                if (string.IsNullOrWhiteSpace(comparable))
                {
                    // The mirror image of the case above: a build id was stored and the manifest
                    // is unreadable now, so there is no build id left to compare it to. The
                    // fingerprint recorded beside it at that launch is comparable, and using it
                    // is the difference between catching a game update that landed in the same
                    // moment the manifest went quiet and waving it through.
                    if (!string.IsNullOrWhiteSpace(lastLaunchedFingerprint))
                        return CompareFingerprints(current, lastLaunchedFingerprint);

                    return LaunchGuardOutcome.Proceed;
                }

                return string.Equals(comparable, lastLaunchedBuild, StringComparison.OrdinalIgnoreCase)
                    ? LaunchGuardOutcome.Proceed
                    : LaunchGuardOutcome.BuildChanged;
            }

            return LaunchGuardOutcome.BuildChanged;
        }

        /// <summary>
        /// Compares what the binaries hash to right now against the fingerprint stored at the
        /// last launch. A fingerprint that cannot be taken at all decides nothing, so the start
        /// goes ahead rather than being blocked on a guess.
        /// </summary>
        private static LaunchGuardOutcome CompareFingerprints(ServerBuildInfo current, string lastLaunchedFingerprint)
        {
            var now = current.Fingerprint ?? ServerBuildTracker.Fingerprint(current.ExePath);
            if (string.IsNullOrWhiteSpace(now)) return LaunchGuardOutcome.Proceed;

            // The binaries really are different, which is a build change however the identity
            // was written down. Same answer the build id comparison gives, and for the same
            // reason: this launch is about to convert whatever is on disk.
            return string.Equals(now, lastLaunchedFingerprint, StringComparison.OrdinalIgnoreCase)
                ? LaunchGuardOutcome.Proceed
                : LaunchGuardOutcome.BuildChanged;
        }

        /// <summary>
        /// True for a binary fingerprint (64 hex characters of SHA-256) as opposed to a Steam
        /// build id, which is a short run of digits.
        /// </summary>
        private static bool LooksLikeFingerprint(string identity)
        {
            if (identity == null || identity.Length != 64) return false;

            foreach (var c in identity)
            {
                var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }

            return true;
        }

        /// <summary>The wire token for an outcome (what the WebUI switches on).</summary>
        public static string Token(LaunchGuardOutcome outcome) => outcome switch
        {
            LaunchGuardOutcome.UpdatePending => "updatePending",
            LaunchGuardOutcome.BuildChanged => "buildChanged",
            _ => "proceed",
        };
    }
}
