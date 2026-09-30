using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Which realm a press is booked to, and what the key over that realm's name is salted with.
    /// <para>
    /// Both of these are things the beat's payload can never show. The counts are right, the
    /// key is sixteen hex characters either way, and every existing test about the tally passes:
    /// what was wrong was WHICH of two one-way keys a press landed under, and whether the salt
    /// over the name was a secret at all.
    /// </para>
    /// </summary>
    public class CommandTallyProfileKeyTests
    {
        // ------------------------------------------------------------------ the salt

        /// <summary>
        /// The salt is this install's own, and it is not the device hash.
        /// <para>
        /// WHY THIS EXISTS. The key used to be a digest over
        /// <c>AssemblyHelper.GetClientCorrelationId()</c> and the profile name. That id travels
        /// in the SAME beat as the counts, as <c>deviceHash</c>, so anybody holding one beat
        /// held the salt: hash a wordlist of server names against it and the realm name falls
        /// out. The counts were anonymous and the key beside them was not.
        /// </para>
        /// </summary>
        [Fact]
        public void The_key_is_salted_with_something_the_beat_does_not_carry()
        {
            const string name = "Final Sunset";
            const string here = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string there = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

            // Stable within one install, different between two.
            Assert.Equal(CommandTally.ProfileKey(name, here), CommandTally.ProfileKey(name, here));
            Assert.NotEqual(CommandTally.ProfileKey(name, here), CommandTally.ProfileKey(name, there));

            var device = AssemblyHelper.GetClientCorrelationId();

            var prefs = new MockUserPreferencesProvider();
            var tally = new CommandTally(prefs);
            tally.Count("players.kick", name);

            var stored = prefs.LoadPreferences().AnalyticsSalt;
            Assert.True(AnalyticsSalt.Looks(stored), "the salt was never written down: " + stored);
            Assert.NotEqual(device, stored);

            // The key the tally really produced is the one over the STORED salt, and not the one
            // over the device hash. This is the assertion that fails on the pre-1.2.6 code.
            Assert.Contains(CommandTally.ProfileKey(name, stored), tally.Snapshot().Keys);
            Assert.DoesNotContain(CommandTally.ProfileKey(name, device), tally.Snapshot().Keys);
        }

        /// <summary>
        /// The salt is made once and then read. A key that changed between two beats would look
        /// like a realm used once and abandoned, on every restart.
        /// </summary>
        [Fact]
        public void The_salt_is_made_once_and_then_read()
        {
            var prefs = new MockUserPreferencesProvider();

            var first = AnalyticsSalt.Ensure(prefs);
            var second = AnalyticsSalt.Ensure(prefs);

            Assert.True(AnalyticsSalt.Looks(first));
            Assert.Equal(first, second);
            Assert.Equal(first, prefs.LoadPreferences().AnalyticsSalt);

            // A document hand-edited to something that is not a salt is replaced rather than
            // used, because a short or empty salt is the bug back again.
            ((ValheimBakaLoader.Game.IUserPreferencesProvider)prefs)
                .Mutate(p => p.AnalyticsSalt = "nope");
            var replaced = AnalyticsSalt.Ensure(prefs);
            Assert.True(AnalyticsSalt.Looks(replaced));
            Assert.NotEqual("nope", replaced);
        }

        /// <summary>Two fresh salts are not the same salt.</summary>
        [Fact]
        public void Two_installs_do_not_share_a_salt()
        {
            var made = Enumerable.Range(0, 32).Select(_ => AnalyticsSalt.New()).ToList();
            Assert.All(made, s => Assert.True(AnalyticsSalt.Looks(s), s));
            Assert.Equal(made.Count, made.Distinct(StringComparer.Ordinal).Count());
        }

        /// <summary>And the salt never leaves the machine, which is a rule about the beat's shape.</summary>
        [Fact]
        public void The_salt_is_not_in_the_beat()
        {
            var beat = AppSourceTree.Files()["HeartbeatService.cs"];
            Assert.DoesNotContain("AnalyticsSalt", beat, StringComparison.Ordinal);
            Assert.DoesNotContain("analyticsSalt", beat, StringComparison.Ordinal);
        }

        // ------------------------------------------------------- which realm a press books to

        /// <summary>The bridge's own reader, driven directly.</summary>
        private static string Named(JObject parameters)
            => (string)typeof(ValheimBakaLoader.Forms.BlendWindow)
                .GetMethod("CountedProfileName",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Invoke(null, new object[] { parameters });

        /// <summary>
        /// A command issued against a realm the window is NOT showing is booked to that realm.
        /// <para>
        /// WHY THIS EXISTS. The tally was keyed on the window's active realm, and several calls
        /// carry their own: a config write names the realm the buffer came from, a start and a
        /// save carry the whole profile document, an update cancel names the one it cancels.
        /// Every one of those can be issued while the window shows another realm, and the press
        /// landed under the wrong one of the two one-way keys with nothing to show it.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("config.write", "configWrite", "{'profile':'Second Sunset'}")]
        [InlineData("profiles.save", "realmSave", "{'prefs':{'ProfileName':'Second Sunset'}}")]
        [InlineData("server.start", "start", "{'prefs':{'profileName':'Second Sunset'}}")]
        [InlineData("server.updateCancel", "serverUpdateCancel", "{'profile':' Second Sunset '}")]
        public void A_call_that_names_a_realm_is_counted_against_that_realm(
            string method, string counted, string parameters)
        {
            const string salt = "cccccccccccccccccccccccccccccccc";

            var prefs = new MockUserPreferencesProvider();
            ((ValheimBakaLoader.Game.IUserPreferencesProvider)prefs)
                .Mutate(p => p.AnalyticsSalt = salt);

            var named = Named(JObject.Parse(parameters.Replace('\'', '"')));
            Assert.Equal("Second Sunset", named);

            // What the bridge does with it: the named realm, falling back to the active one.
            var tally = new CommandTally(prefs);
            tally.Count(method, named ?? "Final Sunset");

            var snapshot = tally.Snapshot();
            var wanted = CommandTally.ProfileKey("Second Sunset", salt);
            Assert.True(snapshot.ContainsKey(wanted),
                "the press was booked to the realm the window was showing rather than the one it named");
            Assert.False(snapshot.ContainsKey(CommandTally.ProfileKey("Final Sunset", salt)));
            Assert.Contains(counted, snapshot[wanted].Keys);
        }

        /// <summary>
        /// A call that names nothing counts against the window's own realm, which is what every
        /// button in the Players hall is. A <c>name</c> is NOT a realm: on servers.adoptWorld it
        /// is a world and on mods.installFromHexium it is a mod, and a key over a world name
        /// would be a second key for one realm.
        /// </summary>
        [Theory]
        [InlineData("{'hostId':'76561198000000000'}")]
        [InlineData("{'name':'Final Sunset'}")]
        [InlineData("{'world':'Final Sunset'}")]
        [InlineData("{'profile':'   '}")]
        [InlineData("{'prefs':{'ProfileName':''}}")]
        public void A_call_that_names_no_realm_falls_back_to_the_active_one(string parameters)
            => Assert.Null(Named(JObject.Parse(parameters.Replace('\'', '"'))));

        [Fact]
        public void No_parameters_at_all_is_not_a_throw()
            => Assert.Null(Named(null));
    }
}
