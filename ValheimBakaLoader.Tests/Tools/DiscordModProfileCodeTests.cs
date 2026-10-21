using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The mod profile code on the Discord status post (1.2.11).
    /// <para>
    /// A realm can carry the share code its mod manager hands out, and the post shows it as one
    /// field straight after Mods, named for the manager it belongs to, in a code span so a
    /// player copies it in one go. What can be wrong without a build noticing: the field named
    /// for the wrong manager, a field standing there with no code in it instead of no field,
    /// a code that is not a code (a backtick, a mention, a link) reaching a channel, and the
    /// page and the host side disagreeing about which codes are codes.
    /// </para>
    /// </summary>
    public class DiscordModProfileCodeTests
    {
        private static JArray Fields(string kind, string code)
        {
            var snapshot = new DiscordStatusSnapshot
            {
                ServerName = "Final Sunset",
                WorldName = "Sunset",
                StatusText = "Online",
                ServerRunning = true,
                ModCount = 53,
                AppVersion = "1.2.11",
                ModProfileCodeKind = kind,
                ModProfileCode = code,
            };
            var payload = JObject.Parse(DiscordStatusService.BuildEmbedPayload(snapshot, new UserPreferences(), new ServerPreferences()));
            return (JArray)payload["embeds"][0]["fields"];
        }

        private static string Name(string id) => HostCatalog.T(id);

        [Theory]
        [InlineData("thunderstore", "host.status.field.mod_code.thunderstore", "018f5c2a-7d1e-4b3a-9f00-1c2d3e4f5a6b")]
        [InlineData("gale", "host.status.field.mod_code.gale", "AB12CD")]
        [InlineData("gale", "host.status.field.mod_code.gale", "Ab_9-xY_z0Qr7_Lm-NoPqA")]
        [InlineData("Gale", "host.status.field.mod_code.gale", "AB12CD")]
        public void A_code_is_one_field_after_mods_named_for_its_manager(string kind, string nameId, string code)
        {
            var fields = Fields(kind, "  " + code + "  ").ToList();

            var mods = fields.FindIndex(f => (string)f["name"] == Name("host.status.field.mods"));
            Assert.True(mods >= 0, "the post lost its Mods field");

            var named = fields.Where(f => (string)f["name"] == Name(nameId)).ToList();
            Assert.Single(named);
            Assert.Equal(mods + 1, fields.IndexOf(named[0]));
            Assert.Equal("`" + code + "`", (string)named[0]["value"]);
            Assert.True((bool)named[0]["inline"]);

            // And never the other manager's name as well.
            var other = nameId.EndsWith(".gale", StringComparison.Ordinal)
                ? "host.status.field.mod_code.thunderstore"
                : "host.status.field.mod_code.gale";
            Assert.DoesNotContain(fields, f => (string)f["name"] == Name(other));
        }

        [Theory]
        [InlineData("none", "018f5c2a-7d1e-4b3a")]  // a code with no manager chosen
        [InlineData(null, "AB12CD")]                 // an older profile with no kind at all
        [InlineData("thunderstore", "")]             // blank
        [InlineData("thunderstore", "   ")]          // blank after trimming
        [InlineData("gale", null)]                   // no code
        [InlineData("thunderstore", "`@everyone`")]  // not a code: kept out of the channel
        [InlineData("gale", "https://gale.example/AB12CD")]
        [InlineData("gale", "AB12")]                 // too short for either Gale shape
        [InlineData("modrinth", "AB12CD")]           // a kind nothing answers to
        public void No_code_or_no_kind_is_no_field_at_all(string kind, string code)
        {
            var fields = Fields(kind, code);

            Assert.DoesNotContain(fields, f => (string)f["name"] == Name("host.status.field.mod_code.thunderstore"));
            Assert.DoesNotContain(fields, f => (string)f["name"] == Name("host.status.field.mod_code.gale"));
            // The rest of the post is untouched by it.
            Assert.Contains(fields, f => (string)f["name"] == Name("host.status.field.mods"));
            Assert.Contains(fields, f => (string)f["name"] == Name("host.status.field.next_restart"));
        }

        [Fact]
        public void The_two_names_are_the_host_catalogs_and_say_which_manager()
        {
            Assert.Equal("Thunderstore code", HostCatalog.EmbeddedEnglish.Say("host.status.field.mod_code.thunderstore"));
            Assert.Equal("Gale code", HostCatalog.EmbeddedEnglish.Say("host.status.field.mod_code.gale"));

            var service = AppSourceTree.Files()["DiscordStatusService.cs"];
            Assert.Contains("HostCatalog.T(\"host.status.field.mod_code.gale\")", service);
            Assert.Contains("HostCatalog.T(\"host.status.field.mod_code.thunderstore\")", service);
        }

        [Fact]
        public void The_code_round_trips_through_the_profile_file()
        {
            var prefs = new ServerPreferences { ProfileName = "Final Sunset", ModProfileCodeKind = "gale", ModProfileCode = "AB12CD" };

            var file = prefs.ToFile();
            var json = JsonConvert.SerializeObject(file);
            Assert.Contains("\"modProfileCodeKind\":\"gale\"", json);
            Assert.Contains("\"modProfileCode\":\"AB12CD\"", json);

            var back = ServerPreferences.FromFile(JsonConvert.DeserializeObject<ServerPreferencesFile>(json));
            Assert.Equal("gale", back.ModProfileCodeKind);
            Assert.Equal("AB12CD", back.ModProfileCode);

            // A profile written before 1.2.11 has neither key and reads as no code.
            var older = ServerPreferences.FromFile(
                JsonConvert.DeserializeObject<ServerPreferencesFile>("{\"profileName\":\"Old\"}"));
            Assert.Equal(ModProfileCodes.None, older.ModProfileCodeKind);
            Assert.Null(older.ModProfileCode);

            // A spelling nothing answers to reads as none rather than as a third manager.
            var odd = ServerPreferences.FromFile(new ServerPreferencesFile { ModProfileCodeKind = "Modrinth", ModProfileCode = "  x  " });
            Assert.Equal(ModProfileCodes.None, odd.ModProfileCodeKind);
            Assert.Equal("x", odd.ModProfileCode);
        }

        /// <summary>
        /// The page checks a code before it sends it and the host side checks it again, with
        /// patterns written twice. If they drift, the page refuses a code the host would take or
        /// sends one the host refuses, which reads as a toast about a call that failed.
        /// </summary>
        [Fact]
        public void The_page_and_the_host_side_hold_the_same_two_patterns()
        {
            var app = AppSourceTree.Web("app.js");
            var ts = Regex.Match(app, @"\n  thunderstore:/(.+?)/,\n");
            var gale = Regex.Match(app, @"\n  gale:/(.+?)/,\n");
            Assert.True(ts.Success && gale.Success, "app.js no longer carries HERALD_MODCODE_RE");
            Assert.Equal(ModProfileCodes.ThunderstorePattern, ts.Groups[1].Value);
            Assert.Equal(ModProfileCodes.GalePattern, gale.Groups[1].Value);
        }

        [Fact]
        public void The_bridge_hands_the_posted_realms_code_to_the_post_and_refuses_by_name()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];
            var from = bridge.IndexOf("private DiscordStatusSnapshot BuildDiscordSnapshot(string profile)", StringComparison.Ordinal);
            var to = bridge.IndexOf("private object BuildSetupStatus()", from, StringComparison.Ordinal);
            Assert.True(from > 0 && to > from, "the snapshot builder moved");
            var snapshot = bridge.Substring(from, to - from);
            Assert.Contains("ModProfileCodeKind = prefs?.ModProfileCodeKind", snapshot);
            Assert.Contains("ModProfileCode = prefs?.ModProfileCode", snapshot);

            Assert.Contains("HostFacingException(\"userprefs.save.badModCode\"", bridge);
            Assert.Contains("HostFacingException(\"userprefs.save.modCodeNoRealm\"", bridge);
        }

        [Theory]
        [InlineData("thunderstore", "018f5c2a-7d1e-4b3a-9f00-1c2d3e4f5a6b", true)]
        [InlineData("thunderstore", "abc", false)]
        [InlineData("thunderstore", "-abcd", false)]
        [InlineData("thunderstore", "abc d", false)]
        [InlineData("gale", "AB12CD", true)]
        [InlineData("gale", "AB12C", false)]
        [InlineData("gale", "a1b2c3d4-e5f6", false)]
        // A Gale code is the same id the Mods hall follows, checked by GaleProfileId: the long
        // sync id is URL safe base64, so an underscore or a dash can stand anywhere in it.
        [InlineData("gale", "Ab_9-xY_z0Qr7_Lm-NoPqA", true)]
        [InlineData("gale", "_AAAAAAAAAAAAAAAAAAAAA", true)]
        [InlineData("gale", "-b12cd34_ef56gh78ij90g", true)]
        [InlineData("gale", "  Ab_9-xY_z0Qr7_Lm-NoPqA  ", true)]
        [InlineData("gale", "AAAAAAAAAAAAAAAAAAAAAB", false)]
        [InlineData("gale", "ab_12C", false)]
        [InlineData("thunderstore", "Ab_9-xY_z0Qr7_Lm-NoPqA", false)]
        [InlineData("none", "AB12CD", false)]
        [InlineData("none", "", true)]
        public void The_check_is_light(string kind, string code, bool acceptable)
        {
            Assert.Equal(acceptable, ModProfileCodes.IsAcceptable(kind, code));
        }

        // ------------------------------------------------------------------ the write half
        //
        // userprefs.save is an RPC inside a window that owns a WebView2, so the save is driven
        // through the three steps it is made of, which the handler calls in order: read and
        // check the code, write it to its realm, refresh the post. Each step runs here against
        // an in-memory realm store and a Discord service that counts its refresh requests.

        private sealed class RealmStore : IServerPreferencesProvider
        {
            private readonly Dictionary<string, ServerPreferencesFile> files = new(StringComparer.OrdinalIgnoreCase);

            public int Saves { get; private set; }

            public event EventHandler<List<ServerPreferences>> PreferencesSaved;

            public RealmStore(params ServerPreferences[] realms)
            {
                foreach (var realm in realms) files[realm.ProfileName] = realm.ToFile();
            }

            /// <summary>A fresh copy each read, as the file provider hands out.</summary>
            public ServerPreferences LoadPreferences(string profileName) =>
                profileName != null && files.TryGetValue(profileName, out var file) ? ServerPreferences.FromFile(file) : null;

            public IEnumerable<ServerPreferences> LoadPreferences() =>
                files.Values.Select(ServerPreferences.FromFile).ToList();

            public void SavePreferences(ServerPreferences preferences)
            {
                Saves++;
                files[preferences.ProfileName] = preferences.ToFile();
                PreferencesSaved?.Invoke(this, LoadPreferences().ToList());
            }

            public void RemovePreferences(string profileName) => files.Remove(profileName);
        }

        private sealed class CountingDiscord : IDiscordStatusService
        {
            /// <summary>Every refresh asked for: one per realm asked, and one for an ask of every realm.</summary>
            public int Requests => Asked.Count + AskedAll;
            public readonly List<string> Asked = new();
            public int AskedAll { get; private set; }
            public Func<string, DiscordStatusSnapshot> SnapshotProvider { get; set; }
            public void RequestUpdate(string realm) => Asked.Add(realm);
            public void RequestUpdateAll() => AskedAll++;
            public Task<DiscordStatusResult> PublishNowAsync(string realm) => throw new NotSupportedException();
            public Task<DiscordStatusResult> ValidateWebhookAsync(string url) => throw new NotSupportedException();
            public Task<DiscordStatusResult> RemoveStatusMessageAsync(string realm) => throw new NotSupportedException();
        }

        private static ServerPreferences Realm(string name, string kind = null, string code = null) => new()
        {
            ProfileName = name,
            Name = name,
            ModProfileCodeKind = kind ?? ModProfileCodes.None,
            ModProfileCode = code,
        };

        /// <summary>
        /// The save as the handler runs it, minus the app settings it also carries: whether an
        /// app-wide value moved and which realm's Herald block moved are worked out inside the
        /// handler's gated write, and are handed in here as what that write found.
        /// </summary>
        private static (bool Moved, bool Refreshed) Save(JObject dto, string active, RealmStore store, CountingDiscord discord,
            bool appWideMoved = false, string heraldRealmMoved = null)
        {
            var modCode = BlendWindow.ReadModProfileCode(dto, active, store);
            var moved = BlendWindow.WriteModProfileCode(modCode, store);
            var refreshed = BlendWindow.RefreshDiscordPostAfterSave(appWideMoved,
                new[] { heraldRealmMoved, moved ? modCode.Realm : null }, discord);
            return (moved, refreshed);
        }

        private static string Handler(string method)
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];
            var at = bridge.IndexOf("RegisterRpc(\"" + method + "\"", StringComparison.Ordinal);
            Assert.True(at > 0, "the bridge no longer registers " + method);
            var next = bridge.IndexOf("RegisterRpc(\"", at + 13, StringComparison.Ordinal);
            return next > at ? bridge.Substring(at, next - at) : bridge.Substring(at);
        }

        private const string TsCode = "018f5c2a-7d1e-4b3a-9f00-1c2d3e4f5a6b";

        [Fact]
        public void A_code_saved_for_one_realm_lands_in_that_realms_profile_and_nowhere_else()
        {
            var store = new RealmStore(Realm("Final Sunset", "gale", "AB12CD"), Realm("Ashlands Test"));
            var discord = new CountingDiscord();

            // Typed on Ashlands Test while Final Sunset is the active realm (a switch landed
            // between the keystroke and the write): the realm the page named wins.
            var dto = new JObject
            {
                ["ModProfileCodeKind"] = "thunderstore",
                ["ModProfileCode"] = "  " + TsCode + "  ",
                ["ModProfileCodeRealm"] = "Ashlands Test",
            };
            var (moved, refreshed) = Save(dto, "Final Sunset", store, discord);

            Assert.True(moved);
            Assert.True(refreshed);
            Assert.Equal(1, store.Saves);
            var b = store.LoadPreferences("Ashlands Test");
            Assert.Equal(ModProfileCodes.Thunderstore, b.ModProfileCodeKind);
            Assert.Equal(TsCode, b.ModProfileCode);
            var a = store.LoadPreferences("Final Sunset");
            Assert.Equal(ModProfileCodes.Gale, a.ModProfileCodeKind);
            Assert.Equal("AB12CD", a.ModProfileCode);

            // And the code has nowhere app-wide to go: the app's own settings carry no such field,
            // so a save cannot put one realm's code on every realm.
            foreach (var type in new[] { typeof(UserPreferences), typeof(UserPreferencesFile) })
                Assert.DoesNotContain(type.GetProperties(), prop => prop.Name.Contains("ModProfileCode", StringComparison.Ordinal));

            // No realm named: the active one.
            var active = new JObject { ["ModProfileCodeKind"] = "gale", ["ModProfileCode"] = "ZX90QW" };
            Assert.True(Save(active, "Final Sunset", store, discord).Moved);
            Assert.Equal("ZX90QW", store.LoadPreferences("Final Sunset").ModProfileCode);
            Assert.Equal(TsCode, store.LoadPreferences("Ashlands Test").ModProfileCode);
        }

        [Fact]
        public void The_post_is_refreshed_once_when_the_code_moves_and_not_when_nothing_did()
        {
            var store = new RealmStore(Realm("Final Sunset"));
            var discord = new CountingDiscord();
            JObject Dto(string kind, string code) => new()
            {
                ["ModProfileCodeKind"] = kind,
                ["ModProfileCode"] = code,
                ["ModProfileCodeRealm"] = "Final Sunset",
            };

            Save(Dto("thunderstore", TsCode), "Final Sunset", store, discord);
            Assert.Equal(1, discord.Requests);
            Assert.Equal(1, store.Saves);

            // The same code again: nothing written, nothing edited.
            var again = Save(Dto("thunderstore", TsCode), "Final Sunset", store, discord);
            Assert.False(again.Moved);
            Assert.False(again.Refreshed);
            Assert.Equal(1, discord.Requests);
            Assert.Equal(1, store.Saves);

            // The kind moving changes the field's name in the post.
            Save(Dto("gale", "Ab_9-xY_z0Qr7_Lm-NoPqA"), "Final Sunset", store, discord);
            Assert.Equal(2, discord.Requests);

            // Cleared: the field leaves the post, so the post is edited.
            Save(Dto("gale", ""), "Final Sunset", store, discord);
            Assert.Equal(3, discord.Requests);

            // A save that moved this realm's own Herald block AND its code still asks once, and
            // asks for this realm's post only.
            Save(Dto("thunderstore", TsCode), "Final Sunset", store, discord, heraldRealmMoved: "Final Sunset");
            Assert.Equal(4, discord.Requests);
            Assert.Equal(new[] { "Final Sunset", "Final Sunset", "Final Sunset", "Final Sunset" }, discord.Asked);

            // An app-wide value that moved (the master switch, the app-wide webhook, the look, the
            // join domain) shows on every realm's post, so every realm is asked, once, and the
            // realm's own ask is folded into it.
            var plain = Save(new JObject { ["DiscordBotName"] = "Ravenhold" }, "Final Sunset", store, discord,
                appWideMoved: true, heraldRealmMoved: "Final Sunset");
            Assert.False(plain.Moved);
            Assert.True(plain.Refreshed);
            Assert.Equal(1, discord.AskedAll);
            Assert.Equal(5, discord.Requests);

            // A save of something the post does not show asks nothing, and so does a save that
            // carried every Discord switch without moving any: the hall sends them all each time.
            Save(new JObject { ["Language"] = "en" }, "Final Sunset", store, discord);
            Assert.False(Save(new JObject { ["DiscordSharingEnabled"] = true }, "Final Sunset", store, discord).Refreshed);
            Assert.Equal(5, discord.Requests);
        }

        [Fact]
        public void A_refused_code_throws_before_anything_is_written()
        {
            var store = new RealmStore(Realm("Final Sunset", "gale", "AB12CD"));
            var discord = new CountingDiscord();

            foreach (var (kind, code) in new[] { ("thunderstore", "`@everyone`"), ("gale", "AB12"), ("none", "AB12CD"), ("thunderstore", "https://x.io/a") })
            {
                var dto = new JObject { ["ModProfileCodeKind"] = kind, ["ModProfileCode"] = code, ["ModProfileCodeRealm"] = "Final Sunset" };
                var thrown = Assert.Throws<HostFacingException>(() => Save(dto, "Final Sunset", store, discord));
                Assert.Equal("userprefs.save.badModCode", thrown.MessageId);
            }

            var gone = new JObject { ["ModProfileCodeKind"] = "gale", ["ModProfileCode"] = "ZX90QW", ["ModProfileCodeRealm"] = "Deleted Realm" };
            Assert.Equal("userprefs.save.modCodeNoRealm",
                Assert.Throws<HostFacingException>(() => Save(gone, "Deleted Realm", store, discord)).MessageId);

            Assert.Equal(0, store.Saves);
            Assert.Equal(0, discord.Requests);
            Assert.Equal("AB12CD", store.LoadPreferences("Final Sunset").ModProfileCode);

            // And the handler checks before the app settings are touched, so a refusal refuses the
            // whole save rather than landing half of it; then writes the code; then refreshes.
            var handler = Handler("userprefs.save");
            var read = handler.IndexOf("ReadModProfileCode(dto, ActiveProfileName, ServerPrefsProvider)", StringComparison.Ordinal);
            var mutate = handler.IndexOf("UserPrefsProvider.Mutate(", StringComparison.Ordinal);
            var write = handler.IndexOf("var modCodeMoved = WriteModProfileCode(modCode, ServerPrefsProvider);", StringComparison.Ordinal);
            var refresh = handler.IndexOf("RefreshDiscordPostAfterSave(heraldAppMoved,", StringComparison.Ordinal);
            Assert.True(read > 0 && mutate > read && write > mutate && refresh > write,
                "userprefs.save no longer reads the code first, then writes it, then refreshes the post");
        }

        [Fact]
        public void A_blank_code_is_stored_as_none_and_no_code()
        {
            var store = new RealmStore(Realm("Final Sunset", "thunderstore", TsCode));
            var discord = new CountingDiscord();

            foreach (var blank in new[] { "", "   ", null })
            {
                var dto = new JObject { ["ModProfileCodeKind"] = "thunderstore", ["ModProfileCode"] = blank, ["ModProfileCodeRealm"] = "Final Sunset" };
                Save(dto, "Final Sunset", store, discord);
                var realm = store.LoadPreferences("Final Sunset");
                Assert.Equal(ModProfileCodes.None, realm.ModProfileCodeKind);
                Assert.Null(realm.ModProfileCode);
            }
            Assert.Equal(1, store.Saves);
            Assert.Equal(1, discord.Requests);
        }

        [Fact]
        public void A_new_realm_does_not_inherit_the_active_realms_code()
        {
            var active = Realm("Final Sunset", "gale", "AB12CD");
            active.Port = 2460;
            var created = BlendWindow.SeedNewRealm(active);

            Assert.Equal(ModProfileCodes.None, created.ModProfileCodeKind);
            Assert.Null(created.ModProfileCode);
            Assert.Null(ModProfileCodes.Postable(created.ModProfileCodeKind, created.ModProfileCode));
            // The rest of the seed still carries over, and the source realm is untouched.
            Assert.Equal(2460, created.Port);
            Assert.Equal("AB12CD", active.ModProfileCode);

            // Both ways of making a realm go through the seed.
            foreach (var method in new[] { "servers.create", "servers.adoptWorld" })
            {
                var handler = Handler(method);
                Assert.Contains("SeedNewRealm(basePrefs)", handler);
                Assert.DoesNotContain("ServerPreferences.FromFile(basePrefs.ToFile())", handler);
            }
        }
    }
}
