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
        public const string PlayStation = "PlayStation";
        public const string Nintendo = "Nintendo";
        public const string GameCenter = "GameCenter";
        public const string PlayFab = "PlayFab";

        /// <summary>
        /// Normalizes a platform token from the server log to its canonical
        /// casing.
        ///
        /// Valheim 1.0 no longer has a fixed platform list: the platform is a
        /// plain string, and the game also prints a one-letter form of it
        /// (V, X, S, N, A) in some places. So the only token this rejects is an
        /// empty one; anything unrecognised is kept exactly as the server wrote
        /// it, which means a player on a platform we have never seen still gets
        /// tracked instead of being silently dropped.
        /// </summary>
        public static bool TryGetValidPlatform(string input, out string platform)
        {
            var token = input?.Trim();

            if (string.IsNullOrEmpty(token))
            {
                platform = null;
                return false;
            }

            platform = token.ToLowerInvariant() switch
            {
                "steam" or "v" => Steam,
                "xbox" or "x" => Xbox,
                "playstation" or "psn" or "s" => PlayStation,
                "nintendo" or "switch" or "n" => Nintendo,
                "gamecenter" or "a" => GameCenter,
                "playfab" => PlayFab,
                _ => token,
            };

            return true;
        }

        /// <summary>
        /// The id the SERVER knows a connected player by, written the one way that works on
        /// every server: the platform and the player id joined with an underscore.
        /// <para>
        /// The game's own kick resolves the text it is given by reading it as a platform user
        /// id first. On a Steam only server it then looks the peer up by the id alone, because
        /// a Steam socket answers its host name as the bare steamid64; on a crossplay server it
        /// looks it up by the whole "Platform_id", because a PlayFab socket answers its host
        /// name as the whole thing. "Platform_id" satisfies both, and only when that finds
        /// nobody does the game fall back to comparing player NAMES.
        /// </para>
        /// <para>
        /// That fallback is the bug this exists for: a name the app had read in the wrong
        /// encoding matched nobody, so the kick reached no one while the player stood there. An
        /// id is ASCII, is learned from a different log line, and cannot be spelled wrong.
        /// </para>
        /// <para>
        /// Null when either half is missing or carries whitespace, since the command is one
        /// line and the caller has the player's name to fall back to.
        /// </para>
        /// </summary>
        public static string HostId(string platform, string playerId)
        {
            if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(playerId)) return null;

            var left = platform.Trim();
            var right = playerId.Trim();

            foreach (var part in new[] { left, right })
            {
                foreach (var c in part)
                {
                    if (char.IsWhiteSpace(c)) return null;
                }
            }

            return left + "_" + right;
        }

        /// <summary>
        /// The label to show a human for a platform token. Unknown tokens come
        /// back as written so the UI still says something useful.
        /// </summary>
        public static string DisplayName(string platform)
        {
            if (!TryGetValidPlatform(platform, out var canonical)) return string.Empty;

            return canonical switch
            {
                Steam => "Steam",
                Xbox => "Xbox",
                PlayStation => "PlayStation",
                Nintendo => "Nintendo Switch",
                GameCenter => "Apple Game Center",
                PlayFab => "Crossplay",
                _ => canonical,
            };
        }
    }
}
