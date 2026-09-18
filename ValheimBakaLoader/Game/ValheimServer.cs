using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using ValheimBakaLoader.Tools.Processes;

namespace ValheimBakaLoader.Game
{
    /// <summary>Why a launch is being attempted. Carried on <see cref="LaunchContext"/>.</summary>
    public static class LaunchReasons
    {
        /// <summary>The host pressed Start.</summary>
        public const string Manual = "manual";

        /// <summary>The profile's auto-start pref fired when BakaLoader opened.</summary>
        public const string AutoStart = "autostart";

        /// <summary>The every-N-hours scheduled restart came round.</summary>
        public const string Scheduled = "scheduled";

        /// <summary>The server sat empty long enough to be restarted.</summary>
        public const string Empty = "empty";

        /// <summary>The server process died and crash recovery is relaunching it.</summary>
        public const string Crash = "crash";

        /// <summary>Updates were applied in the stopped gap and the server is coming back up.</summary>
        public const string SelfUpdate = "selfupdate";
    }

    /// <summary>
    /// Everything the launch guard needs to decide whether a start may go ahead: who asked,
    /// what the install looks like right now, and what this profile last actually ran.
    /// </summary>
    public sealed class LaunchContext
    {
        /// <summary>True when nobody is necessarily at the keyboard, so no dialog may block.</summary>
        public bool Automatic { get; init; }

        /// <summary>One of <see cref="LaunchReasons"/>.</summary>
        public string Reason { get; init; }

        /// <summary>The Valheim server install as it is on disk right now.</summary>
        public Tools.ServerBuildInfo Current { get; init; }

        /// <summary>Build id or fingerprint this profile last started, or null.</summary>
        public string LastLaunchedBuild { get; init; }

        /// <summary>
        /// The binary fingerprint taken at that same launch, or null for a profile last
        /// launched before BakaLoader recorded one. It is what the guard compares against when
        /// the Steam manifest cannot be read at this start, so a build id it can no longer read
        /// is not the end of the check.
        /// </summary>
        public string LastLaunchedFingerprint { get; init; }

        /// <summary>Game version that server reported, or null.</summary>
        public string LastLaunchedGameVersion { get; init; }

        /// <summary>True when the profile's save folder already holds at least one world.</summary>
        public bool HasWorlds { get; init; }

        /// <summary>The profile this launch belongs to, when the server knows it.</summary>
        public string ProfileName { get; init; }

        /// <summary>The options the server is about to launch with.</summary>
        public IValheimServerOptions Options { get; init; }

        /// <summary>
        /// Whether this launch may use an answer the host staged in the UI. Only a launch the
        /// host asked for may: the start they pressed, and the restart they pressed, which
        /// relaunches unattended but carries the manual reason through the stop and back up
        /// again. Everything BakaLoader decides on by itself - crash recovery, the empty-server
        /// and scheduled cycles, the auto-start, a relaunch that applied updates - has to ask
        /// again, or an answer that was never given about it brings a server up on a changed
        /// build with no backup and nobody watching.
        /// </summary>
        public bool MayUseStagedAnswer => Reason == LaunchReasons.Manual;
    }

    /// <summary>The launch guard's answer.</summary>
    public sealed class LaunchDecision
    {
        /// <summary>False holds the launch: nothing is started and the reason is logged.</summary>
        public bool Proceed { get; init; }

        /// <summary>Copy every world aside before the process starts. Ignored when holding.</summary>
        public bool BackupFirst { get; init; }

        /// <summary>Optional detail for the log line ("host chose start anyway").</summary>
        public string Note { get; init; }

        public static LaunchDecision Go(string note = null) => new() { Proceed = true, Note = note };

        public static LaunchDecision BackUpThenGo(string note = null)
            => new() { Proceed = true, BackupFirst = true, Note = note };

        public static LaunchDecision Hold(string note = null) => new() { Proceed = false, Note = note };
    }

    /// <summary>
    /// A launch that finished without the server coming up. Status never moved, so nothing
    /// else tells the UI the attempt is over and the Start button can be pressed again.
    /// </summary>
    public sealed class LaunchSettledEventArgs : EventArgs
    {
        /// <summary>One of <see cref="LaunchReasons"/>.</summary>
        public string Reason { get; init; }

        /// <summary>True when the guard held the launch on purpose rather than something failing.</summary>
        public bool Held { get; init; }

        /// <summary>A sentence for the host, or null when the hold already said its piece.</summary>
        public string Error { get; init; }
    }

    /// <summary>
    /// What a spawn actually did, as opposed to whether the socket answered at all. The console
    /// replies with a line of text for every outcome, a refusal as readily as a success, so "a
    /// reply came back" was never evidence that anything had been conjured. The day the game
    /// turned PlayerProfile.s_bypassCheatChecks into a property, every spawn answered with a
    /// MissingFieldException and the interface still said it had worked, which is how the bug
    /// stayed hidden from the host for a whole session.
    /// </summary>
    public sealed class SpawnResult
    {
        /// <summary>True only when the server said it had spawned or queued what was asked for.</summary>
        public bool Ok { get; init; }

        /// <summary>The server's own words, ready to put in front of the host.</summary>
        public string Message { get; init; }
    }

    /// <summary>
    /// Owns the valheim_server.exe process: launch arguments, lifecycle (start/stop/
    /// restart/adopt), stdout log parsing into player events, RCON player actions,
    /// and the automatic-restart machinery (scheduled, empty-server, crash recovery).
    /// </summary>
    public class ValheimServer : IDisposable
    {
        /// <summary>One stdout pattern and the reaction it triggers.</summary>
        private sealed record LogLineRule(Regex Pattern, Action<Match> Handle);

        /// <summary>The profile the current (or last-started) server was launched with.</summary>
        public IValheimServerOptions Options { get; private set; } = new ValheimServerOptions();

        /// <summary>
        /// The server-profile name this instance manages. Stamped onto player records so
        /// concurrent servers never cross-attribute joins/spawns to each other's players.
        /// Null on legacy single-server paths; matches anything in the repository.
        /// </summary>
        public string ServerKey { get; set; }

        /// <summary>The per-session server log pipeline. Exposed for testing.</summary>
        public IValheimServerLogger Logger => ServerLogger;

        private ServerStatus CurrentStatus = ServerStatus.Stopped;

        public ServerStatus Status
        {
            get => CurrentStatus;
            private set
            {
                var isTransition = CurrentStatus != value;
                CurrentStatus = value;
                if (isTransition) StatusChanged?.Invoke(this, value);
            }
        }

        // Handle into IProcessProvider for the tracked server process; null = no process.
        private string ProcessKey;
        private bool IsRestarting;
        private bool IsCrashRestart;

        // Reactions to known valheim_server stdout lines, tried in order on every line.
        private readonly List<LogLineRule> LogLineRules;

        // How long to wait for the "playerlist" position line to surface on the server's stdout
        // stream after the RCON command is fired (the RCON response body itself is unreliable).
        private const int PlayerListCaptureTimeoutMs = 2500;

        // Only one RCON command may be in flight: the client opens and closes a socket per
        // command, so overlapping callers cut each other off.
        private readonly SemaphoreSlim RconGate = new(1, 1);

        // Live coordinates for everyone online, keyed by the character name "playerlist" prints.
        // Filled by one playerlist call every few seconds while the server is up, emptied the
        // moment it is not, so the roster never shows where somebody stood last session. Each
        // entry carries the moment it was read: a poll whose RCON reply never comes back leaves
        // the last coordinates sitting here, and a frozen number reads exactly like a live one.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Text, DateTime TakenUtc)>
            PlayerPositions = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource PositionPollCts;

        /// <summary>
        /// How often the roster's coordinates are refreshed. One RCON round trip covers every
        /// player at once, so this is one connection per interval no matter how many are online.
        /// A roster does not need coordinates to the second, and the WebUI polls players.list
        /// twice a second, which is far too often to hang a socket off.
        /// </summary>
        private const int PositionPollSeconds = 5;

        /// <summary>
        /// How long a cached coordinate may be shown for. Several polls wide, so an ordinary
        /// missed reply does not blank the column, but far short of the point where a player
        /// could have walked anywhere. Past it the roster draws its plain hyphen: a coordinate
        /// nobody has confirmed for half a minute is a guess, and a guess printed as a position
        /// is worse than no position at all.
        /// </summary>
        internal const int PositionStaleSeconds = 30;

        /// <summary>Whether a coordinate read at <paramref name="takenUtc"/> may still be shown.</summary>
        internal static bool PositionIsFresh(DateTime takenUtc, DateTime nowUtc)
            => nowUtc - takenUtc < TimeSpan.FromSeconds(PositionStaleSeconds);

        /// <summary>Fires once per real state transition (never repeats the same state).</summary>
        public event EventHandler<ServerStatus> StatusChanged;

        /// <summary>Fires when the game reports a world save, with the save duration in ms.</summary>
        public event EventHandler<decimal> WorldSaved;

        /// <summary>
        /// Fires when the game reports that a world save FAILED, with the elapsed
        /// time in ms. The world on disk still holds the previous save, so play
        /// since then is at risk until a later save succeeds.
        /// </summary>
        public event EventHandler<decimal> WorldSaveFailed;

        /// <summary>
        /// Fires when the game loads a world that is still in the pre-1.0 save
        /// format. The next save rewrites it in the new format, and that cannot be
        /// undone, so the UI warns before the host finds out the hard way.
        /// </summary>
        public event EventHandler LegacyWorldLoaded;

        /// <summary>
        /// Fires when the server prints its version banner. The string is the game
        /// version; <see cref="NetworkVersion"/> carries the matching network one.
        /// </summary>
        public event EventHandler<string> VersionDetected;

        /// <summary>
        /// The game version the running server reported ("1.0.7"), or null before
        /// the banner has been seen. Read from the log, never assumed.
        /// </summary>
        public string GameVersion { get; private set; }

        /// <summary>
        /// The network protocol version the running server reported ("39"), or null
        /// before the banner has been seen. Clients must match it to connect.
        /// </summary>
        public string NetworkVersion { get; private set; }

        /// <summary>Fires when a crossplay session publishes its join code.</summary>
        public event EventHandler<string> InviteCodeReady;

        /// <summary>Fires when the process exits nonzero outside a deliberate stop.</summary>
        public event EventHandler ServerCrashed;

        /// <summary>
        /// Fires when a player's character dies (the game logs a ZDOID of 0:0 for the
        /// character on death). The string is the character name from the log line.
        /// </summary>
        public event EventHandler<string> PlayerDied;

        /// <summary>
        /// Raised while a scheduled restart countdown is running. The string is a short
        /// status message (e.g. "Restart in 5 minutes"), or null when the countdown ends.
        /// </summary>
        public event EventHandler<string> CountdownTick;

        /// <summary>
        /// Optional hook (set by the UI) that returns the number of installed mods with a
        /// newer version available on Thunderstore - or 0 when auto-update is disabled or
        /// nothing needs updating. Used to tell players how many mods will be updated in
        /// the restart countdown announcement.
        /// </summary>
        public Func<Task<int>> GetPendingModUpdateCount { get; set; }

        /// <summary>
        /// Optional hook (set by the UI) that downloads and installs all available mod
        /// updates. Invoked while the server is fully stopped, in the gap between Stop and
        /// Start of an auto-update restart, so BepInEx is never loading the plugin files
        /// while they're being replaced.
        /// </summary>
        public Func<Task> ApplyModUpdates { get; set; }

        /// <summary>
        /// Optional hook (set by the UI) that puts BepInEx in place before the companion
        /// plugins are prepared, when BakaLoader is looking after it and this install has
        /// none. The argument is the server .exe this start is about, because an isolated
        /// profile's loader lives in the base install its junctions point at rather than
        /// beside its own executable.
        /// <para>
        /// It runs inside <c>PrepareCompanionPlugins</c>, which is the one moment in a start
        /// where the process is not up yet and the files are free. A throw is recorded against
        /// this profile like any other companion-plugin failure, so a loader that could not be
        /// fetched says so on the condition bar and in the server log rather than leaving a
        /// modded profile quietly running vanilla.
        /// </para>
        /// </summary>
        public Action<string> PrepareBepInEx { get; set; }

        /// <summary>
        /// Optional hook (set by the UI) that hands back the options built from what is SAVED ON
        /// DISK for this server's profile right now, or null when they cannot be read.
        /// <para>
        /// Every automatic relaunch used to come back up on the options the server was started
        /// with, because a relaunch reuses <see cref="Options"/> and only a fresh Start ever
        /// reads the profile again. A host who changed a setting while the server was up (the
        /// world's difficulty dials are the painful one) then watched restart after restart
        /// launch with the old command line until they stopped and started the server by hand.
        /// This is what a relaunch asks so it comes up on what the host actually saved.
        /// </para>
        /// <para>
        /// It must return a COMPLETE options object, launch history included, exactly as the
        /// Start path builds one: the launch guard reads
        /// <see cref="IValheimServerOptions.LastLaunchedServerBuild"/> and its two siblings off
        /// these options, and the log view is wired through
        /// <see cref="IValheimServerOptions.LogMessageHandler"/>. Unset (as in tests) means a
        /// relaunch keeps the options it is running with, exactly as it always did.
        /// </para>
        /// </summary>
        public Func<IValheimServerOptions> RefreshOptions { get; set; }

        /// <summary>
        /// How long a relaunch waits between the old process being gone and the new one being
        /// launched. Half a second for an ordinary restart, so the exiting process has let go of
        /// its port and its save files, and the profile's own crash delay after a crash.
        /// </summary>
        internal const int DefaultRelaunchDelayMs = 500;

        /// <summary>
        /// The wait itself, so a test can drive a restart end to end without sitting out a real
        /// half second on a loaded build machine. Production keeps the real wait: this is only
        /// ever replaced from a test, which hands back a completed task and lets the relaunch
        /// run at once. Nothing about the order of the steps changes either way.
        /// </summary>
        internal Func<int, Task> RelaunchDelay { get; set; } = ms => Task.Delay(ms);

        /// <summary>
        /// Optional hook (set by the UI) that checks GitHub for a newer BakaLoader release and,
        /// when auto-update is enabled and an update is found, stages a headless watchdog and
        /// begins closing the app. Returns <c>true</c> when an app update is taking over (the
        /// server restart should then be abandoned, because the app - and its child server -
        /// is going down and the watchdog will relaunch everything). Invoked at the very start
        /// of a restart, before any countdown or server teardown.
        /// </summary>
        public Func<Task<bool>> CheckForAppUpdateOnRestart { get; set; }

