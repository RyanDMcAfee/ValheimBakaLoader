using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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

        /// <summary>Fires once per real state transition (never repeats the same state).</summary>
        public event EventHandler<ServerStatus> StatusChanged;

        /// <summary>Fires when the game reports a world save, with the save duration in ms.</summary>
        public event EventHandler<decimal> WorldSaved;

        /// <summary>Fires when a crossplay session publishes its join code.</summary>
        public event EventHandler<string> InviteCodeReady;

        /// <summary>Fires when the process exits nonzero outside a deliberate stop.</summary>
        public event EventHandler ServerCrashed;

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
        /// Optional hook (set by the UI) that checks GitHub for a newer BakaLoader release and,
        /// when auto-update is enabled and an update is found, stages a headless watchdog and
        /// begins closing the app. Returns <c>true</c> when an app update is taking over (the
        /// server restart should then be abandoned, because the app - and its child server -
        /// is going down and the watchdog will relaunch everything). Invoked at the very start
        /// of a restart, before any countdown or server teardown.
        /// </summary>
        public Func<Task<bool>> CheckForAppUpdateOnRestart { get; set; }

        // True when the next restart should apply pending mod updates in the stop->start gap.
        private bool ApplyUpdatesOnRestart;

        public bool CanStart => ProcessKey == null && Status == ServerStatus.Stopped;

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
            ICommanderInstaller commanderInstaller)
        {
            ApplicationLogger = appLogger;
            ProcessProvider = processProvider;
            RconClient = rconClient;
            PlayerDataRepository = playerDataRepository;
            ItemIndexerInstaller = itemIndexerInstaller;
            SpawnHelperInstaller = spawnHelperInstaller;
            KillAllInstaller = killAllInstaller;
            CommanderInstaller = commanderInstaller;

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

                // Periodic world save, with the save duration captured in ms.
                new(Rx(@"World saved \(\s*?([[\d\.]+?)\s*?ms\s*?\)\s*?$"), HandleWorldSaved),

                // Crossplay session published its join code.
                new(Rx(@"Session "".*?"" with join code (.*?) "), HandleJoinCode),

                // A client began connecting: Steam carries a SteamID, crossplay a
                // "{platform}_{id}" pair on the PlayFab socket line.
                new(Rx(@"Got connection SteamID (\d+?)\D*?$"), HandleSteamConnecting),
                new(Rx(@"PlayFab socket with remote ID .*? received local Platform ID (\w+?)_(\d+?)$"), HandleCrossplayConnecting),

                // The character actually spawned in-world. ZDOIDs may be negative,
                // hence [\d-] in the id capture.
                new(Rx(@"Got character ZDOID from (.+?) : ([\d-]+?)\D*?:(\d+?)\D*?$"), HandleCharacterSpawned),

                // Rejected connection attempt.
                new(Rx(@"Peer (\d+?) has wrong password"), HandleWrongPassword),

                // Departures. "Closing socket" is the most reliable terminator the game
                // prints; the abandoned-zdo line covers crossplay clients, and the
                // client-disconnect line covers ValheimPlus version mismatches.
                new(Rx(@"Closing socket (\d+?)\D*?$"), HandleSocketClosed),
                new(Rx(@"Destroying abandoned non persistent zdo ([\d-]+?):.*$"), HandleSocketClosed),
                new(Rx(@"Disconnect: The client \((\w+?)_(\d+?)\)"), HandleCrossplayDisconnect),
            };
        }

        private void OnStatusTransition(object sender, ServerStatus status)
        {
            switch (status)
            {
                case ServerStatus.Running:
                    // A fresh session begins with nobody online, and the scheduled
                    // restart clock starts counting from this moment.
                    LastActivePlayerCount = 0;
                    CancelEmptyRestart();
                    StartScheduledRestartTimer();
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
        private async Task ResumeAfterStopAsync()
        {
            var delayMs = IsCrashRestart ? Options.AutoRestartDelay * 1000 : 500;
            await Task.Delay(delayMs);

            if (!IsRestarting) return;

            if (ApplyUpdatesOnRestart && ApplyModUpdates != null)
            {
                try
                {
                    ApplicationLogger.Information("Applying pending mod updates before restart...");
                    await ApplyModUpdates();
                }
                catch (Exception e)
                {
                    ApplicationLogger.Error(e, "Error applying mod updates during restart; starting with the existing mods.");
                }
            }
            ApplyUpdatesOnRestart = false;

            if (!IsRestarting) return;

            IsRestarting = false;
            IsCrashRestart = false;
            Start(Options);
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

            ApplicationLogger.Information(
                "Adopting existing server process (PID {pid})", existingProcess.Id);

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
        /// Launches valheim_server.exe as a tracked background process using the given
        /// profile: prepares companion plugins, wires up log capture and exit/crash
        /// detection, boosts process priority, and moves the status to Starting.
        /// </summary>
        public void Start(IValheimServerOptions options)
        {
            if (!CanStart) return;
            ApplicationLogger.Information("Starting server: {name}", options.Name);

            var exePath = options.GetValidatedServerExe().FullName;
            var launchArgs = GenerateArgs(options);

            PrepareCompanionPlugins(exePath, options);
            ApplicationLogger.Information(@"Server run command: ""{exePath}"" {processArgs}",
                exePath, RedactPassword(launchArgs));

            // The stdout pipeline must exist before IO starts, or early lines are lost.
            ServerLogger = new ValheimServerLogger(options);
            ServerLogger.LogReceived += OnServerLogLine;

            // Raw server output also flows to the UI's log view when it asked for it.
            if (options.LogMessageHandler != null) ServerLogger.LogReceived += options.LogMessageHandler;

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
            Options = options;
            Status = ServerStatus.Starting; // last: fires StatusChanged
        }

        /// <summary>
        /// Ensures the companion BepInEx plugins are installed and current while the
        /// server is still down - BepInEx only picks up plugin files at launch. Each
        /// installer failure is logged and skipped so one bad plugin can't block a start.
        /// </summary>
        private void PrepareCompanionPlugins(string exePath, IValheimServerOptions options)
        {
            var pluginsDir = GetPluginsDirectory(exePath);

            void Install(string label, Action install)
            {
                try
                {
                    install();
                }
                catch (Exception ex)
                {
                    ApplicationLogger.Warning("Could not prepare the " + label + " plugin: {message}", ex.Message);
                }
            }

            Install("item indexer", () => ItemIndexerInstaller?.EnsureInstalled(pluginsDir));
            Install("spawn helper", () => SpawnHelperInstaller?.EnsureInstalled(pluginsDir));
            Install("kill-all", () => KillAllInstaller?.EnsureInstalled(pluginsDir));
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
                    ApplicationLogger.Information("Auto-restarting server in {delay} seconds...", Options.AutoRestartDelay);
                    IsCrashRestart = true;
                    IsRestarting = true;
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
        /// current one is reused.
        /// </summary>
        public void Restart(IValheimServerOptions options = null)
        {
            if (!CanRestart) return;
            if (options != null) Options = options;
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

        public async Task RestartWithCountdown(int[] countdownSeconds = null, bool applyModUpdates = false)
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
                Restart();
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
                Restart();
                return;
            }

            var cts = new CancellationTokenSource();
            CountdownCts = cts;
            var token = cts.Token;
            var shouldRestart = false;

            try
            {
                var connected = await RconClient.ConnectAsync("127.0.0.1", Options.RconPort, Options.RconPassword);
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
                    await EnsureRconConnectedAsync();
                    await RconClient.SendCommandAsync(BuildBroadcast($"Server restarting in {FormatTime(remaining)}!{updateNote}"));
                    CountdownTick?.Invoke(this, $"Restart in {FormatTime(remaining)}");

                    var next = (i + 1 < points.Length) ? points[i + 1] : 0;
                    var waitTime = restartAtUtc.AddSeconds(-next) - DateTime.UtcNow;
                    if (waitTime > TimeSpan.Zero) await Task.Delay(waitTime, token);
                }

                await EnsureRconConnectedAsync();
                await RconClient.SendCommandAsync(BuildBroadcast($"Server restarting NOW!{updateNote}"));
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
                    ApplicationLogger.Information("Restart countdown bypassed - restarting now.");
                    try
                    {
                        await EnsureRconConnectedAsync();
                        await RconClient.SendCommandAsync(BuildBroadcast($"Server restarting NOW!{updateNote}"));
                    }
                    catch { }
                    shouldRestart = true;
                }
                else
                {
                    ApplicationLogger.Information("Scheduled restart cancelled.");
                    try
                    {
                        await EnsureRconConnectedAsync();
                        await RconClient.SendCommandAsync(BuildBroadcast("Server restart cancelled."));
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
                RconClient.Disconnect();
                if (CountdownCts == cts) CountdownCts = null;
                cts.Dispose();
                CountdownTick?.Invoke(this, null);
            }

            if (shouldRestart)
            {
                ApplyUpdatesOnRestart = modUpdateCount > 0;
                Restart();
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
            }
        }

        /// <summary>
        /// Sends an arbitrary RCON command to the running server and returns the raw response
        /// text (empty string on success-with-no-output, null on failure / RCON off / not running).
        /// Factored exactly like <see cref="BroadcastNow"/>: opens and closes the connection per call.
        /// All player-targeting actions below route through this single method.
        /// </summary>
        public async Task<string> SendRconCommandAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            if (!Options.RconEnabled)
            {
                ApplicationLogger.Warning("Cannot send RCON command: RCON is not enabled for this server.");
                return null;
            }

            if (Status != ServerStatus.Running)
            {
                ApplicationLogger.Warning("Cannot send RCON command: the server is not running.");
                return null;
            }

            try
            {
                var connected = await RconClient.ConnectAsync("127.0.0.1", Options.RconPort, Options.RconPassword);
                if (!connected)
                {
                    ApplicationLogger.Warning("Cannot send RCON command: RCON connection failed.");
                    return null;
                }

                var response = await RconClient.SendCommandAsync(command);
                ApplicationLogger.Information("RCON command sent: {command}", command);
                return response ?? string.Empty;
            }
            catch (Exception e)
            {
                ApplicationLogger.Error(e, "Error sending RCON command");
                return null;
            }
            finally
            {
                RconClient.Disconnect();
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
        /// Spawns <paramref name="amount"/> of the given catalog entry at <paramref name="playerName"/>'s
        /// current position. First resolves the player's coordinates (pos), then issues a coordinate
        /// spawn so the items/creatures appear around the player. Returns false if the position could
        /// not be resolved or the command failed. The exact spawn syntax is VERIFY-LIVE.
        /// </summary>
        public async Task<bool> SpawnAtPlayerAsync(string playerName, ItemCatalogEntry entry, int amount, int levelOrQuality)
        {
            if (entry == null) return false;

            var coords = await GetPlayerPositionAsync(playerName);
            if (string.IsNullOrEmpty(coords))
            {
                ApplicationLogger.Warning("Could not resolve position for player {player}; spawn aborted.", playerName);
                return false;
            }

            var response = await SendRconCommandAsync(BuildSpawn(entry, amount, levelOrQuality, coords));
            return response != null;
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

        private static int CountActivePlayers(IEnumerable<PlayerInfo> players)
        {
            // "Active" = currently online or mid-connect. Leaving/Offline players don't count,
            // so the empty-server timer starts as soon as the last real player drops.
            return players.Count(p => p.PlayerStatus == PlayerStatus.Online || p.PlayerStatus == PlayerStatus.Joining);
        }

        private void OnPlayerStatusChanged(object sender, PlayerInfo player)
        {
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

                ApplicationLogger.Information("Server still empty; restarting now.");
                if (await TryStageAppUpdateAsync()) return; // app self-update staged; app is closing.
                Restart();
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
                        Restart();
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

                // applyModUpdates: true -> a scheduled restart also installs any pending mod
                // updates (gated by the auto-update preference inside the hook itself).
                await RestartWithCountdown(countdownPoints.Length > 0 ? countdownPoints : null, applyModUpdates: true);
            });
        }

        private void CancelScheduledRestart()
        {
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
            });
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
            });
        }

        private void HandleCharacterSpawned(Match match)
        {
            var characterName = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(characterName)) return;

            // Group 2 is the session-scoped ZDOID; group 3 trails it and has no known use.
            PlayerDataRepository.SetPlayerOnline(characterName, match.Groups[2].Value);
        }

        private void HandleWrongPassword(Match match)
        {
            var id = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(id)) return;

            PlayerDataRepository.SetPlayerLeaving(IdOrZdoQuery(id));
        }

        private void HandleSocketClosed(Match match)
        {
            var id = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(id)) return;

            PlayerDataRepository.SetPlayerOffline(IdOrZdoQuery(id));
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
            });
        }

        private void HandleWorldSaved(Match match)
        {
            decimal.TryParse(match.Groups[1].Value, out var durationMs);
            WorldSaved?.Invoke(this, durationMs);
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

            Stop();
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Helper methods

        /// <summary>
        /// Composes the dedicated-server command line from the given options.
        /// The flag names and ordering match what valheim_server.exe expects.
        /// </summary>
        private static string GenerateArgs(IValheimServerOptions options)
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

            // A preset overrides individual modifiers; the game ignores -modifier
            // flags when -preset is present, so don't emit both.
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
            if (!string.IsNullOrWhiteSpace(options.AdditionalArgs)) parts.Add(options.AdditionalArgs);

            return string.Join(" ", parts);
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
        /// converts to Vector3(x, y, z) internally. Level is 0-based (0=base, 1=1star, 2=2star).
        /// REQUIRES BakaLoaderSpawnHelper.dll in server BepInEx/plugins.
        /// </summary>
        private static string BuildSpawn(ItemCatalogEntry entry, int amount, int levelOrQuality, string coords)
        {
            var count = Math.Max(1, amount);
            var level = 0;
            if (entry.HasLevel)
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
