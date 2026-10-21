using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The one-off event posts. Every one names the realm it is about, and the service decides
    /// whether it goes out and where (see HeraldRealms.SendsEvent and EventRouteFor): the master
    /// switch on, the realm on, the kind of post on, read when the post is sent. The call sites
    /// only say what happened.
    /// </summary>
    public interface IDiscordWebhookService
    {
        void SendServerStarted(string realm, string serverName);
        void SendServerStopped(string realm, string serverName);
        void SendServerCrashed(string realm, string serverName, bool willRestart, int restartDelay);
        void SendPlayerJoined(string realm, string playerName, string serverName);
        void SendPlayerLeft(string realm, string playerName, string serverName);
        void SendLaunchHeld(string realm, string serverName, string reason);
        void SendServerUpdated(string realm, string serverName, string buildId, bool starting);
        void SendServerUpdateFailed(string realm, string serverName, string reason);
        void SendLegacyWorldLoaded(string realm, string serverName, string worldName);
        void SendCheatMarksCleared(string realm, string serverName, string counts);
        void SendModsUpdated(string realm, string serverName, IReadOnlyList<ModVersionMove> mods);
    }

    /// <summary>One mod a mod update moved, for the mods updated post.</summary>
    public sealed record ModVersionMove(string Mod, string FromVersion, string ToVersion);

    public class DiscordWebhookService : IDiscordWebhookService
    {
        private readonly IUserPreferencesProvider UserPrefsProvider;
        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IApplicationLogger Logger;

        /// <summary>
        /// The clocks one webhook post is held to.
        /// <para>
        /// It had none of its own, so it ran on HttpClient's default hundred seconds, and that
        /// default is released the moment the response headers are in: a Discord edge that
        /// accepted the connection and then went quiet held the task and its client for as long
        /// as the process lived. A herald line is worth twenty seconds and not a second more;
        /// the next one will say the same thing.
        /// </para>
        /// </summary>
        public DownloadBudget PostBudget { get; set; } = new()
        {
            HeaderTimeout = TimeSpan.FromSeconds(20),
            TotalTimeout = TimeSpan.FromSeconds(20),
            IdleTimeout = TimeSpan.FromSeconds(20),
        };

        public DiscordWebhookService(
            IUserPreferencesProvider userPrefsProvider,
            IHttpClientProvider httpClientProvider,
            IApplicationLogger appLogger)
        {
            UserPrefsProvider = userPrefsProvider;
            HttpClientProvider = httpClientProvider;
            Logger = appLogger;
        }

        public void SendServerStarted(string realm, string serverName)
        {
            SendEmbed(
                realm, HeraldEventClass.Routine,
                HostCatalog.T("host.discord.started.title"),
                HostCatalog.T("host.discord.started.body", ("server", serverName)),
                0x57F287); // Green
        }

        public void SendServerStopped(string realm, string serverName)
        {
            SendEmbed(
                realm, HeraldEventClass.Routine,
                HostCatalog.T("host.discord.stopped.title"),
                HostCatalog.T("host.discord.stopped.body", ("server", serverName)),
                0x95A5A6); // Gray
        }

        public void SendServerCrashed(string realm, string serverName, bool willRestart, int restartDelay)
        {
            var description = willRestart
                ? HostCatalog.T(
                    "host.discord.crashed.body_restarting", ("server", serverName), ("seconds", restartDelay))
                : HostCatalog.T("host.discord.crashed.body", ("server", serverName));
            // The one post that asks somebody to do something, so it is the one that can ping
            // the role the host named (see Route).
            SendEmbed(realm, HeraldEventClass.Crash, HostCatalog.T("host.discord.crashed.title"), description, 0xED4245); // Red
        }

        public void SendPlayerJoined(string realm, string playerName, string serverName)
        {
            SendEmbed(
                realm, HeraldEventClass.Players,
                HostCatalog.T("host.discord.joined.title"),
                HostCatalog.T("host.discord.joined.body", ("player", playerName), ("server", serverName)),
                0x3498DB); // Blue
        }

        public void SendPlayerLeft(string realm, string playerName, string serverName)
        {
            SendEmbed(
                realm, HeraldEventClass.Players,
                HostCatalog.T("host.discord.left.title"),
                HostCatalog.T("host.discord.left.body", ("player", playerName), ("server", serverName)),
                0xF39C12); // Orange
        }

        /// <summary>
        /// The server did NOT come back up because the build on disk is not the one it last
        /// ran, or a Steam update is waiting. Someone has to make that call, so say so.
        /// </summary>
        public void SendLaunchHeld(string realm, string serverName, string reason)
        {
            SendEmbed(
                realm, HeraldEventClass.Routine,
                HostCatalog.T("host.discord.held.title"),
                HostCatalog.T("host.discord.held.body", ("server", serverName), ("reason", reason)),
                0xE0A35C); // Amber
        }

        /// <summary>
        /// The dedicated server was updated from inside BakaLoader. Mirrors
        /// <see cref="SendLaunchHeld"/>: same gating, same shape, one post per outcome.
        /// </summary>
        public void SendServerUpdated(string realm, string serverName, string buildId, bool starting)
        {
            var build = string.IsNullOrWhiteSpace(buildId)
                ? ""
                : " " + HostCatalog.T("host.discord.updated.build", ("build", buildId));
            var next = " " + (starting
                ? HostCatalog.T("host.discord.updated.starting")
                : HostCatalog.T("host.discord.updated.when_ready"));
            SendEmbed(
                realm, HeraldEventClass.Routine,
                HostCatalog.T("host.discord.updated.title"),
                HostCatalog.T("host.discord.updated.body", ("server", serverName)) + build + next,
                0x57F287); // Green
        }

        /// <summary>The update did not finish, so the server is still on the old build.</summary>
        public void SendServerUpdateFailed(string realm, string serverName, string reason)
        {
            SendEmbed(
                realm, HeraldEventClass.Routine,
                HostCatalog.T("host.discord.update_failed.title"),
                HostCatalog.T("host.discord.update_failed.body", ("server", serverName), ("reason", reason)),
                0xED4245); // Red
        }

        public void SendLegacyWorldLoaded(string realm, string serverName, string worldName)
        {
            SendEmbed(
                realm, HeraldEventClass.Routine,
                HostCatalog.T("host.discord.legacy_world.title"),
                HostCatalog.T("host.discord.legacy_world.body", ("server", serverName), ("world", worldName)),
                0xE0A35C); // Amber
        }

        /// <summary>
        /// The armed cleanse landed on an empty server, which is usually at four in the
        /// morning with nobody at the window. The counts go in verbatim: they are the plugin's
        /// own reply line, the same words the Players hall shows, so there is one reading of
        /// this sweep and not two.
        /// </summary>
        public void SendCheatMarksCleared(string realm, string serverName, string counts)
        {
            SendEmbed(
                realm, HeraldEventClass.Routine,
                HostCatalog.T("host.discord.cleanse.title"),
                HostCatalog.T("host.discord.cleanse.body", ("server", serverName), ("counts", counts)),
                0x8C6D3F); // Bronze
        }

        /// <summary>
        /// A mod update, from the mod hall or a restart's auto update, moved at least one mod. One
        /// post for the whole run, each mod with the version it left and the one it is on now,
        /// so a player knows what to update before joining. A long run is cut with a count of the
        /// rest, because an embed holds 4096 characters.
        /// </summary>
        public void SendModsUpdated(string realm, string serverName, IReadOnlyList<ModVersionMove> mods)
        {
            var moved = (mods ?? Array.Empty<ModVersionMove>()).Where(m => m != null && !string.IsNullOrWhiteSpace(m.Mod)).ToList();
            if (moved.Count == 0) return;
            SendEmbed(
                realm, HeraldEventClass.Routine,
                HostCatalog.T("host.discord.mods_updated.title"),
                ModsUpdatedBody(serverName, moved),
                0x5865F2); // Blurple
        }

        // Twenty lines is a screenful in a channel; past that the post says how many more.
        internal const int ModsUpdatedLines = 20;

        internal static string ModsUpdatedBody(string serverName, IReadOnlyList<ModVersionMove> moved)
        {
            var body = new StringBuilder(HostCatalog.T("host.discord.mods_updated.body", ("server", serverName)));
            foreach (var m in moved.Take(ModsUpdatedLines))
            {
                body.Append('\n').Append(HostCatalog.T("host.discord.mods_updated.line",
                    ("mod", m.Mod),
                    ("from", string.IsNullOrWhiteSpace(m.FromVersion) ? "?" : m.FromVersion),
                    ("to", string.IsNullOrWhiteSpace(m.ToVersion) ? "?" : m.ToVersion)));
            }
            var rest = Math.Max(0, moved.Count - ModsUpdatedLines);
            if (rest > 0)
                body.Append('\n').Append(HostCatalog.T("host.discord.mods_updated.more", ("count", rest)));
            return body.ToString();
        }

        /// <summary>
        /// One event post, signed with the Herald's look: the host's name and picture, and the
        /// BakaLoader line when the switch is on. The title and the status line belong to the
        /// status post; an event post keeps the title that says what happened, because a channel
        /// of posts all headed with the same custom title would say nothing.
        /// </summary>
        /// <para>
        /// A crash post with a role names the role in the content, the only place a mention
        /// pings, and tells Discord that role is the only mention it may act on, so nothing
        /// else in the post can ever reach everyone.
        /// </para>
        internal static string BuildEventPayload(string title, string description, int color, UserPreferences prefs, string roleId = null, ServerPreferences realm = null)
        {
            var embed = new JObject
            {
                ["title"] = title,
                ["description"] = description,
                ["color"] = color,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["footer"] = new JObject { ["text"] = "ValheimBakaLoader" },
            };
            var payload = new JObject { ["embeds"] = new JArray(embed) };
            if (!string.IsNullOrEmpty(roleId))
            {
                payload["content"] = "<@&" + roleId + ">";
                payload["allowed_mentions"] = new JObject
                {
                    ["parse"] = new JArray(),
                    ["roles"] = new JArray(roleId),
                };
            }
            DiscordHeraldLook.Sign(payload, embed, prefs, realm);
            return payload.ToString(Formatting.None);
        }

        /// <summary>
        /// Whether one event post goes out and where, decided here and nowhere else: the master
        /// on, the realm on, the kind on (HeraldRealms.SendsEvent), read when the post is sent,
        /// so a switch turned off a second ago is honoured. Null when it does not go.
        /// </summary>
        internal static (string Url, string Json)? Route(UserPreferences prefs, string realm, HeraldEventClass kind,
            string title, string description, int color)
        {
            var server = HeraldRealms.Find(prefs, realm);
            if (!HeraldRealms.SendsEvent(prefs, server, kind)) return null;
            var (webhookUrl, threadId) = HeraldRealms.EventRouteFor(prefs, server, kind);
            webhookUrl = webhookUrl.Trim();

            // Escaped the way the status service escapes it. The box takes whatever is pasted,
            // and a stray character in a thread id must not become a second query parameter.
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                var separator = webhookUrl.Contains("?") ? "&" : "?";
                webhookUrl = $"{webhookUrl}{separator}thread_id={Uri.EscapeDataString(threadId.Trim())}";
            }

            return (webhookUrl, BuildEventPayload(title, description, color, prefs, RoleFor(server, kind), server));
        }

        /// <summary>
        /// The role a post of this kind pings: only a crash, and only a role id that is one. One
        /// that is not is left out rather than sent: the hall refuses it on save, so this only
        /// meets a file edited by hand.
        /// </summary>
        internal static string RoleFor(ServerPreferences server, HeraldEventClass kind)
            => kind == HeraldEventClass.Crash && HeraldRealms.TryReadRoleId(server?.HeraldCrashRoleId, out var read)
                ? read
                : null;

        /// <summary>
        /// A sample of one kind of event post from this realm, for the hall's preview: a start
        /// for the routine news, a crash with its ping, and a viking joining, each built by the
        /// same BuildEventPayload a real post is.
        /// </summary>
        internal static string SamplePayload(UserPreferences prefs, ServerPreferences server, HeraldEventClass kind)
        {
            var serverName = ServerPreferences.BlankAsNull(server?.Name) ?? server?.ProfileName ?? "";
            return kind switch
            {
                HeraldEventClass.Crash => BuildEventPayload(
                    HostCatalog.T("host.discord.crashed.title"),
                    HostCatalog.T("host.discord.crashed.body", ("server", serverName)),
                    0xED4245, prefs, RoleFor(server, kind), server),
                HeraldEventClass.Players => BuildEventPayload(
                    HostCatalog.T("host.discord.joined.title"),
                    HostCatalog.T("host.discord.joined.body", ("player", HostCatalog.T("host.discord.sample.player")), ("server", serverName)),
                    0x3498DB, prefs, null, server),
                _ => BuildEventPayload(
                    HostCatalog.T("host.discord.started.title"),
                    HostCatalog.T("host.discord.started.body", ("server", serverName)),
                    0x57F287, prefs, null, server),
            };
        }

        /// <summary>
        /// Keeps on the realm whether Discord refused the last post of a kind for naming no thread
        /// in a forum, so the Discord screen can say so under that kind and not only in the Saga.
        /// Written only when it moves, so a busy realm's every post is not a write of the file.
        /// </summary>
        internal void NoteNoThread(string realm, HeraldEventClass kind, bool refused)
        {
            try
            {
                var known = HeraldRealms.Find(UserPrefsProvider.LoadPreferences(), realm);
                if (known == null || HeraldRealms.NoThread(known, kind) == refused) return;
                UserPrefsProvider.Mutate((Func<UserPreferences, bool>)(prefs =>
                {
                    var server = HeraldRealms.Find(prefs, realm);
                    if (server == null || HeraldRealms.NoThread(server, kind) == refused) return false;
                    HeraldRealms.SetNoThread(server, kind, refused);
                    return true;
                }));
            }
            catch (Exception e)
            {
                Logger.Warning("Could not note the refused Discord post of {realm}: {message}", realm, e.Message);
            }
        }

        private void SendEmbed(string realm, HeraldEventClass kind, string title, string description, int color)
        {
            var routed = Route(UserPrefsProvider.LoadPreferences(), realm, kind, title, description, color);
            if (routed == null) return;
            var (webhookUrl, json) = routed.Value;

            Task.Run(async () =>
            {
                try
                {
                    using var client = HttpClientProvider.CreateClient();
                    // The client's own timeout out of the way, the budget's clocks in its
                    // place: whichever of the two deadlines settles first ends the post.
                    var budget = PostBudget.Copy();
                    BoundedDownload.Unbounded(client);
                    using var deadline = BoundedDownload.Deadline(budget.TotalTimeout, CancellationToken.None);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(webhookUrl, content, deadline.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        // A forum channel refuses a post that names no thread. The routine news
                        // finds the realm's forum post by itself; a kind with its own forum
                        // webhook, or a crash or player post, needs a thread ID in its box.
                        if (await DiscordStatusService.IsForumRefusalAsync(response))
                        {
                            Logger.Warning("Discord refused the {kind} post of {realm}: its webhook is a forum channel and no thread was named. Put a thread ID in the box under that kind's tick on the Discord screen.", kind, realm);
                            NoteNoThread(realm, kind, true);
                        }
                        else
                            Logger.Warning("Discord webhook failed: {statusCode}", response.StatusCode);
                    }
                    else
                    {
                        NoteNoThread(realm, kind, false);
                    }
                }
                catch (Exception e)
                {
                    // The exception object, its type and the innermost reason. On a timeout
                    // the message alone says only that the clock ran out, which is the one
                    // thing a host reading this line already knows.
                    Logger.Warning(e, "Discord webhook error: {message}", Http.WireTrace.Innermost(e));
                }
            });
        }
    }
}
