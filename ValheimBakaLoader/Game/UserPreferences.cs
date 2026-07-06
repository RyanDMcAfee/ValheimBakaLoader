using System.Collections.Generic;
using System.Linq;
using ValheimBakaLoader.Properties;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// The app-wide settings as the rest of the code sees them: no nulls,
    /// every property carrying either the saved value or its default.
    /// Round-trips to disk through <see cref="UserPreferencesFile"/>.
    /// </summary>
    public class UserPreferences
    {
        public static UserPreferences GetDefault() => new();

        public string ServerExePath { get; set; } = Resources.DefaultServerPath;

        public string SaveDataFolderPath { get; set; } = Resources.DefaultValheimSaveFolder;

        public bool CheckForUpdates { get; set; } = true;

        public bool AutoUpdateMods { get; set; }

        public bool AutoUpdateBakaLoader { get; set; } = true;

        public bool StartWithWindows { get; set; }

        // Anonymous usage heartbeat (install count / servers online); see HeartbeatService.
        public bool ShareAnonymousStats { get; set; } = true;

        public bool StartMinimized { get; set; }

        public bool SaveProfileOnStart { get; set; } = true;

        public bool WriteApplicationLogsToFile { get; set; } = true;

        public bool EnablePasswordValidation { get; set; } = true;

        public bool DarkMode { get; set; } = true;

        // Swaps the Norse-lore UI terminology for plain English (off by default).
        public bool PlainTerminology { get; set; }

        // True once the first-launch setup wizard has been finished (or skipped).
        public bool SetupCompleted { get; set; }

        public string DiscordWebhookUrl { get; set; }

        public string DiscordWebhookThreadId { get; set; }

        public List<ServerPreferences> Servers { get; set; } = new();

        public List<WorldPreferences> Worlds { get; set; } = new();

        /// <summary>
        /// Hydrates preferences from a deserialized file, substituting the
        /// default for any key the file doesn't carry.
        /// </summary>
        public static UserPreferences FromFile(UserPreferencesFile file)
        {
            var defaults = new UserPreferences();
            if (file is null) return defaults;

            return new UserPreferences
            {
                ServerExePath = file.ServerExePath ?? defaults.ServerExePath,
                SaveDataFolderPath = file.SaveDataFolderPath ?? defaults.SaveDataFolderPath,
                CheckForUpdates = file.CheckForUpdates ?? defaults.CheckForUpdates,
                AutoUpdateMods = file.AutoUpdateMods ?? defaults.AutoUpdateMods,
                AutoUpdateBakaLoader = file.AutoUpdateBakaLoader ?? defaults.AutoUpdateBakaLoader,
                StartWithWindows = file.StartWithWindows ?? defaults.StartWithWindows,
                ShareAnonymousStats = file.ShareAnonymousStats ?? defaults.ShareAnonymousStats,
                StartMinimized = file.StartMinimized ?? defaults.StartMinimized,
                SaveProfileOnStart = file.SaveProfileOnStart ?? defaults.SaveProfileOnStart,
                WriteApplicationLogsToFile = file.WriteApplicationLogsToFile ?? defaults.WriteApplicationLogsToFile,
                EnablePasswordValidation = file.EnablePasswordValidation ?? defaults.EnablePasswordValidation,
                DarkMode = file.DarkMode ?? defaults.DarkMode,
                PlainTerminology = file.PlainTerminology ?? defaults.PlainTerminology,
                SetupCompleted = file.SetupCompleted ?? defaults.SetupCompleted,
                DiscordWebhookUrl = file.DiscordWebhookUrl ?? defaults.DiscordWebhookUrl,
                DiscordWebhookThreadId = file.DiscordWebhookThreadId ?? defaults.DiscordWebhookThreadId,

                Servers = (file.Servers ?? new())
                    .Where(s => s != null)
                    .Select(ServerPreferences.FromFile)
                    .DistinctBy(s => s.ProfileName)
                    .ToList(),

                Worlds = (file.Worlds ?? new())
                    .Where(w => w != null)
                    .Select(WorldPreferences.FromFile)
                    .DistinctBy(w => w.WorldName)
                    .ToList(),
            };
        }

        /// <summary>
        /// Produces the on-disk model, discarding nameless or duplicate
        /// server/world entries along the way.
        /// </summary>
        public UserPreferencesFile ToFile() => new()
        {
            ServerExePath = ServerExePath,
            SaveDataFolderPath = SaveDataFolderPath,
            CheckForUpdates = CheckForUpdates,
            AutoUpdateMods = AutoUpdateMods,
            AutoUpdateBakaLoader = AutoUpdateBakaLoader,
            StartWithWindows = StartWithWindows,
            ShareAnonymousStats = ShareAnonymousStats,
            StartMinimized = StartMinimized,
            SaveProfileOnStart = SaveProfileOnStart,
            WriteApplicationLogsToFile = WriteApplicationLogsToFile,
            EnablePasswordValidation = EnablePasswordValidation,
            DarkMode = DarkMode,
            PlainTerminology = PlainTerminology,
            SetupCompleted = SetupCompleted,
            DiscordWebhookUrl = DiscordWebhookUrl,
            DiscordWebhookThreadId = DiscordWebhookThreadId,

            Servers = (Servers ?? new())
                .Select(s => s.ToFile())
                .Where(s => !string.IsNullOrWhiteSpace(s.ProfileName))
                .DistinctBy(s => s.ProfileName)
                .ToList(),

            Worlds = (Worlds ?? new())
                .Select(w => w.ToFile())
                .Where(w => !string.IsNullOrWhiteSpace(w.WorldName))
                .DistinctBy(w => w.WorldName)
                .ToList(),
        };
    }
}
