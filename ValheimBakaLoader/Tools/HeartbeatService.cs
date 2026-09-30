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

        /// <summary>
        /// The clocks one beat is held to. Fifteen seconds, the way the old HttpClient timeout
        /// read, but bounding the whole post rather than only the wait for its headers.
        /// </summary>
        private static readonly DownloadBudget BeatBudget = new()
        {
            HeaderTimeout = TimeSpan.FromSeconds(15),
            TotalTimeout = TimeSpan.FromSeconds(15),
            IdleTimeout = TimeSpan.FromSeconds(15),
        };

        private readonly IUserPreferencesProvider UserPrefsProvider;
        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IApplicationLogger Logger;
        private readonly ICommandTally Commands;

        private Timer BeatTimer;

        public Func<bool> ServerRunningProvider { get; set; }

        public HeartbeatService(
            IUserPreferencesProvider userPrefsProvider,
            IHttpClientProvider httpClientProvider,
            IApplicationLogger logger,
            ICommandTally commands = null)
        {
            UserPrefsProvider = userPrefsProvider;
            HttpClientProvider = httpClientProvider;
            Logger = logger;
            Commands = commands;
        }

        public void Start()
        {
            if (BeatTimer != null) return;
            BeatTimer = new Timer(_ => _ = SendBeatAsync(), null, FirstBeatDelay, BeatInterval);
        }

        /// <summary>
        /// One beat, now, for a test. The timer is what drives it in a shipped run; a suite
        /// that had to wait twenty seconds for the first one and five minutes for the next
        /// could not hold the payload's shape at all.
        /// </summary>
        internal Task Beat() => SendBeatAsync();

        private async Task SendBeatAsync()
        {
            try
            {
                if (!UserPrefsProvider.LoadPreferences().ShareAnonymousStats) return;

                // Since the last beat the backend took: how many times each host command ran,
                // per server profile, counts only. Left OUT of the payload when nothing was
                // issued, so an idle install sends the same three fields it always did.
                var commands = Commands?.Snapshot();
                var payload = JsonConvert.SerializeObject(commands == null || commands.Count == 0
                    ? new
                    {
                        deviceHash = AssemblyHelper.GetClientCorrelationId(),
                        appVersion = AssemblyHelper.GetApplicationVersion(),
                        serverRunning = ServerRunningProvider?.Invoke() ?? false,
                    }
                    : (object)new
                    {
                        deviceHash = AssemblyHelper.GetClientCorrelationId(),
                        appVersion = AssemblyHelper.GetApplicationVersion(),
                        serverRunning = ServerRunningProvider?.Invoke() ?? false,
                        commands,
                    });

                using var client = HttpClientProvider.CreateClient();
                // A budget's clocks, not HttpClient.Timeout. The old fifteen second timeout is
                // released the moment the headers are in, so a backend that accepted the
                // connection and then went quiet held this beat for the life of the process
                // while the five minute timer kept adding more of them. The herald's post
                // carries the same shape, with twenty seconds instead of fifteen.
                var budget = BeatBudget.Copy();
                BoundedDownload.Unbounded(client);
                using var deadline = BoundedDownload.Deadline(budget.TotalTimeout, CancellationToken.None);
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(HeartbeatUrl, content, deadline.Token);

                // Only a beat the backend TOOK clears the counts, and it clears exactly what
                // was sent: a command issued while this post was in flight is still on the
                // tally and rides the next one. A beat that failed leaves everything, so a
                // backend that is down for an hour costs nothing but one payload's worth of
                // combining.
                if (response.IsSuccessStatusCode) Commands?.Forget(commands);
            }
            catch (Exception e)
            {
                // The type and the innermost reason, not the clock's own sentence about
                // itself, which is what the required-mod install and the herald's post both
                // write too.
                Logger.Debug(e, "Usage heartbeat skipped: {0}", Http.WireTrace.Innermost(e));
            }
        }
    }
}
