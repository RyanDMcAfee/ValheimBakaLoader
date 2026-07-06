using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    public interface IHeartbeatService
    {
        /// <summary>Reports whether a managed server is currently running; set by the active main window.</summary>
        Func<bool> ServerRunningProvider { get; set; }

        /// <summary>Begins the periodic heartbeat. Safe to call more than once.</summary>
        void Start();
    }

    /// <summary>
    /// Sends a tiny anonymous usage heartbeat every few minutes so aggregate install and
    /// live-server counts can be tracked. The payload is exactly three fields:
    /// { deviceHash, appVersion, serverRunning } - deviceHash is a one-way MD5 of
    /// MAC address + machine name (the same anonymous install ID used for crash reports),
    /// so no IPs, server names, world names, passwords, or any personal data ever leave
    /// the machine. Gated on the "Share anonymous usage stats" preference, which is on by
    /// default and can be switched off in the Hearth's Upkeep card. Delivery is
    /// best-effort: failures are logged at debug level and never surface to the user.
    /// </summary>
    public class HeartbeatService : IHeartbeatService
    {
        private const string HeartbeatUrl = "https://heartbeat-production-766c.up.railway.app/heartbeat";
        private static readonly TimeSpan FirstBeatDelay = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan BeatInterval = TimeSpan.FromMinutes(5);

        private readonly IUserPreferencesProvider UserPrefsProvider;
        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IApplicationLogger Logger;

        private Timer BeatTimer;

        public Func<bool> ServerRunningProvider { get; set; }

        public HeartbeatService(
            IUserPreferencesProvider userPrefsProvider,
            IHttpClientProvider httpClientProvider,
            IApplicationLogger logger)
        {
            UserPrefsProvider = userPrefsProvider;
            HttpClientProvider = httpClientProvider;
            Logger = logger;
        }

        public void Start()
        {
            if (BeatTimer != null) return;
            BeatTimer = new Timer(_ => _ = SendBeatAsync(), null, FirstBeatDelay, BeatInterval);
        }

        private async Task SendBeatAsync()
        {
            try
            {
                if (!UserPrefsProvider.LoadPreferences().ShareAnonymousStats) return;

                var payload = JsonConvert.SerializeObject(new
                {
                    deviceHash = AssemblyHelper.GetClientCorrelationId(),
                    appVersion = AssemblyHelper.GetApplicationVersion(),
                    serverRunning = ServerRunningProvider?.Invoke() ?? false,
                });

                using var client = HttpClientProvider.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(HeartbeatUrl, content);
            }
            catch (Exception e)
            {
                Logger.Debug("Usage heartbeat skipped: {0}", e.Message);
            }
        }
    }
}
