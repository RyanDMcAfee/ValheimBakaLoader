using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Data;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Processes;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// A kick used to go out as the player's NAME and nothing else, which is the one thing
    /// about a player the app can get wrong: the name was learned from the server's console
    /// output, and until 1.2.0 that output was read with the machine's code page, so a name in
    /// Greek left the app as four characters nobody answers to. The game matches names exactly,
    /// so the kick reached no one while the player stood there.
    /// <para>
    /// The platform id goes first now. It is ASCII, it comes off a different log line, and the
    /// game resolves it without ever comparing a name. The name is still sent when the id finds
    /// nobody, which is the honest outcome for an id the app assembled from a line the game had
    /// already rewritten.
    /// </para>
    /// </summary>
    public class ValheimServerKickTests : IDisposable
    {
        private readonly ServiceProvider Services;
        private readonly ValheimServer Server;
        private readonly ScriptedRconClient Rcon = new();
        private readonly string SandboxDir;
        private readonly IDisposable OwnRecords;

        public ValheimServerKickTests()
        {
            OwnRecords = CompanionPluginStatus.BeginOwnRecords();

            var collection = new ServiceCollection();
            Program.ConfigureServices(collection, Array.Empty<string>());
            collection.Replace(ServiceDescriptor.Singleton<IFileProvider>(new MockDataFileProvider()));
            collection.Replace(ServiceDescriptor.Singleton<IProcessProvider>(new MockProcessProvider()));
            collection.Replace(ServiceDescriptor.Singleton<IHttpClientProvider>(new MockHttpClientProvider()));
            collection.Replace(ServiceDescriptor.Singleton<IUserPreferencesProvider>(new MockUserPreferencesProvider()));
            collection.Replace(ServiceDescriptor.Singleton<IRconClient>(Rcon));

            Services = collection.BuildServiceProvider();
            Server = Services.GetRequiredService<ValheimServer>();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-kick-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Server.Dispose(); } catch { /* best effort */ }
            try { Services.Dispose(); } catch { /* best effort */ }
            try { Directory.Delete(SandboxDir, true); } catch { /* best effort */ }
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        // ------------------------------------------------------------- reading the answer

        [Theory]
        [InlineData("Error: no player named 'Bjorn' is online", true)]
        [InlineData("Error: NO PLAYER NAMED 'Bjorn' is online", true)]
        [InlineData("09/19/2026 21:06:01: Error: no player named 'Bjorn' is online", true)]
        [InlineData("Console: Error: no player named 'Bjorn' is online", true)]
        [InlineData("Kicked: Bjorn", false)]
        [InlineData("Error: server not ready (world still loading)", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void A_kick_that_found_nobody_is_told_apart_from_every_other_answer(string reply, bool nobody)
        {
            // Only the one sentence means "try the name instead". An empty answer must NOT,
            // because the vanilla console answers a kick with nothing at all and a second kick
            // fired on silence would be a kick nobody asked for.
            Assert.Equal(nobody, ValheimServer.KickFoundNobody(reply));
        }

        // ------------------------------------------------------------------ what goes out

        [Fact]
        public async Task The_platform_id_goes_first_when_the_roster_knows_one()
        {
            await RunningServer();
            Rcon.Answer = _ => "Kicked: " + Mojibake.Greek;

            var reply = await Server.KickAsync(Mojibake.Greek, "Steam_76561198000000001");

            Assert.Equal("kick Steam_76561198000000001", Assert.Single(Rcon.Sent));
            Assert.Equal("Kicked: " + Mojibake.Greek, reply);
        }

        [Fact]
        public async Task The_name_is_still_sent_when_the_id_finds_nobody()
        {
            await RunningServer();
            Rcon.Answer = command => command.Contains("Steam_")
                ? "Error: no player named 'Steam_76561198000000009' is online"
                : "Kicked: Bjorn";

            var reply = await Server.KickAsync("Bjorn", "Steam_76561198000000009");

            Assert.Equal(
                new[] { "kick Steam_76561198000000009", "kick Bjorn" },
                Rcon.Sent.ToArray());
            Assert.Equal("Kicked: Bjorn", reply);
        }

        [Fact]
        public async Task A_refusal_is_answered_as_it_stands_rather_than_kicking_again_by_name()
        {
            await RunningServer();
            Rcon.Answer = _ => "Error: server not ready (world still loading)";

            var reply = await Server.KickAsync("Bjorn", "Steam_76561198000000001");

            // One command, not two: the server did not say the player is missing, it said it
            // could not do the work, and sending it again by name would ask twice for nothing.
            Assert.Single(Rcon.Sent);
            Assert.Equal("Error: server not ready (world still loading)", reply);
        }

        [Fact]
        public async Task With_no_id_known_the_kick_goes_out_by_name_exactly_as_it_always_did()
        {
            await RunningServer();
            Rcon.Answer = _ => "Kicked: Bjorn";

            await Server.KickAsync("Bjorn");
            await Server.KickAsync("Bjorn", "");
            await Server.KickAsync("Bjorn", "   ");

            Assert.Equal(new[] { "kick Bjorn", "kick Bjorn", "kick Bjorn" }, Rcon.Sent.ToArray());
        }

        /// <summary>
        /// A host who typed the id into the palette hands the same text as both, and the kick
        /// must not be sent twice for it.
        /// </summary>
        [Fact]
        public async Task An_id_that_is_also_the_target_is_sent_once()
        {
            await RunningServer();
            Rcon.Answer = _ => "Error: no player named 'Steam_1' is online";

            await Server.KickAsync("Steam_1", "Steam_1");

            Assert.Single(Rcon.Sent);
        }

        // ---------------------------------------------------------------------- plumbing

        private async Task RunningServer()
        {
            Server.Start(Options());
            Server.Logger.Information("Game server connected");
            await WaitUntil(() => Server.Status == ServerStatus.Running);
            Rcon.Sent.Clear();
        }

        private ValheimServerOptions Options() => new()
        {
            Name = "Kick Test Server",
            WorldName = "Kick Test World",
            Password = "hunter2",
            Port = 2456,
            SaveInterval = 30,
            Backups = 1,
            BackupShort = 60,
            BackupLong = 120,
            LogToFile = false,
            RconEnabled = true,
            RconPort = 2458,
            RconPassword = "rconpass",
            ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
            SaveDataFolderPath = Path.Combine(SandboxDir, "saves"),
        };

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 30000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(condition(), "The condition never came true.");
        }

        /// <summary>
        /// An RCON client that keeps every command it was given, in order, and answers each one
        /// the way the test says the server would. The roster's position poll runs on its own
        /// timer against the same client, so playerlist is dropped from the record: this test is
        /// about what a kick sends.
        /// </summary>
        private sealed class ScriptedRconClient : IRconClient
        {
            public readonly List<string> Sent = new();

            public Func<string, string> Answer = _ => "";

            public bool IsConnected { get; private set; }

            public Task<bool> ConnectAsync(string host, int port, string password)
            {
                IsConnected = true;
                return Task.FromResult(true);
            }

            public Task<string> SendCommandAsync(string command)
            {
                var text = command ?? "";
                if (text.StartsWith("playerlist", StringComparison.Ordinal)) return Task.FromResult("");

                lock (Sent) Sent.Add(text);
                return Task.FromResult(Answer(text));
            }

            public void Disconnect() => IsConnected = false;
        }
    }
}
