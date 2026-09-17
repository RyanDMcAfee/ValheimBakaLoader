using System;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// Drives the log-line rule table with the exact lines a Valheim 1.0
    /// dedicated server prints, alongside the pre-1.0 lines that must keep
    /// working. Every line here is copied from a real server log or from the
    /// game's own format string, so none of them may be reworded.
    /// </summary>
    public class ValheimServerLogRulesTests : BaseTest, IDisposable
    {
        private readonly ValheimServer Server;
        private readonly IPlayerDataRepository Players;

        // Throwaway on-disk sandbox (dummy exe + save folder) so Start()'s path
        // validation passes anywhere. The process is never launched: BaseTest
        // swaps in MockProcessProvider.
        private readonly string SandboxDir;

        // A record book of its own. Starting a server runs the companion plugin install pass,
        // and that pass clears the record for the realm it is starting, which walks over what a
        // class reading the shared record had written. See
        // CompanionPluginStatusTests.Every_test_class_that_touches_the_record_keeps_a_book_of_its_own.
        private readonly IDisposable OwnRecords;

        public ValheimServerLogRulesTests()
        {
            OwnRecords = CompanionPluginStatus.BeginOwnRecords();
            Server = GetService<ValheimServer>();
            Players = GetService<IPlayerDataRepository>();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        #region World saves

        [Theory]
        // Valheim 1.0: five stages, and only stage 5 means the save is on disk.
        [InlineData("World save (5/5) done. Total time [50ms]", 50d)]
        // Anything past 999 ms is printed with thousands grouping.
        [InlineData("World save (5/5) done. Total time [1,234ms]", 1234d)]
        // Pre-1.0 servers print one line for the whole save.
        [InlineData("World saved ( 198.5ms )", 198.5d)]
        public void CompletedSaveRaisesWorldSaved(string line, double expectedMs)
        {
            decimal? observed = null;
            Server.WorldSaved += (_, ms) => observed = ms;

            Feed(line);

            Assert.Equal((decimal)expectedMs, observed);
        }

        [Fact]
        public void FailedSaveRaisesWorldSaveFailedAndNotWorldSaved()
        {
            decimal? failed = null;
            var savedFired = false;
            Server.WorldSaveFailed += (_, ms) => failed = ms;
            Server.WorldSaved += (_, _) => savedFired = true;

            Feed("World save (5/5) FAILED. Total time [12ms]");

            Assert.Equal(12m, failed);
            Assert.False(savedFired);
        }

        [Fact]
        public void StagesBeforeTheLastOneAreNotTreatedAsACompletedSave()
        {
            var savedFired = false;
            Server.WorldSaved += (_, _) => savedFired = true;

            Feed("World save (1/5) Cloud & Backup checks done [3ms] => Save number 7");
            Feed("World save (2/5) Chunks writing done [21ms]");
            Feed("World save (3/5) DB2 writing done [4ms]");
            Feed("World save (4/5) FWL writing done [1ms]");

            Assert.False(savedFired);
        }

        [Fact]
        public void LoadingAPre10WorldRaisesLegacyWorldLoaded()
        {
            var fired = 0;
            Server.LegacyWorldLoaded += (_, _) => fired++;

            Feed("ZNet.LoadOldWorld done [271ms]");

            Assert.Equal(1, fired);
        }

        #endregion

        #region Version banner

        [Theory]
        [InlineData("Valheim version: 1.0.7 (network version 39)", "1.0.7", "39")]
        [InlineData("Valheim version: l-0.221.12 (network version 36)", "l-0.221.12", "36")]
        public void VersionBannerIsReadFromTheLog(string line, string expectedGame, string expectedNetwork)
        {
            string announced = null;
            Server.VersionDetected += (_, version) => announced = version;

            Feed(line);

            Assert.Equal(expectedGame, Server.GameVersion);
            Assert.Equal(expectedNetwork, Server.NetworkVersion);
            Assert.Equal(expectedGame, announced);
        }

        #endregion

        #region Platform ids

        [Theory]
        // A console id is numeric but far longer than an int.
        [InlineData(
            "PlayFab socket with remote ID abc received local Platform ID Xbox_2535401234567890",
            PlayerPlatforms.Xbox, "2535401234567890")]
        // A PlayFab id is not numeric at all and carries underscores of its own,
        // so only the FIRST underscore separates the platform from the id.
        [InlineData(
            "PlayFab socket with remote ID abc received local Platform ID PlayFab_BakaXplay_2498_3c72cce4f7974e61d93e0eccb6b0bac56638f3a1",
            PlayerPlatforms.PlayFab, "BakaXplay_2498_3c72cce4f7974e61d93e0eccb6b0bac56638f3a1")]
        [InlineData(
            "PlayFab socket with remote ID abc received local Platform ID PlayStation_123456789",
            PlayerPlatforms.PlayStation, "123456789")]
        public void CrossplayConnectionKeepsTheWholePlatformId(string line, string platform, string playerId)
        {
            Feed(line);

            var player = Assert.Single(Players.Data);
            Assert.Equal(platform, player.Platform);
            Assert.Equal(playerId, player.PlayerId);
            Assert.Equal(PlayerStatus.Joining, player.PlayerStatus);
        }

        [Fact]
        public void CrossplayDisconnectMatchesTheJoinedPlayer()
        {
            Feed("PlayFab socket with remote ID abc received local Platform ID Nintendo_98765");

            Feed("Disconnect: The client (Nintendo_98765)");

            var player = Assert.Single(Players.Data);
            Assert.Equal(PlayerPlatforms.Nintendo, player.Platform);
            Assert.Equal("98765", player.PlayerId);
            Assert.Equal(PlayerStatus.Offline, player.PlayerStatus);
        }

        #endregion

        #region Numeric player id

        [Fact]
        public void PlayerIdLineIsRecordedOnTheSpawnedCharactersPlayer()
        {
            Feed("Got connection SteamID 76561198000000001");
            Feed("Got character ZDOID from Smithix : 12345:1");

            Feed("Got player ID from Smithix : 1454938750");

            var player = Assert.Single(Players.Data);
            Assert.Equal("Smithix", player.LastStatusCharacter);
            Assert.Equal("1454938750", player.PlayerNumericId);
        }

        [Fact]
        public void PlayerIdLineFallsBackToTheOnlyConnectingPlayer()
        {
            // The id line can arrive before the character has ever been seen.
            Feed("Got connection SteamID 76561198000000002");

            Feed("Got player ID from BrandNewCharacter : -42");

            var player = Assert.Single(Players.Data);
            Assert.Equal("-42", player.PlayerNumericId);
        }

        [Fact]
        public void PlayerIdLineIsIgnoredWhenSeveralPlayersAreConnectingAtOnce()
        {
            // Two unknown joiners and an unknown character name: guessing here
            // would stamp the id on the wrong person, so nothing is recorded.
            Feed("Got connection SteamID 76561198000000003");
            Feed("Got connection SteamID 76561198000000004");

            Feed("Got player ID from BrandNewCharacter : 999");

            Assert.All(Players.Data, p => Assert.Null(p.PlayerNumericId));
        }

        #endregion

        #region The bundled max-players plugin

        /// <summary>
        /// The plugin only raises the advertised cap when the admission rewrite actually took,
        /// and prints one fixed sentence when it did not. That sentence used to arrive as an
        /// ordinary info line among BepInEx chatter while the World hall went on showing the
        /// number the host saved, as though it were in force. The sentence is a compile-time
        /// constant in the plugin (CapStaysAtVanilla), so it is copied here verbatim and may
        /// not be reworded on either side.
        /// </summary>
        private const string PluginRefusalSentence =
            "BakaLoader Max Players: this server keeps the vanilla limit of 10 players for this start. "
            + "The number saved in the settings is not in force.";

        [Fact]
        public void The_max_players_refusal_is_lifted_to_a_warning_exactly_once()
        {
            // Boot the pipeline first, then listen: the warning goes back out through the same
            // stream, which is the whole point of it.
            Feed("Valheim version: 1.0.7 (network version 39)");

            var lines = new System.Collections.Generic.List<string>();
            void Collect(string line) { lock (lines) lines.Add(line); }
            Server.Logger.LogReceived += Collect;

            try
            {
                // Exactly as BepInEx prints it: the plugin's own log prefix in front of the
                // sentence, which is what reaches the manager's stdout reader.
                Feed("[Warning:BakaLoaderMaxPlayers] The admission cap was not raised, so the "
                    + "advertised and lobby capacity are left at the vanilla numbers as well. "
                    + PluginRefusalSentence);
            }
            finally
            {
                Server.Logger.LogReceived -= Collect;
            }

            string said;
            lock (lines) said = string.Join(" | ", lines);

            const string Warning = "Max Players could not be raised on this build of the game";
            Assert.Equal(1, said.Split(new[] { Warning }, StringSplitOptions.None).Length - 1);
            Assert.Contains("The number saved in the World hall is not in force for this session.", said);
        }

        [Fact]
        public void An_ordinary_max_players_line_raises_nothing()
        {
            Feed("Valheim version: 1.0.7 (network version 39)");

            var lines = new System.Collections.Generic.List<string>();
            void Collect(string line) { lock (lines) lines.Add(line); }
            Server.Logger.LogReceived += Collect;

            try
            {
                // The success path the plugin prints when the patch DID take. Nothing to say.
                Feed("[Info   :BakaLoaderMaxPlayers] Admission cap raised to 20 players.");
            }
            finally
            {
                Server.Logger.LogReceived -= Collect;
            }

            string said;
            lock (lines) said = string.Join(" | ", lines);

            Assert.DoesNotContain("Max Players could not be raised on this build of the game", said);
        }

        #endregion

        #region Plumbing

        /// <summary>
        /// Pushes one line through the server's log pipeline. The server only
        /// creates its logger on Start(), so the first line boots it with
        /// throwaway options pointed at the temp sandbox.
        /// </summary>
        private void Feed(string line)
        {
            if (Server.Logger == null)
            {
                Server.Start(new ValheimServerOptions
                {
                    Name = "Test Server",
                    WorldName = "Test World",
                    Password = "hunter2",
                    Port = 2456,
                    Public = false,
                    Crossplay = true,
                    SaveInterval = 30,
                    Backups = 1,
                    BackupShort = 60,
                    BackupLong = 120,
                    LogToFile = false,
                    ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
                    SaveDataFolderPath = Path.Combine(SandboxDir, "saves"),
                });
            }

            Server.Logger.Information(line);
        }

        #endregion
    }
}
