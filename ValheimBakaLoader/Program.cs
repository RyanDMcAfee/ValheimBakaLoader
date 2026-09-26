using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Data;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Processes;

namespace ValheimBakaLoader
{
    /// <summary>
    /// Entry point: wires the whole app together through a single DI container,
    /// then hands control to the splash screen, which orchestrates startup.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            PinFrameworkMessagesToEnglish();

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var services = new ServiceCollection();
            ConfigureServices(services, args);
            using var container = services.BuildServiceProvider();

            AnnounceLogLevel(container);
            RepointStartupEntry(container);

            try
            {
                // SplashForm routes startup (update check, player data, auto-start profiles,
                // orphan-server adoption) and then opens the Blend (WebView2) UI.
                Application.Run(container.GetRequiredService<SplashForm>());
            }
            catch (Exception e)
            {
                container.GetRequiredService<IExceptionHandler>()
                    .HandleException(e, "Application Run Exception");
            }
        }

        /// <summary>
        /// The first line of the session: how much detail the log is going to carry, and
        /// which of the two answers decided it.
        /// <para>
        /// It is written before the splash screen opens, so a host who turned Detailed log on
        /// and then could not reproduce the problem can tell from the top of the file whether
        /// the log in front of them is the detailed one. A logger that will not write is not a
        /// reason to refuse to start.
        /// </para>
        /// </summary>
        private static void AnnounceLogLevel(IServiceProvider container)
        {
            try
            {
                var said = container.GetRequiredService<ILogLevelControl>().Apply();
                container.GetRequiredService<ILogger>().Information("{Line:l}", said);
            }
            catch
            {
                // Nothing about the level of the log is worth failing a launch over.
            }
        }

        /// <summary>
        /// The Windows startup entry, checked once a run and put right when the switch is on
        /// and this account's entry names a copy of BakaLoader that is no longer on disk.
        /// <para>
        /// It is here, and not on the save of the Upkeep card, because that card posts all of
        /// its switches together: applying this preference whenever the key arrived is what made
        /// an ordinary save write to the registry for something nobody had touched, and 1.2.5
        /// closed that road. Closing it took the re-point with it, and a host who renames or
        /// moves the folder is not a host who then goes and flips the switch off and on. A
        /// launch is the first thing that happens after the folder moves, so the check belongs
        /// here.
        /// </para>
        /// <para>
        /// It is deliberately NOT last-launched-wins. An entry naming a copy that is still
        /// installed is left exactly where it is, because opening an old copy once must not
        /// quietly take startup away from the one the host moved to; the Upkeep card names that
        /// copy instead, and the host says which one they meant. A missing entry with the
        /// switch on IS written, because the switch reading on while Windows knows nothing
        /// about it is the thing this whole road exists to end.
        /// </para>
        /// <para>
        /// With the switch off nothing is looked at at all. Neither read needs elevation and
        /// neither of them writes a line, and the answer is discarded because the card works
        /// out the same notes for itself: StartupHelper.NotesFor re-derives four of the five
        /// from the registry, and StartupEntry remembers a refused write, the fifth, for the
        /// rest of the run so the card draws that one too. A launch that met a refusal and said
        /// nothing anywhere but the log is the shape this whole road exists to end, so if a note
        /// is ever added that neither a read nor that memory can reach, this answer stops being
        /// one to throw away.
        /// </para>
        /// </summary>
        private static void RepointStartupEntry(IServiceProvider container)
        {
            try
            {
                var prefs = container.GetRequiredService<IUserPreferencesProvider>().LoadPreferences();
                StartupHelper.RepointMovedEntry(
                    prefs.StartWithWindows, container.GetRequiredService<ILogger>());
            }
            catch
            {
                // A startup entry that could not be looked at is not a reason to refuse to start.
            }
        }

        /// <summary>
        /// Keeps .NET's own exception text in one language.
        /// <para>
        /// Any IO, permission, network or JSON failure that escapes a handler becomes page
        /// text through <c>ex.Message</c>, and .NET writes those sentences in
        /// <see cref="CultureInfo.CurrentUICulture"/>. Nothing pinned that, so a host on a
        /// Japanese Windows install was already shown Japanese framework sentences inside an
        /// English interface, with no way to tell where they came from.
        /// </para>
        /// <para>
        /// Only the UI culture moves. <see cref="CultureInfo.CurrentCulture"/> is left alone
        /// on purpose: how a host's machine writes numbers, dates and money is theirs, and
        /// nothing about the language of a message should change it.
        /// </para>
        /// </summary>
        private static void PinFrameworkMessagesToEnglish()
        {
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        }

        /// <summary>
        /// Registers every service in the app. Public because the test suite builds
        /// its container from the same registrations, swapping in mocks afterwards.
        /// </summary>
        public static void ConfigureServices(IServiceCollection services, string[] args)
        {
            // Core plumbing: logging, files, processes, HTTP.
            services.AddSingleton<ApplicationLogger>();
            services.AddSingleton<ILogger>(sp => sp.GetRequiredService<ApplicationLogger>());
            services.AddSingleton<IApplicationLogger>(sp => sp.GetRequiredService<ApplicationLogger>());
            // How much detail the application log carries. The logger reads the dial on every
            // write and the file sink is told to follow it, so Detailed log takes effect the
            // moment it is saved rather than at the next launch.
            services.AddSingleton<ILogLevelControl, LogLevelControl>();
            services.AddSingleton<IExceptionHandler, ExceptionHandler>();
            services.AddSingleton<IFileProvider, JsonFileProvider>();
            services.AddSingleton<IProcessProvider, ProcessProvider>();
            // One seam for every remote client: the two connection switches are read here
            // and the handler they shape is what Thunderstore, GitHub, the language packs,
            // BepInEx, Hexium, Discord, the heartbeat and the IP lookup all send through.
            services.AddSingleton<IHttpTransportSettings, HttpTransportSettings>();
            services.AddSingleton<IHttpClientProvider, HttpClientProvider>();
            services.AddSingleton<IRestClientContext, RestClientContext>();
            services.AddSingleton<IIpAddressProvider, IpAddressProvider>();

            // Remote services: GitHub self-update, Thunderstore mods, telemetry.
            services.AddSingleton<IGitHubClient, GitHubClient>();
            services.AddSingleton<IThunderstoreClient, ThunderstoreClient>();
            services.AddSingleton<IHexiumClient, HexiumClient>();
            services.AddSingleton<IModScanner, ModScanner>();
            services.AddSingleton<IModUpdateService, ModUpdateService>();
            services.AddSingleton<IModRemovalService, ModRemovalService>();
            services.AddSingleton<IInstallIsolationService, InstallIsolationService>();
            services.AddSingleton<IBepInExService, BepInExService>();
            services.AddSingleton<IAppUpdateService, AppUpdateService>();
            services.AddSingleton<IServerBuildProbe, ServerBuildProbe>();
            services.AddSingleton<ISteamHandoff, SteamHandoff>();
            services.AddSingleton<ISteamCmdRunner, SteamCmdRunner>();
            services.AddSingleton<IServerUpdateService, ServerUpdateService>();
            services.AddSingleton<IHeartbeatService, HeartbeatService>();
            services.AddSingleton<IAnalyticsService, AnalyticsService>();
            services.AddSingleton<ISoftwareUpdateProvider, SoftwareUpdateProvider>();

            // One language pack download at a time, across every window: the service owns that
            // latch, so a quiet post-update fetch with no page involved cannot collide with a
            // host who just picked a language from the globe menu.
            services.AddSingleton<ILanguagePackService, LanguagePackService>();
            services.AddSingleton<IRemoteApiClient, RemoteApiClient>();
            services.AddSingleton<IDiscordWebhookService, DiscordWebhookService>();
            services.AddSingleton<IDiscordStatusService, DiscordStatusService>();

            // Server management: companion plugins, RCON, player and pref data.
            services.AddSingleton<IItemIndexerInstaller, ItemIndexerInstaller>();
            services.AddSingleton<ISpawnHelperInstaller, SpawnHelperInstaller>();
            services.AddSingleton<IKillAllInstaller, KillAllInstaller>();
            services.AddSingleton<ICommanderInstaller, CommanderInstaller>();
            services.AddSingleton<IMaxPlayersInstaller, MaxPlayersInstaller>();
            services.AddSingleton<IRequiredModChecker, RequiredModChecker>();
            services.AddSingleton<PlayerListService>();
            services.AddSingleton<ItemCatalog>();
            services.AddTransient<IRconClient, RconClient>();
            services.AddSingleton<IPlayerDataRepository, PlayerDataRepository>();
            services.AddSingleton<IUserPreferencesProvider, UserPreferencesProvider>();
            services.AddSingleton<IServerPreferencesProvider, ServerPreferencesProvider>();
            services.AddSingleton<IWorldPreferencesProvider, WorldPreferencesProvider>();
            services.AddSingleton<IStartupArgsProvider>(new StartupArgsProvider(args));
            services.AddTransient<ValheimServer>();

            // Every live server session in the app, across every window. One singleton, because
            // the question it answers (may BakaLoader replace itself right now?) is about the
            // process, and a per-window answer misses the world running in the window next door.
            services.AddSingleton<IServerSessionRegistry, ServerSessionRegistry>();

            // Windows: one splash for the app, one BlendWindow per server profile.
            services.AddSingleton<IFormProvider, FormProvider>();
            services.AddSingleton<SplashForm>();
            services.AddTransient<BlendWindow>();
        }
    }
}
