using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
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
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The realm's event posts in three kinds (1.2.16, issue 24): Server news, Crashes, and
    /// players coming and going, each with its own switch, webhook and thread, and a role to ping
    /// on a crash.
    /// <para>
    /// What can go wrong without a build noticing: a kind posting to the realm's channel when it
    /// was given one of its own, a thread carried into a channel it does not live in, a kind or a
    /// realm or the master switched off and still sending, the crash ping reaching everyone, the
    /// update from 1.2.15 switching event posts off for the host who had them on (or on for one
    /// who had them off), a profile save forgetting the routing, and a mod update that moved mods
    /// saying nothing. Nothing here reaches Discord: every request lands in a handler in this
    /// file, and nothing touches the disk.
    /// </para>
    /// </summary>
    public class HeraldEventRoutingTests
    {
        private const string HookA = "https://discord.com/api/webhooks/111/aaaa";
        private const string HookB = "https://discord.com/api/webhooks/222/bbbb";
        private const string HookCrash = "https://discord.com/api/webhooks/333/cccc";
        private const string HookPlayers = "https://discord.com/api/webhooks/444/dddd";

        private static UserPreferences Prefs(Action<ServerPreferences> realm = null)
        {
            var r = new ServerPreferences
            {
                ProfileName = "Final Sunset", Name = "Final Sunset", HeraldOn = true,
                HeraldRoutineOn = true, HeraldCrashOn = true, HeraldPlayersOn = true,
            };
            realm?.Invoke(r);
            return new UserPreferences
            {
                HeraldPerRealmMigrated = true,
                DiscordSharingEnabled = true,
                DiscordWebhookUrl = HookA,
                DiscordWebhookThreadId = "777",
                Servers = new List<ServerPreferences> { r },
            };
        }

        private static (string Url, JObject Body)? Routed(UserPreferences prefs, HeraldEventClass kind, string realm = "Final Sunset")
        {
            var routed = DiscordWebhookService.Route(prefs, realm, kind, "Title", "Body", 1);
            return routed == null ? null : (routed.Value.Url, JObject.Parse(routed.Value.Json));
        }

        // ------------------------------------------------------------------ where each kind goes

        [Fact]
        public void Each_kind_goes_to_its_own_webhook_and_thread_and_a_blank_box_is_the_realms()
        {
            var prefs = Prefs(r =>
            {
                r.HeraldWebhookUrl = HookB;
                r.HeraldThreadId = "500";
                r.HeraldCrashWebhookUrl = HookCrash;
                r.HeraldCrashThreadId = "600";
                r.HeraldPlayersWebhookUrl = HookPlayers;
                r.HeraldRoutineThreadId = "700";
            });

            // Routine: the realm's webhook, its own thread.
            Assert.Equal(HookB + "?thread_id=700", Routed(prefs, HeraldEventClass.Routine).Value.Url);
            // Crash: its own webhook and thread.
            Assert.Equal(HookCrash + "?thread_id=600", Routed(prefs, HeraldEventClass.Crash).Value.Url);
            // Players: its own webhook and no thread, because the realm's thread lives in the
            // realm's channel and not in this one.
            Assert.Equal(HookPlayers, Routed(prefs, HeraldEventClass.Players).Value.Url);

            // Every box blank: the realm's webhook and thread for all three.
            var plain = Prefs(r => { r.HeraldWebhookUrl = HookB; r.HeraldThreadId = "500"; });
            foreach (var kind in new[] { HeraldEventClass.Routine, HeraldEventClass.Crash, HeraldEventClass.Players })
                Assert.Equal(HookB + "?thread_id=500", Routed(plain, kind).Value.Url);

            // A realm on the app-wide webhook: the app-wide thread, as the realm's own post has it.
            Assert.Equal(HookA + "?thread_id=777", Routed(Prefs(), HeraldEventClass.Routine).Value.Url);
            // And a kind's own thread wins inside the app-wide channel too.
            Assert.Equal(HookA + "?thread_id=9%209", Routed(Prefs(r => r.HeraldPlayersThreadId = "9 9"), HeraldEventClass.Players).Value.Url);
        }

        [Theory]
        [InlineData(HeraldEventClass.Routine)]
        [InlineData(HeraldEventClass.Crash)]
        [InlineData(HeraldEventClass.Players)]
        public void A_kind_off_a_realm_off_or_the_master_off_sends_nothing(HeraldEventClass kind)
        {
            Assert.NotNull(Routed(Prefs(), kind));

            // This kind off, the other two still go.
            var off = Prefs(r =>
            {
                if (kind == HeraldEventClass.Routine) r.HeraldRoutineOn = false;
                if (kind == HeraldEventClass.Crash) r.HeraldCrashOn = false;
                if (kind == HeraldEventClass.Players) r.HeraldPlayersOn = false;
            });
            Assert.Null(Routed(off, kind));
            foreach (var other in Enum.GetValues<HeraldEventClass>().Where(k => k != kind))
                Assert.NotNull(Routed(off, other));

            Assert.Null(Routed(Prefs(r => r.HeraldOn = false), kind));
            var master = Prefs();
            master.DiscordSharingEnabled = false;
            Assert.Null(Routed(master, kind));

            // A realm that is not there, and a realm with nowhere to post.
            Assert.Null(Routed(Prefs(), kind, "Gone"));
            var nowhere = Prefs();
            nowhere.DiscordWebhookUrl = null;
            Assert.Null(Routed(nowhere, kind));
            // A kind's own webhook is somewhere to post even with nothing app wide.
            var own = Prefs(r => { r.HeraldRoutineWebhookUrl = HookB; r.HeraldCrashWebhookUrl = HookB; r.HeraldPlayersWebhookUrl = HookB; });
            own.DiscordWebhookUrl = null;
            Assert.Equal(HookB, Routed(own, kind).Value.Url);
        }

        // ------------------------------------------------------------------ the crash ping

        [Fact]
        public void A_crash_pings_the_one_role_and_tells_Discord_that_role_is_the_only_mention()
        {
            var prefs = Prefs(r => r.HeraldCrashRoleId = "123456789012345678");

            var crash = Routed(prefs, HeraldEventClass.Crash).Value.Body;
            Assert.Equal("<@&123456789012345678>", (string)crash["content"]);
            Assert.True(JToken.DeepEquals(
                JObject.Parse("{\"parse\":[],\"roles\":[\"123456789012345678\"]}"),
                crash["allowed_mentions"]));

            // The role is the crash post's alone.
            foreach (var kind in new[] { HeraldEventClass.Routine, HeraldEventClass.Players })
            {
                var body = Routed(prefs, kind).Value.Body;
                Assert.Null(body["content"]);
                Assert.Null(body["allowed_mentions"]);
            }

            // No role: no content and no mention rules, as before.
            var none = Routed(Prefs(), HeraldEventClass.Crash).Value.Body;
            Assert.Null(none["content"]);
            Assert.Null(none["allowed_mentions"]);

            // A role id edited by hand into something else is left out rather than sent.
            var bad = Routed(Prefs(r => r.HeraldCrashRoleId = "@everyone"), HeraldEventClass.Crash).Value.Body;
            Assert.Null(bad["content"]);
            Assert.Null(bad["allowed_mentions"]);
        }

        [Theory]
        [InlineData("123456789012345678", true, "123456789012345678")]
        [InlineData(" <@&123456789012345678> ", true, "123456789012345678")]
        [InlineData("", true, null)]
        [InlineData(null, true, null)]
        [InlineData("@everyone", false, null)]
        [InlineData("<@123456789012345678>", false, null)]
        [InlineData("12 34", false, null)]
        [InlineData("1234", false, null)]
        public void A_role_id_is_digits_or_the_mention_a_host_pastes(string text, bool ok, string expected)
        {
            Assert.Equal(ok, HeraldRealms.TryReadRoleId(text, out var id));
            Assert.Equal(expected, id);
        }

        // ------------------------------------------------------------------ on the wire

        private sealed class Wire : HttpMessageHandler
        {
            public readonly List<(string Url, string Body)> Seen = new();
            public readonly TaskCompletionSource<bool> Landed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Expect = 1;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var body = await request.Content.ReadAsStringAsync(token);
                lock (Seen)
                {
                    Seen.Add((request.RequestUri.AbsoluteUri, body));
                    if (Seen.Count >= Expect) Landed.TrySetResult(true);
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        private sealed class Over : IHttpClientProvider
        {
            private readonly HttpMessageHandler Handler;
            public Over(HttpMessageHandler handler) => Handler = handler;
            public HttpClient CreateClient() => new(Handler, disposeHandler: false);
        }

        private sealed class Store : IUserPreferencesProvider
        {
            public UserPreferences Current;
            public event EventHandler<UserPreferences> PreferencesSaved;
            public UserPreferences LoadPreferences() => Current;
            public void SavePreferences(UserPreferences preferences) { Current = preferences; PreferencesSaved?.Invoke(this, preferences); }
        }

        [Fact]
        public async Task Every_event_reaches_the_place_of_its_kind()
        {
            var prefs = Prefs(r =>
            {
                r.HeraldRoutineWebhookUrl = HookB;
                r.HeraldCrashWebhookUrl = HookCrash;
                r.HeraldCrashRoleId = "123456789012345678";
                r.HeraldPlayersWebhookUrl = HookPlayers;
            });
            var wire = new Wire { Expect = 11 };
            var events = new DiscordWebhookService(new Store { Current = prefs }, new Over(wire), new Mock<IApplicationLogger>().Object);

            events.SendServerStarted("Final Sunset", "Final Sunset");
            events.SendServerStopped("Final Sunset", "Final Sunset");
            events.SendLaunchHeld("Final Sunset", "Final Sunset", "A Steam update is waiting.");
            events.SendServerUpdated("Final Sunset", "Final Sunset", "123", true);
            events.SendServerUpdateFailed("Final Sunset", "Final Sunset", "Steam said no.");
            events.SendLegacyWorldLoaded("Final Sunset", "Final Sunset", "Midgard");
            events.SendCheatMarksCleared("Final Sunset", "Final Sunset", "3 items cleared");
            events.SendModsUpdated("Final Sunset", "Final Sunset", new[] { new ModVersionMove("Azumatt-AzuCraftyBoxes", "1.0.0", "1.1.0") });
            events.SendServerCrashed("Final Sunset", "Final Sunset", true, 10);
            events.SendPlayerJoined("Final Sunset", "Ragnar", "Final Sunset");
            events.SendPlayerLeft("Final Sunset", "Ragnar", "Final Sunset");

            Assert.Same(wire.Landed.Task, await Task.WhenAny(wire.Landed.Task, Task.Delay(TimeSpan.FromSeconds(10))));
            var byUrl = wire.Seen.GroupBy(s => s.Url).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(8, byUrl[HookB]);
            Assert.Equal(1, byUrl[HookCrash]);
            Assert.Equal(2, byUrl[HookPlayers]);
            Assert.False(byUrl.ContainsKey(HookA));

            var crash = JObject.Parse(wire.Seen.Single(s => s.Url == HookCrash).Body);
            Assert.Equal("<@&123456789012345678>", (string)crash["content"]);
            Assert.Equal(new[] { "123456789012345678" }, crash["allowed_mentions"]["roles"].Select(t => (string)t).ToArray());
            Assert.Empty((JArray)crash["allowed_mentions"]["parse"]);
            Assert.All(wire.Seen.Where(s => s.Url != HookCrash), s => Assert.Null(JObject.Parse(s.Body)["content"]));
        }

        // ------------------------------------------------------------------ the mods updated post

        [Fact]
        public void The_mods_updated_post_names_each_mod_moved_and_cuts_a_long_run()
        {
            var results = new List<ModUpdateResult>
            {
                ModUpdateResult.Success(new InstalledMod { Author = "Azumatt", ModName = "AzuCraftyBoxes" }, "1.0.0", "1.1.0"),
                ModUpdateResult.Skipped(new InstalledMod { Author = "Same", ModName = "Old", InstalledVersion = "2.0.0" }),
                ModUpdateResult.Failed(new InstalledMod { Author = "Broken", ModName = "Mod", InstalledVersion = "3.0.0" }, "404"),
            };
            var moved = BlendWindow.ModsMoved(results);
            var only = Assert.Single(moved);
            Assert.Equal("1.0.0", only.FromVersion);
            Assert.Equal("1.1.0", only.ToVersion);
            Assert.Contains("AzuCraftyBoxes", only.Mod);
            Assert.Empty(BlendWindow.ModsMoved(null));

            var body = DiscordWebhookService.ModsUpdatedBody("Final Sunset", moved);
            Assert.Contains("**Final Sunset**", body);
            Assert.Contains(only.Mod + "` 1.0.0 → 1.1.0", body);
            Assert.DoesNotContain("{", body);

            var many = Enumerable.Range(1, 25).Select(i => new ModVersionMove("Mod" + i, "1.0." + i, "1.1." + i)).ToList();
            var cut = DiscordWebhookService.ModsUpdatedBody("Final Sunset", many);
            Assert.Contains("Mod20`", cut);
            Assert.DoesNotContain("Mod21`", cut);
            Assert.EndsWith("and 5 more", cut);
            Assert.True(cut.Length < 4096);
        }

        [Fact]
        public void A_mod_update_that_moved_mods_posts_once_from_every_update_road()
        {
            // The one place every road writes its results, driven for real on a window that is
            // never shown: a run that moved mods posts once, a run that moved none posts nothing.
            var window = (BlendWindow)RuntimeHelpers.GetUninitializedObject(typeof(BlendWindow));
            // No Form under the bridge any more: the headless host stands in for the window.
            window.Ui = new HeadlessHostUi(Serilog.Log.Logger);
            try
            {
                var hooks = new Mock<IDiscordWebhookService>();
                var realms = new Mock<IServerPreferencesProvider>();
                realms.Setup(r => r.LoadPreferences("Final Sunset")).Returns(new ServerPreferences { ProfileName = "Final Sunset", Name = "Sunset Hall" });
                void Set(string field, object value) =>
                    typeof(BlendWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
                Set("DiscordWebhooks", hooks.Object);
                Set("ServerPrefsProvider", realms.Object);
                Set("Analytics", new Mock<IAnalyticsService>().Object);
                var record = typeof(BlendWindow).GetMethod("RecordModUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!;

                record.Invoke(window, new object[] { "Final Sunset", new List<ModUpdateResult>
                {
                    ModUpdateResult.Success(new InstalledMod { Author = "A", ModName = "One" }, "1", "2"),
                    ModUpdateResult.Success(new InstalledMod { Author = "B", ModName = "Two" }, "3", "4"),
                } });
                record.Invoke(window, new object[] { "Final Sunset", new List<ModUpdateResult>
                {
                    ModUpdateResult.Skipped(new InstalledMod { Author = "C", ModName = "Three", InstalledVersion = "5" }),
                } });

                hooks.Verify(h => h.SendModsUpdated("Final Sunset", "Sunset Hall",
                    It.Is<IReadOnlyList<ModVersionMove>>(m => m.Count == 2 && m[0].ToVersion == "2" && m[1].FromVersion == "3")), Times.Once);
                hooks.Verify(h => h.SendModsUpdated(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ModVersionMove>>()), Times.Once);
            }
            finally
            {
                // Nothing to dispose: the bridge holds no window.
            }

            // Every road that updates mods writes through it: Update all, one row's Update and a
            // restart's auto update.
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            foreach (var road in new[] { "RegisterRpc(\"mods.updateAll\"", "RegisterRpc(\"mods.update\"", "session.Server.ApplyModUpdates = async stop =>" })
            {
                var at = bridge.IndexOf(road, StringComparison.Ordinal);
                Assert.True(at > 0, road);
                Assert.Contains("RecordModUpdates(", bridge.Substring(at, 2600));
            }
        }

        // ------------------------------------------------------------------ the update from 1.2.15

        private static UserPreferences Old(bool eventPosts) => UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(@"{
  ""discordWebhookUrl"": """ + HookA + @""",
  ""discordSharingEnabled"": true,
  ""discordEventPosts"": " + (eventPosts ? "true" : "false") + @",
  ""discordStatusMessageId"": ""5550001"",
  ""servers"": [
    { ""profileName"": ""Alpha"", ""name"": ""Alpha"", ""port"": 2456 },
    { ""profileName"": ""Beta"", ""name"": ""Beta"", ""port"": 2466 }
  ]
}"));

        [Fact]
        public void The_old_event_switch_becomes_all_three_kinds_on_the_realm_that_took_the_post()
        {
            var on = Old(true);
            Assert.True(HeraldRealms.Migrate(on, "Beta"));
            var beta = HeraldRealms.Find(on, "Beta");
            Assert.True(beta.HeraldRoutineOn && beta.HeraldCrashOn && beta.HeraldPlayersOn);
            // Through the realm's own webhook, which is the one the events went through before.
            foreach (var kind in Enum.GetValues<HeraldEventClass>())
                Assert.Equal(HookA, Routed(on, kind, "Beta").Value.Url);
            var alpha = HeraldRealms.Find(on, "Alpha");
            Assert.False(alpha.HeraldRoutineOn || alpha.HeraldCrashOn || alpha.HeraldPlayersOn);
            foreach (var kind in Enum.GetValues<HeraldEventClass>())
                Assert.Null(Routed(on, kind, "Alpha"));

            var off = Old(false);
            Assert.True(HeraldRealms.Migrate(off, "Beta"));
            var quiet = HeraldRealms.Find(off, "Beta");
            Assert.False(quiet.HeraldRoutineOn || quiet.HeraldCrashOn || quiet.HeraldPlayersOn);
            Assert.True(quiet.HeraldOn); // the status post still moved

            // Once: a host who then turns a kind off keeps it off.
            beta.HeraldPlayersOn = false;
            Assert.False(HeraldRealms.Migrate(on, "Beta"));
            Assert.False(beta.HeraldPlayersOn);
        }

        // ------------------------------------------------------------------ the file and the hall

        [Fact]
        public void The_routing_survives_the_file_a_profile_save_and_an_old_profile_reads_as_off()
        {
            var realm = new ServerPreferences
            {
                ProfileName = "Final Sunset",
                HeraldRoutineOn = true, HeraldRoutineWebhookUrl = HookB, HeraldRoutineThreadId = "1",
                HeraldCrashOn = true, HeraldCrashWebhookUrl = HookCrash, HeraldCrashThreadId = "2", HeraldCrashRoleId = "123456789012345678",
                HeraldPlayersOn = true, HeraldPlayersWebhookUrl = HookPlayers, HeraldPlayersThreadId = "3",
            };
            var json = JsonConvert.SerializeObject(realm.ToFile());
            foreach (var key in new[] { "heraldRoutineOn", "heraldRoutineWebhookUrl", "heraldRoutineThreadId", "heraldCrashOn",
                                         "heraldCrashWebhookUrl", "heraldCrashThreadId", "heraldCrashRoleId", "heraldPlayersOn",
                                         "heraldPlayersWebhookUrl", "heraldPlayersThreadId" })
                Assert.Contains("\"" + key + "\"", json);
            var back = ServerPreferences.FromFile(JsonConvert.DeserializeObject<ServerPreferencesFile>(json));
            Assert.Equal(JsonConvert.SerializeObject(realm.ToFile()), JsonConvert.SerializeObject(back.ToFile()));

            // The settings form carries no Herald boxes, so its save keeps what is on disk.
            var stale = new ServerPreferences { ProfileName = "Final Sunset" }.KeepHeraldFrom(back);
            Assert.Equal(JsonConvert.SerializeObject(back.ToFile()), JsonConvert.SerializeObject(stale.ToFile()));

            var old = ServerPreferences.FromFile(JsonConvert.DeserializeObject<ServerPreferencesFile>(@"{ ""profileName"": ""Old"" }"));
            Assert.False(old.HeraldRoutineOn || old.HeraldCrashOn || old.HeraldPlayersOn);
            Assert.Null(old.HeraldCrashRoleId);
        }

        private static string English(string id) =>
            (string)JObject.Parse(File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(), "ValheimBakaLoader", "WebUI", "i18n", "en.json")))
                ["keys"][id]["lore"];

        [Fact]
        public void The_halls_block_sets_each_kind_and_a_box_that_is_not_one_refuses_the_save()
        {
            var prefs = Prefs(r => { r.HeraldRoutineOn = false; r.HeraldCrashOn = false; r.HeraldPlayersOn = false; });
            var block = new JObject
            {
                ["RoutineOn"] = true, ["RoutineWebhookUrl"] = " " + HookB + " ", ["RoutineThreadId"] = "",
                ["CrashOn"] = true, ["CrashWebhookUrl"] = "", ["CrashThreadId"] = "42", ["CrashRoleId"] = "<@&123456789012345678>",
                ["PlayersOn"] = false, ["PlayersWebhookUrl"] = HookPlayers, ["PlayersThreadId"] = null,
            };
            BlendWindow.CheckHeraldEvents(block);
            // None of it shows on the status post, so the post is not asked to move for it.
            Assert.False(BlendWindow.ApplyHeraldBlock(prefs, block, "Final Sunset"));

            var r = prefs.Servers[0];
            Assert.True(r.HeraldRoutineOn);
            Assert.Equal(HookB, r.HeraldRoutineWebhookUrl);
            Assert.Null(r.HeraldRoutineThreadId);
            Assert.True(r.HeraldCrashOn);
            Assert.Null(r.HeraldCrashWebhookUrl);
            Assert.Equal("42", r.HeraldCrashThreadId);
            Assert.Equal("123456789012345678", r.HeraldCrashRoleId);
            Assert.False(r.HeraldPlayersOn);
            Assert.Equal(HookPlayers, r.HeraldPlayersWebhookUrl);

            // The page's block hands all of it back.
            var dto = JArray.FromObject(BlendWindow.BuildHeraldRealmsDto(prefs))[0];
            Assert.True((bool)dto["RoutineOn"]);
            Assert.Equal(HookB, (string)dto["RoutineWebhookUrl"]);
            Assert.Equal("123456789012345678", (string)dto["CrashRoleId"]);
            Assert.Equal("42", (string)dto["CrashThreadId"]);
            Assert.False((bool)dto["PlayersOn"]);

            var hook = Assert.Throws<HostFacingException>(() => BlendWindow.CheckHeraldEvents(new JObject { ["CrashWebhookUrl"] = "https://example.com/hook" }));
            Assert.Equal("userprefs.save.badEventWebhook", hook.MessageId);
            Assert.Equal(English("herald.events.hook.refused"), hook.Message);
            var role = Assert.Throws<HostFacingException>(() => BlendWindow.CheckHeraldEvents(new JObject { ["CrashRoleId"] = "@everyone" }));
            Assert.Equal("userprefs.save.badCrashRole", role.MessageId);
            Assert.Equal(English("herald.events.role.refused"), role.Message);
            BlendWindow.CheckHeraldEvents(null);
            BlendWindow.CheckHeraldEvents(new JObject { ["CrashRoleId"] = "", ["PlayersWebhookUrl"] = "" });
        }
    }
}
