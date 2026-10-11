using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
    /// A stop guarantees a world save, or says that it could not.
    /// <para>
    /// Stop, Restart and closing the app all end in the same close request, and Valheim saves
    /// on its way down. Nothing used to ask for a save first, nothing checked that a save line
    /// appeared between the stop and the exit, and nothing said so when it did not. Now a
    /// running server with RCON on is asked to <c>save</c> and given a bounded wait for the
    /// save line before the close request goes out, and a stop whose process exits with no
    /// save line printed after the stop began leaves a warning in the Saga and a row on the
    /// condition bar.
    /// </para>
    /// <para>
    /// These drive the real stop. The close request is the mock provider's, which writes it
    /// down instead of killing anything, and the log lines are fed in exactly as the server's
    /// own output reaches the rule table. Nothing here starts a server.
    /// </para>
    /// </summary>
    [Collection(WallClockCollection.Name)]
    public class ValheimServerStopSaveTests : IDisposable
    {
        private const string SaveLine = "World save (5/5) done. Total time [1,234ms]";

        private readonly ServiceProvider Services;
        private readonly ValheimServer Server;
        private readonly MockProcessProvider Processes = new();
        private readonly SaveRconClient Rcon = new();
        private readonly string SandboxDir;
        private readonly IDisposable OwnRecords;
        private readonly TimeSpan OriginalDrain = ValheimServer.ExitLineDrain;
        private readonly ConcurrentQueue<string> Saga = new();

        public ValheimServerStopSaveTests()
        {
            OwnRecords = CompanionPluginStatus.BeginOwnRecords();

            var collection = new ServiceCollection();
            Program.ConfigureServices(collection, Array.Empty<string>());
            collection.Replace(ServiceDescriptor.Singleton<IFileProvider>(new MockDataFileProvider()));
            collection.Replace(ServiceDescriptor.Singleton<IProcessProvider>(Processes));
            collection.Replace(ServiceDescriptor.Singleton<IHttpClientProvider>(new MockHttpClientProvider()));
            collection.Replace(ServiceDescriptor.Singleton<IUserPreferencesProvider>(new MockUserPreferencesProvider()));
            collection.Replace(ServiceDescriptor.Singleton<IRconClient>(Rcon));

            Services = collection.BuildServiceProvider();
            Server = Services.GetRequiredService<ValheimServer>();

            // The drain is for a real pipe that can be read a moment after the exit. Fed lines
            // are read at once, so a short one keeps the unsaved cases quick.
            ValheimServer.ExitLineDrain = TimeSpan.FromMilliseconds(100);

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-stopsave-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            ValheimServer.ExitLineDrain = OriginalDrain;
            try { Server.Dispose(); } catch { /* best effort */ }
            try { Services.Dispose(); } catch { /* best effort */ }
            try { Directory.Delete(SandboxDir, true); } catch { /* best effort */ }
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        // ------------------------------------------------------------------- (1)

        [Fact(Timeout = 60000)]
        public async Task With_rcon_on_the_close_request_waits_for_the_save_line_and_nothing_is_raised()
        {
            var process = await RunningServer(rcon: true);

            Server.Stop();
            Assert.Equal(ServerStatus.Stopping, Server.Status);

            await WaitUntil(() => Rcon.Sent.Contains("save"));

            // Asked, and not yet answered: the close request has not gone out.
            await Task.Delay(200);
            Assert.Empty(Processes.KillRequests);
            Assert.Equal(ServerStatus.Stopping, Server.Status);

            Feed(SaveLine);

            await WaitUntil(() => Processes.KillRequests.Count == 1);
            Assert.Equal(ServerStatus.Stopping, Server.Status);

            RaiseExited(process);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.Null(Server.UnsavedStop);
            Assert.DoesNotContain(Saga, line => line.Contains("stopped without reporting a world save"));
        }

        // ------------------------------------------------------------------- (2)

        [Fact(Timeout = 60000)]
        public async Task With_rcon_on_and_no_save_line_the_close_request_follows_the_bound_and_the_exit_warns()
        {
            Server.StopSaveFloor = TimeSpan.FromMilliseconds(400);
            var process = await RunningServer(rcon: true);

            var clock = Stopwatch.StartNew();
            Server.Stop();

            await WaitUntil(() => Rcon.Sent.Contains("save"));
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            clock.Stop();

            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(350),
                "the close request went out after " + clock.ElapsedMilliseconds + "ms, before the save wait was over");
            Assert.Equal(ServerStatus.Stopping, Server.Status);

            RaiseExited(process);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            var unsaved = Server.UnsavedStop;
            Assert.NotNull(unsaved);
            Assert.Equal("Stop Save Server", unsaved.Realm);
            Assert.Equal("Stop Save World", unsaved.World);

            var expected = HostCatalog.T("host.saga.unsaved_stop", ("server", "Stop Save Server"), ("world", "Stop Save World"));
            await WaitUntil(() => Saga.Any(line => line.Contains(expected)));
        }

        // ------------------------------------------------------------------- (3)

        [Fact(Timeout = 60000)]
        public async Task With_rcon_off_the_close_request_goes_at_once_and_a_save_on_the_way_down_raises_nothing()
        {
            var process = await RunningServer(rcon: false);

            Server.Stop();

            // At once: on the same call, with nothing asked over RCON.
            Assert.Single(Processes.KillRequests);
            Assert.Empty(Rcon.Sent);
            Assert.Equal(ServerStatus.Stopping, Server.Status);

            // Valheim saves on its own way down.
            Feed(SaveLine);
            RaiseExited(process);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.Null(Server.UnsavedStop);
            Assert.DoesNotContain(Saga, line => line.Contains("stopped without reporting a world save"));
        }

        // ------------------------------------------------------------------- (4)

        [Fact(Timeout = 60000)]
        public async Task A_save_line_from_before_the_stop_request_does_not_count()
        {
            var process = await RunningServer(rcon: false);

            // The periodic save, a moment before the host pressed Stop.
            Feed(SaveLine);

            Server.Stop();
            RaiseExited(process);

            Assert.NotNull(Server.UnsavedStop);
            var expected = HostCatalog.T("host.saga.unsaved_stop", ("server", "Stop Save Server"), ("world", "Stop Save World"));
            await WaitUntil(() => Saga.Any(line => line.Contains(expected)));
        }

        /// <summary>
        /// The row is cleared by the next start that saves, and only by a save: coming back up
        /// is not proof the world was written.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task The_next_start_that_saves_clears_the_unsaved_stop()
        {
            var process = await RunningServer(rcon: false);
            Server.Stop();
            RaiseExited(process);
            Assert.NotNull(Server.UnsavedStop);

            await RunningServer(rcon: false);
            Assert.NotNull(Server.UnsavedStop);

            Feed(SaveLine);
            Assert.Null(Server.UnsavedStop);
        }

        // ------------------------------------------------------------------- (5)

        [Fact]
        public void The_save_wait_and_the_close_deadline_are_bounded()
        {
            var floor = ValheimServer.DefaultStopSaveFloor;

            // No save timed yet: the floor.
            Assert.Equal(TimeSpan.FromSeconds(30), ValheimServer.ComputeStopSaveBound(null, floor));
            // A quick save: three times it is under the floor, so still the floor.
            Assert.Equal(TimeSpan.FromSeconds(30), ValheimServer.ComputeStopSaveBound(5000m, floor));
            // A slow save: three times it.
            Assert.Equal(TimeSpan.FromSeconds(45), ValheimServer.ComputeStopSaveBound(15000m, floor));
            // A very slow one: the cap.
            Assert.Equal(TimeSpan.FromSeconds(120), ValheimServer.ComputeStopSaveBound(60000m, floor));
            Assert.Equal(ValheimServer.StopSaveCap, ValheimServer.ComputeStopSaveBound(1000000m, floor));

            // The close waits for the longest save wait plus the 60 seconds it always gave.
            Assert.Equal(TimeSpan.FromSeconds(90),
                ValheimServer.CloseDeadlineFor(new[] { TimeSpan.FromSeconds(30) }));
            Assert.Equal(TimeSpan.FromSeconds(105),
                ValheimServer.CloseDeadlineFor(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45) }));
            Assert.Equal(TimeSpan.FromSeconds(180),
                ValheimServer.CloseDeadlineFor(new[] { ValheimServer.StopSaveCap }));
            // Nothing running: the 60 seconds the close always gave.
            Assert.Equal(TimeSpan.FromSeconds(60), ValheimServer.CloseDeadlineFor(Array.Empty<TimeSpan>()));
        }

        [Fact(Timeout = 60000)]
        public async Task The_save_wait_follows_the_last_save_the_server_reported()
        {
            await RunningServer(rcon: false);
            Assert.Equal(TimeSpan.FromSeconds(30), Server.StopSaveBound);

            Feed("World save (5/5) done. Total time [20,000ms]");

            Assert.Equal(20000m, Server.LastSaveDurationMs);
            Assert.Equal(TimeSpan.FromSeconds(60), Server.StopSaveBound);
        }

        // ------------------------------------------------- a refused save is not a sent one

        [Theory(Timeout = 60000)]
        [InlineData("Error forwarding 'save' to console: Object reference not set to an instance of an object")]
        [InlineData("Unknown command: 'save' (Commander natively supports: broadcast, playerlist)")]
        public async Task A_save_that_rcon_refused_sends_the_close_request_at_once(string reply)
        {
            // A long floor: a close request that waited for it would fail the timing below.
            Server.StopSaveFloor = TimeSpan.FromSeconds(20);
            Rcon.Reply = reply;
            var process = await RunningServer(rcon: true);

            var clock = Stopwatch.StartNew();
            Server.Stop();

            await WaitUntil(() => Processes.KillRequests.Count == 1, timeoutMs: 10000);
            clock.Stop();

            Assert.Contains("save", Rcon.Sent);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5),
                "the close request waited " + clock.ElapsedMilliseconds + "ms for a save the server had refused");
            Assert.Equal(ServerStatus.Stopping, Server.Status);

            // No save line followed, so the exit still says the stop did not save.
            RaiseExited(process);
            Assert.NotNull(Server.UnsavedStop);
        }

        [Fact]
        public void Only_commanders_failure_replies_count_as_a_refused_save()
        {
            Assert.False(ValheimServer.SaveWasRefused(null));
            Assert.False(ValheimServer.SaveWasRefused(""));
            Assert.False(ValheimServer.SaveWasRefused("Forwarded to console: save"));
            Assert.True(ValheimServer.SaveWasRefused("Unknown command: 'save'"));
            Assert.True(ValheimServer.SaveWasRefused("  Error forwarding 'save' to console: boom"));
        }

        [Fact]
        public void The_app_close_waits_for_the_save_bound_plus_the_grace()
        {
            // The close loop is in a WinForms window that a test cannot drive, so this holds its
            // source: the deadline it waits on has to come from CloseDeadlineFor over every
            // server's save bound, or a slow save is cut off by the old flat 60 seconds.
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs")))
                root = Path.GetDirectoryName(root);
            Assert.NotNull(root);

            var source = File.ReadAllText(Path.Combine(root, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"));
            var start = source.IndexOf("private async void ShutDownAllServersThenClose()", StringComparison.Ordinal);
            Assert.True(start >= 0, "ShutDownAllServersThenClose is gone");
            var end = source.IndexOf("public void BeginShutDownAndClose()", start, StringComparison.Ordinal);
            Assert.True(end > start);
            var body = source.Substring(start, end - start);

            Assert.Contains("ValheimServer.CloseDeadlineFor(", body);
            Assert.Contains("s.Server.StopSaveBound", body);
            Assert.DoesNotContain("AddSeconds(60)", body);
            Assert.DoesNotContain("FromSeconds(60)", body);
        }

        // ---------------------------------------------------------------- plumbing

        private async Task<Process> RunningServer(bool rcon)
        {
            Server.Start(Options(rcon));
            await WaitUntil(() => Server.GetTrackedProcess() != null);
            Server.Logger.LogReceived += line => Saga.Enqueue(line ?? "");
            Feed("Game server connected");
            await WaitUntil(() => Server.Status == ServerStatus.Running);
            return Server.GetTrackedProcess();
        }

        private void Feed(string line) => Server.Logger.Information(line);

        private static void RaiseExited(Process process)
        {
            var onExited = typeof(Process).GetMethod(
                "OnExited", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(onExited);
            onExited.Invoke(process, null);
        }

        private ValheimServerOptions Options(bool rcon) => new()
        {
            Name = "Stop Save Server",
            WorldName = "Stop Save World",
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

        /// <summary>Answers every command at once, the way Commander answers <c>save</c>, and writes it down.</summary>
        private sealed class SaveRconClient : IRconClient
        {
            public readonly ConcurrentQueue<string> Sent = new();

            /// <summary>What every command is answered with. Commander's "handed it on" reply by default.</summary>
            public string Reply = "";

            public bool IsConnected { get; private set; }

            public Task<bool> ConnectAsync(string host, int port, string password)
            {
                IsConnected = true;
                return Task.FromResult(true);
            }

            public Task<string> SendCommandAsync(string command)
            {
                Sent.Enqueue(command ?? "");
                return Task.FromResult(Reply);
            }

            public void Disconnect() => IsConnected = false;
        }
    }
}
