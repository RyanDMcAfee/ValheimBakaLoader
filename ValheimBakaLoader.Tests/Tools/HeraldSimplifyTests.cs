using System;
using System.Collections.Generic;
using System.Linq;
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
    /// The Discord screen of 1.2.17: every realm stands on its own. The move that gets a file
    /// there (HeraldRealms.Simplify) runs once, after the 1.2.16 move, and must leave every post
    /// signed and placed the way it was, with nothing starting to post by surprise.
    /// <para>
    /// What can go wrong without a build noticing: a host who had the master off finding realms
    /// posting after the update; a realm that posted through the app-wide webhook going silent
    /// because blank now means none; the look changing on every post because the name and the
    /// picture moved; the move running twice and switching a realm off the host turned on since;
    /// and a refusal Discord gave for a forum with no thread living only in the Saga.
    /// </para>
    /// </summary>
    public class HeraldSimplifyTests
    {
        private const string HookA = "https://discord.com/api/webhooks/111/aaa";
        private const string HookB = "https://discord.com/api/webhooks/222/bbb";
        private const string Avatar = "https://i.imgur.com/raven.png";

        private static UserPreferences Before(bool master) => new()
        {
            HeraldPerRealmMigrated = true,
            DiscordSharingEnabled = master,
            DiscordWebhookUrl = HookA,
            DiscordWebhookThreadId = "777",
            DiscordBotName = "Raven",
            DiscordAvatarUrl = Avatar,
            Servers = new List<ServerPreferences>
            {
                new() { ProfileName = "Alpha", Name = "Alpha", HeraldOn = true },
                new() { ProfileName = "Beta", Name = "Beta", HeraldOn = true, HeraldWebhookUrl = HookB, HeraldBotName = "Own" },
                new() { ProfileName = "Off", Name = "Off" },
            },
        };

        private static ServerPreferences R(UserPreferences p, string name) => HeraldRealms.Find(p, name);

        [Fact]
        public void A_host_who_had_the_master_off_gets_every_realm_switched_off_once()
        {
            var prefs = Before(master: false);
            Assert.True(HeraldRealms.Simplify(prefs));

            Assert.All(prefs.Servers, s => Assert.False(s.HeraldOn));
            Assert.True(prefs.DiscordSharingEnabled);
            Assert.True(prefs.HeraldSimplifiedMigrated);
            Assert.True(HeraldRealms.MasterOn(prefs));
            Assert.False(HeraldRealms.Shares(prefs, R(prefs, "Alpha")));
        }

        [Fact]
        public void A_host_who_had_the_master_on_keeps_every_realm_as_it_was()
        {
            var prefs = Before(master: true);
            HeraldRealms.Simplify(prefs);

            Assert.True(R(prefs, "Alpha").HeraldOn);
            Assert.True(R(prefs, "Beta").HeraldOn);
            Assert.False(R(prefs, "Off").HeraldOn);
        }

        [Fact]
        public void The_app_wide_webhook_is_copied_into_the_realms_that_used_it_and_blank_is_none_after()
        {
            var prefs = Before(master: true);
            var alphaBefore = (HeraldRealms.WebhookUrlFor(prefs, R(prefs, "Alpha")), HeraldRealms.ThreadIdFor(prefs, R(prefs, "Alpha")));
            Assert.Equal((HookA, "777"), alphaBefore);

            HeraldRealms.Simplify(prefs);

            // Same channel and thread as before, now the realm's own.
            Assert.Equal(HookA, R(prefs, "Alpha").HeraldWebhookUrl);
            Assert.Equal("777", R(prefs, "Alpha").HeraldThreadId);
            Assert.Equal((HookA, "777"), (HeraldRealms.WebhookUrlFor(prefs, R(prefs, "Alpha")), HeraldRealms.ThreadIdFor(prefs, R(prefs, "Alpha"))));
            Assert.Equal(HookA, R(prefs, "Off").HeraldWebhookUrl);
            // A realm with a webhook of its own keeps it, and gets no thread from another channel.
            Assert.Equal(HookB, R(prefs, "Beta").HeraldWebhookUrl);
            Assert.Null(R(prefs, "Beta").HeraldThreadId);
            // The app-wide fields stay in the file, for the builds before this one.
            Assert.Equal(HookA, prefs.DiscordWebhookUrl);

            // From here on a blank webhook is no webhook: nothing inherits any more.
            var fresh = new ServerPreferences { ProfileName = "New", HeraldOn = true };
            prefs.Servers.Add(fresh);
            Assert.Null(HeraldRealms.WebhookUrlFor(prefs, fresh));
            Assert.Null(HeraldRealms.ThreadIdFor(prefs, fresh));
            Assert.False(HeraldRealms.Shares(prefs, fresh));
        }

        [Fact]
        public void The_name_and_picture_are_copied_into_every_realm_so_no_post_is_signed_differently()
        {
            var prefs = Before(master: true);
            var alphaSignature = DiscordHeraldLook.SignatureFor(prefs, R(prefs, "Alpha"));

            HeraldRealms.Simplify(prefs);

            Assert.Equal("Raven", R(prefs, "Alpha").HeraldBotName);
            Assert.Equal(Avatar, R(prefs, "Alpha").HeraldAvatarUrl);
            Assert.Equal("Own", R(prefs, "Beta").HeraldBotName);
            Assert.Equal(Avatar, R(prefs, "Beta").HeraldAvatarUrl);
            // The same signature, so the signature check does not delete and make the post again.
            Assert.Equal(alphaSignature, DiscordHeraldLook.SignatureFor(prefs, R(prefs, "Alpha")));

            // And the realm's own name is what signs from now on, not the app-wide one.
            prefs.DiscordBotName = "Ignored";
            R(prefs, "Alpha").HeraldBotName = "Alpha Herald";
            var payload = JObject.Parse(DiscordWebhookService.BuildEventPayload("t", "d", 1, prefs, null, R(prefs, "Alpha")));
            Assert.Equal("Alpha Herald", (string)payload["username"]);
            Assert.Equal(Avatar, (string)payload["avatar_url"]);
        }

        [Fact]
        public void The_move_runs_once_and_never_before_the_1_2_16_move()
        {
            var early = Before(master: false);
            early.HeraldPerRealmMigrated = false;
            Assert.False(HeraldRealms.Simplify(early));
            Assert.False(early.HeraldSimplifiedMigrated);
            Assert.True(R(early, "Alpha").HeraldOn);

            var prefs = Before(master: false);
            Assert.True(HeraldRealms.Simplify(prefs));
            // The host turns a realm on afterwards; a second run must not undo it.
            R(prefs, "Alpha").HeraldOn = true;
            prefs.DiscordSharingEnabled = false;
            Assert.False(HeraldRealms.Simplify(prefs));
            Assert.True(R(prefs, "Alpha").HeraldOn);
        }

        [Fact]
        public void An_old_file_without_the_keys_reads_as_before_the_move_and_a_moved_one_forces_the_master_on()
        {
            var old = UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(
                "{\"discordSharingEnabled\":false,\"discordWebhookUrl\":\"" + HookA + "\",\"servers\":[{\"profileName\":\"Alpha\"}]}"));
            Assert.False(old.HeraldSimplifiedMigrated);
            Assert.False(old.DiscordSharingEnabled);
            Assert.Null(old.Servers[0].HeraldBotName);
            Assert.Null(old.Servers[0].HeraldWebhookName);
            Assert.False(old.Servers[0].HeraldCrashNoThread);

            var moved = UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(
                "{\"discordSharingEnabled\":false,\"heraldSimplifiedMigrated\":true}"));
            Assert.True(moved.DiscordSharingEnabled);
            Assert.True(moved.HeraldSimplifiedMigrated);
        }

        [Fact]
        public void The_new_realm_keys_round_trip_and_survive_a_settings_form_save()
        {
            var realm = new ServerPreferences
            {
                ProfileName = "Alpha",
                HeraldWebhookName = "Longhouse",
                HeraldBotName = "Raven",
                HeraldAvatarUrl = Avatar,
                HeraldCrashNoThread = true,
            };
            var back = ServerPreferences.FromFile(JsonConvert.DeserializeObject<ServerPreferencesFile>(JsonConvert.SerializeObject(realm.ToFile())));
            Assert.Equal("Longhouse", back.HeraldWebhookName);
            Assert.Equal("Raven", back.HeraldBotName);
            Assert.Equal(Avatar, back.HeraldAvatarUrl);
            Assert.True(back.HeraldCrashNoThread);
            Assert.False(back.HeraldPlayersNoThread);
            Assert.DoesNotContain("heraldPlayersNoThread", JsonConvert.SerializeObject(realm.ToFile(), new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));

            var form = new ServerPreferences { ProfileName = "Alpha" }.KeepHeraldFrom(back);
            Assert.Equal("Longhouse", form.HeraldWebhookName);
            Assert.Equal("Raven", form.HeraldBotName);
            Assert.True(form.HeraldCrashNoThread);
        }

        [Fact]
        public void A_save_carries_the_realm_look_and_webhook_name_and_a_moved_route_clears_the_forum_warning()
        {
            var prefs = Before(master: true);
            HeraldRealms.Simplify(prefs);
            var alpha = R(prefs, "Alpha");
            alpha.HeraldCrashNoThread = true;

            var moved = BlendWindow.ApplyHeraldBlock(prefs, new JObject
            {
                ["BotName"] = "Alpha Herald",
                ["AvatarUrl"] = "",
                ["WebhookName"] = "Longhouse",
            }, "Alpha");
            Assert.True(moved, "a new name must refresh the post");
            Assert.Equal("Alpha Herald", alpha.HeraldBotName);
            Assert.Null(alpha.HeraldAvatarUrl);
            Assert.Equal("Longhouse", alpha.HeraldWebhookName);
            Assert.True(alpha.HeraldCrashNoThread, "nothing about where the posts go moved");

            BlendWindow.ApplyHeraldBlock(prefs, new JObject { ["CrashThreadId"] = "999" }, "Alpha");
            Assert.False(alpha.HeraldCrashNoThread);

            // A webhook that moved without a name keeps no name of the webhook before.
            BlendWindow.ApplyHeraldBlock(prefs, new JObject { ["WebhookUrl"] = HookB }, "Alpha");
            Assert.Null(alpha.HeraldWebhookName);

            var dto = JArray.FromObject(BlendWindow.BuildHeraldRealmsDto(prefs)).Cast<JObject>().First(b => (string)b["Realm"] == "Alpha");
            Assert.Equal("Alpha Herald", (string)dto["BotName"]);
            Assert.False((bool)dto["CrashNoThread"]);
            Assert.True(dto.ContainsKey("WebhookName"));
            Assert.True(dto.ContainsKey("AvatarUrl"));
            Assert.True(dto.ContainsKey("RoutineNoThread"));
            Assert.True(dto.ContainsKey("PlayersNoThread"));
        }

        [Fact]
        public void A_name_or_picture_in_the_realm_block_is_refused_like_the_app_wide_one()
        {
            var bad = Assert.Throws<HostFacingException>(() =>
                BlendWindow.CheckHeraldLook(new JObject { ["Herald"] = new JObject { ["AvatarUrl"] = "http://example.org/a.png" } }));
            Assert.Equal("userprefs.save.badAvatarUrl", bad.MessageId);
            var name = Assert.Throws<HostFacingException>(() =>
                BlendWindow.CheckHeraldLook(new JObject { ["Herald"] = new JObject { ["BotName"] = "Discord" } }));
            Assert.Equal("userprefs.save.badBotName", name.MessageId);
            BlendWindow.CheckHeraldLook(new JObject { ["Herald"] = new JObject { ["BotName"] = "Raven", ["AvatarUrl"] = Avatar } });
        }

        [Fact]
        public void A_forum_refusal_is_kept_on_the_realm_until_a_post_of_that_kind_goes_through()
        {
            var store = new Store(Before(master: true));
            var service = new DiscordWebhookService(store, new Mock<IHttpClientProvider>().Object, new Mock<IApplicationLogger>().Object);

            service.NoteNoThread("Alpha", HeraldEventClass.Crash, true);
            Assert.True(R(store.LoadPreferences(), "Alpha").HeraldCrashNoThread);
            Assert.False(R(store.LoadPreferences(), "Alpha").HeraldPlayersNoThread);
            var writes = store.Writes;

            // Said once: the same refusal again writes nothing.
            service.NoteNoThread("Alpha", HeraldEventClass.Crash, true);
            Assert.Equal(writes, store.Writes);

            service.NoteNoThread("Alpha", HeraldEventClass.Crash, false);
            Assert.False(R(store.LoadPreferences(), "Alpha").HeraldCrashNoThread);

            // A realm that is gone is left alone.
            service.NoteNoThread("Gone", HeraldEventClass.Players, true);
        }

        private sealed class Store : IUserPreferencesProvider
        {
            private string Stored;
            public int Writes;
            public event EventHandler<UserPreferences> PreferencesSaved;

            public Store(UserPreferences start) => Stored = JsonConvert.SerializeObject(start.ToFile());

            public UserPreferences LoadPreferences() =>
                UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(Stored));

            public void SavePreferences(UserPreferences preferences)
            {
                Writes++;
                Stored = JsonConvert.SerializeObject(preferences.ToFile());
                PreferencesSaved?.Invoke(this, preferences);
            }
        }
    }
}
