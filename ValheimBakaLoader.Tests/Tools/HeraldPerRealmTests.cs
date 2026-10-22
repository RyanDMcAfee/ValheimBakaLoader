using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The Herald per realm (1.2.16, issue 24): each realm keeps a Discord status post of its own.
    /// <para>
    /// What can go wrong without a build noticing: the one post of the builds before going
    /// missing or being doubled by the move, a second realm posting by surprise after the update,
    /// a realm that is off or a master switch that is off still sending, a rename or a stale
    /// profile save forgetting the post so the next edit makes a second one, a deleted realm
    /// leaving its post in the channel, two realms sharing one post again, and several realms on
    /// one webhook meeting Discord's rate limit with nobody waiting it out. Nothing here reaches
    /// Discord: every request lands in a handler in this file, and nothing touches the disk but a
    /// temp folder.
    /// </para>
    /// </summary>
    public class HeraldPerRealmTests
    {
        private const string HookA = "https://discord.com/api/webhooks/111/aaaa";
        private const string HookB = "https://discord.com/api/webhooks/222/bbbb";

        // ------------------------------------------------------------------ the move

        /// <summary>A userprefs.json as 1.2.15 wrote it: one post, three realms, no Herald keys on any realm.</summary>
        private const string OldFile = @"{
  ""discordWebhookUrl"": """ + HookA + @""",
  ""discordWebhookThreadId"": ""777"",
  ""discordSharingEnabled"": true,
  ""discordShareAddress"": false,
  ""discordSharePassword"": true,
  ""discordStatusMessageId"": ""5550001"",
  ""discordStatusMessageSignature"": ""Raven\nhttps://example.org/r.png"",
  ""discordTitle"": ""Ravenhold"",
  ""discordStatusLine"": ""Ashlands opens Friday."",
  ""discordBotName"": ""Raven"",
  ""servers"": [
    { ""profileName"": ""Alpha"", ""name"": ""Alpha"", ""port"": 2456 },
    { ""profileName"": ""Beta"", ""name"": ""Beta"", ""port"": 2466 },
    { ""profileName"": ""Gamma"", ""name"": ""Gamma"", ""port"": 2476, ""archived"": true }
  ]
}";

        private static UserPreferences ReadOld() =>
            UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(OldFile));

        private static UserPreferences RoundTrip(UserPreferences prefs) =>
            UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(JsonConvert.SerializeObject(prefs.ToFile())));

        [Fact]
        public void An_old_file_reads_with_every_realm_off_and_nothing_moved_yet()
        {
            var prefs = ReadOld();
            Assert.False(prefs.HeraldPerRealmMigrated);
            foreach (var realm in prefs.Servers)
            {
                Assert.False(realm.HeraldOn);
                Assert.Null(realm.HeraldWebhookUrl);
                Assert.Null(realm.HeraldStatusMessageId);
                Assert.True(realm.HeraldShareAddress, "a realm with no key does not share its address by default");
                Assert.False(realm.HeraldSharePassword);
            }
        }

        [Fact]
        public void The_single_post_goes_to_the_realm_it_described_and_every_other_realm_starts_off()
        {
            var prefs = ReadOld();

            Assert.True(HeraldRealms.Migrate(prefs, "Beta"));
            prefs = RoundTrip(prefs);

            var beta = HeraldRealms.Find(prefs, "Beta");
            Assert.True(beta.HeraldOn);
            Assert.Equal(HookA, beta.HeraldWebhookUrl);
            Assert.Equal("777", beta.HeraldThreadId);
            Assert.False(beta.HeraldShareAddress);
            Assert.True(beta.HeraldSharePassword);
            Assert.Equal("Ravenhold", beta.HeraldTitle);
            Assert.Equal("Ashlands opens Friday.", beta.HeraldStatusLine);
            Assert.Equal("5550001", beta.HeraldStatusMessageId);
            Assert.Equal("Raven\nhttps://example.org/r.png", beta.HeraldStatusMessageSignature);

            foreach (var other in new[] { "Alpha", "Gamma" }.Select(n => HeraldRealms.Find(prefs, n)))
            {
                Assert.False(other.HeraldOn, other.ProfileName + " posts by surprise after the update");
                Assert.Null(other.HeraldWebhookUrl);
                Assert.Null(other.HeraldStatusMessageId);
                Assert.Null(other.HeraldStatusMessageSignature);
            }

            Assert.True(prefs.HeraldPerRealmMigrated);
            // The app-wide fields are left as they were written, so a build from before the move
            // still edits the same post rather than making a second one.
            Assert.Equal("5550001", prefs.DiscordStatusMessageId);
            Assert.Equal(HookA, prefs.DiscordWebhookUrl);

            // Exactly one realm owns the post.
            Assert.Single(prefs.Servers, s => s.HeraldStatusMessageId != null);
        }

        [Fact]
        public void The_move_happens_once_and_a_realm_switched_off_afterwards_stays_off()
        {
            var prefs = ReadOld();
            Assert.True(HeraldRealms.Migrate(prefs, "Alpha"));
            var alpha = HeraldRealms.Find(prefs, "Alpha");
            alpha.HeraldOn = false;
            alpha.HeraldTitle = "changed since";

            Assert.False(HeraldRealms.Migrate(prefs, "Beta"));
            Assert.False(HeraldRealms.Find(prefs, "Alpha").HeraldOn);
            Assert.Equal("changed since", HeraldRealms.Find(prefs, "Alpha").HeraldTitle);
            Assert.False(HeraldRealms.Find(prefs, "Beta").HeraldOn);

            // And the flag survives the file.
            Assert.False(HeraldRealms.Migrate(RoundTrip(prefs), "Beta"));
        }

        [Fact]
        public void A_described_realm_that_is_gone_hands_the_post_to_the_first_realm_still_listed()
        {
            var prefs = ReadOld();
            prefs.Servers.Reverse(); // Gamma (archived), Beta, Alpha
            Assert.True(HeraldRealms.Migrate(prefs, "Deleted Since"));
            Assert.True(HeraldRealms.Find(prefs, "Beta").HeraldOn);
            Assert.False(HeraldRealms.Find(prefs, "Gamma").HeraldOn);
            Assert.Equal("5550001", HeraldRealms.Find(prefs, "Beta").HeraldStatusMessageId);
        }

        [Fact]
        public void With_no_realm_the_move_waits_for_one_when_there_is_a_post_and_is_done_when_there_is_none()
        {
            var withPost = ReadOld();
            withPost.Servers.Clear();
            Assert.False(HeraldRealms.Migrate(withPost, "Alpha"));
            Assert.False(withPost.HeraldPerRealmMigrated);

            var fresh = new UserPreferences();
            Assert.True(HeraldRealms.Migrate(fresh, "Alpha"));
            Assert.True(fresh.HeraldPerRealmMigrated);
        }

        [Theory]
        [InlineData(new string[0], "Alpha", "Alpha")]
        [InlineData(new[] { "Beta" }, "Alpha", "Beta")]
        [InlineData(new[] { "Beta", "alpha" }, "Alpha", "Alpha")]
        [InlineData(new[] { "Beta", "Delta" }, "Alpha", "Beta")]
        public void The_described_realm_is_the_old_rule(string[] live, string active, string expected)
        {
            Assert.Equal(expected, HeraldRealms.PostRealm(live, active));
        }

        // ------------------------------------------------------------------ where a realm posts

        [Fact]
        public void A_blank_webhook_is_the_app_wide_one_and_a_thread_belongs_to_its_channel()
        {
            var app = new UserPreferences { DiscordWebhookUrl = HookA, DiscordWebhookThreadId = "777", DiscordSharingEnabled = true };

            var plain = new ServerPreferences { ProfileName = "Alpha", HeraldOn = true };
            Assert.Equal(HookA, HeraldRealms.WebhookUrlFor(app, plain));
            Assert.Equal("777", HeraldRealms.ThreadIdFor(app, plain));

            var ownThread = new ServerPreferences { ProfileName = "Alpha", HeraldOn = true, HeraldThreadId = "888" };
            Assert.Equal("888", HeraldRealms.ThreadIdFor(app, ownThread));

            // A webhook of the realm's own posts to another channel, where the app-wide thread is not.
            var own = new ServerPreferences { ProfileName = "Beta", HeraldOn = true, HeraldWebhookUrl = HookB };
            Assert.Equal(HookB, HeraldRealms.WebhookUrlFor(app, own));
            Assert.Null(HeraldRealms.ThreadIdFor(app, own));

            Assert.True(HeraldRealms.Shares(app, plain));
            Assert.False(HeraldRealms.Shares(app, new ServerPreferences { ProfileName = "Off" }));
            Assert.False(HeraldRealms.Shares(new UserPreferences { DiscordWebhookUrl = HookA }, plain), "the master off still shares");
            Assert.False(HeraldRealms.Shares(new UserPreferences { DiscordSharingEnabled = true }, plain), "a realm with nowhere to post shares");
        }

        [Fact]
        public void The_realm_settings_survive_the_file_and_an_old_profile_reads_as_off()
        {
            var realm = new ServerPreferences
            {
                ProfileName = "Alpha", HeraldOn = true, HeraldWebhookUrl = " " + HookB + " ", HeraldThreadId = "9",
                HeraldShareAddress = false, HeraldSharePassword = true, HeraldTitle = "T", HeraldStatusLine = "  ",
                HeraldStatusMessageId = "42", HeraldStatusMessageSignature = "sig", HeraldStatusThreadId = "43",
            };
            var json = JsonConvert.SerializeObject(realm.ToFile());
            foreach (var key in new[] { "heraldOn", "heraldWebhookUrl", "heraldThreadId", "heraldShareAddress", "heraldSharePassword",
                                        "heraldTitle", "heraldStatusMessageId", "heraldStatusMessageSignature", "heraldStatusThreadId" })
                Assert.Contains("\"" + key + "\"", json);

            var back = ServerPreferences.FromFile(JsonConvert.DeserializeObject<ServerPreferencesFile>(json));
            Assert.True(back.HeraldOn);
            Assert.Equal(HookB, back.HeraldWebhookUrl);
            Assert.Equal("9", back.HeraldThreadId);
            Assert.False(back.HeraldShareAddress);
            Assert.True(back.HeraldSharePassword);
            Assert.Equal("T", back.HeraldTitle);
            Assert.Null(back.HeraldStatusLine);
            Assert.Equal("42", back.HeraldStatusMessageId);
            Assert.Equal("sig", back.HeraldStatusMessageSignature);
            Assert.Equal("43", back.HeraldStatusThreadId);

            var old = ServerPreferences.FromFile(JsonConvert.DeserializeObject<ServerPreferencesFile>("{\"profileName\":\"Old\"}"));
            Assert.False(old.HeraldOn);
            Assert.True(old.HeraldShareAddress);
            Assert.Null(old.HeraldStatusMessageId);
        }

        [Fact]
        public void A_new_realm_or_a_duplicate_never_adopts_the_source_realms_post()
        {
            var source = new ServerPreferences
            {
                ProfileName = "Alpha", HeraldOn = true, HeraldWebhookUrl = HookB, HeraldThreadId = "9",
                HeraldSharePassword = true, HeraldStatusMessageId = "42", HeraldStatusMessageSignature = "sig", HeraldStatusThreadId = "43",
            };
            var created = BlendWindow.SeedNewRealm(source);

            Assert.False(created.HeraldOn);
            Assert.Null(created.HeraldStatusMessageId);
            Assert.Null(created.HeraldStatusMessageSignature);
            Assert.Null(created.HeraldStatusThreadId);
            // Where it would post is kept, so turning it on is one switch.
            Assert.Equal(HookB, created.HeraldWebhookUrl);
            Assert.Equal("9", created.HeraldThreadId);
            Assert.True(created.HeraldSharePassword);
            // The source keeps its own.
            Assert.Equal("42", source.HeraldStatusMessageId);
            Assert.True(source.HeraldOn);
        }

        // ------------------------------------------------------------------ the profile writer

        [Fact]
        public void A_profile_save_built_on_an_older_read_keeps_the_post_and_the_realms_switch()
        {
            var userPrefs = new RoundTripping();
            var servers = new ServerPreferencesProvider(userPrefs, new Mock<Serilog.ILogger>().Object);
            servers.SavePreferences(new ServerPreferences { ProfileName = "Alpha", Name = "Alpha" });

            // The server's own thread loads the profile to record a launched build...
            var stale = servers.LoadPreferences("Alpha");

            // ...the hall switches the realm on and the status service makes its post...
            ((IUserPreferencesProvider)userPrefs).Mutate(prefs =>
            {
                var alpha = HeraldRealms.Find(prefs, "Alpha");
                alpha.HeraldOn = true;
                alpha.HeraldTitle = "Ravenhold";
                alpha.HeraldStatusMessageId = "42";
                alpha.HeraldStatusMessageSignature = "sig";
            });

            // ...and the launch record lands with what it read before.
            stale.LastLaunchedServerBuild = "build 9";
            servers.SavePreferences(stale);

            var stored = servers.LoadPreferences("Alpha");
            Assert.Equal("build 9", stored.LastLaunchedServerBuild);
            Assert.Equal("42", stored.HeraldStatusMessageId);
            Assert.Equal("sig", stored.HeraldStatusMessageSignature);
            Assert.True(stored.HeraldOn, "a stale save switched the realm back off");
            Assert.Equal("Ravenhold", stored.HeraldTitle);
        }

        // ------------------------------------------------------------------ the service, on the wire

        private sealed class Wire : HttpMessageHandler
        {
            public readonly List<(string Method, string Url, string Body)> Seen = new();
            public Func<HttpRequestMessage, int, HttpResponseMessage> Answer;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(token);
                int n;
                lock (Seen)
                {
                    Seen.Add((request.Method.Method, request.RequestUri.AbsoluteUri, body));
                    n = Seen.Count;
                }
                return Answer?.Invoke(request, n) ?? Created("9" + n);
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

            public RoundTripping(UserPreferences start = null)
                => Stored = JsonConvert.SerializeObject((start ?? new UserPreferences { HeraldPerRealmMigrated = true }).ToFile());

            public event EventHandler<UserPreferences> PreferencesSaved;

            public UserPreferences LoadPreferences()
                => UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(Stored));

            public void SavePreferences(UserPreferences preferences)
            {
                Stored = JsonConvert.SerializeObject(preferences.ToFile());
                PreferencesSaved?.Invoke(this, preferences);
            }
        }

        private static HttpResponseMessage Created(string id) =>
            new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"" + id + "\"}", Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Limited(double seconds) =>
            new((HttpStatusCode)429) { Content = new StringContent("{\"message\":\"You are being rate limited.\",\"retry_after\":" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"global\":false}", Encoding.UTF8, "application/json") };

        /// <summary>
        /// Two realms: Alpha on the app-wide webhook and thread, Beta on a webhook of its own, both
        /// on, the master on, and a third realm, Off, that shares nothing.
        /// </summary>
        private static UserPreferences TwoRealms() => new()
        {
            HeraldPerRealmMigrated = true,
            DiscordSharingEnabled = true,
            DiscordWebhookUrl = HookA,
            DiscordWebhookThreadId = "777",
            Servers = new List<ServerPreferences>
            {
                new() { ProfileName = "Alpha", Name = "Alpha", HeraldOn = true, HeraldTitle = "Alpha's hall" },
                new() { ProfileName = "Beta", Name = "Beta", HeraldOn = true, HeraldWebhookUrl = HookB },
                new() { ProfileName = "Off", Name = "Off" },
            },
        };

        private static (DiscordStatusService Service, Wire Wire, RoundTripping Store, List<string> Asked, List<TimeSpan> Waits) Herald(UserPreferences prefs)
        {
            var wire = new Wire();
            var store = new RoundTripping(prefs);
            var asked = new List<string>();
            var waits = new List<TimeSpan>();
            var service = new DiscordStatusService(store, new Over(wire), new Mock<IApplicationLogger>().Object)
            {
                SnapshotProvider = realm =>
                {
                    lock (asked) asked.Add(realm);
                    return new DiscordStatusSnapshot { ServerName = realm, StatusText = "Online", ServerRunning = true, AppVersion = "1.2.16" };
                },
                WaitOut = wait => { waits.Add(wait); return Task.CompletedTask; },
            };
            return (service, wire, store, asked, waits);
        }

        private static ServerPreferences Stored(RoundTripping store, string realm) => HeraldRealms.Find(store.LoadPreferences(), realm);

        [Fact]
        public async Task Two_realms_make_two_posts_each_through_its_own_webhook_and_each_keeps_its_own_id()
        {
            var (service, wire, store, asked, _) = Herald(TwoRealms());
            wire.Answer = (r, n) => Created(r.RequestUri.ToString().StartsWith(HookA) ? "1001" : "2002");

            Assert.True((await service.PublishNowAsync("Alpha")).Ok);
            Assert.True((await service.PublishNowAsync("Beta")).Ok);

            Assert.Equal(2, wire.Seen.Count);
            Assert.All(wire.Seen, s => Assert.Equal("POST", s.Method));
            Assert.Equal(HookA + "?thread_id=777&wait=true", wire.Seen[0].Url);
            Assert.Equal(HookB + "?wait=true", wire.Seen[1].Url);
            Assert.Equal("1001", Stored(store, "Alpha").HeraldStatusMessageId);
            Assert.Equal("2002", Stored(store, "Beta").HeraldStatusMessageId);
            Assert.Null(Stored(store, "Off").HeraldStatusMessageId);

            // Each post describes its own realm and carries its own title.
            Assert.Equal(new[] { "Alpha", "Beta" }, asked);
            Assert.Equal("Alpha's hall", (string)JObject.Parse(wire.Seen[0].Body)["embeds"][0]["title"]);
            Assert.Equal("Beta", (string)JObject.Parse(wire.Seen[1].Body)["embeds"][0]["title"]);

            // The next publish of each edits its own post, never the other's.
            wire.Answer = (r, n) => new HttpResponseMessage(HttpStatusCode.OK);
            Assert.Equal("updated", (await service.PublishNowAsync("Beta")).Detail);
            Assert.Equal("PATCH", wire.Seen[2].Method);
            Assert.Equal(HookB + "/messages/2002", wire.Seen[2].Url);
        }

        [Fact]
        public async Task A_realm_that_is_off_and_a_master_that_is_off_send_nothing()
        {
            var (service, wire, store, asked, _) = Herald(TwoRealms());

            service.RequestUpdate("Off");
            Assert.False(service.HasPendingUpdate("Off"), "a realm that is off was scheduled");
            Assert.True((await service.PublishScheduledAsync("Off")).Ok);

            service.RequestUpdateAll();
            Assert.True(service.HasPendingUpdate("Alpha"));
            Assert.True(service.HasPendingUpdate("Beta"));
            Assert.False(service.HasPendingUpdate("Off"));

            // The master goes off between the request and the timer: the timer sends nothing.
            ((IUserPreferencesProvider)store).Mutate(p => { p.DiscordSharingEnabled = false; });
            Assert.Equal("not shared", (await service.PublishScheduledAsync("Alpha")).Detail);
            Assert.Equal("not shared", (await service.PublishScheduledAsync("Beta")).Detail);

            // And with the master off nothing is scheduled at all.
            var offPrefs = TwoRealms();
            offPrefs.DiscordSharingEnabled = false;
            var (masterOff, masterWire, _, _, _) = Herald(offPrefs);
            masterOff.RequestUpdate("Alpha");
            masterOff.RequestUpdateAll();
            Assert.False(masterOff.HasPendingUpdate("Alpha"));
            Assert.False(masterOff.HasPendingUpdate("Beta"));

            Assert.Empty(wire.Seen);
            Assert.Empty(masterWire.Seen);
            Assert.Empty(asked);
        }

        [Fact]
        public async Task An_archived_realm_is_left_out_of_an_update_of_every_realm()
        {
            var prefs = TwoRealms();
            prefs.Servers[1].Archived = true;
            var (service, wire, _, _, _) = Herald(prefs);
            service.RequestUpdateAll();
            Assert.True(service.HasPendingUpdate("Alpha"));
            Assert.False(service.HasPendingUpdate("Beta"));
            await Task.CompletedTask;
            Assert.Empty(wire.Seen);
        }

        [Fact]
        public async Task A_rename_carries_the_post_and_the_next_edit_patches_it()
        {
            var prefs = TwoRealms();
            prefs.Servers[0].HeraldStatusMessageId = "1001";
            prefs.Servers[0].HeraldStatusMessageSignature = DiscordHeraldLook.SignatureFor(prefs);
            var (service, wire, store, asked, _) = Herald(prefs);
            var servers = new ServerPreferencesProvider(store, new Mock<Serilog.ILogger>().Object);

            // profiles.rename's own three steps: load, save under the new name, remove the old.
            var alpha = servers.LoadPreferences("Alpha");
            alpha.ProfileName = "Alpha Prime";
            servers.SavePreferences(alpha);
            servers.RemovePreferences("Alpha");

            var renamed = servers.LoadPreferences("Alpha Prime");
            Assert.Equal("1001", renamed.HeraldStatusMessageId);
            Assert.True(renamed.HeraldOn);

            wire.Answer = (r, n) => new HttpResponseMessage(HttpStatusCode.OK);
            var result = await service.PublishNowAsync("Alpha Prime");
            Assert.True(result.Ok, result.Error);
            var edit = Assert.Single(wire.Seen);
            Assert.Equal("PATCH", edit.Method);
            Assert.Equal(HookA + "/messages/1001?thread_id=777", edit.Url);
            Assert.Equal(new[] { "Alpha Prime" }, asked);
        }

        [Fact]
        public async Task Remove_takes_the_realms_post_down_and_a_failure_still_forgets_it()
        {
            var prefs = TwoRealms();
            prefs.Servers[0].HeraldStatusMessageId = "1001";
            prefs.Servers[1].HeraldStatusMessageId = "2002";
            var (service, wire, store, _, _) = Herald(prefs);
            wire.Answer = (r, n) => new HttpResponseMessage(HttpStatusCode.NoContent);

            var removed = await service.RemoveStatusMessageAsync("Alpha");
            Assert.True(removed.Ok);
            Assert.Equal("removed", removed.Detail);
            var delete = Assert.Single(wire.Seen);
            Assert.Equal("DELETE", delete.Method);
            Assert.Equal(HookA + "/messages/1001?thread_id=777", delete.Url);
            Assert.Null(Stored(store, "Alpha").HeraldStatusMessageId);
            Assert.Equal("2002", Stored(store, "Beta").HeraldStatusMessageId);

            // Discord down: the post is forgotten anyway, so a realm being deleted is not held up.
            wire.Answer = (r, n) => new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var failed = await service.RemoveStatusMessageAsync("Beta");
            Assert.True(failed.Ok);
            Assert.Equal("forgotten", failed.Detail);
            Assert.Null(Stored(store, "Beta").HeraldStatusMessageId);

            Assert.Equal("nothing to remove", (await service.RemoveStatusMessageAsync("Off")).Detail);
            Assert.Equal("nothing to remove", (await service.RemoveStatusMessageAsync("Never Was")).Detail);
        }

        [Fact]
        public async Task A_rate_limit_is_waited_out_once_and_a_second_one_is_reported()
        {
            var (service, wire, store, _, waits) = Herald(TwoRealms());
            wire.Answer = (r, n) => n == 1 ? Limited(0.25) : Created("1001");

            var result = await service.PublishNowAsync("Alpha");
            Assert.True(result.Ok, result.Error);
            Assert.Equal(2, wire.Seen.Count);
            Assert.Equal(wire.Seen[0].Body, wire.Seen[1].Body);
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(250) }, waits);
            Assert.Equal("1001", Stored(store, "Alpha").HeraldStatusMessageId);

            // Twice in a row: one wait, one retry, then the refusal as Discord gave it.
            var (again, againWire, againStore, _, againWaits) = Herald(TwoRealms());
            againWire.Answer = (r, n) => Limited(1.5);
            var refused = await again.PublishNowAsync("Beta");
            Assert.False(refused.Ok);
            Assert.Contains("429", refused.Error);
            Assert.Equal(2, againWire.Seen.Count);
            Assert.Single(againWaits);
            Assert.Null(Stored(againStore, "Beta").HeraldStatusMessageId);
        }

        [Fact]
        public async Task A_rate_limit_answer_is_read_from_the_body_then_the_header_and_is_held_to_a_ceiling()
        {
            Assert.Equal(TimeSpan.FromMilliseconds(250), await DiscordStatusService.RetryAfterAsync(Limited(0.25)));
            Assert.Equal(TimeSpan.FromSeconds(30), await DiscordStatusService.RetryAfterAsync(Limited(600)));

            var header = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("not json") };
            header.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            Assert.Equal(TimeSpan.FromSeconds(3), await DiscordStatusService.RetryAfterAsync(header));

            Assert.Equal(TimeSpan.FromSeconds(1), await DiscordStatusService.RetryAfterAsync(new HttpResponseMessage((HttpStatusCode)429)));
        }

        [Fact]
        public async Task A_realm_that_is_gone_is_refused_and_nothing_is_sent()
        {
            var (service, wire, _, asked, _) = Herald(TwoRealms());
            var result = await service.PublishNowAsync("Never Was");
            Assert.False(result.Ok);
            Assert.Empty(wire.Seen);
            Assert.Empty(asked);
        }

        // ------------------------------------------------------------------ event posts

        private sealed class Store : IUserPreferencesProvider
        {
            public UserPreferences Current;
            public event EventHandler<UserPreferences> PreferencesSaved;
            public UserPreferences LoadPreferences() => Current;
            public void SavePreferences(UserPreferences preferences) { Current = preferences; PreferencesSaved?.Invoke(this, preferences); }
        }

        [Fact]
        public async Task An_event_post_escapes_its_thread_and_the_master_off_sends_none()
        {
            var wire = new Wire();
            var landed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            wire.Answer = (r, n) => { landed.TrySetResult(true); return new HttpResponseMessage(HttpStatusCode.NoContent); };
            var prefs = new UserPreferences
            {
                DiscordWebhookUrl = HookA, DiscordWebhookThreadId = "12 34&wait=false", DiscordSharingEnabled = false,
                Servers = new List<ServerPreferences> { new() { ProfileName = "Final Sunset", HeraldOn = true, HeraldRoutineOn = true } },
            };
            var events = new DiscordWebhookService(new Store { Current = prefs }, new Over(wire), new Mock<IApplicationLogger>().Object);

            events.SendServerStarted("Final Sunset", "Final Sunset");
            Assert.Empty(wire.Seen); // the master off returns before anything is queued

            prefs.DiscordSharingEnabled = true;
            events.SendServerStarted("Final Sunset", "Final Sunset");
            Assert.Same(landed.Task, await Task.WhenAny(landed.Task, Task.Delay(TimeSpan.FromSeconds(10))));
            var post = Assert.Single(wire.Seen);
            Assert.Equal(HookA + "?thread_id=12%2034%26wait%3Dfalse", post.Url);
        }

        // ------------------------------------------------------------------ the hall's save

        private sealed class RealmStore : IServerPreferencesProvider
        {
            public readonly List<ServerPreferences> Realms = new();
            public event EventHandler<List<ServerPreferences>> PreferencesSaved;
            public ServerPreferences LoadPreferences(string profileName) => Realms.FirstOrDefault(r => r.ProfileName == profileName);
            public IEnumerable<ServerPreferences> LoadPreferences() => Realms;
            public void SavePreferences(ServerPreferences preferences) => PreferencesSaved?.Invoke(this, Realms);
            public void RemovePreferences(string profileName) => Realms.RemoveAll(r => r.ProfileName == profileName);
        }

        /// <summary>The page's English for an id, read out of en.json, which the toast falls back to.</summary>
        private static string English(string id) =>
            (string)JObject.Parse(File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(), "ValheimBakaLoader", "WebUI", "i18n", "en.json")))
                ["keys"][id]["lore"];

        [Fact]
        public void The_halls_block_lands_on_the_realm_it_names_and_only_the_keys_it_carries()
        {
            var prefs = TwoRealms();
            var realms = new RealmStore();
            realms.Realms.AddRange(prefs.Servers);

            var block = new JObject { ["Realm"] = "Beta", ["On"] = false, ["Title"] = "  Beta's hall ", ["ThreadId"] = "", ["SharePassword"] = true };
            var realm = BlendWindow.HeraldBlockRealm(block, "Alpha", realms);
            Assert.Equal("Beta", realm);
            Assert.True(BlendWindow.ApplyHeraldBlock(prefs, block, realm));

            var beta = HeraldRealms.Find(prefs, "Beta");
            Assert.False(beta.HeraldOn);
            Assert.Equal("Beta's hall", beta.HeraldTitle);
            Assert.Null(beta.HeraldThreadId);
            Assert.True(beta.HeraldSharePassword);
            Assert.Equal(HookB, beta.HeraldWebhookUrl); // not carried, not touched
            Assert.Equal("Alpha's hall", HeraldRealms.Find(prefs, "Alpha").HeraldTitle);
            Assert.True(HeraldRealms.Find(prefs, "Alpha").HeraldOn);

            // The same block again moves nothing, and a switch that is not a switch is left alone.
            Assert.False(BlendWindow.ApplyHeraldBlock(prefs, block, realm));
            Assert.False(BlendWindow.ApplyHeraldBlock(prefs, new JObject { ["On"] = "yes", ["ShareAddress"] = null }, "Beta"));

            // No realm named: the realm on screen. A realm that is gone is refused by name, in the
            // catalog's own words, before anything is written.
            Assert.Equal("Alpha", BlendWindow.HeraldBlockRealm(new JObject { ["On"] = true }, "Alpha", realms));
            Assert.Null(BlendWindow.HeraldBlockRealm(null, "Alpha", realms));
            // Naming none while the realm on screen has no profile yet leaves the block out rather
            // than refusing the app-wide switches saved beside it.
            Assert.Null(BlendWindow.HeraldBlockRealm(new JObject { ["On"] = true }, "Not Saved Yet", realms));
            var gone = Assert.Throws<HostFacingException>(() =>
                BlendWindow.HeraldBlockRealm(new JObject { ["Realm"] = "Deleted" }, "Alpha", realms));
            Assert.Equal("userprefs.save.heraldNoRealm", gone.MessageId);
            Assert.Equal(English("herald.realm.gone.save"), gone.Message);
        }

        [Fact]
        public void Only_an_app_wide_value_that_moved_reaches_every_realms_post()
        {
            var prefs = TwoRealms();
            var before = BlendWindow.HeraldAppWideKey(prefs);
            prefs.DiscordEventPosts = !prefs.DiscordEventPosts;
            Assert.Equal(before, BlendWindow.HeraldAppWideKey(prefs));
            foreach (Action<UserPreferences> move in new Action<UserPreferences>[]
                     {
                         p => p.DiscordSharingEnabled = false, p => p.DiscordWebhookUrl = HookB, p => p.DiscordWebhookThreadId = "1",
                         p => p.DiscordBotName = "Raven", p => p.DiscordAvatarUrl = "https://i.imgur.com/x.png",
                         p => p.DiscordLinkToBakaLoader = false, p => p.CustomJoinDomain = "valheim.example.com",
                     })
            {
                var p = TwoRealms();
                move(p);
                Assert.NotEqual(before, BlendWindow.HeraldAppWideKey(p));
            }
        }

        // ------------------------------------------------------------------ the snapshot

        [Fact]
        public void The_snapshot_is_built_for_the_realm_asked_and_never_picks_one()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"].Replace("\r\n", "\n");
            var at = bridge.IndexOf("private DiscordStatusSnapshot BuildDiscordSnapshot(string profile)", StringComparison.Ordinal);
            Assert.True(at > 0, "the snapshot no longer takes the realm");
            var body = bridge.Substring(at, bridge.IndexOf("private object BuildSetupStatus()", at, StringComparison.Ordinal) - at);
            Assert.Contains("Sessions.TryGetValue(profile, out var session);", body);
            Assert.DoesNotContain("OrderByDescending", body);
            Assert.DoesNotContain("Sessions.Values", body);
            Assert.Contains("DiscordStatus.SnapshotProvider = BuildDiscordSnapshot;", bridge);

            // Every request names its realm; only the app-wide triggers ask for every realm.
            Assert.DoesNotContain("DiscordStatus.RequestUpdate();", bridge);
            Assert.Contains("DiscordStatus.RequestUpdate(profile);", bridge);
            Assert.Contains("DiscordStatus.RequestUpdate(player?.ServerKey ?? ActiveProfileName);", bridge);
        }

        // ------------------------------------------------------------------ the real RPC table

        /// <summary>
        /// A BlendWindow with its bridge wired over this test's providers: the Form is constructed
        /// (so it has no handle, shows nothing and posts nothing), the status service is the real
        /// one over the fake wire, every other interface is a loose mock, and the RPC table is the
        /// real one.
        /// </summary>
        private sealed class Hall : IDisposable
        {
            public readonly RoundTripping UserPrefs;
            public readonly Wire Wire = new();
            public readonly DiscordStatusService Status;
            public readonly BlendWindow Window;
            public readonly Dictionary<string, Func<JObject, Task<object>>> Rpc;
            public readonly List<string> Asked = new();

            public Hall(UserPreferences start, string onScreen)
            {
                UserPrefs = new RoundTripping(start);
                Status = new DiscordStatusService(UserPrefs, new Over(Wire), new Mock<IApplicationLogger>().Object)
                {
                    SnapshotProvider = realm =>
                    {
                        lock (Asked) Asked.Add(realm);
                        return new DiscordStatusSnapshot { ServerName = realm, StatusText = "Offline", AppVersion = "1.2.16" };
                    },
                    WaitOut = _ => Task.CompletedTask,
                };

                Window = (BlendWindow)RuntimeHelpers.GetUninitializedObject(typeof(BlendWindow));
                // No Form under the bridge any more: the headless host stands in for the window.
                Window.Ui = new HeadlessHostUi(Serilog.Log.Logger);
                var serilog = new Mock<Serilog.ILogger>().Object;
                var appLogger = new Mock<IApplicationLogger>().Object;
                var known = new Dictionary<Type, object>
                {
                    [typeof(IUserPreferencesProvider)] = UserPrefs,
                    [typeof(IServerPreferencesProvider)] = new ServerPreferencesProvider(UserPrefs, serilog),
                    [typeof(IWorldPreferencesProvider)] = new WorldPreferencesProvider(UserPrefs, serilog),
                    [typeof(IDiscordStatusService)] = Status,
                    [typeof(IApplicationLogger)] = appLogger,
                    [typeof(Serilog.ILogger)] = serilog,
                };
                Fill(Window, typeof(object), known);
                typeof(BlendWindow).GetMethod("RegisterRpcHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, null);
                Rpc = (Dictionary<string, Func<JObject, Task<object>>>)typeof(BlendWindow)
                    .GetField("RpcHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);
                typeof(BlendWindow).GetField("CurrentProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, onScreen);
            }

            public async Task<JToken> Call(string method, object args)
            {
                var json = JsonConvert.SerializeObject(await Rpc[method](JObject.FromObject(args)));
                using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
                return JToken.ReadFrom(reader);
            }

            public ServerPreferences Realm(string name) => HeraldRealms.Find(UserPrefs.LoadPreferences(), name);

            public void Dispose()
            {
                // Nothing to dispose: the bridge holds no window.
            }
        }

        private static void Fill(object target, Type stopAt, Dictionary<Type, object> known)
        {
            for (var type = target.GetType(); type != null && type != stopAt; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (field.GetValue(target) != null) continue;
                    var value = DefaultFor(field.FieldType, known);
                    if (value != null) field.SetValue(target, value);
                }
            }
        }

        private static object DefaultFor(Type t, Dictionary<Type, object> known)
        {
            if (known.TryGetValue(t, out var k)) return k;
            if (t.IsValueType || t == typeof(string)) return null;

            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                if (def == typeof(System.Collections.Concurrent.ConcurrentDictionary<,>) && t.GetGenericArguments()[0] == typeof(string))
                    return Activator.CreateInstance(t, StringComparer.OrdinalIgnoreCase);
                if (def == typeof(Dictionary<,>) && t.GetGenericArguments()[0] == typeof(string))
                    return Activator.CreateInstance(t, StringComparer.OrdinalIgnoreCase);
                if (def == typeof(HashSet<>) && t.GetGenericArguments()[0] == typeof(string))
                    return Activator.CreateInstance(t, StringComparer.OrdinalIgnoreCase);
            }

            if (t == typeof(object)) return new object();

            if (t.IsInterface)
            {
                var mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(t))!;
                mock.DefaultValue = DefaultValue.Empty;
                return mock.Object;
            }

            if (!t.IsAbstract && t.Namespace != null && t.Namespace.StartsWith("System", StringComparison.Ordinal)
                && t.GetConstructor(Type.EmptyTypes) != null)
                return Activator.CreateInstance(t);

            return null;
        }

        [Fact]
        public async Task Userprefs_get_hands_the_post_to_the_realm_on_screen_once_and_hands_back_every_realms_block()
        {
            var old = ReadOld();
            using var hall = new Hall(old, "Beta");

            var up = await hall.Call("userprefs.get", new { });
            var blocks = ((JArray)up["HeraldRealms"]).ToDictionary(b => (string)b["Realm"]);
            Assert.Equal(new[] { "Alpha", "Beta" }, blocks.Keys.OrderBy(k => k).ToArray()); // Gamma is archived
            Assert.True((bool)blocks["Beta"]["On"]);
            Assert.True((bool)blocks["Beta"]["HasPost"]);
            Assert.Equal(HookA, (string)blocks["Beta"]["WebhookUrl"]);
            Assert.False((bool)blocks["Alpha"]["On"]);
            Assert.False((bool)blocks["Alpha"]["HasPost"]);
            Assert.True((bool)up["HasDiscordStatusMessage"]);
            Assert.True(hall.UserPrefs.LoadPreferences().HeraldPerRealmMigrated);

            // The realm on screen moves and the hall reads again: the post stays where it went.
            typeof(BlendWindow).GetField("CurrentProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(hall.Window, "Alpha");
            up = await hall.Call("userprefs.get", new { });
            Assert.False((bool)up["HasDiscordStatusMessage"]);
            Assert.Equal("5550001", hall.Realm("Beta").HeraldStatusMessageId);
            Assert.Null(hall.Realm("Alpha").HeraldStatusMessageId);
            Assert.Empty(hall.Wire.Seen);
        }

        [Fact]
        public async Task The_halls_save_writes_the_named_realm_and_publish_and_remove_act_on_it()
        {
            using var hall = new Hall(TwoRealms(), "Alpha");

            var saved = await hall.Call("userprefs.save", new
            {
                prefs = new
                {
                    DiscordSharingEnabled = true,
                    DiscordWebhookUrl = HookA,
                    DiscordWebhookThreadId = "777",
                    Herald = new { Realm = "Off", On = true, WebhookUrl = "", ThreadId = "", ShareAddress = false, SharePassword = false, Title = "The quiet one", StatusLine = "" },
                },
            });
            Assert.NotNull(saved);
            var off = hall.Realm("Off");
            Assert.True(off.HeraldOn);
            Assert.Equal("The quiet one", off.HeraldTitle);
            Assert.False(off.HeraldShareAddress);
            Assert.Equal("Alpha's hall", hall.Realm("Alpha").HeraldTitle);

            var published = await hall.Call("discord.publish", new { profile = "Off" });
            Assert.True((bool)published["ok"], (string)published["error"]);
            var post = Assert.Single(hall.Wire.Seen);
            Assert.Equal(HookA + "?thread_id=777&wait=true", post.Url);
            Assert.Equal("The quiet one", (string)JObject.Parse(post.Body)["embeds"][0]["title"]);
            Assert.Equal(new[] { "Off" }, hall.Asked);
            Assert.NotNull(hall.Realm("Off").HeraldStatusMessageId);
            Assert.Null(hall.Realm("Alpha").HeraldStatusMessageId);

            var removed = await hall.Call("discord.remove", new { profile = "Off" });
            Assert.True((bool)removed["ok"]);
            Assert.Equal("DELETE", hall.Wire.Seen[1].Method);
            Assert.Null(hall.Realm("Off").HeraldStatusMessageId);

            // A realm that is gone is refused by name, in the catalog's own words, and nothing goes out.
            var gone = await Assert.ThrowsAsync<HostFacingException>(() => hall.Call("discord.publish", new { profile = "Deleted" }));
            Assert.Equal("discord.publish.noSuchRealm", gone.MessageId);
            Assert.Equal(English("herald.realm.gone.send"), gone.Message);
            var goneSave = await Assert.ThrowsAsync<HostFacingException>(() =>
                hall.Call("userprefs.save", new { prefs = new { Herald = new { Realm = "Deleted", On = true } } }));
            Assert.Equal("userprefs.save.heraldNoRealm", goneSave.MessageId);
            Assert.Equal(2, hall.Wire.Seen.Count);
        }

        [Fact]
        public async Task A_publish_with_the_realm_off_is_refused_and_the_retired_master_is_not_read()
        {
            var prefs = TwoRealms();
            prefs.DiscordSharingEnabled = true;
            prefs.DiscordWebhookUrl = HookA;
            using var hall = new Hall(prefs, "Alpha");

            // The realm's own switch off, with the master on.
            Change(hall, p => HeraldRealms.Find(p, "Alpha").HeraldOn = false);
            var realmOff = await Assert.ThrowsAsync<HostFacingException>(() => hall.Call("discord.publish", new { profile = "Alpha" }));
            Assert.Equal("discord.publish.off", realmOff.MessageId);
            Assert.Equal(English("herald.realm.off.send"), realmOff.Message);

            Assert.Empty(hall.Wire.Seen);
            Assert.Null(hall.Realm("Alpha").HeraldStatusMessageId);

            // The realm on, the old master switched off in the file. Since 1.2.17 the master is
            // retired: the move switched every realm off once instead, and it is read as on from
            // then on, so the press goes out. (Before 1.2.16 this press was refused.)
            Assert.True(hall.UserPrefs.LoadPreferences().HeraldSimplifiedMigrated);
            Change(hall, p => { var a = HeraldRealms.Find(p, "Alpha"); a.HeraldOn = true; a.HeraldWebhookUrl = HookA; p.DiscordSharingEnabled = false; });
            var sent = await hall.Call("discord.publish", new { profile = "Alpha" });
            Assert.True((bool)sent["ok"], (string)sent["error"]);
            Assert.Single(hall.Wire.Seen);
        }

        private static void Change(Hall hall, Action<UserPreferences> change)
        {
            var prefs = hall.UserPrefs.LoadPreferences();
            change(prefs);
            hall.UserPrefs.SavePreferences(prefs);
        }

        [Fact]
        public async Task A_hall_that_asks_while_startup_checks_for_a_running_server_leaves_the_move_to_startup()
        {
            using var hall = new Hall(ReadOld(), "Beta");
            var held = typeof(BlendWindow).GetField("HeraldMoveHeldForStartup", BindingFlags.Instance | BindingFlags.NonPublic)!;

            held.SetValue(hall.Window, true);
            await hall.Call("userprefs.get", new { });
            Assert.False(hall.UserPrefs.LoadPreferences().HeraldPerRealmMigrated);

            held.SetValue(hall.Window, false);
            await hall.Call("userprefs.get", new { });
            Assert.True(hall.UserPrefs.LoadPreferences().HeraldPerRealmMigrated);
            Assert.Equal("5550001", hall.Realm("Beta").HeraldStatusMessageId);
        }

        [Fact]
        public async Task Deleting_or_removing_a_realm_takes_its_post_down_first_and_a_rename_carries_it()
        {
            var prefs = TwoRealms();
            prefs.Servers[0].HeraldStatusMessageId = "1001";
            prefs.Servers[1].HeraldStatusMessageId = "2002";
            prefs.Servers.Add(new ServerPreferences { ProfileName = "Spare", Name = "Spare", HeraldOn = true, HeraldStatusMessageId = "3003" });
            using var hall = new Hall(prefs, "Off");
            hall.Wire.Answer = (r, n) => new HttpResponseMessage(HttpStatusCode.NoContent);

            await hall.Call("profiles.rename", new { name = "Beta", newName = "Beta Prime" });
            Assert.Equal("2002", hall.Realm("Beta Prime").HeraldStatusMessageId);
            Assert.True(hall.Realm("Beta Prime").HeraldOn);
            Assert.Empty(hall.Wire.Seen);

            await hall.Call("profiles.delete", new { name = "Alpha", deleteFiles = false });
            Assert.Null(hall.Realm("Alpha"));
            var delete = Assert.Single(hall.Wire.Seen);
            Assert.Equal("DELETE", delete.Method);
            Assert.Equal(HookA + "/messages/1001?thread_id=777", delete.Url);

            // Discord down: the realm still goes, and its post is let go of.
            hall.Wire.Answer = (r, n) => new HttpResponseMessage(HttpStatusCode.BadGateway);
            await hall.Call("profiles.remove", new { name = "Spare" });
            Assert.Null(hall.Realm("Spare"));
            Assert.Equal(HookA + "/messages/3003?thread_id=777", hall.Wire.Seen[1].Url);

            Assert.Equal("2002", hall.Realm("Beta Prime").HeraldStatusMessageId);
        }
    }
}
