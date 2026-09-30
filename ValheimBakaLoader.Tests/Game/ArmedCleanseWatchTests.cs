using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The armed one-shot cheat-mark cleanse: the switch a host leaves on so the sweep runs
    /// the next time nobody is playing.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS. The Players hall's button needs a host at the window at a moment the
    /// server is empty, because the sweep rewrites containers and a container somebody has open
    /// refuses to reload itself. On a server people use, that moment is four in the morning.
    /// The switch arms the same sweep and waits for it.
    /// <para>
    /// Three things about the waiting can be wrong without a build noticing. It can fire while
    /// somebody is on the server, which is the one thing it exists to avoid. It can fire after
    /// the host has put the switch down, which is a setting doing something after it was turned
    /// off. And it can fire twice, because the switch is a saved setting and the only thing that
    /// puts it down is the sweep landing.
    /// </para>
    /// <para>
    /// In the wall clock collection because the wait is a real one: the product waits sixty
    /// seconds and these hold it to a much shorter override, but it is still a clock.
    /// </para>
    /// </remarks>
    [Collection(WallClockCollection.Name)]
    public class ArmedCleanseWatchTests : BaseTest, IDisposable
    {
        private const string JoinLine = "Got connection SteamID {0}";
        private const string SpawnLine = "Got character ZDOID from {0} : {1}:1";
        private const string DisconnectLine = "Closing socket {0}";

        private readonly ValheimServer Server;
        private readonly IPlayerDataRepository Players;
        private readonly string SandboxDir;
        private readonly IDisposable OwnRecords;

        public ArmedCleanseWatchTests()
        {
            OwnRecords = CompanionPluginStatus.BeginOwnRecords();
            Server = GetService<ValheimServer>();
            Players = GetService<IPlayerDataRepository>();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-armed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());

            // 150ms rather than a minute. The wait's own length is held by the selftest against
            // the source; what these prove is what the wait does at the end of it.
            ValheimServer.EmptyCleanseDelayOverride = TimeSpan.FromMilliseconds(150);
        }

        public void Dispose()
        {
            ValheimServer.EmptyCleanseDelayOverride = null;
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        // ------------------------------------------------------------------ the watch itself

        /// <summary>
        /// A server that comes up empty with the switch on runs the sweep once, and the switch
        /// going down is what stops it running again.
        /// </summary>
        [Fact]
        public async Task The_watch_fires_once_and_the_switch_going_down_ends_it()
        {
            var armed = true;
            var runs = 0;

            Server.CleanseWhenEmptyWanted = () => armed;
            Server.RunEmptyCleanse = () =>
            {
                runs++;
                armed = false;   // what SetCleanseWhenEmpty does on the real road
                return Task.FromResult(true);
            };

            Running();

            Assert.True(await Until(() => runs > 0), "the armed sweep never went out");
            Assert.False(armed, "the switch is still on after a sweep that landed");

            // And nothing arms it again on its own. The switch is down, so every later look
            // finds nothing to do.
            Server.ArmEmptyCleanseWatch();
            await Task.Delay(400);
            Assert.Equal(1, runs);
        }

        /// <summary>
        /// A refusal leaves the switch on. Somebody walked onto the server, which is the very
        /// thing the host armed it for, so the next empty moment gets its turn.
        /// </summary>
        [Fact]
        public async Task A_refusal_leaves_the_switch_on_and_the_next_empty_moment_gets_its_turn()
        {
            var armed = true;
            var runs = 0;

            Server.CleanseWhenEmptyWanted = () => armed;
            Server.RunEmptyCleanse = () =>
            {
                runs++;
                return Task.FromResult(false);   // refused: nothing writes the switch
            };

            Running();

            Assert.True(await Until(() => runs > 0), "the armed sweep never went out");
            Assert.True(armed, "a refused sweep put the switch down, so the marks are never cleared");

            // The next empty moment: a player arrives and leaves again.
            Feed(JoinLine, "4242");
            Feed(SpawnLine, "Bjorn", "1");
            Feed(DisconnectLine, "4242");

            Assert.True(await Until(() => runs > 1), "the next empty moment did not get its turn");
        }

        /// <summary>
        /// The switch is a saved setting, so a fresh app over a profile that still carries it
        /// fires without anybody having to join and leave first. This is the case the
        /// last-player-left edge cannot reach: nothing has happened for the edge to fire on.
        /// </summary>
        [Fact]
        public async Task A_fresh_app_over_an_armed_profile_still_fires()
        {
            var runs = 0;

            // No player has ever connected in this session, which is exactly what a relaunched
            // app looks like.
            Server.CleanseWhenEmptyWanted = () => true;
            Server.RunEmptyCleanse = () => { runs++; return Task.FromResult(true); };

            Running();

            Assert.True(await Until(() => runs > 0),
                "an armed profile waits for somebody to join and leave before it means anything");
        }

        /// <summary>
        /// A player on the server holds it, and a switch put down during the wait holds it too.
        /// </summary>
        [Fact]
        public async Task Nothing_goes_out_while_somebody_is_on_the_server()
        {
            var runs = 0;
            Server.CleanseWhenEmptyWanted = () => true;
            Server.RunEmptyCleanse = () => { runs++; return Task.FromResult(true); };

            Running();
            // Straight in, before the 150ms wait is out.
            Feed(JoinLine, "9001");
            Feed(SpawnLine, "Freya", "2");

            await Task.Delay(400);
            Assert.Equal(0, runs);
        }

        [Fact]
        public async Task A_switch_put_down_during_the_wait_stops_the_sweep()
        {
            var armed = true;
            var runs = 0;
            Server.CleanseWhenEmptyWanted = () => armed;
            Server.RunEmptyCleanse = () => { runs++; return Task.FromResult(true); };

            Running();
            armed = false;   // the host changed their mind inside the wait

            await Task.Delay(400);
            Assert.Equal(0, runs);
        }

        // ------------------------------------------------------------------ the reading

        /// <summary>
        /// What a reply means for the switch. Only a sweep that was turned away keeps it up.
        /// </summary>
        [Theory]
        // it finished: down it goes
        [InlineData(true, false, false, "Cleanse complete: 4 world objects cleared, 1 container rewritten, 9 items cleared.", true)]
        [InlineData(true, false, false, "Cleanse complete: nothing in this world carries a cheat mark.", true)]
        // a sweep still walking at the end of the wait is work in progress, not a refusal
        [InlineData(true, true, false, "Cleanse running: 80000 of 240000 objects checked.", true)]
        // a second sweep already walking is also work that is happening
        [InlineData(true, false, false, "Cleanse is already running: 900 objects still to check.", true)]
        // a reply this build has no reading for: down, or the same command goes out every
        // minute of the night for ever
        [InlineData(true, false, false, "Cleansed 5 things", true)]
        [InlineData(true, false, false, "Unknown command: 'baka_cleanse'", true)]
        // somebody is on the server, which is the one thing the switch exists to wait out
        [InlineData(true, false, false, "Error: 1 player is still connected (Bjorn). Cheat marks can only be cleared on an empty server.", false)]
        [InlineData(true, false, false, "Cleanse stopped: 1 player connected while it was running (Bjorn). Up to that point: 0 world objects cleared, 0 containers rewritten, 0 items cleared.", false)]
        // it never got through, and the server went down under the wait
        [InlineData(false, false, false, "", false)]
        [InlineData(true, false, true, "Cleanse running: 1 of 2 objects checked.", false)]
        public void A_reply_decides_whether_the_switch_goes_down(
            bool ok, bool stillRunning, bool notAnswering, string response, bool lands)
        {
            var outcome = new BlendWindow.CleanseOutcome
            {
                Ok = ok,
                StillRunning = stillRunning,
                NotAnswering = notAnswering,
                Response = response,
            };

            Assert.Equal(lands, BlendWindow.ArmedCleanseLands(outcome));
        }

        [Fact]
        public void No_reply_at_all_keeps_the_switch_up()
        {
            Assert.False(BlendWindow.ArmedCleanseLands(null));
        }

        // ------------------------------------------------------------------ the herald

        /// <summary>
        /// The line the Herald posts. This fires while nobody is at the window, so the Discord
        /// post is often the only place a host ever reads the counts: it names the realm and
        /// carries the plugin's own reply line verbatim, which is the same words the Players hall
        /// shows, so one sweep has one wording.
        /// </summary>
        [Fact]
        public void The_herald_line_names_the_realm_and_carries_the_counts()
        {
            var title = ValheimBakaLoader.Tools.HostCatalog.EmbeddedEnglish.Say(
                "host.discord.cleanse.title");
            Assert.Equal("Cheat Marks Cleared", title);

            var body = ValheimBakaLoader.Tools.HostCatalog.EmbeddedEnglish.Say(
                "host.discord.cleanse.body",
                ("server", "Final Sunset"),
                ("counts", "Cleanse complete: 412 world objects cleared, 37 containers rewritten, "
                    + "1883 items cleared."));

            Assert.Contains("Final Sunset", body);
            Assert.Contains("412 world objects cleared", body);
            Assert.Contains("1883 items cleared", body);
            // No slot left unfilled, which is what an id printed at a community looks like.
            Assert.DoesNotContain("{", body);
            Assert.DoesNotContain("host.discord", body);

            // And it goes out on the herald's own switch, not on its own.
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            var at = bridge.IndexOf("session.Server.RunEmptyCleanse = async () =>", StringComparison.Ordinal);
            Assert.True(at > 0);
            var wiring = bridge.Substring(at, 2600);
            Assert.Matches("DiscordEventPosts[\\s\\S]{0,240}SendCheatMarksCleared", wiring);

            // The realm's own display name when it has one, and the profile name when it does not.
            Assert.Contains("ServerPrefsProvider.LoadPreferences(profile)?.Name", wiring);
        }

        // ------------------------------------------------------------------ the setting

        /// <summary>
        /// It survives closing the app, which is the whole point, so it has a home on disk and
        /// is carried both ways.
        /// </summary>
        [Fact]
        public void The_switch_is_carried_to_disk_and_back()
        {
            var prefs = new ServerPreferences { ProfileName = "Armed", CleanseWhenEmpty = true };

            var file = prefs.ToFile();
            Assert.True(file.CleanseWhenEmpty);
            Assert.True(ServerPreferences.FromFile(file).CleanseWhenEmpty);

            // And off by default: nothing turns it on but a host's own press.
            Assert.False(new ServerPreferences().CleanseWhenEmpty);
            Assert.False(ServerPreferences.FromFile(new ServerPreferencesFile()).CleanseWhenEmpty);

            // A profile written by an older build has no key for it, and reads as off rather
            // than as a sweep armed on a realm nobody armed.
            var older = Newtonsoft.Json.JsonConvert.DeserializeObject<ServerPreferencesFile>(
                "{\"profileName\":\"Armed\"}");
            Assert.False(ServerPreferences.FromFile(older).CleanseWhenEmpty);
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>Brings the server up, which is where the watch is armed.</summary>
        private void Running() => Feed("Game server connected");

        /// <summary>Waits for a condition, polling, rather than sleeping for a fixed guess.</summary>
        private static async Task<bool> Until(Func<bool> done, int withinMs = 3000)
        {
            for (var waited = 0; waited < withinMs; waited += 25)
            {
                if (done()) return true;
                await Task.Delay(25);
            }
            return done();
        }

        private void Feed(string template, params object[] args)
        {
            if (Server.Logger == null)
            {
                Server.Start(new ValheimServerOptions
                {
                    Name = "Armed Test",
                    WorldName = "Armed World",
                    Password = "hunter2",
                    Port = 2456,
                    ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
                    SaveDataFolderPath = Path.Combine(SandboxDir, "saves"),
                });
            }

            Server.Logger.Information(string.Format(template, args));
        }
    }
}
