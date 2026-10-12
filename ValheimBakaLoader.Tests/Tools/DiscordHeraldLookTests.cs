using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The Herald's look (1.2.14): the name and picture a post is signed with, the status post's
    /// title and a status line of the host's own, and the BakaLoader line that links to the
    /// project page.
    /// <para>
    /// What can go wrong without a build noticing: a blank box sent as an empty key (Discord
    /// refuses an empty username), a picture link that is not a picture reaching Discord, the
    /// switch off and the author line still there, the event posts signed one way and the
    /// status post another, a name change that never shows because Discord fixes a post's name
    /// when it is made, and a preference that does not survive the file. Nothing here reaches
    /// Discord: every request lands in a handler in this file.
    /// </para>
    /// </summary>
    public class DiscordHeraldLookTests
    {
        private const string Name = "Ravenhold Herald";
        private const string Avatar = "https://example.org/pics/raven.png";
        private const string Title = "Ravenhold, the long winter";
        private const string Status = "Ashlands opens Friday at eight.";
        private const string Hook = "https://discord.com/api/webhooks/123456789/abcDEF_ghi-jkl";

        private static UserPreferences Prefs(bool name, bool avatar, bool title, bool status, bool link) => new()
        {
            DiscordBotName = name ? Name : null,
            DiscordAvatarUrl = avatar ? Avatar : null,
            DiscordTitle = title ? Title : null,
            DiscordStatusLine = status ? Status : null,
            DiscordLinkToBakaLoader = link,
        };

        private static DiscordStatusSnapshot Snapshot() => new()
        {
            ServerName = "Final Sunset",
            WorldName = "Midgard",
            StatusText = "Running",
            ServerRunning = true,
            PlayersOnline = 3,
            AppVersion = "1.2.14",
        };

        /// <summary>Every combination of the four boxes blank or set and the switch on or off.</summary>
        public static IEnumerable<object[]> EveryLook()
        {
            for (var bits = 0; bits < 32; bits++)
                yield return new object[] { (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0 };
        }

        [Theory]
        [MemberData(nameof(EveryLook))]
        public void The_status_post_carries_exactly_what_was_chosen(bool name, bool avatar, bool title, bool status, bool link)
        {
            var payload = JObject.Parse(DiscordStatusService.BuildEmbedPayload(Snapshot(), Prefs(name, avatar, title, status, link)));
            var embed = (JObject)payload["embeds"][0];

            // A blank name is no key at all: Discord refuses an empty username outright.
            if (name) Assert.Equal(Name, (string)payload["username"]);
            else Assert.False(payload.ContainsKey("username"), "a blank name was sent as a key");

            // A blank picture is the logo, never nothing.
            Assert.Equal(avatar ? Avatar : DiscordHeraldLook.LogoUrl, (string)payload["avatar_url"]);

            // The title is the host's when set and the server name as before when not.
            Assert.Equal(title ? Title : "Final Sunset", (string)embed["title"]);

            // The status line is the first paragraph of the description, and blank is no description.
            if (status) Assert.StartsWith(Status, (string)embed["description"]);
            else Assert.False(embed.ContainsKey("description"), "a blank status line was sent as an empty description");

            if (link)
            {
                var author = (JObject)embed["author"];
                Assert.Equal("BakaLoader", (string)author["name"]);
                Assert.Equal("https://github.com/RyanDMcAfee/ValheimBakaLoader", (string)author["url"]);
                Assert.Equal(DiscordHeraldLook.LogoUrl, (string)author["icon_url"]);
            }
            else
            {
                Assert.False(embed.ContainsKey("author"), "the switch is off and the BakaLoader line is still on the post");
            }

            // The rest of the post is the post it always was.
            Assert.NotEmpty((JArray)embed["fields"]);
            Assert.NotNull(embed["footer"]?["text"]);
            Assert.Empty((JArray)payload["allowed_mentions"]["parse"]);
        }

        [Theory]
        [MemberData(nameof(EveryLook))]
        public void Every_event_post_is_signed_the_same_way_and_keeps_its_own_title(bool name, bool avatar, bool title, bool status, bool link)
        {
            var json = DiscordWebhookService.BuildEventPayload("Server Started", "**Final Sunset** is up.", 0x57F287,
                Prefs(name, avatar, title, status, link));
            var payload = JObject.Parse(json);
            var embed = (JObject)payload["embeds"][0];

            if (name) Assert.Equal(Name, (string)payload["username"]);
            else Assert.False(payload.ContainsKey("username"));
            Assert.Equal(avatar ? Avatar : DiscordHeraldLook.LogoUrl, (string)payload["avatar_url"]);
            Assert.Equal(link, embed.ContainsKey("author"));

            // The title says what happened and the description is the event's own sentence.
            Assert.Equal("Server Started", (string)embed["title"]);
            Assert.Equal("**Final Sunset** is up.", (string)embed["description"]);
        }

        [Theory]
        [InlineData("https://example.org/a.png", true)]
        [InlineData("https://example.org/a.JPG", true)]
        [InlineData("https://example.org/a.jpeg", true)]
        [InlineData("https://example.org/a.gif", true)]
        [InlineData("https://example.org/a.webp", true)]
        [InlineData("https://example.org/a.png?size=128", true)]
        [InlineData("https://raw.githubusercontent.com/someone/repo/main/logo", true)]
        [InlineData("https://cdn.discordapp.com/avatars/1/abc", true)]
        [InlineData("https://i.imgur.com/AbCdEf", true)]
        [InlineData("  https://i.imgur.com/AbCdEf.png  ", true)]
        [InlineData("http://example.org/a.png", false)]
        [InlineData("ftp://example.org/a.png", false)]
        [InlineData("https://example.org/a.png.html", false)]
        [InlineData("https://example.org/page", false)]
        [InlineData("https://imgur.com/AbCdEf", false)]
        [InlineData("https://i.imgur.com.evil.example/a", false)]
        [InlineData("https://user:pass@example.org/a.png", false)]
        [InlineData("javascript:alert(1)//.png", false)]
        [InlineData("example.org/a.png", false)]
        [InlineData("not a link", false)]
        [InlineData("", false)]
        public void A_picture_link_is_https_and_an_image(string url, bool ok)
        {
            Assert.Equal(ok, DiscordHeraldLook.IsImageUrl(url));
        }

        [Fact]
        public void Blank_is_never_a_refused_picture_or_name()
        {
            Assert.Null(DiscordHeraldLook.AvatarProblem(null));
            Assert.Null(DiscordHeraldLook.AvatarProblem("   "));
            Assert.Null(DiscordHeraldLook.NameProblem(null));
            Assert.Null(DiscordHeraldLook.NameProblem(""));
            Assert.Equal(DiscordHeraldLook.BadAvatarSentence, DiscordHeraldLook.AvatarProblem("http://example.org/a.png"));
        }

        [Theory]
        [InlineData("Ravenhold Herald", true)]
        [InlineData("The Discord Herald", false)]
        [InlineData("clyde", false)]
        [InlineData("DISCORD", false)]
        public void A_name_Discord_refuses_is_refused_here(string name, bool ok)
        {
            Assert.Equal(ok, DiscordHeraldLook.NameProblem(name) == null);
            Assert.False(DiscordHeraldLook.NameProblem(new string('a', 81)) == null);
            Assert.True(DiscordHeraldLook.NameProblem(new string('a', 80)) == null);
        }

        /// <summary>
        /// A value that got into the file some other way than the save (a hand edit, an older
        /// build) is never sent: the logo takes a bad picture's place and a bad name is dropped.
        /// </summary>
        [Fact]
        public void A_stored_link_that_is_not_a_picture_is_never_sent()
        {
            var prefs = new UserPreferences { DiscordAvatarUrl = "http://tracker.example/x", DiscordBotName = "Discord Bot" };
            var status = JObject.Parse(DiscordStatusService.BuildEmbedPayload(Snapshot(), prefs));
            var ev = JObject.Parse(DiscordWebhookService.BuildEventPayload("t", "d", 1, prefs));
            foreach (var payload in new[] { status, ev })
            {
                Assert.Equal(DiscordHeraldLook.LogoUrl, (string)payload["avatar_url"]);
                Assert.False(payload.ContainsKey("username"));
                Assert.DoesNotContain("tracker.example", payload.ToString());
            }
        }

        [Fact]
        public void The_logo_and_the_icon_pass_the_same_check_and_the_file_is_a_small_square()
        {
            Assert.True(DiscordHeraldLook.IsImageUrl(DiscordHeraldLook.LogoUrl));
            Assert.StartsWith("https://raw.githubusercontent.com/RyanDMcAfee/ValheimBakaLoader/main/", DiscordHeraldLook.LogoUrl);

            // The url names a file the repository carries, and that file is a 256 pixel square
            // PNG well under the megabyte Discord fetches comfortably.
            var relative = DiscordHeraldLook.LogoUrl.Substring("https://raw.githubusercontent.com/RyanDMcAfee/ValheimBakaLoader/main/".Length);
            var file = Path.Combine(AppSourceTree.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), "the logo the post points at is not in the repository: " + relative);
            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.Length < 1024 * 1024, "the logo is a megabyte or more");
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray());
            int Be(int at) => (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
            Assert.Equal(256, Be(16));
            Assert.Equal(256, Be(20));
        }

        [Fact]
        public void A_long_title_or_status_line_is_cut_to_what_Discord_takes()
        {
            var prefs = new UserPreferences { DiscordTitle = new string('t', 400), DiscordStatusLine = new string('s', 900) };
            var embed = JObject.Parse(DiscordStatusService.BuildEmbedPayload(Snapshot(), prefs))["embeds"][0];
            Assert.Equal(DiscordHeraldLook.TitleMax, ((string)embed["title"]).Length);
            Assert.Equal(DiscordHeraldLook.StatusLineMax, ((string)embed["description"]).Length);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n")]
        public void A_blank_title_or_status_line_left_in_the_file_falls_back_to_the_server_name_and_no_line(string blank)
        {
            // A hand edited settings file can hold an empty or all space title; the post still
            // carries the server name and never an empty title or an empty description.
            var prefs = new UserPreferences { DiscordTitle = blank, DiscordStatusLine = blank };
            var embed = (JObject)JObject.Parse(DiscordStatusService.BuildEmbedPayload(Snapshot(), prefs))["embeds"][0];
            Assert.Equal("Final Sunset", (string)embed["title"]);
            Assert.False(embed.ContainsKey("description"), "a blank status line was sent as a description");
        }

        // ------------------------------------------------------------------ the preferences

        [Fact]
        public void The_look_survives_the_file()
        {
            var prefs = new UserPreferences
            {
                DiscordBotName = Name,
                DiscordAvatarUrl = Avatar,
                DiscordStatusLine = Status,
                DiscordTitle = Title,
                DiscordLinkToBakaLoader = false,
                DiscordStatusMessageSignature = "sig",
            };
            var json = JsonConvert.SerializeObject(prefs.ToFile());
            Assert.Contains("\"discordBotName\"", json);
            Assert.Contains("\"discordLinkToBakaLoader\":false", json);

            var back = UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(json));
            Assert.Equal(Name, back.DiscordBotName);
            Assert.Equal(Avatar, back.DiscordAvatarUrl);
            Assert.Equal(Status, back.DiscordStatusLine);
            Assert.Equal(Title, back.DiscordTitle);
            Assert.False(back.DiscordLinkToBakaLoader);
            Assert.Equal("sig", back.DiscordStatusMessageSignature);
        }

        [Fact]
        public void A_file_from_before_the_look_reads_as_all_blank_with_the_link_on()
        {
            var older = UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(
                "{\"discordWebhookUrl\":\"" + Hook + "\",\"discordSharingEnabled\":true}"));
            Assert.Null(older.DiscordBotName);
            Assert.Null(older.DiscordAvatarUrl);
            Assert.Null(older.DiscordStatusLine);
            Assert.Null(older.DiscordTitle);
            Assert.True(older.DiscordLinkToBakaLoader);
            Assert.True(new UserPreferences().DiscordLinkToBakaLoader);
        }

        /// <summary>The page's English for an id, read out of en.json, which the toast falls back to.</summary>
        private static string English(string id) =>
            (string)JObject.Parse(File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(), "ValheimBakaLoader", "WebUI", "i18n", "en.json")))
                ["keys"][id]["lore"];

        [Fact]
        public void The_save_refuses_a_bad_picture_or_name_by_name_and_takes_blank()
        {
            var bad = Assert.Throws<HostFacingException>(() =>
                BlendWindow.CheckHeraldLook(new JObject { ["DiscordAvatarUrl"] = "http://example.org/a.png" }));
            Assert.Equal("userprefs.save.badAvatarUrl", bad.MessageId);
            Assert.Equal(English("herald.look.avatar.refused"), bad.Message);

            var name = Assert.Throws<HostFacingException>(() =>
                BlendWindow.CheckHeraldLook(new JObject { ["DiscordBotName"] = "Discord" }));
            Assert.Equal("userprefs.save.badBotName", name.MessageId);
            Assert.Equal(English("herald.look.name.refused"), name.Message);

            BlendWindow.CheckHeraldLook(new JObject { ["DiscordAvatarUrl"] = "", ["DiscordBotName"] = "" });
            BlendWindow.CheckHeraldLook(new JObject { ["DiscordAvatarUrl"] = Avatar, ["DiscordBotName"] = Name });
            BlendWindow.CheckHeraldLook(new JObject { ["DiscordSharingEnabled"] = true });
        }

        // ------------------------------------------------------------------ the wire

        private sealed class Wire : HttpMessageHandler
        {
            public readonly List<(string Method, string Url, string Body)> Seen = new();
            public Func<HttpRequestMessage, HttpResponseMessage> Answer;
            public readonly TaskCompletionSource<bool> First = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(token);
                lock (Seen) Seen.Add((request.Method.Method, request.RequestUri.ToString(), body));
                First.TrySetResult(true);
                return Answer?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        private sealed class Over : ValheimBakaLoader.Tools.Http.IHttpClientProvider
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
            public void SavePreferences(UserPreferences preferences)
            {
                Current = preferences;
                PreferencesSaved?.Invoke(this, preferences);
            }
        }

        private static HttpResponseMessage Created(string id) =>
            new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"" + id + "\"}", Encoding.UTF8, "application/json") };

        private static (DiscordStatusService Service, Wire Wire, Store Store) Herald(UserPreferences prefs)
        {
            prefs.DiscordWebhookUrl = Hook;
            prefs.DiscordSharingEnabled = true;
            var wire = new Wire();
            var store = new Store { Current = prefs };
            var service = new DiscordStatusService(store, new Over(wire), new Mock<IApplicationLogger>().Object)
            {
                SnapshotProvider = Snapshot,
            };
            return (service, wire, store);
        }

        [Fact]
        public async Task A_new_post_is_signed_and_its_signature_is_kept_beside_its_id()
        {
            var (service, wire, store) = Herald(Prefs(true, true, true, true, true));
            wire.Answer = _ => Created("111");

            var result = await service.PublishNowAsync();

            Assert.True(result.Ok, result.Error);
            var post = Assert.Single(wire.Seen);
            Assert.Equal("POST", post.Method);
            var body = JObject.Parse(post.Body);
            Assert.Equal(Name, (string)body["username"]);
            Assert.Equal(Avatar, (string)body["avatar_url"]);
            Assert.Equal("111", store.Current.DiscordStatusMessageId);
            Assert.Equal(DiscordHeraldLook.SignatureFor(store.Current), store.Current.DiscordStatusMessageSignature);
        }

        [Fact]
        public async Task An_edit_of_a_post_signed_the_same_carries_no_name_or_picture()
        {
            var prefs = Prefs(true, true, true, true, true);
            prefs.DiscordStatusMessageId = "111";
            prefs.DiscordStatusMessageSignature = DiscordHeraldLook.SignatureFor(prefs);
            var (service, wire, store) = Herald(prefs);

            var result = await service.PublishNowAsync();

            Assert.True(result.Ok, result.Error);
            var edit = Assert.Single(wire.Seen);
            Assert.Equal("PATCH", edit.Method);
            Assert.EndsWith("/messages/111", edit.Url);
            var body = JObject.Parse(edit.Body);
            Assert.False(body.ContainsKey("username"));
            Assert.False(body.ContainsKey("avatar_url"));
            Assert.Equal(Title, (string)body["embeds"][0]["title"]);
            Assert.Equal("111", store.Current.DiscordStatusMessageId);
        }

        /// <summary>
        /// Discord fixes a webhook post's name and picture when the post is made, so a new name
        /// on an existing post can only show by making the post again: the old one is taken down
        /// first, so the channel never holds two.
        /// </summary>
        [Fact]
        public async Task A_new_name_or_picture_takes_the_post_down_and_makes_it_again()
        {
            var prefs = Prefs(false, false, false, false, true);
            prefs.DiscordStatusMessageId = "111";
            prefs.DiscordStatusMessageSignature = DiscordHeraldLook.SignatureFor(prefs);
            prefs.DiscordBotName = Name;
            var (service, wire, store) = Herald(prefs);
            wire.Answer = r => r.Method == HttpMethod.Post ? Created("222") : new HttpResponseMessage(HttpStatusCode.NoContent);

            var result = await service.PublishNowAsync();

            Assert.True(result.Ok, result.Error);
            Assert.Equal(new[] { "DELETE", "POST" }, wire.Seen.Select(s => s.Method).ToArray());
            Assert.EndsWith("/messages/111", wire.Seen[0].Url);
            Assert.Equal(Name, (string)JObject.Parse(wire.Seen[1].Body)["username"]);
            Assert.Equal("222", store.Current.DiscordStatusMessageId);
            Assert.Equal(DiscordHeraldLook.SignatureFor(store.Current), store.Current.DiscordStatusMessageSignature);
        }

        [Fact]
        public async Task A_post_that_cannot_be_taken_down_is_edited_rather_than_doubled()
        {
            var prefs = Prefs(true, false, false, false, true);
            prefs.DiscordStatusMessageId = "111";
            prefs.DiscordStatusMessageSignature = "an older look";
            var (service, wire, store) = Herald(prefs);
            wire.Answer = r => r.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK);

            var result = await service.PublishNowAsync();

            Assert.True(result.Ok, result.Error);
            Assert.Equal(new[] { "DELETE", "PATCH" }, wire.Seen.Select(s => s.Method).ToArray());
            Assert.DoesNotContain(wire.Seen, s => s.Method == "POST");
            Assert.Equal("111", store.Current.DiscordStatusMessageId);
        }

        [Fact]
        public async Task An_event_post_goes_out_signed()
        {
            var prefs = Prefs(true, true, false, false, true);
            prefs.DiscordWebhookUrl = Hook;
            var wire = new Wire();
            var events = new DiscordWebhookService(new Store { Current = prefs }, new Over(wire), new Mock<IApplicationLogger>().Object);

            events.SendServerStarted("Final Sunset");
            var landed = await Task.WhenAny(wire.First.Task, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.Same(wire.First.Task, landed);
            var post = Assert.Single(wire.Seen);
            var body = JObject.Parse(post.Body);
            Assert.Equal(Name, (string)body["username"]);
            Assert.Equal(Avatar, (string)body["avatar_url"]);
            Assert.Equal("BakaLoader", (string)body["embeds"][0]["author"]["name"]);
        }
    }
}
