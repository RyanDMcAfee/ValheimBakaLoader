using System;
using System.Collections.Generic;
using ValheimBakaLoader.Properties;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// One mod recorded in a profile's manifest, so a server's mod set is tracked
    /// and reconstructable on restore/export. Owner+Name+Version identify a
    /// Thunderstore package the same way the mod scanner resolves installed mods.
    /// </summary>
    public class ModManifestEntry
    {
        [Newtonsoft.Json.JsonProperty("owner")] public string Owner { get; set; }
        [Newtonsoft.Json.JsonProperty("name")] public string Name { get; set; }
        [Newtonsoft.Json.JsonProperty("version")] public string Version { get; set; }
    }

    /// <summary>
    /// One saved server profile with all defaults applied. Round-trips to
    /// disk through <see cref="ServerPreferencesFile"/>.
    /// </summary>
    public class ServerPreferences : INamedEntry
    {
        string INamedEntry.EntryName => ProfileName;

        public string ProfileName { get; set; } = Resources.DefaultServerProfileName;

        public DateTime LastSaved { get; set; } = DateTime.UnixEpoch;

        public string Name { get; set; }

        public string Password { get; set; }

        public string WorldName { get; set; }

        public bool Public { get; set; }

        public int Port { get; set; } = int.Parse(Resources.DefaultServerPort);

        public bool Crossplay { get; set; }

        public int SaveInterval { get; set; } = int.Parse(Resources.DefaultSaveInterval);

        public int BackupCount { get; set; } = int.Parse(Resources.DefaultBackupCount);

        public int BackupIntervalShort { get; set; } = int.Parse(Resources.DefaultBackupIntervalShort);

        public int BackupIntervalLong { get; set; } = int.Parse(Resources.DefaultBackupIntervalLong);

        public bool AutoStart { get; set; }

        public string AdditionalArgs { get; set; }

        public string ServerExePath { get; set; }

        public string SaveDataFolderPath { get; set; }

        public bool WriteServerLogsToFile { get; set; } = true;

        public bool AutoRestart { get; set; }

        public int AutoRestartDelay { get; set; } = 10;

        public bool EmptyServerRestart { get; set; }

        public int EmptyServerRestartDelayMinutes { get; set; } = 5;

        public bool ScheduledRestart { get; set; }

        public int ScheduledRestartHours { get; set; } = 6;

        public bool RconEnabled { get; set; }

        public int RconPort { get; set; } = 25575;

        public string RconPassword { get; set; }

        // -- Multi-server isolation --

        /// <summary>Soft-deleted: hidden from the chip strip, kept for restore. Touches nothing on disk.</summary>
        public bool Archived { get; set; }

        /// <summary>
        /// This profile owns a BakaLoader-provisioned install directory (junctioned game
        /// files + its own BepInEx/plugins). Marks the install as safe to reclaim on delete.
        /// </summary>
        public bool IsolatedInstall { get; set; }

        /// <summary>The mods this server was last seen running, for restore/export.</summary>
        public List<ModManifestEntry> ModManifest { get; set; }

        public static ServerPreferences FromFile(ServerPreferencesFile file)
        {
            var defaults = new ServerPreferences();
            if (file is null) return defaults;

            return new ServerPreferences
            {
                ProfileName = string.IsNullOrWhiteSpace(file.ProfileName) ? defaults.ProfileName : file.ProfileName,
                LastSaved = file.LastSaved ?? defaults.LastSaved,
                Name = file.Name ?? defaults.Name,
                Password = file.Password ?? defaults.Password,
                WorldName = file.WorldName ?? defaults.WorldName,
                Public = file.Community ?? defaults.Public,
                Port = file.Port ?? defaults.Port,
                Crossplay = file.Crossplay ?? defaults.Crossplay,
                SaveInterval = file.SaveInterval ?? defaults.SaveInterval,
                BackupCount = file.BackupCount ?? defaults.BackupCount,
                BackupIntervalShort = file.BackupIntervalShort ?? defaults.BackupIntervalShort,
                BackupIntervalLong = file.BackupIntervalLong ?? defaults.BackupIntervalLong,
                AutoStart = file.AutoStart ?? defaults.AutoStart,
                AdditionalArgs = file.AdditionalArgs ?? defaults.AdditionalArgs,
                ServerExePath = file.ServerExePath ?? defaults.ServerExePath,
                SaveDataFolderPath = file.SaveDataFolderPath ?? defaults.SaveDataFolderPath,
                WriteServerLogsToFile = file.WriteServerLogsToFile ?? defaults.WriteServerLogsToFile,
                AutoRestart = file.AutoRestart ?? defaults.AutoRestart,
                AutoRestartDelay = file.AutoRestartDelay ?? defaults.AutoRestartDelay,
                EmptyServerRestart = file.EmptyServerRestart ?? defaults.EmptyServerRestart,
                EmptyServerRestartDelayMinutes = file.EmptyServerRestartDelayMinutes ?? defaults.EmptyServerRestartDelayMinutes,
                ScheduledRestart = file.ScheduledRestart ?? defaults.ScheduledRestart,
                ScheduledRestartHours = file.ScheduledRestartHours ?? defaults.ScheduledRestartHours,
                RconEnabled = file.RconEnabled ?? defaults.RconEnabled,
                RconPort = file.RconPort ?? defaults.RconPort,
                RconPassword = file.RconPassword ?? defaults.RconPassword,
                Archived = file.Archived ?? defaults.Archived,
                IsolatedInstall = file.IsolatedInstall ?? defaults.IsolatedInstall,
                ModManifest = file.ModManifest ?? defaults.ModManifest,
            };
        }

        public ServerPreferencesFile ToFile() => new()
        {
            ProfileName = ProfileName,
            LastSaved = LastSaved,
            Name = Name,
            Password = Password,
            WorldName = WorldName,
            Community = Public,
            Port = Port,
            Crossplay = Crossplay,
            SaveInterval = SaveInterval,
            BackupCount = BackupCount,
            BackupIntervalShort = BackupIntervalShort,
            BackupIntervalLong = BackupIntervalLong,
            AutoStart = AutoStart,
            AdditionalArgs = AdditionalArgs,
            ServerExePath = ServerExePath,
            SaveDataFolderPath = SaveDataFolderPath,
            WriteServerLogsToFile = WriteServerLogsToFile,
            AutoRestart = AutoRestart,
            AutoRestartDelay = AutoRestartDelay,
            EmptyServerRestart = EmptyServerRestart,
            EmptyServerRestartDelayMinutes = EmptyServerRestartDelayMinutes,
            ScheduledRestart = ScheduledRestart,
            ScheduledRestartHours = ScheduledRestartHours,
            RconEnabled = RconEnabled,
            RconPort = RconPort,
            RconPassword = RconPassword,
            Archived = Archived,
            IsolatedInstall = IsolatedInstall,
            ModManifest = ModManifest,
        };
    }
}
