using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
// The Atlas engine has its own WorldGen (the seed->terrain port), which collides
// with ValheimBakaLoader.Tools.WorldGen (the -modifier vocabulary) used below.
using AtlasEngine = ValheimBakaLoader.Tools.Atlas;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// The service bridge for the Blend UI - registers every RPC method the JS side can
    /// call and forwards service/server events as pushes. RPC handlers run on the UI
    /// thread (WebView2 raises WebMessageReceived there), so WinForms-affine services are
    /// safe to touch directly. Mirrors MainWindow behavior per BLEND-PARITY-SPEC.md.
    /// </summary>
    public partial class BlendWindow
    {
        private IServiceProvider ServiceProvider;
        private IUserPreferencesProvider UserPrefsProvider;
        private IServerPreferencesProvider ServerPrefsProvider;
        private IWorldPreferencesProvider WorldPrefsProvider;
        private IPlayerDataRepository PlayerDataProvider;
        private IIpAddressProvider IpAddressProvider;
        private IModScanner ModScanner;
        private IThunderstoreClient ThunderstoreClient;
        private IModUpdateService ModUpdateService;
        private IModRemovalService ModRemovalService;
        private IRequiredModChecker RequiredModChecker;
        private ItemCatalog ItemCatalog;
        private PlayerListService PlayerListService;
        private IAppUpdateService AppUpdateService;
        private IHeartbeatService Heartbeat;
        private IInstallIsolationService InstallIsolation;
        private IMaxPlayersInstaller MaxPlayersInstaller;
        private IDiscordStatusService DiscordStatus;
        private IDiscordWebhookService DiscordWebhooks;
        private IAnalyticsService Analytics;
        private IApplicationLogger AppLogger;

        // Last status the Skald recorded per player key, so the analytics journal only
        // gets real join/leave transitions (PlayerStatusChanged also fires on plain
        // name-resolution updates with no status change).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PlayerStatus> AnalyticsPlayerStatus =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The profile whose preferences were last loaded/saved through the bridge.</summary>
        private string CurrentProfile;

        // Multi-server registry: one live ValheimServer per profile name, all sharing the
        // singleton services (player repo, mod tooling). Creation is lock-guarded rather
        // than GetOrAdd-with-factory: a double-invoked factory would leak a stray
        // ValheimServer whose ctor subscribes to the shared player repository.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ServerSession> Sessions =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object SessionsLock = new();

        /// <summary>The profile the UI is currently showing (never null).</summary>
        private string ActiveProfileName =>
            !string.IsNullOrWhiteSpace(CurrentProfile) ? CurrentProfile
            : !string.IsNullOrWhiteSpace(StartProfile) ? StartProfile
            : Resources.DefaultServerProfileName;

        /// <summary>The session for the profile currently shown in the UI (created on demand).</summary>
        private ServerSession ActiveSession => GetOrCreateSession(ActiveProfileName);

        /// <summary>
        /// The active session's server. Keeps the existing RPC bodies single-server-shaped:
        /// every lifecycle/RCON call routes to whichever server the UI is looking at, while
        /// other sessions keep running in the background.
        /// </summary>
        private ValheimServer Server => ActiveSession.Server;

        private bool _modScanInProgress;
        private bool _modUpdateInProgress;
        private bool _requiredModInstallInProgress;
        private bool _maxPlayersSaveInProgress;
        private bool _atlasRenderInProgress;
        private bool _atlasInfoInProgress;

        /// <summary>
        /// Locations worth pinning on the Atlas map (boss altars, spawn, traders),
        /// keyed by their save-file prefab name. Everything else in the ~19k location
        /// list is dungeon/ruin noise at map scale.
        /// </summary>
        private static readonly Dictionary<string, string> AtlasPoiLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["StartTemple"] = "Sacrificial Stones",
            ["Eikthyrnir"] = "Eikthyr",
            ["GDKing"] = "The Elder",
            ["Bonemass"] = "Bonemass",
            ["Dragonqueen"] = "Moder",
            ["GoblinKing"] = "Yagluth",
            ["Mistlands_DvergrBossEntrance1"] = "The Queen",
            ["FaderLocation"] = "Fader",
            ["Vendor_BlackForest"] = "Haldor",
            ["Hildir_camp"] = "Hildir",
            ["BogWitch_Camp"] = "Bog Witch",
        };

        // Max Players above vanilla's 10 is handled by the bundled BakaLoaderMaxPlayers
        // companion plugin (Resources/MaxPlayers), installed on demand via MaxPlayersInstaller.
        // It replaced the third-party Azumatt-MaxPlayerCount mod in 0.9.34; the installer
        // migrates legacy installs automatically at server start. Its BepInEx cfg is the
        // single source of truth for the configured count (no pref duplication).

        private void InitializeBridge(
            IServiceProvider serviceProvider,
            IUserPreferencesProvider userPrefsProvider,
            IServerPreferencesProvider serverPrefsProvider,
            IWorldPreferencesProvider worldPrefsProvider,
            IPlayerDataRepository playerDataProvider,
            IIpAddressProvider ipAddressProvider,
            IModScanner modScanner,
            IThunderstoreClient thunderstoreClient,
            IModUpdateService modUpdateService,
            IModRemovalService modRemovalService,
            IRequiredModChecker requiredModChecker,
            ItemCatalog itemCatalog,
            PlayerListService playerListService,
            IAppUpdateService appUpdateService,
            IHeartbeatService heartbeatService,
            IApplicationLogger appLogger)
        {
            ServiceProvider = serviceProvider;
            UserPrefsProvider = userPrefsProvider;
            ServerPrefsProvider = serverPrefsProvider;
            WorldPrefsProvider = worldPrefsProvider;
            PlayerDataProvider = playerDataProvider;
            IpAddressProvider = ipAddressProvider;
            ModScanner = modScanner;
            ThunderstoreClient = thunderstoreClient;
            ModUpdateService = modUpdateService;
            ModRemovalService = modRemovalService;
            RequiredModChecker = requiredModChecker;
            ItemCatalog = itemCatalog;
            PlayerListService = playerListService;
            AppUpdateService = appUpdateService;
            Heartbeat = heartbeatService;
            InstallIsolation = serviceProvider.GetRequiredService<IInstallIsolationService>();
            MaxPlayersInstaller = serviceProvider.GetRequiredService<IMaxPlayersInstaller>();
            DiscordStatus = serviceProvider.GetRequiredService<IDiscordStatusService>();
            DiscordWebhooks = serviceProvider.GetRequiredService<IDiscordWebhookService>();
            Analytics = serviceProvider.GetRequiredService<IAnalyticsService>();
            AppLogger = appLogger;

            // The Herald: single self-editing Discord status post (gated on prefs inside the service).
            DiscordStatus.SnapshotProvider = BuildDiscordSnapshot;
            DiscordStatus.RequestUpdate();

            // Anonymous usage heartbeat (gated on the ShareAnonymousStats pref).
            Heartbeat.ServerRunningProvider = () =>
                Sessions.Values.Any(s => s.Server.Status == ServerStatus.Running);
            Heartbeat.Start();

            RegisterServiceEvents();
            RegisterRpcHandlers();
        }

        #region Multi-server session registry

        /// <summary>
        /// Returns the live session for a profile, creating (and fully wiring) it on first
        /// use. Each session owns its own transient ValheimServer; the ServerKey stamp is
        /// what keeps concurrent servers from cross-attributing players in the shared repo.
        /// </summary>
        private ServerSession GetOrCreateSession(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) profileName = Resources.DefaultServerProfileName;
            if (Sessions.TryGetValue(profileName, out var existing)) return existing;

            lock (SessionsLock)
            {
                if (Sessions.TryGetValue(profileName, out existing)) return existing;

                var server = (ValheimServer)ServiceProvider.GetService(typeof(ValheimServer))
                    ?? throw new InvalidOperationException("ValheimServer is not registered");
                server.ServerKey = profileName;

                var session = new ServerSession(profileName, server);
                WireSessionEvents(session);
                WireAppSelfUpdate(session);
                WireModUpdateHooks(session);

                Sessions[profileName] = session;
                return session;
            }
        }

        /// <summary>
        /// Forwards one session's server events to JS, tagged with the profile name so the
        /// UI can tell "the server I'm looking at" apart from a background server's chip.
        /// </summary>
        private void WireSessionEvents(ServerSession session)
        {
            var profile = session.ProfileName;
            var server = session.Server;

            // Tracks whether the most recent exit was a crash, so the Stopped transition
            // below doesn't ALSO announce a routine "server stopped" for it.
            // (ServerCrashed always fires before the status lands on Stopped.)
            var crashAnnounced = false;

            // Same idea for the Skald's journal, kept separate because the Discord flag
            // only gets set when Discord event posts are enabled.
            var crashRecorded = false;

            string DisplayName() =>
                ServerPrefsProvider.LoadPreferences(profile)?.Name is string n && !string.IsNullOrWhiteSpace(n)
                    ? n : profile;

            server.StatusChanged += (s, status) =>
            {
                PostEvent("server.status", BuildServerState(session));
                PostEvent("servers.changed", BuildServersList());

                if (status == ServerStatus.Running)
                {
                    crashRecorded = false;
                    Analytics.Record(new AnalyticsEvent { Kind = "start", Server = profile });
                }
                else if (status == ServerStatus.Stopped && !crashRecorded)
                {
                    // A crash already closed this uptime span with its own event.
                    Analytics.Record(new AnalyticsEvent { Kind = "stop", Server = profile });
                }

                DiscordStatus.RequestUpdate();

                if (UserPrefsProvider.LoadPreferences().DiscordEventPosts)
                {
                    if (status == ServerStatus.Running)
                    {
                        crashAnnounced = false;
                        DiscordWebhooks.SendServerStarted(DisplayName());
                    }
                    else if (status == ServerStatus.Stopped && !crashAnnounced)
                    {
                        DiscordWebhooks.SendServerStopped(DisplayName());
                    }
                }
            };
            server.WorldSaved += (s, seconds) => PostEvent("server.worldSaved", new { seconds, profile });
            server.InviteCodeReady += (s, code) => PostEvent("server.inviteCode", new { code, profile });
            server.ServerCrashed += (s, e) =>
            {
                PostEvent("server.crashed", new { profile });

                crashRecorded = true;
                Analytics.Record(new AnalyticsEvent { Kind = "crash", Server = profile });

                if (UserPrefsProvider.LoadPreferences().DiscordEventPosts)
                {
                    crashAnnounced = true;
                    var willRestart = server.Options?.AutoRestart == true;
                    DiscordWebhooks.SendServerCrashed(DisplayName(), willRestart, server.Options?.AutoRestartDelay ?? 0);
                }
            };
            server.CountdownTick += (s, message) => PostEvent("server.countdown", new { message, profile });
            server.PlayerDied += (s, characterName) =>
            {
                // Tie the character back to a known player when possible: same server,
                // seen using this character most recently, preferring whoever is online.
                var player = PlayerDataProvider.Data
                    .Where(pl => (pl.ServerKey == null || string.Equals(pl.ServerKey, profile, StringComparison.OrdinalIgnoreCase))
                              && string.Equals(pl.LastStatusCharacter, characterName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(pl => pl.PlayerStatus == PlayerStatus.Online)
                    .ThenByDescending(pl => pl.LastStatusChange)
                    .FirstOrDefault();

                Analytics.Record(new AnalyticsEvent
                {
                    Kind = "death",
                    Server = profile,
                    PlayerKey = player?.Key,
                    PlayerName = player?.PlayerName,
                    Character = characterName,
                });
                PostEvent("server.playerDied", new { character = characterName, profile });
            };
        }

        /// <summary>
        /// Preflight for starting a session while others run: Valheim binds the game port
        /// AND port+1, RCON ports must be unique, two servers can't share one exe install
        /// (BepInEx config/plugins would collide), and one world can't be loaded twice.
        /// </summary>
        private void EnsureNoServerCollisions(ServerSession starting, IValheimServerOptions options)
        {
            foreach (var other in Sessions.Values)
            {
                if (ReferenceEquals(other, starting)) continue;

                var live = other.Server;
                if (live.Status == ServerStatus.Stopped) continue;

                var theirs = live.Options;
                if (theirs == null) continue;

                if (Math.Abs(options.Port - theirs.Port) <= 1)
                    throw new InvalidOperationException(
                        $"Port clash with running server '{other.ProfileName}': Valheim uses ports " +
                        $"{theirs.Port}-{theirs.Port + 1}. Give this profile a different port (2+ apart).");

                if (options.RconEnabled && theirs.RconEnabled && options.RconPort == theirs.RconPort)
                    throw new InvalidOperationException(
                        $"RCON port {options.RconPort} is already in use by running server '{other.ProfileName}'.");

                if (SamePath(options.ServerExePath, theirs.ServerExePath))
                    throw new InvalidOperationException(
                        $"Server install clash with running server '{other.ProfileName}': two servers can't " +
                        "share one valheim_server.exe (their BepInEx mods and configs would collide). " +
                        "Give each profile its own copy of the server folder - a renamed exe works.");

                if (!string.IsNullOrWhiteSpace(options.WorldName)
                    && string.Equals(options.WorldName, theirs.WorldName, StringComparison.OrdinalIgnoreCase)
                    && SamePath(options.SaveDataFolderPath ?? "", theirs.SaveDataFolderPath ?? ""))
                    throw new InvalidOperationException(
                        $"World clash: '{options.WorldName}' is already loaded by running server '{other.ProfileName}'.");
            }
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b);
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        #endregion

        #region Splash startup (profile handoff, orphan adoption, auto-start)

        /// <summary>
        /// Runs once on first Show (BlendWindow is always routed through SplashForm, which
        /// assigns StartProfile/StartServerAutomatically). Mirrors MainWindow.OnShown:
        /// check for an orphaned server process first, then auto-start when requested.
        /// Player data is loaded by SplashForm's startup task, not here.
        /// </summary>
        private void OnBlendStartup()
        {
            if (!string.IsNullOrWhiteSpace(StartProfile))
            {
                // Hand the splash-assigned profile to the JS boot sequence via app.info.
                CurrentProfile ??= StartProfile;
            }

            var proceed = true;
            try
            {
                proceed = CheckForExistingServerProcess();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Orphaned-server check failed");
            }

            if (!proceed)
            {
                // Another BakaLoader instance is still in charge of the running server.
                // This instance must bow out - adopting the server here would put it in
                // BOTH instances' kill-on-close job objects, so closing EITHER window
                // would kill it. Exiting the new instance is the only safe move.
                // The form may already be tearing down (e.g. the WebView failed to
                // initialize and its error path called Close first), and BeginInvoke on
                // a disposed / handle-less form throws - guard it (live-crash 2026-07-07).
                try
                {
                    if (IsDisposed || Disposing) return;
                    if (IsHandleCreated) BeginInvoke(new Action(Close));
                    else Close();
                }
                catch (InvalidOperationException)
                {
                    // Form tore down between the check and the call - already exiting.
                }
                return;
            }

            if (StartServerAutomatically)
            {
                AutoStartServer();
            }
        }

        /// <summary>
        /// Handles a server process that is already running at startup. Returns false when
        /// this instance must exit (another BakaLoader instance owns the server), true to
        /// continue normal startup.
        /// </summary>
        private bool CheckForExistingServerProcess()
        {
            // Scope discovery to the executable BakaLoader is configured to manage so
            // we never adopt or kill an unrelated valheim_server install.
            var existing = ValheimServer.FindExistingServerProcess(GetServerExePath());
            if (existing == null) return true;

            var nl = Environment.NewLine;

            // If another BakaLoader instance is alive, the server almost certainly belongs
            // to it - and its kill-on-close job object means closing that instance would
            // kill the server. Never offer adoption here: the only safe action is closing
            // THIS (new) instance and leaving the old one in charge.
            var other = FindOtherBakaLoaderInstance();
            if (other != null)
            {
                var otherVersion = TryGetProcessVersion(other);
                var otherLabel = otherVersion != null
                    ? $"another BakaLoader (version {otherVersion}, PID {other.Id})"
                    : $"another BakaLoader (PID {other.Id})";

                Logger.Warning(
                    "Server PID {serverPid} is managed by another BakaLoader instance (PID {otherPid}) - this instance will close",
                    existing.Id, other.Id);

                var page = new TaskDialogPage
                {
                    Caption = Resources.ApplicationTitle,
                    Heading = "Another BakaLoader is already running",
                    Text =
                        $"A Valheim dedicated server (PID {existing.Id}) is already running and " +
                        $"{otherLabel} is managing it.{nl}{nl}" +
                        $"That BakaLoader stays in charge - it cannot be closed automatically, because " +
                        $"closing it shuts down the server it is running.{nl}{nl}" +
                        $"This new window will close. To switch to this version, stop the server in the " +
                        $"old BakaLoader, close it, then launch this one again.",
                    Icon = TaskDialogIcon.Warning,
                    AllowCancel = false,
                };
                page.Buttons.Clear();
                page.Buttons.Add(new TaskDialogButton("Close this"));

                TaskDialog.ShowDialog(this, page);
                return false;
            }

            // No other BakaLoader instance: this is a genuine orphan (BakaLoader crashed or
            // was closed without stopping the server) - offer adoption as before.
            Logger.Warning("Found an already-running Valheim server process (PID {pid})", existing.Id);
            var result = MessageBox.Show(
                $"A Valheim dedicated server is already running (PID {existing.Id}).{nl}{nl}" +
                $"This can happen if BakaLoader was closed without stopping the server, " +
                $"or if the server was started outside of BakaLoader.{nl}{nl}" +
                $"Yes = Adopt this server (monitor and manage it){nl}" +
                $"No  = Kill it so you can start fresh",
                "Server Already Running",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                // Adopt: track the process so Stop/Restart/exit-detection work.
                // No log capture (stdout was not redirected by us).
                try
                {
                    Server.AdoptProcess(existing, BuildServerOptions(LoadStartupPrefs()));
                    Logger.Information("Adopted server process PID {pid}", existing.Id);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to adopt server process");
                }
            }
            else if (result == DialogResult.No)
            {
                try
                {
                    Logger.Information("Killing orphaned server process PID {pid}", existing.Id);
                    existing.Kill();
                    existing.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to kill server process");
                }
            }
            // Cancel = leave it alone, user can manage manually
            return true;
        }

        /// <summary>
        /// Another live BakaLoader process (same exe name, different PID), or null.
        /// </summary>
        private static Process FindOtherBakaLoaderInstance()
        {
            try
            {
                using var me = Process.GetCurrentProcess();
                return Process.GetProcessesByName(me.ProcessName)
                    .FirstOrDefault(p => p.Id != me.Id);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Product version of a process's main module ("0.9.26"), or null when unreadable.</summary>
        private static string TryGetProcessVersion(Process process)
        {
            try
            {
                var version = process.MainModule?.FileVersionInfo?.ProductVersion;
                if (string.IsNullOrWhiteSpace(version)) return null;

                // Trim SourceRevisionId build metadata: "0.9.26+build2026-..." -> "0.9.26"
                var plus = version.IndexOf('+');
                return plus > 0 ? version[..plus] : version;
            }
            catch
            {
                return null;
            }
        }

        private void AutoStartServer()
        {
            try
            {
                if (!Server.CanStart)
                {
                    Logger.Warning(
                        "Auto-start skipped: server is not in a startable state ({status})", Server.Status);
                    return;
                }

                var prefs = ServerPrefsProvider.LoadPreferences(StartProfile)
                    ?? throw new InvalidOperationException($"No server profile named '{StartProfile}'");

                Server.Start(BuildServerOptions(prefs));
                Logger.Information("Auto-started server for profile {profile}", StartProfile);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to auto-start server for profile {profile}", StartProfile);
            }
        }

        #region Close guard (save the world before the hearth goes out)

        private bool _closeGuardPassed;
        private bool _closeShutdownInProgress;

        /// <summary>
        /// The window X (and Alt+F4) while the server is running would otherwise let the
        /// kill-on-close job object hard-kill valheim_server with no final world save.
        /// Instead: notify the user (calling out any vikings still online), then shut the
        /// server down gracefully (close request, not /F - Valheim flushes a final world
        /// save on the way down) and only close BakaLoader once it has stopped.
        /// </summary>
        private void OnCloseRequested(object sender, FormClosingEventArgs e)
        {
            if (_closeGuardPassed) return;
            if (Sessions.Values.All(s => s.Server.Status == ServerStatus.Stopped)) return;

            e.Cancel = true;
            if (_closeShutdownInProgress) return; // already saving + shutting down

            // Vikings on ANY running server count - closing the app shuts them all down.
            var online = PlayerDataProvider.Data
                .Count(pl => pl.PlayerStatus == PlayerStatus.Online || pl.PlayerStatus == PlayerStatus.Joining);

            var nl = Environment.NewLine;
            var playersLine = online > 0
                ? $"{online} viking{(online == 1 ? " is" : "s are")} still online and will be disconnected.{nl}{nl}"
                : "";

            var runningCount = Sessions.Values.Count(s => s.Server.Status != ServerStatus.Stopped);

            var page = new TaskDialogPage
            {
                Caption = Resources.ApplicationTitle,
                Heading = online > 0
                    ? "Vikings are still online"
                    : runningCount > 1 ? "Servers are still running" : "The server is still running",
                Text = playersLine + (runningCount > 1
                    ? $"All {runningCount} running servers will save their worlds and shut down gracefully before BakaLoader closes."
                    : "The world will be saved and the server shut down gracefully before BakaLoader closes."),
                Icon = online > 0 ? TaskDialogIcon.Warning : TaskDialogIcon.Information,
                AllowCancel = true,
            };
            var shutdown = new TaskDialogButton("Save && shut down");
            page.Buttons.Clear();
            page.Buttons.Add(shutdown);
            page.Buttons.Add(TaskDialogButton.Cancel);

            if (TaskDialog.ShowDialog(this, page) != shutdown) return;

            ShutDownAllServersThenClose();
        }

        private async void ShutDownAllServersThenClose()
        {
            _closeShutdownInProgress = true;
            try
            {
                Logger.Information("Close requested while server(s) are running - saving worlds and shutting down first");

                // Bounded wait so a wedged server can never trap the user in a window
                // that refuses to close. Stop() is a graceful close request, so Valheim
                // saves the world on the way down; it no-ops until CanStop (e.g. while
                // the server is still Starting), hence the retry inside the loop.
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (Sessions.Values.Any(s => s.Server.Status != ServerStatus.Stopped)
                    && DateTime.UtcNow < deadline)
                {
                    foreach (var session in Sessions.Values)
                    {
                        if (session.Server.CanStop) session.Server.Stop();
                    }
                    await Task.Delay(250);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Graceful shutdown before close failed");
            }
            finally
            {
                _closeGuardPassed = true;
                Close();
            }
        }

        #endregion

        /// <summary>StartProfile's saved preferences, or defaults when the profile is missing.</summary>
        private ServerPreferences LoadStartupPrefs()
        {
            var prefs = string.IsNullOrWhiteSpace(StartProfile)
                ? null
                : ServerPrefsProvider.LoadPreferences(StartProfile);
            return prefs ?? new ServerPreferences();
        }

        #endregion

        #region Self-update on restart

        /// <summary>
        /// Wires the server's restart flow to a BakaLoader self-update check. On every restart,
        /// if the user has enabled auto-update, the app checks GitHub for a newer release; when
        /// one is staged, the app closes so the headless watchdog can swap the files and relaunch
        /// the updated app (which re-auto-starts the server from the profile's AutoStart pref).
        /// </summary>
        private void WireAppSelfUpdate(ServerSession session)
        {
            session.Server.CheckForAppUpdateOnRestart = async () =>
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                if (!prefs.AutoUpdateBakaLoader) return false;

                // Never self-update while ANOTHER server is still running: swapping the app
                // files closes every session, not just the one that happens to be restarting.
                var othersLive = Sessions.Values.Any(s =>
                    !ReferenceEquals(s, session) && s.Server.Status != ServerStatus.Stopped);
                if (othersLive) return false;

                var staged = await AppUpdateService.CheckAndStageUpdateAsync();
                if (!staged) return false;

                // An update is staged; close the app so the watchdog can take over. Marshal to
                // the UI thread and defer the close so this hook returns first and the restart
                // is cleanly abandoned. Go through the graceful shutdown path: it saves the
                // world and stops the server before closing (a bare Close() would be cancelled
                // by the close guard - unattended dialog - and the old behavior let the job
                // object hard-kill the server with no final save).
                BeginInvoke(new Action(ShutDownAllServersThenClose));
                return true;
            };
        }

        /// <summary>
        /// Wires the empty-server auto mod-update machinery (pending-count probe + apply).
        /// Gated on the AutoUpdateMods pref; scans this session's OWN plugins folder so
        /// per-server mod installs update independently.
        /// </summary>
        private void WireModUpdateHooks(ServerSession session)
        {
            var profile = session.ProfileName;

            session.Server.GetPendingModUpdateCount = async () =>
            {
                if (!UserPrefsProvider.LoadPreferences().AutoUpdateMods) return 0;

                var mods = await ScanModsWithLatestAsync(profile);
                return mods?.Count(m => m.UpdateAvailable) ?? 0;
            };

            session.Server.ApplyModUpdates = async () =>
            {
                if (!UserPrefsProvider.LoadPreferences().AutoUpdateMods) return;

                var mods = await ScanModsWithLatestAsync(profile);
                var updatable = mods?.Where(m => m.UpdateAvailable).ToList();
                if (updatable == null || updatable.Count == 0) return;

                var results = await ModUpdateService.UpdateModsAsync(updatable);
                Logger.Information("Auto-updated {count} mod(s) for profile {profile}",
                    results.Count(r => r.Updated), profile);

                RecordModUpdates(profile, results);

                // Herald: mod count / last-update time changed.
                DiscordStatus.RequestUpdate();
            };
        }

        /// <summary>Per-player scratch bucket for the analytics.overview aggregation.</summary>
        private class SkaldPlayerAgg
        {
            public string Key;
            public string Name;
            public string Character;
            public double PlaySec;
            public int Sessions;
            public int Deaths;
            public DateTime LastSeen;
            public DateTime? OpenSince;
            public bool OnlineNow;
        }

        /// <summary>Writes each successful mod update into the Skald's journal.</summary>
        private void RecordModUpdates(string profile, IEnumerable<ModUpdateResult> results)
        {
            if (results == null) return;
            foreach (var r in results.Where(r => r != null && r.Updated))
            {
                Analytics.Record(new AnalyticsEvent
                {
                    Kind = "modup",
                    Server = profile,
                    Mod = r.Mod?.FullName ?? r.Mod?.ModName,
                    FromVersion = r.FromVersion,
                    ToVersion = r.ToVersion,
                });
            }
        }

        /// <summary>
        /// Scans a profile's plugins folder and resolves each mod's latest Thunderstore
        /// version. Null when the profile has no usable plugins folder.
        /// </summary>
        private async Task<List<Tools.Models.InstalledMod>> ScanModsWithLatestAsync(string profileName)
        {
            var pluginsDir = GetPluginsDirectoryFor(profileName);
            if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir)) return null;

            var mods = ModScanner.ScanPlugins(pluginsDir);
            await Task.WhenAll(mods.Select(async mod =>
            {
                try
                {
                    var package = await ThunderstoreClient.GetLatestAsync(mod.Author, mod.ModName);
                    mod.LatestVersion = package?.LatestVersion;
                }
                catch
                {
                    // Leave LatestVersion null - unknown means "no update".
                }
            }));
            return mods;
        }

        #endregion

        #region Event forwarding (C# -> JS pushes)

        private void RegisterServiceEvents()
        {
            // Per-server events (status/save/invite/crash/countdown) are wired per session
            // in WireSessionEvents, tagged with the profile name.
            PlayerDataProvider.EntityUpdated += (s, player) => PostEvent("player.updated", BuildPlayerDto(player));

            // Status transitions drive the server chips' player-count badges.
            PlayerDataProvider.PlayerStatusChanged += (s, player) =>
            {
                PostEvent("servers.changed", BuildServersList());

                // Herald: player counts changed - refresh the Discord status post.
                DiscordStatus.RequestUpdate();

                // Skald journal: record real join/leave transitions only. This event also
                // fires on name-resolution updates with an unchanged status, so gate on a
                // per-player last-recorded-status map.
                if (player != null
                    && (player.PlayerStatus == PlayerStatus.Online || player.PlayerStatus == PlayerStatus.Offline))
                {
                    var seenBefore = AnalyticsPlayerStatus.TryGetValue(player.Key, out var last);
                    AnalyticsPlayerStatus[player.Key] = player.PlayerStatus;

                    // Only an Online arrival counts as a join; only Online->Offline counts
                    // as a leave (a failed join that never got Online isn't a session).
                    var isJoin = player.PlayerStatus == PlayerStatus.Online && (!seenBefore || last != PlayerStatus.Online);
                    var isLeave = player.PlayerStatus == PlayerStatus.Offline && seenBefore && last == PlayerStatus.Online;

                    if (isJoin || isLeave)
                    {
                        Analytics.Record(new AnalyticsEvent
                        {
                            Kind = isJoin ? "join" : "leave",
                            // Legacy player records predating multi-server have no
                            // ServerKey; attribute those to the profile on screen.
                            Server = player.ServerKey ?? ActiveProfileName,
                            PlayerKey = player.Key,
                            PlayerName = player.PlayerName,
                            Character = player.LastStatusCharacter,
                        });
                    }
                }

                if (UserPrefsProvider.LoadPreferences().DiscordEventPosts
                    && !string.IsNullOrWhiteSpace(player?.PlayerName))
                {
                    var serverName = ServerPrefsProvider.LoadPreferences(player.ServerKey)?.Name;
                    if (string.IsNullOrWhiteSpace(serverName)) serverName = player.ServerKey ?? "server";

                    if (player.PlayerStatus == PlayerStatus.Online)
                        DiscordWebhooks.SendPlayerJoined(player.PlayerName, serverName);
                    else if (player.PlayerStatus == PlayerStatus.Offline)
                        DiscordWebhooks.SendPlayerLeft(player.PlayerName, serverName);
                }
            };

            IpAddressProvider.ExternalIpChanged += (s, ip) =>
            {
                PostEvent("ip.external", new { ip });
                DiscordStatus.RequestUpdate();
            };
            IpAddressProvider.InternalIpChanged += (s, ip) => PostEvent("ip.internal", new { ip });

            ServerPrefsProvider.PreferencesSaved += (s, all) =>
                PostEvent("profiles.changed", all.Select(p => new { p.ProfileName, p.LastSaved }).ToList());

            AppLogger.LogReceived += line => PostEvent("log.app", new { line });
        }

        #endregion

        #region RPC registration

        private void RegisterRpcHandlers()
        {
            // --- App ---
            RegisterRpc("app.info", p => Task.FromResult<object>(new
            {
                version = AssemblyHelper.GetApplicationVersion(),
                // StartProfile fallback covers the (unlikely) case where the JS boots
                // before OnBlendStartup has copied the splash-assigned profile over.
                profile = CurrentProfile ?? StartProfile,
            }));

            // --- Profiles (ServerPreferences) ---
            RegisterRpc("profiles.list", p => Task.FromResult<object>(
                ServerPrefsProvider.LoadPreferences()
                    .Select(pref => new { pref.ProfileName, pref.LastSaved })
                    .ToList()));

            RegisterRpc("profiles.get", p =>
            {
                var name = p.Value<string>("name");
                var prefs = ServerPrefsProvider.LoadPreferences(name)
                    ?? throw new ArgumentException($"No profile named '{name}'");
                CurrentProfile = prefs.ProfileName;
                // Loading a profile IS the server switch - refresh the chips' active marker.
                PostEvent("servers.changed", BuildServersList());
                return Task.FromResult<object>(prefs);
            });

            RegisterRpc("profiles.save", p =>
            {
                var prefs = (p["prefs"] ?? throw new ArgumentException("prefs is required"))
                    .ToObject<ServerPreferences>();
                ServerPrefsProvider.SavePreferences(prefs);
                CurrentProfile = prefs.ProfileName;
                PostEvent("servers.changed", BuildServersList());
                return Task.FromResult<object>(prefs);
            });

            RegisterRpc("profiles.remove", p =>
            {
                var name = p.Value<string>("name");
                if (Sessions.TryGetValue(name, out var session) && session.Server.Status != ServerStatus.Stopped)
                    throw new InvalidOperationException($"Stop the server '{name}' before removing its profile.");

                ServerPrefsProvider.RemovePreferences(name);
                if (CurrentProfile == name) CurrentProfile = null;

                // Drop the dead session so its server unsubscribes from the player repo.
                if (Sessions.TryRemove(name, out var removed)) removed.Server.Dispose();

                PostEvent("servers.changed", BuildServersList());
                return Task.FromResult<object>(true);
            });

            // Renames a profile in place (settings only; the isolated install folder keeps
            // its original on-disk name, which is harmless). Guards a running server and a
            // name collision.
            RegisterRpc("profiles.rename", p =>
            {
                var name = p.Value<string>("name");
                var newName = (p.Value<string>("newName") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(newName))
                    throw new ArgumentException("A new name is required.");
                if (string.Equals(name, newName, StringComparison.Ordinal))
                    return Task.FromResult<object>(true);

                var prefs = ServerPrefsProvider.LoadPreferences(name)
                    ?? throw new ArgumentException($"No profile named '{name}'");
                if (Sessions.TryGetValue(name, out var s) && s.Server.Status != ServerStatus.Stopped)
                    throw new InvalidOperationException($"Stop the server '{name}' before renaming it.");
                if (ServerPrefsProvider.LoadPreferences()
                        .Any(x => string.Equals(x.ProfileName, newName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"A server named '{newName}' already exists.");

                prefs.ProfileName = newName;
                ServerPrefsProvider.SavePreferences(prefs);
                ServerPrefsProvider.RemovePreferences(name);

                // Retire any (stopped) session under the old key so the registry stays consistent.
                if (Sessions.TryRemove(name, out var removed)) removed.Server.Dispose();
                if (string.Equals(CurrentProfile, name, StringComparison.OrdinalIgnoreCase))
                    CurrentProfile = newName;

                PostEvent("servers.changed", BuildServersList());
                return Task.FromResult<object>(true);
            });

            // Soft-delete: hide from the chip strip but keep everything on disk, so the
            // realm can be restored later. Touches no files. Switches the UI away if active.
            RegisterRpc("profiles.archive", p =>
            {
                var name = p.Value<string>("name");
                var prefs = ServerPrefsProvider.LoadPreferences(name)
                    ?? throw new ArgumentException($"No profile named '{name}'");
                if (Sessions.TryGetValue(name, out var s) && s.Server.Status != ServerStatus.Stopped)
                    throw new InvalidOperationException($"Stop the server '{name}' before archiving it.");

                prefs.Archived = true;
                ServerPrefsProvider.SavePreferences(prefs);
                if (string.Equals(CurrentProfile, name, StringComparison.OrdinalIgnoreCase))
                    CurrentProfile = FirstActiveProfileExcept(name);

                PostEvent("servers.changed", BuildServersList());
                return Task.FromResult<object>(true);
            });

            RegisterRpc("profiles.unarchive", p =>
            {
                var name = p.Value<string>("name");
                var prefs = ServerPrefsProvider.LoadPreferences(name)
                    ?? throw new ArgumentException($"No profile named '{name}'");
                prefs.Archived = false;
                ServerPrefsProvider.SavePreferences(prefs);
                PostEvent("servers.changed", BuildServersList());
                return Task.FromResult<object>(true);
            });

            // Pre-delete summary so the danger dialog can tell the user exactly what a
            // "delete files too" will reclaim (isolated worlds/backups + isolated install).
            RegisterRpc("profiles.deleteInfo", p =>
            {
                var name = p.Value<string>("name");
                var prefs = ServerPrefsProvider.LoadPreferences(name)
                    ?? throw new ArgumentException($"No profile named '{name}'");

                var running = Sessions.TryGetValue(name, out var s) && s.Server.Status != ServerStatus.Stopped;
                var installDir = GetManagedInstallDir(prefs);
                var saveFolder = GetIsolatedSaveFolder(prefs);
                var (fileCount, sizeBytes) = MeasureDeletableFiles(installDir, saveFolder);

                return Task.FromResult<object>(new
                {
                    name,
                    running,
                    isLast = CountNonArchivedProfiles() <= 1,
                    hasIsolatedInstall = installDir != null,
                    installDir,
                    saveFolder,
                    fileCount,
                    sizeBytes,
                });
            });

            // Hard-delete a profile. deleteFiles=false removes only the saved settings
            // (files stay, realm can be re-adopted); deleteFiles=true also reclaims the
            // isolated install (junction-safe) and this server's isolated worlds/backups.
            RegisterRpc("profiles.delete", async p =>
            {
                var name = p.Value<string>("name");
                var deleteFiles = p.Value<bool?>("deleteFiles") ?? false;

                var prefs = ServerPrefsProvider.LoadPreferences(name)
                    ?? throw new ArgumentException($"No profile named '{name}'");
                if (Sessions.TryGetValue(name, out var s) && s.Server.Status != ServerStatus.Stopped)
                    throw new InvalidOperationException($"Stop the server '{name}' before deleting it.");
                if (CountNonArchivedProfiles() <= 1 && !prefs.Archived)
                    throw new InvalidOperationException("This is your only active server - archive it instead of deleting the last one.");

                if (deleteFiles)
                {
                    var installDir = GetManagedInstallDir(prefs);
                    var saveFolder = GetIsolatedSaveFolder(prefs);

                    // Slow file work off the UI thread; junction-safe delete never touches shared game data.
                    await Task.Run(() =>
                    {
                        if (installDir != null)
                        {
                            try { InstallIsolation.DeleteInstall(installDir); }
                            catch (Exception e) { AppLogger.Error(e, $"Failed to reclaim isolated install for '{name}'."); }
                        }
                        if (saveFolder != null && Directory.Exists(saveFolder))
                        {
                            try { Directory.Delete(saveFolder, recursive: true); }
                            catch (Exception e) { AppLogger.Error(e, $"Failed to delete isolated save folder for '{name}'."); }
                        }
                    });
                }

                ServerPrefsProvider.RemovePreferences(name);
                if (Sessions.TryRemove(name, out var removed)) removed.Server.Dispose();
                if (string.Equals(CurrentProfile, name, StringComparison.OrdinalIgnoreCase))
                    CurrentProfile = FirstActiveProfileExcept(name);

                PostEvent("servers.changed", BuildServersList());
                return true;
            });

            // --- Multi-server registry ---
            RegisterRpc("servers.list", p => Task.FromResult<object>(BuildServersList()));

            // Suggests a free game port (+ its silent port+1 twin) and a free RCON port that
            // don't clash with any existing profile, so the New-Server wizard can show the
            // ports it will assign before the user commits.
            RegisterRpc("servers.suggestPort", p =>
            {
                var (gamePort, rconPort) = SuggestFreePorts();
                return Task.FromResult<object>(new { gamePort, rconPort });
            });

            // Creates a new server profile with guaranteed-distinct identity (name, world,
            // ports) so a second server is genuinely separate, not a shadow of the first.
            // When isolateInstall is set, provisions a junction-based isolated install (its
            // own BepInEx/plugins) off the base install so its mod set is independent. The
            // slow provisioning (copying the base mod set) runs off the UI thread.
            RegisterRpc("servers.create", async p =>
            {
                var name = (p.Value<string>("name") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("A server name is required.");

                if (ServerPrefsProvider.LoadPreferences()
                        .Any(x => string.Equals(x.ProfileName, name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"A server named '{name}' already exists. Pick a different name.");

                var isolateInstall = p.Value<bool?>("isolateInstall") ?? true;
                var seedMods = p.Value<bool?>("seedMods") ?? true;
                var isolateSaveFolder = p.Value<bool?>("isolateSaveFolder") ?? true;

                // Distinct world: explicit value, else a safe token derived from the name.
                var world = (p.Value<string>("world") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(world)) world = InstallIsolationService.MakeSafeName(name);

                // Ports: explicit values (validated free) else auto-suggested.
                var (suggestedGame, suggestedRcon) = SuggestFreePorts();
                var gamePort = p.Value<int?>("port") ?? suggestedGame;
                var rconPort = p.Value<int?>("rconPort") ?? suggestedRcon;

                // Seed the new profile from the active one so it inherits sane backup/save-interval
                // defaults, then stamp on its own distinct identity.
                var basePrefs = ServerPrefsProvider.LoadPreferences(ActiveProfileName) ?? new ServerPreferences();
                var prefs = basePrefs.ToFile();
                var created = ServerPreferences.FromFile(prefs);

                created.ProfileName = name;
                created.Name = name;              // its own in-game name, never the base server's
                created.WorldName = world;
                created.Port = gamePort;
                created.RconPort = rconPort;
                created.AutoStart = false;        // never surprise-launch a brand-new server
                created.Archived = false;
                created.LastSaved = DateTime.UtcNow;

                // Save folder: give the new server its own so worlds/backups never mingle.
                if (isolateSaveFolder)
                    created.SaveDataFolderPath = MakeIsolatedSaveFolder(name);

                // Optional explicit seed for the NEW world: pre-write its .fwl (the
                // dedicated server has no -seed argument, but it adopts a pre-existing
                // .fwl on first launch). FwlWriter hard-refuses when the world already
                // exists - a created world's seed is immutable. Blank = let the game
                // roll a random seed itself on first launch.
                var worldSeed = (p.Value<string>("worldSeed") ?? "").Trim();
                if (worldSeed.Length > 0)
                {
                    var targetSaveFolder = isolateSaveFolder
                        ? created.SaveDataFolderPath
                        : ResolveSaveDataFolder(created.SaveDataFolderPath);
                    if (string.IsNullOrWhiteSpace(targetSaveFolder))
                        throw new InvalidOperationException("No save folder is configured, so the world seed can't be applied.");

                    var written = FwlWriter.WriteNewWorld(targetSaveFolder, world, worldSeed);
                    Logger.Information("Pre-created world '{0}' with seed '{1}' ({2}).",
                        world, written.SeedName, written.Seed);
                }

                if (isolateInstall)
                {
                    var baseExe = GetCanonicalBaseServerExe();
                    var seedSourceBepInEx = GetActiveBepInExDir();
                    var result = await Task.Run(() =>
                        InstallIsolation.ProvisionInstall(baseExe, name, seedMods, seedSourceBepInEx));
                    created.ServerExePath = result.ServerExePath;
                    created.IsolatedInstall = true;
                    Logger.Information("Created isolated server '{0}': install={1}, world={2}, port={3}.",
                        name, result.InstallDirectory, world, gamePort);
                }
                else
                {
                    created.IsolatedInstall = false;
                }

                ServerPrefsProvider.SavePreferences(created);
                CurrentProfile = created.ProfileName;   // switch the UI to the new server
                PostEvent("servers.changed", BuildServersList());
                return created;
            });

            // Worlds sitting on disk that no profile currently owns, so a past realm can be
            // pulled back in on demand. Pull-based (this is only queried when the user opens the
            // restore panel - it never nags on startup). Newest-first; the same world name found
            // in several save folders folds to one entry (newest kept) with an olderCount hint.
            RegisterRpc("worlds.listOrphans", p =>
            {
                var owned = new HashSet<string>(
                    ServerPrefsProvider.LoadPreferences()
                        .Select(pr => pr.WorldName)
                        .Where(w => !string.IsNullOrWhiteSpace(w)),
                    StringComparer.OrdinalIgnoreCase);

                var found = new List<(string World, string Folder, string Sub, long SizeBytes, DateTime ModifiedUtc)>();
                foreach (var saveFolder in KnownSaveFolders())
                {
                    foreach (var sub in new[] { "worlds_local", "worlds" })
                    {
                        var dir = Path.Combine(saveFolder, sub);
                        if (!Directory.Exists(dir)) continue;

                        foreach (var fwl in Directory.GetFiles(dir, "*.fwl"))
                        {
                            var world = Path.GetFileNameWithoutExtension(fwl);
                            if (string.IsNullOrWhiteSpace(world) || owned.Contains(world)) continue;

                            // Automatic backup snapshots ("World_backup_auto-…") of a world some
                            // profile still owns aren't adoptable realms - hide them so a live
                            // server's snapshot trail doesn't flood the restore panel.
                            var backupIdx = world.IndexOf("_backup_", StringComparison.OrdinalIgnoreCase);
                            if (backupIdx > 0 && owned.Contains(world.Substring(0, backupIdx))) continue;

                            var db = Path.Combine(dir, world + ".db");
                            long size = 0;
                            try { size += new FileInfo(fwl).Length; } catch { /* ignore */ }
                            try { if (File.Exists(db)) size += new FileInfo(db).Length; } catch { /* ignore */ }
                            DateTime modified;
                            try { modified = File.GetLastWriteTimeUtc(File.Exists(db) ? db : fwl); }
                            catch { modified = DateTime.MinValue; }

                            found.Add((world, saveFolder, sub, size, modified));
                        }
                    }
                }

                var folded = found
                    .GroupBy(o => o.World, StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var newest = g.OrderByDescending(o => o.ModifiedUtc).First();
                        return new
                        {
                            world = newest.World,
                            folder = newest.Folder,
                            sub = newest.Sub,
                            sizeBytes = newest.SizeBytes,
                            modifiedUtc = newest.ModifiedUtc,
                            olderCount = g.Count() - 1,
                        };
                    })
                    .OrderByDescending(o => o.modifiedUtc)
                    .ToList();

                return Task.FromResult<object>(folded);
            });

            // Pulls an orphan world back in as its own isolated server: creates a distinct profile,
            // COPIES the world files into a fresh isolated save folder (the original files are left
            // untouched, so the same world can be adopted again or recovered), and provisions an
            // isolated install so this realm's mods stay distinct and can never contaminate another
            // server. Mods are seeded from the active server when seedMods is set, else vanilla.
            RegisterRpc("servers.adoptWorld", async p =>
            {
                var world = (p.Value<string>("world") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(world))
                    throw new ArgumentException("A world is required.");

                var sourceFolder = p.Value<string>("folder");
                var sub = p.Value<string>("sub");
                if (string.IsNullOrWhiteSpace(sub)) sub = "worlds_local";

                // Fail loudly BEFORE creating anything: a missing source must never produce a
                // profile that claims the world while zero files were actually copied.
                var srcWorldsDir = string.IsNullOrWhiteSpace(sourceFolder) ? null : Path.Combine(sourceFolder, sub);
                if (srcWorldsDir == null || !Directory.Exists(srcWorldsDir)
                    || !File.Exists(Path.Combine(srcWorldsDir, world + ".fwl")))
                    throw new ArgumentException(
                        $"World '{world}' was not found under '{sourceFolder ?? "<null>"}\\{sub}' - nothing was adopted.");

                var name = (p.Value<string>("name") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) name = world;

                if (ServerPrefsProvider.LoadPreferences()
                        .Any(x => string.Equals(x.ProfileName, name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"A server named '{name}' already exists. Pick a different name.");

                var seedMods = p.Value<bool?>("seedMods") ?? true;
                var (suggestedGame, suggestedRcon) = SuggestFreePorts();
                var gamePort = p.Value<int?>("port") ?? suggestedGame;
                var rconPort = p.Value<int?>("rconPort") ?? suggestedRcon;

                // Seed from the active profile for sane defaults, then stamp a distinct identity.
                var basePrefs = ServerPrefsProvider.LoadPreferences(ActiveProfileName) ?? new ServerPreferences();
                var created = ServerPreferences.FromFile(basePrefs.ToFile());
                created.ProfileName = name;
                created.Name = name;   // its own in-game name, never the base server's
                created.WorldName = world;
                created.Port = gamePort;
                created.RconPort = rconPort;
                created.AutoStart = false;
                created.Archived = false;
                created.LastSaved = DateTime.UtcNow;

                // Own save folder; copy the world files into it (leaving the source in place).
                var saveFolder = MakeIsolatedSaveFolder(name);
                created.SaveDataFolderPath = saveFolder;

                var baseExe = GetCanonicalBaseServerExe();
                var seedSourceBepInEx = GetActiveBepInExDir();
                await Task.Run(() =>
                {
                    CopyWorldFiles(sourceFolder, sub, world, saveFolder);
                    var result = InstallIsolation.ProvisionInstall(baseExe, name, seedMods, seedSourceBepInEx);
                    created.ServerExePath = result.ServerExePath;
                });
                created.IsolatedInstall = true;

                ServerPrefsProvider.SavePreferences(created);
                CurrentProfile = created.ProfileName;
                Logger.Information("Adopted orphan world '{0}' as server '{1}' (port {2}).", world, name, gamePort);
                PostEvent("servers.changed", BuildServersList());
                return created;
            });

            // Non-throwing collision check that powers the live warnings in the World/Network
            // halls: given this realm's current (possibly-unsaved) world/ports/install/save
            // settings, reports every overlap with ANOTHER profile so a conflict is visible
            // while editing - long before it would hard-fail at launch (EnsureNoServerCollisions).
            RegisterRpc("servers.checkCollision", p =>
            {
                var self = (p.Value<string>("profile") ?? CurrentProfile ?? "").Trim();
                var selfPrefs = string.IsNullOrWhiteSpace(self) ? null : ServerPrefsProvider.LoadPreferences(self);
                var userSave = UserPrefsProvider.LoadPreferences().SaveDataFolderPath;
                string EffectiveSave(ServerPreferences pr) =>
                    !string.IsNullOrWhiteSpace(pr?.SaveDataFolderPath) ? pr.SaveDataFolderPath : userSave;

                var port = p.Value<int?>("port") ?? selfPrefs?.Port ?? 0;
                var rconEnabled = p.Value<bool?>("rconEnabled") ?? selfPrefs?.RconEnabled ?? false;
                var rconPort = p.Value<int?>("rconPort") ?? selfPrefs?.RconPort ?? 0;
                var world = (p.Value<string>("world") ?? selfPrefs?.WorldName ?? "").Trim();
                var exe = p.Value<string>("exePath") ?? GetServerExePathFor(self);
                var save = p.Value<string>("saveFolder") ?? EffectiveSave(selfPrefs);

                var warnings = new List<object>();
                foreach (var other in ServerPrefsProvider.LoadPreferences())
                {
                    if (string.Equals(other.ProfileName, self, StringComparison.OrdinalIgnoreCase)) continue;
                    if (other.Archived) continue;

                    if (port > 0 && Math.Abs(port - other.Port) <= 1)
                        warnings.Add(new { kind = "port", other = other.ProfileName,
                            message = $"Game port {port} overlaps '{other.ProfileName}' (uses {other.Port}-{other.Port + 1}). Space game ports at least 2 apart." });

                    if (rconEnabled && other.RconEnabled && rconPort > 0 && rconPort == other.RconPort)
                        warnings.Add(new { kind = "rcon", other = other.ProfileName,
                            message = $"RCON port {rconPort} is also used by '{other.ProfileName}'." });

                    if (SamePath(exe, other.ServerExePath))
                        warnings.Add(new { kind = "install", other = other.ProfileName,
                            message = $"Shares its install (valheim_server.exe) with '{other.ProfileName}' - their mods and configs would collide. Give this realm its own install." });

                    if (!string.IsNullOrWhiteSpace(world)
                        && string.Equals(world, other.WorldName, StringComparison.OrdinalIgnoreCase)
                        && SamePath(save ?? "", EffectiveSave(other) ?? ""))
                        warnings.Add(new { kind = "world", other = other.ProfileName,
                            message = $"World '{world}' sits in the same save folder as '{other.ProfileName}' - both can't load it at once." });
                }
                return Task.FromResult<object>(warnings);
            });

            // --- User preferences ---
            RegisterRpc("userprefs.get", p => Task.FromResult<object>(BuildUserPrefsDto()));

            RegisterRpc("userprefs.save", p =>
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                var dto = p["prefs"] as JObject ?? throw new ArgumentException("prefs is required");

                // Only apply keys the client sent, so partial updates never clobber other settings.
                void Apply(string key, Action<JToken> setter)
                {
                    if (dto.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var v)) setter(v);
                }

                Apply("ServerExePath", v => prefs.ServerExePath = v.Value<string>());
                Apply("SaveDataFolderPath", v => prefs.SaveDataFolderPath = v.Value<string>());
                Apply("CheckForUpdates", v => prefs.CheckForUpdates = v.Value<bool>());
                Apply("AutoUpdateMods", v => prefs.AutoUpdateMods = v.Value<bool>());
                Apply("AutoUpdateBakaLoader", v => prefs.AutoUpdateBakaLoader = v.Value<bool>());
                Apply("StartWithWindows", v => prefs.StartWithWindows = v.Value<bool>());
                Apply("ShareAnonymousStats", v => prefs.ShareAnonymousStats = v.Value<bool>());
                Apply("StartMinimized", v => prefs.StartMinimized = v.Value<bool>());
                Apply("SaveProfileOnStart", v => prefs.SaveProfileOnStart = v.Value<bool>());
                Apply("WriteApplicationLogsToFile", v => prefs.WriteApplicationLogsToFile = v.Value<bool>());
                Apply("LogsFolderPath", v =>
                {
                    // Blank = back to the default folder. A custom path must be
                    // rooted (no relative surprises) and creatable, or the save fails loud.
                    var path = v.Value<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        prefs.LogsFolderPath = null;
                        return;
                    }
                    var expanded = Environment.ExpandEnvironmentVariables(path);
                    if (!Path.IsPathRooted(expanded))
                        throw new ArgumentException("The logs folder must be a full path (e.g. D:\\ValheimLogs).");
                    Directory.CreateDirectory(expanded); // throws if unusable
                    prefs.LogsFolderPath = path;
                });
                Apply("EnablePasswordValidation", v => prefs.EnablePasswordValidation = v.Value<bool>());
                Apply("DarkMode", v => prefs.DarkMode = v.Value<bool>());
                Apply("PlainTerminology", v => prefs.PlainTerminology = v.Value<bool>());
                Apply("DiscordWebhookUrl", v => prefs.DiscordWebhookUrl = v.Value<string>());
                Apply("DiscordWebhookThreadId", v => prefs.DiscordWebhookThreadId = v.Value<string>());
                Apply("DiscordSharingEnabled", v => prefs.DiscordSharingEnabled = v.Value<bool>());
                Apply("DiscordShareAddress", v => prefs.DiscordShareAddress = v.Value<bool>());
                Apply("DiscordSharePassword", v => prefs.DiscordSharePassword = v.Value<bool>());
                Apply("DiscordEventPosts", v => prefs.DiscordEventPosts = v.Value<bool>());
                Apply("CustomJoinDomain", v => prefs.CustomJoinDomain = v.Value<string>()?.Trim());

                UserPrefsProvider.SavePreferences(prefs);

                // A Discord-affecting save should refresh the status post right away
                // (e.g. share-password toggled -> the field appears/disappears; the
                // custom join domain changes the address line in the post).
                if (dto.Properties().Any(prop => prop.Name.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)
                    || prop.Name.Equals("CustomJoinDomain", StringComparison.OrdinalIgnoreCase)))
                {
                    DiscordStatus.RequestUpdate();
                }

                // When the "start with Windows" toggle was part of this save, mirror it into the
                // Windows Run registry key so the choice actually takes effect (mirrors MainWindow).
                if (dto.TryGetValue("StartWithWindows", StringComparison.OrdinalIgnoreCase, out _))
                {
                    try { StartupHelper.ApplyStartupSetting(prefs.StartWithWindows, Logger); }
                    catch (Exception e) { AppLogger.Error(e, "Failed to apply the 'start with Windows' setting."); }
                }

                return Task.FromResult<object>(BuildUserPrefsDto());
            });

            // --- Discord (the Herald) ---
            // Checks a pasted webhook URL is real by GETting its metadata - sends nothing.
            RegisterRpc("discord.validate", async p =>
            {
                var result = await DiscordStatus.ValidateWebhookAsync(p.Value<string>("url"));
                return new { ok = result.Ok, error = result.Error, name = result.Detail };
            });

            // Creates the status post if none exists yet, otherwise edits it in place.
            RegisterRpc("discord.publish", async p =>
            {
                var result = await DiscordStatus.PublishNowAsync();
                return new { ok = result.Ok, error = result.Error, detail = result.Detail };
            });

            // Deletes the status post from the channel and forgets its id.
            RegisterRpc("discord.remove", async p =>
            {
                var result = await DiscordStatus.RemoveStatusMessageAsync();
                return new { ok = result.Ok, error = result.Error };
            });

            // --- Custom join domain (the Waystone) ---
            // Resolves a hostname through real DNS and compares it to the server's public IP,
            // so the wizard can prove the A record points home before the domain is trusted.
            RegisterRpc("domain.check", async p =>
            {
                var domain = (p.Value<string>("domain") ?? "").Trim().TrimEnd('.');

                // Bare hostname only - no scheme, no port, no path.
                if (domain.Length == 0 || domain.Length > 253
                    || Uri.CheckHostName(domain) != UriHostNameType.Dns
                    || !domain.Contains('.'))
                {
                    return new { ok = false, error = "not a valid hostname", ips = Array.Empty<string>(), publicIp = IpAddressProvider.ExternalIpAddress, match = false };
                }

                try
                {
                    var resolveTask = System.Net.Dns.GetHostAddressesAsync(domain);
                    var winner = await Task.WhenAny(resolveTask, Task.Delay(TimeSpan.FromSeconds(6)));
                    if (winner != resolveTask)
                    {
                        return new { ok = false, error = "DNS lookup timed out", ips = Array.Empty<string>(), publicIp = IpAddressProvider.ExternalIpAddress, match = false };
                    }

                    var ips = (await resolveTask)
                        .Select(a => a.ToString())
                        .Distinct()
                        .ToArray();
                    var publicIp = IpAddressProvider.ExternalIpAddress;
                    var match = !string.IsNullOrWhiteSpace(publicIp)
                        && ips.Contains(publicIp, StringComparer.OrdinalIgnoreCase);

                    return new
                    {
                        ok = ips.Length > 0,
                        error = ips.Length > 0 ? null : "the name resolves to nothing",
                        ips,
                        publicIp,
                        match,
                    };
                }
                catch (Exception e)
                {
                    AppLogger.Debug("domain.check failed for {Domain}: {Error}", domain, e.Message);
                    return new { ok = false, error = "the name does not resolve yet", ips = Array.Empty<string>(), publicIp = IpAddressProvider.ExternalIpAddress, match = false };
                }
            });

            // --- Worlds ---
            RegisterRpc("worlds.list", p =>
            {
                var options = new ValheimServerOptions
                {
                    SaveDataFolderPath = ResolveSaveDataFolder(p.Value<string>("saveDataFolderPath")),
                };
                return Task.FromResult<object>(options.GetValidatedSaveDataFolder().GetWorldNames());
            });

            RegisterRpc("world.info", p =>
            {
                // On-disk detail for one world: primary db/fwl files (+ .old variants) and
                // automatic backups, sourced from both worlds/ and worlds_local/ subfolders.
                var world = p.Value<string>("world");
                if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("world is required");

                var saveFolder = new ValheimServerOptions
                {
                    SaveDataFolderPath = ResolveSaveDataFolder(null),
                }.GetValidatedSaveDataFolder();

                var files = new List<object>();
                var backups = new List<object>();

                foreach (var sub in new[] { "worlds_local", "worlds" })
                {
                    var dir = Path.Join(saveFolder.FullName, sub);
                    if (!Directory.Exists(dir)) continue;

                    foreach (var f in new DirectoryInfo(dir).GetFiles($"{world}*"))
                    {
                        var name = f.Name;
                        var dto = new { name = $"{sub}/{name}", sizeBytes = f.Length, modifiedUtc = f.LastWriteTimeUtc };

                        // Precise name matching so "MyWorld" doesn't swallow "MyWorld2" files.
                        if (name == $"{world}.db" || name == $"{world}.fwl"
                            || name == $"{world}.db.old" || name == $"{world}.fwl.old")
                        {
                            files.Add(dto);
                        }
                        else if (name.StartsWith($"{world}_backup_", StringComparison.OrdinalIgnoreCase))
                        {
                            backups.Add(dto);
                        }
                    }
                }

                return Task.FromResult<object>(new
                {
                    world,
                    folder = saveFolder.FullName,
                    files,
                    backups,
                });
            });

            // --- The Barrow: layered per-world backup manager ---

            // Every world on disk (across the shared save folder AND all per-server isolated
            // folders) with its full layer stack: automatic snapshots, the game's last-known-good
            // .old pair, and the safety copies BakaLoader takes before every restore.
            RegisterRpc("backups.overview", p =>
            {
                var profiles = ServerPrefsProvider.LoadPreferences();
                var groups = new List<(DateTime modified, object dto)>();

                foreach (var saveFolder in KnownSaveFolders())
                {
                    foreach (var sub in new[] { "worlds_local", "worlds" })
                    {
                        var dir = Path.Combine(saveFolder, sub);
                        if (!Directory.Exists(dir)) continue;

                        string[] fwls;
                        try { fwls = Directory.GetFiles(dir, "*.fwl"); }
                        catch { continue; }

                        foreach (var fwl in fwls)
                        {
                            var world = Path.GetFileNameWithoutExtension(fwl);
                            // Backup layers are folded under their live world, never listed as worlds.
                            if (string.IsNullOrWhiteSpace(world)
                                || world.IndexOf("_backup_", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                            var owner = profiles.FirstOrDefault(pr =>
                                string.Equals(pr.WorldName, world, StringComparison.OrdinalIgnoreCase)
                                && SameFolder(pr.SaveDataFolderPath, saveFolder));
                            var running = owner != null
                                && Sessions.TryGetValue(owner.ProfileName, out var session)
                                && session.Server.Status != ServerStatus.Stopped;

                            long liveSize = 0;
                            try { liveSize += new FileInfo(fwl).Length; } catch { /* ignore */ }
                            var liveDb = Path.Combine(dir, world + ".db");
                            try { if (File.Exists(liveDb)) liveSize += new FileInfo(liveDb).Length; } catch { /* ignore */ }
                            DateTime liveModified;
                            try { liveModified = File.GetLastWriteTimeUtc(File.Exists(liveDb) ? liveDb : fwl); }
                            catch { liveModified = DateTime.MinValue; }

                            var layers = new List<(DateTime modified, object dto)>();
                            long backupBytes = 0;
                            void AddLayer(string fwlPath, string dbPath, string kind)
                            {
                                if (!File.Exists(fwlPath)) return;
                                long size = 0;
                                try { size += new FileInfo(fwlPath).Length; } catch { /* ignore */ }
                                var hasDb = File.Exists(dbPath);
                                try { if (hasDb) size += new FileInfo(dbPath).Length; } catch { /* ignore */ }
                                DateTime modified;
                                try { modified = File.GetLastWriteTimeUtc(hasDb ? dbPath : fwlPath); }
                                catch { modified = DateTime.MinValue; }
                                backupBytes += size;
                                layers.Add((modified, new
                                {
                                    file = Path.GetFileName(fwlPath),
                                    kind,
                                    sizeBytes = size,
                                    modifiedUtc = modified,
                                    day = TryReadWorldDay(dbPath),
                                    hasDb,
                                }));
                            }

                            string[] snapshots;
                            try { snapshots = Directory.GetFiles(dir, world + "_backup_*.fwl"); }
                            catch { snapshots = Array.Empty<string>(); }
                            foreach (var snap in snapshots)
                            {
                                // Precise prefix match so "MyWorld" never swallows "MyWorld2" snapshots.
                                var snapName = Path.GetFileName(snap);
                                if (!snapName.StartsWith(world + "_backup_", StringComparison.OrdinalIgnoreCase)) continue;
                                var stem = Path.Combine(dir, Path.GetFileNameWithoutExtension(snap));
                                var kind = snapName.IndexOf("_backup_auto-", StringComparison.OrdinalIgnoreCase) >= 0 ? "auto"
                                    : snapName.IndexOf("_backup_restore-", StringComparison.OrdinalIgnoreCase) >= 0 ? "restore"
                                    : "other";
                                AddLayer(snap, stem + ".db", kind);
                            }
                            AddLayer(Path.Combine(dir, world + ".fwl.old"), Path.Combine(dir, world + ".db.old"), "old");

                            groups.Add((liveModified, new
                            {
                                world,
                                folder = saveFolder,
                                sub,
                                owner = owner?.ProfileName,
                                running,
                                sizeBytes = liveSize,
                                modifiedUtc = liveModified,
                                day = TryReadWorldDay(liveDb),
                                backups = layers
                                    .OrderByDescending(l => l.modified)
                                    .Select(l => l.dto)
                                    .ToList(),
                                backupBytes,
                            }));
                        }
                    }
                }

                return Task.FromResult<object>(groups
                    .OrderByDescending(g => g.modified)
                    .Select(g => g.dto)
                    .ToList());
            });

            // Restores one backup layer over the live world pair - but FIRST lays down a
            // safety copy of the current live files ("{world}_backup_restore-<ts>"), so a
            // restore is always reversible from the Barrow itself. Refuses while any server
            // in that save folder is running the world (the game would clobber the files).
            RegisterRpc("backups.restore", p =>
            {
                var (dir, world, file) = ValidateBackupRef(p, requireBackupShape: true);

                foreach (var pr in ServerPrefsProvider.LoadPreferences())
                {
                    if (!string.Equals(pr.WorldName, world, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Sessions.TryGetValue(pr.ProfileName, out var session)
                        && session.Server.Status != ServerStatus.Stopped)
                        throw new InvalidOperationException(
                            $"'{pr.ProfileName}' is running world '{world}' right now - stop it before restoring a backup.");
                }

                var liveFwl = Path.Combine(dir, world + ".fwl");
                var liveDb = Path.Combine(dir, world + ".db");

                // Safety layer first: snapshot whatever is live right now.
                string snapshot = null;
                if (File.Exists(liveFwl))
                {
                    var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    snapshot = $"{world}_backup_restore-{ts}";
                    File.Copy(liveFwl, Path.Combine(dir, snapshot + ".fwl"), overwrite: true);
                    if (File.Exists(liveDb)) File.Copy(liveDb, Path.Combine(dir, snapshot + ".db"), overwrite: true);
                }

                // The layer's paired .db: same stem for snapshots, ".db.old" for the .old pair.
                var srcFwl = Path.Combine(dir, file);
                var srcDb = file.EndsWith(".fwl.old", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dir, world + ".db.old")
                    : Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".db");

                File.Copy(srcFwl, liveFwl, overwrite: true);
                var restoredDb = File.Exists(srcDb);
                if (restoredDb) File.Copy(srcDb, liveDb, overwrite: true);

                Logger.Information("Barrow restore: '{0}' <- '{1}' (db: {2}; safety copy: {3}).",
                    world, file, restoredDb, snapshot ?? "none");
                return Task.FromResult<object>(new
                {
                    ok = true,
                    snapshot,
                    restoredDb,
                    day = TryReadWorldDay(restoredDb ? liveDb : null),
                });
            });

            // Deletes one backup layer (its .fwl + paired .db). The live pair can never be
            // named here - ValidateBackupRef only accepts backup-shaped filenames.
            RegisterRpc("backups.delete", p =>
            {
                var (dir, world, file) = ValidateBackupRef(p, requireBackupShape: true);

                var deleted = new List<string>();
                var fwlPath = Path.Combine(dir, file);
                var dbPath = file.EndsWith(".fwl.old", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dir, world + ".db.old")
                    : Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".db");

                if (File.Exists(fwlPath)) { File.Delete(fwlPath); deleted.Add(Path.GetFileName(fwlPath)); }
                if (File.Exists(dbPath)) { File.Delete(dbPath); deleted.Add(Path.GetFileName(dbPath)); }

                Logger.Information("Barrow delete: world '{0}' layer '{1}' ({2} file(s)).", world, file, deleted.Count);
                return Task.FromResult<object>(new { deleted });
            });

            // --- The Skald (analytics) ---
            // Aggregates the local analytics journal for one server profile: playtime per
            // player (join->leave pairing), uptime (start->stop/crash pairing), deaths, a
            // recent-happenings feed, and the mod update history. Computed entirely from
            // the append-only local journal - nothing here touches the network.
            RegisterRpc("analytics.overview", p =>
            {
                var profile = p.Value<string>("profile");
                if (string.IsNullOrWhiteSpace(profile)) profile = ActiveProfileName;

                var running = Sessions.TryGetValue(profile, out var session)
                    && session.Server.Status == ServerStatus.Running;

                var events = Analytics.EventsFor(profile);
                var now = DateTime.UtcNow;

                // When the app was closed mid-session the journal holds open spans with
                // no closing event. Close those at the LAST journaled moment rather than
                // "now" so dead time between app runs doesn't inflate the totals.
                var lastEventTime = events.Count > 0 ? events[^1].TimeUtc : now;

                // -- Uptime: "start" opens a span; "stop"/"crash" closes it.
                double uptimeSec = 0, currentUptimeSec = 0;
                DateTime? upSince = null;
                int starts = 0, crashes = 0;
                foreach (var e in events)
                {
                    switch (e.Kind)
                    {
                        case "start":
                            starts++;
                            // Two starts with no stop between them = the earlier session's
                            // end never got journaled; close it at the second start.
                            if (upSince.HasValue) uptimeSec += Math.Max(0, (e.TimeUtc - upSince.Value).TotalSeconds);
                            upSince = e.TimeUtc;
                            break;
                        case "crash":
                            crashes++;
                            goto case "stop";
                        case "stop":
                            if (upSince.HasValue)
                            {
                                uptimeSec += Math.Max(0, (e.TimeUtc - upSince.Value).TotalSeconds);
                                upSince = null;
                            }
                            break;
                    }
                }
                if (upSince.HasValue)
                {
                    var end = running ? now : lastEventTime;
                    var span = Math.Max(0, (end - upSince.Value).TotalSeconds);
                    uptimeSec += span;
                    if (running) currentUptimeSec = span;
                }

                // -- Players: pair join->leave per player; stop/crash closes everyone.
                var players = new Dictionary<string, SkaldPlayerAgg>(StringComparer.OrdinalIgnoreCase);

                SkaldPlayerAgg AggFor(AnalyticsEvent e)
                {
                    var key = e.PlayerKey;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        // Death events can lack a player key when the character couldn't
                        // be tied to a known player - fold into a character-name match.
                        var byCharacter = players.Values.FirstOrDefault(a =>
                            !string.IsNullOrWhiteSpace(e.Character)
                            && string.Equals(a.Character, e.Character, StringComparison.OrdinalIgnoreCase));
                        if (byCharacter != null) return byCharacter;
                        key = e.PlayerName ?? e.Character ?? "unknown";
                    }
                    if (!players.TryGetValue(key, out var agg))
                    {
                        agg = new SkaldPlayerAgg { Key = key };
                        players[key] = agg;
                    }
                    if (!string.IsNullOrWhiteSpace(e.PlayerName)) agg.Name = e.PlayerName;
                    if (!string.IsNullOrWhiteSpace(e.Character)) agg.Character = e.Character;
                    return agg;
                }

                foreach (var e in events)
                {
                    switch (e.Kind)
                    {
                        case "join":
                        {
                            var a = AggFor(e);
                            a.OpenSince ??= e.TimeUtc;
                            a.Sessions++;
                            a.LastSeen = e.TimeUtc;
                            break;
                        }
                        case "leave":
                        {
                            var a = AggFor(e);
                            if (a.OpenSince.HasValue)
                            {
                                a.PlaySec += Math.Max(0, (e.TimeUtc - a.OpenSince.Value).TotalSeconds);
                                a.OpenSince = null;
                            }
                            a.LastSeen = e.TimeUtc;
                            break;
                        }
                        case "death":
                        {
                            var a = AggFor(e);
                            a.Deaths++;
                            a.LastSeen = e.TimeUtc;
                            break;
                        }
                        case "stop":
                        case "crash":
                            foreach (var a in players.Values)
                            {
                                if (!a.OpenSince.HasValue) continue;
                                a.PlaySec += Math.Max(0, (e.TimeUtc - a.OpenSince.Value).TotalSeconds);
                                a.OpenSince = null;
                            }
                            break;
                    }
                }
                foreach (var a in players.Values)
                {
                    if (!a.OpenSince.HasValue) continue;
                    var end = running ? now : lastEventTime;
                    a.PlaySec += Math.Max(0, (end - a.OpenSince.Value).TotalSeconds);
                    a.OnlineNow = running;
                    a.OpenSince = null;
                }

                var feedKinds = new HashSet<string> { "join", "leave", "death", "start", "stop", "crash" };
                var feed = events
                    .Where(e => feedKinds.Contains(e.Kind))
                    .OrderByDescending(e => e.TimeUtc)
                    .Take(120)
                    .Select(e => new { t = e.TimeUtc, kind = e.Kind, name = e.PlayerName, character = e.Character })
                    .ToList();

                var mods = events
                    .Where(e => e.Kind == "modup" || e.Kind == "modin")
                    .OrderByDescending(e => e.TimeUtc)
                    .Take(60)
                    .Select(e => new { t = e.TimeUtc, kind = e.Kind, mod = e.Mod, from = e.FromVersion, to = e.ToVersion })
                    .ToList();

                return Task.FromResult<object>(new
                {
                    profile,
                    running,
                    since = events.Count > 0 ? (DateTime?)events[0].TimeUtc : null,
                    eventCount = events.Count,
                    uptime = new
                    {
                        totalSec = Math.Round(uptimeSec),
                        currentSec = Math.Round(currentUptimeSec),
                        starts,
                        crashes,
                    },
                    players = players.Values
                        .OrderByDescending(a => a.OnlineNow)
                        .ThenByDescending(a => a.PlaySec)
                        .Select(a => new
                        {
                            key = a.Key,
                            name = a.Name ?? a.Character ?? a.Key,
                            character = a.Character,
                            playSec = Math.Round(a.PlaySec),
                            sessions = a.Sessions,
                            deaths = a.Deaths,
                            lastSeen = a.LastSeen,
                            online = a.OnlineNow,
                        })
                        .ToList(),
                    totals = new
                    {
                        playSec = Math.Round(players.Values.Sum(a => a.PlaySec)),
                        deaths = players.Values.Sum(a => a.Deaths),
                        sessions = players.Values.Sum(a => a.Sessions),
                        modUpdates = events.Count(e => e.Kind == "modup"),
                        modInstalls = events.Count(e => e.Kind == "modin"),
                    },
                    feed,
                    mods,
                });
            });

            // World identity from the .fwl (read-only): the typed seed string and the
            // numeric seed the game derived from it. exists=false means the world has
            // no .fwl yet (it will be generated - with a random seed - on first launch).
            RegisterRpc("world.seed", p =>
            {
                var world = p.Value<string>("world");
                if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("world is required");

                var saveFolder = ResolveSaveDataFolder(null);
                var info = FwlReader.TryReadWorld(saveFolder, world);
                return Task.FromResult<object>(new
                {
                    world,
                    exists = info != null,
                    seedName = info?.SeedName ?? "",
                    seed = info?.Seed ?? 0,
                    worldVersion = info?.WorldVersion ?? 0,
                });
            });

            // Chooses the seed for a world that does NOT exist yet by pre-writing its
            // .fwl (the server adopts it on first launch). FwlWriter hard-refuses when
            // the world already exists - a created world's seed is immutable.
            RegisterRpc("world.setSeed", p =>
            {
                var world = p.Value<string>("world");
                if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("world is required");
                var seedName = (p.Value<string>("seedName") ?? "").Trim();
                if (seedName.Length == 0) throw new ArgumentException("seedName is required");

                var saveFolder = ResolveSaveDataFolder(null);
                if (string.IsNullOrWhiteSpace(saveFolder))
                    throw new InvalidOperationException("No save folder is configured, so the world seed can't be applied.");

                var written = FwlWriter.WriteNewWorld(saveFolder, world, seedName);
                Logger.Information("Pre-created world '{0}' with seed '{1}' ({2}).",
                    world, written.SeedName, written.Seed);
                return Task.FromResult<object>(new
                {
                    world,
                    exists = true,
                    seedName = written.SeedName,
                    seed = written.Seed,
                });
            });

            RegisterRpc("worldgen.get", p =>
            {
                // Saved world-generation dials for one world (empty defaults when unset).
                var world = p.Value<string>("world");
                if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("world is required");

                var prefs = WorldPrefsProvider.LoadPreferences(world);
                return Task.FromResult<object>(new
                {
                    world,
                    preset = prefs?.Preset ?? "",
                    modifiers = prefs?.Modifiers ?? new Dictionary<string, string>(),
                });
            });

            RegisterRpc("worldgen.save", p =>
            {
                var world = p.Value<string>("world");
                if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("world is required");

                // Every dial is validated against the game's own vocabulary; a missing,
                // empty, or "normal" value means "game default" and drops the key so no
                // -modifier arg is emitted for it.
                var modifiers = new Dictionary<string, string>();
                foreach (var prop in (p["modifiers"] as JObject) ?? new JObject())
                {
                    var value = prop.Value?.Value<string>();
                    if (string.IsNullOrWhiteSpace(value) || value == "normal") continue;
                    if (!WorldGen.Modifiers.TryGetValue(prop.Key, out var allowed) || !allowed.Contains(value))
                        throw new ArgumentException($"'{value}' is not a valid {prop.Key} setting");
                    modifiers[prop.Key] = value;
                }

                var prefs = WorldPrefsProvider.LoadPreferences(world) ?? new WorldPreferences { WorldName = world };
                prefs.Preset = null; // individual dials replace any preset (mutually exclusive)
                prefs.Modifiers = modifiers;
                WorldPrefsProvider.SavePreferences(prefs);

                return Task.FromResult<object>(new { world, preset = "", modifiers });
            });

            // Renders the Atlas biome/terrain map for a world's seed to a cached PNG
            // served via the atlas.baka virtual host. Mod-free: the map is computed
            // from our own WorldGenerator port, adapted to Expand World Size configs
            // where detected (other worldgen mods surface as honest warnings).
            RegisterRpc("atlas.render", async p =>
            {
                if (_atlasRenderInProgress)
                    throw new InvalidOperationException("A map render is already in progress.");
                _atlasRenderInProgress = true;
                try
                {
                    var world = p.Value<string>("world");
                    if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("world is required");
                    var size = Math.Clamp(p.Value<int?>("size") ?? 2048, 256, 4096);
                    var force = p.Value<bool?>("force") ?? false;

                    var info = FwlReader.TryReadWorld(ResolveSaveDataFolder(null), world);
                    if (info == null)
                        throw new InvalidOperationException(
                            $"World '{world}' has no .fwl yet - choose a seed or start the server once first.");

                    // Adapt to worldgen mods where possible; warn honestly where not.
                    var exePath = GetServerExePath();
                    var installDir = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
                    var compat = AtlasEngine.ModCompatScanner.Scan(installDir);

                    var compatKey = compat.HasExpandWorldSize
                        ? FormattableString.Invariant($"_ews{compat.WorldEdge:0}s{compat.WorldStretch:0.##}b{compat.BiomeStretch:0.##}")
                        : "";
                    var fileName = $"map_{info.Seed}_{size}{compatKey}.png";
                    var path = Path.Combine(GetAtlasCacheDir(), fileName);

                    if (force || !File.Exists(path))
                    {
                        var seed = info.Seed;
                        await Task.Run(() =>
                        {
                            var gen = new AtlasEngine.WorldGen(seed);
                            AtlasEngine.MapRenderer.RenderToPng(
                                gen, path, size,
                                pct => PostEvent("atlas.renderProgress", new { world, pct }),
                                default, compat);
                        });
                    }

                    return (object)new
                    {
                        world,
                        seed = info.Seed,
                        seedName = info.SeedName,
                        url = $"https://{AtlasVirtualHost}/{fileName}",
                        sizePx = size,
                        radius = compat.WorldRadius,
                        edge = compat.WorldEdge,
                        warnings = compat.Warnings,
                    };
                }
                finally
                {
                    _atlasRenderInProgress = false;
                }
            });

            // Parses the world's .db save (read-only, share-friendly) into a live
            // snapshot for the Atlas hall: day/clock, tagged portals, placed POIs,
            // build sites, and the cartography tables' combined fog of war (baked
            // into a mask PNG served via atlas.baka). Mod-free: everything comes
            // from vanilla save data.
            RegisterRpc("atlas.worldInfo", async p =>
            {
                if (_atlasInfoInProgress)
                    throw new InvalidOperationException("A world scan is already in progress.");
                _atlasInfoInProgress = true;
                try
                {
                    var world = p.Value<string>("world");
                    if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("world is required");

                    var fwl = FwlReader.FindWorldFwl(ResolveSaveDataFolder(null), world);
                    var dbPath = fwl == null ? null : Path.ChangeExtension(fwl, ".db");
                    if (dbPath == null || !File.Exists(dbPath))
                        return (object)new { world, hasDb = false };

                    var savedAtUtc = File.GetLastWriteTimeUtc(dbPath);
                    var db = await Task.Run(() => AtlasEngine.WorldDbReader.TryRead(dbPath));
                    if (db == null)
                        return (object)new { world, hasDb = false };

                    // Combined fog of war: OR every cartography table's explored bitmap.
                    AtlasEngine.SharedMapData shared = null;
                    foreach (var table in db.MapTables)
                    {
                        var decoded = AtlasEngine.SharedMapData.TryDecode(table.Data);
                        if (decoded == null) continue;
                        if (shared == null) shared = decoded;
                        else shared.MergeFrom(decoded);
                    }

                    string fogUrl = null;
                    double exploredPercent = 0;
                    var pins = new List<object>();
                    float fogExtent = AtlasEngine.SharedMapData.VanillaPixelSize * 2048f / 2f;
                    if (shared != null)
                    {
                        int exploredCount = 0;
                        for (int i = 0; i < shared.Explored.Length; i++)
                            if (shared.Explored[i]) exploredCount++;
                        exploredPercent = exploredCount * 100.0 / shared.Explored.Length;
                        fogExtent = shared.TextureSize * AtlasEngine.SharedMapData.VanillaPixelSize / 2f;

                        foreach (var pin in shared.Pins)
                            pins.Add(new { name = pin.Name, x = pin.X, z = pin.Z, type = pin.Type, done = pin.Checked });

                        // Bake the fog mask (transparent where explored, dark veil
                        // elsewhere), flipped so row 0 = north like the biome map.
                        // Keyed on the .db timestamp so a fresh save invalidates it.
                        var safeWorld = string.Concat(world.Split(Path.GetInvalidFileNameChars()));
                        var fogFile = $"fog_{safeWorld}_{savedAtUtc.Ticks}.png";
                        var fogPath = Path.Combine(GetAtlasCacheDir(), fogFile);
                        if (!File.Exists(fogPath))
                        {
                            var size = shared.TextureSize;
                            var mask = shared.Explored;
                            await Task.Run(() =>
                            {
                                var px = new int[size * size];
                                const int veil = unchecked((int)0xCC06070A);
                                for (int py = 0; py < size; py++)
                                {
                                    int src = py * size;
                                    int dst = (size - 1 - py) * size;
                                    for (int col = 0; col < size; col++)
                                        px[dst + col] = mask[src + col] ? 0 : veil;
                                }
                                AtlasEngine.MapRenderer.SavePng(px, size, fogPath);
                            });
                        }
                        fogUrl = $"https://{AtlasVirtualHost}/{fogFile}";
                    }

                    return (object)new
                    {
                        world,
                        hasDb = true,
                        worldVersion = db.WorldVersion,
                        day = db.DayNumber,
                        netTime = db.NetTime,
                        zdoCount = db.ZdoCount,
                        zones = db.GeneratedZoneCount,
                        globalKeys = db.GlobalKeys,
                        savedAtUtc = savedAtUtc.ToString("o"),
                        savedAgeSeconds = (DateTime.UtcNow - savedAtUtc).TotalSeconds,
                        portals = db.Portals
                            .Select(pt => new { tag = pt.Tag, x = pt.X, z = pt.Z })
                            .ToList(),
                        pois = db.Locations
                            .Where(l => l.Placed && AtlasPoiLabels.ContainsKey(l.Prefab))
                            .Select(l => new { prefab = l.Prefab, label = AtlasPoiLabels[l.Prefab], x = l.X, z = l.Z })
                            .ToList(),
                        builds = db.BuildClusters
                            .Select(b => new { x = b.CenterX, z = b.CenterZ, pieces = b.PieceCount, radius = b.RadiusMeters })
                            .ToList(),
                        mapTables = db.MapTables.Count,
                        hasSharedMap = shared != null,
                        exploredPercent,
                        pins,
                        fogUrl,
                        fogExtent,
                        eventName = db.EventName,
                        eventX = db.EventPosX,
                        eventZ = db.EventPosZ,
                    };
                }
                finally
                {
                    _atlasInfoInProgress = false;
                }
            });

            // --- Max players (bundled BakaLoaderMaxPlayers plugin cfg) ---
            RegisterRpc("maxplayers.get", p => Task.FromResult<object>(ReadMaxPlayers()));

            RegisterRpc("maxplayers.save", p =>
            {
                if (_maxPlayersSaveInProgress)
                    throw new InvalidOperationException("A max-players change is already in progress.");
                _maxPlayersSaveInProgress = true;
                try
                {
                    // 1-127: the plugin patches lobby-cap constants that are sbyte-sized in
                    // vanilla IL, so 127 stays the supported ceiling.
                    var count = Math.Clamp(p.Value<int?>("count") ?? 10, 1, 127);
                    var pluginsDir = GetPluginsDirectory();

                    // Opportunistic legacy migration + DLL refresh. No-op while the server
                    // runs (the old DLL is file-locked); PrepareCompanionPlugins retries
                    // at the next server start.
                    MaxPlayersInstaller.EnsureCurrent(pluginsDir);

                    var installed = MaxPlayersInstaller.IsInstalled(pluginsDir);
                    var legacy = MaxPlayersInstaller.IsLegacyInstalled(pluginsDir);

                    if (!installed && !legacy)
                    {
                        // Vanilla cap requested and no plugin present - nothing to do.
                        if (count <= 10)
                            return Task.FromResult<object>(new { count = 10, modInstalled = false });

                        MaxPlayersInstaller.Install(pluginsDir); // throws with a friendly message on failure
                        installed = true;
                    }

                    if (installed)
                        MaxPlayersInstaller.WriteConfiguredCount(GetConfigDirectory(), count);

                    // Legacy mod still on disk (migration deferred until the server stops):
                    // keep ITS cfg in sync too, so the chosen count applies no matter which
                    // plugin loads at the next start.
                    if (legacy) WriteLegacyMaxPlayersCfg(count);

                    return Task.FromResult<object>(
                        new { count, modInstalled = true, note = "Applies on the next server start." });
                }
                finally
                {
                    _maxPlayersSaveInProgress = false;
                }
            });

            // --- Hearth metrics (Forge Load card) ---
            RegisterRpc("metrics.get", p =>
            {
                var session = ActiveSession;
                try
                {
                    var proc = session.Server.GetTrackedProcess();
                    if (proc == null || proc.HasExited)
                    {
                        session.MetricsPid = 0;
                        return Task.FromResult<object>(new { running = false });
                    }

                    proc.Refresh();
                    var now = DateTime.UtcNow;
                    var cpuTime = proc.TotalProcessorTime;

                    // First sample (or a new PID after restart) has no delta - report 0%.
                    double cpu = 0;
                    if (session.MetricsPid == proc.Id && session.MetricsSampleTime != default)
                    {
                        var wallMs = (now - session.MetricsSampleTime).TotalMilliseconds;
                        if (wallMs > 0)
                            cpu = (cpuTime - session.MetricsCpuTime).TotalMilliseconds
                                  / wallMs / Environment.ProcessorCount * 100.0;
                    }
                    session.MetricsPid = proc.Id;
                    session.MetricsSampleTime = now;
                    session.MetricsCpuTime = cpuTime;

                    return Task.FromResult<object>(new
                    {
                        running = true,
                        cpu = Math.Clamp(Math.Round(cpu, 1), 0, 100),
                        ramBytes = proc.WorkingSet64,
                    });
                }
                catch
                {
                    // Process died between the null-check and the sample - stopped, not an error.
                    session.MetricsPid = 0;
                    return Task.FromResult<object>(new { running = false });
                }
            });

            // --- Server lifecycle ---
            RegisterRpc("server.state", p => Task.FromResult<object>(BuildServerState()));

            RegisterRpc("server.start", p =>
            {
                var prefs = (p["prefs"] ?? throw new ArgumentException("prefs is required"))
                    .ToObject<ServerPreferences>();
                var session = GetOrCreateSession(
                    string.IsNullOrWhiteSpace(prefs.ProfileName) ? CurrentProfile : prefs.ProfileName);
                var options = BuildServerOptions(prefs);
                EnsureNoServerCollisions(session, options);
                session.Server.Start(options);
                return Task.FromResult<object>(BuildServerState(session));
            });

            RegisterRpc("server.stop", p =>
            {
                Server.Stop();
                return Task.FromResult<object>(BuildServerState());
            });

            RegisterRpc("server.restart", async p =>
            {
                // Smart restart: countdown running -> bypass & restart NOW; players online ->
                // 1-minute warned countdown; empty server -> immediate restart, no broadcast.
                var restart = await Server.RequestSmartRestart();
                return new { restart, state = BuildServerState() };
            });

            RegisterRpc("server.broadcast", async p =>
            {
                var message = p.Value<string>("message");
                if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("message is required");
                return await Server.BroadcastNow(message);
            });

            RegisterRpc("server.command", async p =>
            {
                // Free-form console command over RCON (Saga terminal + Ctrl+K palette).
                // Unlike shell.* (closed keywords only), RCON is precisely the admin's own
                // console channel, so raw text is the intended contract. Guarded inside
                // SendRconCommandAsync: RCON enabled + server running, else null.
                var command = p.Value<string>("command");
                if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("command is required");
                var response = await Server.SendRconCommandAsync(command);
                return new { ok = response != null, response };
            });

            // --- Players ---
            RegisterRpc("players.list", p => Task.FromResult<object>(
                PlayerDataProvider.Data
                    .Where(pl => pl.ServerKey == null
                        || string.Equals(pl.ServerKey, ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                    .Select(BuildPlayerDto).ToList()));

            RegisterRpc("players.remove", p =>
            {
                var player = FindPlayer(p);
                PlayerDataProvider.Remove(player);
                return Task.FromResult<object>(true);
            });

            RegisterRpc("players.kick", async p => await Server.KickAsync(RequireTarget(p)));
            RegisterRpc("players.heal", async p => await Server.HealAsync(RequireTarget(p)));
            RegisterRpc("players.smite", async p => await Server.SmiteAsync(RequireTarget(p)));

            RegisterRpc("players.teleport", async p =>
            {
                var destination = p.Value<string>("destination");
                if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("destination is required");
                return await Server.TeleportAsync(RequireTarget(p), destination);
            });

            RegisterRpc("players.spawn", async p =>
            {
                var playerName = p.Value<string>("playerName");
                var prefab = p.Value<string>("prefab");
                var amount = p.Value<int?>("amount") ?? 1;
                var levelOrQuality = p.Value<int?>("levelOrQuality") ?? 0;

                var entry = ItemCatalog.Entries.FirstOrDefault(e =>
                        string.Equals(e.PrefabName, prefab, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown prefab '{prefab}'");

                return await Server.SpawnAtPlayerAsync(playerName, entry, amount, levelOrQuality);
            });

            RegisterRpc("players.isListed", p =>
            {
                var (list, id) = RequireListArgs(p);
                return Task.FromResult<object>(
                    PlayerListService.IsListed(ResolveSaveDataFolder(null), list, id));
            });

            RegisterRpc("players.setList", p =>
            {
                var (list, id) = RequireListArgs(p);
                var on = p.Value<bool?>("on") ?? throw new ArgumentException("on is required");
                var folder = ResolveSaveDataFolder(null);
                var changed = on
                    ? PlayerListService.AddToList(folder, list, id)
                    : PlayerListService.RemoveFromList(folder, list, id);
                return Task.FromResult<object>(changed);
            });

            // --- Item catalog (Spawn X at) ---
            RegisterRpc("items.search", p =>
            {
                var query = p.Value<string>("query") ?? "";
                var limit = p.Value<int?>("limit") ?? 500;

                EnsureItemCatalogLoaded();

                var results = ItemCatalog.Entries
                    .Where(e => string.IsNullOrWhiteSpace(query)
                        || e.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || e.PrefabName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(limit)
                    .Select(e => new
                    {
                        e.PrefabName,
                        e.Label,
                        category = e.Category.ToString(),
                        e.HasQuality,
                        e.HasLevel,
                    })
                    .ToList();

                return Task.FromResult<object>(new { results, loadedFrom = ItemCatalog.LoadedFrom });
            });

            // --- Mods ---
            RegisterRpc("mods.scan", async p =>
            {
                if (_modScanInProgress) throw new InvalidOperationException("A mod scan is already in progress");
                _modScanInProgress = true;
                try
                {
                    var pluginsDir = GetPluginsDirectory()
                        ?? throw new InvalidOperationException("Server exe path is not configured");
                    var mods = ModScanner.ScanPlugins(pluginsDir);

                    await Task.WhenAll(mods.Select(async mod =>
                    {
                        try
                        {
                            var package = await ThunderstoreClient.GetLatestAsync(mod.Author, mod.ModName);
                            mod.LatestVersion = package?.LatestVersion;
                        }
                        catch
                        {
                            // Leave LatestVersion null - the UI shows "-" for unknown.
                        }
                    }));

                    // Remember this realm's mod set so it's tracked per-profile and reconstructable
                    // on restore/export (kept distinct so servers never cross-contaminate).
                    RecordModManifest(CurrentProfile, mods);

                    return mods.Select(BuildModDto).ToList();
                }
                finally
                {
                    _modScanInProgress = false;
                }
            });

            RegisterRpc("mods.updateAll", async p =>
            {
                if (_modUpdateInProgress) throw new InvalidOperationException("A mod update is already in progress");
                _modUpdateInProgress = true;
                try
                {
                    var pluginsDir = GetPluginsDirectory()
                        ?? throw new InvalidOperationException("Server exe path is not configured");
                    var mods = ModScanner.ScanPlugins(pluginsDir);

                    await Task.WhenAll(mods.Select(async mod =>
                    {
                        try
                        {
                            var package = await ThunderstoreClient.GetLatestAsync(mod.Author, mod.ModName);
                            mod.LatestVersion = package?.LatestVersion;
                        }
                        catch { }
                    }));

                    var updatable = mods.Where(m => m.UpdateAvailable).ToList();
                    var results = await ModUpdateService.UpdateModsAsync(updatable);

                    RecordModUpdates(ActiveProfileName, results);

                    return results.Select(r => new
                    {
                        mod = r.Mod.FullName,
                        r.Updated,
                        r.FromVersion,
                        r.ToVersion,
                        r.Error,
                    }).ToList();
                }
                finally
                {
                    _modUpdateInProgress = false;
                }
            });

            RegisterRpc("mods.findConfigs", p =>
            {
                var mod = FindInstalledMod(p);
                return Task.FromResult<object>(ModRemovalService.FindConfigFiles(mod));
            });

            RegisterRpc("mods.remove", p =>
            {
                var mod = FindInstalledMod(p);
                var includeConfig = p.Value<bool?>("includeConfig") ?? false;
                var result = ModRemovalService.RemoveMod(mod, includeConfig);
                return Task.FromResult<object>(new
                {
                    mod = result.Mod.FullName,
                    result.Removed,
                    result.BackupDirectory,
                    result.DeletedConfigFiles,
                    result.Error,
                });
            });

            // Installs a mod from any pasted Thunderstore link (page URL, versions page,
            // direct download URL, or ror2mm:// mod-manager link). No version in the
            // link = latest. Structured errors come back in the result's Error field
            // so the UI can show them without a generic RPC-failure toast.
            RegisterRpc("mods.addFromUrl", async p =>
            {
                object FailDto(string error) => new
                {
                    Installed = false,
                    Replaced = false,
                    Owner = (string)null,
                    Name = (string)null,
                    Version = (string)null,
                    Error = error,
                };

                if (_modUpdateInProgress)
                    return FailDto("A mod update is already in progress - try again in a moment.");

                if (!ThunderstoreUrlParser.TryParse(p.Value<string>("url"), out var reference, out var parseError))
                    return FailDto(parseError);

                var pluginsDir = GetPluginsDirectory();
                if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
                    return FailDto("BepInEx plugins folder not found - set a valid server .exe path first.");

                _modUpdateInProgress = true;
                try
                {
                    var result = await ModUpdateService.InstallFromThunderstoreAsync(reference, pluginsDir);

                    if (result.Installed)
                    {
                        Analytics.Record(new AnalyticsEvent
                        {
                            Kind = "modin",
                            Server = ActiveProfileName,
                            Mod = $"{result.Owner}-{result.Name}",
                            ToVersion = result.Version,
                        });
                    }

                    return new
                    {
                        result.Installed,
                        result.Replaced,
                        result.Owner,
                        result.Name,
                        result.Version,
                        result.Error,
                    };
                }
                finally
                {
                    _modUpdateInProgress = false;
                }
            });

            // --- Capabilities (required-mod gating) ---
            RegisterRpc("caps.get", p =>
            {
                var pluginsDir = GetPluginsDirectory();
                List<RequiredMod> missing;

                if (string.IsNullOrWhiteSpace(pluginsDir))
                {
                    missing = Tools.RequiredModChecker.RequiredMods.ToList();
                }
                else
                {
                    missing = RequiredModChecker.GetMissingMods(pluginsDir);
                }

                // Mirrors MainWindow.UpdateModCapabilities gating logic.
                var rconInstalled = !missing.Any(m => m.ModName == "RCON");
                var devcommandsInstalled = !missing.Any(m => m.ModName == "Server_devcommands")
                                        && !missing.Any(m => m.ModName == "Rcon_Commands");

                return Task.FromResult<object>(new
                {
                    rcon = rconInstalled,
                    devcommands = rconInstalled && devcommandsInstalled,
                    missing = missing.Select(m => new
                    {
                        m.Author,
                        m.ModName,
                        m.Description,
                        m.ThunderstoreUrl,
                        m.RequiredFor,
                    }).ToList(),
                });
            });

            // Downloads and installs ALL missing required mods from Thunderstore. Safe while
            // the server is running (new plugin folders aren't locked), but BepInEx only
            // loads plugins at launch, so they take effect on the next server start.
            RegisterRpc("caps.install", async p =>
            {
                if (_requiredModInstallInProgress)
                    throw new InvalidOperationException("A required-mod install is already in progress.");

                var pluginsDir = GetPluginsDirectory();
                if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
                    throw new DirectoryNotFoundException(
                        "BepInEx plugins folder not found - set a valid server .exe path first.");

                _requiredModInstallInProgress = true;
                try
                {
                    var missing = RequiredModChecker.GetMissingMods(pluginsDir);
                    var results = new List<object>();
                    foreach (var mod in missing)
                    {
                        var ok = await RequiredModChecker.InstallModAsync(mod, pluginsDir);
                        results.Add(new { mod.Author, mod.ModName, installed = ok });
                    }
                    return new
                    {
                        results,
                        stillMissing = RequiredModChecker.GetMissingMods(pluginsDir)
                            .Select(m => m.FolderName).ToList(),
                        note = "Installed mods load on the next server start.",
                    };
                }
                finally
                {
                    _requiredModInstallInProgress = false;
                }
            });

            // --- Config editor ---
            RegisterRpc("config.list", p =>
            {
                var dir = GetConfigDirectory();
                if (dir == null || !Directory.Exists(dir)) return Task.FromResult<object>(new List<string>());
                return Task.FromResult<object>(
                    Directory.GetFiles(dir, "*.cfg").Select(Path.GetFileName).ToList());
            });

            RegisterRpc("config.read", p =>
            {
                var path = ResolveConfigFilePath(p.Value<string>("file"));
                return Task.FromResult<object>(File.ReadAllText(path));
            });

            RegisterRpc("config.write", p =>
            {
                var path = ResolveConfigFilePath(p.Value<string>("file"));
                File.WriteAllText(path, p.Value<string>("text") ?? "");
                return Task.FromResult<object>(true);
            });

            // --- Logs ---
            RegisterRpc("logs.appBuffer", p => Task.FromResult<object>(AppLogger.LogBuffer.ToList()));

            // In-memory tail of the ACTIVE profile's current server session (empty
            // when the server has never been started this app run).
            RegisterRpc("logs.serverBuffer", p => Task.FromResult<object>(
                Server?.Logger?.LogBuffer?.ToList() ?? new List<string>()));

            // --- Network / IP ---
            RegisterRpc("ip.get", p => Task.FromResult<object>(new
            {
                @internal = IpAddressProvider.InternalIpAddress,
                external = IpAddressProvider.ExternalIpAddress,
            }));

            RegisterRpc("ip.refresh", async p =>
            {
                await Task.WhenAll(
                    IpAddressProvider.LoadInternalIpAddressAsync(),
                    IpAddressProvider.LoadExternalIpAddressAsync());
                return new
                {
                    @internal = IpAddressProvider.InternalIpAddress,
                    external = IpAddressProvider.ExternalIpAddress,
                };
            });

            // --- Shell ---
            // Opens a well-known folder in Explorer. Only closed keywords are accepted -
            // never a raw path from JS - so the page can't shell-execute arbitrary files.
            RegisterRpc("shell.open", p =>
            {
                var target = p.Value<string>("target");
                var path = target switch
                {
                    "saveData" => ResolveSaveDataFolder(null),
                    "serverDir" => Path.GetDirectoryName(GetServerExePath() ?? ""),
                    "config" => GetConfigDirectory(),
                    "plugins" => GetPluginsDirectory(),
                    "logs" => ResolveLogsFolder(),
                    "appData" => Path.GetDirectoryName(Resources.UserPrefsFilePathV2),
                    _ => throw new ArgumentException($"Unknown shell.open target: {target}"),
                };

                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    throw new DirectoryNotFoundException($"Folder not found: {path ?? "(unset)"}");

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
                return Task.FromResult<object>(true);
            });

            // Opens a well-known EXTERNAL URL in the default browser. Like shell.open,
            // JS may only send a closed keyword - never a raw URL - so the page can't
            // navigate the OS to an arbitrary link.
            RegisterRpc("shell.openUrl", p =>
            {
                var target = p.Value<string>("target");
                var url = target switch
                {
                    "donate" => DonateUrl,
                    _ => throw new ArgumentException($"Unknown shell.openUrl target: {target}"),
                };

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                return Task.FromResult<object>(true);
            });

            // --- First-launch setup ---
            // Reports whether the guided setup has been completed and whether the
            // currently-saved paths actually exist on disk.
            RegisterRpc("setup.status", p => Task.FromResult<object>(BuildSetupStatus()));

            // Scans common Steam locations for a Valheim dedicated server install.
            RegisterRpc("setup.detect", p => Task.FromResult<object>(DetectServerInstalls()));

            // Validates a user-supplied path without saving anything.
            RegisterRpc("setup.validate", p =>
            {
                var kind = p.Value<string>("kind");
                var raw = p.Value<string>("path") ?? "";
                string expanded;
                try
                {
                    expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
                }
                catch
                {
                    return Task.FromResult<object>(new { valid = false, expanded = raw });
                }

                bool valid;
                try
                {
                    valid = kind switch
                    {
                        "exe" => expanded.EndsWith("valheim_server.exe", StringComparison.OrdinalIgnoreCase)
                                 && File.Exists(expanded),
                        "dir" => Directory.Exists(expanded),
                        _ => throw new ArgumentException($"Unknown setup.validate kind: {kind}"),
                    };
                }
                catch (ArgumentException)
                {
                    throw;
                }
                catch
                {
                    valid = false; // invalid path characters etc.
                }

                return Task.FromResult<object>(new { valid, expanded });
            });

            // Persists the wizard's answers. Either path may be null/blank to keep defaults.
            RegisterRpc("setup.complete", p =>
            {
                var prefs = UserPrefsProvider.LoadPreferences();

                var exe = p.Value<string>("serverExePath");
                if (!string.IsNullOrWhiteSpace(exe)) prefs.ServerExePath = exe.Trim();

                var save = p.Value<string>("saveDataFolderPath");
                if (!string.IsNullOrWhiteSpace(save)) prefs.SaveDataFolderPath = save.Trim();

                prefs.SetupCompleted = true;
                UserPrefsProvider.SavePreferences(prefs);
                return Task.FromResult<object>(BuildSetupStatus());
            });

            // Resets first-time setup: paths back to defaults, SetupCompleted cleared, and any
            // per-profile path overrides removed so the wizard's next answers actually apply.
            // Server files on disk are never touched.
            RegisterRpc("setup.reset", p =>
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                prefs.ServerExePath = Resources.DefaultServerPath;
                prefs.SaveDataFolderPath = Resources.DefaultValheimSaveFolder;
                prefs.SetupCompleted = false;
                UserPrefsProvider.SavePreferences(prefs);

                foreach (var profile in ServerPrefsProvider.LoadPreferences().ToList())
                {
                    if (string.IsNullOrWhiteSpace(profile.ServerExePath)
                        && string.IsNullOrWhiteSpace(profile.SaveDataFolderPath)) continue;

                    profile.ServerExePath = null;
                    profile.SaveDataFolderPath = null;
                    ServerPrefsProvider.SavePreferences(profile);
                }

                return Task.FromResult<object>(BuildSetupStatus());
            });
        }

        /// <summary>
        /// The "buy me a coffee" / horn-of-mead support link opened by the sidebar.
        /// Ko-fi chosen for its 0% platform fee on one-time tips.
        /// </summary>
        private const string DonateUrl = "https://ko-fi.com/bakaloader";

        #endregion

        #region DTO builders & helpers

        private object BuildServerState() => BuildServerState(ActiveSession);

        private object BuildServerState(ServerSession session)
        {
            var server = session.Server;
            return new
            {
                profile = session.ProfileName,
                status = server.Status.ToString(),
                canStart = server.CanStart,
                canStop = server.CanStop,
                canRestart = server.CanRestart,
                countdownActive = server.IsCountdownActive,
                adopted = server.IsAdopted,
            };
        }

        /// <summary>
        /// One entry per known server profile (saved profiles plus any live sessions),
        /// with live status and this-server player counts for the sidebar chip strip.
        /// </summary>
        private object BuildServersList()
        {
            // Map each saved profile to its Archived flag so the strip can hide archived
            // realms while a collapsible "archived" section can still surface them.
            var archivedByName = ServerPrefsProvider.LoadPreferences()
                .Where(pref => !string.IsNullOrWhiteSpace(pref.ProfileName))
                .GroupBy(pref => pref.ProfileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Archived, StringComparer.OrdinalIgnoreCase); // value: bool

            var names = archivedByName.Keys
                .Concat(Sessions.Keys)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var active = ActiveProfileName;
            return names.Select(name =>
            {
                Sessions.TryGetValue(name, out var session);
                var status = session?.Server.Status ?? ServerStatus.Stopped;
                var playersOnline = PlayerDataProvider.Data.Count(pl =>
                    (pl.PlayerStatus == PlayerStatus.Online || pl.PlayerStatus == PlayerStatus.Joining)
                    && string.Equals(pl.ServerKey, name, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    name,
                    status = status.ToString(),
                    running = status != ServerStatus.Stopped,
                    playersOnline,
                    active = string.Equals(name, active, StringComparison.OrdinalIgnoreCase),
                    archived = archivedByName.TryGetValue(name, out var a) && a,
                };
            }).ToList();
        }

        /// <summary>First non-archived profile name that isn't <paramref name="excludeName"/> (null if none).</summary>
        private string FirstActiveProfileExcept(string excludeName)
        {
            return ServerPrefsProvider.LoadPreferences()
                .Where(pref => !pref.Archived
                    && !string.Equals(pref.ProfileName, excludeName, StringComparison.OrdinalIgnoreCase))
                .Select(pref => pref.ProfileName)
                .FirstOrDefault();
        }

        /// <summary>Count of profiles still visible on the strip (not archived).</summary>
        private int CountNonArchivedProfiles() =>
            ServerPrefsProvider.LoadPreferences().Count(pref => !pref.Archived);

        /// <summary>
        /// The profile's isolated install directory IF it is a BakaLoader-managed install
        /// (safe to reclaim); null for shared/base installs so delete never touches them.
        /// </summary>
        private string GetManagedInstallDir(ServerPreferences prefs)
        {
            if (!(prefs.IsolatedInstall) || string.IsNullOrWhiteSpace(prefs.ServerExePath)) return null;
            try
            {
                var dir = Path.GetDirectoryName(prefs.ServerExePath);
                return InstallIsolation.IsManagedInstall(dir) ? dir : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The profile's isolated save folder IF it is one we provisioned (lives under a
        /// ".../servers/&lt;name&gt;" path and differs from the base save folder); null otherwise,
        /// so a shared/base save folder is never deleted.
        /// </summary>
        private string GetIsolatedSaveFolder(ServerPreferences prefs)
        {
            var path = prefs.SaveDataFolderPath;
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                // Guard: only a folder whose parent directory is literally "servers" is one we made.
                var parent = Directory.GetParent(full)?.Name;
                if (!string.Equals(parent, "servers", StringComparison.OrdinalIgnoreCase)) return null;

                // Never treat the base/user save folder as deletable even if it somehow matches.
                if (SamePath(full, ResolveSaveDataFolder(null))) return null;
                return full;
            }
            catch { return null; }
        }

        /// <summary>Counts files and total bytes under the isolated install + save folder (best effort).</summary>
        private (int fileCount, long sizeBytes) MeasureDeletableFiles(string installDir, string saveFolder)
        {
            int count = 0; long bytes = 0;

            void Measure(string root)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
                var di = new DirectoryInfo(root);
                // Skip reparse points (junctions) so shared game data isn't counted or implied deleted.
                foreach (var f in EnumerateRealFiles(di))
                {
                    count++;
                    try { bytes += f.Length; } catch { /* ignore */ }
                }
            }

            Measure(installDir);
            Measure(saveFolder);
            return (count, bytes);
        }

        /// <summary>Enumerates files under a directory tree WITHOUT descending into junctions/symlinks.</summary>
        private static IEnumerable<FileInfo> EnumerateRealFiles(DirectoryInfo dir)
        {
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) yield break;
            foreach (var f in dir.GetFiles()) yield return f;
            foreach (var sub in dir.GetDirectories())
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                foreach (var f in EnumerateRealFiles(sub)) yield return f;
            }
        }

        /// <summary>
        /// Picks a game port (and its silent port+1 twin) plus an RCON port that collide with
        /// no existing profile. Valheim binds port AND port+1, so game ports are spaced 2 apart.
        /// </summary>
        private (int gamePort, int rconPort) SuggestFreePorts()
        {
            var profiles = ServerPrefsProvider.LoadPreferences().ToList();

            // Every port Valheim would occupy for existing profiles: p and p+1 for each.
            var usedGame = new HashSet<int>();
            foreach (var pr in profiles) { usedGame.Add(pr.Port); usedGame.Add(pr.Port + 1); }
            var usedRcon = new HashSet<int>(profiles.Where(pr => pr.RconEnabled).Select(pr => pr.RconPort));

            var defaultGame = int.TryParse(Resources.DefaultServerPort, out var dg) ? dg : 2456;
            var gamePort = defaultGame;
            // Advance by 2 (skipping the port+1 twin) until neither this port nor its twin is taken.
            while (usedGame.Contains(gamePort) || usedGame.Contains(gamePort + 1))
                gamePort += 2;

            var rconPort = 25575;
            while (usedRcon.Contains(rconPort) || rconPort == gamePort || rconPort == gamePort + 1)
                rconPort++;

            return (gamePort, rconPort);
        }

        /// <summary>
        /// Every save-data folder BakaLoader might find worlds in: the base/user save folder,
        /// the Valheim LocalLow default, and each profile's own isolated save folder.
        /// De-duplicated by full path; only folders that actually exist are returned.
        /// </summary>
        private List<string> KnownSaveFolders()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            void Consider(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                    if (Directory.Exists(full) && seen.Add(full)) list.Add(full);
                }
                catch { /* ignore unreadable paths */ }
            }

            Consider(ResolveSaveDataFolder(null));
            Consider(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "IronGate", "Valheim"));
            foreach (var pr in ServerPrefsProvider.LoadPreferences())
                Consider(pr.SaveDataFolderPath);

            // Per-server isolated save folders live at "<base>/servers/<name>". Scan every
            // base's servers/ subtree too - not just folders referenced by a live profile -
            // so worlds from a deleted-but-files-kept realm still surface as orphans.
            foreach (var baseFolder in list.ToList())
            {
                var serversRoot = Path.Combine(baseFolder, "servers");
                if (!Directory.Exists(serversRoot)) continue;
                try
                {
                    foreach (var sub in Directory.GetDirectories(serversRoot)) Consider(sub);
                }
                catch { /* unreadable servers dir - skip */ }
            }

            return list;
        }

        /// <summary>
        /// True when two paths point at the same directory (full-path normalized,
        /// trailing-separator and case insensitive). False on any unresolvable path.
        /// </summary>
        private static bool SameFolder(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Best-effort in-game day read from a Valheim .db header: int32 worldVersion
        /// followed by a double netTime in seconds; one day is 1800 seconds (EnvMan).
        /// Returns null on any doubt - a missing day is better than a wrong one.
        /// </summary>
        private static long? TryReadWorldDay(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath)) return null;
            try
            {
                if (!File.Exists(dbPath)) return null;
                using var stream = File.OpenRead(dbPath);
                if (stream.Length < 12) return null;
                using var reader = new BinaryReader(stream);
                var version = reader.ReadInt32();
                if (version <= 0 || version >= 100) return null;
                var netTime = reader.ReadDouble();
                if (double.IsNaN(netTime) || netTime < 0 || netTime >= 4e9) return null;
                return (long)(netTime / 1800.0);
            }
            catch { return null; }
        }

        /// <summary>
        /// Validates a Barrow backup reference ({world, folder, sub, file}) and resolves it to
        /// a real on-disk location. Hard rules: no path separators or ".." anywhere, folder must
        /// be one of the known save folders, sub must be worlds_local/worlds, and (when
        /// requireBackupShape) the file must be a backup-shaped name for THAT world - either
        /// "{world}_backup_*.fwl" or "{world}.fwl.old" - so the live pair can never be targeted.
        /// </summary>
        private (string dir, string world, string file) ValidateBackupRef(JObject p, bool requireBackupShape)
        {
            var world = p.Value<string>("world");
            var folder = p.Value<string>("folder");
            var sub = p.Value<string>("sub");
            var file = p.Value<string>("file");

            if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("world is required");
            if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("file is required");

            foreach (var piece in new[] { world, file })
            {
                if (piece.IndexOfAny(new[] { '/', '\\' }) >= 0 || piece.Contains(".."))
                    throw new ArgumentException("Invalid characters in backup reference.");
            }

            if (sub != "worlds_local" && sub != "worlds")
                throw new ArgumentException("Invalid save subfolder.");

            if (string.IsNullOrWhiteSpace(folder) || !KnownSaveFolders().Any(k => SameFolder(k, folder)))
                throw new ArgumentException("Unknown save folder.");

            if (requireBackupShape)
            {
                var isSnapshot = file.StartsWith(world + "_backup_", StringComparison.OrdinalIgnoreCase)
                    && file.EndsWith(".fwl", StringComparison.OrdinalIgnoreCase);
                var isOldPair = string.Equals(file, world + ".fwl.old", StringComparison.OrdinalIgnoreCase);
                if (!isSnapshot && !isOldPair)
                    throw new ArgumentException("Not a backup layer of that world.");
            }

            var dir = Path.Combine(Path.GetFullPath(folder), sub);
            if (!Directory.Exists(dir)) throw new ArgumentException("Save subfolder does not exist.");
            if (!File.Exists(Path.Combine(dir, file))) throw new ArgumentException("That backup no longer exists.");

            return (dir, world, file);
        }

        /// <summary>
        /// Copies a world's on-disk files (db/fwl and their .old siblings) from a source save
        /// folder into a destination save folder's worlds_local subdir, so an adopted world is a
        /// real independent copy and the original is never moved, altered, or deleted.
        /// </summary>
        private static void CopyWorldFiles(string sourceFolder, string sub, string world, string destSaveFolder)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder) || string.IsNullOrWhiteSpace(world)) return;

            var srcDir = Path.Combine(sourceFolder, string.IsNullOrWhiteSpace(sub) ? "worlds_local" : sub);
            if (!Directory.Exists(srcDir)) return;

            // A dedicated server reads its worlds from worlds_local, so land the copy there.
            var destDir = Path.Combine(destSaveFolder, "worlds_local");
            Directory.CreateDirectory(destDir);

            foreach (var ext in new[] { ".db", ".fwl", ".db.old", ".fwl.old" })
            {
                var src = Path.Combine(srcDir, world + ext);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(destDir, world + ext), overwrite: true);
            }
        }

        /// <summary>
        /// Builds a per-server save-data folder path under the base save location so each
        /// server's worlds and backups stay in their own directory (no cross-server mingling).
        /// </summary>
        private string MakeIsolatedSaveFolder(string profileName)
        {
            // Always anchor at the USER-level base save folder, never the current profile's:
            // an isolated profile's own save folder ends in "servers/<name>", and anchoring
            // there would nest every realm created from it ("servers/A/servers/B/...").
            var baseSave = UserPrefsProvider.LoadPreferences().SaveDataFolderPath;
            if (string.IsNullOrWhiteSpace(baseSave))
            {
                // No configured save folder yet: fall back to the Valheim LocalLow default.
                var localLow = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "IronGate", "Valheim");
                baseSave = localLow;
            }

            var parent = Path.Combine(baseSave, "servers");
            var safe = InstallIsolationService.MakeSafeName(profileName);
            var candidate = Path.Combine(parent, safe);
            var n = 2;
            while (Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any())
                candidate = Path.Combine(parent, $"{safe}-{n++}");

            Directory.CreateDirectory(candidate);
            return candidate;
        }

        private object BuildPlayerDto(PlayerInfo player)
        {
            // Display-name logic mirrors MainWindow.GetPlayerDisplayName.
            var name = player.PlayerName ?? $"[...{player.PlayerId[^4..]}]";
            if (!string.IsNullOrWhiteSpace(player.LastStatusCharacter))
            {
                name += $" ({player.LastStatusCharacter})";
            }

            return new
            {
                key = player.Key,
                player.Platform,
                player.PlayerId,
                player.PlayerName,
                displayName = name,
                status = player.PlayerStatus.ToString(),
                lastStatusChange = player.LastStatusChange,
                characters = player.Characters?.Select(c => c.CharacterName).ToList(),
                serverKey = player.ServerKey,
            };
        }

        /// <summary>
        /// Persists a profile's current installed-mod set into its ModManifest so each server's
        /// mod list is tracked distinctly (used on restore/export). Best-effort: a persistence
        /// failure never breaks a scan.
        /// </summary>
        private void RecordModManifest(string profileName, IEnumerable<Tools.Models.InstalledMod> mods)
        {
            if (string.IsNullOrWhiteSpace(profileName) || mods == null) return;
            try
            {
                var prefs = ServerPrefsProvider.LoadPreferences(profileName);
                if (prefs == null) return;

                prefs.ModManifest = mods
                    .Where(m => !string.IsNullOrWhiteSpace(m.ModName))
                    .Select(m => new ModManifestEntry
                    {
                        Owner = m.Author,
                        Name = m.ModName,
                        Version = m.InstalledVersion,
                    })
                    .ToList();

                ServerPrefsProvider.SavePreferences(prefs);
            }
            catch (Exception e)
            {
                AppLogger.Error(e, "Failed to record the mod manifest for profile '{0}'.", profileName);
            }
        }

        private object BuildModDto(Tools.Models.InstalledMod mod)
        {
            return new
            {
                mod.Author,
                mod.ModName,
                mod.FullName,
                mod.InstalledVersion,
                mod.LatestVersion,
                mod.UpdateAvailable,
                mod.PluginDirectory,
            };
        }

        private object BuildUserPrefsDto()
        {
            var prefs = UserPrefsProvider.LoadPreferences();
            return new
            {
                prefs.ServerExePath,
                prefs.SaveDataFolderPath,
                prefs.CheckForUpdates,
                prefs.AutoUpdateMods,
                prefs.AutoUpdateBakaLoader,
                prefs.StartWithWindows,
                prefs.ShareAnonymousStats,
                prefs.StartMinimized,
                prefs.SaveProfileOnStart,
                prefs.WriteApplicationLogsToFile,
                prefs.LogsFolderPath,
                DefaultLogsFolderPath = Environment.ExpandEnvironmentVariables(Resources.LogsFolderPath),
                prefs.EnablePasswordValidation,
                prefs.DarkMode,
                prefs.PlainTerminology,
                prefs.SetupCompleted,
                prefs.DiscordWebhookUrl,
                prefs.DiscordWebhookThreadId,
                prefs.DiscordSharingEnabled,
                prefs.DiscordShareAddress,
                prefs.DiscordSharePassword,
                prefs.DiscordEventPosts,
                prefs.CustomJoinDomain,
                HasDiscordStatusMessage = !string.IsNullOrWhiteSpace(prefs.DiscordStatusMessageId),
                AppVersion = AssemblyHelper.GetApplicationVersion(),
            };
        }

        /// <summary>
        /// Everything the Herald's Discord status post shows, built fresh at publish time.
        /// Prefers a live (non-stopped) session - the active one wins ties - and falls back
        /// to the active profile so a stopped server still reports "Offline" truthfully.
        /// </summary>
        private DiscordStatusSnapshot BuildDiscordSnapshot()
        {
            var session = Sessions.Values
                .Where(s => s.Server.Status != ServerStatus.Stopped)
                .OrderByDescending(s => string.Equals(s.ProfileName, ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            var profile = session?.ProfileName ?? ActiveProfileName;
            var server = session?.Server;
            var prefs = ServerPrefsProvider.LoadPreferences(profile);

            var status = server?.Status ?? ServerStatus.Stopped;
            var statusText = status switch
            {
                ServerStatus.Running => "Online",
                ServerStatus.Starting => "Starting up…",
                ServerStatus.Stopping => "Shutting down…",
                _ => "Offline",
            };

            var playersOnline = PlayerDataProvider.Data.Count(pl =>
                (pl.PlayerStatus == PlayerStatus.Online || pl.PlayerStatus == PlayerStatus.Joining)
                && string.Equals(pl.ServerKey, profile, StringComparison.OrdinalIgnoreCase));

            // Mod count + the newest plugin-folder write time (= last mod install/update).
            var modCount = 0;
            DateTime? lastModUpdate = null;
            try
            {
                var pluginsDir = GetPluginsDirectoryFor(profile);
                if (!string.IsNullOrWhiteSpace(pluginsDir) && Directory.Exists(pluginsDir))
                {
                    var mods = ModScanner.ScanPlugins(pluginsDir);
                    modCount = mods?.Count ?? 0;
                    lastModUpdate = (mods ?? new())
                        .Select(m => m.PluginDirectory)
                        .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                        .Select(d => (DateTime?)Directory.GetLastWriteTimeUtc(d))
                        .DefaultIfEmpty(null)
                        .Max();
                }
            }
            catch { /* mod info is decorative - never block the status post on it */ }

            var externalIp = IpAddressProvider.ExternalIpAddress;

            // A verified custom domain reads better than a raw IP and survives IP changes.
            var joinHost = UserPrefsProvider.LoadPreferences()?.CustomJoinDomain;
            if (string.IsNullOrWhiteSpace(joinHost)) joinHost = externalIp;

            return new DiscordStatusSnapshot
            {
                ServerName = !string.IsNullOrWhiteSpace(prefs?.Name) ? prefs.Name : profile,
                WorldName = prefs?.WorldName,
                StatusText = statusText,
                ServerRunning = status == ServerStatus.Running,
                ServerStarting = status == ServerStatus.Starting || status == ServerStatus.Stopping,
                AddressText = string.IsNullOrWhiteSpace(joinHost) ? null : $"{joinHost}:{prefs?.Port ?? 2456}",
                Password = prefs?.Password,
                PlayersOnline = playersOnline,
                ModCount = modCount,
                LastModUpdateUtc = lastModUpdate,
                NextRestartUtc = server?.NextScheduledRestartUtc,
                AppVersion = AssemblyHelper.GetApplicationVersion(),
            };
        }

        private object BuildSetupStatus()
        {
            var prefs = UserPrefsProvider.LoadPreferences();

            // Validate the RESOLVED paths (profile override → user prefs), so existing
            // installs configured at the profile level never re-trigger the wizard.
            bool exeValid = false, saveValid = false;
            try
            {
                var exe = Environment.ExpandEnvironmentVariables(GetServerExePath() ?? "");
                exeValid = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);
            }
            catch { /* invalid path characters - treat as not found */ }

            try
            {
                var dir = Environment.ExpandEnvironmentVariables(ResolveSaveDataFolder(null) ?? "");
                saveValid = !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir);
            }
            catch { /* invalid path characters - treat as not found */ }

            return new
            {
                setupCompleted = prefs.SetupCompleted,
                serverExePath = prefs.ServerExePath,
                saveDataFolderPath = prefs.SaveDataFolderPath,
                exeValid,
                saveValid,
                defaultExePath = Resources.DefaultServerPath,
                defaultSavePath = Resources.DefaultValheimSaveFolder,
            };
        }

        /// <summary>
        /// Best-effort scan for valheim_server.exe: the Steam registry install path plus every
        /// library in libraryfolders.vdf, then well-known library folder names on each fixed drive.
        /// </summary>
        private static List<string> DetectServerInstalls()
        {
            var found = new List<string>(); // preserves discovery order
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string relExe = @"steamapps\common\Valheim dedicated server\valheim_server.exe";

            void TryLibrary(string libraryRoot)
            {
                if (string.IsNullOrWhiteSpace(libraryRoot)) return;
                try
                {
                    var exe = Path.Combine(libraryRoot, relExe);
                    if (File.Exists(exe) && seen.Add(exe)) found.Add(exe);
                }
                catch { /* malformed path - skip */ }
            }

            // 1. The Steam install dir from the registry + all libraries in libraryfolders.vdf.
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var steamPath = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(steamPath))
                {
                    steamPath = steamPath.Replace('/', '\\');
                    TryLibrary(steamPath);

                    var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (System.Text.RegularExpressions.Match m in
                            System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        {
                            TryLibrary(m.Groups[1].Value.Replace(@"\\", @"\"));
                        }
                    }
                }
            }
            catch { /* no Steam registry key - fall through to the drive scan */ }

            // 2. Well-known library folder names on every fixed drive.
            try
            {
                foreach (var drive in System.IO.DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    foreach (var folder in new[] { @"Program Files (x86)\Steam", "SteamLibrary", "Steam", @"Games\Steam" })
                    {
                        TryLibrary(Path.Combine(drive.RootDirectory.FullName, folder));
                    }
                }
            }
            catch { /* drive enumeration failed - return whatever we have */ }

            return found;
        }

        /// <summary>
        /// The effective logs folder: the user's custom choice when set, else the
        /// app default - always env-expanded and created so Explorer can open it.
        /// </summary>
        private string ResolveLogsFolder()
        {
            var custom = UserPrefsProvider.LoadPreferences().LogsFolderPath;
            var folder = Environment.ExpandEnvironmentVariables(
                string.IsNullOrWhiteSpace(custom) ? Resources.LogsFolderPath : custom);
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Builds runtime server options from a preferences payload, mirroring
        /// MainWindow.GetServerOptionsFromFormState (user-pref fallbacks, world prefs,
        /// log handler).
        /// </summary>
        private ValheimServerOptions BuildServerOptions(ServerPreferences serverPrefs)
        {
            var userPrefs = UserPrefsProvider.LoadPreferences();

            // Captured for the log handler closure so log lines carry their server's
            // profile even after the active profile switches.
            var logProfile = string.IsNullOrWhiteSpace(serverPrefs.ProfileName)
                ? ActiveProfileName
                : serverPrefs.ProfileName;

            var options = new ValheimServerOptions
            {
                Name = serverPrefs.Name,
                Password = serverPrefs.Password,
                PasswordValidation = userPrefs.EnablePasswordValidation,
                WorldName = serverPrefs.WorldName,
                Public = serverPrefs.Public,
                Port = serverPrefs.Port,
                Crossplay = serverPrefs.Crossplay,
                SaveInterval = serverPrefs.SaveInterval,
                Backups = serverPrefs.BackupCount,
                BackupShort = serverPrefs.BackupIntervalShort,
                BackupLong = serverPrefs.BackupIntervalLong,
                AdditionalArgs = serverPrefs.AdditionalArgs,
                ServerExePath = !string.IsNullOrWhiteSpace(serverPrefs.ServerExePath)
                    ? serverPrefs.ServerExePath
                    : userPrefs.ServerExePath,
                SaveDataFolderPath = !string.IsNullOrWhiteSpace(serverPrefs.SaveDataFolderPath)
                    ? serverPrefs.SaveDataFolderPath
                    : userPrefs.SaveDataFolderPath,
                LogToFile = serverPrefs.WriteServerLogsToFile,
                LogFolderPath = userPrefs.LogsFolderPath,
                AutoRestart = serverPrefs.AutoRestart,
                AutoRestartDelay = serverPrefs.AutoRestartDelay,
                EmptyServerRestart = serverPrefs.EmptyServerRestart,
                EmptyServerRestartDelayMinutes = serverPrefs.EmptyServerRestartDelayMinutes,
                ScheduledRestart = serverPrefs.ScheduledRestart,
                ScheduledRestartHours = serverPrefs.ScheduledRestartHours,
                RconEnabled = serverPrefs.RconEnabled,
                RconPort = serverPrefs.RconPort,
                RconPassword = serverPrefs.RconPassword,
                LogMessageHandler = line => PostEvent("log.server", new { line, profile = logProfile }),
            };

            var worldName = serverPrefs.WorldName;
            if (!string.IsNullOrWhiteSpace(worldName))
            {
                var worldPrefs = WorldPrefsProvider.LoadPreferences(worldName);
                if (worldPrefs != null)
                {
                    if (!string.IsNullOrEmpty(worldPrefs.Preset))
                    {
                        options.WorldPreset = worldPrefs.Preset;
                    }
                    else
                    {
                        options.WorldModifiers = worldPrefs.Modifiers;
                    }

                    options.WorldKeys = worldPrefs.Keys;
                }
            }

            return options;
        }

        private PlayerInfo FindPlayer(JObject p)
        {
            var key = p.Value<string>("key");
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required");

            return PlayerDataProvider.Data.FirstOrDefault(pl => pl.Key == key)
                ?? throw new ArgumentException($"No player with key '{key}'");
        }

        private Tools.Models.InstalledMod FindInstalledMod(JObject p)
        {
            var fullName = p.Value<string>("fullName");
            if (string.IsNullOrWhiteSpace(fullName)) throw new ArgumentException("fullName is required");

            var pluginsDir = GetPluginsDirectory()
                ?? throw new InvalidOperationException("Server exe path is not configured");

            return ModScanner.ScanPlugins(pluginsDir).FirstOrDefault(m => m.FullName == fullName)
                ?? throw new ArgumentException($"No installed mod named '{fullName}'");
        }

        private static string RequireTarget(JObject p)
        {
            var target = p.Value<string>("target");
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("target is required");
            return target;
        }

        private static (PlayerListType, string) RequireListArgs(JObject p)
        {
            var listName = p.Value<string>("list");
            if (!Enum.TryParse<PlayerListType>(listName, ignoreCase: true, out var list))
            {
                throw new ArgumentException($"Unknown list '{listName}' (expected Admin, Banned, or Permitted)");
            }

            var id = p.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required");
            return (list, id);
        }

        /// <summary>Save-data folder: explicit value → current profile prefs → user prefs → Valheim default.</summary>
        private string ResolveSaveDataFolder(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;

            var profilePrefs = CurrentProfile != null ? ServerPrefsProvider.LoadPreferences(CurrentProfile) : null;
            if (!string.IsNullOrWhiteSpace(profilePrefs?.SaveDataFolderPath)) return profilePrefs.SaveDataFolderPath;

            return UserPrefsProvider.LoadPreferences().SaveDataFolderPath;
        }

        private string GetServerExePath() => GetServerExePathFor(CurrentProfile);

        /// <summary>
        /// The canonical base server exe to provision new isolated installs from. When the
        /// active profile itself runs from a BakaLoader-managed isolated install, hoists back
        /// out to the true base install: provisioning FROM an instance would root the new
        /// install at ".bakaloader-instances/.bakaloader-instances/…" and point its game-data
        /// junctions at another instance's junctions - a chain that breaks the moment that
        /// intermediate realm is deleted.
        /// </summary>
        private string GetCanonicalBaseServerExe()
        {
            var exe = GetServerExePathFor(ActiveProfileName);
            try
            {
                if (string.IsNullOrWhiteSpace(exe)) return exe;
                var dir = Path.GetDirectoryName(Path.GetFullPath(exe));
                var parent = string.IsNullOrWhiteSpace(dir) ? null : Directory.GetParent(dir);
                if (parent != null && string.Equals(parent.Name, InstallIsolationService.InstancesRootName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var baseRoot = parent.Parent?.FullName;
                    if (baseRoot != null)
                    {
                        var hoisted = Path.Combine(baseRoot, Path.GetFileName(exe));
                        if (File.Exists(hoisted)) return hoisted;
                    }

                    var userExe = UserPrefsProvider.LoadPreferences().ServerExePath;
                    if (!string.IsNullOrWhiteSpace(userExe) && File.Exists(userExe)) return userExe;
                }
            }
            catch { /* fall through to the profile's own exe */ }
            return exe;
        }

        /// <summary>
        /// The ACTIVE server's BepInEx directory - the wizard promises "copy this server's
        /// mods", so mod seeding always reads from the active profile's install even when the
        /// junction/link work is anchored at the canonical base install.
        /// </summary>
        private string GetActiveBepInExDir()
        {
            try
            {
                var exe = GetServerExePathFor(ActiveProfileName);
                var dir = string.IsNullOrWhiteSpace(exe) ? null : Path.GetDirectoryName(exe);
                return string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, "BepInEx");
            }
            catch { return null; }
        }

        private string GetServerExePathFor(string profileName)
        {
            var profilePrefs = profileName != null ? ServerPrefsProvider.LoadPreferences(profileName) : null;
            if (!string.IsNullOrWhiteSpace(profilePrefs?.ServerExePath)) return profilePrefs.ServerExePath;

            return UserPrefsProvider.LoadPreferences().ServerExePath;
        }

        private string GetPluginsDirectory() => GetPluginsDirectoryFor(CurrentProfile);

        private string GetPluginsDirectoryFor(string profileName)
        {
            var exePath = GetServerExePathFor(profileName);
            if (string.IsNullOrWhiteSpace(exePath)) return null;

            try
            {
                var dir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrWhiteSpace(dir)) return null;
                return Path.Combine(dir, "BepInEx", "plugins");
            }
            catch
            {
                return null;
            }
        }

        private string GetConfigDirectory()
        {
            var exePath = GetServerExePath();
            if (string.IsNullOrWhiteSpace(exePath)) return null;

            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(dir)) return null;
            return Path.Combine(dir, "BepInEx", "config");
        }

        private string ResolveConfigFilePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("file is required");

            // Reject path traversal - only bare .cfg file names inside BepInEx/config are allowed.
            if (fileName != Path.GetFileName(fileName) || !fileName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Invalid config file name '{fileName}'");
            }

            var dir = GetConfigDirectory()
                ?? throw new InvalidOperationException("Server exe path is not configured");
            return Path.Combine(dir, fileName);
        }

        private object ReadMaxPlayers()
        {
            var pluginsDir = GetPluginsDirectory();
            var configDir = GetConfigDirectory();

            if (MaxPlayersInstaller.IsInstalled(pluginsDir))
            {
                // Our bundled plugin's cfg is the source of truth; a missing cfg or key
                // means the plugin default of 10 applies.
                var count = MaxPlayersInstaller.ReadConfiguredCount(configDir) ?? 10;
                return new { count, modInstalled = true };
            }

            if (MaxPlayersInstaller.IsLegacyInstalled(pluginsDir))
            {
                // Legacy Azumatt mod still on disk (migration runs at the next server
                // start). Missing cfg or key means it would apply its OWN default of 20.
                var count = MaxPlayersInstaller.ReadLegacyCount(configDir) ?? 20;
                return new { count, modInstalled = true };
            }

            return new { count = 10, modInstalled = false };
        }

        // The legacy Azumatt mod's cfg, kept in sync only while its one-way migration to
        // the bundled BakaLoaderMaxPlayers plugin is still pending (see MaxPlayersInstaller).
        private const string LegacyMaxPlayersCfgFile = "Azumatt.MaxPlayerCount.cfg";

        private void WriteLegacyMaxPlayersCfg(int count)
        {
            var dir = GetConfigDirectory()
                ?? throw new InvalidOperationException("Server exe path is not configured");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, LegacyMaxPlayersCfgFile);

            if (File.Exists(path))
            {
                // Rewrite only the key line, preserving everything else BepInEx wrote.
                var lines = File.ReadAllLines(path).ToList();
                var idx = lines.FindIndex(l =>
                    l.TrimStart().StartsWith("MaxPlayerCount", StringComparison.OrdinalIgnoreCase)
                    && l.Contains('='));
                if (idx >= 0) lines[idx] = $"MaxPlayerCount = {count}";
                else lines.Add($"MaxPlayerCount = {count}");
                File.WriteAllLines(path, lines);
            }
            else
            {
                // Mod present but cfg not written yet - seed a minimal cfg so the next
                // start uses the chosen value instead of the mod's surprise default of 20.
                File.WriteAllText(path,
                    "[1 - General]\n\n" +
                    "## Override the player count that valheim checks for. Default is the vanilla max of 10.\n" +
                    "# Setting type: Int32\n" +
                    "# Default value: 20\n" +
                    $"MaxPlayerCount = {count}\n");
            }
        }

        private void EnsureItemCatalogLoaded()
        {
            var exePath = GetServerExePath();
            if (string.IsNullOrWhiteSpace(exePath)) return;

            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(dir)) return;

            ItemCatalog.EnsureLoaded(Path.Combine(dir, "BepInEx"));
        }

        #endregion
    }
}