        /// <summary>
        /// Optional hook (set by the UI) consulted before EVERY launch - the host's Start button,
        /// the auto-start at app launch, the scheduled restart, the empty-server restart, crash
        /// recovery and the relaunch that follows an update. It returns whether the launch may go
        /// ahead and whether the worlds should be copied aside first. Returning null or
        /// <see cref="LaunchDecision.Hold"/> skips the launch; automatic paths then arm a retry so
        /// a later cycle asks again. Unset (as in tests) means every launch proceeds unchanged.
        /// </summary>
        public Func<LaunchContext, Task<LaunchDecision>> ConfirmLaunchAsync { get; set; }

        /// <summary>
        /// Optional hook (set by the UI) that answers one question before any launch is even
        /// claimed: is something rewriting this install right now? A server update runs Steam or
        /// steamcmd over the very folder <c>valheim_server.exe</c> sits in, so a start that lands
        /// mid-update runs a half written exe against half written managed assemblies. The launch
        /// guard cannot see that: it compares builds, and the build on disk during a rewrite is
        /// whatever the writer has got to so far.
        /// <para>
        /// It is consulted by EVERY path into <see cref="BeginLaunch"/>, so the Start button, the
        /// auto start, the retry timer and the relaunch after a restart all refuse together.
        /// Unset (as in tests) means nothing is ever blocked.
        /// </para>
        /// </summary>
        public Func<bool> LaunchBlocked { get; set; }

        /// <summary>
        /// What the host is told when <see cref="LaunchBlocked"/> refuses a launch. One sentence,
        /// the same one the bridge answers a Start RPC with, so the banner and the toast agree.
        /// </summary>
        public const string LaunchBlockedMessage =
            "An update is running for this install. Wait for it to finish.";

        /// <summary>
        /// Optional hook (set by the UI) that writes the build and game version a launched server
        /// actually ran into the profile's preferences. Called with the build identity once the
        /// server reaches Running, and again with the game version once its banner is parsed.
        /// Either argument may be null, meaning "leave that one alone".
        /// </summary>
        public Action<string, string> RecordLaunchedBuild { get; set; }

        /// <summary>
        /// Fires after a pre-update world snapshot, whether it worked or not. A result carrying
        /// an Error means the launch was abandoned rather than run without a safety copy.
        /// </summary>
        public event EventHandler<WorldStore.WorldSnapshotResult> PreUpdateBackupCompleted;

        /// <summary>
        /// Optional hook (set by the UI, which is the only thing that knows about the other
        /// profiles) asked about every world before the pre-update snapshot copies it. Returning
        /// a reason abandons the whole launch: several profiles can share one save folder, and a
        /// world another server is saving into cannot be copied safely at all.
        /// </summary>
        public Func<WorldInfo, string> WorldSnapshotVeto { get; set; }

        /// <summary>
        /// Fires whenever a launch attempt ends with the server still down: the guard held it,
        /// the guard threw, the pre-update snapshot failed, or the process could not be started.
        /// Status never moves on any of those paths, so this is the only signal the UI gets that
        /// the attempt is over and the Start button is live again.
        /// </summary>
        public event EventHandler<LaunchSettledEventArgs> LaunchSettled;

        // True when the next restart should apply pending mod updates in the stop->start gap.
        private bool ApplyUpdatesOnRestart;

        // Why the pending relaunch is happening, so the launch guard can tell a crash recovery
        // apart from a scheduled restart. Set by whoever asks for the restart.
        private string PendingLaunchReason = LaunchReasons.Manual;

        // True when the restart in flight was handed its own options. A caller that brings
        // options is naming the ones to come back up on, so that one relaunch does not read the
        // profile again. Consumed by the relaunch; every fresh start clears it.
        private bool RelaunchOptionsPinned;

        // True between "a guarded launch was accepted" and "the process exists", so a second
        // click (or a timer firing) cannot start two servers while the guard is still thinking.
        // Held as an int and only ever claimed through Interlocked: a plain volatile bool made
        // the check and the set two separate steps, and two starts arriving together both read
        // "free" before either wrote "taken", so both went on to launch a server.
        private int LaunchPendingFlag;

        private bool LaunchPending => Volatile.Read(ref LaunchPendingFlag) != 0;

        /// <summary>Takes the launch claim, or false when another launch already holds it.</summary>
        private bool ClaimLaunch() => Interlocked.CompareExchange(ref LaunchPendingFlag, 1, 0) == 0;

        /// <summary>Gives the launch claim back, whether the launch happened or not.</summary>
        private void ReleaseLaunch() => Volatile.Write(ref LaunchPendingFlag, 0);

        /// <summary>
        /// Whether something is rewriting this install right now. A hook that throws is read as
        /// "not blocked": refusing every start because a status read failed would be worse than
        /// the window it guards, and the hook is best effort by design.
        /// </summary>
        private bool IsLaunchBlocked()
        {
            var blocked = LaunchBlocked;
            if (blocked == null) return false;

            try { return blocked(); }
            catch (Exception e)
            {
                ApplicationLogger.Warning("Could not check whether an update is running: {message}", e.Message);
                return false;
            }
        }

        // The build the running (or last-launched) process was started from, recorded into the
        // profile once the server actually comes up.
        private string LaunchedBuildIdentity;

        // Retry for an automatic launch the guard held: the same question, asked again later.
        private CancellationTokenSource LaunchRetryCts;

        /// <summary>
        /// Floor for how often a held automatic launch re-asks. A held launch needs a person,
        /// so retrying on a crash-restart's 10-second cadence would only spam the log and the
        /// Discord post without ever getting further.
        /// </summary>
        private const int LaunchHoldRetryMinutes = 10;

        public bool CanStart => ProcessKey == null && Status == ServerStatus.Stopped && !LaunchPending;

        public bool CanStop => ProcessKey != null
            && (Status == ServerStatus.Starting || Status == ServerStatus.Running);

        public bool CanRestart => ProcessKey != null && Status == ServerStatus.Running;

        /// <summary>
        /// True when this instance is monitoring an externally-started server process
        /// (adopted at launch). Log capture is unavailable for adopted processes.
        /// </summary>
        public bool IsAdopted { get; private set; }

        public bool IsCountdownActive => CountdownCts != null && !CountdownCts.IsCancellationRequested;

        // Restart countdown announcement points (seconds before restart): 30m, 5m, 1m - always,
        // per the host's request. Used for long-lead automatic (scheduled) restarts.
        private static readonly int[] DefaultCountdownSeconds = { 1800, 300, 60 };

        // Shorter countdown used when the host clicks "Restart" by hand: warn at 1m, 30s, 10s
        // then restart. A full 1-hour countdown would make the manual button feel like it does
        // nothing for an hour, so a manual restart announces briefly and restarts promptly.
        public static readonly int[] ManualRestartCountdownSeconds = { 60, 30, 10 };
        private CancellationTokenSource CountdownCts;

        // Restarts the server a set time after the last player leaves; cancelled if a player rejoins.
        private CancellationTokenSource EmptyRestartCts;
        // Fires a scheduled (e.g. every 6 hours) restart while the server is running.
        private CancellationTokenSource ScheduledRestartCts;
        // When the next scheduled restart will fire (UTC); null when no restart is armed.
        // Consumed by the Discord status post ("next restart" field).
        public DateTime? NextScheduledRestartUtc { get; private set; }
        // While the server sits empty, periodically checks for pending mod updates and, if any
        // are found, restarts to install them (item C: auto-update on an empty server).
        private CancellationTokenSource EmptyUpdateCts;

        // How long the server must be continuously empty before the empty-server auto-update
        // check runs (and how often it re-checks while still empty). Player request: 10 minutes.
        private const int EmptyUpdateDelayMinutes = 10;
        // Tracks the number of active (online/joining) players to detect the >0 -> 0 transition.
        private int LastActivePlayerCount;

        private readonly IApplicationLogger ApplicationLogger;
        private readonly IProcessProvider ProcessProvider;
        private readonly IRconClient RconClient;
        private readonly IPlayerDataRepository PlayerDataRepository;
        private readonly IItemIndexerInstaller ItemIndexerInstaller;
        private readonly ISpawnHelperInstaller SpawnHelperInstaller;
        private readonly IKillAllInstaller KillAllInstaller;
        private readonly ICommanderInstaller CommanderInstaller;
        private readonly IMaxPlayersInstaller MaxPlayersInstaller;
        private readonly PlayerListService PlayerLists;

        // Rebuilt on every Start(); tails the new process's stdout/stderr.
        private IValheimServerLogger ServerLogger;

        public ValheimServer(
            IApplicationLogger appLogger,
            IProcessProvider processProvider,
            IRconClient rconClient,
            IPlayerDataRepository playerDataRepository,
            IItemIndexerInstaller itemIndexerInstaller,
            ISpawnHelperInstaller spawnHelperInstaller,
            IKillAllInstaller killAllInstaller,
            ICommanderInstaller commanderInstaller,
            IMaxPlayersInstaller maxPlayersInstaller,
            PlayerListService playerListService)
        {
            ApplicationLogger = appLogger;
            ProcessProvider = processProvider;
            RconClient = rconClient;
            PlayerDataRepository = playerDataRepository;
            ItemIndexerInstaller = itemIndexerInstaller;
            SpawnHelperInstaller = spawnHelperInstaller;
            KillAllInstaller = killAllInstaller;
            CommanderInstaller = commanderInstaller;
            MaxPlayersInstaller = maxPlayersInstaller;
            PlayerLists = playerListService;

            LogLineRules = BuildLogLineRules();
            StatusChanged += OnStatusTransition;
            PlayerDataRepository.PlayerStatusChanged += OnPlayerStatusChanged;
        }

        #region Log parsing and status transitions

        /// <summary>
        /// The stdout lines valheim_server emits at each lifecycle moment, and how the
        /// manager reacts to them. Patterns are matched case-insensitively against every
        /// log line; ALL rules are tried per line (a line may satisfy several).
        /// </summary>
        private List<LogLineRule> BuildLogLineRules()
        {
            static Regex Rx(string pattern) =>
                new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

            return new List<LogLineRule>
            {
                // Startup complete.
                new(Rx(@"Game server connected"), HandleServerReady),

                // The version banner, printed once per boot. Both numbers are read
                // from the line, never assumed, so a game update cannot outdate them.
                new(Rx(@"Valheim version:\s*(\S+)\s*\(network version (\d+)\)"), HandleVersionBanner),

                // Periodic world save, with the save duration captured in ms.
                // Pre-1.0 servers print a single "World saved ( 198.5ms )" line.
                new(Rx(@"World saved \(\s*?([[\d\.]+?)\s*?ms\s*?\)\s*?$"), HandleWorldSaved),

                // Valheim 1.0 splits the save into five stages; only stage 5 means
                // the save is on disk. Durations past 999 ms are grouped ("1,234ms").
                new(Rx(@"World save \(5/5\) done\. Total time \[([\d.,]+)ms\]"), HandleWorldSaved),
                new(Rx(@"World save \(5/5\) FAILED\. Total time \[([\d.,]+)ms\]"), HandleWorldSaveFailed),

                // The world on disk is still in the pre-1.0 format and is about to
                // be converted by the next save.
                new(Rx(@"ZNet\.LoadOldWorld done"), HandleLegacyWorldLoaded),

                // Crossplay session published its join code.
                new(Rx(@"Session "".*?"" with join code (.*?) "), HandleJoinCode),

                // A client began connecting: Steam carries a SteamID, crossplay a
                // "{platform}_{id}" pair on the PlayFab socket line. Valheim 1.0
                // splits that pair on the FIRST underscore only and neither half is
                // guaranteed numeric, so the id capture takes everything that is left
                // (e.g. "PlayFab_BakaXplay_2498_3c72cce4...").
                new(Rx(@"Got connection SteamID (\d+?)\D*?$"), HandleSteamConnecting),
                new(Rx(@"PlayFab socket with remote ID .*? received local Platform ID ([^_\s]+)_(\S+)\s*$"), HandleCrossplayConnecting),

                // The character actually spawned in-world. ZDOIDs may be negative,
                // hence [\d-] in the id capture.
                new(Rx(@"Got character ZDOID from (.+?) : ([\d-]+?)\D*?:(\d+?)\D*?$"), HandleCharacterSpawned),

                // Valheim 1.0 prints the peer's numeric player id next to their
                // character name once the connection is accepted.
                new(Rx(@"^.*Got player ID from (.+?) : (-?\d+)\s*$"), HandleGotPlayerId),

                // Rejected connection attempt.
                new(Rx(@"Peer (\d+?) has wrong password"), HandleWrongPassword),

                // Departures. "Closing socket" is the most reliable terminator the game
                // prints; the abandoned-zdo line covers crossplay clients, and the
                // client-disconnect line covers ValheimPlus version mismatches.
                new(Rx(@"Closing socket (\d+?)\D*?$"), HandleSocketClosed),
                new(Rx(@"Destroying abandoned non persistent zdo ([\d-]+?):.*$"), HandleSocketClosed),
                new(Rx(@"Disconnect: The client \(([^_\s]+)_([^)\s]+)\)"), HandleCrossplayDisconnect),

                // The bundled max-players plugin refuses to patch when the game's IL is not the
                // shape it expects, and prints one fixed sentence when it does. That sentence is
                // a compile-time constant in the plugin (CapStaysAtVanilla in
                // Resources/MaxPlayers/BakaLoaderMaxPlayers.cs), so it is safe to match on, and
                // it must not be reworded on either side. Without this the number in the World
                // hall reads as though it were in force for the rest of the session.
                new(Rx(@"BakaLoader Max Players: this server keeps the vanilla limit of 10 players for this start"),
                    HandleMaxPlayersRefused),
            };
        }

