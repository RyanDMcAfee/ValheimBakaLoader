using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Client for the optional companion web API (player-name lookups and
    /// crash reports). When no API URL is configured, every call is a no-op.
    /// </summary>
    public interface IRemoteApiClient
    {
        event EventHandler<PlayerInfoResponse> PlayerInfoAvailable;

        Task RequestPlayerInfoAsync(string platform, string playerId);

        Task SendCrashReportAsync(CrashReport report);
    }

    public class RemoteApiClient : RestClient, IRemoteApiClient
    {
        public RemoteApiClient(IRestClientContext context) : base(context)
        {
        }

        public event EventHandler<PlayerInfoResponse> PlayerInfoAvailable;

        private static bool Configured => !string.IsNullOrWhiteSpace(Resources.UrlRemoteApi);

        public async Task RequestPlayerInfoAsync(string platform, string playerId)
        {
            if (!Configured) return;

            var request = WithApiKey(Get($"{Resources.UrlRemoteApi}/player-info?platform={platform}&playerId={playerId}"));
            var info = await request.SendAsync<PlayerInfoResponse>();

            if (info == null)
            {
                Logger.Error($"Player-info lookup came back empty ({platform}/{playerId})");
                return;
            }

            PlayerInfoAvailable?.Invoke(this, info);
        }

        public async Task SendCrashReportAsync(CrashReport report)
        {
            if (!Configured) return;

            var request = WithApiKey(Post($"{Resources.UrlRemoteApi}/crash-report", report));
            var response = await request.SendAsync();

            if (response != null && response.IsSuccessStatusCode) return;

            throw new Exception(await DescribeFailure(response));
        }

        /// <summary>Stamps the shared client API key onto an outgoing call.</summary>
        private static ApiCall WithApiKey(ApiCall call)
            => call.WithHeader(ClientSecrets.RemoteApiKeyHeader, ClientSecrets.RemoteClientApiKey);

        private static async Task<string> DescribeFailure(HttpResponseMessage response)
        {
            if (response == null) return "Unable to reach the remote API";

            try
            {
                var body = await response.Content.ReadAsStringAsync();
                var error = JsonConvert.DeserializeObject<ErrorResponse>(body);
                return $"({(int)response.StatusCode}) {error.Message}";
            }
            catch
            {
                return "Unknown error";
            }
        }
    }
}
