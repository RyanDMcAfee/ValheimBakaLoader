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
