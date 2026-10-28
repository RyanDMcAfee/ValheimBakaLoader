using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The three Gale fields on a realm: they reach the file, come back from it, survive the
    /// settings form's save, and are not handed to a new realm.
    /// </summary>
    public class GaleFollowPreferencesTests
    {
        private const string Stamp = "2026-10-06T12:34:56.789123+00:00";

        private static ServerPreferences Following() => new()
        {
            ProfileName = "Gale Test",
            GaleProfileId = "AAAAAAAAAAAA-_AAAAAAAA",
            GaleLastUpdatedAt = Stamp,
            GaleUnticked = new List<string> { "Azumatt-AzuCraftyBoxes", "ValheimModding-Jotunn" },
        };

        [Fact]
        public void The_fields_go_to_the_file_under_their_own_keys_and_come_back()
        {
            var json = JsonConvert.SerializeObject(Following().ToFile());
            var keys = JObject.Parse(json);
            Assert.Equal("AAAAAAAAAAAA-_AAAAAAAA", (string)keys["galeProfileId"]);
            Assert.Equal(2, ((JArray)keys["galeUnticked"]).Count);

            var back = ServerPreferences.FromFile(JsonConvert.DeserializeObject<ServerPreferencesFile>(json));

            Assert.Equal("AAAAAAAAAAAA-_AAAAAAAA", back.GaleProfileId);
            // The service's text exactly, not a DateTime written out again.
            Assert.Equal(Stamp, back.GaleLastUpdatedAt);
            Assert.Equal(new[] { "Azumatt-AzuCraftyBoxes", "ValheimModding-Jotunn" }, back.GaleUnticked);
        }

        [Fact]
        public void The_Hexium_notes_round_trip_under_their_own_key_and_a_file_without_them_reads_empty()
        {
            // 1.3.1, issue 30. Made-up mod names throughout.
            var prefs = new ServerPreferences
            {
                ProfileName = "Gale Test",
                GaleProfileId = "AAAAAAAAAAAA-_AAAAAAAA",
                GaleNotAvailable = new List<string> { "Owner-Package@1.2.3", "Owner-Other@2.0.0" },
                GaleHexium = new List<GaleHexiumMark>
                {
                    new() { Mod = " Owner-Package ", Version = "1.2.3", Kind = "noPackage", Hexium = "exact" },
                    new() { Mod = "Owner-Other", Version = "2.0.0", Kind = "noVersion", Hexium = null },
                    // The same build written again: the later answer wins, one entry stays.
                    new() { Mod = "owner-package", Version = "1.2.3", Kind = "noPackage", Hexium = "other" },
                    // No mod name: dropped.
                    new() { Mod = "", Version = "1.0.0", Kind = "noPackage", Hexium = "exact" },
                    // Words this build does not know read as nothing, never as an offer.
                    new() { Mod = "Owner-Odd", Version = "3.0.0", Kind = "gone", Hexium = "maybe" },
                },
            };

            var json = JsonConvert.SerializeObject(prefs.ToFile());
            var notes = (JArray)JObject.Parse(json)["galeHexium"];
            Assert.Equal(3, notes.Count);
            Assert.Equal("owner-package", (string)notes[0]["mod"]);
            Assert.Equal("1.2.3", (string)notes[0]["version"]);
            Assert.Equal("noPackage", (string)notes[0]["kind"]);
            Assert.Equal("other", (string)notes[0]["hexium"]);

            var back = ServerPreferences.FromFile(JsonConvert.DeserializeObject<ServerPreferencesFile>(json));
            Assert.Equal(new (string, string, string, string)[]
            {
                ("owner-package", "1.2.3", "noPackage", "other"),
                ("Owner-Other", "2.0.0", "noVersion", null),
                ("Owner-Odd", "3.0.0", null, null),
            }, back.GaleHexium.Select(n => (n.Mod, n.Version, n.Kind, n.Hexium)).ToArray());

            // A file written before 1.3.1 has marks and no notes: the marks stand, the notes are empty.
            var older = ServerPreferences.FromFile(JsonConvert.DeserializeObject<ServerPreferencesFile>(
                "{\"profileName\":\"Old\",\"galeProfileId\":\"ab12CD\",\"galeNotAvailable\":[\"Owner-Package@1.2.3\"]}"));
            Assert.Single(older.GaleNotAvailable);
            Assert.NotNull(older.GaleHexium);
            Assert.Empty(older.GaleHexium);
            Assert.Empty(new ServerPreferences().GaleHexium);
            Assert.Null(new ServerPreferences { ProfileName = "Plain" }.ToFile().GaleHexium);

            // The settings form's save keeps what is on disk, and a first save keeps what it has.
            var fromPage = new ServerPreferences { ProfileName = "Gale Test", GaleHexium = null }.KeepGaleFollowFrom(back);
            Assert.Equal(3, fromPage.GaleHexium.Count);
            Assert.Empty(new ServerPreferences { GaleHexium = null }.KeepGaleFollowWithoutStore().GaleHexium);
        }

        [Fact]
        public void A_file_from_before_the_fields_existed_reads_as_not_following()
        {
            var older = JsonConvert.DeserializeObject<ServerPreferencesFile>("{\"profileName\":\"Old\",\"port\":2456}");
            var prefs = ServerPreferences.FromFile(older);

            Assert.Null(prefs.GaleProfileId);
            Assert.Null(prefs.GaleLastUpdatedAt);
            Assert.NotNull(prefs.GaleUnticked);
            Assert.Empty(prefs.GaleUnticked);

            Assert.Null(new ServerPreferences().GaleProfileId);
            Assert.Empty(new ServerPreferences().GaleUnticked);
            Assert.Empty(ServerPreferences.FromFile(null).GaleUnticked);
        }

        [Fact]
        public void A_realm_that_never_followed_writes_nulls_and_an_empty_list_writes_null()
        {
            var file = new ServerPreferences { ProfileName = "Plain" }.ToFile();
            Assert.Null(file.GaleProfileId);
            Assert.Null(file.GaleLastUpdatedAt);
            Assert.Null(file.GaleUnticked);

            var blanks = new ServerPreferences { GaleProfileId = "  ", GaleLastUpdatedAt = " ", GaleUnticked = new List<string> { " ", "" } }.ToFile();
            Assert.Null(blanks.GaleProfileId);
            Assert.Null(blanks.GaleLastUpdatedAt);
            Assert.Null(blanks.GaleUnticked);
        }

        [Theory]
        [InlineData("not-a-gale-id")]
        [InlineData("ABCDE")]
        [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")]
        public void A_stored_id_that_fails_the_shape_check_reads_as_off(string stored)
        {
            var prefs = ServerPreferences.FromFile(new ServerPreferencesFile { GaleProfileId = stored });
            Assert.Null(prefs.GaleProfileId);
            Assert.Null(new ServerPreferences { GaleProfileId = stored }.ToFile().GaleProfileId);
        }

        [Fact]
        public void A_stored_id_is_trimmed()
        {
            var prefs = ServerPreferences.FromFile(new ServerPreferencesFile { GaleProfileId = "  ab12CD \n" });
            Assert.Equal("ab12CD", prefs.GaleProfileId);
        }

        [Fact]
        public void The_unticked_list_is_kept_tidy_one_name_once_whatever_its_case()
        {
            var prefs = ServerPreferences.FromFile(new ServerPreferencesFile
            {
                GaleUnticked = new List<string> { " A-Mod ", "a-mod", null, "", "B-Mod", "A-MOD" },
            });
            Assert.Equal(new[] { "A-Mod", "B-Mod" }, prefs.GaleUnticked);
        }

        // ------------------------------------------------------------------ the settings form's save

        [Fact]
        public void The_settings_forms_round_trip_mangles_the_stamp_which_is_why_disk_wins()
        {
            // What the page does: the profile goes out as JSON, the form edits its own boxes,
            // and the whole object comes back and is read as a JObject before ToObject.
            var sent = JObject.FromObject(Following()).ToString(Formatting.None);
            var payload = JObject.Parse(sent);
            var mangled = payload.ToObject<ServerPreferences>();

            // Proof of the hazard this guards: a default JObject parse turned the stamp into a date.
            Assert.NotEqual(Stamp, mangled.GaleLastUpdatedAt);

            // And the guard: what is on disk is put back.
            var stored = Following();
            mangled.KeepGaleFollowFrom(stored);
            Assert.Equal(Stamp, mangled.GaleLastUpdatedAt);
            Assert.Equal(stored.GaleProfileId, mangled.GaleProfileId);
            Assert.Equal(stored.GaleUnticked, mangled.GaleUnticked);
        }

        [Fact]
        public void A_save_built_from_an_older_read_does_not_undo_an_approval()
        {
            // The page read the profile before the approval, the approval wrote a new stamp and
            // a new untick, and then the host pressed Save on the form.
            var staleFromPage = Following();
            staleFromPage.GaleLastUpdatedAt = "older";
            staleFromPage.GaleUnticked = new List<string>();
            staleFromPage.Port = 2470;   // the edit the host actually made

            var onDisk = Following();
            onDisk.GaleUnticked.Add("New-Untick");

            var saved = staleFromPage.KeepGaleFollowFrom(onDisk);

            Assert.Equal(Stamp, saved.GaleLastUpdatedAt);
            Assert.Contains("New-Untick", saved.GaleUnticked);
            Assert.Equal(2470, saved.Port);
            // A copy, not the stored list itself.
            Assert.NotSame(onDisk.GaleUnticked, saved.GaleUnticked);
        }

        [Fact]
        public void With_nothing_on_disk_the_payload_stands()
        {
            var prefs = Following();
            Assert.Same(prefs, prefs.KeepGaleFollowFrom(null));
            Assert.Equal(Stamp, prefs.GaleLastUpdatedAt);
        }

        [Fact]
        public void With_nothing_on_disk_the_mangled_stamp_is_dropped_and_the_rest_kept()
        {
            // The same round trip as above, with no stored profile to put back.
            var payload = JObject.Parse(JObject.FromObject(Following()).ToString(Formatting.None));
            var mangled = payload.ToObject<ServerPreferences>();
            Assert.NotEqual(Stamp, mangled.GaleLastUpdatedAt);

            var kept = mangled.KeepGaleFollowWithoutStore();

            Assert.Same(mangled, kept);
            Assert.Null(kept.GaleLastUpdatedAt);
            Assert.Equal(Following().GaleProfileId, kept.GaleProfileId);
            Assert.Equal(Following().GaleUnticked, kept.GaleUnticked);
        }

        [Fact]
        public void The_save_handler_with_nothing_stored_drops_the_stamp()
        {
            var bridge = File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(), "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"));
            var start = bridge.IndexOf("private ServerPreferences KeepGaleFollow(", System.StringComparison.Ordinal);
            Assert.True(start > 0);
            var body = bridge.Substring(start, bridge.IndexOf("/// <summary>", start, System.StringComparison.Ordinal) - start);
            Assert.Contains("prefs.KeepGaleFollowWithoutStore()", body);
        }

        [Fact]
        public void The_save_handler_puts_the_stored_follow_back_before_it_writes()
        {
            var bridge = File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(), "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"));
            var start = bridge.IndexOf("RegisterRpc(\"profiles.save\"", System.StringComparison.Ordinal);
            Assert.True(start > 0);
            var handler = bridge.Substring(start, bridge.IndexOf("RegisterRpc(\"profiles.remove\"", start, System.StringComparison.Ordinal) - start);

            var keep = handler.IndexOf("prefs = KeepGaleFollow(prefs);", System.StringComparison.Ordinal);
            var save = handler.IndexOf("ServerPrefsProvider.SavePreferences(prefs);", System.StringComparison.Ordinal);
            Assert.True(keep > 0 && save > keep, "the stored follow has to be put back before the save");
            Assert.Single(Regex.Matches(bridge, Regex.Escape("prefs = KeepGaleFollow(prefs);")));
        }

        // ------------------------------------------------------------------ a new realm

        [Fact]
        public void A_new_realm_does_not_follow_the_active_realms_profile()
        {
            var active = Following();
            active.Port = 2460;

            var created = BlendWindow.SeedNewRealm(active);

            Assert.Null(created.GaleProfileId);
            Assert.Null(created.GaleLastUpdatedAt);
            Assert.Empty(created.GaleUnticked);
            Assert.Equal(2460, created.Port);
            // The source realm is untouched.
            Assert.Equal("AAAAAAAAAAAA-_AAAAAAAA", active.GaleProfileId);
            Assert.Equal(2, active.GaleUnticked.Count);
        }
    }
}
