using Newtonsoft.Json;
using System;
using System.Collections.Generic;

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

        // -- Launch guard (what this profile last actually ran) --

        [JsonProperty("lastLaunchedServerBuild")] public string LastLaunchedServerBuild { get; set; }
        [JsonProperty("lastLaunchedServerFingerprint")] public string LastLaunchedServerFingerprint { get; set; }
        [JsonProperty("lastLaunchedGameVersion")] public string LastLaunchedGameVersion { get; set; }

        // -- Restart automation --

        [JsonProperty("autoRestart")] public bool? AutoRestart { get; set; }
        [JsonProperty("autoRestartDelay")] public int? AutoRestartDelay { get; set; }
        [JsonProperty("emptyServerRestart")] public bool? EmptyServerRestart { get; set; }
        [JsonProperty("emptyServerRestartDelayMinutes")] public int? EmptyServerRestartDelayMinutes { get; set; }
        [JsonProperty("scheduledRestart")] public bool? ScheduledRestart { get; set; }
        [JsonProperty("scheduledRestartHours")] public int? ScheduledRestartHours { get; set; }

        // -- One-shot cleanse --

        [JsonProperty("cleanseWhenEmpty")] public bool? CleanseWhenEmpty { get; set; }

        // -- The mod profile code on the Discord post --

        [JsonProperty("modProfileCodeKind")] public string ModProfileCodeKind { get; set; }
        [JsonProperty("modProfileCode")] public string ModProfileCode { get; set; }

        // -- The realm's own Herald post (1.2.16). Each realm keeps a post of its own, so the
        //    switch, the webhook, the thread, what the post carries and the post itself live
        //    here. Null keys read as a realm that does not share, with the app-wide webhook. --

        [JsonProperty("heraldOn")] public bool? HeraldOn { get; set; }
        [JsonProperty("heraldWebhookUrl")] public string HeraldWebhookUrl { get; set; }
        [JsonProperty("heraldThreadId")] public string HeraldThreadId { get; set; }
        [JsonProperty("heraldShareAddress")] public bool? HeraldShareAddress { get; set; }
        [JsonProperty("heraldSharePassword")] public bool? HeraldSharePassword { get; set; }
        [JsonProperty("heraldTitle")] public string HeraldTitle { get; set; }
        [JsonProperty("heraldStatusLine")] public string HeraldStatusLine { get; set; }
        [JsonProperty("heraldStatusMessageId")] public string HeraldStatusMessageId { get; set; }
        [JsonProperty("heraldStatusMessageSignature")] public string HeraldStatusMessageSignature { get; set; }
        [JsonProperty("heraldStatusThreadId")] public string HeraldStatusThreadId { get; set; }

        // -- Where the realm's event posts go (1.2.16): three kinds, each with its own switch,
        //    webhook and thread. Null switches read as off; blank boxes as the realm's own. --

        [JsonProperty("heraldRoutineOn")] public bool? HeraldRoutineOn { get; set; }
        [JsonProperty("heraldRoutineWebhookUrl")] public string HeraldRoutineWebhookUrl { get; set; }
        [JsonProperty("heraldRoutineThreadId")] public string HeraldRoutineThreadId { get; set; }
        [JsonProperty("heraldCrashOn")] public bool? HeraldCrashOn { get; set; }
        [JsonProperty("heraldCrashWebhookUrl")] public string HeraldCrashWebhookUrl { get; set; }
        [JsonProperty("heraldCrashThreadId")] public string HeraldCrashThreadId { get; set; }
        [JsonProperty("heraldCrashRoleId")] public string HeraldCrashRoleId { get; set; }
        [JsonProperty("heraldPlayersOn")] public bool? HeraldPlayersOn { get; set; }
        [JsonProperty("heraldPlayersWebhookUrl")] public string HeraldPlayersWebhookUrl { get; set; }
        [JsonProperty("heraldPlayersThreadId")] public string HeraldPlayersThreadId { get; set; }

        // -- The Discord screen of 1.2.17: the webhook's own name, the realm's own name and
        //    picture for its posts, and the forum refusals per kind. Null reads as none. --

        [JsonProperty("heraldWebhookName")] public string HeraldWebhookName { get; set; }
        [JsonProperty("heraldBotName")] public string HeraldBotName { get; set; }
        [JsonProperty("heraldAvatarUrl")] public string HeraldAvatarUrl { get; set; }
        [JsonProperty("heraldRoutineNoThread")] public bool? HeraldRoutineNoThread { get; set; }
        [JsonProperty("heraldCrashNoThread")] public bool? HeraldCrashNoThread { get; set; }
        [JsonProperty("heraldPlayersNoThread")] public bool? HeraldPlayersNoThread { get; set; }

        // -- RCON --

        [JsonProperty("rconEnabled")] public bool? RconEnabled { get; set; }
        [JsonProperty("rconPort")] public int? RconPort { get; set; }
        [JsonProperty("rconPassword")] public string RconPassword { get; set; }

        // -- Multi-server isolation --

        [JsonProperty("archived")] public bool? Archived { get; set; }
        [JsonProperty("isolatedInstall")] public bool? IsolatedInstall { get; set; }
        [JsonProperty("modManifest")] public List<ModManifestEntry> ModManifest { get; set; }

        // -- A test copy of another realm --

        [JsonProperty("testCopyOf")] public string TestCopyOf { get; set; }
        [JsonProperty("testSourceGone")] public bool? TestSourceGone { get; set; }

        // -- The chip's look in the sidebar --

        [JsonProperty("lookRune")] public string LookRune { get; set; }
        [JsonProperty("lookColor")] public string LookColor { get; set; }

        // -- Following a Gale profile --

        [JsonProperty("galeProfileId")] public string GaleProfileId { get; set; }
        [JsonProperty("galeLastUpdatedAt")] public string GaleLastUpdatedAt { get; set; }
        [JsonProperty("galeUnticked")] public List<string> GaleUnticked { get; set; }
        [JsonProperty("galeNotAvailable")] public List<string> GaleNotAvailable { get; set; }
        [JsonProperty("galeConfigUnticked")] public List<string> GaleConfigUnticked { get; set; }

        // -- Following a test copy of this realm (merge-back, 1.3.0) --

        [JsonProperty("followTestCopy")] public string FollowTestCopy { get; set; }
        [JsonProperty("followTestCopyLastApproved")] public string FollowTestCopyLastApproved { get; set; }
        [JsonProperty("followTestCopyUnticked")] public List<string> FollowTestCopyUnticked { get; set; }
        [JsonProperty("followTestCopyConfigUnticked")] public List<string> FollowTestCopyConfigUnticked { get; set; }
    }
}
