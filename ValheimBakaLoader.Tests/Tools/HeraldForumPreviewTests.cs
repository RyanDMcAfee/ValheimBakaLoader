using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The Herald in a forum channel, and the hall's preview (1.2.16, issue 24).
    /// <para>
    /// A webhook of a forum channel cannot post into the channel itself: Discord answers 400 with
    /// code 220001. What can go wrong quietly: the status post never being made (the refusal
    /// reported and nothing else), a forum post opened but its thread not kept so every edit
    /// misses the message and makes another forum post, a thread kept but a delete sent without
    /// it, routine news sent to a forum with no thread and refused every time, and a preview that
    /// is built somewhere else than the publish and so shows a post that is not the one sent.
    /// Nothing here reaches Discord: every request lands in a handler in this file.
    /// </para>
    /// </summary>
    public class HeraldForumPreviewTests
    {
        private const string Hook = "https://discord.com/api/webhooks/111/aaaa";
        private const string ForumRefusal = "{\"message\":\"Webhooks posted to forum channels must have a thread_name or thread_id\",\"code\":220001}";

        private sealed class Wire : HttpMessageHandler
        {
            public readonly List<(string Method, string Url, string Body)> Seen = new();
            public Func<HttpRequestMessage, string, int, HttpResponseMessage> Answer;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(token);
                int n;
                lock (Seen)
                {
                    Seen.Add((request.Method.Method, request.RequestUri.AbsoluteUri, body));
                    n = Seen.Count;
                }
                return Answer?.Invoke(request, body, n) ?? Created("9" + n, "4444");
            }
        }

        private sealed class Over : IHttpClientProvider
        {
            private readonly HttpMessageHandler Handler;
            public Over(HttpMessageHandler handler) => Handler = handler;
            public HttpClient CreateClient() => new(Handler, disposeHandler: false);
        }

        /// <summary>userprefs.json kept in memory but round-tripped through its file shape, the way the disk does it.</summary>
        private sealed class RoundTripping : IUserPreferencesProvider
        {
            private string Stored;
            public RoundTripping(UserPreferences start) => Stored = JsonConvert.SerializeObject(start.ToFile());
            public event EventHandler<UserPreferences> PreferencesSaved;
            public UserPreferences LoadPreferences() => UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(Stored));
            public void SavePreferences(UserPreferences preferences)
            {
                Stored = JsonConvert.SerializeObject(preferences.ToFile());
                PreferencesSaved?.Invoke(this, preferences);
            }
        }

        private static HttpResponseMessage Created(string id, string channel) =>
            new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"" + id + "\",\"channel_id\":\"" + channel + "\"}", Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Refused(string body) =>
            new(HttpStatusCode.BadRequest) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static UserPreferences OneRealm(Action<ServerPreferences> shape = null)
        {
            var realm = new ServerPreferences { ProfileName = "alpha", Name = "Alpha Hall", HeraldOn = true, HeraldWebhookUrl = Hook };
            shape?.Invoke(realm);
            return new UserPreferences
            {
                HeraldPerRealmMigrated = true,
                DiscordSharingEnabled = true,
                Servers = new List<ServerPreferences> { realm },
            };
        }

        private static (DiscordStatusService Service, Wire Wire, RoundTripping Store) Herald(UserPreferences prefs)
        {
            var wire = new Wire();
            var store = new RoundTripping(prefs);
            var service = new DiscordStatusService(store, new Over(wire), new Mock<IApplicationLogger>().Object)
            {
                SnapshotProvider = realm => new DiscordStatusSnapshot { ServerName = "Alpha Hall", StatusText = "Online", ServerRunning = true, PlayersOnline = 2, AppVersion = "1.2.16" },
                WaitOut = _ => Task.CompletedTask,
            };
            return (service, wire, store);
        }

        private static ServerPreferences Stored(RoundTripping store) => HeraldRealms.Find(store.LoadPreferences(), "alpha");

        private static JObject WithoutClock(string json)
        {
            var o = JObject.Parse(json);
            foreach (var e in o["embeds"] ?? new JArray()) ((JObject)e).Remove("timestamp");
            return o;
        }

        // ------------------------------------------------------------------ the forum path

        [Fact]
        public async Task A_forum_refusal_opens_a_forum_post_named_after_the_realm_and_keeps_its_thread_for_edits_and_the_delete()
        {
            var (service, wire, store) = Herald(OneRealm());
            wire.Answer = (r, body, n) => n == 1 ? Refused(ForumRefusal) : Created("5001", "60060060");

            var made = await service.PublishNowAsync("alpha");
            Assert.True(made.Ok, made.Error);
            Assert.Equal("created", made.Detail);

            // The first ask names no thread; the second names the forum post to open, and only that.
            Assert.Equal(2, wire.Seen.Count);
            Assert.Equal(Hook + "?wait=true", wire.Seen[0].Url);
            Assert.Null(JObject.Parse(wire.Seen[0].Body)["thread_name"]);
            Assert.Equal(Hook + "?wait=true", wire.Seen[1].Url);
            var second = JObject.Parse(wire.Seen[1].Body);
            Assert.Equal("Alpha Hall", (string)second["thread_name"]);
            second.Remove("thread_name");
            Assert.True(JToken.DeepEquals(WithoutClock(wire.Seen[0].Body), WithoutClock(second.ToString())), "the forum post carries another post than the first ask did");

            Assert.Equal("5001", Stored(store).HeraldStatusMessageId);
            Assert.Equal("60060060", Stored(store).HeraldStatusThreadId);

            // The edit names the thread the post is in, and carries no thread_name.
            wire.Answer = (r, body, n) => new HttpResponseMessage(HttpStatusCode.OK);
            Assert.Equal("updated", (await service.PublishNowAsync("alpha")).Detail);
            Assert.Equal("PATCH", wire.Seen[2].Method);
            Assert.Equal(Hook + "/messages/5001?thread_id=60060060", wire.Seen[2].Url);
            Assert.Null(JObject.Parse(wire.Seen[2].Body)["thread_name"]);

            // Remove takes down the first message in that thread, says the forum post stays, and
            // forgets both, so the next publish opens a new forum post rather than missing one.
            var removed = await service.RemoveStatusMessageAsync("alpha");
            Assert.True(removed.Ok);
            Assert.True(removed.InForum);
            Assert.Equal("DELETE", wire.Seen[3].Method);
            Assert.Equal(Hook + "/messages/5001?thread_id=60060060", wire.Seen[3].Url);
            Assert.Null(Stored(store).HeraldStatusMessageId);
            Assert.Null(Stored(store).HeraldStatusThreadId);
        }

        [Fact]
        public async Task A_text_channel_post_sends_no_thread_name_keeps_no_thread_and_a_remove_is_not_a_forum_one()
        {
            var (service, wire, store) = Herald(OneRealm());
            wire.Answer = (r, body, n) => Created("5001", "1234567");

            Assert.True((await service.PublishNowAsync("alpha")).Ok);
            Assert.Single(wire.Seen);
            Assert.Null(JObject.Parse(wire.Seen[0].Body)["thread_name"]);
            Assert.Null(Stored(store).HeraldStatusThreadId);

            wire.Answer = (r, body, n) => new HttpResponseMessage(HttpStatusCode.NoContent);
            var removed = await service.RemoveStatusMessageAsync("alpha");
            Assert.False(removed.InForum);
            Assert.Equal(Hook + "/messages/5001", wire.Seen[1].Url);
        }

        [Fact]
        public async Task Any_other_refusal_is_reported_and_not_answered_with_a_forum_post()
        {
            var (service, wire, store) = Herald(OneRealm());
            wire.Answer = (r, body, n) => Refused("{\"message\":\"Invalid Form Body\",\"code\":50035}");

            var result = await service.PublishNowAsync("alpha");
            Assert.False(result.Ok);
            Assert.Single(wire.Seen);
            Assert.Null(Stored(store).HeraldStatusMessageId);
        }

        [Fact]
        public async Task A_hand_made_thread_still_works_and_is_never_replaced_by_a_new_forum_post()
        {
            var (service, wire, store) = Herald(OneRealm(r => r.HeraldThreadId = "7777777"));
            wire.Answer = (r, body, n) => Refused(ForumRefusal);

            Assert.False((await service.PublishNowAsync("alpha")).Ok);
            Assert.Single(wire.Seen);
            Assert.Equal(Hook + "?thread_id=7777777&wait=true", wire.Seen[0].Url);
            Assert.Null(JObject.Parse(wire.Seen[0].Body)["thread_name"]);
        }

        [Fact]
        public async Task A_forum_id_that_is_not_an_id_is_not_kept()
        {
            var (service, wire, store) = Herald(OneRealm());
            wire.Answer = (r, body, n) => n == 1 ? Refused(ForumRefusal) : Created("5001", "60060060&x=1");

            Assert.True((await service.PublishNowAsync("alpha")).Ok);
            Assert.Equal("5001", Stored(store).HeraldStatusMessageId);
            Assert.Null(Stored(store).HeraldStatusThreadId);
        }

        [Fact]
        public void The_forum_post_name_is_the_display_name_then_the_profile_name_cut_to_a_hundred()
        {
            Assert.Equal("Alpha Hall", HeraldRealms.ForumPostNameFor(new ServerPreferences { ProfileName = "alpha", Name = " Alpha Hall " }));
            Assert.Equal("alpha", HeraldRealms.ForumPostNameFor(new ServerPreferences { ProfileName = "alpha", Name = "  " }));
            Assert.Equal(100, HeraldRealms.ForumPostNameFor(new ServerPreferences { ProfileName = "a", Name = new string('x', 140) }).Length);
        }

        // ------------------------------------------------------------------ routine news into the forum post

        [Fact]
        public void Routine_news_defaults_to_the_forum_post_and_the_other_kinds_and_own_boxes_do_not()
        {
            var prefs = OneRealm(r => { r.HeraldStatusThreadId = "60060060"; r.HeraldRoutineOn = r.HeraldCrashOn = r.HeraldPlayersOn = true; });
            var realm = prefs.Servers[0];

            Assert.Equal((Hook, "60060060"), HeraldRealms.EventRouteFor(prefs, realm, HeraldEventClass.Routine));
            Assert.Equal((Hook, (string)null), HeraldRealms.EventRouteFor(prefs, realm, HeraldEventClass.Crash));
            Assert.Equal((Hook, (string)null), HeraldRealms.EventRouteFor(prefs, realm, HeraldEventClass.Players));

            // A thread of the kind's own wins; a webhook of the kind's own is another channel.
            realm.HeraldRoutineThreadId = "8888888";
            Assert.Equal("8888888", HeraldRealms.EventRouteFor(prefs, realm, HeraldEventClass.Routine).Thread);
            realm.HeraldRoutineThreadId = null;
            realm.HeraldRoutineWebhookUrl = "https://discord.com/api/webhooks/333/cccc";
            Assert.Null(HeraldRealms.EventRouteFor(prefs, realm, HeraldEventClass.Routine).Thread);

            // And the routine post itself goes there through the service's one route.
            realm.HeraldRoutineWebhookUrl = null;
            var routed = DiscordWebhookService.Route(prefs, "alpha", HeraldEventClass.Routine, "t", "d", 1);
            Assert.Equal(Hook + "?thread_id=60060060", routed.Value.Url);
        }

        // ------------------------------------------------------------------ the news line on the status post

        private static JToken EventsField(string payload)
        {
            var name = HostCatalog.T("host.status.field.events");
            return JObject.Parse(payload)["embeds"][0]["fields"].FirstOrDefault(f => (string)f["name"] == name);
        }

        private static string Post(UserPreferences prefs) =>
            DiscordStatusService.BuildEmbedPayload(new DiscordStatusSnapshot { ServerName = "Alpha Hall", AppVersion = "1.2.16" }, prefs, prefs.Servers[0]);

        [Fact]
        public void The_status_post_links_the_thread_routine_news_goes_to_and_only_then()
        {
            var prefs = OneRealm(r => { r.HeraldRoutineOn = true; r.HeraldRoutineThreadId = "8888888"; });
            var field = EventsField(Post(prefs));
            Assert.NotNull(field);
            Assert.Equal("<#8888888>", (string)field["value"]);
            Assert.False((bool)field["inline"]);

            // Routine off: no line.
            prefs.Servers[0].HeraldRoutineOn = false;
            Assert.Null(EventsField(Post(prefs)));

            // News into the channel itself: no line.
            prefs.Servers[0].HeraldRoutineOn = true;
            prefs.Servers[0].HeraldRoutineThreadId = null;
            Assert.Null(EventsField(Post(prefs)));

            // News into the forum post this post opened: the replies are right below it, no line.
            prefs.Servers[0].HeraldStatusThreadId = "60060060";
            Assert.Null(EventsField(Post(prefs)));

            // News into the thread the post itself is in: no line either.
            prefs.Servers[0].HeraldStatusThreadId = null;
            prefs.Servers[0].HeraldThreadId = "7777777";
            Assert.Null(EventsField(Post(prefs)));

            // A box holding something that is not an id never reaches the post.
            prefs.Servers[0].HeraldThreadId = null;
            prefs.Servers[0].HeraldRoutineThreadId = "12345> @everyone";
            Assert.Null(EventsField(Post(prefs)));
        }

        // ------------------------------------------------------------------ the preview

        [Fact]
        public async Task The_preview_is_the_post_a_publish_sends_and_sends_nothing()
        {
            var (service, wire, store) = Herald(OneRealm(r => { r.HeraldTitle = "Alpha's hall"; r.HeraldRoutineOn = true; r.HeraldRoutineThreadId = "8888888"; }));

            var preview = service.Preview("alpha");
            Assert.True(preview.Ok, preview.Error);
            Assert.Empty(wire.Seen);

            Assert.True((await service.PublishNowAsync("alpha")).Ok);
            Assert.True(JToken.DeepEquals(WithoutClock(preview.Payload), WithoutClock(wire.Seen[0].Body)),
                "the preview is not the post a publish sent:\n" + preview.Payload + "\n" + wire.Seen[0].Body);
            Assert.Equal("Alpha's hall", (string)JObject.Parse(preview.Payload)["embeds"][0]["title"]);
        }

        [Fact]
        public void The_preview_carries_a_sample_of_each_kind_with_where_it_goes_and_the_crash_ping()
        {
            var (service, wire, _) = Herald(OneRealm(r =>
            {
                r.HeraldRoutineOn = true;
                r.HeraldCrashOn = true;
                r.HeraldCrashRoleId = "99999999";
                r.HeraldCrashThreadId = "5555555";
                r.HeraldPlayersOn = false;
            }));

            var preview = service.Preview("alpha");
            Assert.Equal(new[] { "Routine", "Crash", "Players" }, preview.Events.Select(e => e.Kind));
            Assert.Equal(new[] { true, true, false }, preview.Events.Select(e => e.On));
            Assert.Equal("5555555", preview.Events[1].Thread);

            var crash = JObject.Parse(preview.Events[1].Payload);
            Assert.Equal("<@&99999999>", (string)crash["content"]);
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"parse\":[],\"roles\":[\"99999999\"]}"), crash["allowed_mentions"]));
            Assert.Equal(HostCatalog.T("host.discord.crashed.title"), (string)crash["embeds"][0]["title"]);

            Assert.Null(JObject.Parse(preview.Events[0].Payload)["content"]);
            Assert.Contains(HostCatalog.T("host.discord.sample.player"), (string)JObject.Parse(preview.Events[2].Payload)["embeds"][0]["description"]);
            Assert.Empty(wire.Seen);
        }

        [Fact]
        public void The_preview_of_a_realm_that_is_gone_says_so_and_sends_nothing()
        {
            var (service, wire, _) = Herald(OneRealm());
            var preview = service.Preview("nobody");
            Assert.False(preview.Ok);
            Assert.False(string.IsNullOrWhiteSpace(preview.Error));
            Assert.Empty(wire.Seen);
        }

        [Fact]
        public void The_bridge_serves_the_preview_from_the_service_and_hands_remove_its_forum_answer()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            var at = bridge.IndexOf("RegisterRpc(\"discord.preview\"", StringComparison.Ordinal);
            Assert.True(at >= 0, "discord.preview is not registered");
            var body = bridge.Substring(at, bridge.IndexOf("// --- Custom join domain", at, StringComparison.Ordinal) - at);
            Assert.Contains("DiscordStatus.Preview(realm)", body);
            foreach (var key in new[] { "payload =", "events =", "kind =", "on =", "thread =" })
                Assert.Contains(key, body);
            Assert.Contains("forum = result.InForum", bridge);
        }
    }
}
