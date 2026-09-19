using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The death line, and the one thing it must NOT do.
    /// <para>
    /// Valheim prints a character's death as a spawn line carrying a cleared ZDOID:
    /// "Got character ZDOID from Broheim : 0:0". The handler noticed that, raised
    /// PlayerDied, and then FELL THROUGH into SetPlayerOnline with the id "0", which is
    /// the id of nobody. Whoever that call resolved to was marked online, carrying a zdo
    /// the game had just thrown away, by a line that says the opposite of online.
    /// </para>
    /// <para>
    /// The damage is not on the dead player's own record, which is already online and so
    /// matches nothing the resolver looks for. It lands on somebody ELSE: a second viking
    /// still connecting is the only identity "joining right now" resolves to, so they were
    /// flipped online under the dead one's character name and given ZdoId "0". Every later
    /// lookup by zdo (Closing socket, the abandoned-zdo line) then matched them.
    /// </para>
    /// </summary>
    public class ValheimServerDeathTests : BaseTest, IDisposable
    {
        // Throwaway on-disk sandbox (dummy exe + save folder) so Start()'s path validation
        // passes. The process is never launched: BaseTest swaps in MockProcessProvider.
        private readonly string SandboxDir;

        // A record book of its own. Starting a server runs the companion plugin install
        // pass, and that pass clears the record for the realm it starts. See
        // CompanionPluginStatusTests.Every_test_class_that_touches_the_record_keeps_a_book_of_its_own.
        private readonly IDisposable OwnRecords;

        public ValheimServerDeathTests()
        {
            OwnRecords = CompanionPluginStatus.BeginOwnRecords();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-death-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// The direct statement of the fix: a death reaches the repository through nothing
        /// at all. The repository is a mock here rather than the real one, so the assertion
        /// is about the CALL and not about what the call would have done with it.
        /// </summary>
        [Fact]
        public void A_death_line_never_marks_anybody_online()
        {
            var book = new Mock<IPlayerDataRepository>();
            book.Setup(r => r.Data).Returns(Array.Empty<PlayerInfo>());

            ServiceCollection.Replace(ServiceDescriptor.Singleton(book.Object));
            using var provider = ServiceCollection.BuildServiceProvider();
            var server = provider.GetRequiredService<ValheimServer>();

            var died = new List<string>();
            server.PlayerDied += (_, name) => died.Add(name);

            Feed(server, "Got character ZDOID from Broheim : 0:0");

            Assert.Equal(new[] { "Broheim" }, died);
            book.Verify(
                r => r.SetPlayerOnline(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        /// <summary>
        /// An ordinary spawn is untouched by the fix: the line that carries a real id still
        /// marks the character online. Without this the test above would pass just as
        /// happily on a handler that had stopped recording spawns altogether.
        /// </summary>
        [Fact]
        public void A_real_spawn_line_still_marks_the_character_online()
        {
            var book = new Mock<IPlayerDataRepository>();
            book.Setup(r => r.Data).Returns(Array.Empty<PlayerInfo>());

            ServiceCollection.Replace(ServiceDescriptor.Singleton(book.Object));
            using var provider = ServiceCollection.BuildServiceProvider();
            var server = provider.GetRequiredService<ValheimServer>();

            var died = new List<string>();
            server.PlayerDied += (_, name) => died.Add(name);

            Feed(server, "Got character ZDOID from Broheim : 12345:1");

            Assert.Empty(died);
            book.Verify(r => r.SetPlayerOnline("Broheim", "12345", It.IsAny<string>()), Times.Once);
        }

        /// <summary>
        /// The same thing again through the REAL record book, which is where the damage was
        /// visible: one viking online and a second still connecting when the first dies.
        /// The fall-through resolved the death to "whoever is joining right now" and wrote
        /// the dead character's name and a zero id onto them.
        /// </summary>
        [Fact]
        public void A_death_does_not_drag_a_connecting_viking_online_under_the_dead_name()
        {
            var server = GetService<ValheimServer>();
            var book = GetService<IPlayerDataRepository>();

            // Broheim connects and spawns: online, carrying a real zdo.
            Feed(server, "Got connection SteamID 76561198000000011");
            Feed(server, "Got character ZDOID from Broheim : 12345:1");

            // Smithix is still connecting when Broheim dies.
            Feed(server, "Got connection SteamID 76561198000000012");

            var died = new List<string>();
            server.PlayerDied += (_, name) => died.Add(name);

            Feed(server, "Got character ZDOID from Broheim : 0:0");

            Assert.Equal(new[] { "Broheim" }, died);

            // Nobody anywhere carries the id of nobody.
            Assert.DoesNotContain(book.Data, p => p.ZdoId == "0");

            var broheim = Assert.Single(book.Data.Where(p => p.PlayerId == "76561198000000011"));
            Assert.Equal(PlayerStatus.Online, broheim.PlayerStatus);
            Assert.Equal("12345", broheim.ZdoId);

            // And the one who was merely connecting is still connecting, under no name.
            var joining = Assert.Single(book.Data.Where(p => p.PlayerId == "76561198000000012"));
            Assert.Equal(PlayerStatus.Joining, joining.PlayerStatus);
            Assert.Null(joining.ZdoId);
            Assert.Null(joining.LastStatusCharacter);
            Assert.True(
                joining.Characters == null
                    || joining.Characters.TrueForAll(c => c.CharacterName != "Broheim"),
                "the viking who was only connecting was given the dead one's character");
        }

        /// <summary>
        /// Pushes one line through a server's log pipeline. The logger only exists once
        /// Start() has run, so the first line boots it against the temp sandbox.
        /// </summary>
        private void Feed(ValheimServer server, string line)
        {
            if (server.Logger == null)
            {
                server.Start(new ValheimServerOptions
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

            server.Logger.Information(line);
        }
    }
}
