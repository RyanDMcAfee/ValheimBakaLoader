using Newtonsoft.Json;
using System.Collections.Generic;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// The exact on-disk shape of userprefs.json. Every value is nullable so
    /// that keys missing from older files fall back to the current defaults
    /// (see <see cref="UserPreferences.FromFile"/>). The JSON key names are a
    /// compatibility contract with existing installs - never rename them.
    /// </summary>
    public class UserPreferencesFile
    {
        // -- Paths --

        [JsonProperty("valheimServerPath")] public string ServerExePath { get; set; }
        [JsonProperty("valheimSaveDataFolder")] public string SaveDataFolderPath { get; set; }

        // -- App behavior --

        [JsonProperty("startWithWindows")] public bool? StartWithWindows { get; set; }
        [JsonProperty("startMinimized")] public bool? StartMinimized { get; set; }
        [JsonProperty("checkForUpdates")] public bool? CheckForUpdates { get; set; }
        [JsonProperty("autoUpdateMods")] public bool? AutoUpdateMods { get; set; }
        [JsonProperty("autoUpdateBakaLoader")] public bool? AutoUpdateBakaLoader { get; set; }
        [JsonProperty("shareAnonymousStats")] public bool? ShareAnonymousStats { get; set; }
        [JsonProperty("saveProfileOnStart")] public bool? SaveProfileOnStart { get; set; }
        [JsonProperty("writeApplicationLogsToFile")] public bool? WriteApplicationLogsToFile { get; set; }
        [JsonProperty("enablePasswordValidation")] public bool? EnablePasswordValidation { get; set; }
        [JsonProperty("darkMode")] public bool? DarkMode { get; set; }
        [JsonProperty("plainTerminology")] public bool? PlainTerminology { get; set; }
        [JsonProperty("setupCompleted")] public bool? SetupCompleted { get; set; }

        // -- Integrations --

        [JsonProperty("discordWebhookUrl")] public string DiscordWebhookUrl { get; set; }
        [JsonProperty("discordWebhookThreadId")] public string DiscordWebhookThreadId { get; set; }
        [JsonProperty("discordSharingEnabled")] public bool? DiscordSharingEnabled { get; set; }
        [JsonProperty("discordShareAddress")] public bool? DiscordShareAddress { get; set; }
        [JsonProperty("discordSharePassword")] public bool? DiscordSharePassword { get; set; }
        [JsonProperty("discordEventPosts")] public bool? DiscordEventPosts { get; set; }
        [JsonProperty("discordStatusMessageId")] public string DiscordStatusMessageId { get; set; }
        [JsonProperty("customJoinDomain")] public string CustomJoinDomain { get; set; }

        // -- Nested collections --

        [JsonProperty("servers")] public List<ServerPreferencesFile> Servers { get; set; }
        [JsonProperty("worlds")] public List<WorldPreferencesFile> Worlds { get; set; }
    }
}
