using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    public interface IDiscordWebhookService
    {
        void SendServerStarted(string serverName);
        void SendServerStopped(string serverName);
        void SendServerCrashed(string serverName, bool willRestart, int restartDelay);
        void SendPlayerJoined(string playerName, string serverName);
        void SendPlayerLeft(string playerName, string serverName);
        void SendLaunchHeld(string serverName, string reason);
        void SendServerUpdated(string serverName, string buildId, bool starting);
        void SendServerUpdateFailed(string serverName, string reason);
        void SendLegacyWorldLoaded(string serverName, string worldName);
    }

    public class DiscordWebhookService : IDiscordWebhookService
    {
        private readonly IUserPreferencesProvider UserPrefsProvider;
        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IApplicationLogger Logger;

        public DiscordWebhookService(
            IUserPreferencesProvider userPrefsProvider,
            IHttpClientProvider httpClientProvider,
            IApplicationLogger appLogger)
        {
            UserPrefsProvider = userPrefsProvider;
            HttpClientProvider = httpClientProvider;
            Logger = appLogger;
        }

        public void SendServerStarted(string serverName)
        {
            SendEmbed(
                HostCatalog.T("host.discord.started.title"),
                HostCatalog.T("host.discord.started.body", ("server", serverName)),
                0x57F287); // Green
        }

        public void SendServerStopped(string serverName)
        {
            SendEmbed(
                HostCatalog.T("host.discord.stopped.title"),
                HostCatalog.T("host.discord.stopped.body", ("server", serverName)),
                0x95A5A6); // Gray
        }

        public void SendServerCrashed(string serverName, bool willRestart, int restartDelay)
        {
            var description = willRestart
                ? HostCatalog.T(
                    "host.discord.crashed.body_restarting", ("server", serverName), ("seconds", restartDelay))
                : HostCatalog.T("host.discord.crashed.body", ("server", serverName));
            SendEmbed(HostCatalog.T("host.discord.crashed.title"), description, 0xED4245); // Red
        }

        public void SendPlayerJoined(string playerName, string serverName)
        {
            SendEmbed(
                HostCatalog.T("host.discord.joined.title"),
                HostCatalog.T("host.discord.joined.body", ("player", playerName), ("server", serverName)),
                0x3498DB); // Blue
        }

        public void SendPlayerLeft(string playerName, string serverName)
        {
            SendEmbed(
                HostCatalog.T("host.discord.left.title"),
                HostCatalog.T("host.discord.left.body", ("player", playerName), ("server", serverName)),
                0xF39C12); // Orange
        }

        /// <summary>
        /// The server did NOT come back up because the build on disk is not the one it last
        /// ran, or a Steam update is waiting. Someone has to make that call, so say so.
        /// </summary>
        public void SendLaunchHeld(string serverName, string reason)
        {
            SendEmbed(
                HostCatalog.T("host.discord.held.title"),
                HostCatalog.T("host.discord.held.body", ("server", serverName), ("reason", reason)),
                0xE0A35C); // Amber
        }

        /// <summary>
        /// The dedicated server was updated from inside BakaLoader. Mirrors
        /// <see cref="SendLaunchHeld"/>: same gating, same shape, one post per outcome.
        /// </summary>
        public void SendServerUpdated(string serverName, string buildId, bool starting)
        {
            var build = string.IsNullOrWhiteSpace(buildId)
                ? ""
                : " " + HostCatalog.T("host.discord.updated.build", ("build", buildId));
            var next = " " + (starting
                ? HostCatalog.T("host.discord.updated.starting")
                : HostCatalog.T("host.discord.updated.when_ready"));
            SendEmbed(
                HostCatalog.T("host.discord.updated.title"),
                HostCatalog.T("host.discord.updated.body", ("server", serverName)) + build + next,
                0x57F287); // Green
        }

        /// <summary>The update did not finish, so the server is still on the old build.</summary>
        public void SendServerUpdateFailed(string serverName, string reason)
        {
            SendEmbed(
                HostCatalog.T("host.discord.update_failed.title"),
                HostCatalog.T("host.discord.update_failed.body", ("server", serverName), ("reason", reason)),
                0xED4245); // Red
        }

        public void SendLegacyWorldLoaded(string serverName, string worldName)
        {
            SendEmbed(
                HostCatalog.T("host.discord.legacy_world.title"),
                HostCatalog.T("host.discord.legacy_world.body", ("server", serverName), ("world", worldName)),
                0xE0A35C); // Amber
        }

        private void SendEmbed(string title, string description, int color)
        {
            var prefs = UserPrefsProvider.LoadPreferences();
            var webhookUrl = prefs?.DiscordWebhookUrl;
            if (string.IsNullOrWhiteSpace(webhookUrl)) return;

            var threadId = prefs?.DiscordWebhookThreadId;
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                var separator = webhookUrl.Contains("?") ? "&" : "?";
                webhookUrl = $"{webhookUrl}{separator}thread_id={threadId}";
            }

            var payload = new
            {
                embeds = new[]
                {
                    new
                    {
                        title,
                        description,
                        color,
                        timestamp = DateTime.UtcNow.ToString("o"),
                        footer = new { text = "ValheimBakaLoader" }
                    }
                }
            };

            Task.Run(async () =>
            {
                try
                {
                    using var client = HttpClientProvider.CreateClient();
                    var json = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(webhookUrl, content);

                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.Warning("Discord webhook failed: {statusCode}", response.StatusCode);
                    }
                }
                catch (Exception e)
                {
                    Logger.Warning("Discord webhook error: {message}", e.Message);
                }
            });
        }
    }
}
