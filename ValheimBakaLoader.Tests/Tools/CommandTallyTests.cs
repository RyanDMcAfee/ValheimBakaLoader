using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The command counts the anonymous beat carries, and the two things about them that would
    /// be a privacy failure rather than a bug.
    /// </summary>
    /// <remarks>
    /// WHAT THIS IS FOR. The beat has always said three things: a one-way id for the install,
    /// the version, and whether a server is up. 1.2.6 adds a fourth: how many times each command
    /// was issued since the last beat, per server profile. It is the difference between knowing
    /// how many installs there are and knowing which parts of the window anybody uses.
    /// <para>
    /// COUNTS, AND NOTHING ELSE. The two ways this could go wrong are both about what travels
    /// beside the number. A profile is a one-way key and never a name; a console line is counted
    /// under its VERB and never its arguments, which is where a player name or a world name
    /// would ride out. Both are held here by driving the real reader over lines that carry one.
    /// </para>
    /// <para>
    /// And the size of it. One of the counted commands is the free-typed console line, so its
    /// key is whatever a host typed: without a ceiling an evening of mistyping grows the payload
    /// a key at a time.
    /// </para>
    /// </remarks>
    public class CommandTallyTests : BaseTest
    {
        // ------------------------------------------------------------------ what is counted

        /// <summary>
        /// A representative press from each hall, and each one lands under its own name.
        /// </summary>
        [Theory]
        [InlineData("players.kick", "kick")]
        [InlineData("players.cleanse", "cleanse")]
        [InlineData("mods.remove", "modRemove")]
        [InlineData("server.restart", "restart")]
        [InlineData("bepinex.install", "bepinexInstall")]
        [InlineData("net.diagnose", "connectionTest")]
        [InlineData("worlds.copyAs", "copyWorld")]
        [InlineData("servers.create", "duplicate")]
        [InlineData("worldgen.save", "worldSettingsSave")]
        [InlineData("backups.restore", "restore")]
        public void A_command_is_counted_under_its_own_name(string method, string name)
        {
            var tally = new CommandTally();
            tally.Count(method, "Default");

            var counts = Only(tally);
            Assert.Equal(1, counts[name]);
            Assert.Single(counts);
        }

        /// <summary>
        /// Most of what crosses the bridge is the page READING something, several times a
        /// second. Counting those would say how often a timer ran and nothing about a host.
        /// </summary>
        [Theory]
        [InlineData("players.list")]
        [InlineData("server.state")]
        [InlineData("metrics.get")]
        [InlineData("profiles.get")]
        [InlineData("logs.appBuffer")]
        [InlineData("mods.count")]
        [InlineData("userprefs.get")]
        [InlineData("not.a.method.at.all")]
        [InlineData("")]
        [InlineData(null)]
        public void A_read_is_not_a_command(string method)
        {
            var tally = new CommandTally();
            tally.Count(method, "Default");
            Assert.Empty(tally.Snapshot());
        }

        [Fact]
        public void The_same_press_twice_is_two()
        {
            var tally = new CommandTally();
            tally.Count("players.kick", "Default");
            tally.Count("players.kick", "Default");
            tally.Count("players.cleanse", "Default");

            var counts = Only(tally);
            Assert.Equal(2, counts["kick"]);
            Assert.Equal(1, counts["cleanse"]);
        }

        // ------------------------------------------------------------------ the console verb

        /// <summary>
        /// A console line is counted under its verb, and the arguments never leave the machine.
        /// This is the one command whose key a host types, so it is also the one that could
        /// carry a player name or a world name out with it.
        /// </summary>
        [Theory]
        [InlineData("baka_killall", "rcon.baka_killall")]
        [InlineData("kick Bjorn", "rcon.kick")]
        [InlineData("  BAKA_CLEANSE  ", "rcon.baka_cleanse")]
        [InlineData("baka_spawn SwordIron 1 Bjorn", "rcon.baka_spawn")]
        [InlineData("save", "rcon.save")]
        [InlineData("tp Bjorn 100 20 -300", "rcon.tp")]
        public void A_console_line_is_counted_under_its_verb_alone(string line, string key)
        {
            var tally = new CommandTally();
            tally.Count("server.command", "Default", line);

            var counts = Only(tally);
            Assert.Equal(1, counts[key]);

            // And nothing off the line rides along.
            foreach (var word in line.Trim().Split(' ').Skip(1).Where(w => w.Length > 0))
                Assert.DoesNotContain(counts.Keys, k => k.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void A_console_verb_cannot_grow_the_payload_by_being_long()
        {
            var tally = new CommandTally();
            tally.Count("server.command", "Default", new string('x', 400));

            var key = Only(tally).Keys.Single();
            Assert.StartsWith("rcon.", key);
            Assert.Equal("rcon.".Length + CommandTally.MaxVerbLength, key.Length);
        }

        [Fact]
        public void A_console_line_with_no_verb_in_it_is_not_counted()
        {
            var tally = new CommandTally();
            tally.Count("server.command", "Default", "   ");
            tally.Count("server.command", "Default", null);
            tally.Count("server.command", "Default", "!!! ???");
            Assert.Empty(tally.Snapshot());
        }

        /// <summary>
        /// A console line written in a language this build has no command in never travels, in
        /// whole or in part.
        /// </summary>
        /// <remarks>
        /// WHY THIS IS THE ONE TO GET RIGHT. The console line is the only counted command whose
        /// key a host TYPES, so it is the only one that can carry a word off the machine. The
        /// first filter kept anything <c>char.IsLetterOrDigit</c> said yes to, and that method
        /// answers for the whole of Unicode: every Cyrillic, Greek, CJK and accented Latin
        /// letter was kept and lower-cased. A Valheim player name is very often exactly that,
        /// and a mistyped line whose first word is a name would have gone out as the key.
        /// <para>
        /// Half a name is still a name, so a letter or a digit this build cannot spell in plain
        /// ASCII drops the WHOLE line rather than being transliterated away. Punctuation is
        /// still just dropped, so a host who types <c>/kick</c> is still counted under kick.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("Bj\u00f6rn")]                 // an accented Latin name
        [InlineData("\u041a\u0430\u0442\u044f")]   // Cyrillic
        [InlineData("\u3055\u304f\u3089")]         // Japanese
        [InlineData("\uff4b\uff49\uff43\uff4b")]   // full-width latin, which IsLetterOrDigit also keeps
        [InlineData("Bjo\u0308rn")]                 // a name written with a combining accent
        public void A_console_verb_that_is_not_plain_ascii_is_not_counted_at_all(string line)
        {
            Assert.Null(CommandTally.Verb(line));

            var tally = new CommandTally();
            tally.Count("server.command", "Default", line);
            Assert.Empty(tally.Snapshot());
        }

        /// <summary>
        /// And the shape of every console key the beat can carry, asserted as the shape the
        /// README promises rather than as a length. This is the assertion that fails on the
        /// pre-1.2.6 filter: a Unicode-wide letter test passes a length check happily.
        /// </summary>
        [Fact]
        public void Every_console_key_that_does_travel_is_plain_ascii()
        {
            var lines = new[]
            {
                "baka_killall", "kick Bjorn", "  BAKA_CLEANSE  ", "save", "tp Bjorn 100 20 -300",
                "/kick Bjorn", "help!", "Bj\u00f6rn", "\u041a\u0430\u0442\u044f", "list;drop",
                new string('x', 400), "event forest\u30c8\u30ed\u30eb",
            };

            var tally = new CommandTally();
            foreach (var line in lines) tally.Count("server.command", "Default", line);

            foreach (var key in Only(tally).Keys)
                Assert.Matches("^rcon[.][a-z0-9_.]+$", key);
        }

        // ------------------------------------------------------------------ the profile key

        /// <summary>
        /// Two servers on one machine are two keys, and neither key is a name. The name cannot
        /// be read back out of the key and does not appear anywhere in the snapshot.
        /// </summary>
        [Fact]
        public void A_profile_travels_as_a_one_way_key_and_never_as_a_name()
        {
            var tally = new CommandTally();
            tally.Count("players.kick", "Final Sunset");
            tally.Count("players.cleanse", "Baka Gaijin");

            var snapshot = tally.Snapshot();
            Assert.Equal(2, snapshot.Count);

            foreach (var key in snapshot.Keys)
            {
                Assert.Equal(16, key.Length);
                Assert.Matches("^[0-9a-f]{16}$", key);
            }

            var whole = string.Join(" ", snapshot.Keys) + " " +
                string.Join(" ", snapshot.Values.SelectMany(v => v.Keys));
            Assert.DoesNotContain("Final Sunset", whole);
            Assert.DoesNotContain("Baka Gaijin", whole);
            Assert.DoesNotContain("Sunset", whole, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_same_profile_is_the_same_key_and_a_different_one_is_not()
        {
            // The salt is handed in now rather than read off the device hash; see
            // BepInEx-free CommandTallyProfileKeyTests for why that mattered.
            const string salt = "0123456789abcdef0123456789abcdef";

            Assert.Equal(CommandTally.ProfileKey("Default", salt),
                         CommandTally.ProfileKey("Default", salt));
            Assert.NotEqual(CommandTally.ProfileKey("Default", salt),
                            CommandTally.ProfileKey("default", salt));
            Assert.NotEqual(CommandTally.ProfileKey("A", salt), CommandTally.ProfileKey("B", salt));

            // A null profile is still a key rather than a throw: a command issued before a
            // profile has been chosen is still a command.
            Assert.Matches("^[0-9a-f]{16}$", CommandTally.ProfileKey(null, salt));
        }

        // ------------------------------------------------------------------ the ceiling

        /// <summary>
        /// Sixty-four distinct keys per profile, and past that the counting stops for that
        /// profile rather than the tally: the keys already there keep counting.
        /// </summary>
        [Fact]
        public void One_profile_carries_at_most_sixty_four_keys()
        {
            var tally = new CommandTally();
            for (var i = 0; i < 200; i++) tally.Count("server.command", "Default", "verb" + i);

            var counts = Only(tally);
            Assert.Equal(CommandTally.MaxKeysPerProfile, counts.Count);

            // A key that is already there is not turned away by the ceiling.
            tally.Count("server.command", "Default", "verb0");
            Assert.Equal(2, Only(tally)["rcon.verb0"]);

            // And a second profile gets its own sixty-four, because the cap is per profile.
            tally.Count("players.kick", "Other");
            Assert.Equal(2, tally.Snapshot().Count);
        }

        // ------------------------------------------------------------------ the reset

        /// <summary>
        /// Only what the backend acknowledged is taken off, and a press that landed while the
        /// beat was in flight rides the next one instead of being thrown away with the reply.
        /// </summary>
        [Fact]
        public void Forgetting_a_snapshot_subtracts_it_rather_than_clearing()
        {
            var tally = new CommandTally();
            tally.Count("players.kick", "Default");
            tally.Count("players.kick", "Default");

            var sent = tally.Snapshot();

            // A third press, after the snapshot was taken and before the reply arrived.
            tally.Count("players.kick", "Default");

            tally.Forget(sent);

            Assert.Equal(1, Only(tally)["kick"]);
        }

        [Fact]
        public void Forgetting_everything_empties_the_tally()
        {
            var tally = new CommandTally();
            tally.Count("players.kick", "Default");
            tally.Count("server.restart", "Other");

            tally.Forget(tally.Snapshot());
            Assert.Empty(tally.Snapshot());

            // And forgetting nothing is not a crash.
            tally.Forget(null);
            Assert.Empty(tally.Snapshot());
        }

        [Fact]
        public void A_snapshot_is_a_copy_and_not_the_tally_itself()
        {
            var tally = new CommandTally();
            tally.Count("players.kick", "Default");

            var first = tally.Snapshot();
            tally.Count("players.kick", "Default");

            Assert.Equal(1, first.Values.Single()["kick"]);
            Assert.Equal(2, Only(tally)["kick"]);
        }

        // ------------------------------------------------------------------ the beat itself

        /// <summary>
        /// The payload's shape: the three fields that were always there, and a commands map of
        /// profile key to counts. Nothing else.
        /// </summary>
        [Fact]
        public async Task The_beat_carries_the_counts_and_the_three_fields_it_always_did()
        {
            var tally = new CommandTally();
            tally.Count("players.kick", "Default");
            tally.Count("players.kick", "Default");
            tally.Count("players.cleanse", "Default");

            var (service, sent) = Beat(tally, sharing: true);
            await service.Beat();

            var body = JObject.Parse(Assert.Single(sent));
            Assert.NotNull(body["deviceHash"]);
            Assert.NotNull(body["appVersion"]);
            Assert.NotNull(body["serverRunning"]);

            var commands = Assert.IsType<JObject>(body["commands"]);
            var forProfile = Assert.IsType<JObject>(commands.Properties().Single().Value);
            Assert.Equal(2, forProfile["kick"].Value<int>());
            Assert.Equal(1, forProfile["cleanse"].Value<int>());

            // The profile is a key and the payload holds no name of any kind.
            Assert.Matches("^[0-9a-f]{16}$", commands.Properties().Single().Name);
            Assert.DoesNotContain("Default", body.ToString());
        }

        /// <summary>An install where nothing was pressed sends exactly the beat it always sent.</summary>
        [Fact]
        public async Task An_idle_install_sends_no_commands_field_at_all()
        {
            var (service, sent) = Beat(new CommandTally(), sharing: true);
            await service.Beat();

            var body = JObject.Parse(Assert.Single(sent));
            Assert.Null(body["commands"]);
            Assert.Equal(3, body.Properties().Count());
        }

        /// <summary>
        /// The opt-out. The switch says nothing is sent at all, and this holds that sentence
        /// true: with it off there is no post, so there is nothing to carry counts on.
        /// </summary>
        [Fact]
        public async Task The_opt_out_sends_nothing()
        {
            var tally = new CommandTally();
            tally.Count("players.kick", "Default");

            var (service, sent) = Beat(tally, sharing: false);
            await service.Beat();

            Assert.Empty(sent);

            // And the counts are still there, unsent, rather than quietly dropped.
            Assert.NotEmpty(tally.Snapshot());
        }

        /// <summary>
        /// A beat the backend took clears the counts. A beat it refused leaves them, so a
        /// backend that is down for an hour costs one combined payload and no data.
        /// </summary>
        [Fact]
        public async Task Only_an_acknowledged_beat_clears_the_counts()
        {
            var tally = new CommandTally();
            tally.Count("players.kick", "Default");

            var (bad, _) = Beat(tally, sharing: true, answer: HttpStatusCode.InternalServerError);
            await bad.Beat();
            Assert.Equal(1, Only(tally)["kick"]);

            var (good, _) = Beat(tally, sharing: true);
            await good.Beat();
            Assert.Empty(tally.Snapshot());
        }

        /// <summary>
        /// The counting itself is gated on the same preference, not only the sending. With the
        /// switch off nothing is held in memory either, because "nothing is sent at all" would
        /// otherwise be untrue the moment it was turned back on.
        /// </summary>
        [Fact]
        public void The_bridge_gates_the_counting_on_the_same_preference()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            var at = bridge.IndexOf("partial void CountCommand(", StringComparison.Ordinal);
            Assert.True(at > 0, "the bridge no longer counts commands");

            var body = bridge.Substring(at, 900);
            Assert.Contains("ShareAnonymousStats", body);
            Assert.Contains("ActiveProfileName", body);

            // The console line's verb is the only thing read off the parameters, and only for
            // that one method.
            Assert.Contains("CommandTally.ConsoleMethod", body);
            Assert.Contains("parameters?.Value<string>(\"command\")", body);
        }

        // ------------------------------------------------------------------ plumbing

        private static IReadOnlyDictionary<string, int> Only(ICommandTally tally)
        {
            var snapshot = tally.Snapshot();
            Assert.NotEmpty(snapshot);
            return snapshot.Values.First();
        }

        private (HeartbeatService Service, List<string> Sent) Beat(
            ICommandTally tally, bool sharing, HttpStatusCode answer = HttpStatusCode.OK)
        {
            var sent = new List<string>();
            var prefs = UserPreferences.GetDefault();
            prefs.ShareAnonymousStats = sharing;

            var service = new HeartbeatService(
                new FixedPrefs(prefs),
                new RecordingClients(sent, answer),
                GetService<ValheimBakaLoader.Tools.Logging.IApplicationLogger>(),
                tally);

            service.ServerRunningProvider = () => false;
            return (service, sent);
        }

        private sealed class FixedPrefs : IUserPreferencesProvider
        {
            private readonly UserPreferences Held;
            public FixedPrefs(UserPreferences held) => Held = held;
            public event EventHandler<UserPreferences> PreferencesSaved;
            public UserPreferences LoadPreferences() => Held;
            public void SavePreferences(UserPreferences preferences) =>
                PreferencesSaved?.Invoke(this, preferences);
        }

        private sealed class RecordingClients : IHttpClientProvider
        {
            private readonly List<string> Sent;
            private readonly HttpStatusCode Answer;
            public RecordingClients(List<string> sent, HttpStatusCode answer)
            {
                Sent = sent;
                Answer = answer;
            }
            public HttpClient CreateClient() => new(new Recorder(Sent, Answer));
        }

        private sealed class Recorder : HttpMessageHandler
        {
            private readonly List<string> Sent;
            private readonly HttpStatusCode Answer;
            public Recorder(List<string> sent, HttpStatusCode answer)
            {
                Sent = sent;
                Answer = answer;
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Sent.Add(await request.Content.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(Answer) { Content = new StringContent("") };
            }
        }

    }

}
