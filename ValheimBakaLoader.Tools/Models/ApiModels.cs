using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace ValheimBakaLoader.Tools.Models
{
    /// <summary>
    /// Diagnostic payload posted to the crash-report endpoint when the app
    /// hits an unhandled exception. Property names are the wire contract.
    /// </summary>
    public class CrashReport
    {
        [JsonProperty("id")] public string CrashReportId { get; set; }
        [JsonProperty("clientCorrelationId")] public string ClientCorrelationId { get; set; }
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("timestamp")] public DateTimeOffset? Timestamp { get; set; }
        [JsonProperty("appVersion")] public string AppVersion { get; set; }
        [JsonProperty("osVersion")] public string OsVersion { get; set; }
        [JsonProperty("dotnetVersion")] public string DotnetVersion { get; set; }
        [JsonProperty("currentCulture")] public string CurrentCulture { get; set; }
        [JsonProperty("currentUiCulture")] public string CurrentUICulture { get; set; }
        [JsonProperty("additionalInfo")] public Dictionary<string, string> AdditionalInfo { get; set; }
        [JsonProperty("logs")] public List<string> Logs { get; set; }
    }

    /// <summary>Error body a remote API returns alongside a non-success status.</summary>
    public class ErrorResponse
    {
        [JsonProperty("message")] public string Message { get; set; }
    }

    /// <summary>Player identity record returned by the remote player-lookup API.</summary>
    public class PlayerInfoResponse
    {
        [JsonProperty("platform")] public string Platform { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
    }

    /// <summary>The player platforms Valheim names in its connection logs.</summary>
    public static class PlayerPlatforms
    {
        public const string Steam = "Steam";
        public const string Xbox = "Xbox";

        /// <summary>
        /// Normalizes a platform token from the server log to its canonical
        /// casing. False when the token isn't a platform we know.
        /// </summary>
        public static bool TryGetValidPlatform(string input, out string platform)
        {
            platform = input?.Trim().ToLowerInvariant() switch
            {
                "steam" => Steam,
                "xbox" => Xbox,
                _ => null,
            };

            return platform != null;
        }
    }
}
