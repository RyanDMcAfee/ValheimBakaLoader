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

        // Master switch for the Herald hall: single self-editing Discord status post.
        public bool DiscordSharingEnabled { get; set; }

        // Include the server's public IP:port in the status post.
        public bool DiscordShareAddress { get; set; } = true;

        // Include the server password in the status post (off by default - anyone in the channel can see it).
        public bool DiscordSharePassword { get; set; }

        // Also post one-off event embeds (server started/stopped/crashed, player joined/left).
        public bool DiscordEventPosts { get; set; }

        // Discord message id of the status post we keep editing; state, not a user setting.
        public string DiscordStatusMessageId { get; set; }

        // Optional hostname shown instead of the raw public IP in join prompts and the
        // Discord status post (e.g. "valheim.example.com"). Valheim clients resolve A/AAAA
        // records when joining by name; the port must still be shared (no SRV support).
        public string CustomJoinDomain { get; set; }

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
                DiscordSharingEnabled = file.DiscordSharingEnabled ?? defaults.DiscordSharingEnabled,
                DiscordShareAddress = file.DiscordShareAddress ?? defaults.DiscordShareAddress,
                DiscordSharePassword = file.DiscordSharePassword ?? defaults.DiscordSharePassword,
                DiscordEventPosts = file.DiscordEventPosts ?? defaults.DiscordEventPosts,
                DiscordStatusMessageId = file.DiscordStatusMessageId ?? defaults.DiscordStatusMessageId,
                CustomJoinDomain = file.CustomJoinDomain ?? defaults.CustomJoinDomain,

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
            DiscordSharingEnabled = DiscordSharingEnabled,
            DiscordShareAddress = DiscordShareAddress,
            DiscordSharePassword = DiscordSharePassword,
            DiscordEventPosts = DiscordEventPosts,
            DiscordStatusMessageId = DiscordStatusMessageId,
            CustomJoinDomain = CustomJoinDomain,

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
