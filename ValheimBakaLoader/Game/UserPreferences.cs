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

        // Also look at Hexium, a second mod site, when checking for newer versions.
        // Off unless the host turns it on: with it off BakaLoader never opens a
        // connection to hexium.gg at all. Turning it on is the whole of the consent,
        // and the switch says in the Upkeep card exactly what it means.
        public bool UseHexiumSource { get; set; }

        public bool AutoUpdateBakaLoader { get; set; } = true;

        // BakaLoader puts BepInEx in place when it is missing and moves it forward at the
        // restart windows that already apply mod updates. On by default, because a mod
        // manager that leaves the framework its mods load under to the host is a manager
        // that does not work on a fresh machine. It is one setting for the whole install:
        // every server on it shares one BepInEx through junctions and hard links.
        public bool BepInExMaintained { get; set; } = true;

        // True once the first-start question has been asked, so it is asked exactly once.
        public bool BepInExMaintenanceAsked { get; set; }

        // The name of the last unattended-BepInEx fact the host closed the notice for. Per
        // install, like the two above it, because so is BepInEx. Empty until a notice has been
        // closed. A scheduled restart window records the same refusal every few hours, so
        // without this the bar about a decision the host has already made came back after
        // every launch; see Tools.BepInExNoticeKey for what a fact is named by.
        public string BepInExNoticeSeenKey { get; set; }

        // The stray-save-folder facts the host has waved the condition-bar row away for. A LIST
        // rather than the one field above it, because this fact is per SERVER where that one is
        // per install: an isolated server's folders are its own. Two of the shapes are states
        // only folder surgery in Explorer ends, so without this the same row came back on every
        // boot, every server switch and every Save Config; see Tools.StraySaveFolder.KeyFor.
        public List<string> StraySaveFolderSeenKeys { get; set; } = new();

        public bool StartWithWindows { get; set; }

        // Anonymous usage heartbeat (install count / servers online); see HeartbeatService.
        public bool ShareAnonymousStats { get; set; } = true;

        // The random value this install keys its per-realm command counts with. Made once, the
        // first time a count is taken, and NEVER sent anywhere: see Tools.AnalyticsSalt for why
        // it cannot be the device hash, which travels in the same beat as the counts.
        public string AnalyticsSalt { get; set; }

        public bool StartMinimized { get; set; }

        public bool SaveProfileOnStart { get; set; } = true;

        public bool WriteApplicationLogsToFile { get; set; } = true;

        // A line in the application log when a web request goes out and a line when it comes
        // back: the address and the proxy in front of it, then the status, the size and the
        // clock. Off by default, because it is a lot of lines on a busy install and nobody
        // who is not chasing a problem wants them. The
        // dial it moves is ILogLevelControl, and that follows the moment this is saved, so
        // there is no restart between turning it on and the next request being written down.
        public bool DetailedLog { get; set; }

        // Custom folder for app + server log files; null/blank = the default
        // %USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\logs.
        public string LogsFolderPath { get; set; }

        public bool EnablePasswordValidation { get; set; } = true;

        // Do not hand Thunderstore and the other remote clients through the Windows proxy.
        // .NET honours the system proxy setting and WPAD discovery out of the box, and a
        // machine carrying a proxy entry nothing answers on stalls every request while curl
        // on the same box goes straight out. Off by default: a machine where the ordinary
        // way works must not be pushed off it. The Test connection button says which way
        // worked, so this is a switch a host is told to turn on rather than one they guess at.
        public bool BypassSystemProxy { get; set; }

        // Connect over IPv4 only. An AAAA record that routes nowhere answers a connect by
        // hanging rather than by refusing. Off by default, same reason.
        public bool ForceIPv4 { get; set; }

        public bool DarkMode { get; set; } = true;

        // Swaps the Norse-lore UI terminology for plain English (off by default).
        public bool PlainTerminology { get; set; }

        // The language the interface is shown in: "en", or the code of an installed pack
        // ("ru", "ja", "zh-Hans", "zh-Hant"). English ships inside the app, so the default
        // needs nothing on disk. A saved code whose pack is missing is left exactly as it is:
        // the app opens in English and the globe menu offers the download, so a host who
        // reconnects gets their language back without picking it again.
        public string Language { get; set; } = "en";

        // The language of the words the server sends to players: the restart countdown, kick
        // reasons, broadcasts and Discord posts. "same" follows the interface language, "en"
        // pins it to English, and a language code pins it to that language. It is separate
        // from Language because the host and the people on their server are not always
        // reading the same one.
        public string PlayerMessageLanguage { get; set; } = "same";

        // How big the interface is read at: "normal", "large" or "xlarge". The whole window is
        // one web page, so this is the page's zoom rather than a second set of font sizes, and
        // the canvas fonts the zoom cannot reach are multiplied by the same factor. See
        // TextSizes for the three numbers and why the window's minimum grows with them.
        public string TextSize { get; set; } = TextSizes.Normal;

        // True once the first-launch setup wizard has been finished (or skipped).
        public bool SetupCompleted { get; set; }

        // Last window size the host was left at, written as "WIDTHxHEIGHT" in
        // device-independent pixels (96 dpi). Storing it DPI-free means moving the
        // window to a display with different scaling restores the same amount of
        // interface rather than the same count of physical pixels. Null = the
        // designed default. See BlendWindow.ApplyDpiSizing.
        public string WindowBounds { get; set; }

        // True when the window was left maximized.
        public bool WindowMaximized { get; set; }

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
                UseHexiumSource = file.UseHexiumSource ?? defaults.UseHexiumSource,
                AutoUpdateBakaLoader = file.AutoUpdateBakaLoader ?? defaults.AutoUpdateBakaLoader,
                BepInExMaintained = file.BepInExMaintained ?? defaults.BepInExMaintained,
                BepInExMaintenanceAsked = file.BepInExMaintenanceAsked ?? defaults.BepInExMaintenanceAsked,
                BepInExNoticeSeenKey = file.BepInExNoticeSeenKey ?? defaults.BepInExNoticeSeenKey,
                // Blanks dropped on the way in, so a hand-edited document cannot put an entry in
                // the list that matches an answer naming no fact.
                StraySaveFolderSeenKeys = (file.StraySaveFolderSeenKeys ?? new())
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .ToList(),
                StartWithWindows = file.StartWithWindows ?? defaults.StartWithWindows,
                ShareAnonymousStats = file.ShareAnonymousStats ?? defaults.ShareAnonymousStats,
                AnalyticsSalt = file.AnalyticsSalt ?? defaults.AnalyticsSalt,
                StartMinimized = file.StartMinimized ?? defaults.StartMinimized,
                SaveProfileOnStart = file.SaveProfileOnStart ?? defaults.SaveProfileOnStart,
                WriteApplicationLogsToFile = file.WriteApplicationLogsToFile ?? defaults.WriteApplicationLogsToFile,
                DetailedLog = file.DetailedLog ?? defaults.DetailedLog,
                LogsFolderPath = file.LogsFolderPath ?? defaults.LogsFolderPath,
                EnablePasswordValidation = file.EnablePasswordValidation ?? defaults.EnablePasswordValidation,
                BypassSystemProxy = file.BypassSystemProxy ?? defaults.BypassSystemProxy,
                ForceIPv4 = file.ForceIPv4 ?? defaults.ForceIPv4,
                DarkMode = file.DarkMode ?? defaults.DarkMode,
                PlainTerminology = file.PlainTerminology ?? defaults.PlainTerminology,
                Language = file.Language ?? defaults.Language,
                PlayerMessageLanguage = file.PlayerMessageLanguage ?? defaults.PlayerMessageLanguage,
                // Carried across as it was written, the same as the two language codes beside
                // it. The guard is at every READ instead: TextSizes.Factor answers 1.0 for a
                // spelling this build does not know, the save handler normalises before it
                // writes, and the window's own reader normalises before it zooms. Normalising
                // here as well would mean a document hand-edited to a future spelling lost that
                // spelling the next time anything on this card was saved.
                TextSize = file.TextSize ?? defaults.TextSize,
                SetupCompleted = file.SetupCompleted ?? defaults.SetupCompleted,
                WindowBounds = file.WindowBounds ?? defaults.WindowBounds,
                WindowMaximized = file.WindowMaximized ?? defaults.WindowMaximized,
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
            UseHexiumSource = UseHexiumSource,
            AutoUpdateBakaLoader = AutoUpdateBakaLoader,
            BepInExMaintained = BepInExMaintained,
            BepInExMaintenanceAsked = BepInExMaintenanceAsked,
            BepInExNoticeSeenKey = BepInExNoticeSeenKey,
            StraySaveFolderSeenKeys = (StraySaveFolderSeenKeys ?? new())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList(),
            StartWithWindows = StartWithWindows,
            ShareAnonymousStats = ShareAnonymousStats,
            AnalyticsSalt = AnalyticsSalt,
            StartMinimized = StartMinimized,
            SaveProfileOnStart = SaveProfileOnStart,
            WriteApplicationLogsToFile = WriteApplicationLogsToFile,
            DetailedLog = DetailedLog,
            LogsFolderPath = LogsFolderPath,
            EnablePasswordValidation = EnablePasswordValidation,
            BypassSystemProxy = BypassSystemProxy,
            ForceIPv4 = ForceIPv4,
            DarkMode = DarkMode,
            PlainTerminology = PlainTerminology,
            Language = Language,
            PlayerMessageLanguage = PlayerMessageLanguage,
            TextSize = TextSize,
            SetupCompleted = SetupCompleted,
            WindowBounds = WindowBounds,
            WindowMaximized = WindowMaximized,
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
