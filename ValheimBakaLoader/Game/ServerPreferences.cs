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

        /// <summary>
        /// The Steam build id (or, on an install Steam does not track, the binary
        /// fingerprint) of the server this profile last actually started. Null until the
        /// profile has been launched once, which is what makes the first start after a
        /// game update noticeable.
        /// </summary>
        public string LastLaunchedServerBuild { get; set; }

        /// <summary>
        /// The binary fingerprint of the server this profile last actually started, taken at
        /// the same moment as <see cref="LastLaunchedServerBuild"/>. The build id above is the
        /// Steam manifest's answer and the fingerprint is the binaries' own, so keeping both
        /// leaves the launch guard something to compare when the manifest cannot be read at
        /// the next start. Null until the profile has been launched once.
        /// </summary>
        public string LastLaunchedServerFingerprint { get; set; }

        /// <summary>
        /// The game version that server reported once it was up ("1.0.7"), read from its
        /// own banner. Null until a launched server has printed one.
        /// </summary>
        public string LastLaunchedGameVersion { get; set; }

        public bool AutoRestart { get; set; }

        public int AutoRestartDelay { get; set; } = 10;

        public bool EmptyServerRestart { get; set; }

        public int EmptyServerRestartDelayMinutes { get; set; } = 5;

        public bool ScheduledRestart { get; set; }

        public int ScheduledRestartHours { get; set; } = 6;

        /// <summary>
        /// Clear the cheat marks off this realm's world the next time it has been empty for a
        /// minute, then turn this back off.
        /// <para>
        /// The Players hall's button needs a host sitting at the window at a moment when
        /// nobody is playing, because the sweep rewrites containers and a container somebody
        /// has open refuses to reload itself. On a server people actually use that moment is
        /// four in the morning. This is the same sweep, armed and left: one shot, and the app
        /// puts it down again the moment it lands. Off by default and never turned on by
        /// anything but a host's own press.
        /// </para>
        /// </summary>
        public bool CleanseWhenEmpty { get; set; }

        /// <summary>
        /// Which mod manager this realm's mod profile code is for: <c>none</c>,
        /// <c>thunderstore</c> or <c>gale</c>. See <see cref="ModProfileCodes"/>.
        /// </summary>
        public string ModProfileCodeKind { get; set; } = ModProfileCodes.None;

        /// <summary>
        /// The share code players paste into their mod manager to get this realm's mods. It
        /// rides on the Discord status post as one field; blank means no field.
        /// </summary>
        public string ModProfileCode { get; set; }

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

        // -- Following a Gale profile --

        /// <summary>
        /// The Gale sync profile this realm follows, as the host pasted it: a base64 UUID or a
        /// six character code. Blank means off, and off means Gale is never contacted for this
        /// realm. A stored value that fails the shape check reads as blank, so a hand edit
        /// cannot send something odd to the service.
        /// </summary>
        public string GaleProfileId { get; set; }

        /// <summary>
        /// The profile's updatedAt the host last approved or dismissed, exactly as the service
        /// wrote it. The condition bar's row comes back only when the profile's updatedAt is
        /// different from this. Null until the first approval or dismissal.
        /// </summary>
        public string GaleLastUpdatedAt { get; set; }

        /// <summary>
        /// The "Namespace-Name" of every mod the host unticked in the approval dialog. They
        /// stay unticked through later changes to the profile until the host ticks them again.
        /// Never null on a loaded profile.
        /// </summary>
        public List<string> GaleUnticked { get; set; } = new();

        /// <summary>
        /// Each pinned build the approval dialog tried and Thunderstore no longer has, as
        /// "Namespace-Name@version". The dialog marks those rows "not available" on the next
        /// review instead of offering them as plain adds; a new version in the profile is a new
        /// entry, so it is offered again. Never null on a loaded profile.
        /// </summary>
        public List<string> GaleNotAvailable { get; set; } = new();

        /// <summary>
        /// Puts the three Gale fields of <paramref name="stored"/> onto this profile. The
        /// settings form has no Gale fields: it fetches the profile, changes what it has boxes
        /// for, and sends the whole object back, so whatever Gale state it carries is a copy of
        /// an older read. An approval that landed between that read and the save would be
        /// undone by it, and the updatedAt the page hands back has been through a JSON date
        /// parse on the way, which does not give the service's text back. What is on disk wins.
        /// </summary>
        public ServerPreferences KeepGaleFollowFrom(ServerPreferences stored)
        {
            if (stored == null) return this;
            GaleProfileId = stored.GaleProfileId;
            GaleLastUpdatedAt = stored.GaleLastUpdatedAt;
            GaleUnticked = NormalizeUnticked(stored.GaleUnticked);
            GaleNotAvailable = NormalizeUnticked(stored.GaleNotAvailable);
            return this;
        }

        /// <summary>
        /// The same guard for a profile with nothing on disk to take the Gale fields from (a
        /// first save under a new name, or a stored file that would not read). The id and the
        /// unticks are kept as sent, but the stamp is dropped: it has been through the page's
        /// date parse and is no longer the service's text, so keeping it would match nothing
        /// and could bring back a row the host had dismissed. Dropped, the worst it does is let
        /// the row show once.
        /// </summary>
        public ServerPreferences KeepGaleFollowWithoutStore()
        {
            GaleLastUpdatedAt = null;
            GaleUnticked = NormalizeUnticked(GaleUnticked);
            GaleNotAvailable = NormalizeUnticked(GaleNotAvailable);
            return this;
        }

        /// <summary>The unticked list as it is kept: trimmed, no blanks, each name once whatever its case.</summary>
        public static List<string> NormalizeUnticked(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<string>();
            foreach (var name in names ?? Array.Empty<string>())
            {
                var n = name?.Trim();
                if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
                kept.Add(n);
            }
            return kept;
        }

        private static string StoredGaleProfileId(string stored)
        {
            var check = Tools.GaleProfileId.Check(stored);
            return check.Ok ? check.Id : null;
        }

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
                LastLaunchedServerBuild = file.LastLaunchedServerBuild ?? defaults.LastLaunchedServerBuild,
                LastLaunchedServerFingerprint = file.LastLaunchedServerFingerprint ?? defaults.LastLaunchedServerFingerprint,
                LastLaunchedGameVersion = file.LastLaunchedGameVersion ?? defaults.LastLaunchedGameVersion,
                AutoRestart = file.AutoRestart ?? defaults.AutoRestart,
                AutoRestartDelay = file.AutoRestartDelay ?? defaults.AutoRestartDelay,
                EmptyServerRestart = file.EmptyServerRestart ?? defaults.EmptyServerRestart,
                EmptyServerRestartDelayMinutes = file.EmptyServerRestartDelayMinutes ?? defaults.EmptyServerRestartDelayMinutes,
                ScheduledRestart = file.ScheduledRestart ?? defaults.ScheduledRestart,
                ScheduledRestartHours = file.ScheduledRestartHours ?? defaults.ScheduledRestartHours,
                CleanseWhenEmpty = file.CleanseWhenEmpty ?? defaults.CleanseWhenEmpty,
                ModProfileCodeKind = ModProfileCodes.NormalizeKind(file.ModProfileCodeKind ?? defaults.ModProfileCodeKind),
                ModProfileCode = string.IsNullOrWhiteSpace(file.ModProfileCode) ? defaults.ModProfileCode : file.ModProfileCode.Trim(),
                RconEnabled = file.RconEnabled ?? defaults.RconEnabled,
                RconPort = file.RconPort ?? defaults.RconPort,
                RconPassword = file.RconPassword ?? defaults.RconPassword,
                Archived = file.Archived ?? defaults.Archived,
                IsolatedInstall = file.IsolatedInstall ?? defaults.IsolatedInstall,
                ModManifest = file.ModManifest ?? defaults.ModManifest,
                GaleProfileId = StoredGaleProfileId(file.GaleProfileId),
                GaleLastUpdatedAt = string.IsNullOrWhiteSpace(file.GaleLastUpdatedAt) ? defaults.GaleLastUpdatedAt : file.GaleLastUpdatedAt,
                GaleUnticked = NormalizeUnticked(file.GaleUnticked),
                GaleNotAvailable = NormalizeUnticked(file.GaleNotAvailable),
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
            LastLaunchedServerBuild = LastLaunchedServerBuild,
            LastLaunchedServerFingerprint = LastLaunchedServerFingerprint,
            LastLaunchedGameVersion = LastLaunchedGameVersion,
            AutoRestart = AutoRestart,
            AutoRestartDelay = AutoRestartDelay,
            EmptyServerRestart = EmptyServerRestart,
            EmptyServerRestartDelayMinutes = EmptyServerRestartDelayMinutes,
            ScheduledRestart = ScheduledRestart,
            ScheduledRestartHours = ScheduledRestartHours,
            CleanseWhenEmpty = CleanseWhenEmpty,
            ModProfileCodeKind = ModProfileCodeKind,
            ModProfileCode = ModProfileCode,
            RconEnabled = RconEnabled,
            RconPort = RconPort,
            RconPassword = RconPassword,
            Archived = Archived,
            IsolatedInstall = IsolatedInstall,
            ModManifest = ModManifest,
            GaleProfileId = StoredGaleProfileId(GaleProfileId),
            GaleLastUpdatedAt = string.IsNullOrWhiteSpace(GaleLastUpdatedAt) ? null : GaleLastUpdatedAt,
            // An empty list is written as null, the same as the other two fields of a realm
            // that has never followed a profile, and null reads back as an empty list.
            GaleUnticked = NormalizeUnticked(GaleUnticked) is { Count: > 0 } kept ? kept : null,
            GaleNotAvailable = NormalizeUnticked(GaleNotAvailable) is { Count: > 0 } gone ? gone : null,
        };
    }
}
