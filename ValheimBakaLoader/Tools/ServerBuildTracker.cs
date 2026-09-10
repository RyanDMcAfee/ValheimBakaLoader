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

        /// <summary>
        /// SHA-256 over the server exe and assembly_valheim.dll (each file's bytes plus its
        /// length). Null when neither file could be read.
        /// </summary>
        public string Fingerprint { get; init; }

        /// <summary>Full path of the appmanifest that was read, or null when none was found.</summary>
        public string ManifestPath { get; init; }

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

        private const string SteamAppsFolderName = "steamapps";

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
        private static readonly Regex LastUpdatedKey = Key("LastUpdated");
        private static readonly Regex TargetBuildIdKey = Key("TargetBuildID");

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
                // permissions). Fall back to the binaries rather than guessing.
                return new ServerBuildInfo
                {
                    Fingerprint = fingerprint,
                    ManifestPath = manifestPath,
                    Source = fingerprint != null ? ServerBuildSource.Fingerprint : ServerBuildSource.Unknown,
                };
            }

            var info = ParseManifest(text, manifestPath, fingerprint);

            // A manifest with no readable buildid tells us nothing about identity, so fall
            // back to the binaries rather than reporting an unknown build.
            if (info.BuildId == null && info.Fingerprint == null)
            {
                info = ParseManifest(text, manifestPath, Fingerprint(serverExePath));
            }

            return info;
        }

        /// <summary>
        /// Turns appmanifest text into a build info. Split out from <see cref="Probe"/> so the
        /// parsing can be proved against real .acf files without an install on disk.
        /// </summary>
        public static ServerBuildInfo ParseManifest(string manifestText, string manifestPath = null, string fingerprint = null)
        {
            if (manifestText == null) manifestText = string.Empty;

            var buildId = ReadString(manifestText, BuildIdKey);
            var targetBuildId = ReadString(manifestText, TargetBuildIdKey);
            var stateFlags = ReadLong(manifestText, StateFlagsKey) ?? 0;
            var toDownload = ReadLong(manifestText, BytesToDownloadKey) ?? 0;
            var downloaded = ReadLong(manifestText, BytesDownloadedKey) ?? 0;

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
                Fingerprint = fingerprint,
                ManifestPath = manifestPath,
                Source = string.IsNullOrWhiteSpace(buildId)
                    ? (fingerprint != null ? ServerBuildSource.Fingerprint : ServerBuildSource.Unknown)
                    : ServerBuildSource.Manifest,
                LastUpdatedUtc = ReadUnixSeconds(manifestText, LastUpdatedKey),
            };
        }

        /// <summary>
        /// Walks up from the executable looking for "appmanifest_896660.acf": inside a
        /// "steamapps" folder on the way up (the Steam library layout), or in a "steamapps"
        /// child of any folder on the way up (the steamcmd layout). Null when there is none.
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

            for (var level = 0; dir != null && level <= MaxParentLevels; level++, dir = dir.Parent)
            {
                // The steamcmd layout: "<install>/steamapps/appmanifest_896660.acf".
                var nested = Path.Combine(dir.FullName, SteamAppsFolderName, ManifestFileName);
                if (SafeFileExists(nested)) return nested;

                // The Steam library layout: the manifest sits in the steamapps folder itself,
                // two levels above "steamapps/common/<install>/valheim_server.exe".
                if (string.Equals(dir.Name, SteamAppsFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    var here = Path.Combine(dir.FullName, ManifestFileName);
                    if (SafeFileExists(here)) return here;
                }
            }

            return null;
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
                // First launch this profile has ever been through the guard. A brand new
                // profile has nothing to lose; one that already owns worlds might, because
                // this could be the first start since a game update.
                return hasWorlds ? LaunchGuardOutcome.BuildChanged : LaunchGuardOutcome.Proceed;
            }

            return string.Equals(identity, lastLaunchedBuild, StringComparison.OrdinalIgnoreCase)
                ? LaunchGuardOutcome.Proceed
                : LaunchGuardOutcome.BuildChanged;
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
