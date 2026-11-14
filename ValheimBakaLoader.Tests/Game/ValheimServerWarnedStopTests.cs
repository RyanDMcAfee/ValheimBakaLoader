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
    /// The warned stop (owner decision D1 in 1.3.3). Stop, install and start again used to take
    /// every server down at once, players or not, while a manual restart of the same server gave
    /// them a minute. The warned stop is the restart's countdown ending in a stop, so these drive
    /// the real server: the broadcasts that go out over RCON, the chips the page draws, the stop
    /// that follows, and the one hard case, a restart countdown already running when it is asked.
    /// <para>
    /// Short tiers stand in for 60/30/10 where the whole countdown is run; the default tiers are
    /// asserted on their own without waiting a minute.
    /// </para>
    /// </summary>
    [Collection(WallClockCollection.Name)]
    public class ValheimServerWarnedStopTests : IDisposable
    {
        private readonly ServiceProvider Services;
        private readonly ValheimServer Server;
        private readonly RecordingRcon Rcon = new();
        private readonly string SandboxDir;
        private readonly IDisposable OwnRecords;
        private readonly ConcurrentQueue<string> Chips = new();
        private readonly ConcurrentQueue<ServerStatus> Statuses = new();

        public ValheimServerWarnedStopTests()
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

            // The save wait a stop gives a running world, made short: these tests read the stop's
            // first moves, not how long Valheim takes to save.
            Server.StopSaveFloor = TimeSpan.FromMilliseconds(50);
            Server.CountdownTick += (_, chip) => Chips.Enqueue(chip == null ? "clear" : chip.Id + ":" + chip.Count);
            Server.StatusChanged += (_, status) => Statuses.Enqueue(status);

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-warnedstop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Server.CancelCountdown(); } catch { /* best effort */ }
            try { Server.Dispose(); } catch { /* best effort */ }
            try { Services.Dispose(); } catch { /* best effort */ }
            try { Directory.Delete(SandboxDir, true); } catch { /* best effort */ }
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Scenario 7, the server half. Players on: every tier is broadcast in the restart words,
        /// each with its chip, then "NOW", then the server goes down. A stop and not a restart:
        /// nothing is pending a relaunch.
        /// </summary>
        [Fact]
        public async Task With_players_on_every_tier_is_broadcast_then_it_stops_and_does_not_restart()
        {
            await RunningServer(players: 1);

            var warning = Server.RequestWarnedStop(new[] { 2, 1 });

            Assert.Equal(TimeSpan.FromSeconds(2) + ValheimServer.CountdownFinalPause, warning);
            Assert.True(Server.IsWarningBeforeStop, "the warning is not flagged while it runs");
            Assert.Equal(ServerStatus.Running, Server.Status);

            await Server.WarnedStopRun;

            Assert.Equal(new[]
            {
                "Server restarting in 2 seconds!", "Server restarting in 1 second!", "Server restarting NOW!",
            }, Rcon.Broadcasts.ToArray());
            Assert.Equal(new[] { "restart_in:2", "restart_in:1", "restart_now:0", "clear" }, Chips.ToArray());
            Assert.Equal(ServerStatus.Stopping, Server.Status);
            Assert.False(Server.RelaunchPending, "the warned stop came back up as a restart");
            Assert.False(Server.IsWarningBeforeStop);
        }

        /// <summary>D1: the tiers are the manual restart's own, sixty, thirty and ten seconds.</summary>
        [Fact]
        public async Task The_default_warning_is_the_manual_restart_one_minute()
        {
            await RunningServer(players: 1);

            Assert.Equal(new[] { 60, 30, 10 }, ValheimServer.ManualRestartCountdownSeconds);
            var warning = Server.RequestWarnedStop();
            Assert.Equal(TimeSpan.FromSeconds(60) + ValheimServer.CountdownFinalPause, warning);

            await WaitUntil(() => Rcon.Broadcasts.Any());
            Assert.Equal("Server restarting in 1 minute!", Rcon.Broadcasts.First());

            // A cancel (the host's Cancel restart) ends the warning without a stop. The caller's
            // poll is what stops the server then, through the plain stop.
            Server.CancelCountdown();
            await Server.WarnedStopRun;
            Assert.Equal(ServerStatus.Running, Server.Status);
            Assert.False(Server.IsWarningBeforeStop, "a cancelled warning still reads as running");
        }

        /// <summary>Nobody on: the stop is at once, and nobody is told anything.</summary>
        [Fact]
        public async Task With_nobody_on_it_stops_at_once_and_broadcasts_nothing()
        {
            await RunningServer(players: 0);

            Assert.Equal(TimeSpan.Zero, Server.RequestWarnedStop());
            Assert.Equal(ServerStatus.Stopping, Server.Status);
            Assert.False(Server.RelaunchPending);
            await Task.Delay(100);
            Assert.Empty(Rcon.Broadcasts);
            Assert.False(Server.IsWarningBeforeStop);
        }

        /// <summary>
        /// RCON off with players on: exactly what a manual restart does there, which is go at
        /// once (RestartWithCountdown has nobody it can announce to).
        /// </summary>
        [Fact]
        public async Task With_rcon_off_and_players_on_it_stops_at_once_as_a_manual_restart_would()
        {
            await RunningServer(players: 2, rcon: false);

            Assert.Equal(TimeSpan.Zero, Server.RequestWarnedStop());
            Assert.Equal(ServerStatus.Stopping, Server.Status);
            Assert.Empty(Rcon.Broadcasts);
        }

        /// <summary>
        /// Scenario 8, the server half. A restart countdown is already running when the warned
        /// stop is asked for. The players hear no "cancelled", the warned countdown is the one
        /// sequence after the press, the server goes down once, and the earlier restart never
        /// fires: nothing relaunches, not when the warning ends and not after.
        /// </summary>
        [Fact]
        public async Task A_restart_countdown_already_running_is_replaced_and_never_fires()
        {
            await RunningServer(players: 1);

            var restart = Server.RestartWithCountdown(new[] { 30 });
            await WaitUntil(() => Rcon.Broadcasts.Any());
            var before = Rcon.Broadcasts.Count();

            Server.RequestWarnedStop(new[] { 1 });
            await Server.WarnedStopRun;
            await restart;

            var after = Rcon.Broadcasts.Skip(before).ToArray();
            Assert.Equal(new[] { "Server restarting in 1 second!", "Server restarting NOW!" }, after);
            Assert.DoesNotContain(Rcon.Broadcasts, b => b.Contains("cancelled"));
            Assert.Equal(1, Statuses.Count(s => s == ServerStatus.Stopping));
            Assert.False(Server.RelaunchPending, "the replaced restart turned the stop into a restart");

            // And it stays that way: the replaced countdown had thirty seconds left on its clock.
            await Task.Delay(300);
            Assert.False(Server.RelaunchPending);
            Assert.Equal(1, Statuses.Count(s => s == ServerStatus.Stopping));
            Assert.DoesNotContain(ServerStatus.Starting, Statuses.SkipWhile(s => s != ServerStatus.Stopping));
        }

        // ---------------------------------------------------------------- plumbing

        private async Task RunningServer(int players, bool rcon = true)
        {
            Server.Start(Options(rcon));
            Server.Logger.Information("Game server connected");
            await WaitUntil(() => Server.Status == ServerStatus.Running);
            for (var i = 0; i < players; i++)
                Server.Logger.Information("Got connection SteamID 7656119800000000" + i);
        }

        private ValheimServerOptions Options(bool rcon) => new()
        {
            Name = "Warned Stop Server",
            WorldName = "Warned Stop World",
            Password = "hunter2",
            Port = 2456,
            SaveInterval = 30,
            Backups = 1,
            BackupShort = 60,
            BackupLong = 120,
            LogToFile = false,
            RconEnabled = rcon,
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

        /// <summary>An RCON client that always answers and writes down every broadcast's words.</summary>
        private sealed class RecordingRcon : IRconClient
        {
            private readonly ConcurrentQueue<string> Said = new();

            public IEnumerable<string> Broadcasts => Said;

            public bool IsConnected { get; private set; }

            public Task<bool> ConnectAsync(string host, int port, string password)
            {
                IsConnected = true;
                return Task.FromResult(true);
            }

            public Task<string> SendCommandAsync(string command)
            {
                // A broadcast reaches the game as "broadcast center" and the words after it.
                const string verb = "broadcast center ";
                var text = command ?? "";
                if (text.StartsWith(verb, StringComparison.Ordinal)) Said.Enqueue(text.Substring(verb.Length));
                return Task.FromResult("");
            }

            public void Disconnect() => IsConnected = false;
        }
    }
}