        private void OnStatusTransition(object sender, ServerStatus status)
        {
            // Coordinates only mean anything while the server is up. Anything else empties the
            // cache, so a restarted session can never hand the roster where somebody stood in
            // the last one.
            if (status == ServerStatus.Running) StartPositionPolling();
            else StopPositionPolling();

            switch (status)
            {
                case ServerStatus.Running:
                    // A fresh session begins with nobody online, and the scheduled
                    // restart clock starts counting from this moment.
                    LastActivePlayerCount = 0;
                    CancelEmptyRestart();
                    StartScheduledRestartTimer();
                    // The server is genuinely up on this build, so it becomes the one this
                    // profile "last launched". The version follows when the banner is parsed.
                    RecordLaunchedIdentity();
                    break;

                case ServerStatus.Stopping:
                    // Every pending automatic-restart timer is moot once a stop begins.
                    CancelEmptyRestart();
                    CancelEmptyUpdateCheck();
                    CancelScheduledRestart();
                    break;

                case ServerStatus.Stopped when IsRestarting:
                    _ = ResumeAfterStopAsync();
                    break;
            }
        }

        /// <summary>
        /// Completes an in-flight restart once the old process has fully exited:
        /// waits out the restart delay (longer for crash recovery, per the profile),
        /// installs pending mod updates in the stopped gap when flagged (BepInEx has
        /// no plugin files loaded right now), then relaunches with the same options.
        /// Bails out at each step if something cancelled the restart in the meantime.
        /// </summary>
        /// <summary>
        /// Hands the build (and, when known, the game version) the running server actually
        /// came up on to whoever is storing it against the profile. Best effort: a failure
        /// here must never take a healthy server down.
        /// </summary>
        private void RecordLaunchedIdentity()
        {
            var record = RecordLaunchedBuild;
            if (record == null) return;

            try
            {
                record(LaunchedBuildIdentity, GameVersion);
            }
            catch (Exception e)
            {
                ApplicationLogger.Warning("Could not record the launched build: {message}", e.Message);
            }
        }

        /// <summary>
        /// Swaps in whatever this profile has saved on disk right now, so a relaunch comes back
        /// up on the settings the host last saved rather than the ones the running session was
        /// started with. Used by every path that relaunches on the options already in hand.
        /// <para>
        /// Best effort by design: with no hook wired, a hook that cannot read the profile, or a
        /// hook that throws, the relaunch keeps the options it has and says so. A restart that
        /// refused to happen because a settings file could not be read would be a far worse
        /// answer than a restart on the settings it is already running.
        /// </para>
        /// </summary>
        /// <param name="running">The options the relaunch would otherwise reuse.</param>
        /// <returns>The saved options, or <paramref name="running"/> when there are none.</returns>
        private IValheimServerOptions RefreshOptionsForRelaunch(IValheimServerOptions running)
        {
            var refresh = RefreshOptions;
            if (refresh == null) return running;

            IValheimServerOptions saved;
            try
            {
                saved = refresh();
            }
            catch (Exception e)
            {
                ApplicationLogger.Warning(
                    "Could not read the saved settings for this relaunch, so it uses the ones the server is already running: {message}",
                    e.Message);
                return running;
            }

            if (saved == null)
            {
                ApplicationLogger.Warning(
                    "The saved settings for this profile could not be read, so the relaunch uses the ones the server is already running.");
                return running;
            }

            // Only worth a line when it actually changes the command line. Every scheduled
            // restart of an untouched profile would otherwise say the same thing forever.
            if (ValheimServerOptions.RelaunchWouldDiffer(running, saved))
            {
                ApplicationLogger.Information("Relaunch uses the settings saved on disk");
            }

            return saved;
        }

        /// <summary>
        /// The same refresh, applied to this server's own <see cref="Options"/>. This is what a
        /// relaunch with no options of its own goes through.
        /// </summary>
        private void RefreshOptionsForRelaunch() => Options = RefreshOptionsForRelaunch(Options);

        private async Task ResumeAfterStopAsync()
        {
            var delayMs = IsCrashRestart ? Options.AutoRestartDelay * 1000 : DefaultRelaunchDelayMs;
            await (RelaunchDelay ?? (ms => Task.Delay(ms)))(delayMs);

            if (!IsRestarting) return;

            var appliedUpdates = false;
            if (ApplyUpdatesOnRestart && ApplyModUpdates != null)
            {
                try
                {
                    ApplicationLogger.Information("Applying pending mod updates before restart...");
                    await ApplyModUpdates();
                    appliedUpdates = true;
                }
                catch (Exception e)
                {
                    ApplicationLogger.Error(e, "Error applying mod updates during restart; starting with the existing mods.");
                }
            }
            ApplyUpdatesOnRestart = false;

            if (!IsRestarting) return;

            IsRestarting = false;
            var wasCrash = IsCrashRestart;
            IsCrashRestart = false;

            // Every relaunch here happens on its own, without anyone necessarily watching,
            // so it goes through the launch guard as an automatic one.
            var reason = wasCrash ? LaunchReasons.Crash
                : appliedUpdates ? LaunchReasons.SelfUpdate
                : PendingLaunchReason;

            // Last thing before the relaunch: take whatever the host has saved since this
            // session started. Every automatic path lands here (crash recovery, the scheduled
            // restart, the empty-server restart, the mod-update restart and the manual Restart
            // button), so this is the one place that decides what they all come back up on.
            // The exception is a restart that was handed its own options: that caller has named
            // the settings to come back up on, and the pin is good for this relaunch only.
            var pinned = RelaunchOptionsPinned;
            RelaunchOptionsPinned = false;
            if (!pinned) RefreshOptionsForRelaunch();

            BeginLaunch(Options, reason, automatic: true);
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Scans the system for an already-running valheim_server process that was
        /// launched from <paramref name="expectedExePath"/> (the executable this
        /// BakaLoader instance manages). Returns the first matching live process, or
        /// null if none are running.
        ///
        /// Scoping to the configured executable is critical: matching by process name
        /// alone could pick up an UNRELATED valheim_server install, and the caller's
        /// "Kill it" branch would then corrupt a world BakaLoader never started. When
        /// the expected path is unknown or a candidate's image path can't be read
        /// (e.g. access denied at a different elevation), the candidate is skipped
        /// rather than risk touching a foreign server.
        /// </summary>
        public static Process FindExistingServerProcess(string expectedExePath = null)
        {
            try
            {
                var normalizedExpected = NormalizeExePath(expectedExePath);

                Process found = null;
                foreach (var p in Process.GetProcessesByName("valheim_server"))
                {
                    // Keep the first live, path-matching process; dispose the rest so
                    // their native handles aren't leaked.
                    if (found == null)
                    {
                        try
                        {
                            if (!p.HasExited && ProcessMatchesExe(p, normalizedExpected))
                            {
                                found = p;
                                continue;
                            }
                        }
                        catch { }
                    }
                    p.Dispose();
                }
                return found;
            }
            catch { }
            return null;
        }

        private static string NormalizeExePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
            catch { return null; }
        }

        private static bool ProcessMatchesExe(Process p, string normalizedExpectedExePath)
        {
            // If we don't know which executable to expect, we cannot safely claim
            // ownership of an arbitrary valheim_server - skip it.
            if (normalizedExpectedExePath == null) return false;
            try
            {
                var actual = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(actual)) return false;
                return NormalizeExePath(actual) == normalizedExpectedExePath;
            }
            catch
            {
                // MainModule read denied (different bitness/elevation): don't assume
                // it's ours.
                return false;
            }
        }

        /// <summary>
        /// Adopts an already-running server process that was discovered at startup.
        /// The process is tracked for lifecycle (stop/restart/exit detection) and
        /// assigned to the Job Object so it dies when BakaLoader exits, but stdout/
        /// stderr capture is not available (the process was started externally).
        /// </summary>
        public void AdoptProcess(Process existingProcess, IValheimServerOptions options)
        {
            if (!CanStart) return;

            var pid = "unknown";
            try { pid = existingProcess.Id.ToString(CultureInfo.InvariantCulture); } catch { }
            ApplicationLogger.Information("Adopting existing server process (PID {pid})", pid);

            IsAdopted = true;

            ProcessKey = Guid.NewGuid().ToString();
            ProcessProvider.AddProcess(ProcessKey, existingProcess);

            // Ensure the adopted server dies when BakaLoader exits.
            Tools.Processes.ChildProcessTracker.AddProcess(existingProcess);

            // Wire up exit detection so Stop/status transitions work correctly. Crash
            // auto-restart stays off for adopted processes: we didn't launch this one,
            // so we can't assume relaunching it with our options is safe.
            existingProcess.EnableRaisingEvents = true;
            existingProcess.Exited += (_, _) =>
            {
                var exitCode = 0;
                try { exitCode = existingProcess.ExitCode; } catch { }
                HandleProcessExit(exitCode, allowAutoRestart: false);
            };

            Options = options;
            IsRestarting = false;

            // An adopted process prints nothing we can read, so the version banner that
            // normally fills this in will never arrive. Read the install now instead: without
            // it the profile keeps whatever build it had before the adoption, and the next
            // real Start asks the host about a change that never happened.
            LaunchedBuildIdentity = ProbeInstall(options)?.Identity;

            // Moving to Running is what records the identity (OnStatusTransition), so this
            // assignment has to come after the line above.
            Status = ServerStatus.Running;
        }

        /// <summary>
        /// Force-kills only BakaLoader's own tracked server process (by ProcessKey).
        /// Used as a safety net during application shutdown. The Job Object
        /// (ChildProcessTracker) is the primary kill-on-exit guarantee; this gives a
        /// clean immediate exit for the process we started/adopted WITHOUT touching
        /// any other valheim_server instances that may be running on the same machine
        /// (e.g. a second server the user runs manually) - killing those would corrupt
        /// worlds BakaLoader never started.
        /// </summary>
        public void ForceKillTrackedProcess()
        {
            // Capture once: the Exited handler may null ProcessKey on a ThreadPool
            // thread between the check and the use, which would otherwise pass null
            // (or a just-cleared key) to SafelyKillProcess.
            var key = ProcessKey;
            if (key == null) return;
            try { ProcessProvider.SafelyKillProcess(key); } catch { }
        }

        /// <summary>
        /// The live tracked server process (spawned or adopted), or null when no server
        /// is running. Used by the UI for CPU/RAM metrics; callers must tolerate the
        /// process exiting between the fetch and any member access.
        /// </summary>
        public Process GetTrackedProcess()
        {
            var key = ProcessKey;
            if (key == null) return null;
            try { return ProcessProvider.GetProcess(key); } catch { return null; }
        }

        /// <summary>
        /// Starts the server for the host: the Start button, and every other deliberate
        /// start. Goes through the launch guard when one is wired, which is what stops a
        /// server coming up on a build its worlds have never been opened by.
        /// </summary>
        public void Start(IValheimServerOptions options)
            => BeginLaunch(options, LaunchReasons.Manual, automatic: false);

        /// <summary>
        /// Starts the server on BakaLoader's own initiative (the profile's auto-start at app
        /// launch). Nobody is assumed to be watching, so the launch guard may not open a
        /// dialog: it holds the start and reports the condition instead.
        /// </summary>
        public void StartAutomatically(IValheimServerOptions options)
            => BeginLaunch(options, LaunchReasons.AutoStart, automatic: true);

        /// <summary>True while a launch is waiting on the guard (or on a pre-update backup).</summary>
        public bool LaunchInProgress => LaunchPending;

        /// <summary>
        /// True when an automatic launch that could not go ahead has a retry scheduled, so the
        /// question comes back around on its own rather than leaving the server down for good.
        /// </summary>
        public bool LaunchRetryArmed => LaunchRetryCts != null;

        /// <summary>
        /// True when this server is on its way back up even though no process is running yet.
        /// <para>
        /// Three shapes land here. A restart between the old process exiting and the relaunch
        /// firing, which is where a crash with auto restart on sits: the status is already
        /// Stopped and the relaunch is scheduled. A launch the guard has not answered yet, which
        /// has no status of its own. And a held automatic launch whose retry is armed, which will
        /// ask again on its own. Anything deciding "is this server finished with" has to count
        /// all three, or it reads a Stopped server a moment before it starts itself again.
        /// </para>
        /// </summary>
        public bool RelaunchPending => IsRestarting || LaunchPending || LaunchRetryArmed;

        /// <summary>
        /// The single door every launch goes through. With no guard wired the process starts
        /// synchronously exactly as it always did; with one, the guard is asked first and the
        /// launch happens (or does not) once it answers.
        /// </summary>
        private void BeginLaunch(IValheimServerOptions options, string reason, bool automatic)
        {
            if (options == null) return;
            if (ProcessKey != null || Status != ServerStatus.Stopped) return;

            // Before anything else: an update rewriting this install folder means the exe and the
            // managed assemblies beside it are mid-write, so there is no build here to start. This
            // sits ahead of the claim on purpose, so a refusal leaves the flag exactly as it was.
            if (IsLaunchBlocked())
            {
                ApplicationLogger.Warning(
                    "Start refused for {name} ({reason}): {message}", options.Name, reason, LaunchBlockedMessage);

                // Nobody is watching an automatic one, and a scheduled restart that lands in the
                // stop to start gap of an update would otherwise leave the server down until
                // somebody noticed. Ask again later, the same way a held launch does.
                if (automatic) ArmLaunchRetry(options, reason);

                SettleLaunch(reason, held: false, error: LaunchBlockedMessage);
                return;
            }

            // Claim the launch before doing anything else with it. Everything above is a plain
            // read of state a running server owns; this is the one step that has to be atomic,
            // because it is what a second Start (or a timer firing on its own thread) loses to.
            if (!ClaimLaunch()) return;

            CancelLaunchRetry();

            if (ConfirmLaunchAsync == null)
            {
                // No guard wired: nothing is awaited, so the claim is only wanted for the
                // length of the start itself.
                try { StartCore(options, null); }
                finally { ReleaseLaunch(); }
                return;
            }

            // Off the calling thread on purpose. A guard that answers immediately would
            // otherwise run the whole launch - including a world snapshot that can be
            // gigabytes - inline on the UI thread and freeze the window mid-copy.
            _ = Task.Run(() => GuardedLaunchAsync(options, reason, automatic));
        }

