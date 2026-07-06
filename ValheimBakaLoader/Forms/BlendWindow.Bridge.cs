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
        private IUserPreferencesProvider UserPrefsProvider;
        private IServerPreferencesProvider ServerPrefsProvider;
        private IWorldPreferencesProvider WorldPrefsProvider;
        private IPlayerDataRepository PlayerDataProvider;
        private ValheimServer Server;
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
        private IApplicationLogger AppLogger;

        /// <summary>The profile whose preferences were last loaded/saved through the bridge.</summary>
        private string CurrentProfile;

        private bool _modScanInProgress;
        private bool _modUpdateInProgress;
        private bool _requiredModInstallInProgress;
        private bool _maxPlayersSaveInProgress;

        // Last metrics.get sample, for CPU% computed as a processor-time delta over wall-clock.
        private int _metricsPid;
        private DateTime _metricsSampleTime;
        private TimeSpan _metricsCpuTime;

        /// <summary>
        /// Azumatt's server-side mod that raises Valheim's hard 10-player cap. Installed
        /// on demand when the user sets Max Players above 10; its BepInEx cfg is the
        /// single source of truth for the configured count (no pref duplication).
        /// </summary>
        private static readonly RequiredMod MaxPlayerCountMod = new()
        {
            Author = "Azumatt",
            ModName = "MaxPlayerCount",
            Description = "Server-side mod that raises Valheim's built-in 10-player cap.",
            ThunderstoreUrl = "https://thunderstore.io/c/valheim/p/Azumatt/MaxPlayerCount/",
            RequiredFor = "Max Players above 10 in the World hall",
        };

        private const string MaxPlayerCountCfgFile = "Azumatt.MaxPlayerCount.cfg";

        private void InitializeBridge(
            IUserPreferencesProvider userPrefsProvider,
            IServerPreferencesProvider serverPrefsProvider,
            IWorldPreferencesProvider worldPrefsProvider,
            IPlayerDataRepository playerDataProvider,
            ValheimServer server,
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
            UserPrefsProvider = userPrefsProvider;
            ServerPrefsProvider = serverPrefsProvider;
            WorldPrefsProvider = worldPrefsProvider;
            PlayerDataProvider = playerDataProvider;
            Server = server;
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
            AppLogger = appLogger;

            // Anonymous usage heartbeat (gated on the ShareAnonymousStats pref).
            Heartbeat.ServerRunningProvider = () => Server.Status == ServerStatus.Running;
            Heartbeat.Start();

            WireAppSelfUpdate();
            RegisterServiceEvents();
            RegisterRpcHandlers();
        }

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
            if (Server.Status == ServerStatus.Stopped) return;

            e.Cancel = true;
            if (_closeShutdownInProgress) return; // already saving + shutting down

            var online = PlayerDataProvider.Data
                .Count(pl => pl.PlayerStatus == PlayerStatus.Online || pl.PlayerStatus == PlayerStatus.Joining);

            var nl = Environment.NewLine;
            var playersLine = online > 0
                ? $"{online} viking{(online == 1 ? " is" : "s are")} still online and will be disconnected.{nl}{nl}"
                : "";

            var page = new TaskDialogPage
            {
                Caption = Resources.ApplicationTitle,
                Heading = online > 0 ? "Vikings are still online" : "The server is still running",
                Text = playersLine +
                    "The world will be saved and the server shut down gracefully before BakaLoader closes.",
                Icon = online > 0 ? TaskDialogIcon.Warning : TaskDialogIcon.Information,
                AllowCancel = true,
            };
            var shutdown = new TaskDialogButton("Save && shut down");
            page.Buttons.Clear();
            page.Buttons.Add(shutdown);
            page.Buttons.Add(TaskDialogButton.Cancel);

            if (TaskDialog.ShowDialog(this, page) != shutdown) return;

            ShutDownServerThenClose();
        }

        private async void ShutDownServerThenClose()
        {
            _closeShutdownInProgress = true;
            try
            {
                Logger.Information("Close requested while the server is running - saving world and shutting down first");

                // Bounded wait so a wedged server can never trap the user in a window
                // that refuses to close. Stop() is a graceful close request, so Valheim
                // saves the world on the way down; it no-ops until CanStop (e.g. while
                // the server is still Starting), hence the retry inside the loop.
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (Server.Status != ServerStatus.Stopped && DateTime.UtcNow < deadline)
                {
                    if (Server.CanStop) Server.Stop();
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
        private void WireAppSelfUpdate()
        {
            Server.CheckForAppUpdateOnRestart = async () =>
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                if (!prefs.AutoUpdateBakaLoader) return false;

                var staged = await AppUpdateService.CheckAndStageUpdateAsync();
                if (!staged) return false;

                // An update is staged; close the app so the watchdog can take over. Marshal to
                // the UI thread and defer the close so this hook returns first and the restart
                // is cleanly abandoned.
                BeginInvoke(new Action(() => Close()));
                return true;
            };
        }

        #endregion

        #region Event forwarding (C# -> JS pushes)

        private void RegisterServiceEvents()
        {
            Server.StatusChanged += (s, status) => PostEvent("server.status", BuildServerState());
            Server.WorldSaved += (s, seconds) => PostEvent("server.worldSaved", new { seconds });
            Server.InviteCodeReady += (s, code) => PostEvent("server.inviteCode", new { code });
            Server.ServerCrashed += (s, e) => PostEvent("server.crashed", null);
            Server.CountdownTick += (s, message) => PostEvent("server.countdown", new { message });

            PlayerDataProvider.EntityUpdated += (s, player) => PostEvent("player.updated", BuildPlayerDto(player));

            IpAddressProvider.ExternalIpChanged += (s, ip) => PostEvent("ip.external", new { ip });
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
                return Task.FromResult<object>(prefs);
            });

            RegisterRpc("profiles.save", p =>
            {
                var prefs = (p["prefs"] ?? throw new ArgumentException("prefs is required"))
                    .ToObject<ServerPreferences>();
                ServerPrefsProvider.SavePreferences(prefs);
                CurrentProfile = prefs.ProfileName;
                return Task.FromResult<object>(prefs);
            });

            RegisterRpc("profiles.remove", p =>
            {
                var name = p.Value<string>("name");
                ServerPrefsProvider.RemovePreferences(name);
                if (CurrentProfile == name) CurrentProfile = null;
                return Task.FromResult<object>(true);
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
                Apply("EnablePasswordValidation", v => prefs.EnablePasswordValidation = v.Value<bool>());
                Apply("DarkMode", v => prefs.DarkMode = v.Value<bool>());
                Apply("PlainTerminology", v => prefs.PlainTerminology = v.Value<bool>());
                Apply("DiscordWebhookUrl", v => prefs.DiscordWebhookUrl = v.Value<string>());
                Apply("DiscordWebhookThreadId", v => prefs.DiscordWebhookThreadId = v.Value<string>());

                UserPrefsProvider.SavePreferences(prefs);

                // When the "start with Windows" toggle was part of this save, mirror it into the
                // Windows Run registry key so the choice actually takes effect (mirrors MainWindow).
                if (dto.TryGetValue("StartWithWindows", StringComparison.OrdinalIgnoreCase, out _))
                {
                    try { StartupHelper.ApplyStartupSetting(prefs.StartWithWindows, Logger); }
                    catch (Exception e) { AppLogger.Error(e, "Failed to apply the 'start with Windows' setting."); }
                }

                return Task.FromResult<object>(BuildUserPrefsDto());
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

            // --- Max players (MaxPlayerCount mod cfg) ---
            RegisterRpc("maxplayers.get", p => Task.FromResult<object>(ReadMaxPlayers()));

            RegisterRpc("maxplayers.save", async p =>
            {
                if (_maxPlayersSaveInProgress)
                    throw new InvalidOperationException("A max-players change is already in progress.");
                _maxPlayersSaveInProgress = true;
                try
                {
                    // 1-127: the mod patches an Ldc_I4_S (sbyte) operand, so 127 is the true ceiling.
                    var count = Math.Clamp(p.Value<int?>("count") ?? 10, 1, 127);
                    var pluginsDir = GetPluginsDirectory();
                    var installed = IsMaxPlayerModInstalled(pluginsDir);

                    if (!installed)
                    {
                        // Vanilla cap requested and no mod present - nothing to do.
                        if (count <= 10) return new { count = 10, modInstalled = false };

                        if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
                            throw new DirectoryNotFoundException(
                                "BepInEx plugins folder not found - set a valid server .exe path first.");

                        var ok = await RequiredModChecker.InstallModAsync(MaxPlayerCountMod, pluginsDir);
                        if (!ok)
                            throw new InvalidOperationException(
                                "Couldn't install the MaxPlayerCount mod from Thunderstore.");
                    }

                    WriteMaxPlayersCfg(count);
                    return new { count, modInstalled = true, note = "Applies on the next server start." };
                }
                finally
                {
                    _maxPlayersSaveInProgress = false;
                }
            });

            // --- Hearth metrics (Forge Load card) ---
            RegisterRpc("metrics.get", p =>
            {
                try
                {
                    var proc = Server.GetTrackedProcess();
                    if (proc == null || proc.HasExited)
                    {
                        _metricsPid = 0;
                        return Task.FromResult<object>(new { running = false });
                    }

                    proc.Refresh();
                    var now = DateTime.UtcNow;
                    var cpuTime = proc.TotalProcessorTime;

                    // First sample (or a new PID after restart) has no delta - report 0%.
                    double cpu = 0;
                    if (_metricsPid == proc.Id && _metricsSampleTime != default)
                    {
                        var wallMs = (now - _metricsSampleTime).TotalMilliseconds;
                        if (wallMs > 0)
                            cpu = (cpuTime - _metricsCpuTime).TotalMilliseconds
                                  / wallMs / Environment.ProcessorCount * 100.0;
                    }
                    _metricsPid = proc.Id;
                    _metricsSampleTime = now;
                    _metricsCpuTime = cpuTime;

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
                    _metricsPid = 0;
                    return Task.FromResult<object>(new { running = false });
                }
            });

            // --- Server lifecycle ---
            RegisterRpc("server.state", p => Task.FromResult<object>(BuildServerState()));

            RegisterRpc("server.start", p =>
            {
                var prefs = (p["prefs"] ?? throw new ArgumentException("prefs is required"))
                    .ToObject<ServerPreferences>();
                var options = BuildServerOptions(prefs);
                Server.Start(options);
                return Task.FromResult<object>(BuildServerState());
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
                PlayerDataProvider.Data.Select(BuildPlayerDto).ToList()));

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
                    "logs" => Resources.LogsFolderPath,
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

        private object BuildServerState()
        {
            return new
            {
                status = Server.Status.ToString(),
                canStart = Server.CanStart,
                canStop = Server.CanStop,
                canRestart = Server.CanRestart,
                countdownActive = Server.IsCountdownActive,
                adopted = Server.IsAdopted,
            };
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
            };
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
                prefs.EnablePasswordValidation,
                prefs.DarkMode,
                prefs.PlainTerminology,
                prefs.SetupCompleted,
                prefs.DiscordWebhookUrl,
                prefs.DiscordWebhookThreadId,
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
        /// Builds runtime server options from a preferences payload, mirroring
        /// MainWindow.GetServerOptionsFromFormState (user-pref fallbacks, world prefs,
        /// log handler).
        /// </summary>
        private ValheimServerOptions BuildServerOptions(ServerPreferences serverPrefs)
        {
            var userPrefs = UserPrefsProvider.LoadPreferences();

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
                AutoRestart = serverPrefs.AutoRestart,
                AutoRestartDelay = serverPrefs.AutoRestartDelay,
                EmptyServerRestart = serverPrefs.EmptyServerRestart,
                EmptyServerRestartDelayMinutes = serverPrefs.EmptyServerRestartDelayMinutes,
                ScheduledRestart = serverPrefs.ScheduledRestart,
                ScheduledRestartHours = serverPrefs.ScheduledRestartHours,
                RconEnabled = serverPrefs.RconEnabled,
                RconPort = serverPrefs.RconPort,
                RconPassword = serverPrefs.RconPassword,
                LogMessageHandler = line => PostEvent("log.server", new { line }),
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

        private string GetServerExePath()
        {
            var profilePrefs = CurrentProfile != null ? ServerPrefsProvider.LoadPreferences(CurrentProfile) : null;
            if (!string.IsNullOrWhiteSpace(profilePrefs?.ServerExePath)) return profilePrefs.ServerExePath;

            return UserPrefsProvider.LoadPreferences().ServerExePath;
        }

        private string GetPluginsDirectory()
        {
            var exePath = GetServerExePath();
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

        private bool IsMaxPlayerModInstalled(string pluginsDir) =>
            !string.IsNullOrWhiteSpace(pluginsDir)
            && Directory.Exists(Path.Combine(pluginsDir, MaxPlayerCountMod.FolderName));

        private object ReadMaxPlayers()
        {
            if (!IsMaxPlayerModInstalled(GetPluginsDirectory()))
                return new { count = 10, modInstalled = false };

            // Mod present: its cfg is the source of truth. Missing cfg or key means the
            // mod hasn't written one yet - it would apply its OWN default of 20 (not 10).
            var count = 20;
            var dir = GetConfigDirectory();
            var path = dir == null ? null : Path.Combine(dir, MaxPlayerCountCfgFile);
            if (path != null && File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("MaxPlayerCount", StringComparison.OrdinalIgnoreCase)) continue;
                    var eq = t.IndexOf('=');
                    if (eq > 0 && int.TryParse(t[(eq + 1)..].Trim(), out var v)) { count = v; break; }
                }
            }
            return new { count, modInstalled = true };
        }

        private void WriteMaxPlayersCfg(int count)
        {
            var dir = GetConfigDirectory()
                ?? throw new InvalidOperationException("Server exe path is not configured");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, MaxPlayerCountCfgFile);

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
                // Freshly-installed mod hasn't run yet - seed a minimal cfg so the FIRST
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
