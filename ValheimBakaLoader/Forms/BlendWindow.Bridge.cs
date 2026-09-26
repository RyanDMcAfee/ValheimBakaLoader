using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
        private IHexiumClient HexiumClient;
        private Tools.HexiumScanStep HexiumScan;
        private IModUpdateService ModUpdateService;
        private IModRemovalService ModRemovalService;
        private IRequiredModChecker RequiredModChecker;
        private ItemCatalog ItemCatalog;
        private PlayerListService PlayerListService;
        private IAppUpdateService AppUpdateService;
        private IHeartbeatService Heartbeat;
        private IInstallIsolationService InstallIsolation;
        private IBepInExService BepInEx;
        private IMaxPlayersInstaller MaxPlayersInstaller;
        private IDiscordStatusService DiscordStatus;
        private IDiscordWebhookService DiscordWebhooks;
        private IAnalyticsService Analytics;
        private ISoftwareUpdateProvider SoftwareUpdates;
        private IServerUpdateService ServerUpdates;
        private IServerSessionRegistry SessionRegistry;
        private ILanguagePackService LanguagePacks;
        private IApplicationLogger AppLogger;

        /// <summary>The Detailed log dial, so the switch on the Upkeep card can move it.</summary>
        private ILogLevelControl LogLevel;

        // One cancellation source per profile with an update in flight. It is the app's own
        // shutdown handle and NOTHING else: cancelling it used to be how "Stop waiting for Steam"
        // worked, and because the service links it into the steamcmd run, that stopped BakaLoader
        // awaiting steamcmd without stopping steamcmd, which kept rewriting the install while the
        // per install lock came off. A cancel now only ever goes through ServerUpdates.Cancel.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> ServerUpdateCts =
            new(StringComparer.OrdinalIgnoreCase);

        // The server executable each in flight update is rewriting, per profile. Kept so the
        // Start button, the Restart button and the window's close guard can all ask the update
        // service the one question that matters without loading preferences on every render.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ServerUpdateExe =
            new(StringComparer.OrdinalIgnoreCase);

        // The last phase each in flight update reported. The service decides whether a cancel is
        // honoured; this is how the bridge learns what it decided, without matching on a sentence:
        // a cancel that was taken is reported as phase Cancelled before the operation completes.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ServerUpdatePhase> ServerUpdatePhaseSeen =
            new(StringComparer.OrdinalIgnoreCase);

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

        // The one outstanding "yes, fetch that from Hexium" the page has been handed.
        // mods.installFromHexium will not move a byte without it, it only ever matches
        // the exact package and version the host was shown, it is spent the moment it is
        // used, and it goes stale on its own so a dialog left open overnight is not still
        // good in the morning. This is what makes an install from the other site
        // something the host did rather than something the app did.
        private readonly Tools.HexiumConsentGate HexiumConsent = new();

        // One BepInEx write at a time, for the same reason there is one mod update at a
        // time: the files are shared by every install on this base, and two writers over one
        // BepInEx/core is an install that starts neither version.
        //
        // It is a real slot rather than a flag because FOUR paths write BepInEx and only one
        // of them is a host pressing a button: the row's Install and Update, the unattended
        // restart window, the loader put in place before a start, and the removal of a pack
        // unpacked into the wrong folder. Three of those run with nobody watching. A flag that
        // only the button path ever set left the exact window this comment claims to close:
        // an auto-start writing the loader while the host presses Update, over a core that is
        // CLEARED before it is copied.
        private readonly SemaphoreSlim _bepInExWriteSlot = new(1, 1);

        /// <summary>
        /// The longest a start waits for another BepInEx write to finish. Longer than the
        /// download's own five minute timeout on purpose, so the wait ends because the other
        /// write ended rather than because this one gave up in the middle of it.
        /// </summary>
        private static readonly TimeSpan BepInExWriteWait = TimeSpan.FromMinutes(6);

        /// <summary>True while any of the four write paths holds the slot.</summary>
        private bool BepInExWriteInProgress => _bepInExWriteSlot.CurrentCount == 0;

        /// <summary>Takes the slot without waiting, or answers false because somebody has it.</summary>
        private bool TryBeginBepInExWrite() => _bepInExWriteSlot.Wait(0);

        /// <summary>
        /// Takes the slot, waiting up to <paramref name="wait"/>. Only the start path waits:
        /// it cannot be told "try again in a moment", and what it is waiting for is very often
        /// the very loader it wants put there.
        /// <para>
        /// The wait is dropped to nothing when this is the UI thread. A start that finds no
        /// launch guard wired runs inline on the thread that asked for it, and the writer it
        /// would be waiting for finishes its own await ON that thread: the wait could never
        /// end, so it is refused straight away instead of hanging the window for six minutes.
        /// </para>
        /// </summary>
        private bool BeginBepInExWrite(TimeSpan wait) =>
            _bepInExWriteSlot.Wait(OnUiThread ? TimeSpan.Zero : wait);

        /// <summary>
        /// True when this is the window's own thread, which is the one thread that must never
        /// block on a slot an awaiting writer holds. Treated as true when it cannot be
        /// answered, because not waiting is the safe way to be wrong.
        /// </summary>
        private bool OnUiThread
        {
            get { try { return !InvokeRequired; } catch { return true; } }
        }

        /// <summary>Hands the slot back. Safe to call once per successful take and no more.</summary>
        private void EndBepInExWrite()
        {
            try { _bepInExWriteSlot.Release(); }
            catch (SemaphoreFullException) { /* already handed back */ }
        }

        /// <summary>
        /// The one refusal every writer that cannot get the slot throws. Written once because
        /// an id is an identity: four call sites spelling the same sentence out four times is
        /// four chances for one of them to drift into a second meaning for one catalog entry.
        /// </summary>
        private static HostFacingException BepInExBusy() =>
            new HostFacingException("bepinex.busy",
                "BepInEx is already being written. Try again in a moment.");

        /// <summary>
        /// The refusal both language methods give a code the app has never heard of, written
        /// once for the same reason BepInExBusy is: an id is an identity, and one sentence
        /// spelled at two call sites is one chance for the two to drift apart.
        /// </summary>
        private static HostFacingException UnknownLanguage() =>
            new HostFacingException("lang.unknownCode", "BakaLoader has no language by that name.");

        // When each live server process was started, so the load check has something to
        // compare BepInEx/LogOutput.log against. Set the moment a start is taken, cleared
        // when the server stops.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> ServerLaunchUtc =
            new(StringComparer.OrdinalIgnoreCase);

        // The pack version an unattended window found and could not apply because another
        // server on this install was up. Null when nothing is waiting. It is per install
        // rather than per profile, because so is BepInEx.
        private string _bepInExUpdateWaiting;

        // How the last unattended BepInEx write ended: what it moved, or why it did not
        // happen. Per install for the same reason, and it STANDS: a write inside a restart
        // window happens with nobody at the keyboard, so there is no toast to show it and very
        // often no window open to show one in. Held here, it travels on every BepInEx answer,
        // the load-time one included, until a page has drawn it and says so through
        // bepinex.noticeSeen. One field rather than two, because it is the LAST outcome that
        // is worth standing: a refusal after a write replaces it, and so does the other way
        // round.
        private BepInExUnattendedOutcome _bepInExLastUnattended;
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
            // The Hexium reader. Resolved here rather than taken through the window's
            // constructor so nothing else in the app has to know it exists; it opens no
            // connection at all until the host turns their own switch on.
            HexiumClient = serviceProvider.GetRequiredService<IHexiumClient>();
            HexiumScan = new Tools.HexiumScanStep(HexiumClient, appLogger);
            InstallIsolation = serviceProvider.GetRequiredService<IInstallIsolationService>();
            BepInEx = serviceProvider.GetRequiredService<IBepInExService>();
            MaxPlayersInstaller = serviceProvider.GetRequiredService<IMaxPlayersInstaller>();
            DiscordStatus = serviceProvider.GetRequiredService<IDiscordStatusService>();
            DiscordWebhooks = serviceProvider.GetRequiredService<IDiscordWebhookService>();
            Analytics = serviceProvider.GetRequiredService<IAnalyticsService>();
            SoftwareUpdates = serviceProvider.GetRequiredService<ISoftwareUpdateProvider>();
            ServerUpdates = serviceProvider.GetRequiredService<IServerUpdateService>();
            SessionRegistry = serviceProvider.GetRequiredService<IServerSessionRegistry>();
            LanguagePacks = serviceProvider.GetRequiredService<ILanguagePackService>();
            AppLogger = appLogger;
            // Resolved rather than taken through the window's constructor, for the same
            // reason the Hexium reader is: nothing else in the app has to know it is there.
            LogLevel = serviceProvider.GetRequiredService<ILogLevelControl>();

            // The sentences the people on the server read are written on this side, so the
            // catalog they come out of is picked before anything can send one.
            RefreshHostCatalog();

            // The Herald: single self-editing Discord status post (gated on prefs inside the service).
            DiscordStatus.SnapshotProvider = BuildDiscordSnapshot;
            DiscordStatus.RequestUpdate();

            // The Atlas world reader explains every file it turns down (wrong version, missing
            // generation, unreadable chunk). Those lines are worth having in the app log when a
            // map refuses to render, instead of vanishing into a static field nobody reads.
            AtlasEngine.WorldDbReader.DiagnosticSink = message =>
            {
                if (string.IsNullOrWhiteSpace(message)) return;
                try { appLogger.Information("Atlas: {message}", message); } catch { }
            };

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
                WireRelaunchSettings(session);
                WireLaunchGuard(session);

                Sessions[profileName] = session;

                // And into the application-wide registry, which is what the self update reads.
                // A window only ever sees its own profiles; the update closes every window.
                SessionRegistry?.Register(session);
                return session;
            }
        }

        /// <summary>
        /// Every session in the whole application: the registry's, plus this window's own in
        /// case one was created before the registry was resolved. The union can only ever
        /// over-report, and over-reporting here means an update waits when it need not have,
        /// while under-reporting means a live world goes down under a file swap.
        /// </summary>
        private IReadOnlyCollection<ServerSession> AllSessions()
        {
            var mine = Sessions.Values.ToList();

            var registered = SessionRegistry?.All;
            if (registered == null || registered.Count == 0) return mine;

            var all = new List<ServerSession>(registered);
            foreach (var session in mine)
            {
                if (!registered.Contains(session)) all.Add(session);
            }

            return all;
        }

        /// <summary>
        /// Drops a session from the application-wide registry. Called wherever this window is
        /// finished with one: a removed profile, a renamed one, and the window itself closing.
        /// </summary>
        private void ForgetSession(ServerSession session) => SessionRegistry?.Unregister(session);

        /// <summary>
        /// The window is gone, so its sessions are nobody's business any more. Leaving them in
        /// the registry would mean a closed window's last status pinned the self update shut for
        /// the rest of the process.
        /// </summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                foreach (var session in Sessions.Values) ForgetSession(session);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not drop this window's sessions from the registry");
            }

            base.OnFormClosed(e);
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
                // The clock the BepInEx load check reads. Taken at Starting, because that is
                // the transition closest to the process being created, and cleared at Stopped
                // so a stopped realm never carries a verdict about a start that is over.
                if (status == ServerStatus.Starting) ServerLaunchUtc[profile] = DateTime.UtcNow;
                else if (status == ServerStatus.Stopped) ServerLaunchUtc.TryRemove(profile, out _);

                PostEvent("server.status", BuildServerState(session));
                PostEvent("servers.changed", BuildServersList());

                if (status == ServerStatus.Running)
                {
                    crashRecorded = false;
                    Analytics.Record(new AnalyticsEvent { Kind = "start", Server = profile });

                    // Nothing else asks again once the grace has passed: the status event only
                    // fires on a transition, and "the log never appeared" is not one. So the
                    // question is asked once more, a little after the window closes, and the
                    // answer travels on the status the page already listens to.
                    ScheduleBepInExLoadCheck(session);
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

            // A FAILED save means everything played since the last good one is only in memory.
            // That is the one server message a host must not miss, so it gets its own event.
            server.WorldSaveFailed += (s, durationMs) =>
            {
                PostEvent("server.worldSaveFailed", new { durationMs, profile });
                Analytics.Record(new AnalyticsEvent { Kind = "savefail", Server = profile });
            };

            // The world on disk is still pre-1.0. The next save rewrites it and there is no
            // way back, so warn while the host can still take a copy.
            server.LegacyWorldLoaded += (s, e) =>
            {
                var named = ServerPrefsProvider.LoadPreferences(profile)?.WorldName;

                // The page's copy, with the stand-in it has always carried. This one is read
                // in the window, so wording it belongs to the interface catalog and to the
                // page; it is English here for now and it is the last piece of this event
                // that is.
                PostEvent("server.legacyWorld", new { world = named ?? "the world", profile });

                if (UserPrefsProvider.LoadPreferences().DiscordEventPosts)
                {
                    // The post is read in a channel by the people who play on the server, so
                    // the stand-in comes out of their catalog rather than out of this line.
                    // It used to be the same English string as above, dropped into the middle
                    // of an otherwise translated sentence.
                    DiscordWebhooks.SendLegacyWorldLoaded(
                        DisplayName(), named ?? HostCatalog.T("host.discord.legacy_world.unnamed"));
                }
            };

            // Both numbers come from the server's own banner, never from an assumption.
            server.VersionDetected += (s, version) =>
            {
                PostEvent("server.version", new
                {
                    version,
                    network = server.NetworkVersion,
                    profile,
                });
                DiscordStatus.RequestUpdate();
            };
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
            /* The chip's own tick. An id and a number, never a sentence: the page writes the
               words out of the interface catalog, which is the only place that knows which
               language this window is being read in. A null tick is the countdown ending, and
               carries no id, which is how the page knows to say nothing.

               "message" is the key this event carried before the chip was split, holding the
               finished English sentence, and it is still here holding the same sentence to the
               byte. Nothing in the page reads it: the words on screen come from the id, the
               unit and the count above. It stays because an event is a contract and this pass
               is additive, and because ValheimServer.CountdownTick handing a CountdownChip
               where it used to hand a string is a break on the C# side that nothing should
               have to feel twice. See CountdownMessage for where the English comes from. */
            server.CountdownTick += (s, chip) => PostEvent("server.countdown",
                new { id = chip?.Id, unit = chip?.Unit, count = chip?.Count ?? 0, message = CountdownMessage(chip), profile });
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
        /// The countdown chip's sentence in English, for the "message" key the
        /// server.countdown event has always carried. Null for the tick that ends a
        /// countdown, which carries no chip and so has nothing to say.
        /// <para>
        /// The words are read out of the English catalog under the same ids the page reads
        /// in whatever language the window is in, so there is one owner for this sentence
        /// and the English it produces is the English the chip itself shows, to the byte. A
        /// literal here would be that same sentence written down a second time, which is how
        /// a translated product ends up with one line nobody can move.
        /// </para>
        /// <para>
        /// The unit falls back to minutes for anything unrecognised, which is the fallback
        /// the page's own countdownChipWords makes, so the two cannot disagree about a tier
        /// neither of them has heard of.
        /// </para>
        /// </summary>
        internal static string CountdownMessage(ValheimServer.CountdownChip chip)
        {
            if (chip == null) return null;

            if (string.Equals(chip.Id, "restart_now", StringComparison.Ordinal))
                return HostCatalog.PageEnglish("hearth.countdown.restart_now");

            if (!string.Equals(chip.Id, "restart_in", StringComparison.Ordinal)) return null;

            var time = chip.Unit switch
            {
                "hours" => HostCatalog.PageEnglish("hearth.countdown.hours", ("count", chip.Count)),
                "seconds" => HostCatalog.PageEnglish("hearth.countdown.seconds", ("count", chip.Count)),
                _ => HostCatalog.PageEnglish("hearth.countdown.minutes", ("count", chip.Count)),
            };

            return HostCatalog.PageEnglish("hearth.countdown.restart_in", ("time", time));
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
                        "Give each profile its own copy of the server folder. A renamed exe works.");

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
                    "Server PID {serverPid} is managed by another BakaLoader instance (PID {otherPid}), so this instance will close",
                    existing.Id, other.Id);

                var page = new TaskDialogPage
                {
                    Caption = Resources.ApplicationTitle,
                    Heading = "Another BakaLoader is already running",
                    Text =
                        $"A Valheim dedicated server (PID {existing.Id}) is already running and " +
                        $"{otherLabel} is managing it.{nl}{nl}" +
                        $"That BakaLoader stays in charge. It cannot be closed automatically, because " +
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

                // Auto-start is unattended by definition, so it goes through the launch guard
                // as an automatic launch: a changed build holds it and raises a banner rather
                // than quietly upgrading every world on the way up.
                Server.StartAutomatically(BuildServerOptions(prefs));
                Logger.Information("Auto-start requested for profile {profile}", StartProfile);
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

            // An update comes first, and it refuses harder than a running server does. Closing
            // takes the app's child processes with it, so a close during a steamcmd run kills
            // steamcmd mid file and leaves an install that is neither the old build nor the new
            // one. There is no dialog to answer here either: the wait is measured in minutes and
            // the window closes itself the moment the update is done.
            var choice = DecideUpdateClose(
                AnyServerUpdateRunning(), AnyServerUpdateInSteamCmd(), CloseFirstAskedUtc, DateTime.UtcNow);

            if (choice == UpdateCloseChoice.Wait)
            {
                e.Cancel = true;
                CloseWhenUpdatesFinish = true;

                Logger.Information("Close held: an update is running. The window will close when it finishes.");
                try
                {
                    TaskDialog.ShowDialog(this, new TaskDialogPage
                    {
                        Caption = Resources.ApplicationTitle,
                        Heading = "An update is running",
                        Text = UpdateCloseMessage,
                        Icon = TaskDialogIcon.Information,
                        AllowCancel = true,
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Could not show the update close notice");
                }

                // Timed from the moment the host can click again, not from the click that put
                // this notice up: the notice is modal, so anything measured from before it would
                // be spending the host's ten seconds on a dialog they cannot click past yet.
                CloseFirstAskedUtc = DateTime.UtcNow;
                return;
            }

            if (choice == UpdateCloseChoice.Force)
            {
                // The host asked twice and nothing is mid write, so the promise is off.
                CloseWhenUpdatesFinish = false;
                CloseFirstAskedUtc = null;
                Logger.Warning("Closing while an update is running: the host asked a second time.");
            }

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
                Logger.Information("Close requested while server(s) are running, saving worlds and shutting down first");

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

        /// <summary>
        /// The self update's way in to this window: stop whatever it is holding and close, on
        /// this window's own UI thread. Posted rather than run inline, exactly as the close
        /// always was, because the caller is either an RPC whose answer still has to reach the
        /// page or a restart hook that has to return before the app goes down.
        /// </summary>
        public void BeginShutDownAndClose()
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(ShutDownAllServersThenClose));
                return;
            }

            // No handle means no UI thread of its own to post to. A window in that state has
            // never been shown and holds no running server, so closing it directly is safe and
            // is better than leaving it behind to hold the process open.
            ShutDownAllServersThenClose();
        }

        /// <summary>
        /// A newer BakaLoader has been staged, so the whole application closes: every window,
        /// not just this one.
        /// <para>
        /// The watchdog that swaps the files is launched the moment staging succeeds and waits
        /// two minutes for this process to exit before it starts writing. BakaLoader opens one
        /// window per auto-start profile and stays up while any of them remain, so closing the
        /// window the host clicked in left the process running, and the swap went over a live
        /// install and then failed on the locked exe: half old, half new, and no relaunch.
        /// </para>
        /// </summary>
        private void CloseEveryWindowForSelfUpdate()
        {
            // Posted onto the UI thread, which is where the window list lives: one caller is an
            // RPC whose answer still has to reach the page, and the other is a restart hook that
            // runs on whichever thread the server exited on.
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(RunSelfUpdateShutdown));
                return;
            }

            RunSelfUpdateShutdown();
        }

        /// <summary>The shutdown itself, on the UI thread. Idempotent: the second ask is a no-op.</summary>
        private void RunSelfUpdateShutdown()
        {
            AppShutdown.Current.ShutDownAllWindowsForSelfUpdate(
                OpenServerWindows(),
                () => (ServiceProvider?.GetService(typeof(SplashForm)) as SplashForm)?.RequestExitForSelfUpdate(),
                Application.Exit,
                (ex, what) => Logger.Warning(ex, what));
        }

        /// <summary>
        /// Every open window that can hold a live server, this one included. Read off the
        /// application's own list rather than off anything a window keeps, so a window opened
        /// by a path nobody remembers is still asked to close.
        /// </summary>
        private static IReadOnlyList<ISelfUpdateClosable> OpenServerWindows()
            => Application.OpenForms.OfType<ISelfUpdateClosable>().ToList();

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
                // Every session in the application, not merely this window's: BakaLoader opens
                // one window per auto-start profile, and the swap closes all of them.
                var othersLive = UpdateGate.AnyServerBusy(
                    AllSessions().Where(s => !ReferenceEquals(s, session)));
                if (othersLive) return false;

                // Same reason a steamcmd run blocks the button: closing now abandons a rewrite
                // of an install this app is holding open, and a half written install cannot be
                // undone.
                if (AnyServerUpdateRunning()) return false;

                var staged = await AppUpdateService.CheckAndStageUpdateAsync();
                if (!staged) return false;

                // An update is staged; close the app so the watchdog can take over. Every window
                // goes, not just this one: the watchdog is already counting down to writing over
                // the install, and a second window left open keeps the process alive right
                // through that. The ask is deferred so this hook returns first and the restart
                // is cleanly abandoned, and each window goes through its own graceful shutdown
                // path, which saves the world and stops the server before closing (a bare
                // Close() would be cancelled by the close guard, unattended dialog and all, and
                // the old behavior let the job object hard-kill the server with no final save).
                CloseEveryWindowForSelfUpdate();
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

            // Installed before the companion plugins, in the slot where the server is still
            // down. Set here rather than in the constructor because it needs the profile name
            // the session was created for.
            session.Server.PrepareBepInEx = exePath => PrepareBepInExForStart(profile, exePath);

            session.Server.GetPendingModUpdateCount = async () =>
            {
                if (!UserPrefsProvider.LoadPreferences().AutoUpdateMods) return 0;

                var mods = await ScanModsWithLatestAsync(profile);
                return mods?.Count(m => m.UpdateAvailable) ?? 0;
            };

            // BepInEx first, and on a hook of its own: the loader is what the mods load under,
            // so moving it after them would leave one restart where new plugins meet an old
            // core. It is not inside ApplyModUpdates because that one is only reached when
            // this restart was raised BECAUSE mod updates were pending, and the loader has to
            // be looked at on every restart that has a window whatever mod auto update is set
            // to. The two settings answer different questions.
            session.Server.ApplyLoaderUpdate = () => ApplyBepInExUpdateAsync(profile);

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

        /// <summary>
        /// Teaches one session's server to read its profile again before it relaunches, so a
        /// setting the host saved while the world was up is in force the moment it comes back
        /// rather than waiting for somebody to stop and start the server by hand.
        /// <para>
        /// Built exactly the way <c>server.start</c> builds its options, launch history and log
        /// handler included: the launch guard reads the history off these options, and the Saga
        /// log is fed by the handler. The profile is the SESSION's own, never
        /// <see cref="ActiveProfileName"/>: windows are per profile but sessions are keyed by
        /// profile, so a background realm relaunching while the host is looking at another one
        /// must read its own settings.
        /// </para>
        /// </summary>
        private void WireRelaunchSettings(ServerSession session)
        {
            var profile = session.ProfileName;

            session.Server.RefreshOptions = () =>
            {
                var prefs = ServerPrefsProvider.LoadPreferences(profile);
                return prefs == null ? null : BuildServerOptions(MergeLaunchHistory(prefs));
            };
        }

        /// <summary>
        /// Whether this session is running (or coming up on) settings the host has since
        /// changed. That is the whole of "restart pending": what is saved differs from what the
        /// live server started with, and only a restart can close the gap.
        /// <para>
        /// A stopped server is never pending: its next start reads the profile anyway. Anything
        /// that cannot be read or compared answers false, because a restart the host does not
        /// need costs them their world for nothing.
        /// </para>
        /// </summary>
        private IValheimServerOptions SavedOptionsIfRestartPending(ServerSession session)
        {
            try
            {
                var server = session.Server;
                if (server.Status != ServerStatus.Running && server.Status != ServerStatus.Starting) return null;

                var prefs = ServerPrefsProvider.LoadPreferences(session.ProfileName);
                if (prefs == null) return null;

                var saved = BuildServerOptions(MergeLaunchHistory(prefs));
                return ValheimServerOptions.RelaunchWouldDiffer(server.Options, saved) ? saved : null;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex,
                    "Could not compare the saved settings with the running server for profile {profile}",
                    session.ProfileName);
                return null;
            }
        }

        #endregion

        #region Launch guard (build changed / Steam update waiting)

        // A one-shot answer the host gave in the UI, keyed by profile: "proceed" starts on the
        // new build as-is, "backup" copies every world aside first. Consumed by the next launch.
        // The timestamp rides along so an answer that never got used (the start was refused for
        // some other reason) cannot silently clear a question asked half an hour later.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, string Answer)> LaunchOverrides =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How long a staged answer stays good for. A launch follows within seconds.</summary>
        private static readonly TimeSpan LaunchAnswerLifetime = TimeSpan.FromMinutes(5);

        // The condition a held launch left behind, keyed by profile, so the banner survives a
        // page reload and shows up in server.state.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> LaunchHolds =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Teaches one session's server to ask before it launches. A start the host asked for
        /// in the UI carries its answer along; anything BakaLoader starts by itself is held
        /// with a banner instead, because nobody may be at the keyboard to answer a dialog.
        /// </summary>
        private void WireLaunchGuard(ServerSession session)
        {
            var profile = session.ProfileName;
            var server = session.Server;

            server.ConfirmLaunchAsync = context =>
            {
                // The host already answered for this launch, and answered it recently. An answer
                // is only good for the launch it was given for, which is what MayUseStagedAnswer
                // decides: anything BakaLoader started by itself asks again rather than helping
                // itself to a choice the host made about a different launch.
                if (context.MayUseStagedAnswer
                    && LaunchOverrides.TryRemove(profile, out var staged)
                    && DateTime.UtcNow - staged.At <= LaunchAnswerLifetime)
                {
                    ClearLaunchHold(profile);
                    return Task.FromResult(string.Equals(staged.Answer, "backup", StringComparison.OrdinalIgnoreCase)
                        ? LaunchDecision.BackUpThenGo("the host asked for a world backup first")
                        : LaunchDecision.Go("the host chose to start anyway"));
                }

                var outcome = LaunchGuard.Decide(
                    context.Current, context.LastLaunchedBuild, context.LastLaunchedFingerprint, context.HasWorlds);
                if (outcome == LaunchGuardOutcome.Proceed)
                {
                    ClearLaunchHold(profile);
                    return Task.FromResult(LaunchDecision.Go());
                }

                HoldLaunch(profile, context, outcome);
                return Task.FromResult(LaunchDecision.Hold(HoldReason(outcome, context)));
            };

            // Every path into a launch asks this first, including the ones no RPC can guard: the
            // crash relaunch, the scheduled restart, the empty server restart, the auto start and
            // the held launch retry timer. An update rewriting the install means there is no build
            // on disk to start, so they all stand down together.
            server.LaunchBlocked = () => IsServerUpdateRunning(ResolveUpdateExePath(profile));

            server.RecordLaunchedBuild = (build, version) => StoreLaunchedBuild(profile, build, version);

            // Profiles can share one save folder, so the pre-update snapshot can reach a world
            // some other profile's server is saving into right now. Copying that gives a layer
            // that reads as a whole world and is not one, so it stops the launch instead.
            server.WorldSnapshotVeto = WorldInUseByRunningProfile;

            server.PreUpdateBackupCompleted += (s, result) =>
            {
                PostEvent("server.worldsBackedUp", new
                {
                    profile,
                    ok = result.Ok,
                    error = result.Error,
                    count = result.Copied.Count,
                    bytes = result.Bytes,
                    skipped = result.Skipped,
                });

                if (result.Ok)
                {
                    Analytics.Record(new AnalyticsEvent { Kind = "presnap", Server = profile });
                }
            };

            // A launch that ends without the server coming up never changes Status, so nothing
            // else tells the Hearth the attempt is over. Without this the Start button stays
            // disabled from the state the start call returned and there is no way back.
            server.LaunchSettled += (s, settled) =>
            {
                if (!string.IsNullOrWhiteSpace(settled.Error))
                {
                    PostEvent("server.launchFailed", new
                    {
                        profile,
                        reason = settled.Reason,
                        message = settled.Error,
                    });
                }

                PostEvent("server.status", BuildServerState(session));
                PostEvent("servers.changed", BuildServersList());
            };
        }

        /// <summary>
        /// Why a world may not be copied aside right now, or null when it is free. Mirrors the
        /// guard the Barrow's restore already applies: a world a running server owns is being
        /// written to, so it cannot be captured whole.
        /// </summary>
        private string WorldInUseByRunningProfile(WorldInfo world)
        {
            if (world == null || string.IsNullOrWhiteSpace(world.Name)) return null;

            foreach (var pr in ServerPrefsProvider.LoadPreferences())
            {
                if (!string.Equals(pr.WorldName, world.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (Sessions.TryGetValue(pr.ProfileName, out var session)
                    && session.Server.Status != ServerStatus.Stopped)
                {
                    return $"'{pr.ProfileName}' is running it right now. Stop that server first.";
                }
            }

            return null;
        }

        /// <summary>
        /// Writes what a launched server actually ran into its profile: the identity the guard
        /// was given, and the binaries' own fingerprint taken at the same moment. Two fields
        /// because they answer different questions. The identity is whatever could be read
        /// (the Steam build id when the manifest was readable), while the fingerprint is always
        /// available, so a start where the manifest has gone quiet still has something to
        /// compare against instead of having to assume nothing changed.
        /// </summary>
        private void StoreLaunchedBuild(string profile, string build, string gameVersion)
        {
            if (string.IsNullOrWhiteSpace(build) && string.IsNullOrWhiteSpace(gameVersion)) return;

            try
            {
                var prefs = ServerPrefsProvider.LoadPreferences(profile);
                if (prefs == null) return;

                var changed = false;
                if (!string.IsNullOrWhiteSpace(build) && prefs.LastLaunchedServerBuild != build)
                {
                    prefs.LastLaunchedServerBuild = build;
                    changed = true;
                }

                var fingerprint = LaunchedFingerprint(
                    prefs.ServerExePath,
                    message => Logger.Warning("Could not fingerprint the launched server: {message}", message));
                if (!string.IsNullOrWhiteSpace(fingerprint) && prefs.LastLaunchedServerFingerprint != fingerprint)
                {
                    prefs.LastLaunchedServerFingerprint = fingerprint;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(gameVersion) && prefs.LastLaunchedGameVersion != gameVersion)
                {
                    prefs.LastLaunchedGameVersion = gameVersion;
                    changed = true;
                }
                if (!changed) return;

                ServerPrefsProvider.SavePreferences(prefs);
                Logger.Information(
                    "Recorded {profile} as last launched on {build} ({version})",
                    profile, build ?? "(unchanged)", gameVersion ?? "version not reported yet");
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not record the launched build for profile {profile}", profile);
            }
        }

        /// <summary>
        /// The binaries' own identity for an install, asked for even when the Steam manifest
        /// answered. Never throws and never blocks the record: a fingerprint that cannot be
        /// taken simply leaves the profile with the one identity it always had.
        /// </summary>
        public static string LaunchedFingerprint(string serverExePath, Action<string> onFailure = null)
        {
            if (string.IsNullOrWhiteSpace(serverExePath)) return null;

            try
            {
                return ServerBuildTracker.Probe(serverExePath, alwaysFingerprint: true).Fingerprint;
            }
            catch (Exception ex)
            {
                onFailure?.Invoke(ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Records and announces a launch the guard would not clear: a banner in the UI, a line
        /// in the log, a Herald post when event posts are on, and an entry in the journal.
        /// </summary>
        private void HoldLaunch(string profile, LaunchContext context, LaunchGuardOutcome outcome)
        {
            var dto = BuildLaunchGuardDto(profile, context, outcome);
            LaunchHolds[profile] = dto;

            var reason = HoldReason(outcome, context);
            Logger.Warning("Start held for profile {profile}: {reason}", profile, reason);

            PostEvent("server.launchHold", dto);
            PostEvent("servers.changed", BuildServersList());

            Analytics.Record(new AnalyticsEvent
            {
                Kind = "hold",
                Server = profile,
                FromVersion = context.LastLaunchedBuild,
                ToVersion = context.Current?.Identity,
            });

            try
            {
                // The Discord post exists to reach somebody who is not at the keyboard. A hold
                // on a launch the app decided to make on its own is exactly that; a hold on a
                // start the host just pressed is not, and they are already looking at the
                // banner this method put on screen. The in-app record above happens either way.
                if (ShouldAnnounceHold(context.Automatic, UserPrefsProvider.LoadPreferences().DiscordEventPosts))
                {
                    var name = ServerPrefsProvider.LoadPreferences(profile)?.Name;
                    DiscordWebhooks.SendLaunchHeld(
                        string.IsNullOrWhiteSpace(name) ? profile : name, reason);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not post the held start to Discord");
            }
        }

        /// <summary>
        /// The prefs a start-shaped call is talking about: the payload when the UI sent one,
        /// otherwise whatever is saved for the active profile.
        /// </summary>
        private ServerPreferences ResolveStartPrefs(JObject p)
        {
            var payload = p?["prefs"];
            var prefs = payload is JObject sent && sent.HasValues ? sent.ToObject<ServerPreferences>() : null;
            prefs ??= ServerPrefsProvider.LoadPreferences(ActiveProfileName) ?? new ServerPreferences();
            return MergeLaunchHistory(prefs);
        }

        /// <summary>
        /// The WebUI builds its prefs payload out of form fields, which do not include the
        /// launch history - so a plain round-trip would erase it and make every start look
        /// like the first one. Fill those two fields back in from what is on disk.
        /// <para>
        /// Disk wins whenever it has a value. StoreLaunchedBuild is the only thing that ever
        /// writes these two fields, so a value the browser sent is at best a copy of an older
        /// disk read: the page could have been loaded before the last launch recorded its
        /// build. Taking the browser's word for it whenever both fields happened to be filled
        /// in is how a start gets checked against a build the server has already moved past.
        /// </para>
        /// </summary>
        private ServerPreferences MergeLaunchHistory(ServerPreferences prefs)
        {
            if (prefs == null) return null;

            try
            {
                var stored = ServerPrefsProvider.LoadPreferences(
                    string.IsNullOrWhiteSpace(prefs.ProfileName) ? ActiveProfileName : prefs.ProfileName);
                return MergeLaunchHistory(prefs, stored);
            }
            catch { /* an unreadable profile just means the guard asks again */ }

            return prefs;
        }

        /// <summary>
        /// The rule itself, with the disk read already done, so it can be exercised on its own:
        /// what is stored wins whenever it has a value, and the payload only fills the gaps.
        /// </summary>
        public static ServerPreferences MergeLaunchHistory(ServerPreferences prefs, ServerPreferences stored)
        {
            if (prefs == null) return null;
            if (stored == null) return prefs;

            if (!string.IsNullOrWhiteSpace(stored.LastLaunchedServerBuild))
                prefs.LastLaunchedServerBuild = stored.LastLaunchedServerBuild;
            if (!string.IsNullOrWhiteSpace(stored.LastLaunchedServerFingerprint))
                prefs.LastLaunchedServerFingerprint = stored.LastLaunchedServerFingerprint;
            if (!string.IsNullOrWhiteSpace(stored.LastLaunchedGameVersion))
                prefs.LastLaunchedGameVersion = stored.LastLaunchedGameVersion;

            return prefs;
        }

        /// <summary>
        /// Whether a held launch is worth a Discord post. The post exists to reach somebody who
        /// is not at the keyboard, so only a launch BakaLoader decided to make on its own earns
        /// one; a start the host just pressed is answered by the banner in front of them.
        /// </summary>
        public static bool ShouldAnnounceHold(bool automatic, bool discordEventPosts)
            => automatic && discordEventPosts;

        /// <summary>
        /// The other profile keeping its worlds in <paramref name="saveFolder"/>, or null when
        /// this one has it to itself. Deleting a profile's files has to stop when the answer is
        /// not null, or another realm's worlds go with them.
        /// </summary>
        public static ServerPreferences ProfileSharingSaveFolder(
            IEnumerable<ServerPreferences> all, string profileName, string saveFolder)
        {
            if (all == null || string.IsNullOrWhiteSpace(saveFolder)) return null;

            return all.FirstOrDefault(other =>
                other != null
                && !string.Equals(other.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)
                && SameFolder(other.SaveDataFolderPath, saveFolder));
        }

        /// <summary>
        /// The last gate in front of a world delete: the host has to write the world's own
        /// name out. Whitespace on either side is forgiven, because a name copied off the
        /// screen often brings some with it. A different capitalisation is not forgiven: the
        /// whole point of the box is that the host reads the name and types it back, and
        /// "midgard" for "Midgard" is the sign of someone who has not read it.
        /// </summary>
        public static bool DeleteWorldNameConfirmed(string world, string typed)
        {
            if (string.IsNullOrWhiteSpace(world) || typed == null) return false;

            return string.Equals(world.Trim(), typed.Trim(), StringComparison.Ordinal);
        }

        /// <summary>
        /// The realm that still has this world chosen, or null when nothing points at it.
        /// <para>
        /// A realm with no save folder of its own keeps its worlds in the user-level folder, so
        /// a blank path means "the shared folder", not "somewhere else". Reading a blank as
        /// somewhere else is how a delete would take the world out from under a realm that is
        /// simply stopped, which is the whole reason this rule exists.
        /// </para>
        /// <para>
        /// Archived realms count too. An archived realm is one the host means to bring back,
        /// and it would come back pointed at a world that is no longer there.
        /// </para>
        /// </summary>
        public static ServerPreferences ProfileSelectingWorld(
            IEnumerable<ServerPreferences> all, string world, string saveFolder, string userSaveFolder)
        {
            if (all == null || string.IsNullOrWhiteSpace(world) || string.IsNullOrWhiteSpace(saveFolder))
                return null;

            return all.FirstOrDefault(pr =>
                pr != null
                && string.Equals(pr.WorldName, world, StringComparison.OrdinalIgnoreCase)
                && SameFolder(
                    string.IsNullOrWhiteSpace(pr.SaveDataFolderPath) ? userSaveFolder : pr.SaveDataFolderPath,
                    saveFolder));
        }

        /// <summary>
        /// The allowlist every caller-supplied world reference goes through before it reaches
        /// disk: a plain world name, one of the two subfolders the game uses, and a save folder
        /// BakaLoader already knows about. Throws with the reason; returns nothing on success.
        /// </summary>
        public static void ValidateWorldSourceRef(
            string world, string folder, string sub, IEnumerable<string> knownSaveFolders)
        {
            if (!WorldStore.IsSafeReferenceToken(world))
                throw new ArgumentException("Invalid characters in the world name.");

            if (!WorldStore.WorldSubfolders.Contains(sub, StringComparer.Ordinal))
                throw new ArgumentException("Invalid save subfolder.");

            if (string.IsNullOrWhiteSpace(folder)
                || knownSaveFolders == null
                || !knownSaveFolders.Any(k => SameFolder(k, folder)))
                throw new ArgumentException("Unknown save folder.");
        }

        /// <summary>
        /// Whether a name may be used as a backup reference at all. The live world exists on
        /// disk under its own name, so "it is there" is not the question: only a name shaped
        /// like a backup layer of that world can ever be one.
        /// </summary>
        public static bool IsUsableBackupReference(string file, string world)
            => WorldStore.IsBackupShapedFor(file, world);

        /// <summary>
        /// Turns a caller-supplied world-modifier map into the validated set BakaLoader stores.
        /// Every dial is checked against the game's own vocabulary (<see cref="WorldGen.Modifiers"/>);
        /// a missing, empty, or "normal" value means "game default" and drops the key so no
        /// -modifier argument is ever emitted for it, while an unknown dial or an out-of-range
        /// value throws with a message the UI can show. Both realm creation and Save Config run
        /// through this one gate so the two can never validate the same thing differently.
        /// </summary>
        public static Dictionary<string, string> ParseWorldModifiers(JObject raw)
        {
            var modifiers = new Dictionary<string, string>();
            foreach (var prop in raw ?? new JObject())
            {
                var value = prop.Value?.Value<string>();
                if (string.IsNullOrWhiteSpace(value) || value == "normal") continue;
                if (!WorldGen.Modifiers.TryGetValue(prop.Key, out var allowed) || !allowed.Contains(value))
                    throw new ArgumentException($"'{value}' is not a valid {prop.Key} setting");
                modifiers[prop.Key] = value;
            }
            return modifiers;
        }

        /// <summary>
        /// Turns a caller-supplied world-switch list into the set BakaLoader stores, or null
        /// when the caller did not send one at all.
        /// <para>
        /// Absent and empty are different answers and both are kept. A page that knows nothing
        /// about switches sends no <c>keys</c> at all, and null means "leave the stored keys
        /// exactly as they are"; an empty array is a host who turned every switch off, and that
        /// clears them. Making absent mean empty would let an older page wipe a world's
        /// switches every time it saved a dial.
        /// </para>
        /// <para>
        /// Only the five switches are accepted here, because only the five are what this list
        /// carries: the page draws a toggle each, and <c>keys</c> replaces that subset and
        /// nothing else. The keys a world carries that no toggle represents never travel
        /// through here at all, so nothing the pass-through must keep can be refused by it.
        /// While <see cref="ValheimServerOptions.Validate"/> is unreachable this is the only
        /// guard on keys there is.
        /// </para>
        /// </summary>
        public static HashSet<string> ParseWorldKeys(JToken raw)
        {
            if (raw == null || raw.Type == JTokenType.Null || raw.Type == JTokenType.Undefined) return null;

            if (raw is not JArray list)
                throw new HostFacingException("worldgen.keysNotAList",
                    "The world switches must be sent as a list.");

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in list)
            {
                var key = (entry?.Value<string>() ?? "").Trim().ToLowerInvariant();
                if (key.Length == 0) continue;

                if (!WorldGen.IsSwitch(key))
                    throw new HostFacingException("worldgen.unknownKey",
                        $"'{key}' is not a world switch.", ("key", key));

                keys.Add(key);
            }

            return keys;
        }

        /// <summary>
        /// The stored keys after a save that carried the switch list. Everything that is not
        /// one of the five switches is kept exactly as it was: a host may already carry
        /// <c>carryweightrate 150</c> on a world, and a save of the five toggles is not a
        /// statement about it. A null choice is no statement about the switches either, and
        /// leaves the whole stored set alone.
        /// </summary>
        public static HashSet<string> MergeWorldKeys(IEnumerable<string> stored, HashSet<string> chosenSwitches)
        {
            var kept = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in stored ?? Enumerable.Empty<string>())
            {
                var normalised = (key ?? "").Trim().ToLowerInvariant();
                if (normalised.Length == 0) continue;
                if (chosenSwitches != null && WorldGen.IsSwitch(normalised)) continue;
                kept.Add(normalised);
            }

            if (chosenSwitches != null) foreach (var key in chosenSwitches) kept.Add(key);
            return kept;
        }

        /// <summary>
        /// The restore safety layer's name. Local time on purpose: the game names its own
        /// restore layers with the local clock, BakaLoader's pre-update layers are local, and
        /// the Barrow reads all of them back as a wall clock.
        /// </summary>
        public static string RestoreLayerName(string world) => RestoreLayerName(world, DateTime.Now);

        public static string RestoreLayerName(string world, DateTime whenLocal)
            => $"{world}_backup_restore-{whenLocal:yyyyMMdd-HHmmss}";

        /// <summary>
        /// Whether a pre-update snapshot actually protected anything. A pass that finished
        /// cleanly but copied nothing is only good news when there was nothing to copy; with
        /// worlds on disk it means zero bytes were protected, which is a failure (LG-10).
        /// </summary>
        public static bool SnapshotProtectedTheWorlds(WorldStore.WorldSnapshotResult result, bool hadWorlds)
        {
            if (result == null || !result.Ok) return false;
            return result.Copied.Count > 0 || !hadWorlds;
        }

        /// <summary>
        /// Stages the host's answer to the launch guard for the next launch of that profile.
        /// Anything other than the two known answers is ignored, so the guard is never
        /// cleared by a stray parameter.
        /// </summary>
        private void StageLaunchAnswer(string profile, string answer)
        {
            if (string.IsNullOrWhiteSpace(profile)) return;

            if (string.Equals(answer, "backup", StringComparison.OrdinalIgnoreCase)
                || string.Equals(answer, "proceed", StringComparison.OrdinalIgnoreCase))
            {
                LaunchOverrides[profile] = (DateTime.UtcNow, answer.ToLowerInvariant());
                return;
            }

            // No answer this time: make sure a stale one cannot clear a fresh question.
            LaunchOverrides.TryRemove(profile, out _);
        }

        /// <summary>
        /// Takes a staged answer back when the launch it was staged for never happened, so no
        /// later launch can consume it.
        /// </summary>
        private void DropLaunchAnswer(string profile)
        {
            if (string.IsNullOrWhiteSpace(profile)) return;
            LaunchOverrides.TryRemove(profile, out _);
        }

        private void ClearLaunchHold(string profile)
        {
            if (LaunchHolds.TryRemove(profile, out _))
            {
                PostEvent("server.launchHoldCleared", new { profile });
            }
        }

        /// <summary>One plain sentence for the log and the Discord post.</summary>
        private static string HoldReason(LaunchGuardOutcome outcome, LaunchContext context)
        {
            if (outcome == LaunchGuardOutcome.UpdatePending)
            {
                var mb = Math.Max(1, (context.Current?.PendingBytes ?? 0) / 1048576);
                return $"Steam has a server update waiting ({mb} MB). Starting now would run the old build.";
            }

            var from = string.IsNullOrWhiteSpace(context.LastLaunchedBuild)
                ? "an unknown build"
                : "build " + ServerBuildInfo.Short(context.LastLaunchedBuild);
            var to = "build " + ServerBuildInfo.Short(context.Current?.Identity);
            return $"The server changed from {from} to {to}. Starting will upgrade the worlds to the new version.";
        }

        /// <summary>Everything the WebUI needs to phrase the question and offer the three answers.</summary>
        private object BuildLaunchGuardDto(string profile, LaunchContext context, LaunchGuardOutcome outcome)
        {
            var current = context.Current;
            return new
            {
                profile,
                outcome = LaunchGuard.Token(outcome),
                automatic = context.Automatic,
                reason = context.Reason,
                currentBuild = current?.Identity,
                currentBuildShort = ServerBuildInfo.Short(current?.Identity),
                source = current?.Source.ToString(),
                pendingBytes = current?.PendingBytes ?? 0,
                lastBuild = context.LastLaunchedBuild,
                lastBuildShort = ServerBuildInfo.Short(context.LastLaunchedBuild),
                lastVersion = context.LastLaunchedGameVersion,
                hasWorlds = context.HasWorlds,
                manifest = current?.ManifestPath,
                message = HoldReason(outcome, context),
            };
        }

        /// <summary>
        /// Answers the same question the guard asks, without starting anything - so the Start
        /// button can put the choice to the host BEFORE the server goes anywhere.
        /// </summary>
        private object BuildLaunchCheck(string profile, IValheimServerOptions options, ServerPreferences prefs)
        {
            Tools.ServerBuildInfo current = null;
            try
            {
                current = ServerBuildTracker.Probe(options.ServerExePath);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not read the server build for profile {profile}", profile);
            }

            var hasWorlds = false;
            try
            {
                hasWorlds = WorldStore.Enumerate(options.SaveDataFolderPath).Count > 0;
            }
            catch { /* an unreadable save folder is not a reason to block a start */ }

            var context = new LaunchContext
            {
                Automatic = false,
                Reason = LaunchReasons.Manual,
                Current = current,
                LastLaunchedBuild = prefs.LastLaunchedServerBuild,
                LastLaunchedFingerprint = prefs.LastLaunchedServerFingerprint,
                LastLaunchedGameVersion = prefs.LastLaunchedGameVersion,
                HasWorlds = hasWorlds,
                ProfileName = profile,
                Options = options,
            };

            var outcome = LaunchGuard.Decide(
                current, context.LastLaunchedBuild, context.LastLaunchedFingerprint, hasWorlds);
            return BuildLaunchGuardDto(profile, context, outcome);
        }

        #endregion

        #region BepInEx

        /// <summary>
        /// Every profile BakaLoader knows about and the install it runs from, with whether
        /// its server is up right now. This is what the sharing rule is asked about: two
        /// profiles whose exe paths hoist to the same base write through one BepInEx, and one
        /// of them being up is what makes writing it unsafe.
        /// <para>
        /// Run state comes off the process-wide session registry rather than this window's
        /// own sessions, because BakaLoader opens one window per auto-start profile and the
        /// world that would be harmed is very often the one in the window next door.
        /// </para>
        /// </summary>
        private IReadOnlyList<Tools.BepInExProfileInstall> KnownInstalls()
        {
            var live = new Dictionary<string, ServerSession>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var session in SessionRegistry.All)
                {
                    if (session?.ProfileName == null) continue;
                    if (!live.ContainsKey(session.ProfileName)) live[session.ProfileName] = session;
                }
            }
            catch (Exception e)
            {
                AppLogger.Debug("Could not read the live session registry: {0}", e.Message);
            }

            var names = new List<string>();
            try
            {
                names.AddRange(ServerPrefsProvider.LoadPreferences()
                    .Select(pref => pref.ProfileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name)));
            }
            catch (Exception e)
            {
                AppLogger.Debug("Could not read the server profiles: {0}", e.Message);
            }

            foreach (var name in live.Keys) names.Add(name);

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name =>
                {
                    live.TryGetValue(name, out var session);
                    var exe = session?.Server?.Options?.ServerExePath;
                    if (string.IsNullOrWhiteSpace(exe)) exe = GetServerExePathFor(name);

                    return new Tools.BepInExProfileInstall
                    {
                        ProfileName = name,
                        ServerExePath = exe,
                        Running = session != null && session.Server.Status != ServerStatus.Stopped,
                    };
                })
                .Where(install => !string.IsNullOrWhiteSpace(install.ServerExePath))
                .ToList();
        }

        /// <summary>
        /// The same answer as <see cref="KnownInstalls"/>, built again every time the sequence
        /// is walked.
        /// <para>
        /// The writer asks its refusal twice, once before the download and once with the
        /// archive on disk and nothing written yet, and a list handed in would answer the
        /// second ask with the first one's snapshot: a realm started during a fifty megabyte
        /// fetch would never be seen. An iterator method re-runs its body on every enumeration,
        /// which is exactly the re-reading this needs and costs nothing where nobody asks twice.
        /// </para>
        /// </summary>
        private IEnumerable<Tools.BepInExProfileInstall> LiveInstalls()
        {
            foreach (var install in KnownInstalls()) yield return install;
        }

        /// <summary>
        /// How the last BepInEx write that nobody watched ended: it wrote, it declined on a
        /// rule, or it threw.
        /// <para>
        /// A write inside a restart window is the one write with no audience, so the ordinary
        /// answer to "what happened" (the reply to the call that asked for it) does not exist.
        /// This is what stands in its place, and it stands until it has been drawn: a host who
        /// closes the app at midnight and opens it at nine still reads what their loader did.
        /// </para>
        /// </summary>
        private sealed class BepInExUnattendedOutcome
        {
            /// <summary>written | healed | refused | failed.</summary>
            public string Outcome { get; init; }

            public string Profile { get; init; }

            /// <summary>What was here before the write, for an outcome that wrote.</summary>
            public string FromVersion { get; init; }

            /// <summary>What is here now.</summary>
            public string ToVersion { get; init; }

            public string BackupStamp { get; init; }

            /// <summary>
            /// A <see cref="Tools.BepInExSkipReason"/> when the write declined on a rule, or
            /// the id of the refusal it threw when it failed outright.
            /// </summary>
            public string Reason { get; init; }

            /// <summary>The pack that was on offer, when the reason is about a pack.</summary>
            public string Version { get; init; }

            /// <summary>
            /// The loader version on disk here. The "this one is newer than the pack" sentence
            /// names both numbers, and it is the only sentence that can.
            /// </summary>
            public string InstalledVersion { get; init; }

            /// <summary>When a pack that is still soaking becomes old enough to go in.</summary>
            public DateTime? EligibleUtc { get; init; }

            public DateTime WhenUtc { get; init; }
        }

        /// <summary>Records what an unattended write came to, for the next page that asks.</summary>
        private void RecordUnattendedBepInEx(string profile, Tools.BepInExInstallResult result)
        {
            if (result == null) return;

            if (result.Skipped)
            {
                _bepInExLastUnattended = new BepInExUnattendedOutcome
                {
                    Outcome = "refused",
                    Profile = profile,
                    Reason = result.SkipReason,
                    Version = result.Version,
                    InstalledVersion = result.PreviousCoreVersion,
                    EligibleUtc = result.EligibleUtc,
                    WhenUtc = DateTime.UtcNow,
                };
                return;
            }

            // An adoption that found the pack already there moved no bytes. Saying "BepInEx
            // went from 5.4.2350 to 5.4.2350" would be a made-up sentence, and a refusal an
            // earlier window raised has stopped being true either way.
            if (result.NothingChanged)
            {
                _bepInExLastUnattended = null;
                return;
            }

            // A write that was UNDONE, which is the only shape a restore takes when nobody
            // pressed anything: a write stopped part way, and the copy from before it went
            // back. Nothing was updated and nothing was put in, so it cannot ride on the
            // written wording. It used to, and what a host read was "BepInEx 5.4.23.5 was put
            // in at a restart. Anything it replaced is kept in the BepInEx backups folder",
            // pointing at a folder that a heal over a missing core does not even leave behind.
            if (result.Healed || result.Restored)
            {
                _bepInExLastUnattended = new BepInExUnattendedOutcome
                {
                    Outcome = "healed",
                    Profile = profile,
                    ToVersion = result.CoreVersion,
                    BackupStamp = result.BackupStamp,
                    WhenUtc = DateTime.UtcNow,
                };
                return;
            }

            _bepInExLastUnattended = new BepInExUnattendedOutcome
            {
                Outcome = "written",
                Profile = profile,
                FromVersion = result.PreviousVersion,
                ToVersion = result.Version,
                BackupStamp = result.BackupStamp,
                WhenUtc = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// Records that an unattended write threw, with the id the page words it from.
        /// <para>
        /// A Thunderstore that did not answer is deliberately not one of these. Nothing was
        /// written and nothing is wrong with the install: it was a bad minute on the internet,
        /// the next restart asks again, and a standing row about it would be a thing the host
        /// has to read and dismiss for every hiccup between here and Sweden.
        /// </para>
        /// </summary>
        private void RecordUnattendedBepInExFailure(string profile, Exception error)
        {
            var id = HostFacingException.IdOf(error) ?? "bepinex.writeFailed";
            if (string.Equals(id, "bepinex.offline", StringComparison.Ordinal)) return;

            // A repair whose own pack the site no longer serves IS one of these, and it is the
            // one that most needs to be. It reads like a hiccup and is not one: the note names a
            // version that has been taken down, so every restart from here on asks for a file
            // that is not there and the install stays broken with nothing said. Both versions
            // ride along so the row can name what is here and offer the pack that IS served.
            var values = HostFacingException.ParamsOf(error);
            string Value(string name)
                => values != null && values.TryGetValue(name, out var v) ? v as string : null;

            _bepInExLastUnattended = new BepInExUnattendedOutcome
            {
                Outcome = "failed",
                Profile = profile,
                Reason = id,
                // For bepinex.repairPackGone: what the site offers now, and what this install
                // was written from. Null for every other reason, exactly as before.
                Version = Value("offered"),
                InstalledVersion = Value("noted"),
                WhenUtc = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// The row the Mods page draws above the table, and the payload of bepinex.changed.
        /// One shape for both, so a page that read the event never has to ask again.
        /// </summary>
        /// <param name="result">
        /// What the write this answer is about came to, when there is one: the reply to a call
        /// that asked for a write, and the event an unattended write posts when it is over.
        /// Null on a plain status read, where there is no one write to report.
        /// </param>
        private object BuildBepInExDto(Tools.BepInExInstallResult result = null)
        {
            var prefs = UserPrefsProvider.LoadPreferences();
            var baseExe = GetCanonicalBaseServerExe();
            var status = BepInEx.Status(baseExe, GetPluginsDirectory(), KnownInstalls());

            return new
            {
                installed = status.Installed,
                baseFolder = status.BaseFolder,
                // The pack version when BakaLoader wrote it, the assembly's own file version
                // when somebody else did. They are not the same number and never have been:
                // pack 5.4.2350 ships BepInEx 5.4.23.5.
                maintainedByBakaLoader = status.MaintainedByBakaLoader,
                packVersion = status.PackVersion,
                coreFileVersion = status.CoreFileVersion,
                package = status.Package,
                source = status.Source,
                installedUtc = status.InstalledUtc,
                wrongLocationFolder = status.WrongLocationFolder,
                sharingProfiles = status.SharingProfiles,
                runningProfiles = status.RunningProfiles,
                maintained = prefs.BepInExMaintained,
                maintenanceAsked = prefs.BepInExMaintenanceAsked,
                // The switch and the answer read together, which is the only reading that is
                // true before the host has said anything: the preference defaults to on, so on
                // its own it says yes on behalf of somebody who has not spoken.
                consent = Tools.BepInExConsent.Effective(
                    prefs.BepInExMaintained, prefs.BepInExMaintenanceAsked),
                consentUnanswered = Tools.BepInExConsent.Unanswered(prefs.BepInExMaintenanceAsked),
                // What the host's own doorstop_config.ini names, and whether that is this
                // install's BepInEx or another tool's profile folder.
                doorstopTarget = status.DoorstopTarget,
                drivenElsewhere = status.DrivenElsewhere,
                // A core that is not a 5.x BepInEx at all.
                foreignCore = status.ForeignCore,
                // The assembly's own version whenever there is one, note or no note.
                coreVersion = status.CoreVersion,
                // winhttp.dll beside the server, which is half of what "installed" means, and
                // the name of that file so the row can say which one has gone without the page
                // carrying a copy of it. The other half is the core assembly itself, which is
                // the plain "there is a loader here" fact whether or not it has a readable
                // version and whether or not the loose file beside it is still there.
                loaderFilePresent = status.LoaderFilePresent,
                loaderFileName = Tools.BepInExService.LoaderFileName,
                coreFilePresent = status.CoreFilePresent,
                // BepInEx/core is there and BepInEx.dll is not, and whether what IS there is
                // something BakaLoader did not put down.
                damaged = status.Damaged,
                unrecognised = status.Unrecognised,
                // Files the note lists that are gone: antivirus is the usual cause.
                missingFiles = status.MissingFiles,
                // Somebody else wrote over the files the note recorded, so BakaLoader gave up
                // ownership rather than taking the install back off them.
                drifted = status.Drifted,
                // The backup the restore would put back, and whether a write was interrupted.
                newestBackup = status.NewestBackup,
                // The backup holding the loader this host had before BakaLoader, which is the
                // one copy of it there will ever be and the one the keep-three rule steps over.
                adoptionBackup = status.AdoptionBackup,
                adoptionBackupCoreVersion = status.AdoptionBackupCoreVersion,
                interruptedWrite = status.InterruptedWrite,
                coreIsJunction = status.CoreIsJunction,
                // Set by the unattended window when it found a newer pack and could not write
                // it because another server on this install was up.
                updateWaiting = _bepInExUpdateWaiting,
                // True for ALL four writers, so the row never draws its buttons enabled while
                // an unattended window or a start is writing the very files they would write.
                busy = BepInExWriteInProgress,
                // What the write this answer is about came to. Null on a plain status read.
                // The three states a write can leave an install in (written to, adopted with
                // nothing changed, left alone) all look the same in the fields above, because
                // all three end with an install that is there: this is the only thing that
                // tells them apart.
                result = result == null ? null : new
                {
                    skipped = result.Skipped,
                    skipReason = result.SkipReason,
                    nothingChanged = result.NothingChanged,
                    alreadyCurrent = result.AlreadyCurrent,
                    adopted = result.Adopted,
                    replaced = result.Replaced,
                    restored = result.Restored,
                    healed = result.Healed,
                    doorstopReplaced = result.DoorstopReplaced,
                    version = result.Version,
                    previousVersion = result.PreviousVersion,
                    previousPackVersion = result.PreviousPackVersion,
                    previousCoreVersion = result.PreviousCoreVersion,
                    coreVersion = result.CoreVersion,
                    backupStamp = result.BackupStamp,
                    backupPath = BepInExBackupPath(status.BaseFolder, result.BackupStamp),
                    isolatedInstallsLinked = result.IsolatedInstallsLinked,
                    profileLoaderFilesRefreshed = result.ProfileLoaderFilesRefreshed,
                    eligibleUtc = result.EligibleUtc,
                },
                // How the last write nobody watched ended. It stands until a page has drawn
                // it, so a host who opens the window the next morning still reads it.
                lastUnattended = BepInExUnattendedDto(),
            };
        }

        /// <summary>
        /// Where the files a write replaced went, as a path a host can paste into Explorer, or
        /// null when that write replaced nothing.
        /// </summary>
        private static string BepInExBackupPath(string baseFolder, string stamp)
        {
            if (string.IsNullOrWhiteSpace(baseFolder) || string.IsNullOrWhiteSpace(stamp)) return null;

            try { return Path.Combine(baseFolder, "BepInEx", Tools.BepInExService.BackupDirName, stamp); }
            catch { return null; }
        }

        /// <summary>
        /// How the last unattended write ended, as the page reads it, or null when there has
        /// not been one. A refused or failed one always carries <c>leftAsItWas</c>: every
        /// reason on that path ends with the install untouched, and that is the half a host
        /// most needs to be told.
        /// </summary>
        private object BepInExUnattendedDto()
        {
            var last = _bepInExLastUnattended;
            return last == null ? null : new
            {
                outcome = last.Outcome,
                profile = last.Profile,
                fromVersion = last.FromVersion,
                toVersion = last.ToVersion,
                backupStamp = last.BackupStamp,
                backupFolder = Tools.BepInExService.BackupDirName,
                reason = last.Reason,
                version = last.Version,
                installedVersion = last.InstalledVersion,
                eligibleUtc = last.EligibleUtc,
                // A heal is not one of these either: it MOVED files, it just moved them back.
                // The row that carries "the install was left exactly as it was" is for the
                // window that declined or threw.
                leftAsItWas = last.Outcome != "written" && last.Outcome != "healed",
                whenUtc = last.WhenUtc,
            };
        }

        /// <summary>
        /// Asks the load check once more, a little after the grace has passed, and sends the
        /// answer on the status event. Fire and forget: a window that closed in the meantime
        /// drops the push inside PostEvent, and a session that stopped answers false.
        /// </summary>
        private void ScheduleBepInExLoadCheck(ServerSession session)
        {
            var when = TimeSpan.FromSeconds(Tools.BepInExLoadCheck.DefaultGraceSeconds + 5);
            _ = Task.Delay(when).ContinueWith(_ =>
            {
                try
                {
                    if (IsDisposed) return;
                    if (session.Server.Status != ServerStatus.Running) return;
                    PostEvent("server.status", BuildServerState(session));
                }
                catch (Exception e)
                {
                    AppLogger.Debug("The BepInEx load check could not report: {0}", e.Message);
                }
            });
        }

        /// <summary>
        /// Tells every page the BepInEx answer has changed, and what it changed to.
        /// </summary>
        /// <param name="result">
        /// What the write that changed it came to, when this push follows one. An unattended
        /// write has no reply to carry it, so the event is where the page reads it, and it is
        /// the same object the three write calls answer with.
        /// </param>
        private void PostBepInExChanged(Tools.BepInExInstallResult result = null)
        {
            try { PostEvent("bepinex.changed", BuildBepInExDto(result)); }
            catch (Exception e) { AppLogger.Debug("Could not post bepinex.changed: {0}", e.Message); }
        }

        /// <summary>
        /// The one place a host-driven BepInEx write goes through: the busy guard, the
        /// already-looked-after notice, the progress events and the changed event, so
        /// bepinex.install and bepinex.update cannot drift apart.
        /// </summary>
        /// <summary>
        /// The answers a page has already collected from the host, read off the call. Each one
        /// is a refusal the service makes on its own unless it is told the host was shown what
        /// it would cost and said yes; a page that forgets to ask simply gets the refusal.
        /// </summary>
        private static Tools.BepInExWriteOptions BepInExOptionsFrom(JObject call) => new()
        {
            AllowDowngrade = call?.Value<bool?>("allowDowngrade") == true,
            OverUnrecognised = call?.Value<bool?>("overUnrecognised") == true,
            OverOutside = call?.Value<bool?>("overOutside") == true,
            OverDrivenElsewhere = call?.Value<bool?>("overDrivenElsewhere") == true,
            OverForeign = call?.Value<bool?>("overForeign") == true,
            TakeCurrentPack = call?.Value<bool?>("takeCurrentPack") == true,
        };

        private async Task<object> WriteBepInExAsync(
            string url, bool update, Tools.BepInExWriteOptions options = null)
        {
            if (!TryBeginBepInExWrite()) throw BepInExBusy();

            try
            {
                // While BakaLoader is looking after BepInEx, the source is BakaLoader's to
                // choose. A host who hands it another one is told where the setting is rather
                // than having their link quietly ignored or quietly obeyed. Read through the
                // consent rule: a host who has not been asked yet is not being looked after.
                var writePrefs = UserPrefsProvider.LoadPreferences();
                if (!string.IsNullOrWhiteSpace(url) && Tools.BepInExConsent.Effective(
                        writePrefs.BepInExMaintained, writePrefs.BepInExMaintenanceAsked))
                    throw new HostFacingException("bepinex.alreadyMaintained",
                        "BepInEx is already looked after by BakaLoader.");

                var progress = new SynchronousProgress<Tools.BepInExProgress>(pr =>
                    PostEvent("bepinex.progress",
                        new { phase = pr.Phase, percent = pr.Percent, version = pr.Version }));

                var baseExe = GetCanonicalBaseServerExe();
                var installs = LiveInstalls();

                var result = update
                    ? await BepInEx.UpdateAsync(baseExe, installs, progress, options: options)
                    : await BepInEx.InstallAsync(baseExe, url, installs, progress, options: options);

                RecordBepInExInstall(ActiveProfileName, result);
                _bepInExUpdateWaiting = null;

                // A press answers whatever an earlier window raised. The host is looking at
                // the reply, so an outcome still standing about the same install would be a
                // second telling of something they have just dealt with.
                if (!result.Skipped) _bepInExLastUnattended = null;

                return BuildBepInExDto(result);
            }
            finally
            {
                EndBepInExWrite();
                PostBepInExChanged();
            }
        }

        /// <summary>Writes a BepInEx install into the journal beside the mod installs.</summary>
        private void RecordBepInExInstall(string profile, Tools.BepInExInstallResult result)
        {
            if (result == null || !result.Installed) return;

            // A write that was refused and a write that found the pack already in place both
            // answer "installed", because the install IS there. Neither of them moved a
            // version, and a journal entry saying one did would be a made-up line.
            if (result.Skipped || result.NothingChanged) return;

            try
            {
                Analytics.Record(new AnalyticsEvent
                {
                    Kind = result.Replaced ? "modup" : "modin",
                    Server = profile,
                    Mod = result.Package,
                    FromVersion = result.PreviousVersion,
                    ToVersion = result.Version,
                });
            }
            catch (Exception e)
            {
                AppLogger.Debug("Could not record the BepInEx install: {0}", e.Message);
            }
        }

        /// <summary>
        /// Whether a pasted Thunderstore package is the loader pack itself rather than a mod.
        /// </summary>
        private static bool IsBepInExPackReference(string owner, string name)
            => string.Equals(owner, Tools.BepInExService.DefaultPackageOwner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, Tools.BepInExService.DefaultPackageName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The verdict of the load check for one live session, or false when the question does
        /// not apply. Reads BepInEx/LogOutput.log beside the server the session actually runs,
        /// which on an isolated install is that install's own copy.
        /// </summary>
        private bool BepInExDidNotLoad(ServerSession session)
        {
            try
            {
                if (session == null) return false;
                if (session.Server.Status != ServerStatus.Running) return false;
                if (!ServerLaunchUtc.TryGetValue(session.ProfileName, out var startedUtc)) return false;

                var bepDir = session.Server.GetBepInExDirectory();
                if (string.IsNullOrWhiteSpace(bepDir)) return false;

                var installed = File.Exists(Path.Combine(bepDir, "core", Tools.BepInExService.CoreAssemblyName));
                DateTime? written = null;
                var log = Path.Combine(bepDir, Tools.BepInExLoadCheck.LogFileName);
                if (File.Exists(log)) written = File.GetLastWriteTimeUtc(log);

                return Tools.BepInExLoadCheck.DidNotLoad(installed, written, startedUtc, DateTime.UtcNow);
            }
            catch (Exception e)
            {
                AppLogger.Debug("Could not run the BepInEx load check: {0}", e.Message);
                return false;
            }
        }

        /// <summary>
        /// What server.status carries about BepInEx: the two standing conditions, and enough
        /// beside them for the page to word both without a second call.
        /// </summary>
        private object BuildBepInExState(ServerSession session)
        {
            try
            {
                var waiting = _bepInExUpdateWaiting;
                var statePrefs = UserPrefsProvider.LoadPreferences();
                return new
                {
                    maintained = statePrefs.BepInExMaintained,
                    // The switch and the answer together: unanswered is its own state and is
                    // not a yes, so the conditions that speak for BakaLoader read this one.
                    consent = Tools.BepInExConsent.Effective(
                        statePrefs.BepInExMaintained, statePrefs.BepInExMaintenanceAsked),
                    consentUnanswered = Tools.BepInExConsent.Unanswered(statePrefs.BepInExMaintenanceAsked),
                    updateWaiting = waiting,
                    waitingProfiles = waiting == null
                        ? Array.Empty<string>()
                        : Tools.BepInExService.ProfilesBlockingWrite(GetCanonicalBaseServerExe(), KnownInstalls())
                            .ToArray(),
                    notLoaded = BepInExDidNotLoad(session),
                    // How the last write nobody watched ended. Read straight off what this
                    // window is holding, so it costs this event nothing: it touches no install
                    // and asks the service nothing.
                    lastUnattended = BepInExUnattendedDto(),
                };
            }
            catch (Exception e)
            {
                AppLogger.Debug("Could not build the BepInEx state: {0}", e.Message);
                return null;
            }
        }

        /// <summary>
        /// The unattended BepInEx step, run inside the window that already applies mod
        /// updates: the server that is restarting is down, so the only question left is
        /// whether any OTHER server on this install is up. One that is means the write waits,
        /// and the condition bar says so rather than the update silently never happening.
        /// <para>
        /// Every step in here is on the unattended clock, and it has to be: ResumeAfterStopAsync
        /// AWAITS this step and relaunches after it, so a machine that cannot reach Thunderstore
        /// kept a host's server DOWN for as long as the version lookup's own deadlines allowed,
        /// on every restart. The lookup below is handed UnattendedResolveTimeout for that
        /// reason, and when it runs out the version reads as unknown, which the rule already
        /// answers with Skip. The loader is left as it is and the relaunch goes ahead.
        /// </para>
        /// </summary>
        private async Task ApplyBepInExUpdateAsync(string profile)
        {
            try
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                var baseExe = GetCanonicalBaseServerExe();
                // Left lazy on purpose: the writer asks its refusal again after the download,
                // and this sequence answers with whoever is up at the moment it is asked.
                var installs = LiveInstalls()
                    .Where(i => !string.Equals(i.ProfileName, profile, StringComparison.OrdinalIgnoreCase));

                // A write that did not finish is finished here, before anything else looks at
                // the install: the window is the one moment this server is down.
                HealInterruptedBepInExWrite(baseExe, installs);

                // The answer is read BEFORE anything is asked of Thunderstore, and it is the
                // whole of what happens next when it is not a yes. It used to be read after the
                // version lookup, which meant every restart window asked the site which pack is
                // current whatever the host had answered: a host who said no, and a host who
                // has not been asked at all, still had their machine reach Sweden on every
                // restart. Nothing downstream of here can run without consent anyway, so asking
                // first was never buying anything.
                var consent = Tools.BepInExConsent.Effective(prefs.BepInExMaintained, prefs.BepInExMaintenanceAsked);

                // The rule carries the ORDER as well as the table: the answer is read first,
                // and without a yes the install is not read and Thunderstore is not asked
                // anything at all. The site used to be asked before the answer was, so every
                // restart window reached out whatever the host had said.
                var plan = await Tools.BepInExUnattended.PlanAsync(
                    consent,
                    () => BepInEx.Status(baseExe, GetPluginsDirectoryFor(profile), installs),
                    // Bounded: the relaunch is waiting on this one. See the note above.
                    package => BepInEx.LatestVersionAsync(package, BepInEx.UnattendedResolveTimeout),
                    () => Tools.BepInExService.ProfilesBlockingWrite(baseExe, installs).Count > 0);

                var decision = plan.Action;
                var latest = plan.Latest;

                if (decision == Tools.BepInExUnattendedAction.Skip)
                {
                    _bepInExUpdateWaiting = null;
                    return;
                }

                if (decision == Tools.BepInExUnattendedAction.Defer)
                {
                    _bepInExUpdateWaiting = latest;
                    Logger.Information(
                        "A newer BepInEx pack ({0}) is waiting for every server on this install to stop.", latest);
                    PostBepInExChanged();
                    return;
                }

                Tools.BepInExInstallResult windowResult = null;

                // The one write slot. A host pressing Install or Update on the row right now
                // is writing the same BepInEx/core, and an unattended window does not queue
                // behind a person: it waits for the next restart and the row says so.
                if (!TryBeginBepInExWrite())
                {
                    _bepInExUpdateWaiting = latest;
                    Logger.Information(
                        "A newer BepInEx pack ({0}) is waiting: another BepInEx write is running.", latest);
                    PostBepInExChanged();
                    return;
                }

                try
                {
                    // The slot is in hand and the write begins on the next line. Say so now:
                    // the DTO's busy flag is what greys the row's buttons on an open page, and
                    // a page told only when the write is over spends the whole of it offering
                    // presses that would be refused. The refusal is correctly worded either
                    // way, but a button that cannot work should not look like one that can.
                    PostBepInExChanged();

                    var result = await BepInEx.UpdateAsync(baseExe, installs,
                        options: Tools.BepInExWriteOptions.Window);
                    windowResult = result;
                    RecordBepInExInstall(profile, result);
                    RecordUnattendedBepInEx(profile, result);
                    _bepInExUpdateWaiting = null;

                    if (result.Skipped)
                        Logger.Information(
                            "BepInEx for profile {0} was left as it was at the restart window ({1}).",
                            profile, result.SkipReason);
                    else if (result.NothingChanged)
                        Logger.Information(
                            "BepInEx for profile {0} already held the current pack at the restart window.",
                            profile);
                    else
                        Logger.Information(
                            "BepInEx for profile {0} went from {1} to {2} at the restart window; what it "
                            + "replaced is in BepInEx\\{3}\\{4}.",
                            profile, result.PreviousVersion ?? "nothing", result.Version,
                            Tools.BepInExService.BackupDirName,
                            result.BackupStamp ?? "(nothing was replaced)");
                }
                finally
                {
                    EndBepInExWrite();
                }

                // The same object the three write calls answer with, on the event the page
                // already listens to. An unattended write has no reply of its own, so without
                // this a page that was open through it learns only that something changed.
                PostBepInExChanged(windowResult);
            }
            catch (Exception e)
            {
                // A restart must still happen. The world coming back up matters more than the
                // loader being a version behind. The warning alone is not telling anybody
                // though, so the reason stands on the row until somebody has read it.
                AppLogger.Warning("The unattended BepInEx step did not run: {0}", e.Message);
                RecordUnattendedBepInExFailure(profile, e);
                PostBepInExChanged();
            }
        }

        /// <summary>
        /// Finishes a BepInEx write that did not finish, and tells the host once that it did.
        /// <para>
        /// The mark a write leaves is only ever left by a write that stopped somewhere, and the
        /// one place stopping costs anything is the single rename between the old core going
        /// into the backup and the new one coming in. Asked here rather than inside the
        /// service's status read, so the repair happens under the one write slot every other
        /// writer holds.
        /// </para>
        /// </summary>
        private void HealInterruptedBepInExWrite(
            string baseExe, IEnumerable<Tools.BepInExProfileInstall> installs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(baseExe)) return;

                var status = BepInEx.Status(baseExe, null, installs);
                if (!status.InterruptedWrite) return;

                if (!TryBeginBepInExWrite()) return;

                Tools.BepInExInstallResult result;
                try { result = BepInEx.HealInterruptedWrite(baseExe, installs); }
                finally { EndBepInExWrite(); }

                if (result == null) return;

                Logger.Information(
                    "A BepInEx write here did not finish, so the copy from before it was put back.");

                // Nobody watched this either: it runs before a start and inside a window.
                RecordUnattendedBepInEx(null, result);
                PostBepInExChanged();
            }
            catch (Exception e)
            {
                // A repair that will not run is not a reason to stop a start or a restart.
                AppLogger.Warning("A BepInEx write could not be finished: {0}", e.Message);
            }
        }

        /// <summary>
        /// Puts BepInEx in place before a server that needs it starts, when BakaLoader is
        /// looking after it. Runs in the companion-plugin slot, which is the one moment in a
        /// start where the process is not up yet and the files are free.
        /// <para>
        /// It fires for every profile, and there is no "does this one have anything to load"
        /// question in front of it: the companion plugins ship inside BakaLoader and are
        /// installed on the very next lines of this same start, so the answer is always yes.
        /// There WAS such a check here, and it could only ever return true, which made it a
        /// condition on paper and nothing at all in the code.
        /// </para>
        /// </summary>
        private void PrepareBepInExForStart(string profile, string exePath)
        {
            // The switch and the answer together. A host upgrading from 1.1.x has the switch on
            // by default and has not been asked anything yet, and a profile that auto-starts
            // never reaches the question at all: reading the preference alone would put a
            // loader in on their behalf before they had a chance to say no.
            var startPrefs = UserPrefsProvider.LoadPreferences();
            if (!Tools.BepInExConsent.Effective(
                    startPrefs.BepInExMaintained, startPrefs.BepInExMaintenanceAsked)) return;

            // The base this profile's loader really lives in, resolved from the profile's own
            // preferences rather than from the path: an isolated install cannot say which of
            // its instances root's siblings the base is.
            var baseExe = GetCanonicalBaseServerExe(profile);
            if (string.IsNullOrWhiteSpace(baseExe)) baseExe = exePath;

            // A write that did not finish is finished before a start reads the install: this
            // server is down right now, which is the only time the files are free.
            HealInterruptedBepInExWrite(baseExe, LiveInstalls());

            var startStatus = BepInEx.Status(baseExe, GetPluginsDirectoryFor(profile), LiveInstalls());
            if (startStatus.Installed) return;

            // A core somebody else put here is never written over by a start. The row explains
            // it and the manual install asks first; a server coming up is not the place to make
            // that decision on the host's behalf.
            //
            // The last clause is the one that covers the ordinary case rather than the odd
            // ones. An install with no trusted note whose winhttp.dll an antivirus has taken
            // answers Installed false, so a start used to walk straight past the three checks
            // above and write a fresh pack over a core the host had put there themselves. What
            // the question promised was adoption at the next SCHEDULED RESTART, which is the
            // window: the whole install is down for it, every file it replaces goes into a
            // backup first, and the row says it is coming. A start is not that.
            if (startStatus.Unrecognised || startStatus.DrivenElsewhere || startStatus.ForeignCore
                || (startStatus.CoreFilePresent && !startStatus.MaintainedByBakaLoader))
            {
                AppLogger.Information(
                    "BepInEx was left as it is for the start of {0}: there is an install here BakaLoader "
                    + "did not make.", profile);
                return;
            }

            // The one write slot, and the only path that WAITS for it. A host who pressed
            // Install on the row a moment ago is writing the very files this start needs, and
            // two writers over one BepInEx/core is an install that starts neither version.
            // The wait outlasts the download's own timeout, so it ends because the other write
            // ended rather than in the middle of it.
            if (!BeginBepInExWrite(BepInExWriteWait)) throw BepInExBusy();

            Tools.BepInExInstallResult startResult = null;

            try
            {
                // Same reason as the restart-window write: the slot is held from here, so an
                // open page hears it now rather than after the install, and the row's buttons
                // grey out for as long as they would actually be refused.
                PostBepInExChanged();

                // Asked again with the slot in hand: the write this waited for may well have
                // been the loader going in, and there is then nothing left to do.
                var installs = LiveInstalls();
                if (BepInEx.Status(baseExe, GetPluginsDirectoryFor(profile), installs).Installed) return;

                var progress = new SynchronousProgress<Tools.BepInExProgress>(pr =>
                    PostEvent("bepinex.progress",
                        new { phase = pr.Phase, percent = pr.Percent, version = pr.Version }));

                // This waits. Every start in the app goes through the launch guard, and a
                // guarded launch runs StartCore on a task of its own precisely so a slow step
                // here cannot freeze the window; the installers around this one are
                // synchronous. The start itself is not held hostage: this runs through
                // Install("BepInEx", ...) in PrepareCompanionPlugins, so a failure here is
                // recorded against the BepInEx entry in CompanionPluginStatus and the launch
                // carries on, the same as any other companion plugin that could not be placed.
                try
                {
                    var result = BepInEx.InstallAsync(baseExe, null, installs, progress,
                            options: Tools.BepInExWriteOptions.Window)
                        .GetAwaiter().GetResult();

                    startResult = result;
                    RecordBepInExInstall(profile, result);
                    RecordUnattendedBepInEx(profile, result);
                }
                catch (Exception e)
                {
                    // Recorded and rethrown. The companion-plugin pass around this call is what
                    // puts a failed loader on that profile's condition bar, and it needs the
                    // throw to do it; the standing reason is for the Mods row, which has no
                    // other way to learn that a start tried and could not.
                    RecordUnattendedBepInExFailure(profile, e);
                    throw;
                }
            }
            finally
            {
                EndBepInExWrite();
                PostBepInExChanged(startResult);
            }
        }

        #endregion

        #region Analytics plumbing

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

            // A newer BakaLoader release. Every open window wants its own banner, and this
            // method already runs once per window, so the subscription belongs here.
            SoftwareUpdates.UpdateAvailable += (s, found) => PostAppUpdateAvailable(found);

            // The interface language. One window's globe menu changes it for the app, and the
            // service is the singleton every window shares, so every open window follows: this
            // method runs once per window, the same way the release banner above does. A window
            // that has gone is not a problem, because PostEvent drops a push to a dead handle.
            LanguagePacks.LanguageChanged += (s, changed) =>
            {
                RefreshHostCatalog();
                PostEvent("lang.changed", new { code = changed?.Code, version = changed?.Version });
            };

            // The server update the host started from this app. Progress is a push rather
            // than a poll: a steamcmd run prints for minutes and the bar follows it live.
            ServerUpdates.Progress += (s, progress) =>
            {
                if (!string.IsNullOrWhiteSpace(progress?.ProfileName))
                    ServerUpdatePhaseSeen[progress.ProfileName] = progress.Phase;

                PostEvent("server.updateProgress", new
                {
                    profile = progress.ProfileName,
                    phase = progress.Phase.ToString(),
                    percent = progress.Percent,
                    bytesDone = progress.BytesDone,
                    bytesTotal = progress.BytesTotal,
                    line = progress.Line,
                    message = progress.Message,
                });
            };

            ServerUpdates.Completed += (s, result) => OnServerUpdateCompleted(result);

            AppLogger.LogReceived += line => PostEvent("log.app", new { line });
        }

        /// <summary>
        /// Tells this window's UI that a newer BakaLoader release exists. Used both by the
        /// live event and by the replay on load.
        /// </summary>
        private void PostAppUpdateAvailable(AppUpdateAvailability found)
        {
            if (found == null || string.IsNullOrWhiteSpace(found.Version)) return;

            PostEvent("app.updateAvailable", new
            {
                version = found.Version,
                notesUrl = found.NotesUrl,
            });
        }

        /// <summary>
        /// The update check runs as a launch step, which finishes before any window is on
        /// screen - and a push made before a window has its handle is dropped with no second
        /// chance. The WebUI calls app.info first on every load, so that call replays whatever
        /// the check found. A window opened later, or a page reload, gets it the same way.
        /// </summary>
        private void ReplayAppUpdateAvailable()
        {
            try { PostAppUpdateAvailable(SoftwareUpdates?.LatestAvailable); }
            catch (Exception ex) { Logger.Warning(ex, "Could not replay the available app update"); }
        }

        /// <summary>
        /// Everything any screen needs to say the same thing about a waiting BakaLoader release:
        /// the sidebar, the Hearth pill, the standing row and the dialog all read this one answer,
        /// and it comes off the very object the push event is built from, so none of them can
        /// disagree with another about whether there is an update or what version it is.
        /// </summary>
        private object BuildAppUpdateStatus()
        {
            var prefs = LoadUserPrefsOrNull();

            return BuildAppUpdateStatusDto(
                SoftwareUpdates?.LatestAvailable,
                AssemblyHelper.GetApplicationVersion(),
                autoUpdateOnRestart: prefs?.AutoUpdateBakaLoader == true,
                checkEnabled: prefs?.CheckForUpdates == true,
                anyServerRunning: AnyServerRunning());
        }

        /// <summary>
        /// The user preferences, or null if reading them ever throws. The provider answers with
        /// defaults rather than failing, so this is the belt on top of that: a status payload is
        /// not worth taking a window down for.
        /// </summary>
        private UserPreferences LoadUserPrefsOrNull()
        {
            try { return UserPrefsProvider?.LoadPreferences(); }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not read the user preferences for the app update status");
                return null;
            }
        }

        /// <summary>
        /// The wire shape, built from the found release and nothing else. Separated so the
        /// answer can be checked against a real <see cref="AppUpdateAvailability"/> without a
        /// window: the point of this payload is that it agrees with the push event, and a shape
        /// only the running app can produce is a shape nothing can hold to that.
        /// </summary>
        public static object BuildAppUpdateStatusDto(
            AppUpdateAvailability latest,
            string installedVersion,
            bool autoUpdateOnRestart,
            bool checkEnabled,
            bool anyServerRunning)
        {
            var version = latest?.Version;
            var available = !string.IsNullOrWhiteSpace(version);

            return new
            {
                installedVersion,
                latestVersion = available ? version : null,
                updateAvailable = available,
                // The page of the release this check actually found, or null when the check did
                // not hand one over. Null is the honest answer there, and the page has somewhere
                // to send the host either way: with an address the app opens that release, and
                // without one it opens the releases page.
                releaseUrl = SpecificAppReleaseUrl(latest?.NotesUrl),
                autoUpdateOnRestart,
                checkEnabled,
                anyServerRunning,
            };
        }

        /// <summary>
        /// True while ANY server in the whole application is running, stopping, starting, or on
        /// its way back up.
        /// <para>
        /// This used to read only the sessions belonging to this window, and BakaLoader opens one
        /// window per auto-start profile. A second window whose own profile was stopped answered
        /// "nothing is running" while the first had a live world, and the self update went ahead
        /// on that answer: the file swap starts the moment staging succeeds, the process never
        /// exits because the other window keeps it alive, and the install ends up half old and
        /// half new with no relaunch. The registry is the whole-app answer.
        /// </para>
        /// </summary>
        private bool AnyServerRunning()
        {
            try { return UpdateGate.AnyServerBusy(AllSessions()); }
            catch (Exception ex)
            {
                // Unreadable means treat it as live: the only thing this answer gates is whether
                // the app may close itself, and closing on a maybe is how a world gets lost.
                Logger.Warning(ex, "Could not work out whether a server is still running");
                return true;
            }
        }

        /// <summary>
        /// Why an asked-for self-update is being turned down, or null when it may go ahead.
        /// <para>
        /// Updating BakaLoader means closing BakaLoader, and the server is the app's own child
        /// process, so a running server would go down with it. That is the host's call to make
        /// from the server controls, never a side effect of a button about the app, which is why
        /// this refuses rather than offering to stop anything. A stopped profile whose install is
        /// mid rewrite counts as busy for the same reason: steamcmd is writing files that belong
        /// to a server this app is holding open.
        /// </para>
        /// </summary>
        public static string SelfUpdateRefusal(bool checkEnabled, bool anyServerRunning, bool anyServerUpdateRunning)
            => SelfUpdateRefusal(checkEnabled, anyServerRunning, anyServerUpdateRunning, onCooldown: false);

        /// <summary>
        /// The same refusal, plus the one answer that is about the app rather than the servers.
        /// <para>
        /// The cooldown comes last, because it is the only one that clears itself: a host told "a
        /// server is running" needs that sentence even if they also clicked twice in a row.
        /// </para>
        /// </summary>
        public static string SelfUpdateRefusal(
            bool checkEnabled, bool anyServerRunning, bool anyServerUpdateRunning, bool onCooldown)
        {
            if (!checkEnabled) return "checkingOff";
            if (anyServerRunning || anyServerUpdateRunning) return "serverBusy";
            if (onCooldown) return "cooldown";
            return null;
        }

        /// <summary>
        /// What the page is told when the check the host asked for staged nothing. The service
        /// knows the difference between a machine with no internet and a machine that is already
        /// current, and this is where that difference becomes a sentence: an offline app used to
        /// answer "nothing newer came back", which it had no way of knowing.
        /// </summary>
        public static string SelfUpdateReason(StageOutcome outcome)
        {
            switch (outcome)
            {
                case StageOutcome.Staged: return null;
                case StageOutcome.NetworkError: return "offline";
                case StageOutcome.SwitchedOff: return "checkingOff";

                // AlreadyCurrent and NoRelease both mean the same thing to the host: GitHub was
                // reached and there is nothing there to install.
                default: return "notAvailable";
            }
        }

        /// <summary>
        /// How long the asked-for update waits before it will go out to GitHub again. The button
        /// bypasses the six-hour floor the quiet checks run on, which is right for a host who
        /// just clicked it and wrong for a host holding the mouse down: this is the floor under
        /// the bypass.
        /// </summary>
        public static readonly TimeSpan SelfUpdateCooldown = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Whether an asked-for update is still inside the cooldown. Static and given both times,
        /// so the rule can be proved without waiting a minute for it.
        /// </summary>
        public static bool SelfUpdateOnCooldown(DateTime lastAttemptUtc, DateTime nowUtc)
            => nowUtc - lastAttemptUtc < SelfUpdateCooldown;

        /// <summary>
        /// When the last asked-for self update actually went out to GitHub. Static because the
        /// throttle is the process's, not the window's: every window's button reaches the same
        /// GitHub. Only stamped once a request is really about to be made, so a refusal (a
        /// running server, checking switched off) never spends the host's minute for them.
        /// </summary>
        private static DateTime LastSelfUpdateAttemptUtc = DateTime.MinValue;

        /// <summary>
        /// The release page the "View release notes" button opens. GitHub's own address for the
        /// exact release the check found, so the notes match the version the host was offered,
        /// and only when it really is a release page on this repository: the page never sends a
        /// URL, and neither does anything else get to aim the host's browser through here.
        /// </summary>
        public static string AppReleaseNotesUrl(string notesUrl)
            => SpecificAppReleaseUrl(notesUrl) ?? ReleasesUrl;

        /// <summary>
        /// The same address without the fallback: the page for the exact release the check found,
        /// or null when the check handed over nothing, or handed over something that is not a
        /// release page on this repository. The page reads this one to know whether it has a
        /// specific release to offer or only the releases list.
        /// </summary>
        public static string SpecificAppReleaseUrl(string notesUrl)
            => !string.IsNullOrWhiteSpace(notesUrl)
                && notesUrl.StartsWith(ReleaseNotesPrefix, StringComparison.OrdinalIgnoreCase)
                ? notesUrl
                : null;

        #endregion

        #region Server update (Steam or steamcmd, driven from the app)

        /// <summary>
        /// What BakaLoader says when it cannot tell how an install was made. Kept here so the
        /// refusal reads the same whether the service or the bridge produced it.
        /// </summary>
        private const string UnknownInstallMessage =
            "BakaLoader cannot tell how this server was installed, so it cannot update it for you. "
            + "Open Steam and update Valheim Dedicated Server there.";

        /// <summary>The wire word for an install kind. Matches the tokens the WebUI switches on.</summary>
        private static string InstallKindToken(ServerInstallKind kind) => kind switch
        {
            ServerInstallKind.SteamLibrary => "steamLibrary",
            ServerInstallKind.Standalone => "standalone",
            _ => "unknown",
        };

        /// <summary>Classify, never throwing: an unreadable install is an Unknown one.</summary>
        private ServerInstallInfo ClassifyInstall(string serverExePath)
        {
            if (string.IsNullOrWhiteSpace(serverExePath)) return null;
            try { return ServerUpdates.Classify(serverExePath); }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not work out how the server at {path} was installed", serverExePath);
                return null;
            }
        }

        private bool IsServerUpdateRunning(string serverExePath)
        {
            if (string.IsNullOrWhiteSpace(serverExePath)) return false;
            try { return ServerUpdates.IsRunning(serverExePath); }
            catch { return false; }
        }

        /// <summary>
        /// What an RPC answers when it will not do the thing it was asked to do. The page shows
        /// the sentence exactly as it arrives, so the wording lives on this side.
        /// </summary>
        private static object RefusedRpc(string error, string reason = null) => new { ok = false, error, reason };

        /// <summary>
        /// The executable an update for this profile is rewriting. Taken from the operation this
        /// window started when there is one, so the Start button costs a dictionary read; falling
        /// back to the profile's own preferences for anything that never went through here.
        /// </summary>
        private string ResolveUpdateExePath(string profile)
        {
            if (string.IsNullOrWhiteSpace(profile)) return null;
            if (ServerUpdateExe.TryGetValue(profile, out var known) && !string.IsNullOrWhiteSpace(known))
                return known;

            try
            {
                var prefs = ServerPrefsProvider.LoadPreferences(profile);
                return prefs == null ? null : BuildServerOptions(prefs)?.ServerExePath;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not work out which server executable profile {profile} uses", profile);
                return null;
            }
        }

        /// <summary>
        /// True while any install is being rewritten right now. The updates this window started
        /// answer from a dictionary; every other profile is asked properly, because the update
        /// service is one singleton shared by every open window and a close here would take a
        /// steamcmd run started over there down with it.
        /// </summary>
        private bool AnyServerUpdateRunning()
        {
            if (ServerUpdateExe.Any(pair => IsServerUpdateRunning(pair.Value))) return true;

            try
            {
                foreach (var pr in ServerPrefsProvider.LoadPreferences())
                {
                    if (IsServerUpdateRunning(BuildServerOptions(pr)?.ServerExePath)) return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not check whether an update is running for every profile");
            }

            return false;
        }

        /// <summary>
        /// True while an update is in the one phase that must never be cut short. Everything else
        /// can be abandoned; a half written install cannot be undone.
        /// <para>
        /// An update this window did not start has no phase recorded here, and an unknown phase
        /// counts as the dangerous one. Guessing wrong the other way means force closing on top
        /// of somebody else's steamcmd run, and the cost of guessing this way is only that the
        /// host waits.
        /// </para>
        /// </summary>
        private bool AnyServerUpdateInSteamCmd()
        {
            foreach (var pair in ServerUpdateExe)
            {
                if (!IsServerUpdateRunning(pair.Value)) continue;
                if (!ServerUpdatePhaseSeen.TryGetValue(pair.Key, out var phase)) return true;
                if (phase == ServerUpdatePhase.RunningSteamCmd) return true;
            }

            try
            {
                foreach (var pr in ServerPrefsProvider.LoadPreferences())
                {
                    if (pr?.ProfileName == null || ServerUpdateExe.ContainsKey(pr.ProfileName)) continue;
                    if (IsServerUpdateRunning(BuildServerOptions(pr)?.ServerExePath)) return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not check how far along another profile's update is");
                return true;
            }

            return false;
        }

        /// <summary>What the window does about a close click while an update is running.</summary>
        public enum UpdateCloseChoice
        {
            /// <summary>Nothing is being rewritten: close as normal.</summary>
            Close,

            /// <summary>Refuse, say so, and close by itself when the update finishes.</summary>
            Wait,

            /// <summary>The host asked twice and no steamcmd run is mid write: let it go.</summary>
            Force,
        }

        /// <summary>How long a second close click still counts as "yes, I meant it".</summary>
        public static readonly TimeSpan UpdateCloseInsistWindow = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The close decision on its own, so it can be driven without a window. First click while
        /// an update runs always waits: the update finishes in seconds to minutes and the window
        /// closes itself when it does. A second click inside ten seconds is the host insisting,
        /// and is honoured unless steamcmd is mid write, where killing the app mid file leaves an
        /// install that is neither the old build nor the new one.
        /// </summary>
        public static UpdateCloseChoice DecideUpdateClose(
            bool updateRunning, bool steamCmdRunning, DateTime? firstAskedUtc, DateTime nowUtc)
        {
            if (!updateRunning) return UpdateCloseChoice.Close;
            if (firstAskedUtc == null) return UpdateCloseChoice.Wait;
            if (nowUtc - firstAskedUtc.Value > UpdateCloseInsistWindow) return UpdateCloseChoice.Wait;

            return steamCmdRunning ? UpdateCloseChoice.Wait : UpdateCloseChoice.Force;
        }

        /// <summary>What the host is told when a close is refused because an update is running.</summary>
        public const string UpdateCloseMessage =
            "An update is running. BakaLoader will close when it finishes.";

        // Whether a cancel will be honoured, and whether an update ended in one, used to be
        // worked out here from the last phase this window happened to see. Both answers belong
        // to the update service and both now come straight from it: ServerUpdates.Cancel says
        // whether it took, and ServerUpdateResult.Cancelled says how the operation ended. Two
        // copies of one rule cannot be kept in step, and the copy here read a phase that could
        // already be out of date. The gate in BlendWindowUpdateGuardTests keeps them gone.

        /// <summary>
        /// Everything the update UI needs before it offers a button: the install kind, what
        /// Steam has queued, and one plain sentence when the answer is no.
        /// </summary>
        private object BuildServerUpdateCheck(string profile, IValheimServerOptions options, bool serverStopped)
        {
            var exe = options?.ServerExePath;
            var install = ClassifyInstall(exe);
            var kind = install?.Kind ?? ServerInstallKind.Unknown;

            // Every field this answer carries comes out of the Steam manifest, and the probe
            // hashes the binaries whenever there is no manifest to read. A dashboard render
            // calls this, so an install with no manifest is answered from what ClassifyInstall
            // already found rather than by hashing tens of megabytes for fields that would all
            // come back empty anyway.
            Tools.ServerBuildInfo current = null;
            if (install?.ManifestPath != null)
            {
                try { current = ServerBuildTracker.Probe(exe); }
                catch (Exception ex) { Logger.Warning(ex, "Could not read the server build for profile {profile}", profile); }
            }

            var running = IsServerUpdateRunning(exe);

            string reason = null;
            var canUpdate = true;
            if (kind == ServerInstallKind.Unknown)
            {
                canUpdate = false;
                reason = string.IsNullOrWhiteSpace(install?.Reason) ? UnknownInstallMessage : install.Reason;
            }
            else if (running)
            {
                canUpdate = false;
                reason = "An update is already running for this install.";
            }
            else if (!serverStopped)
            {
                canUpdate = false;
                reason = "Stop the server to update it.";
            }

            return new
            {
                profile,
                installKind = InstallKindToken(kind),
                updatePending = current?.UpdatePending ?? false,
                pendingBytes = current?.PendingBytes ?? 0,
                buildId = current?.BuildId,
                targetBuildId = current?.TargetBuildId,
                canUpdate,
                running,
                reason,
            };
        }

        /// <summary>
        /// The worlds-aside step the update runs before it lets anything write to the install.
        /// Wraps the same snapshot the launch guard uses, and answers the one question the
        /// update service asks: are the worlds safe now?
        /// <para>
        /// A pass that copied nothing while worlds exist is NOT safe, however cleanly it
        /// finished. That is the whole point of the step, so it returns false and the update
        /// never starts (LG-10). A genuinely empty save folder has nothing to protect and
        /// passes.
        /// </para>
        /// </summary>
        private Task<bool> RunUpdateBackupAsync(string profile, IValheimServerOptions options)
        {
            return Task.Run(() =>
            {
                var hadWorlds = false;
                try { hadWorlds = WorldStore.Enumerate(options.SaveDataFolderPath).Count > 0; }
                catch { /* an unreadable save folder is reported by the snapshot itself */ }

                WorldStore.WorldSnapshotResult result;
                try
                {
                    result = WorldStore.SnapshotAllPreUpdate(
                        options.SaveDataFolderPath, whenLocal: null, refuse: WorldInUseByRunningProfile);
                }
                catch (Exception e)
                {
                    result = new WorldStore.WorldSnapshotResult { Error = e.Message };
                }

                var ok = SnapshotProtectedTheWorlds(result, hadWorlds);
                var protectedNothing = result.Ok && !ok;

                var error = result.Error;
                if (protectedNothing)
                {
                    error = "This save folder holds worlds, but none of them could be copied aside.";
                    Logger.Error(
                        "The pre-update snapshot for profile {profile} copied no worlds while worlds exist; the update was not started.",
                        profile);
                }

                // Same event the launch path raises, so the WebUI's existing handler shows the
                // real count or the real failure without knowing which flow asked for it.
                PostEvent("server.worldsBackedUp", new
                {
                    profile,
                    ok,
                    error,
                    count = result.Copied.Count,
                    bytes = result.Bytes,
                    skipped = result.Skipped,
                });

                if (ok)
                {
                    Analytics.Record(new AnalyticsEvent { Kind = "presnap", Server = profile });
                    ServerUpdateBackedUp[profile] = true;
                }

                return ok;
            });
        }

        /// <summary>
        /// The end of an update, whichever way it went: tell the UI, tell Discord when event
        /// posts are on, and start the server when that is what was asked for.
        /// </summary>
        private void OnServerUpdateCompleted(ServerUpdateResult result)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.ProfileName)) return;

            var profile = result.ProfileName;

            // The update service is one singleton every open window listens to, so this fires
            // in all of them. Only the window that started this update follows through: two
            // Discord posts and two start attempts for one update is not a small mistake.
            if (!ServerUpdateCts.TryRemove(profile, out var cts)) return;
            try { cts.Dispose(); } catch { }

            ServerUpdateExe.TryRemove(profile, out _);

            // The service says so itself now. This used to be read off the last phase that
            // reached this window, which is a copy of the truth rather than the truth: a
            // progress event that never arrived, or one that arrived after the completion,
            // would have turned a cancel into a red failure row.
            var cancelled = result.Cancelled;
            ServerUpdatePhaseSeen.TryRemove(profile, out _);

            var name = ServerPrefsProvider.LoadPreferences(profile)?.Name;
            var serverName = string.IsNullOrWhiteSpace(name) ? profile : name;

            if (result.Ok)
            {
                Logger.Information("Profile {profile} finished updating (build {build})",
                    profile, string.IsNullOrWhiteSpace(result.BuildId) ? "unknown" : result.BuildId);

                PostEvent("server.updateDone", new
                {
                    profile,
                    buildId = result.BuildId,
                    startAfter = result.StartAfter,
                });
            }
            else
            {
                Logger.Warning("Profile {profile} was not updated: {reason}", profile, result.Reason);

                PostEvent("server.updateFailed", new
                {
                    profile,
                    reason = result.Reason,
                    // A cancel is not a failure the host needs a red row about: they asked for it,
                    // and Steam carries on with the download by itself. The page reads this and
                    // puts the launch hold back exactly as it was instead.
                    cancelled,
                });
            }

            // Mirrors the launch-hold post: same gate, same best-effort try/catch.
            try
            {
                if (UserPrefsProvider.LoadPreferences().DiscordEventPosts)
                {
                    if (result.Ok)
                        DiscordWebhooks.SendServerUpdated(serverName, result.BuildId, result.StartAfter);
                    else
                        DiscordWebhooks.SendServerUpdateFailed(serverName, result.Reason);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not post the server update to Discord");
            }

            if (result.Ok && result.StartAfter)
            {
                try { BeginInvoke(new Action(() => StartAfterUpdate(profile))); }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Could not start profile {profile} after its update", profile);
                }
            }

            // The host clicked the window X while this was running and was told the app would
            // close when it finished. It has finished.
            if (CloseWhenUpdatesFinish && !AnyServerUpdateRunning())
            {
                try { BeginInvoke(new Action(CloseAfterUpdate)); }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Could not close the window after the update finished");
                }
            }
        }

        // Set when a close was refused because an update was running: the window owes the host a
        // close as soon as the last one finishes.
        private bool CloseWhenUpdatesFinish;

        // When the close was first refused, so a second click inside ten seconds can be read as
        // the host insisting rather than as a fresh first click.
        private DateTime? CloseFirstAskedUtc;

        /// <summary>The close the window promised when it refused one during an update.</summary>
        private void CloseAfterUpdate()
        {
            if (!CloseWhenUpdatesFinish) return;
            CloseWhenUpdatesFinish = false;

            try { Close(); }
            catch (Exception ex) { Logger.Warning(ex, "Could not close the window after the update finished"); }
        }

        /// <summary>
        /// Starts a profile the way the Start button does, once its update finished. It goes
        /// through the same guarded start as everything else - the build genuinely changed, so
        /// the guard has a real question to ask - with the answer already staged.
        /// <para>
        /// The staged answer is "proceed" only when this operation already copied the worlds
        /// aside. When the host asked to update WITHOUT a backup, the answer staged is "backup",
        /// so the guard still copies them before the new build converts them: skipping the
        /// snapshot before a download is a different decision from skipping it before the one
        /// launch that rewrites every world on disk.
        /// </para>
        /// </summary>
        private void StartAfterUpdate(string profile)
        {
            // This runs on the UI thread from a background completion, so nothing may escape:
            // a collision check that throws here would take the window with it rather than
            // land in an RPC reply the way a Start button press would.
            try
            {
                var prefs = ServerPrefsProvider.LoadPreferences(profile);
                if (prefs == null)
                {
                    Logger.Warning("Profile {profile} vanished while it was updating, so nothing was started", profile);
                    return;
                }

                var session = GetOrCreateSession(profile);
                var options = BuildServerOptions(MergeLaunchHistory(prefs));

                StageLaunchAnswer(profile, ServerUpdateBackedUp.TryRemove(profile, out var copied) && copied
                    ? "proceed"
                    : "backup");

                if (!session.Server.CanStart)
                {
                    DropLaunchAnswer(profile);
                    Logger.Warning("Profile {profile} could not be started after its update", profile);
                    PostEvent("server.launchFailed", new
                    {
                        profile,
                        reason = LaunchReasons.Manual,
                        message = "The update finished, but the server could not be started. Start it yourself when you are ready.",
                    });
                    return;
                }

                EnsureNoServerCollisions(session, options);
                session.Server.Start(options);
            }
            catch (Exception ex)
            {
                DropLaunchAnswer(profile);
                Logger.Error(ex, "Profile {profile} could not be started after its update", profile);
                PostEvent("server.launchFailed", new
                {
                    profile,
                    reason = LaunchReasons.Manual,
                    message = ex.Message,
                });
            }
        }

        // Whether the update that just finished copied the worlds aside, per profile. Read once
        // by StartAfterUpdate to decide what to stage for the guard.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ServerUpdateBackedUp =
            new(StringComparer.OrdinalIgnoreCase);

        #endregion

        #region RPC registration

        private void RegisterRpcHandlers()
        {
            // --- App ---
            RegisterRpc("app.info", p =>
            {
                // First call the WebUI makes on every load, so it is where anything the app
                // learned before this window existed gets said again.
                ReplayAppUpdateAvailable();

                return Task.FromResult<object>(new
                {
                    version = AssemblyHelper.GetApplicationVersion(),
                    // StartProfile fallback covers the (unlikely) case where the JS boots
                    // before OnBlendStartup has copied the splash-assigned profile over.
                    profile = CurrentProfile ?? StartProfile,
                });
            });

            // What the page asks whenever it is about to say something about a newer BakaLoader.
            // Read only, cheap, and always the same answer for every surface on the page.
            RegisterRpc("app.updateStatus", p => Task.FromResult<object>(BuildAppUpdateStatus()));

            // The host asked for the update by name. This stages it and closes the app so the
            // watchdog can swap the files; it never stops a server to get there. A server that is
            // up, or an install that steamcmd is still writing, is a refusal with a reason the
            // page can put into a sentence.
            RegisterRpc("app.selfUpdateNow", async p =>
            {
                var prefs = LoadUserPrefsOrNull();
                var refusal = SelfUpdateRefusal(
                    prefs?.CheckForUpdates == true, AnyServerRunning(), AnyServerUpdateRunning(),
                    onCooldown: SelfUpdateOnCooldown(LastSelfUpdateAttemptUtc, DateTime.UtcNow));

                if (refusal != null)
                {
                    Logger.Information("The self-update the host asked for was turned down: {reason}", refusal);
                    return new { ok = false, reason = refusal };
                }

                // Stamped here and nowhere else: past every refusal, so a host who was told to
                // wait for their server is not also made to wait out a cooldown they never spent,
                // and before the request, so a slow one cannot be asked for twice.
                LastSelfUpdateAttemptUtc = DateTime.UtcNow;

                StageOutcome outcome;
                try
                {
                    outcome = await AppUpdateService.TryStageUpdateAsync(userInitiated: true);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "The self-update the host asked for could not reach GitHub");
                    outcome = StageOutcome.NetworkError;
                }

                if (outcome != StageOutcome.Staged)
                {
                    var why = SelfUpdateReason(outcome);
                    Logger.Information("The self-update the host asked for staged nothing: {reason}", why);
                    return new { ok = false, reason = why };
                }

                // Staged, and the watchdog is already counting down to writing over the install,
                // so the whole application closes rather than this one window: each window goes
                // through its own graceful path, which saves and stops whatever it is holding,
                // and the process ends when the last of them has gone.
                CloseEveryWindowForSelfUpdate();
                return new { ok = true };
            });

            // --- Profiles (ServerPreferences) ---
            RegisterRpc("profiles.list", p => Task.FromResult<object>(
                ServerPrefsProvider.LoadPreferences()
                    .Select(pref => new { pref.ProfileName, pref.LastSaved })
                    .ToList()));

            RegisterRpc("profiles.get", p =>
            {
                var name = p.Value<string>("name");
                var prefs = ServerPrefsProvider.LoadPreferences(name)
                    ?? throw new HostFacingException("profiles.get.noSuchProfile",
                        $"No profile named '{name}'", ("name", name));
                CurrentProfile = prefs.ProfileName;
                // Loading a profile IS the server switch - refresh the chips' active marker.
                PostEvent("servers.changed", BuildServersList());
                return Task.FromResult<object>(BuildProfilePrefsDto(prefs, UserPrefsProvider.LoadPreferences()));
            });

            RegisterRpc("profiles.save", p =>
            {
                var prefs = (p["prefs"] ?? throw new HostFacingException("profiles.save.prefsRequired", "prefs is required"))
                    .ToObject<ServerPreferences>();
                // The form has no fields for the launch history, so carry it over rather than
                // letting a plain save wipe it and make the next start look like the first.
                prefs = MergeLaunchHistory(prefs);
                ServerPrefsProvider.SavePreferences(prefs);
                CurrentProfile = prefs.ProfileName;
                PostEvent("servers.changed", BuildServersList());
                // The same shape the page was handed on the way in, so the Directories lines
                // are answered from the save rather than left describing the state before it.
                return Task.FromResult<object>(BuildProfilePrefsDto(prefs, UserPrefsProvider.LoadPreferences()));
            });

            RegisterRpc("profiles.remove", p =>
            {
                var name = p.Value<string>("name");
                if (Sessions.TryGetValue(name, out var session) && session.Server.Status != ServerStatus.Stopped)
                    throw new HostFacingException("profiles.remove.serverRunning",
                        $"Stop the server '{name}' before removing its profile.", ("name", name));

                ServerPrefsProvider.RemovePreferences(name);
                if (CurrentProfile == name) CurrentProfile = null;

                // Drop the dead session so its server unsubscribes from the player repo.
                if (Sessions.TryRemove(name, out var removed)) { ForgetSession(removed); removed.Server.Dispose(); }

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
                    throw new HostFacingException("profiles.rename.nameRequired", "A new name is required.");
                if (string.Equals(name, newName, StringComparison.Ordinal))
                    return Task.FromResult<object>(true);

                var prefs = ServerPrefsProvider.LoadPreferences(name)
                    ?? throw new HostFacingException("profiles.rename.noSuchProfile",
                        $"No profile named '{name}'", ("name", name));
                if (Sessions.TryGetValue(name, out var s) && s.Server.Status != ServerStatus.Stopped)
                    throw new HostFacingException("profiles.rename.serverRunning",
                        $"Stop the server '{name}' before renaming it.", ("name", name));
                if (ServerPrefsProvider.LoadPreferences()
                        .Any(x => string.Equals(x.ProfileName, newName, StringComparison.OrdinalIgnoreCase)))
                    throw new HostFacingException("profiles.rename.nameTaken",
                        $"A server named '{newName}' already exists.", ("newName", newName));

                prefs.ProfileName = newName;
                ServerPrefsProvider.SavePreferences(prefs);
                ServerPrefsProvider.RemovePreferences(name);

                // Retire any (stopped) session under the old key so the registry stays consistent.
                if (Sessions.TryRemove(name, out var removed)) { ForgetSession(removed); removed.Server.Dispose(); }
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
                    ?? throw new HostFacingException("profiles.archive.noSuchProfile",
                        $"No profile named '{name}'", ("name", name));
                if (Sessions.TryGetValue(name, out var s) && s.Server.Status != ServerStatus.Stopped)
                    throw new HostFacingException("profiles.archive.serverRunning",
                        $"Stop the server '{name}' before archiving it.", ("name", name));

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
                    ?? throw new HostFacingException("profiles.unarchive.noSuchProfile",
                        $"No profile named '{name}'", ("name", name));
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
                    ?? throw new HostFacingException("profiles.deleteInfo.noSuchProfile",
                        $"No profile named '{name}'", ("name", name));

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
                    ?? throw new HostFacingException("profiles.delete.noSuchProfile",
                        $"No profile named '{name}'", ("name", name));
                if (Sessions.TryGetValue(name, out var s) && s.Server.Status != ServerStatus.Stopped)
                    throw new HostFacingException("profiles.delete.serverRunning",
                        $"Stop the server '{name}' before deleting it.", ("name", name));
                if (CountNonArchivedProfiles() <= 1 && !prefs.Archived)
                    throw new HostFacingException("profiles.delete.lastServer",
                        "This is your only active server. Archive it instead of deleting the last one.");

                if (deleteFiles)
                {
                    var installDir = GetManagedInstallDir(prefs);
                    var saveFolder = GetIsolatedSaveFolder(prefs);

                    // Two profiles may be pointed at one save folder, deliberately or by a
                    // hand-edited prefs file. Deleting the files then takes the other realm's
                    // worlds with them, and nothing would say so. Refuse instead, and name the
                    // profile that is still using it so the host can decide.
                    if (saveFolder != null)
                    {
                        var sharedWith = ProfileSharingSaveFolder(
                            ServerPrefsProvider.LoadPreferences(), name, saveFolder);

                        if (sharedWith != null)
                            throw new HostFacingException("profiles.delete.saveFolderShared",
                                $"'{sharedWith.ProfileName}' keeps its worlds in the same folder, so the files were not deleted. "
                                + "Point that server somewhere else first, or delete this one without its files.",
                                ("sharedWith", sharedWith.ProfileName));
                    }

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
                if (Sessions.TryRemove(name, out var removed)) { ForgetSession(removed); removed.Server.Dispose(); }
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
                    throw new HostFacingException("servers.create.nameRequired", "A server name is required.");

                if (ServerPrefsProvider.LoadPreferences()
                        .Any(x => string.Equals(x.ProfileName, name, StringComparison.OrdinalIgnoreCase)))
                    throw new HostFacingException("servers.create.nameTaken",
                        $"A server named '{name}' already exists. Pick a different name.", ("name", name));

                // Optional world-generation dials for the NEW world, validated up front (shared
                // with worldgen.save) so an invalid value is refused before any install is
                // provisioned. Empty / Normal dials drop out, leaving an empty map.
                var worldModifiers = ParseWorldModifiers(p["modifiers"] as JObject);

                // Optional world switches for the NEW world, on the same gate as Save Config.
                // Absent leaves whatever the world turns out to carry alone.
                var worldSwitches = ParseWorldKeys(p["keys"]);

                var isolateInstall = p.Value<bool?>("isolateInstall") ?? true;
                var seedMods = p.Value<bool?>("seedMods") ?? true;
                var isolateSaveFolder = p.Value<bool?>("isolateSaveFolder") ?? true;

                // Distinct world: explicit value, else a safe token derived from the name.
                var world = (p.Value<string>("world") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(world)) world = InstallIsolationService.MakeSafeName(name);

                // OPTIONAL: the new realm starts with a copy of another realm's world rather
                // than with an empty one. This is what Duplicate always looked like it did and
                // never did: it switched profile and opened the forge, so the second realm came
                // up on a brand new empty world and the host found out by walking into it.
                //
                // The pair names a PROFILE and a world, never a folder: the folder is read off
                // that profile's own preferences here, so nothing the page sends can point a
                // copy at a directory BakaLoader does not own.
                var copyFrom = p["copyWorldFrom"] as JObject;
                var copySourceProfile = (copyFrom?.Value<string>("profile") ?? "").Trim();
                var copySourceWorldName = (copyFrom?.Value<string>("world") ?? "").Trim();
                var wantsCopy = copyFrom != null && copySourceWorldName.Length > 0;

                WorldInfo copySource = null;
                if (wantsCopy)
                {
                    if (!WorldStore.IsSafeReferenceToken(copySourceWorldName))
                        throw new HostFacingException("servers.create.copyBadSourceRef",
                            "That is not a world name BakaLoader can copy from.",
                            ("world", copySourceWorldName));

                    var sourcePrefs = string.IsNullOrWhiteSpace(copySourceProfile)
                        ? null
                        : ServerPrefsProvider.LoadPreferences(copySourceProfile);
                    var sourceFolder = ResolveSaveDataFolder(sourcePrefs?.SaveDataFolderPath);
                    if (string.IsNullOrWhiteSpace(sourceFolder))
                        throw new HostFacingException("servers.create.copyNoSourceFolder",
                            $"'{copySourceProfile}' has no save folder BakaLoader can read, so its world could not be copied.",
                            ("profile", copySourceProfile));

                    // Asked BEFORE anything is created. A missing source that was noticed
                    // afterwards would leave a realm standing over a world it does not have.
                    copySource = WorldStore.Find(sourceFolder, copySourceWorldName);
                    if (copySource == null)
                        throw new HostFacingException("servers.create.copySourceMissing",
                            $"There is no world named '{copySourceWorldName}' in the save folder of '{copySourceProfile}', so nothing was copied.",
                            ("world", copySourceWorldName), ("profile", copySourceProfile));

                    // The Barrow's own rule, and the same method it uses.
                    RefuseWhileTheWorldIsBeingWritten(copySourceWorldName, sourceFolder);
                }

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
                if (worldSeed.Length > 0 && wantsCopy)
                {
                    // A copied world already has a seed: the one it was made with, which is
                    // what the world IS. Writing another over it is not a thing that can
                    // happen, so it is refused here rather than half-done further down.
                    throw new HostFacingException("servers.create.copySeedConflict",
                        "A copied world keeps the seed it was made with, so a seed cannot be set for it.");
                }

                if (worldSeed.Length > 0)
                {
                    var targetSaveFolder = isolateSaveFolder
                        ? created.SaveDataFolderPath
                        : ResolveSaveDataFolder(created.SaveDataFolderPath);
                    if (string.IsNullOrWhiteSpace(targetSaveFolder))
                        throw new HostFacingException("servers.create.noSaveFolder",
                            "No save folder is configured, so the world seed can't be applied.");

                    var written = FwlWriter.WriteNewWorld(targetSaveFolder, world, worldSeed);
                    Logger.Information("Pre-created world '{0}' with seed '{1}' ({2}).",
                        world, written.SeedName, written.Seed);
                }

                // The copy, and it lands BEFORE the first meeting below. That order is the
                // whole point: the header the copy carries is what ImportWorldKeysOnFirstMeeting
                // reads, so the copied world's own dials and switches become the new realm's
                // rather than the realm coming up Normal over somebody else's difficulty.
                if (wantsCopy)
                {
                    var targetSaveFolder = isolateSaveFolder
                        ? created.SaveDataFolderPath
                        : ResolveSaveDataFolder(created.SaveDataFolderPath);
                    if (string.IsNullOrWhiteSpace(targetSaveFolder))
                        throw new HostFacingException("servers.create.copyNoTargetFolder",
                            "No save folder is configured, so the world could not be copied.");

                    // A 1.0 world is a whole directory tree, so the copy goes off the UI thread.
                    // A name already taken at the destination is refused from inside this, with
                    // the same sentence the Barrow's copy gives, and nothing is left behind.
                    var landed = await Task.Run(() => WorldStore.CopyWorldAs(copySource, world, targetSaveFolder));
                    Logger.Information("Copied world '{source}' from '{profile}' into {folder} as '{target}'.",
                        copySourceWorldName, copySourceProfile, landed, world);
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

                // A realm can be forged over a world that is already on disk, and that world may
                // carry its own settings. It gets the same first meeting every start path gets,
                // BEFORE anything chosen in the wizard is written on top, so adopting a world
                // never costs it what it had. A brand new world has no header yet and this does
                // nothing at all.
                ImportWorldKeysOnFirstMeeting(world, created.SaveDataFolderPath);

                // Persist the chosen difficulty to the NEW world so it is born this way on its
                // very first launch, rather than starting Normal until the host opens Save Config.
                // Keyed by world name exactly like worldgen.save, under the same gated store.
                if (worldModifiers.Count > 0 || worldSwitches != null)
                {
                    var worldPrefs = WorldPrefsProvider.LoadPreferences(world)
                        ?? new WorldPreferences { WorldName = world };
                    worldPrefs.Preset = null; // individual dials replace any preset (mutually exclusive)
                    // The forge's five dials all start at Normal and only the moved ones are sent,
                    // so silence about a dial here is not a statement about it. A one-dial wizard
                    // is no more a statement about the other four than an all-Normal one is about
                    // all five: the moved ones go over what the import just read off the world,
                    // and the rest stay as the world had them. Save Config is the other way round
                    // on purpose: that page sends the whole dial state every time, so there a
                    // blank set IS a clear.
                    if (worldModifiers.Count > 0)
                    {
                        if (worldPrefs.Modifiers == null) worldPrefs.Modifiers = new Dictionary<string, string>();
                        foreach (var pair in worldModifiers) worldPrefs.Modifiers[pair.Key] = pair.Value;
                    }
                    worldPrefs.Keys = MergeWorldKeys(worldPrefs.Keys, worldSwitches);
                    WorldPrefsProvider.SavePreferences(worldPrefs);
                    Logger.Information("New world '{0}' created with modifiers: {1}; switches: {2}", world,
                        worldModifiers.Count == 0 ? "none" : string.Join(", ", worldModifiers.Select(kv => kv.Key + "=" + kv.Value)),
                        worldSwitches == null || worldSwitches.Count == 0 ? "none" : string.Join(", ", worldSwitches.OrderBy(k => k, StringComparer.Ordinal)));
                }

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

                var found = new List<(string World, string Folder, string Sub, string Format, long SizeBytes, DateTime ModifiedUtc)>();
                foreach (var saveFolder in KnownSaveFolders())
                {
                    foreach (var sub in WorldStore.WorldSubfolders)
                    {
                        // Backups are not adoptable realms, and WorldStore never returns one, so
                        // a live server's snapshot trail and the originals left behind by a 1.0
                        // conversion both stay out of the restore panel on their own.
                        foreach (var w in WorldStore.EnumerateIn(saveFolder, sub))
                        {
                            if (owned.Contains(w.Name)) continue;
                            found.Add((w.Name, saveFolder, sub,
                                w.Format == WorldFormat.Chunked ? "chunked" : "legacy",
                                w.SizeBytes, w.LastWriteUtc));
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
                            format = newest.Format,
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
                    throw new HostFacingException("servers.adoptWorld.worldRequired", "A world is required.");

                var sourceFolder = p.Value<string>("folder");
                var sub = p.Value<string>("sub");
                if (string.IsNullOrWhiteSpace(sub)) sub = WorldStore.WorldSubfolders[0];

                // Same allowlist the Barrow's own references go through (ValidateBackupRef):
                // the world name has to be a plain name, the subfolder one of the two the game
                // uses, and the folder one BakaLoader already knows about. Without this the
                // caller picks the path, and "sub" alone is enough to climb out of the save
                // folder and read a directory that was never anybody's world.
                ValidateWorldSourceRef(world, sourceFolder, sub, KnownSaveFolders());

                // Fail loudly BEFORE creating anything: a missing source must never produce a
                // profile that claims the world while zero files were actually copied. Either
                // save format counts as present.
                var sourceWorld = string.IsNullOrWhiteSpace(sourceFolder)
                    ? null
                    : WorldStore.EnumerateIn(sourceFolder, sub)
                        .FirstOrDefault(w => string.Equals(w.Name, world, StringComparison.OrdinalIgnoreCase));
                if (sourceWorld == null)
                    throw new HostFacingException("servers.adoptWorld.worldNotFound",
                        $"World '{world}' was not found under '{sourceFolder ?? "<null>"}\\{sub}', so nothing was adopted.",
                        ("world", world), ("folder", sourceFolder), ("sub", sub));

                var name = (p.Value<string>("name") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) name = world;

                if (ServerPrefsProvider.LoadPreferences()
                        .Any(x => string.Equals(x.ProfileName, name, StringComparison.OrdinalIgnoreCase)))
                    throw new HostFacingException("servers.adoptWorld.nameTaken",
                        $"A server named '{name}' already exists. Pick a different name.", ("name", name));

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
                            message = $"Shares its install (valheim_server.exe) with '{other.ProfileName}', so their mods and configs would collide. Give this realm its own install." });

                    if (!string.IsNullOrWhiteSpace(world)
                        && string.Equals(world, other.WorldName, StringComparison.OrdinalIgnoreCase)
                        && SamePath(save ?? "", EffectiveSave(other) ?? ""))
                        warnings.Add(new { kind = "world", other = other.ProfileName,
                            message = $"World '{world}' sits in the same save folder as '{other.ProfileName}', and both can't load it at once." });
                }
                return Task.FromResult<object>(warnings);
            });

            // --- User preferences ---
            RegisterRpc("userprefs.get", p => Task.FromResult<object>(BuildUserPrefsDto()));

            // What this machine can and cannot reach, one step at a time. A host whose curl
            // works and whose BakaLoader does not has nothing to look at but "did not answer",
            // and the difference between those two is invisible from inside an exception. Runs
            // off the UI thread, gives up on each step in turn, and never throws to the page:
            // the page this is pressed from is the one a stuck host is already on.
            RegisterRpc("net.diagnose", async p =>
            {
                var report = await Task.Run(() => ConnectionDiagnostics.RunAsync(null, Logger));

                // The whole result as ONE block, so a host can paste the log instead of the
                // page. Every stage with its outcome and its milliseconds, then the verdict
                // both as the short name the page keys on and as the sentence it shows: a
                // reader of the log should not have to own the page to know what "noproxy"
                // was asking them to do.
                // The verdict sentence is written with :l, which is Serilog's "literal": a
                // string scalar without it comes out wrapped in quotation marks, and a
                // sentence inside a bracket inside quotation marks is not what a host pastes
                // into an issue.
                Logger.Information("Connection test for {host}: {verdict} ({sentence:l}). {stages}",
                    report.Host, report.Verdict,
                    HostCatalog.EmbeddedEnglish.Say("hearth.upkeep.connection.verdict." + report.Verdict),
                    string.Join(" | ", report.Stages.Select(s =>
                        s.Stage + "=" + (s.Ok ? "ok" : "no") + " " + s.Detail + " (" + s.Ms + "ms)")));

                return (object)new
                {
                    host = report.Host,
                    verdict = report.Verdict,
                    stages = report.Stages
                        .Select(s => new { stage = s.Stage, ok = s.Ok, detail = s.Detail, ms = s.Ms })
                        .ToList(),
                };
            });

            RegisterRpc("userprefs.save", p =>
            {
                var dto = p["prefs"] as JObject ?? throw new ArgumentException("prefs is required");

                // userprefs.json is one document: this handler saves the WHOLE of it, servers and
                // worlds included. Loading it here and writing it back after a Discord publish or a
                // launch record had landed in between would put those back the way they were, so the
                // load, the edits and the write all happen under the one gate.
                UserPreferences prefs = null;
                // Whether this save carried the Detailed log switch. The dial itself is moved
                // after the write lands, so the line that says the level changed is the first
                // one in the log a host is about to go and read.
                var detailedLogMoved = false;
                UserPrefsProvider.Mutate(current =>
                {
                    prefs = current;

                    // Only apply keys the client sent, so partial updates never clobber other settings.
                    void Apply(string key, Action<JToken> setter)
                    {
                        if (dto.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var v)) setter(v);
                    }

                    Apply("ServerExePath", v => prefs.ServerExePath = v.Value<string>());
                    Apply("SaveDataFolderPath", v => prefs.SaveDataFolderPath = v.Value<string>());
                    Apply("CheckForUpdates", v => prefs.CheckForUpdates = v.Value<bool>());
                    Apply("AutoUpdateMods", v => prefs.AutoUpdateMods = v.Value<bool>());
                    Apply("UseHexiumSource", v => prefs.UseHexiumSource = v.Value<bool>());
                    Apply("AutoUpdateBakaLoader", v => prefs.AutoUpdateBakaLoader = v.Value<bool>());
                    // Moving the Upkeep switch by hand IS the answer to the first-start
                    // question, so it is recorded as one. Without this a host who found the
                    // setting and turned it on before ever pressing Start would still count as
                    // unanswered, and the standing question would go on being asked after they
                    // had already given it.
                    Apply("BepInExMaintained", v =>
                    {
                        prefs.BepInExMaintained = v.Value<bool>();
                        prefs.BepInExMaintenanceAsked = true;
                    });
                    Apply("BepInExMaintenanceAsked", v => prefs.BepInExMaintenanceAsked = v.Value<bool>());
                    // The two connection switches. They shape the handler every remote client
                    // in the app sends through, and HttpClientProvider reads them again on the
                    // next CreateClient, so a switch moved here takes effect on the next
                    // request rather than on the next launch.
                    Apply("BypassSystemProxy", v => prefs.BypassSystemProxy = v.Value<bool>());
                    Apply("ForceIPv4", v => prefs.ForceIPv4 = v.Value<bool>());
                    Apply("StartWithWindows", v => prefs.StartWithWindows = v.Value<bool>());
                    Apply("ShareAnonymousStats", v => prefs.ShareAnonymousStats = v.Value<bool>());
                    Apply("StartMinimized", v => prefs.StartMinimized = v.Value<bool>());
                    Apply("SaveProfileOnStart", v => prefs.SaveProfileOnStart = v.Value<bool>());
                    Apply("WriteApplicationLogsToFile", v => prefs.WriteApplicationLogsToFile = v.Value<bool>());
                    // Detailed log. The dial is moved AFTER the write lands, below, so the one
                    // line that says the level moved is written into the log the host is about
                    // to read rather than into the one they are leaving. The card posts every
                    // switch on it together, so the key arriving is not the switch moving:
                    // only a save that changed the value counts as a move.
                    Apply("DetailedLog", v =>
                    {
                        if (LogLevelControl.ApplySavedValue(prefs, v)) detailedLogMoved = true;
                    });
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

                    // Both of these are spellings from a closed list, so a spelling nothing
                    // answers to leaves the preference where it was rather than saving a
                    // language the app cannot read. This one is here so the Settings hall can
                    // write the player-message choice.
                    //
                    // The interface language carries lang.set's second guard as well, because
                    // the two roads have to refuse the same things. A Language saved with no
                    // pack on disk leaves lang.status answering that language with nowhere to
                    // read it from, which the page turns into an English window while the globe
                    // draws that row as the current one. No page road writes it today; it is
                    // guarded here so none can.
                    Apply("Language", v =>
                        prefs.Language = ReadableLanguage(LanguageCodes.Normalize(v.Value<string>())) ?? prefs.Language);
                    Apply("PlayerMessageLanguage", v =>
                    {
                        var asked = v.Value<string>()?.Trim();
                        if (string.Equals(asked, "same", StringComparison.OrdinalIgnoreCase))
                        {
                            prefs.PlayerMessageLanguage = "same";
                            return;
                        }

                        prefs.PlayerMessageLanguage = LanguageCodes.Normalize(asked) ?? prefs.PlayerMessageLanguage;
                    });
                });

                // Only when the document could not be read at all, which the provider answers with
                // defaults rather than null. Belt to that brace so the lines below cannot fault.
                prefs ??= UserPrefsProvider.LoadPreferences();

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
                // A save that touched either language preference changes which catalog the
                // countdown and the Discord post are written from.
                if (dto.TryGetValue("Language", StringComparison.OrdinalIgnoreCase, out _)
                    || dto.TryGetValue("PlayerMessageLanguage", StringComparison.OrdinalIgnoreCase, out _))
                {
                    RefreshHostCatalog();
                }

                if (dto.TryGetValue("StartWithWindows", StringComparison.OrdinalIgnoreCase, out _))
                {
                    try { StartupHelper.ApplyStartupSetting(prefs.StartWithWindows, Logger); }
                    catch (Exception e) { AppLogger.Error(e, "Failed to apply the 'start with Windows' setting."); }
                }

                // The log's own detail, moved here and not at the next launch: a host who has
                // just turned it on is about to reproduce the thing they are chasing.
                if (detailedLogMoved && LogLevel != null)
                {
                    try { AppLogger.Information("{Line:l}", LogLevel.Toggled(prefs.DetailedLog)); }
                    catch (Exception e) { AppLogger.Debug("Could not move the log level: {0}", e.Message); }
                }

                return Task.FromResult<object>(BuildUserPrefsDto());
            });

            // --- Language ---
            // The globe menu and the switch behind it. Every answer here is about packs on
            // disk and the preference that names one of them; the words themselves never come
            // through the bridge, because the page fetches the catalog off its own origin.

            // Everything the menu draws, in one answer. Reaching the release page from here is
            // allowed and is the service's decision to make: opening the globe is the host
            // asking out loud. The switch that governs asking on the app's own account is
            // reported as checkEnabled so the menu can say what it means for the rows.
            RegisterRpc("lang.list", async p =>
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                var listing = await LanguagePacks.ListAsync();

                return new
                {
                    current = CurrentLanguage(prefs),
                    appVersion = listing.AppVersion,
                    checkEnabled = prefs.CheckForUpdates,
                    busy = LanguagePacks.IsBusy,
                    languages = (listing.Languages ?? new List<LanguagePackEntry>()).Select(l => new
                    {
                        code = l.Code,
                        nativeName = l.NativeName,
                        englishName = l.EnglishName,
                        builtIn = l.BuiltIn,
                        installed = l.Installed,
                        installedVersion = l.InstalledVersion,
                        matchesApp = l.MatchesApp,
                        available = l.Available,
                        bytes = l.Bytes,
                        keys = l.Keys,
                        translated = l.Translated,
                        status = l.Status,
                    }).ToList(),
                    manifest = new
                    {
                        ok = listing.Manifest?.Ok ?? false,
                        fromCache = listing.Manifest?.FromCache ?? false,
                        checkedUtc = listing.Manifest?.CheckedUtc,
                        errorId = listing.Manifest?.ErrorId,
                    },
                };
            });

            // What the page needs on its very first frame: which language is saved, and where
            // its words are, so a host who reads Russian never sees the window in English
            // first. Nothing here reaches the network.
            RegisterRpc("lang.status", p =>
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                var code = CurrentLanguage(prefs);
                var install = LanguageCodes.IsEnglish(code) ? null : LanguagePacks.InstalledAny(code);

                return Task.FromResult<object>(new
                {
                    current = code,
                    appVersion = AssemblyHelper.GetApplicationVersion(),
                    installedVersion = install?.Version,
                    matchesApp = LanguageCodes.IsEnglish(code) || (install?.MatchesApp ?? false),
                    missingKeys = LanguageMissingKeys(install),
                    busy = LanguagePacks.IsBusy,
                    stringsUrl = LanguageStringsUrl(code, install?.Version),
                    fonts = LanguageFonts(install),
                    quietFetch = QuietLanguageFetch(prefs, code),
                });
            });

            // The whole fetch, awaited, with the bar driven by pushes. Two refusals are the
            // bridge's own because they are answers rather than endings: another pack is
            // already coming down, and a code this app has never heard of.
            RegisterRpc("lang.download", async p =>
            {
                var code = LanguageCodes.Normalize(p.Value<string>("code")) ?? throw UnknownLanguage();

                if (LanguagePacks.IsBusy)
                    throw new HostFacingException("lang.busy", "A language pack is already downloading.");

                // SynchronousProgress, never Progress<T>: a bar that is handed "done" before
                // "downloading" is worse than a bar that does not move.
                var progress = new SynchronousProgress<LanguagePackProgress>(pr =>
                    PostEvent("lang.downloadProgress", new
                    {
                        code = pr.Code,
                        phase = pr.Phase,
                        percent = pr.Percent,
                        bytesDone = pr.BytesDone,
                        bytesTotal = pr.BytesTotal,
                        messageId = pr.MessageId,
                        messageParams = pr.MessageParams,
                    }));

                var result = await LanguagePacks.DownloadAsync(code, progress);

                return new
                {
                    ok = result.Ok,
                    cancelled = result.Cancelled,
                    code = result.Code,
                    version = result.AppVersion,
                    reasonId = result.ReasonId,
                    reasonParams = result.ReasonParams,
                };
            });

            // The service's answer, never a literal true. A cancel that arrives once the pack
            // has begun moving into place stops nothing, and saying otherwise would be the one
            // lie a cancel button must never tell.
            RegisterRpc("lang.cancel", p =>
                Task.FromResult<object>(new { cancelled = LanguagePacks.Cancel(p.Value<string>("code")) }));

            // The switch itself. English is always available; anything else has to be on disk,
            // because a page that fetched a catalog that is not there would paint ids.
            RegisterRpc("lang.set", p =>
            {
                var code = LanguageCodes.Normalize(p.Value<string>("code")) ?? throw UnknownLanguage();

                var install = LanguageCodes.IsEnglish(code) ? null : LanguagePacks.InstalledAny(code);
                if (!LanguageCodes.IsEnglish(code) && install == null)
                    throw new HostFacingException("lang.notInstalled", "That language is not downloaded yet.");

                // The one legal writer. Mutate loads, edits and writes userprefs.json under the
                // gate, so a Discord publish or a launch record landing in between is kept.
                UserPrefsProvider.Mutate(prefs => prefs.Language = code);

                var version = install?.Version ?? AssemblyHelper.GetApplicationVersion();

                // Player messages follow the interface unless the host said otherwise, so the
                // host catalog is picked again before the event goes out.
                RefreshHostCatalog();
                LanguagePacks.NotifyLanguageChanged(code, version);

                return Task.FromResult<object>(new
                {
                    ok = true,
                    code,
                    version,
                    stringsUrl = LanguageStringsUrl(code, install?.Version),
                    fonts = LanguageFonts(install),
                    missingKeys = LanguageMissingKeys(install),
                });
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
                // Both save formats, side by side: pre-1.0 "{name}.fwl" pairs and the 1.0
                // world DIRECTORIES. Conversion originals and auto snapshots are backup
                // layers, so they never show up here as pickable worlds.
                var folder = options.GetValidatedSaveDataFolder();
                return Task.FromResult<object>(WorldStore.GetWorldNames(folder.FullName));
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
                var found = WorldStore.Find(saveFolder.FullName, world);

                if (found != null && found.Format == WorldFormat.Chunked)
                {
                    // A 1.0 world is a directory: list the generation and its chunk files.
                    foreach (var f in new DirectoryInfo(found.Folder).GetFiles())
                        files.Add(new { name = $"{found.Sub}/{world}/{f.Name}", sizeBytes = f.Length, modifiedUtc = f.LastWriteTimeUtc });
                }
                else if (found != null)
                {
                    foreach (var ext in new[] { ".fwl", ".db", ".fwl.old", ".db.old" })
                    {
                        var path = Path.Combine(found.Folder, world + ext);
                        if (!File.Exists(path)) continue;
                        var f = new FileInfo(path);
                        files.Add(new { name = $"{found.Sub}/{f.Name}", sizeBytes = f.Length, modifiedUtc = f.LastWriteTimeUtc });
                    }
                }

                foreach (var sub in WorldStore.WorldSubfolders)
                {
                    foreach (var layer in WorldStore.EnumerateBackups(saveFolder.FullName, sub, world))
                    {
                        // The ".old" pair is shown above with the live files, like it always was.
                        if (layer.Kind == WorldBackupKind.Old) continue;
                        backups.Add(new { name = $"{sub}/{layer.Name}", sizeBytes = layer.SizeBytes, modifiedUtc = layer.LastWriteUtc });
                    }
                }

                // Who has this world spoken for. The delete control on the page needs the same
                // answer the delete itself will give, or it would offer an action that can only
                // come back refused.
                var userSave = UserPrefsProvider.LoadPreferences().SaveDataFolderPath;
                var claimant = ProfileSelectingWorld(
                    ServerPrefsProvider.LoadPreferences(), world, saveFolder.FullName, userSave);
                var claimantRunning = claimant != null
                    && Sessions.TryGetValue(claimant.ProfileName, out var claimantSession)
                    && claimantSession.Server.Status != ServerStatus.Stopped;

                return Task.FromResult<object>(new
                {
                    world,
                    folder = saveFolder.FullName,
                    sub = found?.Sub,
                    owner = claimant?.ProfileName,
                    running = claimantRunning,
                    format = found == null ? null : (found.Format == WorldFormat.Chunked ? "chunked" : "legacy"),
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

                // Layers whose world is gone. They live in the same folders and are the same
                // shape, but the loop below asks for layers world by world from the LIVE worlds,
                // so a set with no live owner is never asked for. Answered as its own array so a
                // page that does not know about it still reads the world list exactly as before.
                var orphans = new List<(DateTime modified, object dto)>();

                foreach (var saveFolder in KnownSaveFolders())
                {
                    // One classifier, and it is WorldStore's: it knows which names are layers
                    // rather than worlds (a rolling save belongs to the world it is named after)
                    // and it looks for a live owner in BOTH worlds subfolders before calling a
                    // set owner less, so a world kept in "worlds" still claims layers sitting in
                    // "worlds_local". Reading the subfolders itself is why this sits outside the
                    // loop below rather than inside it.
                    foreach (var set in WorldStore.EnumerateOrphanBackups(saveFolder))
                    {
                        var layers = set.Layers
                            .Select(layer => BuildBackupLayerDto(layer, TryReadWorldDay(layer.DbPath)))
                            .ToList();

                        orphans.Add((set.Layers.Max(layer => layer.LastWriteUtc), new
                        {
                            world = set.WorldName,
                            folder = saveFolder,
                            sub = set.Sub,
                            layers,
                            backupBytes = set.SizeBytes,
                        }));
                    }

                    foreach (var sub in WorldStore.WorldSubfolders)
                    {
                        // WorldStore returns worlds in BOTH formats and never returns a backup,
                        // so a converted world's "{name}_backup_{stamp}.fwl" original folds in
                        // as a layer below instead of masquerading as a second world.
                        foreach (var live in WorldStore.EnumerateIn(saveFolder, sub))
                        {
                            var world = live.Name;
                            var owner = profiles.FirstOrDefault(pr =>
                                string.Equals(pr.WorldName, world, StringComparison.OrdinalIgnoreCase)
                                && SameFolder(pr.SaveDataFolderPath, saveFolder));
                            var running = owner != null
                                && Sessions.TryGetValue(owner.ProfileName, out var session)
                                && session.Server.Status != ServerStatus.Stopped;

                            var layers = new List<object>();
                            long backupBytes = 0;
                            foreach (var layer in WorldStore.EnumerateBackups(saveFolder, sub, world))
                            {
                                backupBytes += layer.SizeBytes;
                                layers.Add(BuildBackupLayerDto(layer, TryReadWorldDay(layer.DbPath)));
                            }

                            groups.Add((live.LastWriteUtc, new
                            {
                                world,
                                folder = saveFolder,
                                sub,
                                owner = owner?.ProfileName,
                                running,
                                format = live.Format == WorldFormat.Chunked ? "chunked" : "legacy",
                                formatLabel = live.FormatLabel,
                                committed = live.IsCommitted,
                                saveNumber = live.SaveNumber,
                                sizeBytes = live.SizeBytes,
                                modifiedUtc = live.LastWriteUtc,
                                day = TryReadWorldDay(live.DbPath),
                                backups = layers,
                                backupBytes,
                            }));
                        }
                    }
                }

                return Task.FromResult<object>(new
                {
                    worlds = groups.OrderByDescending(g => g.modified).Select(g => g.dto).ToList(),
                    orphans = orphans.OrderByDescending(g => g.modified).Select(g => g.dto).ToList(),
                });
            });

            // Restores one backup layer over the live world pair - but FIRST lays down a
            // safety copy of the current live files ("{world}_backup_restore-<ts>"), so a
            // restore is always reversible from the Barrow itself. Refuses while any server
            // in that save folder is running the world (the game would clobber the files).
            RegisterRpc("backups.restore", async p =>
            {
                var reference = ValidateBackupRef(p, requireBackupShape: true);
                var saveFolder = reference.SaveFolder;
                var world = reference.World;
                var file = reference.File;
                var layer = reference.Layer;

                foreach (var pr in ServerPrefsProvider.LoadPreferences())
                {
                    if (!string.Equals(pr.WorldName, world, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Sessions.TryGetValue(pr.ProfileName, out var session)
                        && session.Server.Status != ServerStatus.Stopped)
                        throw new InvalidOperationException(
                            $"'{pr.ProfileName}' is running world '{world}' right now. Stop it before restoring a backup.");
                }

                // Everything that touches the files, including the safety copy, in one place a
                // test can drive: a 1.0 world is a whole directory, so it runs off the UI thread.
                var (snapshot, restoredDb) = await Task.Run(
                    () => RestoreBackupLayer(saveFolder, reference.Sub, world, layer));

                var restored = WorldStore.FindIn(saveFolder, reference.Sub, world);
                Logger.Information("Barrow restore: '{0}' <- '{1}' (kind: {2}; db: {3}; safety copy: {4}).",
                    world, file, WorldStore.KindToken(layer.Kind), restoredDb, snapshot ?? "none");
                return (object)new
                {
                    ok = true,
                    snapshot,
                    restoredDb,
                    format = restored == null ? null : (restored.Format == WorldFormat.Chunked ? "chunked" : "legacy"),
                    day = TryReadWorldDay(restored?.DbPath),
                };
            });

            // Deletes one backup layer: a legacy .fwl plus its paired .db, or a whole 1.0
            // backup DIRECTORY. The live world can never be named here - ValidateBackupRef
            // resolves the reference against the world's actual backup layers only.
            RegisterRpc("backups.delete", async p =>
            {
                // The one path that may name a damaged layer: clearing it is the only thing
                // left to do with one, and it can be as large as the world itself.
                var reference = ValidateBackupRef(p, requireBackupShape: true, allowDamaged: true);
                // A 1.0 layer is a directory tree, so the delete goes off the UI thread.
                var deleted = await Task.Run(() => WorldStore.DeleteBackup(reference.Layer));

                Logger.Information("Barrow delete: world '{0}' layer '{1}' ({2} item(s)).",
                    reference.World, reference.File, deleted.Count);
                return (object)new { deleted };
            });

            // Deletes a whole world: the 1.0 world directory (or the pre-1.0 ".fwl" and ".db"
            // pair), the same-named world hiding in the sibling worlds subfolder, and the
            // biome cache the game keys on the world's stored name. Backup layers are left
            // alone unless the host asks for those too.
            //
            // Four things have to be true before a byte moves: the reference names a save
            // folder BakaLoader knows and one of the game's two worlds subfolders, the host
            // typed the world's own name, nothing is running the world, and no realm still has
            // it selected. The last one is what stops a delete from pulling the world out from
            // under a realm that is merely stopped rather than gone.
            RegisterRpc("worlds.delete", async p =>
            {
                var world = p.Value<string>("world");
                var folder = p.Value<string>("folder");
                var sub = p.Value<string>("sub");
                var includeBackups = p.Value<bool?>("includeBackups") ?? false;

                if (string.IsNullOrWhiteSpace(world))
                    throw new HostFacingException("worlds.delete.worldRequired", "world is required");
                if (!WorldStore.IsSafeReferenceToken(world))
                    throw new HostFacingException("worlds.delete.badWorldRef", "Invalid characters in world reference.");
                if (sub != "worlds_local" && sub != "worlds")
                    throw new HostFacingException("worlds.delete.badSubfolder", "Invalid save subfolder.");
                if (string.IsNullOrWhiteSpace(folder) || !KnownSaveFolders().Any(k => SameFolder(k, folder)))
                    throw new HostFacingException("worlds.delete.unknownSaveFolder", "Unknown save folder.");

                if (!DeleteWorldNameConfirmed(world, p.Value<string>("confirmName")))
                    throw new HostFacingException("worlds.delete.confirmNameMismatch",
                        $"Type the world's name exactly as it is spelled ('{world}') to delete it.", ("world", world));

                var saveFolder = Path.GetFullPath(folder);
                var userSave = UserPrefsProvider.LoadPreferences().SaveDataFolderPath;

                // A live server holds the files open and writes the world back out as it saves,
                // so a delete underneath one leaves half a world and a running game on top of it.
                foreach (var session in Sessions.Values)
                {
                    if (session.Server.Status == ServerStatus.Stopped) continue;

                    var live = session.Server.Options;
                    var liveFolder = string.IsNullOrWhiteSpace(live?.SaveDataFolderPath)
                        ? userSave
                        : live.SaveDataFolderPath;

                    if (string.Equals(live?.WorldName, world, StringComparison.OrdinalIgnoreCase)
                        && SameFolder(liveFolder, saveFolder))
                        throw new HostFacingException("worlds.delete.worldRunning",
                            $"'{session.ProfileName}' is running world '{world}' right now. Stop it before deleting the world.",
                            ("profile", session.ProfileName), ("world", world));
                }

                var claimant = ProfileSelectingWorld(
                    ServerPrefsProvider.LoadPreferences(), world, saveFolder, userSave);
                if (claimant != null)
                    throw new HostFacingException("worlds.delete.worldClaimed",
                        $"'{claimant.ProfileName}' still has '{world}' chosen as its world. Point that realm at another world first, or delete the realm.",
                        ("profile", claimant.ProfileName), ("world", world));

                // A 1.0 world is a whole directory tree, so the delete goes off the UI thread.
                var removed = await Task.Run(() => WorldStore.DeleteWorld(saveFolder, world, includeBackups));
                if (removed.Count == 0)
                    throw new HostFacingException("worlds.delete.noSuchWorld",
                        $"There is no world named '{world}' in that save folder any more.", ("world", world));

                Logger.Warning("Deleted world '{0}' from {1} ({2} item(s); backup layers {3}).",
                    world, saveFolder, removed.Count, includeBackups ? "deleted too" : "kept");

                return (object)new { world, folder = saveFolder, sub, deleted = removed, includeBackups };
            });

            // Copies one world beside itself under a new name, and rewrites the name stored
            // inside the copy's own header so the copy is a real rename in both save formats.
            // The source and its backup layers are never touched.
            //
            // The same four things have to be true here as for a delete, for the same
            // reasons: the reference names a save folder BakaLoader knows and, when it names
            // one at all, one of the game's two worlds subfolders; the new name is a name a
            // world can be saved under; nothing is running the world, because a live server
            // rewrites it as it saves and a copy taken underneath one is a copy of half a
            // save; and the world is really there. What is NOT checked here is whether the
            // new name is free, because that answer goes stale between the check and the
            // copy: WorldStore refuses an occupied name at the moment it would land on it.
            RegisterRpc("worlds.copyAs", async p =>
            {
                var source = p.Value<string>("source");
                var target = (p.Value<string>("target") ?? string.Empty).Trim();
                var folder = p.Value<string>("folder");
                var sub = p.Value<string>("sub");

                if (string.IsNullOrWhiteSpace(source))
                    throw new HostFacingException("worlds.copySourceRequired", "source is required");
                if (!WorldStore.IsSafeReferenceToken(source))
                    throw new HostFacingException("worlds.copyBadSourceRef", "Invalid characters in world reference.");
                if (!string.IsNullOrWhiteSpace(sub) && sub != "worlds_local" && sub != "worlds")
                    throw new HostFacingException("worlds.copyBadSubfolder", "Invalid save subfolder.");

                var problem = WorldStore.WorldNameProblem(target);
                if (problem == "required")
                    throw new HostFacingException("worlds.copyTargetRequired", "Give the copy a world name.");
                if (problem == "tooLong")
                    throw new HostFacingException("worlds.copyTargetTooLong",
                        $"A world name can be at most {WorldStore.WorldNameMaxLength} characters long.",
                        ("limit", WorldStore.WorldNameMaxLength));
                if (problem != null)
                    throw new HostFacingException("worlds.copyBadTargetRef",
                        "That is not a name a world can be saved under.", ("target", target));

                string saveFolder;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    saveFolder = new ValheimServerOptions
                    {
                        SaveDataFolderPath = ResolveSaveDataFolder(null),
                    }.GetValidatedSaveDataFolder().FullName;
                }
                else
                {
                    if (!KnownSaveFolders().Any(k => SameFolder(k, folder)))
                        throw new HostFacingException("worlds.copyUnknownSaveFolder", "Unknown save folder.");
                    saveFolder = Path.GetFullPath(folder);
                }

                RefuseWhileTheWorldIsBeingWritten(source, saveFolder);

                var world = string.IsNullOrWhiteSpace(sub)
                    ? WorldStore.Find(saveFolder, source)
                    : WorldStore.FindIn(saveFolder, sub, source);
                if (world == null)
                    throw new HostFacingException("worlds.copyNoSuchWorld",
                        $"There is no world named '{source}' in that save folder.", ("world", source));

                // A 1.0 world is a whole directory tree, so the copy goes off the UI thread.
                var landed = await Task.Run(() => WorldStore.CopyWorldAs(world, target));

                Logger.Information("Copied world '{source}' as '{target}' into {folder}.", source, target, landed);

                return (object)new { source, target, folder = saveFolder, sub = world.Sub, landed };
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
                        //
                        // Either spelling of the name counts as the same character. An event
                        // written before 1.2.0 carries the name as the machine's own code page
                        // read it off the server rather than as UTF-8, and on a machine whose
                        // page cannot be undone (932, 936, 950) it stays that way in the journal
                        // for good. Matched on the name alone it became a second person in the
                        // hall, with one person's deaths under one spelling and their visits
                        // under the other.
                        var byCharacter = players.Values.FirstOrDefault(a =>
                            !string.IsNullOrWhiteSpace(e.Character)
                            && (string.Equals(a.Character, e.Character, StringComparison.OrdinalIgnoreCase)
                                || TextRepair.IsDamagedSpellingOf(e.Character, a.Character)
                                || TextRepair.IsDamagedSpellingOf(a.Character, e.Character)));
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

            // Starts the statistics journal again from nothing. The journal on disk is moved
            // aside as "analytics.json.bak-<stamp>" and one such copy is kept, so a host who
            // clears the numbers now and then does not leave a folder of old journals behind.
            // Every realm's numbers live in the one journal, so this clears all of them.
            RegisterRpc("analytics.reset", p =>
            {
                var kept = Analytics.Reset();
                Logger.Warning("Statistics journal reset from the interface ({0}).",
                    kept == null ? "no journal on disk to keep" : "kept as " + Path.GetFileName(kept));

                return Task.FromResult<object>(new
                {
                    ok = true,
                    kept = kept == null ? null : Path.GetFileName(kept),
                });
            });

            // World identity from the world metadata (read-only): the typed seed string and
            // the numeric seed the game derived from it. Reads the pre-1.0 "{world}.fwl" and
            // the 1.0 "_main.{N}.fwl2" alike - the fields up to the seed sit in the same
            // order in both. exists=false means the world has no metadata yet (it will be
            // generated, with a random seed, on first launch).
            RegisterRpc("world.seed", p =>
            {
                var world = p.Value<string>("world");
                if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("world is required");

                var saveFolder = ResolveSaveDataFolder(null);
                var stored = WorldStore.Find(saveFolder, world);
                var info = FwlReader.TryReadWorld(saveFolder, world);
                return Task.FromResult<object>(new
                {
                    world,
                    exists = info != null || stored != null,
                    seedName = info?.SeedName ?? "",
                    seed = info?.Seed ?? 0,
                    worldVersion = info?.WorldVersion ?? 0,
                    worldGenVersion = info?.WorldGenVersion ?? 0,
                    format = stored == null ? null : (stored.Format == WorldFormat.Chunked ? "chunked" : "legacy"),
                    saveNumber = stored?.SaveNumber,
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

                // A world whose own settings BakaLoader has never read is read here too, so the
                // card draws what the world really holds rather than an empty set of dials.
                ImportWorldKeysOnFirstMeeting(world, ResolveSaveDataFolder(null));

                var prefs = WorldPrefsProvider.LoadPreferences(world);
                var keys = WorldKeyList(prefs);
                return Task.FromResult<object>(new
                {
                    world,
                    preset = prefs?.Preset ?? "",
                    modifiers = prefs?.Modifiers ?? new Dictionary<string, string>(),
                    // The stored keys WHOLE, and the same set split the two ways the card draws
                    // it. Whole first because that is the contract: everything saved comes back,
                    // including a key no toggle represents.
                    keys,
                    switches = keys.Where(WorldGen.IsSwitch).ToList(),
                    passThrough = keys.Where(k => !WorldGen.IsSwitch(k)).ToList(),
                    // What a first meeting brought in, once, for the card to say so. Null when
                    // there was nothing to bring in or the host has already been told.
                    imported = WorldKeyImportNotice(world),
                });
            });

            RegisterRpc("worldgen.save", p =>
            {
                var world = p.Value<string>("world");
                if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("world is required");

                // The save below writes a world-prefs entry, which is the very thing that makes a
                // world already known. So the first meeting has to happen here too, and first, or
                // a save is enough to claim a world nobody ever read: the header would never be
                // opened again and the next start's -resetmodifiers would wipe what it holds. The
                // first-time setup wizard reaches this handler on a brand new install, which is
                // exactly the world this import exists for.
                ImportWorldKeysOnFirstMeeting(world, ResolveSaveDataFolder(null));

                // Every dial is validated against the game's own vocabulary; a missing,
                // empty, or "normal" value means "game default" and drops the key so no
                // -modifier arg is emitted for it. Shared with realm creation.
                var modifiers = ParseWorldModifiers(p["modifiers"] as JObject);

                // Optional, and absent is not empty: a page that does not know about switches
                // must not clear the ones a world carries. Shared with realm creation.
                var chosenSwitches = ParseWorldKeys(p["keys"]);

                // Optional, and false unless asked for. The Settings hall sends the WHOLE dial
                // state on every save, so there a blank set really is "put every dial back to
                // Normal" and replacing is right. The first-time wizard sends only the dials the
                // host moved, and silence about a dial there is not a statement about it: with
                // this flag the moved ones are written over what is stored and the rest are left
                // where the import found them.
                var mergeModifiers = p.Value<bool?>("mergeModifiers") == true;

                var prefs = WorldPrefsProvider.LoadPreferences(world) ?? new WorldPreferences { WorldName = world };
                prefs.Preset = null; // individual dials replace any preset (mutually exclusive)
                if (mergeModifiers)
                {
                    var merged = prefs.Modifiers != null
                        ? new Dictionary<string, string>(prefs.Modifiers)
                        : new Dictionary<string, string>();
                    foreach (var pair in modifiers) merged[pair.Key] = pair.Value;
                    prefs.Modifiers = merged;
                }
                else
                {
                    prefs.Modifiers = modifiers;
                }
                prefs.Keys = MergeWorldKeys(prefs.Keys, chosenSwitches);
                WorldPrefsProvider.SavePreferences(prefs);

                var stored = prefs.Modifiers ?? new Dictionary<string, string>();
                var keys = prefs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                return Task.FromResult<object>(new
                {
                    world,
                    preset = "",
                    modifiers = stored,
                    keys,
                    switches = keys.Where(WorldGen.IsSwitch).ToList(),
                    passThrough = keys.Where(k => !WorldGen.IsSwitch(k)).ToList(),
                });
            });

            // The host has read what a first meeting brought in, so it is not shown again.
            RegisterRpc("worldgen.noticeSeen", p =>
            {
                var world = p.Value<string>("world");
                if (!string.IsNullOrWhiteSpace(world)) _worldKeyImports.TryRemove(world, out _);
                return Task.FromResult<object>(new { world, imported = (object)null });
            });

            // Renders the Atlas biome/terrain map for a world's seed to a cached PNG
            // served via the atlas.baka virtual host. Mod-free: the map is computed
            // from our own WorldGenerator port, adapted to Expand World Size configs
            // where detected (other worldgen mods surface as honest warnings).
            RegisterRpc("atlas.render", async p =>
            {
                if (_atlasRenderInProgress)
                    throw new HostFacingException("atlas.render.alreadyRunning", "A map render is already in progress.");
                _atlasRenderInProgress = true;
                try
                {
                    var world = p.Value<string>("world");
                    if (string.IsNullOrWhiteSpace(world))
                        throw new HostFacingException("atlas.render.worldRequired", "world is required");
                    var size = Math.Clamp(p.Value<int?>("size") ?? 2048, 256, 4096);
                    var force = p.Value<bool?>("force") ?? false;

                    var info = FwlReader.TryReadWorld(ResolveSaveDataFolder(null), world);
                    if (info == null)
                        throw new HostFacingException("atlas.render.noFwl",
                            $"World '{world}' has no .fwl yet. Choose a seed or start the server once first.",
                            ("world", world));

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

                    var stored = WorldStore.Find(ResolveSaveDataFolder(null), world);
                    var dbPath = stored?.DbPath;
                    if (dbPath == null || !File.Exists(dbPath))
                    {
                        // "Nothing has been saved yet" and "something is there and it will not
                        // open" want opposite advice, so the answer says which one this is. A
                        // world whose newest generation was never committed has files on disk
                        // even though there is no readable save to point the reader at.
                        return WorldInfoWithNoSave(world, stored != null && !stored.IsCommitted, null);
                    }

                    var savedAtUtc = File.GetLastWriteTimeUtc(dbPath);

                    // A 1.0 world keeps its ZDOs in sibling .chunk files, so the reader is
                    // handed the world DIRECTORY; a pre-1.0 world is handed its .db.
                    var readTarget = stored.Format == WorldFormat.Chunked ? stored.Folder : dbPath;

                    // Keep whatever the reader says about this one file, for this one call.
                    List<string> diagnostics = null;
                    var db = await Task.Run(() => ReadWorldSaveWithDiagnostics(readTarget, out diagnostics));

                    // The save is right there, so "wait for the first save" is the wrong thing
                    // to tell the host about it.
                    if (db == null) return WorldInfoWithNoSave(world, saveExists: true, diagnostics);

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
                        // Anything above zero means portals, builds and the shared map are only
                        // part of the story, so the map must not be read as the whole world.
                        chunksTotal = db.ChunksTotal,
                        chunksSkipped = db.ChunksSkipped,
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
                    // at the next server start. The installer reports its own failures into
                    // the process-wide record, so the scope files them under the realm the
                    // host is actually looking at rather than against every realm at once.
                    using (Tools.CompanionPluginStatus.BeginProfile(ActiveProfileName))
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

                    // Also while the legacy migration is still deferred (the old mod's folder is
                    // locked until the server stops): writing our own cfg now means the count the
                    // host just chose is already there when the migration finally runs, instead of
                    // catching up a server start later.
                    if (installed || legacy)
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

            // Asks the launch guard's question without starting anything, so the Start button
            // can put a changed build (or a waiting Steam update) to the host first.
            RegisterRpc("server.launchCheck", async p =>
            {
                var prefs = ResolveStartPrefs(p);
                var profile = string.IsNullOrWhiteSpace(prefs.ProfileName) ? ActiveProfileName : prefs.ProfileName;
                var options = BuildServerOptions(prefs);
                // Reading the install can mean hashing the server binaries, so keep it off
                // the UI thread: the Start button must not stutter to ask this question.
                return await Task.Run(() => BuildLaunchCheck(profile, options, prefs));
            });

            RegisterRpc("server.start", p =>
            {
                var prefs = (p["prefs"] ?? throw new ArgumentException("prefs is required"))
                    .ToObject<ServerPreferences>();
                var session = GetOrCreateSession(
                    string.IsNullOrWhiteSpace(prefs.ProfileName) ? CurrentProfile : prefs.ProfileName);
                var options = BuildServerOptions(MergeLaunchHistory(prefs));

                // Steam or steamcmd is rewriting this install folder right now, so valheim_server.exe
                // and the managed assemblies beside it are mid write. The launch guard cannot catch
                // this: it compares builds, and the build on disk during a rewrite is whatever the
                // writer has got to. Nothing is staged and nothing is started.
                if (IsServerUpdateRunning(options?.ServerExePath))
                    return Task.FromResult(RefusedRpc(ValheimServer.LaunchBlockedMessage, "updateRunning"));

                EnsureNoServerCollisions(session, options);
                StageLaunchAnswer(session.ProfileName, p.Value<string>("guard"));

                // Start() does nothing at all when the server is not startable, which would
                // leave the answer armed for whatever launches next. Take it back.
                if (!session.Server.CanStart) DropLaunchAnswer(session.ProfileName);
                else session.Server.Start(options);

                return Task.FromResult<object>(BuildServerState(session));
            });

            // What updating this server would mean right now: how it was installed, whether
            // Steam has anything queued for it, and whether BakaLoader can do it from here.
            // Classify is cheap, the build probe can hash binaries, so it goes off the UI thread.
            RegisterRpc("server.updateCheck", async p =>
            {
                var prefs = ResolveStartPrefs(p);
                var profile = string.IsNullOrWhiteSpace(prefs.ProfileName) ? ActiveProfileName : prefs.ProfileName;
                var options = BuildServerOptions(prefs);
                var stopped = !Sessions.TryGetValue(profile, out var session)
                    || session.Server.Status == ServerStatus.Stopped;

                return await Task.Run(() => BuildServerUpdateCheck(profile, options, stopped));
            });

            // Runs the update: worlds aside first when asked, then Steam or steamcmd depending
            // on how the install was made, then (optionally) the normal guarded start. Answers
            // immediately with whether it began; everything after that arrives as an event.
            RegisterRpc("server.update", p =>
            {
                var prefs = ResolveStartPrefs(p);
                var profile = string.IsNullOrWhiteSpace(prefs.ProfileName) ? ActiveProfileName : prefs.ProfileName;
                var options = BuildServerOptions(prefs);
                var backup = p.Value<bool?>("backup") ?? true;
                var startAfter = p.Value<bool?>("startAfter") ?? false;

                object Refuse(string reason) => new { started = false, reason };

                // Never write into an install a running server has files open in.
                if (Sessions.TryGetValue(profile, out var session)
                    && session.Server.Status != ServerStatus.Stopped)
                    return Task.FromResult(Refuse("Stop the server before updating it."));

                var install = ClassifyInstall(options?.ServerExePath);
                if (install == null || install.Kind == ServerInstallKind.Unknown)
                {
                    return Task.FromResult(Refuse(string.IsNullOrWhiteSpace(install?.Reason)
                        ? UnknownInstallMessage
                        : install.Reason));
                }

                if (IsServerUpdateRunning(options?.ServerExePath))
                    return Task.FromResult(Refuse("An update is already running for this install."));

                var cts = new CancellationTokenSource();

                // A leftover entry is dropped, never cancelled. The service links this token
                // into its own operation, and an entry that is still here belongs to an update
                // that never completed: cancelling it is exactly the thing that used to stop
                // BakaLoader awaiting steamcmd while steamcmd carried on writing the install.
                // The refusal above is what stops a second update for the same install; this is
                // only the bookkeeping catching up.
                if (ServerUpdateCts.TryRemove(profile, out var stale))
                {
                    try { stale.Dispose(); } catch { }
                }
                ServerUpdateCts[profile] = cts;

                // What the Start button, the Restart button and the close guard ask about.
                ServerUpdateExe[profile] = options.ServerExePath;
                ServerUpdatePhaseSeen[profile] = ServerUpdatePhase.Idle;

                // Nothing has been copied yet. RunUpdateBackupAsync flips this when it does.
                ServerUpdateBackedUp[profile] = false;

                var request = new ServerUpdateRequest
                {
                    ProfileName = profile,
                    ServerExePath = options.ServerExePath,
                    SaveDataFolder = options.SaveDataFolderPath,
                    BackupWorldsFirst = backup,
                    StartAfter = startAfter,
                };

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var outcome = await ServerUpdates.UpdateAsync(
                            request, () => RunUpdateBackupAsync(profile, options), cts.Token);

                        // A refusal answers on the return value and fires no Completed event,
                        // because no operation began - so without this the bar would sit there
                        // waiting for news that is never coming. OnServerUpdateCompleted takes
                        // the profile's entry out of the map, so this is a no-op whenever the
                        // event already handled the same outcome.
                        if (outcome != null)
                        {
                            if (string.IsNullOrWhiteSpace(outcome.ProfileName)) outcome.ProfileName = profile;
                            OnServerUpdateCompleted(outcome);
                        }
                    }
                    catch (Exception ex)
                    {
                        // UpdateAsync is documented never to throw, so this is the belt to that
                        // brace: without it a surprise would leave the bar spinning forever.
                        Logger.Error(ex, "The server update for profile {profile} failed outright", profile);
                        OnServerUpdateCompleted(new ServerUpdateResult
                        {
                            Ok = false,
                            ProfileName = profile,
                            StartAfter = startAfter,
                            Reason = ex.Message,
                        });
                    }
                });

                return Task.FromResult<object>(new { started = true, reason = (string)null });
            });

            // Only meaningful while BakaLoader is waiting on the Steam client. Steam carries on
            // with the download either way; this just stops watching for it.
            RegisterRpc("server.updateCancel", p =>
            {
                var profile = p.Value<string>("profile");
                if (string.IsNullOrWhiteSpace(profile)) profile = ActiveProfileName;

                // The bridge's own token is NEVER cancelled here. The service used to link it
                // into the steamcmd run, so cancelling it stopped BakaLoader awaiting steamcmd
                // without stopping steamcmd: the run carried on rewriting the install, the
                // operation failed with a raw progress line, and the per install lock came off
                // while files were still being written. The service no longer links it there at
                // all, and only it decides what a cancel means. The token this window keeps is
                // now only the per profile latch that says an update it started is in flight.
                var cancelled = false;
                try
                {
                    var exe = ResolveUpdateExePath(profile);

                    // Whether a cancel means anything is the service's answer, not a guess made
                    // from the last progress event that reached this window: only the Steam
                    // waiting phases can be called off, and by the time a guess is made the run
                    // may already have moved on to writing. Cancel answers false and does
                    // nothing when it will not take, which is exactly what the page reads.
                    if (!string.IsNullOrWhiteSpace(exe)) cancelled = ServerUpdates.Cancel(exe);
                }
                catch (Exception ex)
                {
                    cancelled = false;
                    Logger.Warning(ex, "Could not ask the update service to stop for profile {profile}", profile);
                }

                return Task.FromResult<object>(new { cancelled });
            });

            // The host answered the launch guard from the Hearth banner without starting yet
            // ("Not now"): drop the condition so it stops nagging until the next attempt.
            RegisterRpc("server.dismissLaunchHold", p =>
            {
                var profile = p.Value<string>("profile");
                ClearLaunchHold(string.IsNullOrWhiteSpace(profile) ? ActiveProfileName : profile);
                return Task.FromResult<object>(true);
            });

            RegisterRpc("server.stop", p =>
            {
                Server.Stop();
                return Task.FromResult<object>(BuildServerState());
            });

            RegisterRpc("server.restart", async p =>
            {
                // A restart is a stop and a start, so an update rewriting the install refuses it
                // for the same reason a start refuses: the server would come back up out of files
                // steamcmd is still writing. Refusing BEFORE the stop matters most here, because
                // the alternative is an outage that lasts until somebody notices.
                if (IsServerUpdateRunning(ResolveUpdateExePath(ActiveSession.ProfileName)))
                    return RefusedRpc(ValheimServer.LaunchBlockedMessage, "updateRunning");

                // A restart stops the server and starts it again, so it meets the same guard.
                // The host's answer is staged here, before anything goes down.
                StageLaunchAnswer(ActiveSession.ProfileName, p.Value<string>("guard"));

                // Smart restart: countdown running -> bypass & restart NOW; players online ->
                // 1-minute warned countdown; empty server -> immediate restart, no broadcast.
                var restart = await Server.RequestSmartRestart();

                // Nothing was started, so the answer belongs to no launch. Leaving it armed is
                // how an unattended relaunch minutes later inherits a choice about this one.
                if (string.Equals(restart, "unavailable", StringComparison.OrdinalIgnoreCase))
                {
                    DropLaunchAnswer(ActiveSession.ProfileName);
                }

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

            // hostId is optional and additive: a page from an older build sends only the target
            // and the kick goes out by name, exactly as it always did.
            RegisterRpc("players.kick", async p =>
                await Server.KickAsync(RequireTarget(p), p.Value<string>("hostId")));
            // Takes the cheat marks back off the world. It is a players.* method because it
            // is the Players hall's own command and because what it cannot reach is a player's
            // own inventory, which is the sentence the confirm has to carry.
            //
            // It has an RPC of its own rather than going through server.command because the
            // sweep is one pass over every object in the world: a normal console line is
            // answered in milliseconds, and this one can take a moment on a long-lived world.
            // The reply is handed back exactly as the plugin said it, and the page reads it.
            RegisterRpc("players.cleanse", async p =>
            {
                var outcome = await RunCleanseAsync(
                    command => Server.SendRconCommandAsync(command, quiet: command != "baka_cleanse"),
                    CleansePollEvery,
                    CleansePollCeiling,
                    // Straight into the application log, which is what the Saga hall draws:
                    // a sweep over a big world takes minutes, and a page where nothing moves
                    // for minutes is a page a host presses again.
                    line => AppLogger.Information("{Line:l}", "[RCON] " + line));

                if (outcome.StillRunning)
                {
                    AppLogger.Information(
                        "The cleanse is still running after {0} minutes. Its counts land in the server log, "
                        + "and baka_cleanse_status answers with them.", (int)CleansePollCeiling.TotalMinutes);
                }

                if (outcome.NotAnswering)
                {
                    AppLogger.Warning(
                        "The server stopped answering while the cleanse was running, so BakaLoader "
                        + "stopped waiting for it.");
                }

                return new
                {
                    ok = outcome.Ok,
                    response = outcome.Response,
                    stillRunning = outcome.StillRunning,
                    notAnswering = outcome.NotAnswering,
                };
            });

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
                    ?? throw new HostFacingException("players.spawn.unknownPrefab",
                        $"Unknown prefab '{prefab}'", ("prefab", prefab));

                var result = await Server.SpawnAtPlayerAsync(playerName, entry, amount, levelOrQuality);

                // { ok, message } rather than the bare true this used to answer with. The server
                // is the only side that knows whether anything landed, and its own line already
                // names the stack and the quality, so it travels whole. Additive: a page from an
                // older build reads the object as truthy and falls back to its own wording.
                return new { ok = result.Ok, message = result.Message };
            });

            RegisterRpc("players.isListed", p =>
            {
                var (list, id) = RequireListArgs(p);

                // null travels to the UI as "unknown", which is what a file we could not read
                // means. The menu already treats anything that is not true as not listed.
                bool? listed = PlayerListService.IsListed(ResolveSaveDataFolder(null), list, id);
                return Task.FromResult<object>(listed);
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
                if (_modScanInProgress)
                    throw new HostFacingException("mods.scan.alreadyRunning", "A mod scan is already in progress");
                _modScanInProgress = true;
                try
                {
                    // A host who pressed Scan means "ask the sites again", not "show me
                    // what you happen to be holding". Without this the held index stood
                    // for fifteen minutes and a scan a minute after a release found
                    // nothing, which is exactly what a host reported. Each client keeps
                    // its own short cooldown, so pressing twice is still one trip.
                    var force = p?.Value<bool?>("force") ?? false;
                    ThunderstoreIndexState indexState = null;
                    if (force) indexState = await ThunderstoreClient.RefreshAsync();

                    var pluginsDir = GetPluginsDirectory()
                        ?? throw new HostFacingException("mods.scan.noServerPath", "Server exe path is not configured");
                    var mods = ModScanner.ScanPlugins(pluginsDir);

                    await Task.WhenAll(mods.Select(async mod =>
                    {
                        try
                        {
                            var package = await ThunderstoreClient.GetLatestAsync(mod.Author, mod.ModName);
                            mod.LatestVersion = package?.LatestVersion;

                            // When the newest release was published, used to hint whether a mod
                            // predates the last game update. Null stays null (column blank).
                            mod.LatestReleasedUtc = package?.Latest?.DateCreated;

                            // Keep the identity the index answered with, not the folder name we
                            // asked with: a row only offers its Thunderstore page when there is
                            // a real package behind it.
                            mod.ThunderstoreNamespace = package?.Namespace;
                            mod.ThunderstoreName = package?.Name;

                            // A package list that came back and did not hold this mod is worth
                            // saying on the row. A list that never came back is not: nobody was
                            // asked, so the row says nothing rather than something wrong.
                            mod.NotListedOnThunderstore = NotListedNow(
                                packageFound: package != null,
                                listWasRead: ListIsRecentEnoughToJudge(
                                    ThunderstoreClient.IndexFetchedUtc, DateTime.UtcNow),
                                mod.Author,
                                mod.ModName);
                        }
                        catch
                        {
                            // Leave LatestVersion null - the UI shows "-" for unknown.
                        }
                    }));

                    // The second site, only when the host asked for it. It is read after
                    // Thunderstore and never instead of it, and a Hexium failure cannot
                    // reach the scan: the client answers null rather than throwing, and
                    // this is wrapped besides.
                    await AddHexiumVersionsAsync(mods, force);

                    // Remember this realm's mod set so it's tracked per-profile and reconstructable
                    // on restore/export (kept distinct so servers never cross-contaminate).
                    RecordModManifest(CurrentProfile, mods);

                    // Steam's "last updated" stamp for this install, read once and shared by every
                    // row (it is the same game for all of them). Null when there is no manifest.
                    var gameUpdatedUtc = ReadGameLastUpdatedUtc();

                    // Whether this scan could check anything at all, and why not when it
                    // could not. Both fields are additive: a page from an older build reads
                    // the rows and the index exactly as it always did.
                    //
                    // A scan that read this server's BepInEx folder and then could not reach
                    // Thunderstore, with nothing held from an earlier read, has checked
                    // nothing for a newer version. It used to answer like a success, and the
                    // hall drew every row with a dash in the Latest column as though the site
                    // had said so. The reporter's machine could not reach the site at all,
                    // and the panel they got back was the one that says the mods have not
                    // been scanned yet.
                    var failure = ThunderstoreClient.LastFailure;
                    var blind = failure != null && ThunderstoreClient.IndexFetchedUtc == null;

                    return new
                    {
                        mods = mods.Select(m => BuildModDto(m, gameUpdatedUtc)).ToList(),
                        // When the list the rows were answered from was read, and which of
                        // the two addresses answered, so the page can say so instead of
                        // showing the time the button was pressed.
                        index = BuildIndexStateDto(indexState),
                        ok = !blind,
                        reasonId = blind ? ScanFailureId(failure) : null,
                        reasonParams = blind ? ScanFailureParams(failure) : null,
                    };
                }
                finally
                {
                    _modScanInProgress = false;
                }
            });

            // Asks Thunderstore about one package directly, for a host looking at a row and
            // knowing there is a newer build than BakaLoader is showing. One request per
            // press, and a press repeated inside the cooldown is answered without one.
            RegisterRpc("mods.checkOne", async p =>
            {
                var author = p?.Value<string>("author");
                var name = p?.Value<string>("name");

                if (!IsThunderstoreSegment(author) || !IsThunderstoreSegment(name))
                    return new { ok = false, reason = "badName", fullName = (string)null };

                var fullName = $"{author.Trim()}-{name.Trim()}";

                if (!TakeCheckOneSlot(fullName))
                    return new { ok = false, reason = "cooldown", fullName };

                var lookup = await ThunderstoreClient.LookupLiveAsync(author.Trim(), name.Trim());

                // A site that did not answer says nothing about the package, so the row is
                // left exactly as it was rather than being told the mod has been pulled.
                if (lookup == null || !lookup.Answered)
                    return new { ok = false, reason = "unreachable", fullName };

                var package = lookup.Package;

                // What is installed, read off disk rather than taken from the page, so the
                // answer about whether an update is waiting is BakaLoader's own.
                Tools.Models.InstalledMod installed = null;
                try
                {
                    var pluginsDir = GetPluginsDirectory();
                    if (!string.IsNullOrWhiteSpace(pluginsDir) && Directory.Exists(pluginsDir))
                    {
                        installed = ModScanner.ScanPlugins(pluginsDir)
                            .FirstOrDefault(m => string.Equals(m.FullName, fullName, StringComparison.OrdinalIgnoreCase));
                    }
                }
                catch (Exception e)
                {
                    AppLogger.Debug("Could not read the installed copy of {0}: {1}", fullName, e.Message);
                }

                if (package == null || string.IsNullOrWhiteSpace(package.LatestVersion))
                {
                    return new
                    {
                        ok = true,
                        found = false,
                        notListed = true,
                        fullName,
                        latestVersion = (string)null,
                        latestReleasedUtc = (string)null,
                        updateAvailable = false,
                        held = installed?.IsHexiumInstalled ?? false,
                        thunderstoreNewer = false,
                        thunderstoreNamespace = (string)null,
                        thunderstoreName = (string)null,
                        reason = (string)null,
                    };
                }

                // A copy the host took from Hexium is left alone by every update path, so
                // it never reads as having a Thunderstore update waiting. Same rule as the
                // scan, applied here rather than trusted to the page.
                var heldFromHexium = installed?.IsHexiumInstalled ?? false;
                var updateAvailable = CheckOneSaysUpdateWaiting(
                    package.LatestVersion, installed?.InstalledVersion, heldFromHexium);

                // The other half of the same rule. A held copy Thunderstore has moved past
                // is neither updatable nor the newest, and the page says so instead of
                // telling its host they have the newest.
                var thunderstoreMovedPast = CheckOneSaysThunderstoreMovedPast(
                    package.LatestVersion, installed?.InstalledVersion, heldFromHexium);

                return new
                {
                    ok = true,
                    found = true,
                    notListed = false,
                    fullName,
                    latestVersion = package.LatestVersion,
                    latestReleasedUtc = package.Latest?.DateCreated?.ToUniversalTime().ToString("o"),
                    updateAvailable,
                    // These files came from the other site, so no update is applied to
                    // them however far ahead Thunderstore has gone.
                    held = heldFromHexium,
                    thunderstoreNewer = thunderstoreMovedPast,
                    // The identity the site answered with, so a row the list had no entry
                    // for can open its page the moment this finds it.
                    thunderstoreNamespace = package.Namespace,
                    thunderstoreName = package.Name,
                    reason = (string)null,
                };
            });

            RegisterRpc("mods.updateAll", async p =>
            {
                if (_modUpdateInProgress)
                    throw new HostFacingException("mods.updateAll.alreadyRunning", "A mod update is already in progress");
                _modUpdateInProgress = true;
                try
                {
                    var pluginsDir = GetPluginsDirectory()
                        ?? throw new HostFacingException("mods.updateAll.noServerPath", "Server exe path is not configured");
                    var mods = ModScanner.ScanPlugins(pluginsDir);

                    // Push the same shape of progress the server update already streams: a bar
                    // that follows the run and a status per mod. PostEvent marshals each report
                    // to the UI thread in order (see SynchronousProgress).
                    var progress = new SynchronousProgress<ModUpdateProgress>(pr => PostEvent(
                        "mods.updateProgress",
                        new
                        {
                            index = pr.Index,
                            total = pr.Total,
                            mod = pr.Mod,
                            phase = pr.Phase,
                            fromVersion = pr.FromVersion,
                            toVersion = pr.ToVersion,
                            error = pr.Error,
                        }));

                    // One indeterminate "checking" step while the latest versions are fetched:
                    // the count of updatable mods is not known until this finishes.
                    ((IProgress<ModUpdateProgress>)progress).Report(new ModUpdateProgress { Phase = "checking" });

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
                    var results = await ModUpdateService.UpdateModsAsync(updatable, progress);

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

            // Update one mod from its row menu. Same lock and the same per-mod progress
            // events the bulk update streams, so the row shows the update in place.
            RegisterRpc("mods.update", async p =>
            {
                if (_modUpdateInProgress)
                    throw new HostFacingException("mods.update.alreadyRunning", "A mod update is already in progress");
                _modUpdateInProgress = true;
                try
                {
                    var mod = FindInstalledMod(p);

                    var progress = new SynchronousProgress<ModUpdateProgress>(pr => PostEvent(
                        "mods.updateProgress",
                        new
                        {
                            index = pr.Index,
                            total = pr.Total,
                            mod = pr.Mod,
                            phase = pr.Phase,
                            fromVersion = pr.FromVersion,
                            toVersion = pr.ToVersion,
                            error = pr.Error,
                        }));

                    var results = await ModUpdateService.UpdateModsAsync(new[] { mod }, progress);
                    RecordModUpdates(ActiveProfileName, results);

                    var r = results[0];
                    return new
                    {
                        mod = r.Mod.FullName,
                        r.Updated,
                        r.FromVersion,
                        r.ToVersion,
                        r.Error,
                    };
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

            // The installed mods that depend on a given one, directly or through a chain, so
            // removing a mod can offer to take its now-orphaned dependents with it. All local:
            // it reads the manifests already on disk, no Thunderstore call. The list is ordered
            // dependents-first so removing in that order never orphans a still-installed mod
            // midway, and a visited set keeps a cycle or a shared core from looping.
            RegisterRpc("mods.dependents", p =>
            {
                var result = new List<object>();
                var target = p?.Value<string>("fullName");
                var pluginsDir = GetPluginsDirectory();
                if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(pluginsDir))
                    return Task.FromResult<object>(result);

                var mods = ModScanner.ScanPlugins(pluginsDir);
                foreach (var mod in Tools.ModScanner.FindDependents(mods, target))
                {
                    result.Add(new
                    {
                        fullName = mod.FullName,
                        displayName = string.IsNullOrWhiteSpace(mod.ModName) ? mod.FullName : mod.ModName,
                    });
                }

                return Task.FromResult<object>(result);
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
                // The refusal carries a REASON beside the sentence now. A page could only ever
                // tell "BepInEx is missing" from "Thunderstore is down" by matching English
                // prose, which is not a thing any page should be asked to do and stops working
                // the moment the sentence is translated. The sentence is unchanged, so a page
                // that reads only Error is exactly as it was.
                object FailDto(string error, string reason = null,
                    IReadOnlyDictionary<string, object> errorParams = null) => new
                {
                    Installed = false,
                    Replaced = false,
                    Owner = (string)null,
                    Name = (string)null,
                    Version = (string)null,
                    Error = error,
                    Reason = reason,
                    // The named values the reason's own sentence interpolates, so a refusal
                    // that came back through this road can be worded out of the catalog
                    // exactly as the same refusal is on the direct call.
                    ErrorParams = errorParams,
                    source = (string)null,
                };

                if (_modUpdateInProgress)
                    return FailDto("A mod update is already in progress. Try again in a moment.", "busy");

                // A hexium.gg link is a different site with a different answer: it never
                // installs from a paste. It is resolved, and what it resolves to is handed
                // back for the host to read and accept or turn down.
                var pasted = p.Value<string>("url");
                if (Tools.HexiumUrlParser.LooksLikeHexiumLink(pasted))
                    return await PrepareHexiumInstallAsync(pasted, null, null, null);

                if (!ThunderstoreUrlParser.TryParse(pasted, out var reference, out var parseError))
                    return FailDto(parseError, "badUrl");

                var pluginsDir = GetPluginsDirectory();
                if (string.IsNullOrWhiteSpace(pluginsDir))
                    return FailDto("BepInEx plugins folder not found. Set a valid server .exe path first.",
                        "noServerPath");

                var bepPrefs = UserPrefsProvider.LoadPreferences();
                var bepBaseExe = GetCanonicalBaseServerExe();
                var bepStatus = BepInEx.Status(bepBaseExe, pluginsDir, KnownInstalls());

                // The loader pack is not a mod and cannot be installed as one: its files
                // belong beside the server executable, not three levels down under plugins.
                // While BakaLoader is looking after it there is nothing for the host to do,
                // and the answer says where the switch is instead of failing silently.
                if (IsBepInExPackReference(reference.Owner, reference.Name))
                {
                    if (Tools.BepInExConsent.Effective(
                            bepPrefs.BepInExMaintained, bepPrefs.BepInExMaintenanceAsked))
                        return FailDto("BepInEx is already looked after by BakaLoader.", "alreadyMaintained");

                    try
                    {
                        // A link that NAMES a version is honoured, because the offer dialog's
                        // edited address already is and the two roads have to agree: pasting
                        // .../BepInExPack_Valheim/5.4.2100/ used to fetch the latest pack and
                        // say nothing about it. The address is BUILT from the package identity
                        // rather than taken from the paste, so a pinned package still cannot be
                        // talked into fetching from somewhere else. No version in the link and
                        // the pinned pack is resolved fresh, exactly as the row's Install does.
                        var pinned = string.IsNullOrWhiteSpace(reference.Version)
                            ? null
                            : Tools.BepInExService.ConstructedDownloadUrl(
                                reference.Owner, reference.Name, reference.Version);

                        await WriteBepInExAsync(pinned, update: bepStatus.Installed);
                        return new
                        {
                            Installed = true,
                            Replaced = bepStatus.Installed,
                            Owner = reference.Owner,
                            Name = reference.Name,
                            Version = reference.Version,
                            Error = (string)null,
                            Reason = "bepinex",
                            source = (string)null,
                        };
                    }
                    catch (Exception bepError)
                    {
                        return FailDto(bepError.Message, HostFacingException.IdOf(bepError) ?? "bepinex",
                            HostFacingException.ParamsOf(bepError));
                    }
                }

                if (!bepStatus.Installed)
                {
                    // Maintained OFF means BakaLoader never writes BepInEx on its own. The
                    // reason is what lets the page offer the install as a question.
                    if (!Tools.BepInExConsent.Effective(
                            bepPrefs.BepInExMaintained, bepPrefs.BepInExMaintenanceAsked))
                        return FailDto(
                            "BepInEx is not installed on this server, so there is nothing for a mod to load under.",
                            "noBepInEx");

                    try
                    {
                        await WriteBepInExAsync(null, update: false);
                    }
                    catch (Exception bepError)
                    {
                        return FailDto(bepError.Message, HostFacingException.IdOf(bepError) ?? "noBepInEx",
                            HostFacingException.ParamsOf(bepError));
                    }
                }

                // BepInEx is here and the plugins folder is not: the pack does not ship one,
                // so this is the ordinary state of a fresh install rather than a fault. It is
                // created and the add carries on.
                if (!Directory.Exists(pluginsDir))
                {
                    try { Directory.CreateDirectory(pluginsDir); }
                    catch (Exception folderError) { return FailDto(folderError.Message, "noPlugins"); }
                }

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
                        // One shape for the answer whether it worked or not, so a page reading
                        // Reason never has to check whether the key is there.
                        Reason = (string)null,
                        // Which site the files came from, carried through so the page never
                        // has to guess what it just installed. Additive: a page that does not
                        // read it is unaffected, and a Thunderstore install says so plainly.
                        source = result.Source,
                    };
                }
                finally
                {
                    _modUpdateInProgress = false;
                }
            });

            // Works out what installing a named package from Hexium would actually do,
            // and hands that back for the host to read. It downloads nothing. The answer
            // carries a one-shot token, and mods.installFromHexium refuses without it.
            RegisterRpc("mods.hexiumPrepare", async p =>
                await PrepareHexiumInstallAsync(
                    null,
                    p.Value<string>("owner"),
                    p.Value<string>("name"),
                    p.Value<string>("version")));

            // Fetches and installs one Hexium build. Only reachable with the token the
            // dialog above handed out, for the exact package and version it named.
            RegisterRpc("mods.installFromHexium", async p =>
            {
                object FailDto(string error, string reason = null) => new
                {
                    Installed = false,
                    Replaced = false,
                    Owner = p.Value<string>("owner"),
                    Name = p.Value<string>("name"),
                    Version = p.Value<string>("version"),
                    Source = Tools.ModSourceMarkerFile.HexiumSource,
                    Dependencies = Array.Empty<string>(),
                    Reason = reason,
                    Error = error,
                };

                if (!UserPrefsProvider.LoadPreferences().UseHexiumSource)
                    return FailDto("Turn on Also check Hexium in Upkeep to install from Hexium.", "sourceOff");

                if (_modUpdateInProgress)
                    return FailDto("A mod update is already in progress. Try again in a moment.", "busy");

                var owner = p.Value<string>("owner");
                var name = p.Value<string>("name");
                var version = p.Value<string>("version");

                // Asked BEFORE the acceptance is spent. A token is good once, so a server
                // path that is wrong or not set yet used to burn the host's answer on a
                // refusal they could do nothing about, and the dialog had to be opened and
                // read again. Nothing here fetches or writes a byte, so checking first
                // costs nothing and the acceptance survives to be used once it can be.
                var pluginsDir = GetPluginsDirectory();
                if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
                    return FailDto("BepInEx plugins folder not found. Set a valid server .exe path first.", "noPlugins");

                if (!HexiumConsent.Take(p.Value<string>("token"), owner, name, version))
                    return FailDto("That install was not accepted, or the question has gone stale. Open it again.", "noConsent");

                // Resolved again from the index rather than from anything the page sent,
                // so the address that is fetched is the site's own and this build's.
                var lookup = await HexiumClient.LookupAsync($"{owner}-{name}");
                if (!lookup.IndexAvailable) return FailDto(lookup.Error, "unreachable");
                if (!lookup.Found) return FailDto($"Hexium has no package named {owner}/{name}.", "notFound");

                var chosen = lookup.Package.Find(version);
                if (chosen == null)
                    return FailDto($"Hexium does not offer {owner}/{name} {version}.", "noVersion");

                // What was there before, read before anything is replaced, so the journal
                // can say what this swapped out rather than guessing afterwards.
                var previousVersion = ModScanner.ScanPlugins(pluginsDir)
                    .FirstOrDefault(m => m.FullName == $"{lookup.Package.Owner}-{lookup.Package.Name}")?.InstalledVersion;

                _modUpdateInProgress = true;
                try
                {
                    var result = await ModUpdateService.InstallFromHexiumAsync(new HexiumInstallPlan
                    {
                        Owner = lookup.Package.Owner,
                        Name = lookup.Package.Name,
                        Version = chosen.VersionNumber,
                        DownloadUrl = chosen.DownloadUrl,
                        FileSize = chosen.FileSize,
                        Dependencies = (chosen.Dependencies ?? new List<string>()).ToArray(),
                    }, pluginsDir);

                    if (result.Installed)
                    {
                        Analytics.Record(new AnalyticsEvent
                        {
                            Kind = result.Replaced ? "modup" : "modin",
                            Server = ActiveProfileName,
                            Mod = $"{result.Owner}-{result.Name}",
                            FromVersion = result.Replaced ? previousVersion : null,
                            ToVersion = result.Version,
                            Source = Tools.ModSourceMarkerFile.HexiumSource,
                        });
                    }

                    return new
                    {
                        result.Installed,
                        result.Replaced,
                        result.Owner,
                        result.Name,
                        result.Version,
                        result.Source,
                        result.Dependencies,
                        Reason = (string)null,
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

                // The folder has to be THERE, not merely named. A profile whose exe path is
                // set but which never had BepInEx installed answered "nothing is missing"
                // here, because the checker reads an absent folder as an empty one, and the
                // page then left the console typing box enabled on a server that cannot run a
                // single command. caps.install has always checked this; caps.get never did.
                if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
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
                        "BepInEx plugins folder not found. Set a valid server .exe path first.");

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


            // --- BepInEx (the framework every mod loads under) ---
            // BepInEx is ONE thing per base install, never one per profile: an isolated
            // install shares BepInEx/core and BepInEx/patchers with the base through
            // directory junctions and hard-links the loader files beside the executable. So
            // every answer here is about the base install the active profile runs from, and
            // every write reaches every server on it. That is why the refusal below is a
            // refusal and not a warning.
            RegisterRpc("bepinex.status", p =>
            {
                // A write that did not finish is finished the moment anybody asks what the
                // state of this install is, which is the first thing the Mods page does.
                HealInterruptedBepInExWrite(GetCanonicalBaseServerExe(), LiveInstalls());
                return Task.FromResult<object>(BuildBepInExDto());
            });

            RegisterRpc("bepinex.install", async p =>
            {
                var url = p.Value<string>("url");
                return await WriteBepInExAsync(url, update: false, options: BepInExOptionsFrom(p));
            });

            RegisterRpc("bepinex.update", async p =>
                await WriteBepInExAsync(null, update: true, options: BepInExOptionsFrom(p)));

            // Puts the newest backup back, or the one a stamp names. Offered on the row when
            // there is a backup and the core is missing or will not load, which is the state an
            // antivirus or a half-finished write leaves behind.
            RegisterRpc("bepinex.restore", async p =>
            {
                if (!TryBeginBepInExWrite()) throw BepInExBusy();

                Tools.BepInExInstallResult restored;
                try
                {
                    var progress = new SynchronousProgress<Tools.BepInExProgress>(pr =>
                        PostEvent("bepinex.progress",
                            new { phase = pr.Phase, percent = pr.Percent, version = pr.Version }));

                    restored = await BepInEx.RestoreAsync(GetCanonicalBaseServerExe(), LiveInstalls(),
                        p.Value<string>("stamp"), progress);
                }
                finally
                {
                    EndBepInExWrite();
                    PostBepInExChanged();
                }

                // Putting a backup back is the host dealing with whatever the row was saying,
                // so an outcome an earlier window left standing about this install stops.
                _bepInExLastUnattended = null;

                return BuildBepInExDto(restored);
            });

            // The page has drawn what the last unattended write came to, so it stops travelling
            // on every answer. It is its own call rather than a flag on the status read because
            // a read happens on every page paint, and a read that cleared it would take the
            // notice away before anybody had looked at it.
            RegisterRpc("bepinex.noticeSeen", p =>
            {
                _bepInExLastUnattended = null;
                return Task.FromResult<object>(BuildBepInExDto());
            });

            // Only ever the mis-placed folder under plugins, never BepInEx itself: removing a
            // loader out from under an install is not something a button should be able to do.
            RegisterRpc("bepinex.remove", async p =>
            {
                // Through the same one slot as the other three writers: this deletes a folder
                // under plugins while an install may be junctioning and hard-linking its way
                // through the very same tree.
                if (!TryBeginBepInExWrite()) throw BepInExBusy();

                try
                {
                    var pluginsDir = GetPluginsDirectory();
                    await BepInEx.RemoveWrongLocationAsync(
                        pluginsDir, LiveInstalls(), GetCanonicalBaseServerExe());
                }
                finally
                {
                    EndBepInExWrite();
                    PostBepInExChanged();
                }

                return BuildBepInExDto();
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
                    // Steam's own downloads page, where a waiting server update is applied.
                    "steam-downloads" => "steam://open/downloads",
                    // The release the app update check itself reads, so the notes the host
                    // opens are always the notes for the version they were just offered.
                    "releases" => ReleasesUrl,
                    // What BepInEx is, what BakaLoader writes, and what every server on
                    // one install shares. Offered by the row that says it did not load.
                    "bepinex-wiki" => BepInExWikiUrl,
                    _ => throw new ArgumentException($"Unknown shell.openUrl target: {target}"),
                };

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                return Task.FromResult<object>(true);
            });

            // The notes for the exact release the update check found, rather than whatever is
            // newest on the repository today. The page sends nothing at all: the address comes
            // off the found release and is only used when it really is a release page on this
            // repository, so what the host reads is the version they were just offered.
            RegisterRpc("shell.openAppRelease", p =>
            {
                var url = AppReleaseNotesUrl(SoftwareUpdates?.LatestAvailable?.NotesUrl);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                return Task.FromResult<object>(true);
            });

            // Opens one mod's own page on Thunderstore, which is what the right-click on a mod
            // row offers. The page sends the matched package identity and never a URL: the
            // address is built here from two validated segments, so nothing the page says can
            // send the shell anywhere else.
            // Opens a mod's own page on Hexium. The address is built here from the two
            // halves of the package identity, never taken from the page, so this can only
            // ever land on Hexium.
            RegisterRpc("shell.openHexium", p =>
            {
                // Behind the host's own switch, like the other two calls that reach the second
                // site. With it off nothing about that site is asked for, and that includes
                // opening one of its pages in the host's browser.
                if (!UserPrefsProvider.LoadPreferences().UseHexiumSource)
                    throw new InvalidOperationException("Turn on Also check Hexium in Upkeep to open Hexium pages.");

                var url = Tools.HexiumUrlParser.PageUrl(p.Value<string>("owner"), p.Value<string>("name"))
                    ?? throw new ArgumentException("That mod has no Hexium page to open.");

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                return Task.FromResult<object>(true);
            });

            RegisterRpc("shell.openThunderstore", p =>
            {
                var url = ThunderstorePageUrl(p.Value<string>("namespace"), p.Value<string>("name"))
                    ?? throw new ArgumentException("That mod has no Thunderstore page to open.");

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                return Task.FromResult<object>(true);
            });

            // --- The two Directories boxes: pick a path, and look at one ---
            // Windows' own file picker, filtered to the one file name a dedicated server is
            // ever called. Like shell.open, the page may only send a closed keyword; the
            // dialog itself is what produces a path, so nothing the page says can point this
            // at anything. Nothing is saved: the answer goes into the box and waits for
            // Save Config like every other field on the hall.
            RegisterRpc("shell.pickFile", p =>
            {
                var kind = p.Value<string>("kind");
                if (!string.Equals(kind, PathCheck.KindExe, StringComparison.Ordinal))
                    throw new ArgumentException($"Unknown shell.pickFile kind: {kind}");

                return Task.FromResult(RunOnUiThread(() =>
                {
                    using var dialog = new OpenFileDialog
                    {
                        // No title and no filter label: both would be English in a window
                        // the catalog cannot reach. The file name IS the label, and it is a
                        // name rather than a word, so it reads the same in every language.
                        Filter = PathCheck.ServerExeName + "|" + PathCheck.ServerExeName,
                        CheckFileExists = true,
                        Multiselect = false,
                        InitialDirectory = PickerStartFolder(PathCheck.KindExe),
                    };
                    return PickReply(dialog.ShowDialog(this) == DialogResult.OK, dialog.FileName);
                }));
            });

            // The same for the save folder. A folder that is not there yet can be made from
            // inside the dialog, which is the case the inline note calls "will be created".
            RegisterRpc("shell.pickFolder", p =>
            {
                var kind = p.Value<string>("kind");
                if (!string.Equals(kind, PathCheck.KindDir, StringComparison.Ordinal))
                    throw new ArgumentException($"Unknown shell.pickFolder kind: {kind}");

                return Task.FromResult(RunOnUiThread(() =>
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        ShowNewFolderButton = true,
                        SelectedPath = PickerStartFolder(PathCheck.KindDir),
                    };
                    return PickReply(dialog.ShowDialog(this) == DialogResult.OK, dialog.SelectedPath);
                }));
            });

            // What is at a path, while the host is still typing it. This never refuses
            // anything and never blocks a save: it answers, the page writes one sentence
            // under the box, and Save Config behaves exactly as it did before.
            //
            // AND IT NEVER BLOCKS THE WINDOW. An RPC handler runs on the window's own
            // thread, because that is where WebView2 raises the message, so everything a
            // handler does before its first await is done with the window's painting
            // stopped. What this one does is ask the disk four questions about a path the
            // host is in the middle of typing, and two of those questions have no upper
            // bound on how long they take: a share on a machine that is not answering
            // holds Directory.Exists until SMB gives up, and a drive that has spun down
            // holds it until the platters are back. The page asks again every time the
            // typing pauses, so a host who pasted an unreachable path met a window that
            // had stopped repainting, once per keystroke.
            //
            // So the disk work goes to a worker and this thread waits on it with a
            // budget. Running out is an ANSWER rather than a throw, because a sentence
            // under the box is what every other outcome here produces and a refusal would
            // be the one thing this whole surface was written not to do. The page's own
            // sequence guard covers the other half: the worker is left to finish in its
            // own time, and an answer for a keystroke that is no longer the newest one is
            // dropped where it lands rather than painted over a newer one.
            RegisterRpc("paths.check", async p =>
            {
                var kind = p.Value<string>("kind");
                if (!string.Equals(kind, PathCheck.KindExe, StringComparison.Ordinal)
                    && !string.Equals(kind, PathCheck.KindDir, StringComparison.Ordinal))
                    throw new ArgumentException($"Unknown paths.check kind: {kind}");

                // Filling in %VARIABLES% is string work and stays here. Only the questions
                // that reach a disk go to the worker.
                var expanded = PathCheck.Expand(p.Value<string>("path") ?? "");
                var looking = Task.Run(() => PathCheck.Look(kind, expanded));
                var finished = await Task.WhenAny(looking, Task.Delay(PathCheckBudget));

                PathCheckAnswer answer;
                if (finished != looking)
                {
                    answer = PathCheck.CouldNotBeChecked(expanded);
                    // The worker is left to finish in its own time, and nothing awaits it
                    // after this, so anything it raises on the way out is an unobserved
                    // task exception sitting there until a collection notices it. Read it
                    // and write it down instead: this is the one arm where a path that
                    // really is unreachable ends up, so it is the one worth a log line.
                    _ = looking.ContinueWith(
                        t => Logger.Warning(
                            t.Exception,
                            "Looking at {path} for the Directories box failed after it was given up on",
                            expanded),
                        TaskContinuationOptions.OnlyOnFaulted);
                }
                else
                {
                    // Look catches everything a path a host can type is able to raise, so
                    // a faulted task here is something nobody has seen. It still gets an
                    // answer rather than a refusal: the one rule this surface has is that
                    // it says a sentence and lets the host carry on.
                    try { answer = PathCheck.Judge(kind, expanded, await looking); }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "Looking at {path} for the Directories box failed", expanded);
                        answer = PathCheck.CouldNotBeChecked(expanded);
                    }
                }

                return new
                {
                    expanded = answer.Expanded,
                    exists = answer.Exists,
                    looksRight = answer.LooksRight,
                    writable = answer.Writable,
                    problemId = answer.ProblemId,
                };
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
                var exe = p.Value<string>("serverExePath");
                var save = p.Value<string>("saveDataFolderPath");

                // Whole document write, so it goes through the gate: see the note on Mutate.
                UserPrefsProvider.Mutate(prefs =>
                {
                    if (!string.IsNullOrWhiteSpace(exe)) prefs.ServerExePath = exe.Trim();
                    if (!string.IsNullOrWhiteSpace(save)) prefs.SaveDataFolderPath = save.Trim();
                    prefs.SetupCompleted = true;
                });

                return Task.FromResult<object>(BuildSetupStatus());
            });

            // Resets first-time setup: paths back to defaults, SetupCompleted cleared, and any
            // per-profile path overrides removed so the wizard's next answers actually apply.
            // Server files on disk are never touched.
            RegisterRpc("setup.reset", p =>
            {
                UserPrefsProvider.Mutate(prefs =>
                {
                    prefs.ServerExePath = Resources.DefaultServerPath;
                    prefs.SaveDataFolderPath = Resources.DefaultValheimSaveFolder;
                    prefs.SetupCompleted = false;
                });

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

        /// <summary>
        /// The release page behind the update dialog's notes button when the check did not hand
        /// back an address of its own. Same repository the update check queries, so the two can
        /// never point at different notes.
        /// </summary>
        private const string ReleasesUrl = "https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/latest";

        /// <summary>The page that explains BepInEx, the marker file and what is shared.</summary>
        private const string BepInExWikiUrl = "https://github.com/RyanDMcAfee/ValheimBakaLoader/wiki/BepInEx";

        /// <summary>
        /// The only addresses the notes button may open. GitHub hands back the release page for
        /// whatever it found, and that string is what this opens, so the check being pointed
        /// somewhere else one day cannot turn into the app opening somewhere else.
        /// </summary>
        private const string ReleaseNotesPrefix = "https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/";

        #endregion

        #region DTO builders & helpers

        /// <summary>
        /// Collects the world reader's own explanation for the length of one read. The reader
        /// posts its refusals to a single static sink, which is wired into the app log where no
        /// host ever looks. AsyncLocal so the lines follow this read onto its worker thread and
        /// a read somebody else starts meanwhile cannot land in them.
        /// </summary>
        private static readonly AsyncLocal<List<string>> AtlasReadDiagnostics = new();

        /// <summary>
        /// Reads a world save and keeps whatever the reader said about it. Same read as
        /// <see cref="ReadWorldSave"/>; the sink is borrowed for the length of the call and
        /// every line still reaches whoever had it, so the app log loses nothing.
        /// <para>
        /// atlas.worldInfo is single flight, so the swap here never nests.
        /// </para>
        /// </summary>
        public static AtlasEngine.WorldDbInfo ReadWorldSaveWithDiagnostics(
            string pathOrDirectory, out List<string> diagnostics)
        {
            var collected = new List<string>();
            diagnostics = collected;

            var previous = AtlasEngine.WorldDbReader.DiagnosticSink;
            Action<string> mine = message =>
            {
                if (!string.IsNullOrWhiteSpace(message)) AtlasReadDiagnostics.Value?.Add(message);
                previous?.Invoke(message);
            };
            AtlasEngine.WorldDbReader.DiagnosticSink = mine;
            AtlasReadDiagnostics.Value = collected;

            try
            {
                return ReadWorldSave(pathOrDirectory);
            }
            finally
            {
                AtlasReadDiagnostics.Value = null;

                // Only hand the sink back when it is still the one this call put there. If
                // something else has taken it since, restoring would quietly cut that owner out.
                if (ReferenceEquals(AtlasEngine.WorldDbReader.DiagnosticSink, mine))
                    AtlasEngine.WorldDbReader.DiagnosticSink = previous;
            }
        }

        /// <summary>
        /// What atlas.worldInfo answers when there is no readable world: whether a save is on
        /// disk at all, so "wait for the first save" is not offered as advice about one that is
        /// sitting right there, and the reader's own sentence for why it would not open.
        /// </summary>
        public static object WorldInfoWithNoSave(string world, bool saveExists, IReadOnlyList<string> diagnostics)
            => new
            {
                world,
                hasDb = false,
                saveExists,
                reason = diagnostics?.LastOrDefault(line => !string.IsNullOrWhiteSpace(line)),
            };

        private object BuildServerState() => BuildServerState(ActiveSession);

        private object BuildServerState(ServerSession session)
        {
            var server = session.Server;

            // An update rewriting this install is not a state the server itself can see: the exe
            // and the assemblies beside it are mid write while Status still reads Stopped. Folding
            // it into canStart here is what greys the Start button out on a page that has not seen
            // the progress events, and the RPC refuses independently for a page that ignores both.
            var updating = ServerUpdateExe.TryGetValue(session.ProfileName, out var updatingExe)
                && IsServerUpdateRunning(updatingExe);

            // Settings the host saved while this world was up. Null unless there really is a
            // difference, so the two keys below travel together: the fingerprint is what a
            // dismissed row is keyed on, and a later change gives a new one.
            var pending = SavedOptionsIfRestartPending(session);

            return new
            {
                profile = session.ProfileName,
                status = server.Status.ToString(),
                updating,
                canStart = server.CanStart && !updating,
                canStop = server.CanStop,
                canRestart = server.CanRestart,
                countdownActive = server.IsCountdownActive,
                adopted = server.IsAdopted,
                launchPending = server.LaunchInProgress,
                // Read from the server's own banner; null until it has printed one.
                gameVersion = server.GameVersion,
                networkVersion = server.NetworkVersion,
                // A launch the guard held, so the banner survives a page reload.
                launchHold = LaunchHolds.TryGetValue(session.ProfileName, out var hold) ? hold : null,
                // True while the live server is running settings the host has since changed.
                // They go in at the next restart, which is the one thing the row offers.
                restartPending = pending != null,
                restartPendingSig = pending == null ? null : ValheimServerOptions.RelaunchSignature(pending),
                // Companion plugins that could not be installed, so the interface can say the
                // feature is off and why instead of leaving it quietly missing. Empty normally.
                // Scoped to this session's profile: with two realms up, the other realm's
                // plugin trouble must never appear on this one's bar.
                pluginFailures = BuildPluginFailureDtos(session.ProfileName),
                // The two BepInEx facts that are STILL TRUE and still want an answer: a pack
                // update that could not be written because a server on this install was up,
                // and a start where the preloader never wrote its log. Null when the question
                // could not be asked at all, which a page reads as "say nothing".
                bepinex = BuildBepInExState(session),
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
        /// Best-effort in-game day from a world database header: int32 worldVersion followed by
        /// a double netTime in seconds, one day being 1800 seconds (EnvMan). The first twelve
        /// bytes mean the same thing in the pre-1.0 ".db" and the 1.0 "_main.{N}.db2", so the
        /// caller just hands over whichever the world actually has.
        /// </summary>
        private static long? TryReadWorldDay(string dbPath) => WorldStore.TryReadWorldDay(dbPath);

        /// <summary>
        /// Parses a world save for the Atlas hall. A pre-1.0 world is handed its ".db" path; a
        /// 1.0 world is handed its DIRECTORY, because its ZDOs live in sibling ".chunk" files
        /// rather than inside the database.
        ///
        /// ONE-LINE SWAP: once Atlas ships WorldDbReader.TryReadAny(string pathOrDirectory),
        /// this body becomes `return AtlasEngine.WorldDbReader.TryReadAny(pathOrDirectory);`.
        /// Until then a 1.0 world reads as "no save data" instead of mis-parsing a v41 header
        /// with the pre-1.0 ZDO layout, which would draw a map full of nonsense.
        /// </summary>
        private static AtlasEngine.WorldDbInfo ReadWorldSave(string pathOrDirectory)
        {
            if (string.IsNullOrWhiteSpace(pathOrDirectory)) return null;
            // Legacy .db path or a 1.0 world directory; the reader dispatches on which it is given.
            return AtlasEngine.WorldDbReader.TryReadAny(pathOrDirectory);
        }

        /// <summary>
        /// Validates a Barrow backup reference ({world, folder, sub, file}) and resolves it to a
        /// real on-disk layer. Hard rules: no path separators, "..", or any character illegal in
        /// a file name; folder must be one of the known save folders; sub must be
        /// worlds_local/worlds; and (when requireBackupShape) the reference must resolve to a
        /// layer the world ACTUALLY owns - a "{world}_backup_*.fwl"/".fwl.old" file or a
        /// "{world}_backup_*" DIRECTORY. The live world, pair or 1.0 directory, is never
        /// backup-shaped, so it can never be targeted through here.
        /// <para>
        /// A layer that has lost the world file it would be restored FROM resolves only when
        /// <paramref name="allowDamaged"/> is set, which only the delete path does. The legacy
        /// restore below copies the layer's own file over the live world's ".fwl", so a lone
        /// ".db" taken for one would write database bytes into the world's metadata file.
        /// </para>
        /// </summary>
        private (string SaveFolder, string Sub, string Dir, string World, string File, WorldBackupInfo Layer)
            ValidateBackupRef(JObject p, bool requireBackupShape, bool allowDamaged = false)
        {
            var world = p.Value<string>("world");
            var folder = p.Value<string>("folder");
            var sub = p.Value<string>("sub");
            var file = p.Value<string>("file");

            if (string.IsNullOrWhiteSpace(world)) throw new ArgumentException("world is required");
            if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("file is required");

            // Both pieces must be plain names: anything that could climb out of the worlds
            // folder (separators, "..", a drive colon, a wildcard) is refused outright.
            foreach (var piece in new[] { world, file })
            {
                if (!WorldStore.IsSafeReferenceToken(piece))
                    throw new ArgumentException("Invalid characters in backup reference.");
            }

            if (sub != "worlds_local" && sub != "worlds")
                throw new ArgumentException("Invalid save subfolder.");

            if (string.IsNullOrWhiteSpace(folder) || !KnownSaveFolders().Any(k => SameFolder(k, folder)))
                throw new ArgumentException("Unknown save folder.");

            var saveFolder = Path.GetFullPath(folder);
            var dir = Path.Combine(saveFolder, sub);
            if (!Directory.Exists(dir)) throw new ArgumentException("Save subfolder does not exist.");

            WorldBackupInfo layer = null;
            if (requireBackupShape)
            {
                layer = ResolveBackupLayerOrRefuse(saveFolder, sub, world, file, allowDamaged);
            }
            else
            {
                if (!File.Exists(Path.Combine(dir, file)) && !Directory.Exists(Path.Combine(dir, file)))
                    throw new ArgumentException("That backup no longer exists.");

                // The doc comment above promises the live world can never be reached through
                // here. Existence alone does not keep that promise: "{world}.fwl", "{world}.db"
                // and a 1.0 "{world}" directory all exist and all pass the token check. Only a
                // backup-shaped name is a backup, in this mode too.
                if (!IsUsableBackupReference(file, world))
                    throw new ArgumentException("Not a backup layer of that world.");
            }

            return (saveFolder, sub, dir, world, file, layer);
        }

        /// <summary>
        /// The layer a Barrow reference names, or a refusal that says which of the four things
        /// went wrong: the name was never a layer of this world, the layer has since gone, the
        /// layer is still there but has lost the world file it would be restored FROM, or the
        /// reference resolved outside the world's own subfolder. A damaged layer is answered
        /// only when <paramref name="allowDamaged"/> is set, which only the delete path does:
        /// the legacy restore copies the layer's own file over the live world's ".fwl", so a
        /// lone ".db" taken for one would write database bytes into the world's metadata file.
        /// </summary>
        public static WorldBackupInfo ResolveBackupLayerOrRefuse(
            string saveFolder, string sub, string world, string file, bool allowDamaged)
        {
            var layer = WorldStore.ResolveBackupLayer(saveFolder, sub, world, file, allowDamaged);
            if (layer != null) return layer;

            // A damaged layer is sitting right there taking up as much room as the world, so
            // "no longer exists" would send the host looking for a file that is not missing.
            // Name it and say what it lost instead.
            var damaged = allowDamaged
                ? null
                : WorldStore.ResolveBackupLayer(saveFolder, sub, world, file, allowDamaged: true);
            if (damaged != null)
                throw new ArgumentException(
                    $"'{damaged.Name}' has lost the world file that went with it, so there is nothing to unearth from it.");

            // Tell the two remaining failures apart: a name that was never a layer of this
            // world is a bad reference; one that simply vanished is a stale UI.
            throw new ArgumentException(WorldStore.IsBackupShapedFor(file, world)
                ? "That backup no longer exists."
                : "Not a backup layer of that world.");
        }

        /// <summary>
        /// Unearths one backup layer over the world it belongs to, and answers the safety copy
        /// it laid down first plus whether the world came back with a database beside it.
        /// <para>
        /// The world does not have to exist. A set whose world was deleted or renamed still
        /// names it, and restoring one of its layers is how the world comes back: there is
        /// nothing to overwrite, so no safety copy is taken (the answer's snapshot is null) and
        /// the layer is simply written out under the live names in the same subfolder.
        /// </para>
        /// <para>
        /// Everything here writes to disk, so the caller runs it off the UI thread. The caller
        /// also owns the checks this cannot make: that the reference is a real layer of that
        /// world (<see cref="ValidateBackupRef"/>) and that no server is running the world.
        /// </para>
        /// </summary>
        public static (string Snapshot, bool RestoredDb) RestoreBackupLayer(
            string saveFolder, string sub, string world, WorldBackupInfo layer)
        {
            var dir = Path.Combine(saveFolder, sub);

            // A 1.0 layer is only safe to copy once its generation is committed: the .ok
            // marker is written last, so a layer without it is a half-written save.
            if (layer.IsDirectory && !layer.IsCommitted)
                throw new InvalidOperationException(
                    $"'{layer.Name}' holds no finished save yet, so there is nothing to unearth from it.");

            // Scoped to the layer's OWN subfolder. A name that exists in both worlds_local
            // and worlds would otherwise resolve to whichever one comes first, and the
            // safety copy would be taken of one world while the other is overwritten.
            var live = WorldStore.FindIn(saveFolder, sub, world);
            var liveFwl = Path.Combine(dir, world + ".fwl");
            var liveDb = Path.Combine(dir, world + ".db");
            var liveDir = Path.Combine(dir, world);

            // Prove it before anything is written: the world being replaced has to be the
            // one that sits beside the layer being unearthed.
            if (live != null)
            {
                var liveParent = live.Format == WorldFormat.Chunked
                    ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(live.Folder))
                    : live.Folder;

                if (!SameFolder(liveParent, dir))
                    throw new InvalidOperationException(
                        $"'{world}' resolved to a world in a different save subfolder, so nothing was restored.");
            }

            // Safety layer first: snapshot whatever is live right now, in its own format, so
            // every unearthing stays reversible from the Barrow. A world that is gone has
            // nothing to copy aside, and that is the one case where snapshot comes back null.
            // Local time, like every other backup stamp: the game writes its own restore
            // layers with DateTime.Now, PreUpdateLayerName takes a local time on purpose,
            // and the Barrow reads all fourteen digits back as a wall clock. A UTC stamp
            // here would show and sort by the wrong hour next to all of them.
            string snapshot = live == null ? null : RestoreLayerName(world);

            if (live != null)
            {
                if (live.Format == WorldFormat.Chunked)
                {
                    WorldStore.CopyDirectory(live.Folder, Path.Combine(dir, snapshot));
                }
                else
                {
                    File.Copy(liveFwl, Path.Combine(dir, snapshot + ".fwl"), overwrite: true);
                    if (File.Exists(liveDb)) File.Copy(liveDb, Path.Combine(dir, snapshot + ".db"), overwrite: true);
                }
            }

            if (layer.IsDirectory)
            {
                // Chunked layer: the live directory keeps its place, its contents are
                // replaced wholesale so no stale chunk file from the newer save survives.
                if (live != null && live.Format == WorldFormat.Legacy)
                {
                    foreach (var ext in new[] { ".fwl", ".db" })
                    {
                        var stale = Path.Combine(dir, world + ext);
                        if (File.Exists(stale)) File.Delete(stale);
                    }
                }
                WorldStore.ReplaceDirectoryContents(layer.Path, liveDir, snapshot);
                return (snapshot, true);
            }

            // Legacy layer. Onto a 1.0 world this means going back to the pre-conversion
            // files: the live directory goes away and the pair returns to the worlds root,
            // where the game converts it again on its next save.
            if (live != null && live.Format == WorldFormat.Chunked && Directory.Exists(live.Folder))
                Directory.Delete(live.Folder, recursive: true);

            File.Copy(layer.Path, liveFwl, overwrite: true);
            if (layer.HasDb) File.Copy(layer.DbPath, liveDb, overwrite: true);
            return (snapshot, layer.HasDb);
        }

        /// <summary>
        /// One backup layer as the Barrow lists it. <c>damaged</c> is the layer that has lost
        /// the world file that went with it: it can be cleared but never unearthed, so the
        /// interface greys its UNEARTH control rather than offering a restore that would write
        /// the wrong bytes over a live world.
        /// </summary>
        public static object BuildBackupLayerDto(WorldBackupInfo layer, long? day)
            => new
            {
                file = layer.Name,
                kind = WorldStore.KindToken(layer.Kind),
                isDirectory = layer.IsDirectory,
                sizeBytes = layer.SizeBytes,
                modifiedUtc = layer.LastWriteUtc,
                day,
                hasDb = layer.HasDb,
                committed = layer.IsCommitted,
                damaged = layer.IsDamaged,
                saveNumber = layer.SaveNumber,
            };

        /// <summary>
        /// The companion plugins that could not be installed, as server.status carries them, so
        /// the interface can say a feature is off and why instead of leaving it silently missing.
        /// Empty in the normal case. The record is process-wide, so a profile is passed in and
        /// only that realm's failures come back, plus any recorded with no profile in hand:
        /// otherwise one realm's bar would name a plugin belonging to the other. Passing no
        /// profile returns everything, which is what the single-server path has always got.
        /// </summary>
        public static List<object> BuildPluginFailureDtos(string profile = null)
            => Tools.CompanionPluginStatus.FailuresFor(profile)
                .Select(f => (object)new
                {
                    plugin = f.Plugin,
                    message = f.Message,
                    text = f.Describe(),
                    profile = f.Profile,
                })
                .ToList();

        /// <summary>
        /// Copies a world into a destination save folder's worlds_local subdir in whichever
        /// format it already uses - the pre-1.0 file pair with its .old siblings, or the whole
        /// 1.0 world directory - so an adopted world is a real independent copy and the
        /// original is never moved, altered, or deleted. The biome data cache travels with it.
        /// </summary>
        public static void CopyWorldFiles(string sourceFolder, string sub, string world, string destSaveFolder)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder) || string.IsNullOrWhiteSpace(world)) return;

            var source = WorldStore
                .EnumerateIn(sourceFolder, string.IsNullOrWhiteSpace(sub) ? WorldStore.WorldSubfolders[0] : sub)
                .FirstOrDefault(w => string.Equals(w.Name, world, StringComparison.OrdinalIgnoreCase));
            if (source == null) return;

            try
            {
                WorldStore.CopyWorld(source, destSaveFolder);
            }
            catch (InvalidOperationException taken)
            {
                // WorldStore refuses to copy onto a world of the same name rather than mixing
                // two save histories into one folder. Adopt asks for a fresh folder, so this
                // only fires when a removed profile left its worlds behind. The host asked to
                // adopt, so say what happened to the adoption and what to do about it.
                throw new InvalidOperationException(
                    $"The save folder for this server already holds a world called '{world}', so nothing was adopted. "
                    + "Pick a different name for the new server, or clear that folder first. "
                    + taken.Message);
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

            // Shape it like a save folder the game made itself: worlds_local plus the cache/
            // sibling it drops "{world}_biomedatacache.bin" into on every world load.
            WorldStore.EnsureSaveFolderLayout(candidate);
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
                // Platform arrives straight from the peer handshake ("Steam", "Xbox",
                // "PlayStation", "PlayFab", ...). The UI prefers it over guessing from the id,
                // which stopped being reliable once ids lost their fixed shape.
                platform = player.Platform,
                // Compatibility only: the same value under the old PascalCase key, which the
                // anonymous type used to produce by accident. Added deliberately 2026-09-10
                // so a WebUI still reading it keeps working for one release; remove in 1.1.
                Platform = player.Platform,
                player.PlayerId,
                player.PlayerName,
                // The id the server itself knows this player by, for the actions that must
                // reach a person rather than a spelling. Null when the platform or the id is
                // missing, and the page falls back to the name it shows.
                hostId = Tools.Models.PlayerPlatforms.HostId(player.Platform, player.PlayerId),
                displayName = name,
                status = player.PlayerStatus.ToString(),
                lastStatusChange = player.LastStatusChange,
                characters = player.Characters?.Select(c => c.CharacterName).ToList(),
                serverKey = player.ServerKey,
                position = LivePositionOf(player),
            };
        }

        /// <summary>
        /// Where this player is standing, as the roster shows it ("x, y, z" in whole metres),
        /// or null. Read out of the running server's position cache, which one "playerlist"
        /// call fills for everybody at once every few seconds - there is no per-player call.
        /// Only an online player has a position: a cached coordinate for somebody who has
        /// since logged off would read as if they were still out there.
        /// </summary>
        private string LivePositionOf(PlayerInfo player)
        {
            if (player == null || player.PlayerStatus != PlayerStatus.Online) return null;

            var profile = player.ServerKey ?? ActiveProfileName;
            if (string.IsNullOrWhiteSpace(profile)) return null;
            if (!Sessions.TryGetValue(profile, out var session)) return null;

            var character = string.IsNullOrWhiteSpace(player.LastStatusCharacter)
                ? player.PlayerName
                : player.LastStatusCharacter;

            try { return session.Server.GetCachedPosition(character); }
            catch { return null; }
        }

        /// <summary>
        /// Works out what installing a package from Hexium would do, and answers with
        /// everything the host needs to decide: who Hexium says owns it, the version,
        /// how big the download is when the site says, what the package expects to find
        /// beside it, and whether a folder is already there to be replaced.
        /// <para>
        /// Nothing is downloaded and nothing on disk is touched. The answer carries a
        /// token that is good once, for this package and version only, and
        /// mods.installFromHexium refuses without it. Either a link or an owner and name
        /// may be given; the link route is what a pasted address takes.
        /// </para>
        /// </summary>
        private async Task<object> PrepareHexiumInstallAsync(string url, string owner, string name, string version)
        {
            object Fail(string error, string reason) => new
            {
                Installed = false,
                NeedsConsent = false,
                Replaced = false,
                Owner = owner,
                Name = name,
                Version = version,
                Source = Tools.ModSourceMarkerFile.HexiumSource,
                Reason = reason,
                Error = error,
            };

            if (!UserPrefsProvider.LoadPreferences().UseHexiumSource)
                return Fail("Turn on Also check Hexium in Upkeep to install from Hexium.", "sourceOff");

            if (_modUpdateInProgress)
                return Fail("A mod update is already in progress. Try again in a moment.", "busy");

            if (!string.IsNullOrWhiteSpace(url))
            {
                if (!Tools.HexiumUrlParser.TryParse(url, out var reference, out var parseError))
                    return Fail(parseError, "badLink");

                owner = reference.Owner;
                name = reference.Name;
                version = reference.Version;
            }

            if (!Tools.HexiumUrlParser.IsOwnerSegment(owner) || !Tools.HexiumUrlParser.IsNameSegment(name))
                return Fail("That is not a Hexium owner and mod pair.", "badName");

            if (!string.IsNullOrWhiteSpace(version) && !Tools.HexiumUrlParser.IsVersionSegment(version))
                return Fail($"\"{version}\" does not look like a version number.", "badVersion");

            var lookup = await HexiumClient.LookupAsync($"{owner.Trim()}-{name.Trim()}");
            if (!lookup.IndexAvailable) return Fail(lookup.Error, "unreachable");
            if (!lookup.Found) return Fail($"Hexium has no package named {owner}/{name}.", "notFound");

            var package = lookup.Package;

            // A named version has to exist; otherwise take the highest one, worked out
            // here rather than read off the top of a list the site does not sort.
            var chosen = string.IsNullOrWhiteSpace(version)
                ? PickHexiumVersion(package)
                : package.Find(version);

            if (chosen == null)
            {
                return string.IsNullOrWhiteSpace(version)
                    ? Fail($"Hexium lists {owner}/{name} but offers no version to install.", "noVersion")
                    : Fail($"Hexium does not offer {owner}/{name} {version}.", "noVersion");
            }

            if (!Tools.HexiumUrlParser.IsDownloadAddress(chosen.DownloadUrl, out _))
                return Fail("Hexium gave a download address that is not on hexium.gg, so it was turned down.", "badAddress");

            var pluginsDir = GetPluginsDirectory();
            var folderName = $"{package.Owner}-{package.Name}";
            string replacingVersion = null;
            var replacing = false;

            if (!string.IsNullOrWhiteSpace(pluginsDir) && Directory.Exists(pluginsDir))
            {
                var existing = ModScanner.ScanPlugins(pluginsDir).FirstOrDefault(m => m.FullName == folderName);
                replacing = existing != null;
                replacingVersion = existing?.InstalledVersion;
            }

            var token = HexiumConsent.Issue(package.Owner, package.Name, chosen.VersionNumber);

            return new
            {
                Installed = false,
                NeedsConsent = true,
                Source = Tools.ModSourceMarkerFile.HexiumSource,
                Owner = package.Owner,
                Name = package.Name,
                Version = chosen.VersionNumber,
                FileSize = chosen.FileSize,
                Dependencies = (chosen.Dependencies ?? new List<string>()).ToArray(),
                PageUrl = Tools.HexiumUrlParser.PageUrl(package.Owner, package.Name),
                Folder = folderName,
                Replacing = replacing,
                ReplacingVersion = replacingVersion,
                Token = token,
                Reason = (string)null,
                Error = (string)null,
            };
        }

        /// <summary>
        /// Which Hexium build a link with no version in it means. Full releases only,
        /// unless the folder already holds a pre-release, in which case a pre-release on
        /// the same line or later counts too. Falls back to the highest build of any kind
        /// for a package that has never had a full release.
        /// </summary>
        private Tools.Models.HexiumPackageVersion PickHexiumVersion(Tools.Models.HexiumPackage package)
        {
            var pluginsDir = GetPluginsDirectory();
            string installed = null;

            if (!string.IsNullOrWhiteSpace(pluginsDir) && Directory.Exists(pluginsDir))
            {
                installed = ModScanner.ScanPlugins(pluginsDir)
                    .FirstOrDefault(m => m.FullName == $"{package.Owner}-{package.Name}")?.InstalledVersion;
            }

            return package.LatestFor(installed) ?? package.LatestAny;
        }

        /// <summary>
        /// Reads the host's switch and hands it to the scan step, which is where the
        /// answer is acted on. With the switch off nothing is asked of the second site,
        /// which is what makes "off means no connection to hexium.gg" true rather than
        /// merely intended. A preferences file that cannot be read counts as off.
        /// </summary>
        private async Task AddHexiumVersionsAsync(IEnumerable<Tools.Models.InstalledMod> mods, bool force = false)
        {
            bool enabled;
            try { enabled = UserPrefsProvider.LoadPreferences().UseHexiumSource; }
            catch { return; }

            // The step itself decides what a false means, so the promise can be driven and
            // proved on its own rather than inferred from these two lines. The force is
            // inside that gate as well: a scan with the switch off still contacts nobody.
            await HexiumScan.ApplyAsync(mods, enabled, force);
        }

        /// <summary>
        /// What the page shows about the package list a scan's rows were answered from:
        /// when it was read, which of the two addresses answered, and whether this press
        /// of Scan went out or reused a list read moments ago. Null when the scan did not
        /// ask for a refresh, in which case the page falls back to what it already knows.
        /// </summary>
        private object BuildIndexStateDto(Tools.ThunderstoreIndexState state)
        {
            var fetchedUtc = state?.FetchedUtc ?? ThunderstoreClient.IndexFetchedUtc;
            var source = state?.Source ?? ThunderstoreClient.IndexSource;

            return new
            {
                fetchedUtc = fetchedUtc?.ToUniversalTime().ToString("o"),
                source,
                refreshed = state?.Fetched ?? false,
                reusedFresh = state?.ReusedFresh ?? false,
                packageCount = state?.PackageCount ?? 0,
            };
        }

        /// <summary>
        /// How long one package has to wait before it can be checked again by hand. A
        /// host pressing the row action twice on the same mod is one request, not two.
        /// </summary>
        private static readonly TimeSpan CheckOneCooldown = TimeSpan.FromSeconds(10);

        private readonly Dictionary<string, DateTime> _checkedOneAtUtc = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True when this package may be asked about now, having recorded that it was.
        /// False when it was asked about inside <see cref="CheckOneCooldown"/>, and then
        /// nothing goes out to the site at all.
        /// </summary>
        private bool TakeCheckOneSlot(string fullName)
        {
            var now = DateTime.UtcNow;

            lock (_checkedOneAtUtc)
            {
                // An entry past its cooldown cannot refuse anything any more, so it is
                // dropped rather than kept for the life of the window. Doing it here keeps
                // the list about as long as the presses of the last ten seconds.
                foreach (var stale in _checkedOneAtUtc
                             .Where(pair => !CheckOneOnCooldown(pair.Value, now))
                             .Select(pair => pair.Key)
                             .ToList())
                {
                    _checkedOneAtUtc.Remove(stale);
                }

                if (_checkedOneAtUtc.TryGetValue(fullName, out var last) && CheckOneOnCooldown(last, now))
                    return false;

                _checkedOneAtUtc[fullName] = now;
                return true;
            }
        }

        /// <summary>
        /// True when this package was asked about too recently to ask again. Reachable so
        /// the rule can be read straight rather than inferred from a timing test.
        /// </summary>
        public static bool CheckOneOnCooldown(DateTime lastCheckedUtc, DateTime nowUtc) =>
            lastCheckedUtc != DateTime.MinValue && nowUtc - lastCheckedUtc < CheckOneCooldown;

        /// <summary>
        /// Whether a single-mod check should say an update is waiting.
        /// <para>
        /// The one rule that matters here is the Hexium one: a copy the host deliberately
        /// took from the other site is left alone by every update path, so it never reads
        /// as having a Thunderstore update waiting however far ahead Thunderstore has gone.
        /// The row still shows what Thunderstore holds, and the swap back is its own
        /// deliberate action that asks first.
        /// </para>
        /// </summary>
        public static bool CheckOneSaysUpdateWaiting(string latestVersion, string installedVersion, bool installedFromHexium) =>
            !installedFromHexium && Tools.SemVer.IsNewer(latestVersion, installedVersion);

        /// <summary>
        /// Whether a single-mod check should say Thunderstore has moved past a copy the
        /// host took from Hexium.
        /// <para>
        /// This is the other half of the rule above, and the page needs both: with only
        /// the first, a held copy Thunderstore has left behind reads as "you have the
        /// newest", which is the one thing it certainly is not. Neither an update waiting
        /// nor the newest: something newer is out there and BakaLoader is deliberately
        /// leaving these files alone.
        /// </para>
        /// </summary>
        public static bool CheckOneSaysThunderstoreMovedPast(string latestVersion, string installedVersion, bool installedFromHexium) =>
            installedFromHexium && Tools.SemVer.IsNewer(latestVersion, installedVersion);

        /// <summary>
        /// Whether the package list in hand is recent enough to say a mod that is not in
        /// it has been pulled.
        /// <para>
        /// A failed read leaves the last good list standing on purpose, so a blip does not
        /// wipe a scan's results. That list is worth showing versions from, and it is not
        /// worth marking rows delisted from: after a long run and a refresh that could not
        /// get through, it can be hours old, and hours old is exactly the state this whole
        /// release exists to stop reading as fact. The window is the same one an ordinary
        /// check reads a held list within.
        /// </para>
        /// </summary>
        public static bool ListIsRecentEnoughToJudge(DateTime? listReadUtc, DateTime nowUtc) =>
            listReadUtc is { } read && nowUtc.ToUniversalTime() - read.ToUniversalTime() < Tools.ThunderstoreClient.CacheTtl;

        /// <summary>
        /// Whether a row should say its package was not in the list.
        /// <para>
        /// It needs a list that actually came back, and came back lately: "nobody could be
        /// asked" and "the site answered and this was not in it" are different things, and
        /// only the second is worth putting on a row. A folder that does not name a
        /// package at all is left alone, because it was never expected to be in the list.
        /// </para>
        /// </summary>
        public static bool NotListedNow(bool packageFound, bool listWasRead, string author, string modName) =>
            !packageFound
            && listWasRead
            && !string.IsNullOrWhiteSpace(author)
            && !string.IsNullOrWhiteSpace(modName);

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

        /// <summary>
        /// One row of the mods table. Static and reachable so the shape can be tested without
        /// a scan. The three thunderstore keys are camel case on purpose: they are a new
        /// contract with the page, while the older keys keep the casing the page already reads.
        /// </summary>
        /// <summary>
        /// An <see cref="IProgress{T}"/> that runs its callback inline on the reporting thread
        /// instead of posting it to a captured context. Reports then reach PostEvent in the
        /// exact order they are made (PostEvent marshals each to the UI thread FIFO), so the
        /// bar never sees a "done" before its "updating". <see cref="Progress{T}"/> posts
        /// asynchronously and can reorder, which a progress bar must not do.
        /// </summary>
        private sealed class SynchronousProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SynchronousProgress(Action<T> handler) { _handler = handler; }
            public void Report(T value) => _handler(value);
        }

        public static object BuildModDto(Tools.Models.InstalledMod mod) => BuildModDto(mod, null);

        public static object BuildModDto(Tools.Models.InstalledMod mod, DateTime? gameUpdatedUtc) =>
            BuildModDto(mod, gameUpdatedUtc, DateTime.UtcNow);

        /// <summary>
        /// The window after a Valheim update during which no mod is flagged possibly outdated,
        /// so authors have time to publish before the whole list lights up.
        /// </summary>
        public static readonly TimeSpan PossiblyOutdatedGrace = TimeSpan.FromDays(7);

        /// <summary>
        /// Builds the row a mod scan hands the UI. <paramref name="gameUpdatedUtc"/> is the
        /// game's last update time (same for every row). The "possibly outdated" hint reads
        /// true only when the mod's newest Thunderstore release came out before the game last
        /// updated AND that update is itself older than <see cref="PossiblyOutdatedGrace"/>, so
        /// the column stays quiet for the first week after a patch instead of flagging every
        /// mod at once. Either date unknown leaves it false.
        /// </summary>
        public static object BuildModDto(Tools.Models.InstalledMod mod, DateTime? gameUpdatedUtc, DateTime nowUtc)
        {
            var modUpdatedUtc = mod.LatestReleasedUtc?.ToUniversalTime();
            var gameUtc = gameUpdatedUtc?.ToUniversalTime();
            var patchIsOldEnough = gameUtc != null && gameUtc < nowUtc.ToUniversalTime() - PossiblyOutdatedGrace;
            var possiblyOutdated = modUpdatedUtc != null && gameUtc != null
                && patchIsOldEnough && modUpdatedUtc < gameUtc;

            return new
            {
                mod.Author,
                mod.ModName,
                mod.FullName,
                mod.InstalledVersion,
                mod.LatestVersion,
                mod.UpdateAvailable,
                mod.PluginDirectory,
                // Patcher-type mods (BepInEx/patchers). IsPatcher marks a patcher-ONLY row, which
                // the UI can remove but not update; a mod that ships both parts is not a patcher row.
                mod.IsPatcher,
                mod.PatcherDirectory,
                // Null unless the Thunderstore index actually matched this folder, so a row
                // never offers a page that would land on a 404.
                thunderstoreNamespace = mod.ThunderstoreNamespace,
                thunderstoreName = mod.ThunderstoreName,
                thunderstoreUrl = ThunderstorePageUrl(mod.ThunderstoreNamespace, mod.ThunderstoreName),
                // True only when a package list that actually came back had no entry for
                // this mod. The row says so on the Latest cell and offers no update; it
                // never removes or rolls back anything, and the next scan that finds the
                // package clears it.
                notListed = mod.NotListedOnThunderstore,
                // "Possibly outdated": a hint, not proof. Dates are ISO 8601 (round-trip) or null.
                possiblyOutdated,
                modUpdatedUtc = modUpdatedUtc?.ToString("o"),
                gameUpdatedUtc = gameUtc?.ToString("o"),
                // --- The second mod site. Every one of these is null or false unless the
                // host turned "Also check Hexium" on, and none of them touches
                // UpdateAvailable, the waiting count, "Update all" or the unattended path.
                // hexiumLatest is what Hexium holds; hexiumNewer says it is ahead of BOTH
                // what is installed and what Thunderstore has; installedSource says these
                // files came from Hexium, which is the only thing that makes the row skip
                // Thunderstore updates; thunderstoreNewer is the mirror of that, for a
                // Hexium copy Thunderstore has since moved past.
                hexiumLatest = mod.HexiumLatestVersion,
                hexiumNewer = mod.HexiumNewer,
                hexiumUrl = Tools.HexiumUrlParser.PageUrl(mod.HexiumOwner, mod.HexiumName),
                installedSource = mod.InstalledSource,
                thunderstoreNewer = mod.ThunderstoreNewer,
            };
        }

        /// <summary>
        /// The game install's Steam "last updated" time for the current profile, or null when
        /// there is no manifest to read it from (a hand copy). Never throws.
        /// </summary>
        private DateTime? ReadGameLastUpdatedUtc()
        {
            var exe = GetServerExePath();
            if (string.IsNullOrWhiteSpace(exe)) return null;
            try { return Tools.ServerBuildTracker.Probe(exe).LastUpdatedUtc; }
            catch { return null; }
        }

        // A Thunderstore namespace or package name: letters, digits, underscore, hyphen and
        // dot, nothing else. A slash, a colon, a space or a percent sign is refused, so no
        // scheme, host, query or fragment can be smuggled through either half of the address.
        private static readonly System.Text.RegularExpressions.Regex ThunderstoreSegmentPattern =
            new(@"^[A-Za-z0-9_\-.]{1,128}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// True when a value is usable as one segment of a Thunderstore package address.
        /// A segment made only of dots is refused as well: it passes the character rule but
        /// walks the path somewhere else once a browser works the address out.
        /// </summary>
        public static bool IsThunderstoreSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var trimmed = value.Trim();
            if (trimmed.Trim('.').Length == 0) return false;

            return ThunderstoreSegmentPattern.IsMatch(trimmed);
        }

        /// <summary>
        /// The address of a mod's own Thunderstore page, or null when either half of the
        /// package identity is missing or is not a plain package segment. The app builds the
        /// address itself and never accepts one from the page, so a right-click on a mod row
        /// can only ever open Thunderstore.
        /// </summary>
        public static string ThunderstorePageUrl(string packageNamespace, string packageName)
        {
            if (!IsThunderstoreSegment(packageNamespace) || !IsThunderstoreSegment(packageName))
                return null;

            return "https://thunderstore.io/c/valheim/p/"
                + packageNamespace.Trim() + "/" + packageName.Trim() + "/";
        }

        /// <summary>
        /// One server profile as the Settings hall reads it: everything that is stored,
        /// plus the two paths BakaLoader will REALLY use for this server and where each of
        /// them came from.
        /// <para>
        /// The four added keys are why the Directories boxes used to look blank on a fresh
        /// install. The first time setup writes its answers to the app wide settings, and a
        /// profile only carries a path when somebody overrode it for that one server, so the
        /// hall was rendering a null while Open was resolving the same fallback the launcher
        /// resolves. The boxes are still the override and still show the profile's own
        /// value; the line above them says what is in force.
        /// </para>
        /// <para>
        /// Additive: every key that was in this reply before is still in it, spelled the
        /// same way, so a page that knows nothing about the four new ones is unaffected.
        /// </para>
        /// </summary>
        public static JObject BuildProfilePrefsDto(ServerPreferences profile, UserPreferences user)
        {
            var dto = profile == null ? new JObject() : JObject.FromObject(profile);

            var exe = PathCheck.Effective(profile?.ServerExePath, user?.ServerExePath);
            var save = PathCheck.Effective(profile?.SaveDataFolderPath, user?.SaveDataFolderPath);

            dto["EffectiveServerExePath"] = exe.Path;
            dto["ServerExePathSource"] = exe.Source;
            dto["EffectiveSaveDataFolderPath"] = save.Path;
            dto["SaveDataFolderPathSource"] = save.Source;
            return dto;
        }

        /// <summary>
        /// What a picker answers: the path that was chosen, or that it was cancelled. Two
        /// shapes rather than a null, so the page never has to tell "nothing was chosen"
        /// apart from "the call failed" by looking at what came back.
        /// </summary>
        public static object PickReply(bool accepted, string path)
            => accepted && !string.IsNullOrWhiteSpace(path)
                ? new { path = path.Trim() }
                : (object)new { cancelled = true };

        /// <summary>
        /// Runs a piece of work on the window's own thread and hands the answer back.
        /// <para>
        /// A modal Windows dialog has to be opened on the thread that owns the window, and
        /// an RPC handler is not reliably on it: WebView2 raises the message there, but the
        /// handler before this one may have awaited, and a continuation lands wherever the
        /// scheduler puts it. Opening a picker off that thread either throws or puts up a
        /// window with no owner that the app can be closed behind.
        /// </para>
        /// </summary>
        private T RunOnUiThread<T>(Func<T> work)
        {
            if (work == null) return default;
            if (!InvokeRequired) return work();
            return (T)Invoke(work);
        }

        /// <summary>
        /// How long <c>paths.check</c> waits for the disk before it answers that the path
        /// could not be checked in time.
        /// <para>
        /// Short on purpose. The page asks again 320ms after the typing pauses, so this is
        /// the longest a line under the box can stay blank before it says something, and
        /// nothing downstream of it is worth waiting longer for: an answer that arrives
        /// after the next keystroke is dropped by the page's sequence guard anyway. It is
        /// generous enough for a disk that is merely busy and short enough that a share
        /// nobody is answering on says so while the host is still looking at the box.
        /// </para>
        /// </summary>
        internal static readonly TimeSpan PathCheckBudget = TimeSpan.FromMilliseconds(1500);

        /// <summary>
        /// Where a picker should open: the folder the path in force for this server already
        /// points at, when that folder is really there. Null otherwise, which is Windows'
        /// own "wherever you were last", and never a refusal: this only decides a starting
        /// view.
        /// </summary>
        private string PickerStartFolder(string kind)
        {
            try
            {
                if (string.Equals(kind, PathCheck.KindExe, StringComparison.Ordinal))
                {
                    var exe = PathCheck.Expand(GetServerExePath());
                    var folder = string.IsNullOrWhiteSpace(exe) ? null : Path.GetDirectoryName(exe);
                    return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? folder : null;
                }

                var save = PathCheck.Expand(ResolveSaveDataFolder(null));
                return !string.IsNullOrWhiteSpace(save) && Directory.Exists(save) ? save : null;
            }
            catch { return null; }
        }

        #region Language

        /// <summary>
        /// Set the first time this window answers lang.status, and never unset: the quiet
        /// post-update fetch is started once per window, and every later answer reports what
        /// that one call decided rather than deciding again.
        /// </summary>
        private string LanguageQuietFetchAnswer;

        /// <summary>The language the interface is saved in, always a spelling the app knows.</summary>
        private string CurrentLanguage(UserPreferences prefs) =>
            LanguageCodes.Normalize(prefs?.Language) ?? LanguageCodes.English;

        /// <summary>
        /// A spelling this window could actually be read in: English, which ships inside the
        /// app, or a language with a pack on disk. Null for anything else, which every caller
        /// reads as "leave the preference where it was".
        /// <para>
        /// A service that is not wired yet answers the code back rather than refusing it: the
        /// guard exists to stop a pack-less language being SAVED, and a window with no pack
        /// service has no packs to check against and no globe to have asked from.
        /// </para>
        /// </summary>
        private string ReadableLanguage(string normalized)
        {
            if (normalized == null || LanguageCodes.IsEnglish(normalized)) return normalized;
            if (LanguagePacks == null) return normalized;

            return LanguagePacks.InstalledAny(normalized) != null ? normalized : null;
        }

        /// <summary>
        /// The pack for the language that is being read, on the host the languages folder is
        /// mapped to: a second origin, which the Allow access kind on that mapping is what lets
        /// the page read. Null for English, which ships inside the app and is fetched from
        /// beside the page, on the page's own origin.
        /// </summary>
        private static string LanguageStringsUrl(string code, string version) =>
            LanguageCodes.IsEnglish(code) || string.IsNullOrWhiteSpace(version)
                ? null
                : LanguageUrlPrefix + code + "/" + version + "/strings.json";

        /// <summary>
        /// The faces a pack carries, addressed in the shared font store. The page writes one
        /// @font-face rule per row; the store is content addressed, so two languages that use
        /// the same face name the same URL and the browser fetches it once.
        /// </summary>
        private static List<object> LanguageFonts(LanguagePackInstall install)
        {
            var fonts = new List<object>();
            if (install?.Fonts == null) return fonts;

            foreach (var font in install.Fonts)
            {
                if (string.IsNullOrWhiteSpace(font?.File) || string.IsNullOrWhiteSpace(font.Family)) continue;

                fonts.Add(new
                {
                    family = font.Family,
                    url = LanguageUrlPrefix + font.File.Replace('\\', '/').TrimStart('/'),
                    weight = font.Weight,
                    style = font.Style,
                    unicodeRange = font.UnicodeRange,
                });
            }

            return fonts;
        }

        /// <summary>
        /// How many lines the host is still reading in English. A pack cut for an older
        /// version carries the lines that existed when it was cut, so what the English catalog
        /// has gained since is the gap. Zero for English itself and zero when the arithmetic
        /// would go negative, which a pack cut from a newer catalog than this build ships can
        /// make it do.
        /// </summary>
        private static int LanguageMissingKeys(LanguagePackInstall install)
        {
            if (install == null || HostCatalog.EnglishKeyCount <= 0) return 0;

            var covered = install.Translated > 0 ? install.Translated : install.Keys;
            return Math.Max(0, HostCatalog.EnglishKeyCount - covered);
        }

        /// <summary>
        /// The word the page shows beside the status, and, the first time this window asks,
        /// the fetch itself. It runs on a worker rather than here: this handler answers on the
        /// UI thread and the page is waiting on it, so a pack fetched at boot must never be
        /// something the first frame waits for. It is never a splash step for the same reason.
        /// </summary>
        private string QuietLanguageFetch(UserPreferences prefs, string code)
        {
            if (LanguageQuietFetchAnswer != null) return LanguageQuietFetchAnswer;

            var version = AssemblyHelper.GetApplicationVersion();
            var decision = LanguagePacks.QuietFetchDecision(prefs?.CheckForUpdates ?? true, code, version);
            LanguageQuietFetchAnswer = decision;

            if (decision != LanguageQuietFetch.Started) return decision;

            _ = Task.Run(async () =>
            {
                try
                {
                    await LanguagePacks.EnsureCurrentQuietlyAsync(prefs?.CheckForUpdates ?? true, code, version);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "The language pack for {code} could not be refreshed after the update", code);
                }
            });

            return decision;
        }

        /// <summary>
        /// Points the host-facing sentences at the right catalog. The people on the server are
        /// written to in the interface's language unless the host chose another one for them,
        /// and the pack that answers is whichever version of it is on disk.
        /// </summary>
        private void RefreshHostCatalog()
        {
            try
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                var code = HostCatalog.EffectiveCode(prefs?.Language, prefs?.PlayerMessageLanguage);
                var install = LanguageCodes.IsEnglish(code) ? null : LanguagePacks.InstalledAny(code);

                HostCatalog.Use(HostCatalog.Load(GetLanguagesDir(), code, install?.Version));
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "The player-message catalog could not be picked");
            }
        }

        #endregion

        /// <summary>
        /// Which sentence a failed scan is worded from. "Could not reach Thunderstore" is
        /// only true of a read that left the machine. A press that ran out of time waiting
        /// for another scan's turn never asked the site anything, and telling a host the
        /// site did not answer would send them looking at their network for a queue that is
        /// inside BakaLoader.
        /// </summary>
        public static string ScanFailureId(ThunderstoreFailureMemo failure)
        {
            var reason = failure?.Reason;

            // A press that ran out of time waiting for another scan's turn never asked the
            // site anything.
            if (string.Equals(reason, "busy", StringComparison.Ordinal))
                return "mods.empty.failed.reason.busy";

            // And the site ANSWERING with something that was not its package list is not a
            // site that could not be reached. Framing it as one put "could not reach
            // Thunderstore (the site answered with something that was not its package list)"
            // on the screen, which contradicts itself inside one bracket. A captive portal
            // and a 503 both look like this from here, and both are worth telling apart from
            // a machine that cannot get out at all.
            if (string.Equals(reason, "answered", StringComparison.Ordinal))
                return "mods.empty.failed.reason.answered";

            return "mods.empty.failed.reason.unreachable";
        }

        /// <summary>
        /// The slots the failure sentence has: WHICH kind of failure the last read was, and
        /// when every caller stops being refused the trip.
        /// <para>
        /// All of them travel as data and none of them travels as English. The kind is one of
        /// <see cref="Tools.ThunderstoreClient.ReasonNames"/>, which the page keeps a
        /// sentence for, and the wait travels both as the moment it ends and as the seconds
        /// that were left when this was written. A phrase written here would have landed
        /// inside a translated sentence in English, which is exactly what the four packs this
        /// release carries exist to stop.
        /// </para>
        /// </summary>
        private static object ScanFailureParams(ThunderstoreFailureMemo failure)
        {
            if (failure == null)
                return new
                {
                    detailName = Tools.ThunderstoreClient.ReasonName(null),
                    retrySeconds = 0,
                    retryAtUtc = (string)null,
                };

            return new
            {
                detailName = Tools.ThunderstoreClient.ReasonName(failure.Reason),
                // Kept for a page from an older build, which reads only this.
                retrySeconds = ScanRetrySeconds(failure, DateTime.UtcNow),
                // WHEN the wait is over rather than how long was left when this was written.
                retryAtUtc = ScanRetryAtUtc(failure),
            };
        }

        /// <summary>
        /// How many whole seconds of a memo's backoff are left at <paramref name="nowUtc"/>,
        /// never below zero. A wait that has run out is nought seconds rather than a negative
        /// number, because the page words "none left" as a sentence of its own.
        /// </summary>
        public static int ScanRetrySeconds(ThunderstoreFailureMemo failure, DateTime nowUtc)
        {
            if (failure == null) return 0;
            var left = failure.RetryAtUtc - nowUtc;
            return left <= TimeSpan.Zero ? 0 : (int)Math.Round(left.TotalSeconds);
        }

        /// <summary>
        /// The moment a memo's backoff ends, as the page parses it, or null when there is no
        /// memo.
        /// <para>
        /// The panel that shows this is persistent: a host who leaves the Mods hall open is
        /// still reading the same sentence a quarter of an hour later. A number of seconds
        /// worked out when the reply was written would by then be telling them to wait a
        /// quarter of an hour they have just spent, so the moment travels as well and the
        /// page words the remaining wait on every draw.
        /// </para>
        /// </summary>
        public static string ScanRetryAtUtc(ThunderstoreFailureMemo failure) =>
            failure == null
                ? null
                : failure.RetryAtUtc.ToUniversalTime().ToString(
                    "yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// What a cleanse came to, from the window's side of the RCON socket.
        /// </summary>
        public sealed class CleanseOutcome
        {
            /// <summary>False only when the command did not get through at all.</summary>
            public bool Ok { get; init; }

            /// <summary>The last line the server said, exactly as it said it.</summary>
            public string Response { get; init; }

            /// <summary>
            /// True when the window stopped asking before the sweep finished. Nothing has
            /// gone wrong: the sweep is on the server and will finish and log its counts.
            /// </summary>
            public bool StillRunning { get; init; }

            /// <summary>
            /// True when the server stopped answering part way through the wait.
            /// <para>
            /// This is NOT the ceiling above, and wording it as one told a host their cleanse
            /// was still going when the thing it was running on had gone. A status read
            /// answers null when RCON is off, when the server is not Running, or when the
            /// socket would not open, and on a wait that has already started a sweep every
            /// one of those means the sweep is not walking any more.
            /// </para>
            /// </summary>
            public bool NotAnswering { get; init; }
        }

        /// <summary>
        /// Starts a cleanse and waits it out.
        /// <para>
        /// From plugin 1.8.1 the sweep is sliced across frames, so baka_cleanse answers the
        /// moment it knows what it is about to walk rather than when it has finished. A world
        /// small enough to be walked inside the first slice still answers with its whole
        /// result line, and a refusal still answers with the refusal: both of those are the
        /// 1.2.3 shape and go straight back to the page. Anything opening with
        /// "Cleanse started" is a sweep that is still going, and this then asks
        /// baka_cleanse_status until it says something other than "Cleanse running", which is
        /// the result line.
        /// </para>
        /// <para>
        /// A status read that did not get through is NOT a finished sweep and is not read as
        /// one. It is the server no longer answering at all, which on a wait that has already
        /// started a sweep means the sweep is not walking any more, so the wait ends and says
        /// that rather than sitting out the ceiling and then wording it as "still running".
        /// </para>
        /// <para>
        /// Public and handed its transport so the loop can be driven against a fake RCON: a
        /// wait that only ever ran against a real server is a wait nothing can hold to its
        /// own rules.
        /// </para>
        /// </summary>
        /// <param name="send">Sends one RCON line and answers with the reply, or null.</param>
        /// <param name="pollEvery">How long between two status reads.</param>
        /// <param name="ceiling">How long to keep asking before giving up on the answer.</param>
        /// <param name="progress">
        /// Handed the plugin's own opening line and then each status line whose counts have
        /// MOVED, so a host watching the Saga log sees the sweep walk instead of a page where
        /// nothing happens for ten minutes. Optional: a caller that wants none of them hands
        /// nothing and the wait behaves exactly as it did.
        /// </param>
        public static async Task<CleanseOutcome> RunCleanseAsync(
            Func<string, Task<string>> send, TimeSpan pollEvery, TimeSpan ceiling,
            Action<string> progress = null)
        {
            if (send == null) return new CleanseOutcome { Ok = false };

            var started = await send("baka_cleanse");
            if (started == null) return new CleanseOutcome { Ok = false };

            if (!started.TrimStart().StartsWith(
                    BakaLoaderKillAll.CleansePlan.StartedPrefix, StringComparison.OrdinalIgnoreCase))
                return new CleanseOutcome { Ok = true, Response = started };

            // The line that says what is about to be walked goes into the log the host is
            // watching, not only into the server's own.
            Tell(progress, started);

            // CLOCKED, not counted. A count of polls is a count of ROUND TRIPS, and each one
            // is a socket opened, a command sent, an answer read and the socket closed on top
            // of the wait between them: twelve hundred of those add up to a good deal more
            // than the ten minutes they were meant to, and the ceiling is the promise the
            // toast and the wiki both make.
            var clock = Stopwatch.StartNew();

            var last = started;
            var told = started;

            while (clock.Elapsed < ceiling)
            {
                if (pollEvery > TimeSpan.Zero) await Task.Delay(pollEvery);

                var status = await send("baka_cleanse_status");

                // Nothing came back at all. On a wait that has already started a sweep the
                // live reason for that is the server going down under it, and reading it as
                // "still going" is how a host came to be told their cleanse was walking a
                // world on a server that had stopped, for the ten minutes it then took to
                // give up.
                if (status == null)
                    return new CleanseOutcome { Ok = true, Response = last, NotAnswering = true };

                last = status;
                if (!status.TrimStart().StartsWith(
                        BakaLoaderKillAll.CleansePlan.RunningPrefix, StringComparison.OrdinalIgnoreCase))
                    return new CleanseOutcome { Ok = true, Response = status };

                // One line per CHANGED count. A status that says exactly what the last one
                // said is the same frame read twice, and a log filling with identical lines
                // is the shape of movement rather than movement itself.
                if (!string.Equals(status, told, StringComparison.Ordinal))
                {
                    told = status;
                    Tell(progress, status);
                }
            }

            return new CleanseOutcome { Ok = true, Response = last, StillRunning = true };
        }

        /// <summary>
        /// Hands one line to a listener that may not be there. A note about the wait must
        /// never be what ends the wait.
        /// </summary>
        private static void Tell(Action<string> listener, string line)
        {
            if (listener == null || string.IsNullOrWhiteSpace(line)) return;
            try { listener(line); }
            catch { /* a line in the log is not worth a cleanse */ }
        }

        /// <summary>
        /// How often the window asks the plugin how far the cleanse has got. Half a second is
        /// one RCON round trip against a walk that takes minutes: often enough that the toast
        /// lands with the sweep, rare enough to be nothing next to a frame of the server's own
        /// work.
        /// </summary>
        private static readonly TimeSpan CleansePollEvery = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// How long the window waits for a cleanse before it stops asking. Nothing has gone
        /// wrong at the end of it: the sweep is on the server, it will finish, and its counts
        /// go to the server log and to baka_cleanse_status. This is the point at which the
        /// page says that instead of waiting for ever.
        /// </summary>
        private static readonly TimeSpan CleansePollCeiling = TimeSpan.FromMinutes(10);

        private object BuildUserPrefsDto()
        {
            var prefs = UserPrefsProvider.LoadPreferences();
            return new
            {
                prefs.ServerExePath,
                prefs.SaveDataFolderPath,
                // The same two paths with their variables filled in, which is what the
                // Directories boxes show as "default: ..." when a profile overrides nothing.
                // Named the way DefaultLogsFolderPath below already is.
                DefaultServerExePath = PathCheck.Expand(prefs.ServerExePath),
                DefaultSaveDataFolderPath = PathCheck.Expand(prefs.SaveDataFolderPath),
                prefs.CheckForUpdates,
                prefs.AutoUpdateMods,
                prefs.UseHexiumSource,
                prefs.AutoUpdateBakaLoader,
                prefs.BepInExMaintained,
                prefs.BepInExMaintenanceAsked,
                prefs.StartWithWindows,
                prefs.ShareAnonymousStats,
                prefs.StartMinimized,
                prefs.SaveProfileOnStart,
                prefs.WriteApplicationLogsToFile,
                prefs.DetailedLog,
                // True when --verbose was on the command line. It holds Verbose for the whole
                // session and the window cannot put it back, so the page draws the switch on
                // and refuses to move it rather than offering a choice that is not there.
                DetailedLogForcedByCommandLine = LogLevel?.ForcedByCommandLine ?? false,
                prefs.LogsFolderPath,
                DefaultLogsFolderPath = Environment.ExpandEnvironmentVariables(Resources.LogsFolderPath),
                prefs.EnablePasswordValidation,
                prefs.BypassSystemProxy,
                prefs.ForceIPv4,
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
                prefs.Language,
                prefs.PlayerMessageLanguage,
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
            // The one word in the snapshot that is a word rather than a name or a number, and
            // it is read in the Discord post rather than in this window, so it is written in
            // the language the players are written to in.
            var statusText = status switch
            {
                ServerStatus.Running => HostCatalog.T("host.status.state.online"),
                ServerStatus.Starting => HostCatalog.T("host.status.state.starting"),
                ServerStatus.Stopping => HostCatalog.T("host.status.state.stopping"),
                _ => HostCatalog.T("host.status.state.offline"),
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

        #region A world's own settings, brought in rather than wiped

        /// <summary>
        /// What a first meeting brought in, by world, waiting for a page to say it. It stands
        /// until a page has drawn it (worldgen.noticeSeen takes it away), because the import
        /// often happens with no window open at all: an auto-start runs it before anybody has
        /// looked at the app.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _worldKeyImports =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The last disagreement said out loud, by world. A later start whose world holds
        /// different keys from the profile is worth ONE line, not one per status poll, and a
        /// disagreement that then changes is worth saying again: the value is the pair of lists,
        /// so the line repeats only when one of them moves.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _worldKeyDisagreements =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// When each world's header was last opened for that comparison. The options are built
        /// again for a status read, a launch check and an update check as well as for a start,
        /// and opening a live server's world header every time one of those happens would be a
        /// read nobody asked for. A minute is far finer than the question needs: a disagreement
        /// is something a host set from the console and then wants to hear about, not something
        /// that has to be noticed the same second.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _worldKeyLastCompared =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan WorldKeyCompareEvery = TimeSpan.FromMinutes(1);

        /// <summary>One world's stored keys, in a fixed order, never null.</summary>
        private static List<string> WorldKeyList(WorldPreferences prefs)
            => (prefs?.Keys ?? new HashSet<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

        /// <summary>What a first meeting brought in for this world, or null.</summary>
        private object WorldKeyImportNotice(string world)
            => world != null && _worldKeyImports.TryGetValue(world, out var notice) ? notice : null;

        /// <summary>
        /// Reads a world's own world-modifier settings out of its header and stores them, the
        /// first time BakaLoader meets that world.
        /// <para>
        /// A world made in the game client, or set from the console, keeps its modifiers as
        /// starting keys in its own header. Every BakaLoader start begins by clearing that list
        /// and writing back what the profile holds, which for a world nobody has configured here
        /// is nothing at all: the world came up vanilla and its settings were gone for good.
        /// This closes that. The keys are read, turned into dials, switches and pass-through
        /// keys, and stored BEFORE the options are built, so the start that follows re-emits
        /// exactly what the world already had.
        /// </para>
        /// <para>
        /// It runs on FIRST MEETING only: a world the profile already holds an entry for is the
        /// host's own choice and is never overwritten from the header. It never blocks and never
        /// throws, because the paths it sits on are unattended (auto-start, a crash relaunch, a
        /// scheduled restart) and a world coming back up matters more than a card nobody is
        /// there to read. A header that cannot be read imports nothing, says why once, and the
        /// start goes ahead exactly as it does today.
        /// </para>
        /// </summary>
        /// <summary>
        /// How the Barrow decides a world is safe to copy, in one place so everything that
        /// copies one decides it the same way.
        /// <para>
        /// A live server rewrites its world as it saves, so a copy taken from underneath one
        /// is a copy of half a save. The test is the pair, not the name alone: a running
        /// server counts only when the world it is running is this world AND the folder it is
        /// running it out of is this folder, because two realms can hold worlds of one name
        /// in save folders of their own. A session's own save folder falls back to the
        /// app-wide one when the profile overrides nothing, which is how a realm on the
        /// shared folder is recognised at all.
        /// </para>
        /// </summary>
        private void RefuseWhileTheWorldIsBeingWritten(string world, string saveFolder)
        {
            var userSave = UserPrefsProvider.LoadPreferences().SaveDataFolderPath;
            foreach (var session in Sessions.Values)
            {
                if (session.Server.Status == ServerStatus.Stopped) continue;

                var live = session.Server.Options;
                var liveFolder = string.IsNullOrWhiteSpace(live?.SaveDataFolderPath)
                    ? userSave
                    : live.SaveDataFolderPath;

                if (string.Equals(live?.WorldName, world, StringComparison.OrdinalIgnoreCase)
                    && SameFolder(liveFolder, saveFolder))
                    throw new HostFacingException("worlds.copyServerRunning",
                        $"'{session.ProfileName}' is running world '{world}' right now. Stop it before copying the world.",
                        ("profile", session.ProfileName), ("world", world));
            }
        }

        private void ImportWorldKeysOnFirstMeeting(string world, string saveFolderHint)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(world)) return;

                var saveFolder = ResolveSaveDataFolder(saveFolderHint);
                if (string.IsNullOrWhiteSpace(saveFolder)) return;

                var outcome = WorldKeyImportStep.Run(
                    WorldPrefsProvider,
                    () => FwlReader.TryReadWorldStartingKeys(saveFolder, world),
                    world);

                if (outcome.Kind == WorldKeyImportKind.AlreadyKnown)
                {
                    LogWorldKeyDisagreement(world, saveFolder, outcome.Existing);
                    return;
                }

                if (outcome.Kind == WorldKeyImportKind.NoHeader)
                {
                    // Either there is no world there yet (the ordinary case for a realm about to
                    // make one) or the header would not read. Neither is a reason to stop.
                    AppLogger.Debug(
                        "No readable world header for '{0}' under {1}, so nothing was brought in and the start goes ahead.",
                        world, saveFolder);
                    return;
                }

                if (outcome.Kind != WorldKeyImportKind.Imported) return;

                var imported = outcome.Imported;
                var dials = imported.Modifiers
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => pair.Key + "=" + pair.Value)
                    .ToList();

                Logger.Information(
                    "World '{0}' already carried its own settings, so they were brought in before the start: dials {1}; switches {2}; carried as they were {3}.",
                    world,
                    dials.Count == 0 ? "none" : string.Join(", ", dials),
                    imported.Switches.Count == 0 ? "none" : string.Join(", ", imported.Switches),
                    imported.PassThrough.Count == 0 ? "none" : string.Join(", ", imported.PassThrough));

                var notice = new
                {
                    world,
                    modifiers = imported.Modifiers,
                    keys = imported.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                    switches = imported.Switches,
                    passThrough = imported.PassThrough,
                    whenUtc = DateTime.UtcNow,
                };
                _worldKeyImports[world] = notice;
                PostEvent("worldgen.imported", notice);
            }
            catch (Exception e)
            {
                // A start must still happen. A world that could not be read is a world left
                // exactly as BakaLoader has always left it.
                AppLogger.Warning("A world's own settings could not be brought in: {0}", e.Message);
            }
        }

        /// <summary>
        /// Says once, in the Saga, that a world's header and the profile's stored settings have
        /// stopped agreeing. No dialog and no import: the profile is the host's own choice by
        /// this point, and somebody who set a key from the console is entitled to know that the
        /// next start will clear it rather than to have it quietly kept.
        /// </summary>
        private void LogWorldKeyDisagreement(string world, string saveFolder, WorldPreferences prefs)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (_worldKeyLastCompared.TryGetValue(world, out var last)
                    && now - last < WorldKeyCompareEvery) return;
                _worldKeyLastCompared[world] = now;

                var headerKeys = FwlReader.TryReadWorldStartingKeys(saveFolder, world);
                if (headerKeys == null) return;

                var onDisk = WorldKeyImport.ComparableHeaderKeys(headerKeys);
                var stored = WorldKeyImport.KeysFor(prefs);
                if (onDisk.SequenceEqual(stored, StringComparer.Ordinal)) return;

                // A preset cannot be spelled out as keys from this side, so a profile holding
                // one has no list to compare and would otherwise disagree with every world
                // forever.
                if (!string.IsNullOrEmpty(prefs.Preset)) return;

                var signature = string.Join("|", onDisk) + " vs " + string.Join("|", stored);
                if (_worldKeyDisagreements.TryGetValue(world, out var said)
                    && string.Equals(said, signature, StringComparison.Ordinal)) return;

                _worldKeyDisagreements[world] = signature;
                Logger.Information(
                    "World '{0}' holds {1}, and this profile is set to start it with {2}. The start writes the profile's set.",
                    world,
                    onDisk.Count == 0 ? "no world modifiers" : string.Join(", ", onDisk),
                    stored.Count == 0 ? "no world modifiers" : string.Join(", ", stored));
            }
            catch (Exception e)
            {
                AppLogger.Debug("Could not compare a world's own settings with the profile's: {0}", e.Message);
            }
        }

        #endregion

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

            var worldName = serverPrefs.WorldName;

            // A world BakaLoader has never held settings for has its OWN settings read out of
            // its header and brought in first, so the start below re-emits what the world
            // already had instead of resetting it to nothing. This is the one place every start
            // path builds its options, the unattended ones included, which is why the import
            // hangs here rather than off the Start button: auto-start, a crash relaunch and a
            // scheduled restart have nobody to answer a card, and none of them may wipe a world.
            if (!string.IsNullOrWhiteSpace(worldName))
            {
                ImportWorldKeysOnFirstMeeting(
                    worldName,
                    !string.IsNullOrWhiteSpace(serverPrefs.SaveDataFolderPath)
                        ? serverPrefs.SaveDataFolderPath
                        : userPrefs.SaveDataFolderPath);
            }

            var worldPrefs = string.IsNullOrWhiteSpace(worldName)
                ? null
                : WorldPrefsProvider.LoadPreferences(worldName);

            var options = ValheimServerOptions.FromPreferences(serverPrefs, userPrefs, worldPrefs);
            options.LogMessageHandler = line => PostEvent("log.server", new { line, profile = logProfile });

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
            if (string.IsNullOrWhiteSpace(fullName))
                throw new HostFacingException("mods.fullNameRequired", "fullName is required");

            var pluginsDir = GetPluginsDirectory()
                ?? throw new HostFacingException("mods.noServerPath", "Server exe path is not configured");

            return ModScanner.ScanPlugins(pluginsDir).FirstOrDefault(m => m.FullName == fullName)
                ?? throw new HostFacingException("mods.noSuchMod",
                    $"No installed mod named '{fullName}'", ("fullName", fullName));
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
        private string GetCanonicalBaseServerExe() => GetCanonicalBaseServerExe(ActiveProfileName);

        /// <summary>
        /// The same question for a named profile rather than the one the window is showing.
        /// A background realm relaunching or starting while the host looks at another one has
        /// to resolve its OWN base install, and the window's active profile is not it.
        /// </summary>
        private string GetCanonicalBaseServerExe(string profileName)
        {
            var exe = GetServerExePathFor(profileName);
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
