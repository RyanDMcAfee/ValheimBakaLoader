using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Everything the Herald shares about the (single) server it is announcing.
    /// Built fresh by the main window right before each Discord publish.
    /// </summary>
    public class DiscordStatusSnapshot
    {
        public string ServerName { get; set; }
        public string WorldName { get; set; }
        public string StatusText { get; set; }
        public bool ServerRunning { get; set; }
        public bool ServerStarting { get; set; }
        public string AddressText { get; set; }
        public string Password { get; set; }
        public int PlayersOnline { get; set; }
        public int ModCount { get; set; }
        public DateTime? LastModUpdateUtc { get; set; }
        public DateTime? NextRestartUtc { get; set; }
        public string AppVersion { get; set; }

        /// <summary>The realm's mod manager: none, thunderstore or gale (<see cref="ModProfileCodes"/>).</summary>
        public string ModProfileCodeKind { get; set; }

        /// <summary>The realm's mod profile code, or null for no line in the post.</summary>
        public string ModProfileCode { get; set; }
    }

    public class DiscordStatusResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        /// <summary>Extra success info - e.g. the webhook's name on validate.</summary>
        public string Detail { get; set; }
    }

    public interface IDiscordStatusService
    {
        /// <summary>Set by the main window; produces the current server snapshot on demand.</summary>
        Func<DiscordStatusSnapshot> SnapshotProvider { get; set; }

        /// <summary>
        /// Debounced update request - safe to call from any event handler, any thread,
        /// as often as you like. Coalesces bursts into at most one Discord edit per
        /// cooldown window. No-ops when Discord sharing is disabled or unconfigured.
        /// </summary>
        void RequestUpdate();

        /// <summary>Creates the status post if none exists, otherwise edits it in place.</summary>
        Task<DiscordStatusResult> PublishNowAsync();

        /// <summary>Checks a webhook URL is real by GETting its metadata. Sends nothing.</summary>
        Task<DiscordStatusResult> ValidateWebhookAsync(string url);

        /// <summary>Deletes the status post from Discord (if one exists) and forgets its id.</summary>
        Task<DiscordStatusResult> RemoveStatusMessageAsync();
    }

    /// <summary>
    /// The Herald: maintains ONE Discord message - created once via the configured
    /// webhook, then edited in place forever after (PATCH /webhooks/{id}/{token}/messages/{mid}).
    /// It never spams a channel with new posts; the same message always shows the
    /// current server status. "Next restart" and "last mod update" use Discord's
    /// &lt;t:unix:R&gt; timestamp markdown, which renders as a live relative time on every
    /// viewer's client - so the post stays truthful between edits without us re-posting.
    /// </summary>
    public class DiscordStatusService : IDiscordStatusService
    {
        // Accept the URL Discord's "Copy Webhook URL" button produces (all release channels,
        // with or without an /api/vNN version segment).
        private static readonly Regex WebhookUrlPattern = new(
            @"^https://((ptb|canary)\.)?discord(app)?\.com/api/(v\d+/)?webhooks/\d+/[A-Za-z0-9_\-]+$",
            RegexOptions.Compiled);

        private static readonly TimeSpan EditCooldown = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CoalesceDelay = TimeSpan.FromSeconds(2);

        private readonly IUserPreferencesProvider UserPrefsProvider;
        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IApplicationLogger Logger;

        private readonly SemaphoreSlim PublishLock = new(1, 1);
        private readonly object TimerLock = new();
        private Timer DebounceTimer;
        private DateTime LastEditUtc = DateTime.MinValue;

        public Func<DiscordStatusSnapshot> SnapshotProvider { get; set; }

        public DiscordStatusService(
            IUserPreferencesProvider userPrefsProvider,
            IHttpClientProvider httpClientProvider,
            IApplicationLogger logger)
        {
            UserPrefsProvider = userPrefsProvider;
            HttpClientProvider = httpClientProvider;
            Logger = logger;
        }

        public void RequestUpdate()
        {
            var prefs = UserPrefsProvider.LoadPreferences();
            if (!prefs.DiscordSharingEnabled || string.IsNullOrWhiteSpace(prefs.DiscordWebhookUrl)) return;

            lock (TimerLock)
            {
                if (DebounceTimer != null) return; // an update is already scheduled - it will pick up the latest snapshot

                var sinceLast = DateTime.UtcNow - LastEditUtc;
                var wait = sinceLast >= EditCooldown ? CoalesceDelay : EditCooldown - sinceLast;

                // A timer carries the caller's execution context, and with it the realm scope of
                // whichever command asked for this update. The herald is the whole host's, so its
                // lines must not land in that one realm's Saga: the timer starts with none.
                using (ExecutionContext.SuppressFlow())
                {
                    DebounceTimer = new Timer(_ =>
                    {
                        lock (TimerLock)
                        {
                            DebounceTimer?.Dispose();
                            DebounceTimer = null;
                        }
                        _ = PublishNowAsync();
                    }, null, wait, Timeout.InfiniteTimeSpan);
                }
            }
        }

        public async Task<DiscordStatusResult> PublishNowAsync()
        {
            await PublishLock.WaitAsync();
            try
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                var webhookUrl = prefs?.DiscordWebhookUrl;
                if (string.IsNullOrWhiteSpace(webhookUrl))
                    return new DiscordStatusResult { Error = "No webhook URL is configured." };
                if (!WebhookUrlPattern.IsMatch(webhookUrl.Trim()))
                    return new DiscordStatusResult { Error = "That does not look like a Discord webhook URL." };
                webhookUrl = webhookUrl.Trim();

                var snapshot = SnapshotProvider?.Invoke();
                if (snapshot == null)
                    return new DiscordStatusResult { Error = "No server snapshot is available yet." };

                var payload = BuildEmbedPayload(snapshot, prefs);
                var threadQs = BuildThreadQuery(prefs);

                using var client = HttpClientProvider.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                // Discord fixes a webhook post's name and picture when the post is made, and an
                // edit carries neither, so a post signed differently from the look the host has
                // chosen now could only ever keep the old one. That post is taken down and made
                // again, signed as it should be; one that cannot be taken down is left alone and
                // edited as before, because a second post beside it is the one thing this
                // service must never do.
                var messageId = prefs.DiscordStatusMessageId;
                var signature = DiscordHeraldLook.SignatureFor(prefs);
                if (!string.IsNullOrWhiteSpace(messageId)
                    && !string.Equals(prefs.DiscordStatusMessageSignature, signature, StringComparison.Ordinal))
                {
                    var deleteUrl = $"{webhookUrl}/messages/{messageId}{threadQs}";
                    using var deleteResponse = await client.DeleteAsync(deleteUrl);
                    if (deleteResponse.IsSuccessStatusCode || deleteResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        Logger.Information("Discord status post signed with an older name or picture; making it again.");
                        SaveMessageId(null, null);
                        messageId = null;
                    }
                    else
                    {
                        Logger.Warning("Discord status post could not be taken down to change its name or picture: {status}", deleteResponse.StatusCode);
                    }
                }

                // Edit the existing post in place when we have one...
                if (!string.IsNullOrWhiteSpace(messageId))
                {
                    var editUrl = $"{webhookUrl}/messages/{messageId}{threadQs}";
                    using var editContent = new StringContent(ForEdit(payload), Encoding.UTF8, "application/json");
                    using var editResponse = await client.PatchAsync(editUrl, editContent);

                    if (editResponse.IsSuccessStatusCode)
                    {
                        LastEditUtc = DateTime.UtcNow;
                        return new DiscordStatusResult { Ok = true, Detail = "updated" };
                    }

                    // The post was deleted on the Discord side (or the webhook changed
                    // channels) - forget it and fall through to create a fresh one.
                    if (editResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        Logger.Information("Discord status post no longer exists; creating a new one.");
                        SaveMessageId(null, null);
                    }
                    else
                    {
                        var body = await SafeReadAsync(editResponse);
                        Logger.Warning("Discord status edit failed: {status} {body}", editResponse.StatusCode, body);
                        return new DiscordStatusResult { Error = $"Discord rejected the edit ({(int)editResponse.StatusCode})." };
                    }
                }

                // ...otherwise create it. ?wait=true makes Discord return the created
                // message (including its id) instead of a fire-and-forget 204.
                var createQs = threadQs.Length > 0 ? threadQs + "&wait=true" : "?wait=true";
                using var createContent = new StringContent(payload, Encoding.UTF8, "application/json");
                using var createResponse = await client.PostAsync(webhookUrl + createQs, createContent);

                if (!createResponse.IsSuccessStatusCode)
                {
                    var body = await SafeReadAsync(createResponse);
                    Logger.Warning("Discord status post failed: {status} {body}", createResponse.StatusCode, body);
                    return new DiscordStatusResult { Error = $"Discord rejected the post ({(int)createResponse.StatusCode})." };
                }

                var created = JObject.Parse(await createResponse.Content.ReadAsStringAsync());
                var newId = created.Value<string>("id");
                if (string.IsNullOrWhiteSpace(newId))
                    return new DiscordStatusResult { Error = "Discord did not return the new post's id." };

                SaveMessageId(newId, signature);
                LastEditUtc = DateTime.UtcNow;
                return new DiscordStatusResult { Ok = true, Detail = "created" };
            }
            catch (Exception e)
            {
                Logger.Warning("Discord status publish error: {message}", e.Message);
                return new DiscordStatusResult { Error = e.Message };
            }
            finally
            {
                PublishLock.Release();
            }
        }

        public async Task<DiscordStatusResult> ValidateWebhookAsync(string url)
        {
            try
            {
                url = url?.Trim();
                if (string.IsNullOrWhiteSpace(url))
                    return new DiscordStatusResult { Error = "Paste the webhook URL first." };
                if (!WebhookUrlPattern.IsMatch(url))
                    return new DiscordStatusResult { Error = "That does not look like a Discord webhook URL. It should start with https://discord.com/api/webhooks/" };

                using var client = HttpClientProvider.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var response = await client.GetAsync(url);

                if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Unauthorized)
                    return new DiscordStatusResult { Error = "Discord says that webhook does not exist. It may have been deleted, so create a new one and paste the fresh URL." };
                if (!response.IsSuccessStatusCode)
                    return new DiscordStatusResult { Error = $"Discord answered {(int)response.StatusCode}. Try again in a moment." };

                var info = JObject.Parse(await response.Content.ReadAsStringAsync());
                return new DiscordStatusResult { Ok = true, Detail = info.Value<string>("name") ?? "webhook" };
            }
            catch (Exception e)
            {
                return new DiscordStatusResult { Error = e.Message };
            }
        }

        public async Task<DiscordStatusResult> RemoveStatusMessageAsync()
        {
            await PublishLock.WaitAsync();
            try
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                var messageId = prefs?.DiscordStatusMessageId;
                var webhookUrl = prefs?.DiscordWebhookUrl?.Trim();

                if (string.IsNullOrWhiteSpace(messageId))
                    return new DiscordStatusResult { Ok = true, Detail = "nothing to remove" };

                if (!string.IsNullOrWhiteSpace(webhookUrl) && WebhookUrlPattern.IsMatch(webhookUrl))
                {
                    try
                    {
                        using var client = HttpClientProvider.CreateClient();
                        client.Timeout = TimeSpan.FromSeconds(15);
                        var deleteUrl = $"{webhookUrl}/messages/{messageId}{BuildThreadQuery(prefs)}";
                        using var response = await client.DeleteAsync(deleteUrl);
                        // 404 just means it is already gone - either way we forget the id below.
                    }
                    catch (Exception e)
                    {
                        Logger.Debug("Discord status delete skipped: {0}", e.Message);
                    }
                }

                SaveMessageId(null, null);
                return new DiscordStatusResult { Ok = true, Detail = "removed" };
            }
            finally
            {
                PublishLock.Release();
            }
        }

        private void SaveMessageId(string id, string signature)
        {
            // This runs on the publish timer's thread while the server's own thread can be
            // recording a launched build into the same document. Both write the WHOLE of
            // userprefs.json, so the load and the write belong under the one gate: without it the
            // later of the two put the other's change back the way it was, and a profile that lost
            // its launch history got asked about a build change that had already happened.
            UserPrefsProvider.Mutate(prefs =>
            {
                prefs.DiscordStatusMessageId = id;
                prefs.DiscordStatusMessageSignature = signature;
            });
        }

        private static string BuildThreadQuery(UserPreferences prefs)
        {
            var threadId = prefs?.DiscordWebhookThreadId;
            return string.IsNullOrWhiteSpace(threadId) ? "" : $"?thread_id={Uri.EscapeDataString(threadId.Trim())}";
        }

        /// <summary>
        /// The same post for an edit. Discord's edit takes neither a name nor a picture (both are
        /// fixed when the post is made, which is why a change to either makes the post again),
        /// so they are left out of the edit rather than sent to be ignored.
        /// </summary>
        internal static string ForEdit(string payload)
        {
            var o = JObject.Parse(payload);
            o.Remove("username");
            o.Remove("avatar_url");
            return o.ToString(Formatting.None);
        }

        private static long ToUnix(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

        internal static string BuildEmbedPayload(DiscordStatusSnapshot s, UserPreferences prefs)
        {
            var color = s.ServerRunning ? 0x57F287       // green - up
                : s.ServerStarting ? 0xF39C12            // orange - on the way up/down
                : 0x95A5A6;                              // gray - down

            // Every word in this post is read in a Discord channel by the people who play on
            // the server, so it comes out of the host catalog rather than out of this file. The
            // values that are not words stay where they are: a name the host typed, a count, a
            // Discord timestamp, and the bare hyphen that stands for "nothing here".
            var fields = new List<object>
            {
                new
                {
                    name = HostCatalog.T("host.status.field.status"),
                    value = s.StatusText ?? HostCatalog.T("host.status.value.unknown"),
                    inline = true,
                },
                new
                {
                    name = HostCatalog.T("host.status.field.players"),
                    value = s.ServerRunning ? s.PlayersOnline.ToString() : "-",
                    inline = true,
                },
                new
                {
                    name = HostCatalog.T("host.status.field.world"),
                    value = string.IsNullOrWhiteSpace(s.WorldName) ? "-" : s.WorldName,
                    inline = true,
                },
            };

            if (prefs.DiscordShareAddress)
            {
                fields.Add(new
                {
                    name = HostCatalog.T("host.status.field.address"),
                    value = string.IsNullOrWhiteSpace(s.AddressText) ? "-" : $"`{s.AddressText}`",
                    inline = true,
                });
            }

            fields.Add(new
            {
                name = HostCatalog.T("host.status.field.mods"),
                value = s.ModCount > 0
                    ? HostCatalog.T("host.status.value.mods_installed", ("count", s.ModCount))
                    : HostCatalog.T("host.status.value.vanilla"),
                inline = true,
            });

            // The realm's mod profile code, straight after the mod count it is the other half
            // of. In a code span so a player can copy it in one go, and only when the realm has
            // one: no kind chosen or a blank code is no field at all, not a field saying "-".
            var modCode = ModProfileCodes.Postable(s.ModProfileCodeKind, s.ModProfileCode);
            if (modCode != null)
            {
                fields.Add(new
                {
                    name = ModProfileCodes.NormalizeKind(s.ModProfileCodeKind) == ModProfileCodes.Gale
                        ? HostCatalog.T("host.status.field.mod_code.gale")
                        : HostCatalog.T("host.status.field.mod_code.thunderstore"),
                    value = $"`{modCode}`",
                    inline = true,
                });
            }

            fields.Add(new
            {
                name = HostCatalog.T("host.status.field.last_mod_update"),
                value = s.LastModUpdateUtc.HasValue ? $"<t:{ToUnix(s.LastModUpdateUtc.Value)}:R>" : "-",
                inline = true,
            });

            fields.Add(new
            {
                name = HostCatalog.T("host.status.field.next_restart"),
                value = s.NextRestartUtc.HasValue
                    ? $"<t:{ToUnix(s.NextRestartUtc.Value)}:R>"
                    : HostCatalog.T("host.status.value.not_scheduled"),
                inline = true,
            });

            if (prefs.DiscordSharePassword && !string.IsNullOrWhiteSpace(s.Password))
            {
                fields.Add(new
                {
                    name = HostCatalog.T("host.status.field.password"),
                    value = $"`{s.Password}`",
                    inline = true,
                });
            }

            // The embed and the post around it, built as objects rather than as one anonymous
            // shape, because what the host chose for the look decides which keys are there at
            // all: a blank status line is no description, a blank name is no username, and the
            // switch off is no author line. A key sent empty is not the same as a key not sent.
            var serverTitle = string.IsNullOrWhiteSpace(s.ServerName)
                ? HostCatalog.T("host.status.title")
                : s.ServerName;
            var embed = new JObject
            {
                ["title"] = DiscordHeraldLook.TitleFor(prefs, serverTitle),
            };
            var statusLine = DiscordHeraldLook.StatusLineFor(prefs);
            if (statusLine != null) embed["description"] = statusLine;
            embed["color"] = color;
            embed["fields"] = JArray.FromObject(fields);
            embed["timestamp"] = DateTime.UtcNow.ToString("o");
            embed["footer"] = new JObject
            {
                ["text"] = HostCatalog.T("host.status.footer", ("version", s.AppVersion)),
            };

            var payload = new JObject
            {
                ["embeds"] = new JArray(embed),
                // An empty mention allowlist so nothing in a server name can ever ping anyone.
                ["allowed_mentions"] = new JObject { ["parse"] = new JArray() },
            };
            DiscordHeraldLook.Sign(payload, embed, prefs);

            return payload.ToString(Formatting.None);
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage response)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync();
                return body.Length > 300 ? body[..300] : body;
            }
            catch
            {
                return "";
            }
        }
    }
}
