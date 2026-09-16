using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// Read-only view of everything needed to boot one dedicated server:
    /// identity, networking, save/backup cadence, restart automation, RCON,
    /// and world-generation rules.
    /// </summary>
    public interface IValheimServerOptions
    {
        string Name { get; }

        string Password { get; }

        string WorldName { get; }

        bool Public { get; }

        int Port { get; }

        bool Crossplay { get; }

        int SaveInterval { get; }

        int Backups { get; }

        int BackupShort { get; }

        int BackupLong { get; }

        string AdditionalArgs { get; }

        string ServerExePath { get; }

        string SaveDataFolderPath { get; }

        bool LogToFile { get; }

        // Custom folder for the session log files; null/blank = the app default.
        string LogFolderPath { get; }

        /// <summary>
        /// Steam build id (or binary fingerprint) of the server this profile last started.
        /// Null before the first guarded launch. Read by the launch guard, never by the game.
        /// </summary>
        string LastLaunchedServerBuild { get; }

        /// <summary>
        /// Binary fingerprint of the server this profile last started, taken at the same moment
        /// as <see cref="LastLaunchedServerBuild"/>. The guard falls back to it when the Steam
        /// manifest cannot be read at the next start. Null before the first guarded launch.
        /// </summary>
        string LastLaunchedServerFingerprint { get; }

        /// <summary>Game version that server reported ("1.0.7"), or null when unknown.</summary>
        string LastLaunchedGameVersion { get; }

        bool AutoRestart { get; }

        int AutoRestartDelay { get; }

        bool EmptyServerRestart { get; }

        int EmptyServerRestartDelayMinutes { get; }

        bool ScheduledRestart { get; }

        int ScheduledRestartHours { get; }

        bool RconEnabled { get; }

        int RconPort { get; }

        string RconPassword { get; }

        bool LogFilteringDisabled { get; }

        Action<string> LogMessageHandler { get; }

        string WorldPreset { get; }

        Dictionary<string, string> WorldModifiers { get; }

        HashSet<string> WorldKeys { get; }
    }

    public class ValheimServerOptions : IValheimServerOptions
    {
        /// <summary>
        /// The whole profile preferences to runtime options mapping, with the user level
        /// fallbacks for the exe and save folder and the world's generation rules folded in.
        /// The caller adds the log handler, which is the only part that needs a live window.
        /// <para>
        /// This lives here rather than inline in the bridge so the mapping itself is covered:
        /// a field that is persisted but never copied across, such as the launch history the
        /// guard reads, looks fine in both halves and is still broken in the middle.
        /// </para>
        /// </summary>
        public static ValheimServerOptions FromPreferences(
            ServerPreferences serverPrefs,
            UserPreferences userPrefs,
            WorldPreferences worldPrefs)
        {
            if (serverPrefs == null) throw new ArgumentNullException(nameof(serverPrefs));
            if (userPrefs == null) throw new ArgumentNullException(nameof(userPrefs));

            var options = new ValheimServerOptions
            {
                Name = serverPrefs.Name,
                Password = serverPrefs.Password,
                PasswordValidation = userPrefs.EnablePasswordValidation,
                WorldName = serverPrefs.WorldName,
                Public = serverPrefs.Public,
                Port = serverPrefs.Port,
                Crossplay = serverPrefs.Crossplay,
                SaveInterval = serverPrefs.SaveInterval,
                Backups = serverPrefs.BackupCount,
                BackupShort = serverPrefs.BackupIntervalShort,
                BackupLong = serverPrefs.BackupIntervalLong,
                AdditionalArgs = serverPrefs.AdditionalArgs,
                ServerExePath = !string.IsNullOrWhiteSpace(serverPrefs.ServerExePath)
                    ? serverPrefs.ServerExePath
                    : userPrefs.ServerExePath,
                SaveDataFolderPath = !string.IsNullOrWhiteSpace(serverPrefs.SaveDataFolderPath)
                    ? serverPrefs.SaveDataFolderPath
                    : userPrefs.SaveDataFolderPath,
                LogToFile = serverPrefs.WriteServerLogsToFile,
                LogFolderPath = userPrefs.LogsFolderPath,
                AutoRestart = serverPrefs.AutoRestart,
                AutoRestartDelay = serverPrefs.AutoRestartDelay,
                EmptyServerRestart = serverPrefs.EmptyServerRestart,
                EmptyServerRestartDelayMinutes = serverPrefs.EmptyServerRestartDelayMinutes,
                ScheduledRestart = serverPrefs.ScheduledRestart,
                ScheduledRestartHours = serverPrefs.ScheduledRestartHours,
                RconEnabled = serverPrefs.RconEnabled,
                RconPort = serverPrefs.RconPort,
                RconPassword = serverPrefs.RconPassword,
                // The launch guard compares these against the build on disk. Leaving them out
                // makes every launch look like the first one on a changed build.
                LastLaunchedServerBuild = serverPrefs.LastLaunchedServerBuild,
                LastLaunchedServerFingerprint = serverPrefs.LastLaunchedServerFingerprint,
                LastLaunchedGameVersion = serverPrefs.LastLaunchedGameVersion,
            };

            if (worldPrefs != null)
            {
                if (!string.IsNullOrEmpty(worldPrefs.Preset))
                {
                    options.WorldPreset = worldPrefs.Preset;
                }
                else
                {
                    options.WorldModifiers = worldPrefs.Modifiers;
                }

                options.WorldKeys = worldPrefs.Keys;
            }

            return options;
        }

        /// <summary>
        /// Whether starting the server on <paramref name="saved"/> would run a different server
        /// from the one <paramref name="running"/> started. This is the rule behind "restart
        /// pending": the host saved something while the world was up, and it is not in force
        /// until the server comes back.
        /// <para>
        /// The answer is the command line itself, flag by flag, because the command line is the
        /// whole of what a start hands the game. Order does not count: two dictionaries holding
        /// the same modifiers in a different order launch the same server, and telling the host
        /// otherwise would raise a restart they do not need. When the command line cannot be
        /// built at all (a save folder that has gone missing is the usual reason) the fields a
        /// host can actually change are compared instead.
        /// </para>
        /// <para>
        /// Never throws and never guesses: anything it cannot answer is answered "no
        /// difference", because a false "restart pending" asks a host to take their world down
        /// for nothing.
        /// </para>
        /// </summary>
        public static bool RelaunchWouldDiffer(IValheimServerOptions running, IValheimServerOptions saved)
        {
            if (running == null || saved == null) return false;
            if (ReferenceEquals(running, saved)) return false;

            try
            {
                try
                {
                    var before = ValheimServer.DescribeLaunchParts(running);
                    var after = ValheimServer.DescribeLaunchParts(saved);
                    if (before != null && after != null) return !SameParts(before, after);
                }
                catch
                {
                    // A path that cannot be validated right now (an unplugged drive, a folder
                    // somebody moved) is not an answer about the settings, so fall through to
                    // the fields rather than calling that a difference.
                }

                return FieldsDiffer(running, saved);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// A short, stable fingerprint of what a start on these options would run, or null when
        /// it cannot be taken. The interface keys a dismissed "restart pending" on it, so waving
        /// the row away hides THAT set of saved settings and a later change raises it again.
        /// The command line carries the host's password, so what travels is the hash of it and
        /// never the line itself.
        /// </summary>
        public static string RelaunchSignature(IValheimServerOptions options)
        {
            if (options == null) return null;

            try
            {
                string canonical;
                try
                {
                    var parts = ValheimServer.DescribeLaunchParts(options);
                    if (parts == null) return null;
                    canonical = string.Join("\n", parts.OrderBy(part => part, StringComparer.Ordinal));
                }
                catch
                {
                    canonical = string.Join("\n", DescribeFields(options));
                }

                using var sha = System.Security.Cryptography.SHA256.Create();
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical));
                return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The same flags in any order are the same launch.</summary>
        private static bool SameParts(IReadOnlyList<string> before, IReadOnlyList<string> after)
        {
            if (before.Count != after.Count) return false;

            return before.OrderBy(part => part, StringComparer.Ordinal)
                .SequenceEqual(after.OrderBy(part => part, StringComparer.Ordinal), StringComparer.Ordinal);
        }

        /// <summary>
        /// The fallback comparison: everything a host can change that reaches the game, with the
        /// two collections compared as sets so their order is no more meaningful here than it is
        /// on the command line.
        /// </summary>
        private static bool FieldsDiffer(IValheimServerOptions running, IValheimServerOptions saved)
            => !DescribeFields(running).SequenceEqual(DescribeFields(saved), StringComparer.Ordinal);

        /// <summary>One line per compared field, in a fixed order.</summary>
        private static List<string> DescribeFields(IValheimServerOptions options)
        {
            var modifiers = options.WorldModifiers == null
                ? Array.Empty<string>()
                : options.WorldModifiers
                    .Select(pair => pair.Key + "=" + pair.Value)
                    .OrderBy(entry => entry, StringComparer.Ordinal)
                    .ToArray();

            var keys = options.WorldKeys == null
                ? Array.Empty<string>()
                : options.WorldKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

            return new List<string>
            {
                "world=" + options.WorldName,
                "port=" + options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "password=" + options.Password,
                "public=" + options.Public,
                "crossplay=" + options.Crossplay,
                "preset=" + options.WorldPreset,
                "modifiers=" + string.Join(",", modifiers),
                "keys=" + string.Join(",", keys),
                "args=" + options.AdditionalArgs,
                "saves=" + options.SaveDataFolderPath,
                "exe=" + options.ServerExePath,
            };
        }

        public string Name { get; set; }

        public string Password { get; set; }

        public bool PasswordValidation { get; set; }

        public string WorldName { get; set; }

        public bool Public { get; set; }

        public int Port { get; set; }

        public bool Crossplay { get; set; }

        public int SaveInterval { get; set; }

        public int Backups { get; set; }

        public int BackupShort { get; set; }

        public int BackupLong { get; set; }

        public string AdditionalArgs { get; set; }

        public string ServerExePath { get; set; }

        public string SaveDataFolderPath { get; set; }

        public bool LogToFile { get; set; }

        public string LogFolderPath { get; set; }

        public string LastLaunchedServerBuild { get; set; }

        public string LastLaunchedServerFingerprint { get; set; }

        public string LastLaunchedGameVersion { get; set; }

        public bool AutoRestart { get; set; }

        public int AutoRestartDelay { get; set; } = 10;

        public bool EmptyServerRestart { get; set; }

        public int EmptyServerRestartDelayMinutes { get; set; } = 5;

        public bool ScheduledRestart { get; set; }

        public int ScheduledRestartHours { get; set; } = 6;

        public bool RconEnabled { get; set; }

        public int RconPort { get; set; } = 25575;

        public string RconPassword { get; set; }

        // Not surfaced in the UI; exists so the log pipeline can be run raw.
        public bool LogFilteringDisabled { get; set; }

        public Action<string> LogMessageHandler { get; set; }

        public string WorldPreset { get; set; }

        public Dictionary<string, string> WorldModifiers { get; set; }

        public HashSet<string> WorldKeys { get; set; }

        /// <summary>
        /// The single gate every start path goes through. Throws
        /// <see cref="ArgumentException"/> with a player-readable message on
        /// the first rule that fails.
        /// </summary>
        public void Validate()
        {
            CheckIdentity();
            CheckPassword();
            CheckNetwork();
            CheckSchedules();
            CheckSaves();
            CheckWorldGen();
            CheckExtraArgs();

            // The path helpers throw their own messages when invalid.
            this.GetValidatedServerExe();
            this.GetValidatedSaveDataFolder();
        }

        private static void Require(bool condition, string problem)
        {
            if (!condition) throw new ArgumentException(problem);
        }

        private void CheckIdentity()
        {
            Require(!string.IsNullOrWhiteSpace(Name), "Give the server a name.");
            Require(!string.IsNullOrWhiteSpace(WorldName), "Give the world a name.");
            Require(Name != WorldName, $"The server name and the world name must differ (both are '{WorldName}').");
        }

        private void CheckPassword()
        {
            if (!PasswordValidation) return;

            if (string.IsNullOrWhiteSpace(Password))
            {
                Require(!Public, "Community (public) servers require a password. Set one, or turn off the Community Server option.");
                return;
            }

            Require(Password.Length >= 5, "The password needs at least 5 characters.");
            Require(!Password.Contains(Name), $"The password may not contain the server name ('{Name}').");
            Require(!Password.Contains(WorldName), $"The password may not contain the world name ('{WorldName}').");
        }

        private void CheckNetwork()
        {
            Require(Port is >= 1 and <= 65535, "The game port must be between 1 and 65535.");

            if (!RconEnabled) return;

            Require(RconPort is >= 1 and <= 65535, "The RCON port must be between 1 and 65535.");
            Require(RconPort != Port, $"The RCON port must differ from the game port ({Port}).");
        }

        private void CheckSchedules()
        {
            if (EmptyServerRestart)
            {
                Require(EmptyServerRestartDelayMinutes >= 1, "The empty-server restart delay needs to be at least 1 minute.");
            }

            if (ScheduledRestart)
            {
                Require(ScheduledRestartHours >= 1, "The scheduled restart interval needs to be at least 1 hour.");
            }
        }

        private void CheckSaves()
        {
            Require(SaveInterval >= 1, "The save interval must be greater than zero.");
            Require(BackupShort >= 1, "The short backup interval must be greater than zero.");
            Require(BackupLong >= 1, "The long backup interval must be greater than zero.");
            Require(SaveInterval <= BackupShort && SaveInterval <= BackupLong, "The save interval cannot exceed either backup interval.");
            Require(BackupShort <= BackupLong, "The short backup interval cannot exceed the long one.");
        }

        private void CheckWorldGen()
        {
            if (WorldPreset != null)
            {
                Require(WorldGen.Presets.Contains(WorldPreset),
                    $"'{WorldPreset}' is not a world preset. Choose one of: {string.Join(", ", WorldGen.Presets)}.");
                Require(WorldModifiers == null || WorldModifiers.Count == 0,
                    "World modifiers cannot be combined with a world preset.");
            }
            else if (WorldModifiers != null)
            {
                foreach (var (modifier, value) in WorldModifiers)
                {
                    Require(WorldGen.Modifiers.TryGetValue(modifier, out var allowed),
                        $"'{modifier}' is not a world modifier. Choose one of: {string.Join(", ", WorldGen.Modifiers.Keys)}.");
                    Require(allowed.Contains(value),
                        $"'{value}' is not a valid setting for the '{modifier}' modifier. Choose one of: {string.Join(", ", allowed)}.");
                }
            }

            if (WorldKeys == null) return;

            foreach (var key in WorldKeys)
            {
                Require(WorldGen.Switches.Contains(key),
                    $"'{key}' is not a world key. Choose one of: {string.Join(", ", WorldGen.Switches)}.");
            }
        }

        private void CheckExtraArgs()
        {
            // The game's own -logFile flag would steal the output stream that
            // BakaLoader parses for player/server events, so it's not allowed.
            var hasLogFileFlag = AdditionalArgs?.Contains("-logfile", StringComparison.OrdinalIgnoreCase) ?? false;
            Require(!hasLogFileFlag, "The '-logFile' server argument is not supported. Enable writing server logs to file in the profile settings instead.");
        }
    }
}
