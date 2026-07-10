using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
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
            services.AddSingleton<IModScanner, ModScanner>();
            services.AddSingleton<IModUpdateService, ModUpdateService>();
            services.AddSingleton<IModRemovalService, ModRemovalService>();
            services.AddSingleton<IInstallIsolationService, InstallIsolationService>();
            services.AddSingleton<IAppUpdateService, AppUpdateService>();
            services.AddSingleton<IHeartbeatService, HeartbeatService>();
            services.AddSingleton<ISoftwareUpdateProvider, SoftwareUpdateProvider>();
            services.AddSingleton<IRemoteApiClient, RemoteApiClient>();
            services.AddSingleton<IDiscordWebhookService, DiscordWebhookService>();

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

            // Windows: one splash for the app, one BlendWindow per server profile.
            services.AddSingleton<IFormProvider, FormProvider>();
            services.AddSingleton<SplashForm>();
            services.AddTransient<BlendWindow>();
        }
    }
}