        /// <summary>
        /// Asks the launch guard, then either starts (copying the worlds aside first when the
        /// host asked for that) or leaves the server down and says why. An automatic launch
        /// that is held arms a retry, so the question comes back around on its own.
        /// </summary>
        private async Task GuardedLaunchAsync(IValheimServerOptions options, string reason, bool automatic)
        {
            try
            {
                var current = ProbeInstall(options);
                var context = new LaunchContext
                {
                    Automatic = automatic,
                    Reason = reason,
                    Current = current,
                    LastLaunchedBuild = options.LastLaunchedServerBuild,
                    LastLaunchedFingerprint = options.LastLaunchedServerFingerprint,
                    LastLaunchedGameVersion = options.LastLaunchedGameVersion,
                    HasWorlds = HasAnyWorld(options),
                    ProfileName = ServerKey,
                    Options = options,
                };

                LaunchDecision decision;
                var guardThrew = false;
                try
                {
                    decision = await ConfirmLaunchAsync(context);
                }
                catch (Exception e)
                {
                    // A guard that throws must not become a silent "yes".
                    ApplicationLogger.Error(e, "The launch guard failed; the server was not started.");
                    decision = null;
                    guardThrew = true;
                }

                if (decision == null || !decision.Proceed)
                {
                    var note = decision?.Note;
                    ApplicationLogger.Warning(
                        "Start held for {name} ({reason}): {note}",
                        options.Name,
                        reason,
                        string.IsNullOrWhiteSpace(note) ? "the launch guard did not clear it" : note);

                    ReleaseLaunch();
                    if (automatic) ArmLaunchRetry(options, reason);

                    // A guard that threw is a failure, not a decision the host was shown.
                    SettleLaunch(reason, held: !guardThrew, error: guardThrew
                        ? "The launch check could not run, so the server was not started."
                        : null);
                    return;
                }

                if (decision.BackupFirst && !RunPreUpdateBackup(options, out var backupError))
                {
                    // The snapshot failed, so the start is abandoned: the whole point of the
                    // backup was that this launch converts the worlds one way. The snapshot's
                    // own sentence names the world and says what stopped it, so it is passed
                    // through as written rather than replaced with a generic line.
                    ReleaseLaunch();
                    if (automatic) ArmLaunchRetry(options, reason);
                    SettleLaunch(reason, held: false,
                        error: string.IsNullOrWhiteSpace(backupError)
                            ? "The worlds could not be copied aside, so the server was not started."
                            : backupError);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(decision.Note))
                {
                    ApplicationLogger.Information("Launch guard cleared {name}: {note}", options.Name, decision.Note);
                }

                // The claim is held ACROSS the start, exactly as the unguarded branch above holds
                // it. StartCore sets nothing a second caller would notice until it has copied the
                // companion plugins onto disk and rewritten the access lists, which is tens of
                // milliseconds and longer behind a virus scanner. Giving the claim back before
                // that leaves CanStart true for the whole stretch, and the retry timer firing in
                // that window used to run the guard, and the world snapshot, a second time.
                try { StartCore(options, current); }
                finally { ReleaseLaunch(); }

                // A refused exe path or save folder leaves Status on Stopped with no event of
                // its own, so say so rather than letting the UI show a start that never was.
                if (Status == ServerStatus.Stopped)
                {
                    SettleLaunch(reason, held: false, error: "The server did not start. Check the log for what stopped it.");
                }
            }
            catch (Exception e)
            {
                ReleaseLaunch();
                ApplicationLogger.Error(e, "Could not start the server for profile {name}.", options?.Name);

                // Before the guard moved this onto a background thread these validation errors
                // came back out of the start call and the host saw the exact bad path.
                SettleLaunch(reason, held: false, error: e.Message);
            }
        }

        /// <summary>
        /// Announces that a launch attempt is over and the server is still down. Best effort:
        /// a listener that throws must not take anything else with it.
        /// </summary>
        private void SettleLaunch(string reason, bool held, string error)
        {
            var settled = LaunchSettled;
            if (settled == null) return;

            try
            {
                settled(this, new LaunchSettledEventArgs { Reason = reason, Held = held, Error = error });
            }
            catch (Exception e)
            {
                ApplicationLogger.Warning("Could not report the settled launch: {message}", e.Message);
            }
        }

        /// <summary>
        /// Asks the launch guard whether this profile may come back up, while the server is
        /// still running and nothing has been torn down. A restart that will not be allowed to
        /// relaunch has to be skipped rather than started: asking after the shutdown turns a
        /// routine cycle into an outage that lasts until somebody notices.
        /// <para>
        /// Returns true when no guard is wired or the guard cleared the restart. On a hold the
        /// caller leaves the server up and re-arms its own timer for the next cycle.
        /// </para>
        /// </summary>
        public async Task<bool> ConfirmAutomaticRestartAsync(string reason)
        {
            var guard = ConfirmLaunchAsync;
            var options = Options;
            if (guard == null || options == null) return true;

            LaunchDecision decision;
            try
            {
                decision = await guard(new LaunchContext
                {
                    Automatic = true,
                    Reason = reason,
                    Current = ProbeInstall(options),
                    LastLaunchedBuild = options.LastLaunchedServerBuild,
                    LastLaunchedFingerprint = options.LastLaunchedServerFingerprint,
                    LastLaunchedGameVersion = options.LastLaunchedGameVersion,
                    HasWorlds = HasAnyWorld(options),
                    ProfileName = ServerKey,
                    Options = options,
                });
            }
            catch (Exception e)
            {
                ApplicationLogger.Error(e,
                    "The launch check failed, so the restart was skipped and the server left running.");
                SettleLaunch(reason, held: false,
                    error: "The launch check could not run, so the restart was skipped.");
                return false;
            }

            if (decision != null && decision.Proceed) return true;

            var note = decision?.Note;
            ApplicationLogger.Warning(
                "Restart skipped for {name} ({reason}): {note} The server was left running.",
                options.Name,
                reason,
                string.IsNullOrWhiteSpace(note) ? "the launch guard did not clear it." : note);

            SettleLaunch(reason, held: true, error: null);
            return false;
        }

        /// <summary>Reads what the configured install is right now. Never throws.</summary>
        private Tools.ServerBuildInfo ProbeInstall(IValheimServerOptions options)
        {
            try
            {
                return Tools.ServerBuildTracker.Probe(options.ServerExePath);
            }
            catch (Exception e)
            {
                ApplicationLogger.Warning("Could not read the server build: {message}", e.Message);
                return null;
            }
        }

