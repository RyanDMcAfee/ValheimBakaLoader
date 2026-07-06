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
            SendEmbed("Server Started", $"**{serverName}** is now online.", 0x57F287); // Green
        }

        public void SendServerStopped(string serverName)
        {
            SendEmbed("Server Stopped", $"**{serverName}** has been shut down.", 0x95A5A6); // Gray
        }

        public void SendServerCrashed(string serverName, bool willRestart, int restartDelay)
        {
            var description = willRestart
                ? $"**{serverName}** has crashed! Auto-restarting in {restartDelay} seconds..."
                : $"**{serverName}** has crashed!";
            SendEmbed("Server Crashed", description, 0xED4245); // Red
        }

        public void SendPlayerJoined(string playerName, string serverName)
        {
            SendEmbed("Player Joined", $"**{playerName}** joined **{serverName}**.", 0x3498DB); // Blue
        }

        public void SendPlayerLeft(string playerName, string serverName)
        {
            SendEmbed("Player Left", $"**{playerName}** left **{serverName}**.", 0xF39C12); // Orange
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
