using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// Everything the Herald shares about one realm's server. Built fresh by the main window
    /// right before each Discord publish of that realm's post.
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
        /// <summary>Set by the main window; produces the named realm's snapshot on demand.</summary>
        Func<string, DiscordStatusSnapshot> SnapshotProvider { get; set; }

        /// <summary>
        /// Debounced update of one realm's post - safe to call from any event handler, any
        /// thread, as often as you like. Coalesces bursts into at most one Discord edit per
        /// cooldown window for that realm. No-ops when the master switch or the realm's own
        /// switch is off, or when the realm has nowhere to post.
        /// </summary>
        void RequestUpdate(string realm);

        /// <summary>The same for every realm that shares: for a change that shows on every post.</summary>
        void RequestUpdateAll();

        /// <summary>Creates the realm's status post if it has none, otherwise edits it in place.</summary>
        Task<DiscordStatusResult> PublishNowAsync(string realm);

        /// <summary>Checks a webhook URL is real by GETting its metadata. Sends nothing.</summary>
        Task<DiscordStatusResult> ValidateWebhookAsync(string url);

        /// <summary>Deletes the realm's status post from Discord (if one exists) and forgets its id.</summary>
        Task<DiscordStatusResult> RemoveStatusMessageAsync(string realm);
    }

    /// <summary>
    /// The Herald: maintains ONE Discord message per realm - created once via that realm's
    /// webhook, then edited in place forever after (PATCH /webhooks/{id}/{token}/messages/{mid}).
    /// It never spams a channel with new posts; the same message always shows the realm's
    /// current status. "Next restart" and "last mod update" use Discord's &lt;t:unix:R&gt;
    /// timestamp markdown, which renders as a live relative time on every viewer's client - so
    /// the post stays truthful between edits without us re-posting.
    /// <para>
    /// Every realm has its own debounce timer, cooldown and lock (1.2.16, issue 24). Two realms
    /// running side by side used to share one post, and whichever realm the snapshot happened to
    /// pick won it; now each keeps its own and neither can hold the other's edit back.
    /// </para>
    /// </summary>
    public class DiscordStatusService : IDiscordStatusService
    {
        // Accept the URL Discord's "Copy Webhook URL" button produces (all release channels,
        // with or without an /api/vNN version segment).
        private static readonly Regex WebhookUrlPattern = new(
            @"^https://((ptb|canary)\.)?discord(app)?\.com/api/(v\d+/)?webhooks/\d+/[A-Za-z0-9_\-]+$",
            RegexOptions.Compiled);

        /// <summary>Whether a box holds a Discord webhook link, by the same pattern the checks use.</summary>
        internal static bool LooksLikeWebhook(string url) => url != null && WebhookUrlPattern.IsMatch(url.Trim());

        private static readonly TimeSpan EditCooldown = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CoalesceDelay = TimeSpan.FromSeconds(2);

        // The longest a rate limit answer is waited out before the one retry. Discord's
        // per-webhook limit asks for a second or two; an answer asking for longer than this is
        // a limit the next scheduled edit will meet anyway.
        private static readonly TimeSpan LongestRetryWait = TimeSpan.FromSeconds(30);

        private readonly IUserPreferencesProvider UserPrefsProvider;
        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IApplicationLogger Logger;

        /// <summary>One realm's post: its lock, its timer and when it was last edited.</summary>
        private sealed class RealmPost
        {
            public readonly SemaphoreSlim Lock = new(1, 1);
            public Timer Debounce;
            public DateTime LastEditUtc = DateTime.MinValue;
        }

        private readonly ConcurrentDictionary<string, RealmPost> Posts = new(StringComparer.OrdinalIgnoreCase);
        private readonly object TimerLock = new();

        public Func<string, DiscordStatusSnapshot> SnapshotProvider { get; set; }

        /// <summary>
        /// How a rate limit answer is waited out. Task.Delay in the app; a test hands in one that
        /// records the wait and returns at once.
        /// </summary>
        internal Func<TimeSpan, Task> WaitOut { get; set; } = Task.Delay;

        public DiscordStatusService(
            IUserPreferencesProvider userPrefsProvider,
            IHttpClientProvider httpClientProvider,
            IApplicationLogger logger)
        {
            UserPrefsProvider = userPrefsProvider;
            HttpClientProvider = httpClientProvider;
            Logger = logger;
        }

        private RealmPost PostOf(string realm) => Posts.GetOrAdd(realm, _ => new RealmPost());

        public void RequestUpdate(string realm)
        {
            if (string.IsNullOrWhiteSpace(realm)) return;
            var prefs = UserPrefsProvider.LoadPreferences();
            if (!HeraldRealms.Shares(prefs, HeraldRealms.Find(prefs, realm))) return;
            Schedule(realm);
        }

        public void RequestUpdateAll()
        {
            var prefs = UserPrefsProvider.LoadPreferences();
            if (prefs == null || !prefs.DiscordSharingEnabled) return;
            foreach (var realm in (prefs.Servers ?? new List<ServerPreferences>())
                         .Where(r => r != null && !r.Archived && HeraldRealms.Shares(prefs, r))
                         .Select(r => r.ProfileName)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList())
                Schedule(realm);
        }

        private void Schedule(string realm)
        {
            var post = PostOf(realm);
            lock (TimerLock)
            {
                if (post.Debounce != null) return; // an update is already scheduled - it will pick up the latest snapshot

                var sinceLast = DateTime.UtcNow - post.LastEditUtc;
                var wait = sinceLast >= EditCooldown ? CoalesceDelay : EditCooldown - sinceLast;

                // A timer carries the caller's execution context, and with it the realm scope of
                // whichever command asked for this update. The herald's lines name the realm
                // they are about in their own words and belong to no realm's Saga: the timer
                // starts with none.
                using (ExecutionContext.SuppressFlow())
                {
                    post.Debounce = new Timer(_ =>
                    {
                        lock (TimerLock)
                        {
                            post.Debounce?.Dispose();
                            post.Debounce = null;
                        }
                        _ = PublishAsync(realm, scheduled: true);
                    }, null, wait, Timeout.InfiniteTimeSpan);
                }
            }
        }

        public Task<DiscordStatusResult> PublishNowAsync(string realm) => PublishAsync(realm, scheduled: false);

        /// <summary>What a realm's debounce timer runs when it fires, for the tests to run without the wait.</summary>
        internal Task<DiscordStatusResult> PublishScheduledAsync(string realm) => PublishAsync(realm, scheduled: true);

        /// <summary>Whether an update of the realm's post is waiting on its timer.</summary>
        internal bool HasPendingUpdate(string realm)
        {
            lock (TimerLock) return realm != null && Posts.TryGetValue(realm, out var post) && post.Debounce != null;
        }

        /// <summary>
        /// One realm's publish. A scheduled one asks the switches again when its timer fires,
        /// because the host can turn sharing off in the two seconds between the request and the
        /// edit, and off means nothing is sent. A press of Publish turns both switches on before
        /// it calls, so it does not ask.
        /// </summary>
        private async Task<DiscordStatusResult> PublishAsync(string realm, bool scheduled)
        {
            if (string.IsNullOrWhiteSpace(realm))
                return new DiscordStatusResult { Error = "No realm was named for the post." };

            var post = PostOf(realm);
            await post.Lock.WaitAsync();
            try
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                var server = HeraldRealms.Find(prefs, realm);
                if (server == null)
                    return new DiscordStatusResult { Error = "That realm is gone, so nothing was sent." };
                if (scheduled && !HeraldRealms.Shares(prefs, server))
                    return new DiscordStatusResult { Ok = true, Detail = "not shared" };

                var webhookUrl = HeraldRealms.WebhookUrlFor(prefs, server);
                if (string.IsNullOrWhiteSpace(webhookUrl))
                    return new DiscordStatusResult { Error = "No webhook URL is configured." };
                if (!WebhookUrlPattern.IsMatch(webhookUrl.Trim()))
                    return new DiscordStatusResult { Error = "That does not look like a Discord webhook URL." };
                webhookUrl = webhookUrl.Trim();

                var snapshot = SnapshotProvider?.Invoke(server.ProfileName);
                if (snapshot == null)
                    return new DiscordStatusResult { Error = "No server snapshot is available yet." };

                var payload = BuildEmbedPayload(snapshot, prefs, server);
                var threadQs = BuildThreadQuery(HeraldRealms.ThreadIdFor(prefs, server));

                using var client = HttpClientProvider.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                // Discord fixes a webhook post's name and picture when the post is made, and an
                // edit carries neither, so a post signed differently from the look the host has
                // chosen now could only ever keep the old one. That post is taken down and made
                // again, signed as it should be; one that cannot be taken down is left alone and
                // edited as before, because a second post beside it is the one thing this
                // service must never do.
                var messageId = server.HeraldStatusMessageId;
                var signature = DiscordHeraldLook.SignatureFor(prefs);
                if (!string.IsNullOrWhiteSpace(messageId)
                    && !string.Equals(server.HeraldStatusMessageSignature, signature, StringComparison.Ordinal))
                {
                    var deleteUrl = $"{webhookUrl}/messages/{messageId}{threadQs}";
                    using var deleteResponse = await SendAsync(() => client.DeleteAsync(deleteUrl), realm);
                    if (deleteResponse.IsSuccessStatusCode || deleteResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        Logger.Information("Discord status post of {realm} signed with an older name or picture; making it again.", realm);
                        SaveMessageId(realm, null, null);
                        messageId = null;
                    }
                    else
                    {
                        Logger.Warning("Discord status post of {realm} could not be taken down to change its name or picture: {status}", realm, deleteResponse.StatusCode);
                    }
                }

                // Edit the existing post in place when we have one...
                if (!string.IsNullOrWhiteSpace(messageId))
                {
                    var editUrl = $"{webhookUrl}/messages/{messageId}{threadQs}";
                    var editBody = ForEdit(payload);
                    using var editResponse = await SendAsync(
                        () => client.PatchAsync(editUrl, new StringContent(editBody, Encoding.UTF8, "application/json")), realm);

                    if (editResponse.IsSuccessStatusCode)
                    {
                        post.LastEditUtc = DateTime.UtcNow;
                        return new DiscordStatusResult { Ok = true, Detail = "updated" };
                    }

                    // The post was deleted on the Discord side (or the webhook changed
                    // channels) - forget it and fall through to create a fresh one.
                    if (editResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        Logger.Information("Discord status post of {realm} no longer exists; creating a new one.", realm);
                        SaveMessageId(realm, null, null);
                    }
                    else
                    {
                        var body = await SafeReadAsync(editResponse);
                        Logger.Warning("Discord status edit of {realm} failed: {status} {body}", realm, editResponse.StatusCode, body);
                        return new DiscordStatusResult { Error = $"Discord rejected the edit ({(int)editResponse.StatusCode})." };
                    }
                }

                // ...otherwise create it. ?wait=true makes Discord return the created
                // message (including its id) instead of a fire-and-forget 204.
                var createQs = threadQs.Length > 0 ? threadQs + "&wait=true" : "?wait=true";
                using var createResponse = await SendAsync(
                    () => client.PostAsync(webhookUrl + createQs, new StringContent(payload, Encoding.UTF8, "application/json")), realm);

                if (!createResponse.IsSuccessStatusCode)
                {
                    var body = await SafeReadAsync(createResponse);
                    Logger.Warning("Discord status post of {realm} failed: {status} {body}", realm, createResponse.StatusCode, body);
                    return new DiscordStatusResult { Error = $"Discord rejected the post ({(int)createResponse.StatusCode})." };
                }

                var created = JObject.Parse(await createResponse.Content.ReadAsStringAsync());
                var newId = created.Value<string>("id");
                if (string.IsNullOrWhiteSpace(newId))
                    return new DiscordStatusResult { Error = "Discord did not return the new post's id." };

                SaveMessageId(realm, newId, signature);
                post.LastEditUtc = DateTime.UtcNow;
                return new DiscordStatusResult { Ok = true, Detail = "created" };
            }
            catch (Exception e)
            {
                Logger.Warning("Discord status publish error for {realm}: {message}", realm, e.Message);
                return new DiscordStatusResult { Error = e.Message };
            }
            finally
            {
                post.Lock.Release();
            }
        }

        /// <summary>
        /// One request, with one retry when Discord answers 429. Several realms posting through
        /// one webhook can meet its limit together, and Discord says in the answer how long to
        /// wait. A second 429 is a line in the Saga and the answer as it came, so the caller
        /// reports a refused edit rather than waiting on in silence.
        /// </summary>
        private async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, string realm)
        {
            var first = await send();
            if ((int)first.StatusCode != 429) return first;

            var wait = await RetryAfterAsync(first);
            first.Dispose();
            Logger.Information("Discord asked the status post of {realm} to wait {seconds}s; trying once more.",
                realm, wait.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            await WaitOut(wait);

            var second = await send();
            if ((int)second.StatusCode == 429)
                Logger.Warning("Discord is still rate limiting the status post of {realm} after one wait; this edit is left for the next one.", realm);
            return second;
        }

        /// <summary>
        /// How long a 429 asks for: the body's retry_after in seconds, else the Retry-After
        /// header, else a second. Never less than nothing and never more than the longest wait.
        /// </summary>
        internal static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response)
        {
            double? seconds = null;
            try
            {
                var body = response.Content == null ? null : await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var token = JObject.Parse(body)["retry_after"];
                    if (token != null && token.Type is JTokenType.Float or JTokenType.Integer)
                        seconds = token.Value<double>();
                }
            }
            catch { /* a body that is not JSON leaves the header to say it */ }

            if (seconds == null && response.Headers.RetryAfter?.Delta is TimeSpan delta)
                seconds = delta.TotalSeconds;

            var wait = TimeSpan.FromSeconds(seconds ?? 1);
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            return wait > LongestRetryWait ? LongestRetryWait : wait;
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

        public async Task<DiscordStatusResult> RemoveStatusMessageAsync(string realm)
        {
            if (string.IsNullOrWhiteSpace(realm))
                return new DiscordStatusResult { Ok = true, Detail = "nothing to remove" };

            var post = PostOf(realm);
            await post.Lock.WaitAsync();
            try
            {
                var prefs = UserPrefsProvider.LoadPreferences();
                var server = HeraldRealms.Find(prefs, realm);
                var messageId = server?.HeraldStatusMessageId;
                var webhookUrl = HeraldRealms.WebhookUrlFor(prefs, server)?.Trim();

                if (string.IsNullOrWhiteSpace(messageId))
                    return new DiscordStatusResult { Ok = true, Detail = "nothing to remove" };

                var gone = false;
                if (!string.IsNullOrWhiteSpace(webhookUrl) && WebhookUrlPattern.IsMatch(webhookUrl))
                {
                    try
                    {
                        using var client = HttpClientProvider.CreateClient();
                        client.Timeout = TimeSpan.FromSeconds(15);
                        var deleteUrl = $"{webhookUrl}/messages/{messageId}{BuildThreadQuery(HeraldRealms.ThreadIdFor(prefs, server))}";
                        using var response = await SendAsync(() => client.DeleteAsync(deleteUrl), realm);
                        // 404 just means it is already gone - either way we forget the id below.
                        gone = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
                        if (!gone)
                            Logger.Warning("Discord status post of {realm} could not be taken down: {status}", realm, response.StatusCode);
                    }
                    catch (Exception e)
                    {
                        Logger.Warning("Discord status post of {realm} could not be taken down: {message}", realm, e.Message);
                    }
                }

                SaveMessageId(realm, null, null);
                return new DiscordStatusResult { Ok = true, Detail = gone ? "removed" : "forgotten" };
            }
            finally
            {
                post.Lock.Release();
            }
        }

        private void SaveMessageId(string realm, string id, string signature)
        {
            // This runs on the publish timer's thread while the server's own thread can be
            // recording a launched build into the same document. Both write the WHOLE of
            // userprefs.json, so the load and the write belong under the one gate: without it the
            // later of the two put the other's change back the way it was, and a profile that lost
            // its launch history got asked about a build change that had already happened. A
            // profile save that loaded the realm before this landed keeps it as well: the section
            // writer puts the stored post back onto any profile it replaces.
            UserPrefsProvider.Mutate(prefs =>
            {
                var server = HeraldRealms.Find(prefs, realm);
                if (server == null) return false;
                server.HeraldStatusMessageId = id;
                server.HeraldStatusMessageSignature = signature;
                return true;
            });
        }

        private static string BuildThreadQuery(string threadId)
            => string.IsNullOrWhiteSpace(threadId) ? "" : $"?thread_id={Uri.EscapeDataString(threadId.Trim())}";

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

        /// <summary>
        /// One realm's post. The realm decides what it carries (the address, the password, its
        /// title and its status line); the app decides how it is signed.
        /// </summary>
        internal static string BuildEmbedPayload(DiscordStatusSnapshot s, UserPreferences prefs, ServerPreferences realm)
        {
            realm ??= new ServerPreferences();

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

            if (realm.HeraldShareAddress)
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

            if (realm.HeraldSharePassword && !string.IsNullOrWhiteSpace(s.Password))
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
                ["title"] = DiscordHeraldLook.TitleFor(realm, serverTitle),
            };
            var statusLine = DiscordHeraldLook.StatusLineFor(realm);
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