        /// <summary>True when the profile's save folder already holds at least one world.</summary>
        private static bool HasAnyWorld(IValheimServerOptions options)
        {
            try
            {
                return WorldStore.Enumerate(options.SaveDataFolderPath).Count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Copies every world of this profile aside before a launch that will upgrade them.
        /// Returns false when the copy failed, which aborts the launch, and hands back the
        /// snapshot's own sentence for why in <paramref name="error"/> so the caller can show
        /// it as written. Null when the copy succeeded.
        /// </summary>
        private bool RunPreUpdateBackup(IValheimServerOptions options, out string error)
        {
            WorldStore.WorldSnapshotResult result;
            try
            {
                result = WorldStore.SnapshotAllPreUpdate(
                    options.SaveDataFolderPath, whenLocal: null, refuse: WorldSnapshotVeto);
            }
            catch (Exception e)
            {
                result = new WorldStore.WorldSnapshotResult { Error = e.Message };
            }

            if (result.Ok)
            {
                ApplicationLogger.Information(
                    "Copied {count} world(s) aside before starting ({bytes} bytes): {names}",
                    result.Copied.Count,
                    result.Bytes,
                    string.Join(", ", result.Copied));

                if (result.Skipped.Count > 0)
                {
                    ApplicationLogger.Warning(
                        "Not copied aside: {names}", string.Join(", ", result.Skipped));
                }
            }
            else
            {
                ApplicationLogger.Error(
                    "Could not copy the worlds aside, so the server was not started: {error}", result.Error);
            }

            PreUpdateBackupCompleted?.Invoke(this, result);
            error = result.Ok ? null : result.Error;
            return result.Ok;
        }

        /// <summary>
        /// Re-asks a held automatic launch later. The cadence follows whichever schedule asked
        /// for the launch, floored so a fast crash-restart delay cannot turn into a retry loop.
        /// </summary>
        private void ArmLaunchRetry(IValheimServerOptions options, string reason)
        {
            CancelLaunchRetry();

            var scheduleMinutes = reason switch
            {
                LaunchReasons.Scheduled => Math.Max(1, options.ScheduledRestartHours) * 60,
                LaunchReasons.Empty => Math.Max(1, options.EmptyServerRestartDelayMinutes),
                LaunchReasons.Crash => Math.Max(1, options.AutoRestartDelay) / 60,
                _ => 0,
            };
            var minutes = Math.Max(LaunchHoldRetryMinutes, scheduleMinutes);

            var cts = new CancellationTokenSource();
            LaunchRetryCts = cts;
            var token = cts.Token;

            ApplicationLogger.Information(
                "Will ask again about starting {name} in {minutes} minute(s).", options.Name, minutes);

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(minutes * 60 * 1000, token);
                }
                catch (OperationCanceledException)
                {
                    return; // the host started (or stopped) the server in the meantime
                }

                if (token.IsCancellationRequested) return;
                if (!CanStart) return;

                // A retry can fire hours after the launch it is retrying (a scheduled restart's
                // own interval is the floor), so it asks for the saved settings the same way a
                // relaunch does rather than carrying an old command line back up with it.
                BeginLaunch(RefreshOptionsForRelaunch(options), reason, automatic: true);
            });
        }

        private void CancelLaunchRetry()
        {
            var cts = LaunchRetryCts;
            if (cts == null) return;

            LaunchRetryCts = null;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        /// <summary>
        /// Launches valheim_server.exe as a tracked background process using the given
        /// profile: prepares companion plugins, wires up log capture and exit/crash
        /// detection, boosts process priority, and moves the status to Starting.
        /// </summary>
        private void StartCore(IValheimServerOptions options, Tools.ServerBuildInfo build)
        {
            if (ProcessKey != null || Status != ServerStatus.Stopped) return;
            ApplicationLogger.Information("Starting server: {name}", options.Name);

            LaunchedBuildIdentity = build?.Identity;
            GameVersion = null;
            NetworkVersion = null;

            var exePath = options.GetValidatedServerExe().FullName;
            var launchArgs = GenerateArgs(options, ApplicationLogger);

            PrepareCompanionPlugins(exePath, options);
            UpgradeAccessLists(options);
            ApplicationLogger.Information(@"Server run command: ""{exePath}"" {processArgs}",
                exePath, RedactPassword(launchArgs));

            // The stdout pipeline must exist before IO starts, or early lines are lost.
            ServerLogger = new ValheimServerLogger(options);
            ServerLogger.LogReceived += OnServerLogLine;

            // Raw server output also flows to the UI's log view when it asked for it.
            if (options.LogMessageHandler != null) ServerLogger.LogReceived += options.LogMessageHandler;

            // Companion plugins are installed before this point on a best-effort basis, and the
            // log stream the operator is watching does not exist until now. A failure used to
            // leave only an application-log line, so the feature simply went missing with no
            // explanation. Say it where they are already looking. Only this profile's failures
            // plus any recorded with no profile in hand: another realm's plugin trouble has no
            // business in this realm's log. The message goes in as an argument, never as the
            // template: an exception message can carry braces and Serilog would read those as
            // property names.
            foreach (var failure in Tools.CompanionPluginStatus.FailuresFor(ServerKey))
                ServerLogger.Warning("{message}", failure.Describe());

            ProcessKey = Guid.NewGuid().ToString();
            var process = ProcessProvider.AddBackgroundProcess(ProcessKey, exePath, launchArgs);
            process.StartInfo.EnvironmentVariables.Add("SteamAppId", Resources.ValheimSteamAppId);
            process.OutputDataReceived += OnProcessOutput;
            process.ErrorDataReceived += OnProcessError;
            process.Exited += (_, _) =>
            {
                var exitCode = 0;
                try { exitCode = process.ExitCode; } catch { }
                HandleProcessExit(exitCode, allowAutoRestart: true);
            };

            ProcessProvider.StartIO(process);

            // Give the dedicated server process a higher CPU priority so the world
            // simulation stays smooth when other apps are competing for the CPU.
            // AboveNormal is a safe boost that won't starve the OS the way High/Realtime can.
            try
            {
                process.PriorityClass = ProcessPriorityClass.AboveNormal;
                process.PriorityBoostEnabled = true;
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Could not raise server process priority: {message}", ex.Message);
            }

            IsRestarting = false;
            RelaunchOptionsPinned = false;   // a fresh session owes nothing to an older restart
            Options = options;
            Status = ServerStatus.Starting; // last: fires StatusChanged
        }

        /// <summary>
        /// Gives the adminlist / permittedlist / bannedlist files their Valheim 1.0 twins
        /// while the server is down. Valheim 1.0 only matches a Steam id written as
        /// "V_76561198...", so a list carried over from an older server would silently stop
        /// working - and the host would find out by being locked out of their own server.
        /// Best effort: a list that cannot be rewritten never blocks a start.
        /// </summary>
        private void UpgradeAccessLists(IValheimServerOptions options)
        {
            if (PlayerLists == null) return;

            try
            {
                var added = PlayerLists.UpgradeAll(options.SaveDataFolderPath);
                if (added > 0)
                {
                    ApplicationLogger.Information(
                        "Added {count} Valheim 1.0 id line(s) to the admin/permitted/banned lists.", added);
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Could not upgrade the access lists: {message}", ex.Message);
            }
        }

        /// <summary>
        /// Ensures the companion BepInEx plugins are installed and current while the
        /// server is still down - BepInEx only picks up plugin files at launch. Each
        /// installer failure is logged and skipped so one bad plugin can't block a start.
        /// </summary>
        private void PrepareCompanionPlugins(string exePath, IValheimServerOptions options)
        {
            var pluginsDir = GetPluginsDirectory(exePath);

            // Two realms can be up at once, and the record the interface reads is process-wide,
            // so everything this pass writes is filed under this server's own profile. Start by
            // forgetting what this profile recorded last time, so a plugin that has since been
            // fixed stops being reported; the other realm's records are left exactly as they are.
            // Every session's server is stamped with its profile, so the guard only skips the
            // clear on a bare instance that has no realm to speak for, where wiping the records
            // that belong to nobody in particular would be reaching too far.
            if (!string.IsNullOrWhiteSpace(ServerKey)) Tools.CompanionPluginStatus.ClearProfile(ServerKey);
            using var pluginScope = Tools.CompanionPluginStatus.BeginProfile(ServerKey);

            void Install(string label, Action install)
            {
                try
                {
                    install();
                }
                catch (Exception ex)
                {
                    ApplicationLogger.Warning("Could not prepare the " + label + " plugin: {message}", ex.Message);

                    // The application log is not where an operator looks. Record it so the
                    // server log and the interface can both say the feature is off and why.
                    // The two installers that report for themselves swallow their own failures,
                    // so this only ever covers the three that do not, and the record is keyed
                    // on the name either way so a plugin can never be listed twice.
                    Tools.CompanionPluginStatus.ReportFailure(label, ex.Message);
                }
            }

            // First, because every plugin below it loads under BepInEx: installing the
            // companions into a folder no loader will ever read is work that looks like it
            // worked. A failure is recorded under this profile and the start carries on.
            Install("BepInEx", () => PrepareBepInEx?.Invoke(exePath));

            Install("item indexer", () => ItemIndexerInstaller?.EnsureInstalled(pluginsDir));
            Install("spawn helper", () => SpawnHelperInstaller?.EnsureInstalled(pluginsDir));
            Install("kill-all", () => KillAllInstaller?.EnsureInstalled(pluginsDir));
            // Max-players is install-on-demand (only present when the cap was raised past 10),
            // but EnsureCurrent still runs every start: it refreshes an existing install and
            // migrates away from the legacy third-party Azumatt-MaxPlayerCount mod.
            Install("max-players", () => MaxPlayersInstaller?.EnsureCurrent(pluginsDir));
            Install("Commander", () =>
            {
                // Commander = BakaLoader's own RCON server + native command suite. Its cfg
                // (port/password/enabled) must mirror the profile before BepInEx loads it.
                CommanderInstaller?.EnsureInstalled(pluginsDir);
                CommanderInstaller?.EnsureConfig(pluginsDir, options.RconEnabled, options.RconPort, options.RconPassword);
            });
        }

        /// <summary>
        /// Shared exit bookkeeping for started and adopted processes: releases the
        /// process key, detects crashes (nonzero exit outside a deliberate stop),
        /// optionally arms crash auto-restart, and lands the status on Stopped.
        /// </summary>
        private void HandleProcessExit(int exitCode, bool allowAutoRestart)
        {
            var crashed = exitCode != 0 && Status != ServerStatus.Stopping;

            ProcessKey = null;
            IsAdopted = false;

            if (crashed)
            {
                ApplicationLogger.Warning("Server crashed with exit code {exitCode}", exitCode);
                ServerCrashed?.Invoke(this, EventArgs.Empty);

                if (allowAutoRestart && Options.AutoRestart)
                {
                    // The process is already gone, so there is nothing to shut down and nothing
                    // to protect by asking here. This relaunch starts from a stopped server, so
                    // it asks the guard at launch time in ResumeAfterStopAsync; a hold there
                    // arms a retry rather than leaving anything half torn down.
                    ApplicationLogger.Information("Auto-restarting server in {delay} seconds...", Options.AutoRestartDelay);
                    IsCrashRestart = true;
                    IsRestarting = true;
                    PendingLaunchReason = LaunchReasons.Crash;
                }
            }

            Status = ServerStatus.Stopped;
        }

        /// <summary>Asks the server process to shut down gracefully (world save included).</summary>
        public void Stop()
        {
            if (!CanStop) return;
            CancelCountdown();
            BeginShutdown(restartAfter: false);
        }

        /// <summary>
        /// Gracefully shuts the server down and relaunches it once the process has
        /// exited. Passing options swaps the profile for the relaunch; otherwise the
        /// settings saved on disk for this profile are read afresh, so a change the host
        /// made while the server was up is in force when it comes back.
        /// </summary>
        public void Restart(IValheimServerOptions options = null, string reason = null)
        {
            if (!CanRestart) return;

            RelaunchOptionsPinned = options != null;
            if (options != null) Options = options;
            else RefreshOptionsForRelaunch();
            PendingLaunchReason = reason ?? LaunchReasons.Manual;
            BeginShutdown(restartAfter: true);
        }

        /// <summary>
        /// Common teardown for Stop and Restart: signals the process to exit cleanly
        /// and flags whether the Stopped handler should relaunch afterwards.
        /// </summary>
        private void BeginShutdown(bool restartAfter)
        {
            ApplicationLogger.Information(
                restartAfter ? "Restarting server: {name}" : "Stopping server: {name}",
                Options.Name);
            ProcessProvider.SafelyKillProcess(ProcessKey);
            IsRestarting = restartAfter;
            Status = ServerStatus.Stopping;
        }

        /// <summary>
        /// Runs the "auto-update BakaLoader on restart" hook, if wired. Returns true when an app
        /// update has been staged - in which case the caller must abandon its restart, because the
        /// watchdog is now closing BakaLoader (and its child server) and will relaunch the updated
        /// app, which auto-starts the server again. Returns false (and lets the caller proceed with
        /// a normal restart) when auto-update is off, nothing newer is available, or the check fails.
        /// This must run on EVERY restart path - manual, scheduled, and both empty-server paths.
        /// </summary>
        private async Task<bool> TryStageAppUpdateAsync()
        {
            if (CheckForAppUpdateOnRestart == null) return false;
            try
            {
                return await CheckForAppUpdateOnRestart();
            }
            catch (Exception e)
            {
                ApplicationLogger.Error(e, "Self-update check failed during restart; continuing with a normal restart.");
                return false;
            }
        }

        public async Task RestartWithCountdown(int[] countdownSeconds = null, bool applyModUpdates = false, string reason = null)
        {
            if (!CanRestart) return;
            if (IsCountdownActive) return;

            // Before doing anything else, give the app a chance to update itself. If an app
            // update is staged, the watchdog is now closing BakaLoader (and, with it, the child
            // server) and will relaunch the updated app - which auto-starts the server again -
            // so abandon this restart entirely rather than fighting the shutdown.
            if (await TryStageAppUpdateAsync()) return;

            // If this is an auto-update restart, find out how many mods will be updated so we
            // can tell players. The hook returns 0 when auto-update is off or nothing is pending.
            var modUpdateCount = 0;
            if (applyModUpdates && GetPendingModUpdateCount != null)
            {
                try { modUpdateCount = await GetPendingModUpdateCount(); }
                catch (Exception e) { ApplicationLogger.Error(e, "Could not determine pending mod updates."); }
            }

            // The update message suffix appended to each countdown announcement.
            var updateNote = modUpdateCount > 0
                ? $" ({modUpdateCount} mod update{(modUpdateCount == 1 ? "" : "s")} pending)"
                : string.Empty;

            // No RCON configured -> nothing to announce, just restart now
            if (!Options.RconEnabled)
            {
                ApplyUpdatesOnRestart = modUpdateCount > 0;
                Restart(reason: reason);
                return;
            }

            var points = (countdownSeconds ?? DefaultCountdownSeconds)
                .Where(s => s > 0)
                .Distinct()
                .OrderByDescending(s => s)
                .ToArray();

            if (points.Length == 0)
            {
                ApplyUpdatesOnRestart = modUpdateCount > 0;
                Restart(reason: reason);
                return;
            }

            var cts = new CancellationTokenSource();
            CountdownCts = cts;
            var token = cts.Token;
            var shouldRestart = false;

            try
            {
                // Under the gate as well: ConnectAsync starts by clearing the validated flag, so
                // opening the countdown's connection on top of the roster poll's in flight command
                // would leave that command reading from a socket nothing considers authenticated.
                bool connected;
                await RconGate.WaitAsync();
                try
                {
                    connected = await RconClient.ConnectAsync("127.0.0.1", Options.RconPort, Options.RconPassword);
                }
                finally
                {
                    RconGate.Release();
                }

                if (!connected)
                {
                    ApplicationLogger.Warning("RCON unavailable; restarting without in-game countdown.");
                    shouldRestart = true;
                    return;
                }

                ApplicationLogger.Information("Starting restart countdown ({minutes} minutes)", points[0] / 60);

                // Schedule every announcement off one absolute deadline so send latency can't
                // accumulate into drift, and re-validate the shared RCON client before each
                // announcement: other RCON features (spawn-at-player, playerlist, manual
                // broadcasts) call Disconnect() on the same client when they finish, which
                // used to silently kill every remaining countdown warning.
                var restartAtUtc = DateTime.UtcNow.AddSeconds(points[0]);

                for (var i = 0; i < points.Length; i++)
                {
                    token.ThrowIfCancellationRequested();

                    var remaining = points[i];
                    await SendCountdownBroadcastAsync($"Server restarting in {FormatTime(remaining)}!{updateNote}");
                    CountdownTick?.Invoke(this, $"Restart in {FormatTime(remaining)}");

                    var next = (i + 1 < points.Length) ? points[i + 1] : 0;
                    var waitTime = restartAtUtc.AddSeconds(-next) - DateTime.UtcNow;
                    if (waitTime > TimeSpan.Zero) await Task.Delay(waitTime, token);
                }

                await SendCountdownBroadcastAsync($"Server restarting NOW!{updateNote}");
                CountdownTick?.Invoke(this, "Restarting now");
                await Task.Delay(1500, token);

                shouldRestart = true;
            }
            catch (OperationCanceledException)
            {
                if (CountdownBypassRequested)
                {
                    // "Restart NOW" - the user skipped the rest of the countdown.
                    CountdownBypassRequested = false;
                    ApplicationLogger.Information("Restart countdown bypassed. Restarting now.");
                    try
                    {
                        await SendCountdownBroadcastAsync($"Server restarting NOW!{updateNote}");
                    }
                    catch { }
                    shouldRestart = true;
                }
                else
                {
                    ApplicationLogger.Information("Scheduled restart cancelled.");
                    try
                    {
                        await SendCountdownBroadcastAsync("Server restart cancelled.");
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                ApplicationLogger.Error(e, "Error during restart countdown");
                shouldRestart = true; // best-effort: still restart if the announcement failed
            }
            finally
            {
                // Same reason as the connect above: this clears the validated flag, so it waits
                // for whatever else is mid command rather than pulling the floor out from under it.
                await RconGate.WaitAsync();
                try { RconClient.Disconnect(); }
                finally { RconGate.Release(); }

                if (CountdownCts == cts) CountdownCts = null;
                cts.Dispose();
                CountdownTick?.Invoke(this, null);
            }

            if (shouldRestart)
            {
                ApplyUpdatesOnRestart = modUpdateCount > 0;
                Restart(reason: reason);
            }
        }

        /// <summary>
        /// Cancels an in-progress restart countdown, if any.
        /// </summary>
        public void CancelCountdown()
        {
            CountdownCts?.Cancel();
        }

        /// <summary>Set just before cancelling the countdown CTS to signal "restart NOW" rather than "cancel".</summary>
        private volatile bool CountdownBypassRequested;

        /// <summary>
        /// Skips the remainder of an active restart countdown and restarts immediately
        /// ("Restart NOW"). Players get a final "restarting NOW" broadcast instead of the
        /// "restart cancelled" one.
        /// </summary>
        public void BypassCountdown()
        {
            if (!IsCountdownActive) return;
            CountdownBypassRequested = true;
            CountdownCts?.Cancel();
        }

        /// <summary>
        /// A manual restart request from the UI. Behavior depends on the moment:
        /// a countdown already running is bypassed and the server restarts NOW; with players
        /// online a 60/30/10s in-game warning countdown starts (fire-and-forget); with an
        /// empty server the restart happens immediately - no countdown, no broadcast.
        /// Returns "bypassed", "countdown", "now", or "unavailable" so the UI can phrase its feedback.
        /// </summary>
        public async Task<string> RequestSmartRestart()
        {
            if (IsCountdownActive)
            {
                BypassCountdown();
                return "bypassed";
            }

            if (!CanRestart) return "unavailable";

            if (CountActivePlayers(PlayerDataRepository.Data) == 0)
            {
                // Nobody to warn - restart right away. Every restart path must still give
                // the self-updater a chance to stage first (it relaunches the app itself).
                if (!await TryStageAppUpdateAsync()) Restart();
                return "now";
            }

            _ = RestartWithCountdown(ManualRestartCountdownSeconds);
            return "countdown";
        }

        /// <summary>
        /// Re-validates the shared RCON client if some other RCON operation (spawn-at-player,
        /// playerlist, a manual broadcast) has called Disconnect() on it since the countdown
        /// connected. Best-effort: a failure is logged and the countdown keeps its schedule so
        /// later announcements (and the restart itself) still happen on time.
        /// </summary>
        /// <summary>
        /// One countdown announcement, taken through the SAME single socket gate every other RCON
        /// caller uses. Re-validating and sending were two separate steps outside the gate, and
        /// the roster's position poll runs every few seconds: its own connect (which starts by
        /// clearing the validated flag) or its closing disconnect landing between the two left the
        /// client unvalidated, so the send returned null and the warning never reached a player.
        /// The countdown swallowed that and carried on, so the server went down unannounced.
        /// </summary>
        private async Task SendCountdownBroadcastAsync(string message)
        {
            await RconGate.WaitAsync();
            try
            {
                await EnsureRconConnectedAsync();
                await RconClient.SendCommandAsync(BuildBroadcast(message));
            }
            finally
            {
                RconGate.Release();
            }
        }

        private async Task EnsureRconConnectedAsync()
        {
            if (RconClient.IsConnected) return;

            try
            {
                var reconnected = await RconClient.ConnectAsync("127.0.0.1", Options.RconPort, Options.RconPassword);
                if (!reconnected)
                {
                    ApplicationLogger.Warning("RCON reconnect failed; a countdown announcement may not reach players.");
                }
            }
            catch (Exception e)
            {
                ApplicationLogger.Warning("RCON reconnect error during countdown: {message}", e.Message);
            }
        }

        /// <summary>
        /// Immediately broadcasts a message to all players in-game over RCON.
        /// Returns true if the message was sent, false if RCON is disabled/unreachable
        /// or the server isn't running. The connection is opened and closed per call.
        /// </summary>
        public async Task<bool> BroadcastNow(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;

            if (!Options.RconEnabled)
            {
                ApplicationLogger.Warning("Cannot send announcement: RCON is not enabled for this server.");
                return false;
            }

            if (Status != ServerStatus.Running)
            {
                ApplicationLogger.Warning("Cannot send announcement: the server is not running.");
                return false;
            }

            // Same single-socket rule as SendRconCommandAsync: see the note there.
            await RconGate.WaitAsync();
            try
            {
                var connected = await RconClient.ConnectAsync("127.0.0.1", Options.RconPort, Options.RconPassword);
                if (!connected)
                {
                    ApplicationLogger.Warning("Cannot send announcement: RCON connection failed.");
                    return false;
                }

                await RconClient.SendCommandAsync(BuildBroadcast(message));
                ApplicationLogger.Information("Announcement sent: {message}", message);
                return true;
            }
            catch (Exception e)
            {
                ApplicationLogger.Error(e, "Error sending announcement over RCON");
                return false;
            }
            finally
            {
                RconClient.Disconnect();
                RconGate.Release();
            }
        }

        /// <summary>
        /// Sends an arbitrary RCON command to the running server and returns the raw response
        /// text (empty string on success-with-no-output, null on failure / RCON off / not running).
        /// Factored exactly like <see cref="BroadcastNow"/>: opens and closes the connection per call.
        /// All player-targeting actions below route through this single method.
        /// </summary>
        public async Task<string> SendRconCommandAsync(string command, bool quiet = false)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            if (!Options.RconEnabled)
            {
                if (!quiet) ApplicationLogger.Warning("Cannot send RCON command: RCON is not enabled for this server.");
                return null;
            }

            if (Status != ServerStatus.Running)
            {
                if (!quiet) ApplicationLogger.Warning("Cannot send RCON command: the server is not running.");
                return null;
            }

            // One command at a time. The client opens a fresh socket per command and closes it
            // in the finally below, so two overlapping callers would have one tearing the other's
            // connection down mid-read. The roster's position poll runs on its own timer, which
            // is exactly the caller that would otherwise land on top of a spawn or a broadcast.
            await RconGate.WaitAsync();
            try
            {
                var connected = await RconClient.ConnectAsync("127.0.0.1", Options.RconPort, Options.RconPassword);
                if (!connected)
                {
                    if (!quiet) ApplicationLogger.Warning("Cannot send RCON command: RCON connection failed.");
                    return null;
                }

                var response = await RconClient.SendCommandAsync(command);
                if (!quiet) ApplicationLogger.Information("RCON command sent: {command}", command);
                return response ?? string.Empty;
            }
            catch (Exception e)
            {
                if (!quiet) ApplicationLogger.Error(e, "Error sending RCON command");
                return null;
            }
            finally
            {
                RconClient.Disconnect();
                RconGate.Release();
            }
        }

        /// <summary>
        /// Resolves a player's live world coordinates from the "playerlist" command, returning a
        /// "x,z,y" string ready to feed back into the spawn_object "from=" parameter, or null if the
        /// player isn't online / couldn't be parsed.
        ///
        /// NOTE: the Server-devcommands "pos &lt;player&gt;" command does NOT work over RCON - it
        /// targets the calling player's own character, which a dedicated-server console doesn't have,
        /// so it always answers "Error: No player." "playerlist" is the RCON-safe source: it prints
        /// every online player as "{name}/{steamId}/{charId} (x, z, y)" (verified live 2026-06-14).
        /// </summary>
        public async Task<string> GetPlayerPositionAsync(string playerName)
        {
            if (string.IsNullOrWhiteSpace(playerName)) return null;

            // The AviiNL RCON mod prints "playerlist" output to the server's stdout log, but its
            // RCON response body is unreliable for this command (it comes back empty / with a
            // malformed length header, so the socket-read drains nothing). The position DOES reach
            // the server stdout stream the manager already tails, so we capture it from there: arm a
            // transient listener BEFORE firing the command, send "playerlist", then take whichever
            // source resolves first (RCON reply if a setup does echo it, otherwise the stdout line).
            var logger = ServerLogger;

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLine(string line)
            {
                var parsed = ParsePositionForPlayer(line, playerName);
                if (!string.IsNullOrEmpty(parsed)) tcs.TrySetResult(parsed);
            }

            if (logger != null) logger.LogReceived += OnLine;
            try
            {
                var response = await SendRconCommandAsync(BuildPlayerList());

                // Prefer the RCON reply when a server actually returns it (no waiting needed).
                var fromRcon = ParsePositionForPlayer(response, playerName);
                if (!string.IsNullOrEmpty(fromRcon)) return fromRcon;

                if (logger == null) return null;

                // Otherwise wait briefly for the stdout line the command just triggered.
                var finished = await Task.WhenAny(tcs.Task, Task.Delay(PlayerListCaptureTimeoutMs));
                return finished == tcs.Task ? tcs.Task.Result : null;
            }
            finally
            {
                if (logger != null) logger.LogReceived -= OnLine;
            }
        }

        /// <summary>
        /// Where a player is standing right now, as "x, y, z" rounded to whole metres, or null
        /// when that player is not in the last roster the server answered with, or when nothing
        /// has confirmed the coordinate recently enough to print it. Read straight out of the
        /// cache: no RCON call, so the WebUI may ask as often as it likes.
        /// </summary>
        public string GetCachedPosition(string characterName) => GetCachedPosition(characterName, DateTime.UtcNow);

        /// <summary>The same read against a given moment, so the staleness rule can be driven.</summary>
        internal string GetCachedPosition(string characterName, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(characterName)) return null;
            if (!PlayerPositions.TryGetValue(characterName.Trim(), out var entry)) return null;

            return PositionIsFresh(entry.TakenUtc, nowUtc) ? entry.Text : null;
        }

        /// <summary>
        /// Starts the roster's position poll. One "playerlist" call every
        /// <see cref="PositionPollSeconds"/> seconds refreshes everybody at once; there is
        /// deliberately no per-player call anywhere.
        /// </summary>
        private void StartPositionPolling()
        {
            StopPositionPolling();

            // Nothing to poll with: the roster keeps its plain hyphen and no socket is opened.
            if (Options == null || !Options.RconEnabled) return;

            var cts = new CancellationTokenSource();
            PositionPollCts = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { await RefreshPlayerPositionsAsync(); }
                    catch { /* one bad read must not end the poll for the whole session */ }

                    try { await Task.Delay(TimeSpan.FromSeconds(PositionPollSeconds), token); }
                    catch (OperationCanceledException) { return; }
                }
            }, token);
        }

        /// <summary>Stops the poll and empties the cache. Safe to call when nothing is running.</summary>
        private void StopPositionPolling()
        {
            var cts = PositionPollCts;
            PositionPollCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }

            PlayerPositions.Clear();
        }

        /// <summary>
        /// One "playerlist" round trip, parsed into every online player's coordinates. Quiet on
        /// purpose: this runs on a timer, so a failed read is not worth a log line every few
        /// seconds. A reply that names nobody empties the cache rather than leaving stale
        /// coordinates behind for players who have since logged off.
        /// </summary>
        private async Task RefreshPlayerPositionsAsync()
        {
            if (Status != ServerStatus.Running) return;

            var response = await SendRconCommandAsync(BuildPlayerList(), quiet: true);
            if (response == null) return;

            RecordPositions(ParseAllPositions(response), DateTime.UtcNow);
        }

        /// <summary>
        /// Folds one parsed "playerlist" reply into the cache: everybody named is stamped with
        /// the moment they were seen, and anybody the reply does not name is dropped rather
        /// than left standing where they were.
        /// </summary>
        internal void RecordPositions(IReadOnlyDictionary<string, string> seen, DateTime nowUtc)
        {
            if (seen == null) return;

            foreach (var pair in seen) PlayerPositions[pair.Key] = (pair.Value, nowUtc);

            foreach (var key in PlayerPositions.Keys.ToList())
            {
                if (!seen.ContainsKey(key)) PlayerPositions.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Reads every entry out of a "playerlist" reply at once, rather than running the
        /// single-player match once per name. Same anchor as
        /// <see cref="ParsePositionForPlayer"/> - "{name}/{host}/{charId} (x, z, y)" - with the
        /// name left open, so the log clock and the "Console:" prefix the RCON channel puts in
        /// front of each line have to come off first or they would be read as part of the name.
        /// Coordinates come back rounded to whole metres, which is all a roster column can show.
        /// </summary>
        public static Dictionary<string, string> ParseAllPositions(string response)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(response)) return found;

            foreach (var raw in response.Split('\n'))
            {
                var line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0) continue;

                // "09/10/2026 12:34:56: Broheim/765.../-12 (1, 2, 3)" and "Console: Broheim/..."
                line = LogClockPrefix.Replace(line, "");
                line = ConsolePrefix.Replace(line, "");

                foreach (Match match in PlayerListEntry.Matches(line))
                {
                    var name = match.Groups["name"].Value.Trim();
                    if (name.Length == 0) continue;

                    var pos = Round(match.Groups["x"].Value)
                        + ", " + Round(match.Groups["y"].Value)
                        + ", " + Round(match.Groups["z"].Value);
                    found[name] = pos;
                }
            }

            return found;

            static string Round(string value)
                => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? Math.Round(d).ToString("0", CultureInfo.InvariantCulture)
                    : value;
        }

        private static readonly Regex LogClockPrefix =
            new(@"^.*?\d{1,2}:\d{2}:\d{2}(?:\.\d+)?\s*:?\s*", RegexOptions.Compiled);

        private static readonly Regex ConsolePrefix =
            new(@"^\s*Console\s*:\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PlayerListEntry = new(
            @"(?<name>[^\s/][^/]*?)/(?<host>[^\s/]+)/(?<character>[^\s(]+)\s*\(\s*"
            + @"(?<x>-?\d+(?:\.\d+)?)\s*,\s*(?<y>-?\d+(?:\.\d+)?)\s*,\s*(?<z>-?\d+(?:\.\d+)?)\s*\)",
            RegexOptions.Compiled);

        /// <summary>
        /// Spawns <paramref name="amount"/> of the given catalog entry at <paramref name="playerName"/>'s
        /// current position. First resolves the player's coordinates (pos), then issues a coordinate
        /// spawn so the items/creatures appear around the player. The answer carries what the server
        /// itself said, because that is the only thing that knows whether anything landed: this used
        /// to report success for any reply at all, refusals included.
        /// </summary>
        public async Task<SpawnResult> SpawnAtPlayerAsync(string playerName, ItemCatalogEntry entry, int amount, int levelOrQuality)
        {
            if (entry == null) return new SpawnResult { Ok = false, Message = "nothing was chosen to spawn" };

            var coords = await GetPlayerPositionAsync(playerName);
            if (string.IsNullOrEmpty(coords))
            {
                ApplicationLogger.Warning("Could not resolve position for player {player}; spawn aborted.", playerName);
                return new SpawnResult
                {
                    Ok = false,
                    Message = "could not find that viking's position, so nothing was spawned",
                };
            }

            var response = await SendRconCommandAsync(BuildSpawn(entry, amount, levelOrQuality, coords));
            var result = ParseSpawnReply(response);

            if (!result.Ok)
            {
                // The message goes in as an argument, never as the template: an exception text
                // full of braces would otherwise be read as Serilog property names and vanish.
                ApplicationLogger.Warning(
                    "Spawn of {prefab} for {player} did not take: {reply}",
                    entry.PrefabName, playerName, result.Message);
            }

            return result;
        }

        /// <summary>
        /// Reads the server's answer to a spawn command. Both plugins that answer "baka_spawn"
        /// open their success line with a fixed word. Commander spawns in place and says
        /// "Spawned ...", the spawn helper hands the work to the Unity main thread and says
        /// "Queued spawn: ...", and everything else the console can say (a prefab it does not
        /// know, a command that threw, the usage line) opens with something else, so the opening
        /// is the whole test. An empty body reads the same as no body at all: the command may
        /// well have run, but nothing said so, and guessing on the host's behalf is what this
        /// replaced.
        /// </summary>
        public static SpawnResult ParseSpawnReply(string reply)
        {
            var line = FirstSpokenLine(reply);
            if (line.Length == 0) return new SpawnResult { Ok = false, Message = "no reply from the server" };

            var ok = line.StartsWith("Spawned", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Queued spawn", StringComparison.OrdinalIgnoreCase);

            return new SpawnResult { Ok = ok, Message = line };
        }

        /// <summary>
        /// The first thing the console actually said, with the log clock and the "Console:" tag
        /// the RCON channel puts in front of some lines taken off (the same two prefixes the
        /// roster reader strips), and the rest of a multi line answer left behind: a stack trace
        /// says nothing a toast has room for, and the first line already names the failure.
        /// </summary>
        private static string FirstSpokenLine(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return string.Empty;

            foreach (var raw in reply.Split('\n'))
            {
                var line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0) continue;

                line = LogClockPrefix.Replace(line, "");
                line = ConsolePrefix.Replace(line, "");
                line = line.Trim();

                if (line.Length > 0) return line;
            }

            return string.Empty;
        }

        /// <summary>Kicks a player by name, Steam/Platform id, or IP (vanilla "kick").</summary>
        public Task<string> KickAsync(string target)
        {
            return SendRconCommandAsync(BuildKick(target));
        }

        /// <summary>Deals lethal damage to a player ("smite"). Syntax is VERIFY-LIVE.</summary>
        public Task<string> SmiteAsync(string playerName)
        {
            return SendRconCommandAsync(BuildDamage(playerName, 1000000));
        }

        /// <summary>Fully heals a player. Syntax is VERIFY-LIVE.</summary>
        public Task<string> HealAsync(string playerName)
        {
            return SendRconCommandAsync(BuildHeal(playerName));
        }

        /// <summary>
        /// Teleports <paramref name="playerName"/> to a destination (another player's name, or an
        /// "x,z,y" coordinate triple - human-typed spacing is tolerated and normalized).
        /// </summary>
        public Task<string> TeleportAsync(string playerName, string destination)
        {
            return SendRconCommandAsync(BuildTp(playerName, NormalizeTeleportDestination(destination)));
        }

        /// <summary>
        /// Humans type coordinate triples every which way - "100, 1,  211", "100 1 211",
        /// "100,1,211". The RCON tp command parses its final token as a compact "x,z,y",
        /// so any internal whitespace breaks it. If the destination splits into 2-3 purely
        /// numeric tokens (on commas and/or whitespace), rejoin them as "x,z,y" with no
        /// spaces; anything else is passed through untouched as a player name.
        /// </summary>
        internal static string NormalizeTeleportDestination(string destination)
        {
            if (string.IsNullOrWhiteSpace(destination)) return destination;

            var trimmed = destination.Trim();
            var tokens = trimmed.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || tokens.Length > 3) return trimmed;

            return tokens.All(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                ? string.Join(",", tokens)
                : trimmed;
        }

        /// <summary>True when the current status is one of the given states.</summary>
        public bool IsAnyStatus(params ServerStatus[] statuses)
            => Array.IndexOf(statuses, Status) >= 0;

        #endregion

        #region Automatic restart

        private int CountActivePlayers(IEnumerable<PlayerInfo> players)
        {
            // "Active" = currently online or mid-connect. Leaving/Offline players don't count,
            // so the empty-server timer starts as soon as the last real player drops. Only
            // players on THIS server count; other concurrent servers keep their own tallies.
            return players.Count(p =>
                (p.PlayerStatus == PlayerStatus.Online || p.PlayerStatus == PlayerStatus.Joining)
                && IsOwnPlayer(p));
        }

        /// <summary>
        /// True when the player record belongs to this server instance. Null server keys
        /// (legacy records or single-server mode) match anything.
        /// </summary>
        private bool IsOwnPlayer(PlayerInfo player)
        {
            return ServerKey == null
                || player.ServerKey == null
                || string.Equals(player.ServerKey, ServerKey, StringComparison.OrdinalIgnoreCase);
        }

        private void OnPlayerStatusChanged(object sender, PlayerInfo player)
        {
            // Another concurrent server's player flipping status must not disturb this
            // server's empty-restart / empty-update machinery.
            if (!IsOwnPlayer(player)) return;

            var activeCount = CountActivePlayers(PlayerDataRepository.Data);

            if (activeCount > 0)
            {
                // A player is connected (or just joined) -> cancel any pending empty-server work.
                CancelEmptyRestart();
                CancelEmptyUpdateCheck();
            }
            else if (LastActivePlayerCount > 0 && Status == ServerStatus.Running)
            {
                // The last active player just left.
                // Schedule the empty-server restart (only when that preference is enabled)...
                if (Options.EmptyServerRestart) ScheduleEmptyRestart();
                // ...and always start watching for mod updates to install while empty (item C).
                ScheduleEmptyUpdateCheck();
            }

            LastActivePlayerCount = activeCount;
        }

        private void ScheduleEmptyRestart()
        {
            CancelEmptyRestart();

            var delayMinutes = Math.Max(1, Options.EmptyServerRestartDelayMinutes);
            var cts = new CancellationTokenSource();
            EmptyRestartCts = cts;
            var token = cts.Token;

            ApplicationLogger.Information("Server is empty; scheduling restart in {minutes} minute(s).", delayMinutes);

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMinutes * 60 * 1000, token);
                }
                catch (OperationCanceledException)
                {
                    return; // A player rejoined, or the server was stopped/restarted.
                }

                if (token.IsCancellationRequested) return;
                if (Status != ServerStatus.Running) return;
                if (CountActivePlayers(PlayerDataRepository.Data) > 0) return;

                // Ask before the server goes down: a hold has to leave it running and wait for
                // the next empty window, not strand it offline.
                if (!await ConfirmAutomaticRestartAsync(LaunchReasons.Empty))
                {
                    ScheduleEmptyRestart();
                    return;
                }

                ApplicationLogger.Information("Server still empty; restarting now.");
                if (await TryStageAppUpdateAsync()) return; // app self-update staged; app is closing.
                Restart(reason: LaunchReasons.Empty);
            });
        }

        private void CancelEmptyRestart()
        {
            var cts = EmptyRestartCts;
            if (cts == null) return;

            EmptyRestartCts = null;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        /// <summary>
        /// Item C: while the server is empty, wait <see cref="EmptyUpdateDelayMinutes"/> and then
        /// check Thunderstore for pending mod updates. If any are found (and the server is still
        /// empty), restart to install them - no countdown, since nobody is online. Keeps
        /// re-checking on the same interval so newly published updates are caught while idle.
        /// Auto-update being disabled is handled by the hook returning 0, so this no-ops then.
        /// </summary>
        private void ScheduleEmptyUpdateCheck()
        {
            CancelEmptyUpdateCheck();

            // No UI hook wired -> the feature is inactive.
            if (GetPendingModUpdateCount == null) return;

            var cts = new CancellationTokenSource();
            EmptyUpdateCts = cts;
            var token = cts.Token;
            var delayMs = EmptyUpdateDelayMinutes * 60 * 1000;

            ApplicationLogger.Information(
                "Server is empty; will check for mod updates in {minutes} minute(s).", EmptyUpdateDelayMinutes);

            Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(delayMs, token);

                        if (token.IsCancellationRequested) return;
                        if (Status != ServerStatus.Running) return;
                        if (CountActivePlayers(PlayerDataRepository.Data) > 0) return;

                        int pending;
                        try { pending = await GetPendingModUpdateCount(); }
                        catch (Exception e)
                        {
                            ApplicationLogger.Error(e, "Empty-server update check failed; will retry.");
                            continue;
                        }

                        if (pending <= 0) continue; // nothing to do yet - keep watching while empty.

                        // Re-confirm the server is still empty before pulling the trigger.
                        if (CountActivePlayers(PlayerDataRepository.Data) > 0) return;
                        if (!CanRestart) return;

                        ApplicationLogger.Information(
                            "Server empty {minutes}+ min with {count} mod update(s) pending; restarting to auto-update.",
                            EmptyUpdateDelayMinutes, pending);

                        // App self-update takes priority: if a newer BakaLoader is staged, the app
                        // closes now and the relaunched newer app handles the mod updates instead.
                        if (await TryStageAppUpdateAsync()) return;

                        ApplyUpdatesOnRestart = true;
                        Restart(reason: LaunchReasons.Empty);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    // A player rejoined, or the server stopped/restarted.
                }
            });
        }

        private void CancelEmptyUpdateCheck()
        {
            var cts = EmptyUpdateCts;
            if (cts == null) return;

            EmptyUpdateCts = null;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        private void StartScheduledRestartTimer()
        {
            CancelScheduledRestart();

            if (!Options.ScheduledRestart) return;

            var intervalSeconds = Math.Max(1, Options.ScheduledRestartHours) * 3600;

            // Only announce at points that fit inside the interval, and start the timer early enough
            // that the countdown finishes (and the server restarts) right on the interval mark.
            var countdownPoints = Options.RconEnabled
                ? DefaultCountdownSeconds.Where(s => s < intervalSeconds).ToArray()
                : Array.Empty<int>();
            var leadSeconds = countdownPoints.Length > 0 ? countdownPoints.Max() : 0;
            var waitTime = TimeSpan.FromSeconds(intervalSeconds - leadSeconds);

            var cts = new CancellationTokenSource();
            ScheduledRestartCts = cts;
            var token = cts.Token;

            NextScheduledRestartUtc = DateTime.UtcNow + TimeSpan.FromSeconds(intervalSeconds);

            ApplicationLogger.Information("Scheduling automatic restart every {hours} hour(s).", Math.Max(1, Options.ScheduledRestartHours));

            Task.Run(async () =>
            {
                try
                {
                    if (waitTime > TimeSpan.Zero) await Task.Delay(waitTime, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (token.IsCancellationRequested) return;
                if (!CanRestart) return;

                // Ask before a word is broadcast and before anything is torn down. A cycle that
                // will not be allowed to relaunch is skipped, the server stays up, and the same
                // schedule comes round again.
                if (!await ConfirmAutomaticRestartAsync(LaunchReasons.Scheduled))
                {
                    StartScheduledRestartTimer();
                    return;
                }

                // applyModUpdates: true -> a scheduled restart also installs any pending mod
                // updates (gated by the auto-update preference inside the hook itself).
                await RestartWithCountdown(
                    countdownPoints.Length > 0 ? countdownPoints : null,
                    applyModUpdates: true,
                    reason: LaunchReasons.Scheduled);
            });
        }

        private void CancelScheduledRestart()
        {
            NextScheduledRestartUtc = null;

            var cts = ScheduledRestartCts;
            if (cts == null) return;

            ScheduledRestartCts = null;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        #endregion

        #region Process output plumbing

        private void OnProcessOutput(object sender, DataReceivedEventArgs e)
            => ServerLogger.Information(e.Data);

        private void OnProcessError(object sender, DataReceivedEventArgs e)
            => ServerLogger.Error(e.Data);

        /// <summary>
        /// Runs every server stdout line through the rule table. Handler exceptions are
        /// contained per rule so one bad line can never stall the log pipeline.
        /// </summary>
        private void OnServerLogLine(string line)
        {
            foreach (var rule in LogLineRules)
            {
                var match = rule.Pattern.Match(line);
                if (!match.Success) continue;
                try
                {
                    rule.Handle(match);
                }
                catch (Exception e)
                {
                    ApplicationLogger.Error(e, "Error parsing server log: {message}", line);
                }
            }
        }

        #endregion

        #region Log line reactions

        private void HandleServerReady(Match match)
        {
            // A late Stop request can land while startup is still finishing; in that
            // case the session is already doomed, so don't bounce Stopping -> Running.
            // The process will still exit once it has fully come up.
            if (Status != ServerStatus.Stopping)
            {
                Status = ServerStatus.Running;
            }
        }

        private void HandleSteamConnecting(Match match)
        {
            var steamId = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(steamId)) return;

            PlayerDataRepository.SetPlayerJoining(new()
            {
                Platform = PlayerPlatforms.Steam,
                PlayerId = steamId,
            }, ServerKey);
        }

        private void HandleCrossplayConnecting(Match match)
        {
            if (!PlayerPlatforms.TryGetValidPlatform(match.Groups[1].Value, out var platform)) return;

            var playerId = match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(playerId)) return;

            PlayerDataRepository.SetPlayerJoining(new()
            {
                Platform = platform,
                PlayerId = playerId,
            }, ServerKey);
        }

        private void HandleCharacterSpawned(Match match)
        {
            var characterName = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(characterName)) return;

            // Group 2 is the session-scoped ZDOID; group 3 trails it and has no known use.
            // A ZDOID of "0:0" means the character just DIED (the game clears their id),
            // not that they spawned - surface that as a death instead.
            if (match.Groups[2].Value == "0" && match.Groups[3].Value == "0")
            {
                PlayerDied?.Invoke(this, characterName);
            }

            PlayerDataRepository.SetPlayerOnline(characterName, match.Groups[2].Value, ServerKey);
        }

        private void HandleWrongPassword(Match match)
        {
            var id = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(id)) return;

            PlayerDataRepository.SetPlayerLeaving(IdOrZdoQuery(id), ServerKey);
        }

        private void HandleSocketClosed(Match match)
        {
            var id = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(id)) return;

            PlayerDataRepository.SetPlayerOffline(IdOrZdoQuery(id), ServerKey);
        }

        private void HandleCrossplayDisconnect(Match match)
        {
            if (!PlayerPlatforms.TryGetValidPlatform(match.Groups[1].Value, out var platform)) return;

            var playerId = match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(playerId)) return;

            PlayerDataRepository.SetPlayerOffline(new()
            {
                Platform = platform,
                PlayerId = playerId,
            }, ServerKey);
        }

        private void HandleGotPlayerId(Match match)
        {
            var characterName = match.Groups[1].Value;
            var numericId = match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(numericId)) return;

            PlayerDataRepository.SetPlayerNumericId(characterName.Trim(), numericId, ServerKey);
        }

        private void HandleWorldSaved(Match match)
        {
            WorldSaved?.Invoke(this, ParseDurationMs(match.Groups[1].Value));
        }

        private void HandleWorldSaveFailed(Match match)
        {
            var durationMs = ParseDurationMs(match.Groups[1].Value);

            ApplicationLogger.Warning(
                "The server reported a FAILED world save after {durationMs}ms. Everything played since the last good save is still only in memory. Check the server log, the free space on the save drive, and whether anything else is holding the world files open.",
                durationMs);

            WorldSaveFailed?.Invoke(this, durationMs);
        }

        private void HandleLegacyWorldLoaded(Match match)
        {
            ApplicationLogger.Warning(
                "This world is still in the pre-1.0 save format. Valheim rewrites it in the new format on the next save and there is no way back, so take a copy of the world files now if you may want to run it on an older server.");

            LegacyWorldLoaded?.Invoke(this, EventArgs.Empty);
        }

        private void HandleVersionBanner(Match match)
        {
            GameVersion = match.Groups[1].Value;
            NetworkVersion = match.Groups[2].Value;

            ApplicationLogger.Information(
                "Server is running Valheim {gameVersion} (network version {networkVersion})",
                GameVersion, NetworkVersion);

            // The banner is the only place the real version comes from, so it is what gets
            // stored against the profile as "what this profile last ran".
            RecordLaunchedIdentity();

            VersionDetected?.Invoke(this, GameVersion);
        }

        /// <summary>
        /// Reads a save duration out of a log line. Valheim 1.0 formats the total
        /// with thousands grouping ("1,234ms"), while the older line and the FAILED
        /// line print a raw decimal ("198.5ms"). The invariant reading covers both
        /// on an English server; a server running under another culture falls back
        /// to that culture's own formatting.
        /// </summary>
        private static decimal ParseDurationMs(string value)
        {
            const NumberStyles Styles = NumberStyles.AllowThousands
                | NumberStyles.AllowDecimalPoint
                | NumberStyles.AllowLeadingWhite
                | NumberStyles.AllowTrailingWhite;

            if (decimal.TryParse(value, Styles, CultureInfo.InvariantCulture, out var durationMs)) return durationMs;

            return decimal.TryParse(value, Styles, CultureInfo.CurrentCulture, out durationMs) ? durationMs : 0m;
        }

        /// <summary>
        /// The max-players plugin loaded but could not raise the cap on this build of the game.
        /// The line is already in the log as plain output, buried in BepInEx chatter, while the
        /// World hall keeps showing the number the host saved as though it were in force. This
        /// lifts it to a warning on the same path every other companion-plugin trouble takes, so
        /// it lands in the server log the interface is already showing.
        /// </summary>
        private void HandleMaxPlayersRefused(Match match)
        {
            // The message goes in as an argument, never as the template: Serilog would read any
            // brace in it as a property name. Same reason the plugin failure lines do it.
            ServerLogger.Warning("{message}",
                "Max Players could not be raised on this build of the game, so this server keeps the vanilla limit of 10 players. The number saved in the World hall is not in force for this session.");
        }

        private void HandleJoinCode(Match match)
        {
            var joinCode = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(joinCode)) return;

            InviteCodeReady?.Invoke(this, joinCode);
        }

        /// <summary>
        /// Disconnect log lines carry a bare number that is a platform id for Steam
        /// peers but a ZDOID for crossplay peers - so match on either.
        /// </summary>
        private static PlayerDataQuery IdOrZdoQuery(string id) => new()
        {
            PlayerId = id,
            Or = new() { ZdoId = id },
        };

        #endregion

        #region Disposal

        public void Dispose()
        {
            // Stop listening before tearing down, so timer callbacks can't re-arm.
            PlayerDataRepository.PlayerStatusChanged -= OnPlayerStatusChanged;

            CancelScheduledRestart();
            CancelEmptyUpdateCheck();
            CancelEmptyRestart();
            CancelLaunchRetry();
            StopPositionPolling();

            Stop();
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Helper methods

        /// <summary>
        /// Composes the dedicated-server command line from the given options.
        /// The flag names and ordering match what valheim_server.exe expects.
        /// The logger, when supplied, is told about any extra argument that was
        /// refused.
        /// </summary>
        private static string GenerateArgs(IValheimServerOptions options, IApplicationLogger logger = null)
            => string.Join(" ", BuildArgParts(options, logger));

        /// <summary>
        /// The command line one flag at a time, for anything that needs to compare two launches
        /// rather than run one. Each entry is a whole flag with its value ("-port 2456",
        /// "-modifier combat hard"), which is what makes a comparison able to ignore the order
        /// two dictionaries happened to hand their modifiers over in without also ignoring which
        /// value belongs to which flag.
        /// <para>
        /// Pure: it reads the options and the paths they name, and writes nothing. It can throw
        /// exactly where a start would (a save folder that is not there), so callers that are
        /// not starting anything have to be ready for that.
        /// </para>
        /// </summary>
        internal static IReadOnlyList<string> DescribeLaunchParts(IValheimServerOptions options)
            => BuildArgParts(options, null);

        /// <inheritdoc cref="GenerateArgs(IValheimServerOptions, IApplicationLogger)"/>
        private static List<string> BuildArgParts(IValheimServerOptions options, IApplicationLogger logger)
        {
            // Trim trailing directory separators. A path ending in '\' would otherwise
            // produce -savedir "...\" where the backslash escapes the closing quote on
            // Windows, swallowing the rest of the command line into the savedir value
            // (causing "Illegal characters in path" and a failed world load).
            var saveDir = options.GetValidatedSaveDataFolder().FullName
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

            var parts = new List<string>
            {
                "-nographics",
                "-batchmode",
                @$"-name ""{options.Name}""",
                $"-port {options.Port}",
                @$"-world ""{options.WorldName}""",
                $"-public {(options.Public ? 1 : 0)}",
                @$"-savedir ""{saveDir}""",
                $"-saveinterval {options.SaveInterval}",
                $"-backups {options.Backups}",
                $"-backupshort {options.BackupShort}",
                $"-backuplong {options.BackupLong}",
            };

            if (!string.IsNullOrWhiteSpace(options.Password)) parts.Add(@$"-password ""{options.Password}""");
            if (options.Crossplay) parts.Add("-crossplay");

            // BakaLoader owns a world's difficulty, so every start begins by clearing it.
            // The game keeps those settings as keys in the world's .fwl and never clears a
            // category on its own: "-modifier <category> default" writes no keys at all, so
            // a key left over from an earlier choice (deathdeleteunequipped from a world that
            // used to be Hard, say) would quietly stay in force, and value keys such as
            // "resourcerate 150" pile up beside their replacements instead of being replaced.
            // -resetmodifiers empties that starting-key list, and the whole intended set is
            // written back by the flags below. It reaches only the server-option keys; world
            // progression (the defeated_* boss keys) is out of its range.
            //
            // It must come FIRST of the world flags. The game applies -resetmodifiers,
            // -preset, -modifier and -setkey in one pass over the command line in the order
            // they appear, so a reset sitting after them would wipe exactly what they set and
            // the world would launch vanilla. Emitted even when nothing is configured, which
            // is the case that needs it most: an all-Normal profile is a request for a world
            // with no modifiers on it, and only the reset can deliver that.
            parts.Add("-resetmodifiers");

            // A preset and individual modifiers are never emitted together: the game takes
            // whichever comes last on the command line, so sending both would make the order
            // decide the difficulty.
            if (!string.IsNullOrWhiteSpace(options.WorldPreset))
            {
                parts.Add($"-preset {options.WorldPreset}");
            }
            else if (options.WorldModifiers != null)
            {
                parts.AddRange(options.WorldModifiers
                    .Select(m => $"-modifier {m.Key} {m.Value}"));
            }

            if (options.WorldKeys != null) parts.AddRange(options.WorldKeys.Select(k => $"-setkey {k}"));

            if (!string.IsNullOrWhiteSpace(options.AdditionalArgs))
            {
                var extraArgs = SanitizeAdditionalArgs(options.AdditionalArgs, out var removed);

                foreach (var token in removed)
                {
                    BlockedAdditionalArgs.TryGetValue(token, out var reason);
                    logger?.Warning(
                        "Left {token} out of the server command line: {reason}", token, reason);
                }

                if (!string.IsNullOrWhiteSpace(extraArgs)) parts.Add(extraArgs);
            }

            return parts;
        }

        /// <summary>
        /// Launch flags the host must not be able to hand to the dedicated server
        /// through the extra-arguments box, and the reason each one is refused.
        /// </summary>
        private static readonly Dictionary<string, string> BlockedAdditionalArgs =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["-demomode"] = "it turns off all world saving, so every minute played would be thrown away when the server stops",
                ["-joinserverwithcharacter"] = "it makes the game try to join a server as a player instead of hosting one, which a dedicated server cannot do",
                // The game reads -resetmodifiers, -preset, -modifier and -setkey in one pass
                // over the command line in the order they appear, and the extra arguments are
                // appended last. A second reset down there would land after the flags above
                // and clear the starting keys they had just written, so the world would come
                // up with no modifiers on it at all.
                ["-resetmodifiers"] = "BakaLoader already resets and writes back the world modifiers at every start, so a second reset would wipe the settings it had just applied",
            };

        /// <summary>
        /// Drops the launch flags listed in <see cref="BlockedAdditionalArgs"/> from a
        /// host-typed extra-arguments string and reports what was taken out. Matching is
        /// whole-token and case-insensitive, so "-demomodex" or a value that merely
        /// contains the word survives untouched. Quoted values (a path with spaces, say)
        /// are kept in one piece.
        /// </summary>
        public static string SanitizeAdditionalArgs(string additionalArgs)
            => SanitizeAdditionalArgs(additionalArgs, out _);

        /// <inheritdoc cref="SanitizeAdditionalArgs(string)"/>
        public static string SanitizeAdditionalArgs(string additionalArgs, out IReadOnlyList<string> removed)
        {
            removed = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(additionalArgs)) return additionalArgs;

            var kept = new List<string>();
            var dropped = new List<string>();

            foreach (var token in SplitArgTokens(additionalArgs))
            {
                if (BlockedAdditionalArgs.ContainsKey(token))
                {
                    dropped.Add(token);
                    continue;
                }

                kept.Add(token);
            }

            if (dropped.Count == 0) return additionalArgs;

            removed = dropped;
            return string.Join(" ", kept);
        }

        /// <summary>
        /// Splits a command-line fragment into tokens on whitespace, treating a
        /// double-quoted run as one token (quotes included, so the token can be
        /// re-emitted as typed).
        /// </summary>
        private static IEnumerable<string> SplitArgTokens(string args)
        {
            var token = new StringBuilder();
            var inQuotes = false;

            foreach (var c in args)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    token.Append(c);
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    if (token.Length > 0)
                    {
                        yield return token.ToString();
                        token.Clear();
                    }
                    continue;
                }

                token.Append(c);
            }

            if (token.Length > 0) yield return token.ToString();
        }

        /// <summary>Masks the server password so the command line is safe to log.</summary>
        private static string RedactPassword(string processArgs)
            => Regex.Replace(processArgs, @"-password ""(.*?)""", @"-password ""*****""");

        /// <summary>
        /// Builds the RCON command that broadcasts a center-screen message to all players.
        /// Uses the "broadcast" command provided by the Server devcommands BepInEx mod.
        /// </summary>
        private static string BuildBroadcast(string message)
        {
            return $"broadcast center {message}";
        }

        // ---------------------------------------------------------------------------------------
        // Player-targeting RCON command builders. These wrap the commands exposed by JereKuusela's
        // "Server devcommands" BepInEx mod (plus vanilla kick). The EXACT parameter forms below are
        // VERIFY-LIVE: they MUST be confirmed against the running server with an RCON echo test
        // before the UI is trusted. They are centralized here so adjusting them post-probe is a
        // one-line change with no UI churn.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Lists every online player with their world coordinates as
        /// "{name}/{steamId}/{charId} (x, z, y)". Verified live as the RCON-safe position source
        /// (the per-player "pos" command can't run without a calling-player character context).
        /// </summary>
        private static string BuildPlayerList()
        {
            return "playerlist";
        }

        /// <summary>
        /// Spawn objects at an ABSOLUTE world coordinate using the BakaLoaderSpawnHelper plugin:
        /// "baka_spawn &lt;prefab&gt; &lt;x,z,y&gt; [amount] [level]".
        ///
        /// HISTORY: previously used WEC's "spawn_object" but it crashes dedicated servers because
        /// RCON callbacks run on a ThreadPool thread and Object.Instantiate() called from a non-main
        /// thread triggers "Graphics device is null" (native crash). The vanilla "spawn" command also
        /// fails over RCON (Player.m_localPlayer is null on dedicated servers).
        ///
        /// BakaLoaderSpawnHelper (BepInEx plugin) registers the "baka_spawn" console command, which
        /// queues the instantiation to the Unity main thread via Update(), avoiding both crashes.
        /// The coords are passed as x,z,y (Valheim display order from playerlist), and the plugin
        /// converts to Vector3(x, y, z) internally.
        ///
        /// The 4th argument carries two meanings, the same way the game's own spawn command does:
        /// for a creature it is the star LEVEL and it is 0 based (0 = base, 1 = one star, 2 = two
        /// stars), and for an item it is the QUALITY and it is 1 based (1 = base, 3 = a quality 3
        /// tool). 0 means leave it as it is, and that is what anything with neither gets. It used
        /// to be sent only for creatures, so every item quality the picker offered was thrown away
        /// here and never reached the server.
        ///
        /// REQUIRES BakaLoaderSpawnHelper.dll (or Commander, which answers the same command) in
        /// the server's BepInEx/plugins.
        /// </summary>
        private static string BuildSpawn(ItemCatalogEntry entry, int amount, int levelOrQuality, string coords)
        {
            var count = Math.Max(1, amount);
            var level = 0;
            if (entry.HasLevel || entry.HasQuality)
                level = Math.Max(0, levelOrQuality);
            return $"baka_spawn {entry.PrefabName} {coords} {count} {level}";
        }

        /// <summary>VERIFY-LIVE: vanilla "kick [name/ip/userID]".</summary>
        private static string BuildKick(string target)
        {
            return $"kick {target}";
        }

        /// <summary>
        /// Deal damage to a target player. JereKuusela's Server devcommands exposes this as
        /// "dmg [players] [amount]" (there is no "damage" command); negative amounts heal.
        /// </summary>
        private static string BuildDamage(string target, int amount)
        {
            return $"dmg {target} {amount.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>
        /// Fully heal a target player. JereKuusela's mod has no "heal &lt;player&gt;" command;
        /// healing is done with a large NEGATIVE "dmg" amount.
        /// </summary>
        private static string BuildHeal(string target)
        {
            return $"dmg {target} -1000000";
        }

        /// <summary>VERIFY-LIVE: teleport a player to another player or an "x,z,y" coordinate.</summary>
        private static string BuildTp(string playerName, string destination)
        {
            return $"tp {playerName} {destination}";
        }

        /// <summary>
        /// Pulls the coordinates for ONE named player out of a "playerlist" response and returns
        /// them as a spawn-ready "x,z,y" string, or null if that player isn't in the list.
        ///
        /// "playerlist" prints one entry per online player as "{name}/{steamId}/{charId} (x, z, y)"
        /// (the RCON channel also prepends a log timestamp / "Console:" and echoes each line twice).
        /// We anchor the match on "{name}/{steamId}/{charId}" immediately before the triple so that,
        /// with several players online, we grab the requested player's position rather than the first
        /// (or the log clock). The triple is already in the x,z,y order the spawn_object "from=" form
        /// expects, so it passes straight through. Returns null if the player has no entry so the caller aborts
        /// instead of spawning at a bogus location.
        /// </summary>
        private static string ParsePositionForPlayer(string response, string playerName)
        {
            if (string.IsNullOrWhiteSpace(response) || string.IsNullOrWhiteSpace(playerName)) return null;

            var num = @"-?\d+(?:\.\d+)?";
            var name = Regex.Escape(playerName.Trim());
            // {name}/{steamId}/{charId} (x, z, y) - the id fields are non-space, non-'(' runs.
            var pattern = $@"{name}/[^\s/]+/[^\s(]+\s*\(\s*({num})\s*,\s*({num})\s*,\s*({num})\s*\)";
            var match = Regex.Match(response, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) return null;

            return $"{match.Groups[1].Value},{match.Groups[2].Value},{match.Groups[3].Value}";
        }

        /// <summary>
        /// Resolves the BepInEx plugins folder ("&lt;serverDir&gt;/BepInEx/plugins") from the server
        /// .exe path, mirroring MainWindow.GetPluginsDirectoryFromOptions.
        /// </summary>
        private static string GetPluginsDirectory(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            var dir = Path.GetDirectoryName(exePath);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "BepInEx", "plugins");
        }

        /// <summary>
        /// Resolves the BepInEx root folder ("&lt;serverDir&gt;/BepInEx") from the configured server
        /// .exe path, used to locate the indexer's items.json. Returns null if the path isn't set.
        /// </summary>
        public string GetBepInExDirectory()
        {
            try
            {
                var exePath = Options?.ServerExePath;
                if (string.IsNullOrEmpty(exePath)) return null;
                var dir = Path.GetDirectoryName(exePath);
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "BepInEx");
            }
            catch
            {
                return null;
            }
        }

        private static string FormatTime(int seconds)
        {
            if (seconds >= 3600)
            {
                var hours = seconds / 3600;
                return hours == 1 ? "1 hour" : $"{hours} hours";
            }

            if (seconds >= 60)
            {
                var minutes = seconds / 60;
                return minutes == 1 ? "1 minute" : $"{minutes} minutes";
            }

            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        #endregion
    }
}
