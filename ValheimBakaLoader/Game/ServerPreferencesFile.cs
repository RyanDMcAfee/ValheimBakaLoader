using Newtonsoft.Json;
using System;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// On-disk shape of one server profile inside userprefs.json. Values are
    /// nullable so missing keys fall back to defaults on load. JSON key names
    /// are a compatibility contract - never rename them.
    /// </summary>
    public class ServerPreferencesFile
    {
        [JsonProperty("profileName")] public string ProfileName { get; set; }
        [JsonProperty("lastSaved")] public DateTime? LastSaved { get; set; }

        // -- Server identity --

        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("password")] public string Password { get; set; }
        [JsonProperty("world")] public string WorldName { get; set; }
        [JsonProperty("community")] public bool? Community { get; set; }
        [JsonProperty("port")] public int? Port { get; set; }
        [JsonProperty("crossplay")] public bool? Crossplay { get; set; }

        // -- Saves and backups --

        [JsonProperty("saveInterval")] public int? SaveInterval { get; set; }
        [JsonProperty("backupCount")] public int? BackupCount { get; set; }
        [JsonProperty("backupIntervalShort")] public int? BackupIntervalShort { get; set; }
        [JsonProperty("backupIntervalLong")] public int? BackupIntervalLong { get; set; }

        // -- Launch behavior --

        [JsonProperty("autoStart")] public bool? AutoStart { get; set; }
        [JsonProperty("additionalArgs")] public string AdditionalArgs { get; set; }
        [JsonProperty("valheimServerPath")] public string ServerExePath { get; set; }
        [JsonProperty("valheimSaveDataFolder")] public string SaveDataFolderPath { get; set; }
        [JsonProperty("writeServerLogsToFile")] public bool? WriteServerLogsToFile { get; set; }

        // -- Restart automation --

        [JsonProperty("autoRestart")] public bool? AutoRestart { get; set; }
        [JsonProperty("autoRestartDelay")] public int? AutoRestartDelay { get; set; }
        [JsonProperty("emptyServerRestart")] public bool? EmptyServerRestart { get; set; }
        [JsonProperty("emptyServerRestartDelayMinutes")] public int? EmptyServerRestartDelayMinutes { get; set; }
        [JsonProperty("scheduledRestart")] public bool? ScheduledRestart { get; set; }
        [JsonProperty("scheduledRestartHours")] public int? ScheduledRestartHours { get; set; }

        // -- RCON --

        [JsonProperty("rconEnabled")] public bool? RconEnabled { get; set; }
        [JsonProperty("rconPort")] public int? RconPort { get; set; }
        [JsonProperty("rconPassword")] public string RconPassword { get; set; }
    }
}
