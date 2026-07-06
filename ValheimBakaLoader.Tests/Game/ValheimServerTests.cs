using System;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// Exercises the log-line-to-player-status pipeline end to end: raw
    /// dedicated-server log messages go in, repository state and
    /// PlayerStatusChanged events come out.
    /// </summary>
    public class ValheimServerTests : BaseTest, IDisposable
    {
        // Verbatim message templates emitted by valheim_server.exe. These are
        // the game's wire format, not ours - do not reword them.
        private const string JoinLine = "Got connection SteamID {0}";
        private const string CrossplayJoinLine = "PlayFab socket with remote ID 123456 received local Platform ID {0}_{1}";
        private const string SpawnLine = "Got character ZDOID from {0} : {1}:1";
        private const string BadPasswordLine = "Peer {0} has wrong password";
        private const string DisconnectLine = "Closing socket {0}";

        private readonly ValheimServer Server;
        private readonly IPlayerDataRepository Players;

        // Throwaway on-disk sandbox (dummy exe + save folder) so Start()'s
        // path validation passes on any machine - CI runners included -
        // without a real Valheim install. The process itself is never
        // launched; BaseTest swaps in MockProcessProvider.
        private readonly string SandboxDir;

        private PlayerInfo LastChangedPlayer;

        public ValheimServerTests()
        {
            Server = GetService<ValheimServer>();
            Players = GetService<IPlayerDataRepository>();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
        }

        [Fact]
        public void FreshServerReportsStopped()
        {
            Assert.Equal(ServerStatus.Stopped, Server.Status);
        }

        [Fact]
        public void RunningStateIsDetectedFromTheLog()
        {
            ServerStatus? observed = null;
            Server.StatusChanged += (_, status) => observed = status;

            Feed("Game server connected");

            Assert.Equal(ServerStatus.Running, Server.Status);
            Assert.Equal(ServerStatus.Running, observed);
        }

        [Fact]
        public void SteamConnectionMarksPlayerJoining()
        {
            WatchStatusChanges();

            Feed(JoinLine, "1234");

            ExpectPlayer(LastChangedPlayer, PlayerPlatforms.Steam, "1234", PlayerStatus.Joining);
            ExpectPlayer(Assert.Single(Players.Data), PlayerPlatforms.Steam, "1234", PlayerStatus.Joining);
        }

        [Theory]
        [InlineData(PlayerPlatforms.Steam, "1234")]
        [InlineData(PlayerPlatforms.Xbox, "5678")]
        public void CrossplayConnectionMarksPlayerJoining(string platform, string playerId)
        {
            WatchStatusChanges();

            Feed(CrossplayJoinLine, platform, playerId);

            ExpectPlayer(LastChangedPlayer, platform, playerId, PlayerStatus.Joining);
            ExpectPlayer(Assert.Single(Players.Data), platform, playerId, PlayerStatus.Joining);
        }

        [Fact]
        public void WrongPasswordMarksPlayerLeaving()
        {
            Feed(JoinLine, "1234");
            WatchStatusChanges();

            Feed(BadPasswordLine, "1234");

            ExpectPlayer(LastChangedPlayer, PlayerPlatforms.Steam, "1234", PlayerStatus.Leaving);
            ExpectPlayer(Assert.Single(Players.Data), PlayerPlatforms.Steam, "1234", PlayerStatus.Leaving);
        }

        [Fact]
        public void ClosedSocketMarksPlayerOffline()
        {
            Feed(JoinLine, "1234");
            WatchStatusChanges();

            Feed(DisconnectLine, "1234");

            ExpectPlayer(LastChangedPlayer, PlayerPlatforms.Steam, "1234", PlayerStatus.Offline);
            ExpectPlayer(Assert.Single(Players.Data), PlayerPlatforms.Steam, "1234", PlayerStatus.Offline);
        }

        [Fact]
        public void LoneJoinerIsMatchedToTheSpawnedCharacter()
        {
            // Only one platform id is joining, so a character spawn can only
            // belong to that player.
            Feed(JoinLine, "1234");
            WatchStatusChanges();

            Feed(SpawnLine, "Broheim", "-56789123");

            ExpectPlayer(LastChangedPlayer, PlayerPlatforms.Steam, "1234", PlayerStatus.Online,
                character: "Broheim", zdoId: "-56789123");
            ExpectPlayer(Assert.Single(Players.Data), PlayerPlatforms.Steam, "1234", PlayerStatus.Online,
                character: "Broheim", zdoId: "-56789123");
        }

        [Fact]
        public void SimultaneousJoinersAreGuessedInJoinOrder()
        {
            // Three unknown players connect before any character spawns
            // (typical for a brand-new server). Each spawn should then be
            // guessed against the earliest joiner still waiting.
            var ids = new[] { "123", "456", "789" };
            Feed(JoinLine, ids[0]);
            Feed(JoinLine, ids[1]);
            Feed(JoinLine, ids[2]);
            WatchStatusChanges();

            Assert.Equal(3, Players.Data.Count());

            Feed(SpawnLine, "CharacterOne", "-432");
            ExpectPlayer(LastChangedPlayer, PlayerPlatforms.Steam, ids[0], PlayerStatus.Online,
                character: "CharacterOne", zdoId: "-432");

            Feed(SpawnLine, "CharacterTwo", "5834");
            ExpectPlayer(LastChangedPlayer, PlayerPlatforms.Steam, ids[1], PlayerStatus.Online,
                character: "CharacterTwo", zdoId: "5834");

            Feed(SpawnLine, "CharacterThree", "146131");
            ExpectPlayer(LastChangedPlayer, PlayerPlatforms.Steam, ids[2], PlayerStatus.Online,
                character: "CharacterThree", zdoId: "146131");
        }

        [Fact]
        public void ReturningPlayerCanSpawnADifferentCharacter()
        {
            // A player joins, spawns a character, and disconnects...
            Feed(JoinLine, "1234");
            Feed(SpawnLine, "CharacterOne", "234");
            Feed(DisconnectLine, "1234");
            WatchStatusChanges();

            // ...then rejoins under the same platform id...
            Feed(JoinLine, "1234");
            ExpectPlayer(LastChangedPlayer, PlayerPlatforms.Steam, "1234", PlayerStatus.Joining);
            ExpectPlayer(Assert.Single(Players.Data), PlayerPlatforms.Steam, "1234", PlayerStatus.Joining);

            // ...and spawns a brand-new character: same record, new
            // character name and zdoid, no duplicate entry.
            Feed(SpawnLine, "CharacterTwo", "567");
            ExpectPlayer(LastChangedPlayer, PlayerPlatforms.Steam, "1234", PlayerStatus.Online,
                character: "CharacterTwo", zdoId: "567");

            Assert.Single(Players.Data);
            ExpectPlayer(Players.FindById(LastChangedPlayer.Key), PlayerPlatforms.Steam, "1234", PlayerStatus.Online,
                character: "CharacterTwo", zdoId: "567");
        }

        #region Plumbing

        private void WatchStatusChanges()
            => Players.PlayerStatusChanged += (_, player) => LastChangedPlayer = player;

        /// <summary>
        /// Pushes a formatted log line through the server's log pipeline.
        /// The server only creates its logger on Start(), so the first line
        /// boots it with throwaway options pointed at the temp sandbox, which
        /// satisfies Start()'s on-disk exe/save-path validation everywhere.
        /// </summary>
        private void Feed(string template, params object[] args)
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
                    Crossplay = false,
                    SaveInterval = 30,
                    Backups = 1,
                    BackupShort = 60,
                    BackupLong = 120,
                    LogToFile = false,
                    ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
                    SaveDataFolderPath = Path.Combine(SandboxDir, "saves"),
                });
            }

            Server.Logger.Information(string.Format(template, args));
        }

        private static void ExpectPlayer(
            PlayerInfo actual,
            string platform,
            string playerId,
            PlayerStatus status,
            string character = null,
            string zdoId = null)
        {
            Assert.NotNull(actual);
            Assert.Equal(platform, actual.Platform);
            Assert.Equal(playerId, actual.PlayerId);
            Assert.Equal(status, actual.PlayerStatus);

            if (character != null) Assert.Equal(character, actual.LastStatusCharacter);
            if (zdoId != null) Assert.Equal(zdoId, actual.ZdoId);
        }

        #endregion
    }
}
