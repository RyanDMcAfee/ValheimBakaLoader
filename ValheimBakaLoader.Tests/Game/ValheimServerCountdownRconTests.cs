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
    /// The restart countdown shares one RCON client with everything else that speaks RCON, and
    /// that client is only ever authenticated for as long as nobody else has touched it: a
    /// connect clears the flag for the length of the handshake and a disconnect clears it
    /// outright. The roster's position poll does both every few seconds, all day, while a server
    /// is up.
    /// <para>
    /// The countdown used to check the flag and send as two separate steps with no lock between
    /// them, so the poll landing in that gap left the send with an unauthenticated client. It
    /// returned nothing, the countdown swallowed it and carried on, and the server went down with
    /// players in the world and no warning. The announcements now take the same single socket gate
    /// every other caller takes.
    /// </para>
    /// </summary>
    public class ValheimServerCountdownRconTests : IDisposable
    {
        private readonly ServiceProvider Services;
        private readonly ValheimServer Server;
        private readonly GatedRconClient Rcon = new();
        private readonly string SandboxDir;

        public ValheimServerCountdownRconTests()
        {
            var collection = new ServiceCollection();
            Program.ConfigureServices(collection, Array.Empty<string>());

            // The same boundary fakes BaseTest installs, plus the RCON client itself: this test
            // is about who is allowed to touch that client at the same time as who else.
            collection.Replace(ServiceDescriptor.Singleton<IFileProvider>(new MockDataFileProvider()));
            collection.Replace(ServiceDescriptor.Singleton<IProcessProvider>(new MockProcessProvider()));
            collection.Replace(ServiceDescriptor.Singleton<IHttpClientProvider>(new MockHttpClientProvider()));
            collection.Replace(ServiceDescriptor.Singleton<IUserPreferencesProvider>(new MockUserPreferencesProvider()));
            collection.Replace(ServiceDescriptor.Singleton<IRconClient>(Rcon));

            Services = collection.BuildServiceProvider();
            Server = Services.GetRequiredService<ValheimServer>();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-countdown-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Server.CancelCountdown(); } catch { /* best effort */ }
            Rcon.ReleaseHold();
            try { Server.Dispose(); } catch { /* best effort */ }
            try { Services.Dispose(); } catch { /* best effort */ }
            try { Directory.Delete(SandboxDir, true); } catch { /* best effort */ }
            GC.SuppressFinalize(this);
        }

        [Fact]
        public async Task A_countdown_announcement_waits_for_whoever_holds_the_rcon_client()
        {
            await RunningServer();

            // Somebody else is mid command: the roster's position poll is exactly this, and it
            // runs every five seconds for the whole life of the server.
            var holder = Task.Run(() => Server.SendRconCommandAsync(GatedRconClient.HoldCommand));
            await Rcon.WaitUntilHeld();

            // Now the countdown starts. Its announcements must queue behind the command in
            // flight rather than reading a flag the holder is about to clear.
            var countdown = Server.RestartWithCountdown(new[] { 1 });

            await Task.Delay(300);
            Assert.Empty(Rcon.Broadcasts);

            Rcon.ReleaseHold();
            await holder;

            await WaitUntil(() => Rcon.Broadcasts.Any(b => b.Contains("Server restarting")));
            Server.CancelCountdown();
            await countdown;

            // Nothing was sent while the client belonged to somebody else, so nothing was lost.
            Assert.Empty(Rcon.Dropped);
        }

        [Fact]
        public async Task No_two_callers_are_ever_inside_the_rcon_client_at_once()
        {
            await RunningServer();

            var countdown = Server.RestartWithCountdown(new[] { 3, 2, 1 });

            // Hammer the shared client the way the roster poll does while the countdown runs.
            var deadline = DateTime.UtcNow.AddMilliseconds(1200);
            while (DateTime.UtcNow < deadline)
            {
                await Server.SendRconCommandAsync("playerlist", quiet: true);
            }

            Server.CancelCountdown();
            await countdown;

            Assert.Equal(0, Rcon.Overlaps);
            Assert.Empty(Rcon.Dropped);
        }

        // ---------------------------------------------------------------- plumbing

        private async Task RunningServer()
        {
            Server.Start(Options());
            Server.Logger.Information("Game server connected");
            await WaitUntil(() => Server.Status == ServerStatus.Running);
        }

        private ValheimServerOptions Options() => new()
        {
            Name = "Countdown Test Server",
            WorldName = "Countdown Test World",
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

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 8000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(condition(), "The condition never came true.");
        }

        /// <summary>
        /// Stands in for the real client and keeps its two awkward habits: a connect clears the
        /// authenticated flag for the length of the handshake, and a disconnect clears it for
        /// good. A send with the flag down answers null, exactly as the real one does, and is
        /// recorded as dropped. One command can be held open on request, so a test can decide
        /// when somebody else is mid command rather than hoping for it.
        /// </summary>
        private sealed class GatedRconClient : IRconClient
        {
            public const string HoldCommand = "hold-the-client";

            private readonly TaskCompletionSource<bool> Held =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> Release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private volatile bool Validated;
            private int Inside;

            public readonly ConcurrentQueue<string> Sent = new();
            public readonly ConcurrentQueue<string> Dropped = new();
            public int Overlaps;

            public IEnumerable<string> Broadcasts => Sent.Where(c => c.Contains("Server restarting"));

            public bool IsConnected => Validated;

            public Task WaitUntilHeld() => Held.Task;

            public void ReleaseHold() => Release.TrySetResult(true);

            public async Task<bool> ConnectAsync(string host, int port, string password)
            {
                using var _ = Enter();

                // The real one starts with a disconnect and only re-validates after a full TCP
                // connect plus auth handshake, so the flag is down for a real stretch of time.
                Validated = false;
                await Task.Delay(15);
                Validated = true;
                return true;
            }

            public async Task<string> SendCommandAsync(string command)
            {
                using var _ = Enter();

                if (!Validated)
                {
                    Dropped.Enqueue(command);
                    return null;
                }

                if (command != null && command.Contains(HoldCommand))
                {
                    Held.TrySetResult(true);
                    await Release.Task;
                }
                else
                {
                    await Task.Delay(15);
                }

                Sent.Enqueue(command ?? "");
                return "";
            }

            public void Disconnect()
            {
                using var _ = Enter();
                Validated = false;
            }

            private Scope Enter()
            {
                if (System.Threading.Interlocked.Increment(ref Inside) > 1)
                    System.Threading.Interlocked.Increment(ref Overlaps);

                return new Scope(this);
            }

            private readonly struct Scope : IDisposable
            {
                private readonly GatedRconClient Owner;

                public Scope(GatedRconClient owner) => Owner = owner;

                public void Dispose() => System.Threading.Interlocked.Decrement(ref Owner.Inside);
            }
        }
    }
}
