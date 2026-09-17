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
            services.AddSingleton<IExceptionHandler, ExceptionHandler>();
            services.AddSingleton<IFileProvider, JsonFileProvider>();
            services.AddSingleton<IProcessProvider, ProcessProvider>();
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
            services.AddSingleton<IAppUpdateService, AppUpdateService>();
            services.AddSingleton<IServerBuildProbe, ServerBuildProbe>();
            services.AddSingleton<ISteamHandoff, SteamHandoff>();
            services.AddSingleton<ISteamCmdRunner, SteamCmdRunner>();
            services.AddSingleton<IServerUpdateService, ServerUpdateService>();
            services.AddSingleton<IHeartbeatService, HeartbeatService>();
            services.AddSingleton<IAnalyticsService, AnalyticsService>();
            services.AddSingleton<ISoftwareUpdateProvider, SoftwareUpdateProvider>();
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
