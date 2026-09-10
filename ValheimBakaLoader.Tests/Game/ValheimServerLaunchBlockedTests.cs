using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// Two things a launch must never do. It must not start a server out of an install another
    /// process is rewriting: the exe and the managed assemblies beside it are half written while
    /// Steam or steamcmd works, and the launch guard cannot see that because it compares builds
    /// and the build on disk during a rewrite is whatever the writer has got to so far. And it
    /// must not give the launch claim back before the start has taken hold, because everything
    /// that asks "may I start?" sees a free flag for the whole of that window.
    /// </summary>
    public class ValheimServerLaunchBlockedTests : BaseTest, IDisposable
    {
        private readonly ValheimServer Server;
        private readonly string SandboxDir;

        public ValheimServerLaunchBlockedTests()
        {
            Server = GetService<ValheimServer>();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-blocked-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Server.Dispose(); } catch { /* best effort */ }
            try { Directory.Delete(SandboxDir, true); } catch { /* best effort */ }
            GC.SuppressFinalize(this);
        }

        // ---------------------------------------------------------------- the update gate

        [Fact]
        public async Task A_start_is_refused_while_an_update_is_rewriting_the_install()
        {
            var guardAsked = 0;
            Server.ConfirmLaunchAsync = _ =>
            {
                Interlocked.Increment(ref guardAsked);
                return Task.FromResult(LaunchDecision.Go());
            };

            LaunchSettledEventArgs settled = null;
            Server.LaunchSettled += (_, e) => settled = e;

            Server.LaunchBlocked = () => true;
            Server.Start(Options());

            await WaitUntil(() => settled != null);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.Equal(0, guardAsked);
            Assert.Equal(ValheimServer.LaunchBlockedMessage, settled.Error);

            // And the claim was never taken, so the next start is live the moment the update ends.
            Assert.True(Server.CanStart);
        }

        [Fact]
        public async Task An_automatic_launch_is_refused_the_same_way()
        {
            // Nobody is at the keyboard for these: the crash relaunch, the scheduled restart, the
            // empty server restart, the auto start and the held launch retry all come through the
            // same door, so they all have to stand down together.
            LaunchSettledEventArgs settled = null;
            Server.LaunchSettled += (_, e) => settled = e;
            Server.ConfirmLaunchAsync = _ => Task.FromResult(LaunchDecision.Go());

            Server.LaunchBlocked = () => true;
            Server.StartAutomatically(Options());

            await WaitUntil(() => settled != null);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.Equal(ValheimServer.LaunchBlockedMessage, settled.Error);
        }

        [Fact]
        public async Task An_automatic_launch_that_was_refused_asks_again_later()
        {
            // A scheduled restart landing in the stop to start gap of an update would otherwise
            // leave the server down until somebody noticed it had never come back.
            LaunchSettledEventArgs settled = null;
            Server.LaunchSettled += (_, e) => settled = e;
            Server.ConfirmLaunchAsync = _ => Task.FromResult(LaunchDecision.Go());

            Server.LaunchBlocked = () => true;
            Server.StartAutomatically(Options());
            await WaitUntil(() => settled != null);

            Assert.True(Server.LaunchRetryArmed);
        }

        [Fact]
        public async Task A_manual_start_that_was_refused_arms_nothing()
        {
            // The host is right there and can press Start again, so there is nothing to schedule.
            LaunchSettledEventArgs settled = null;
            Server.LaunchSettled += (_, e) => settled = e;

            Server.LaunchBlocked = () => true;
            Server.Start(Options());
            await WaitUntil(() => settled != null);

            Assert.False(Server.LaunchRetryArmed);
        }

        [Fact]
        public async Task A_start_goes_ahead_once_the_update_has_finished()
        {
            var blocked = true;
            Server.LaunchBlocked = () => blocked;
            Server.ConfirmLaunchAsync = _ => Task.FromResult(LaunchDecision.Go());

            Server.Start(Options());
            await WaitUntil(() => !Server.LaunchInProgress);
            Assert.Equal(ServerStatus.Stopped, Server.Status);

            blocked = false;
            Server.Start(Options());

            await WaitUntil(() => Server.Status == ServerStatus.Starting);
            Assert.Equal(ServerStatus.Starting, Server.Status);
        }

        [Fact]
        public void A_hook_that_throws_never_blocks_a_launch()
        {
            // The hook is best effort. Refusing every start because a status read failed would be
            // worse than the window it guards.
            Server.LaunchBlocked = () => throw new InvalidOperationException("update service gone");

            Server.Start(Options());

            Assert.Equal(ServerStatus.Starting, Server.Status);
        }

        // ---------------------------------------------------------------- LG-12: the claim

        [Fact]
        public async Task The_launch_claim_is_held_until_the_start_has_taken_hold()
        {
            // The claim used to be given back BEFORE the start ran, and the start does real work
            // before it sets anything a second caller would notice: it copies five companion
            // plugin files into place and rewrites the access lists. Throughout that stretch
            // CanStart read true again, so the held launch retry timer could fire, claim the flag
            // and run the whole guarded launch a second time, world snapshot included.
            //
            // The probe is the executable path, which the start reads as its very first act.
            var seen = new List<bool>();
            var options = new ProbingOptions(Options(), () => seen.Add(Server.CanStart));

            Server.ConfirmLaunchAsync = _ => Task.FromResult(LaunchDecision.Go());
            Server.Start(options);

            await WaitUntil(() => Server.Status == ServerStatus.Starting);

            Assert.NotEmpty(seen);
            Assert.DoesNotContain(true, seen);
        }

        // ---------------------------------------------------------------- plumbing

        private ValheimServerOptions Options() => new()
        {
            Name = "Blocked Test Server",
            WorldName = "Blocked Test World",
            Password = "hunter2",
            Port = 2456,
            SaveInterval = 30,
            Backups = 1,
            BackupShort = 60,
            BackupLong = 120,
            LogToFile = false,
            ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
            SaveDataFolderPath = Path.Combine(SandboxDir, "saves"),
        };

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(condition(), "The condition never came true.");
        }

        /// <summary>
        /// The real options with one wire tapped: every read of the executable path calls back,
        /// which is how a test gets to look at the server's own state from inside the start.
        /// </summary>
        private sealed class ProbingOptions : IValheimServerOptions
        {
            private readonly IValheimServerOptions Inner;
            private readonly Action OnExeRead;

            public ProbingOptions(IValheimServerOptions inner, Action onExeRead)
            {
                Inner = inner;
                OnExeRead = onExeRead;
            }

            public string ServerExePath
            {
                get
                {
                    OnExeRead();
                    return Inner.ServerExePath;
                }
            }

            public string Name => Inner.Name;
            public string Password => Inner.Password;
            public string WorldName => Inner.WorldName;
            public bool Public => Inner.Public;
            public int Port => Inner.Port;
            public bool Crossplay => Inner.Crossplay;
            public int SaveInterval => Inner.SaveInterval;
            public int Backups => Inner.Backups;
            public int BackupShort => Inner.BackupShort;
            public int BackupLong => Inner.BackupLong;
            public string AdditionalArgs => Inner.AdditionalArgs;
            public string SaveDataFolderPath => Inner.SaveDataFolderPath;
            public bool LogToFile => Inner.LogToFile;
            public string LogFolderPath => Inner.LogFolderPath;
            public string LastLaunchedServerBuild => Inner.LastLaunchedServerBuild;
            public string LastLaunchedServerFingerprint => Inner.LastLaunchedServerFingerprint;
            public string LastLaunchedGameVersion => Inner.LastLaunchedGameVersion;
            public bool AutoRestart => Inner.AutoRestart;
            public int AutoRestartDelay => Inner.AutoRestartDelay;
            public bool EmptyServerRestart => Inner.EmptyServerRestart;
            public int EmptyServerRestartDelayMinutes => Inner.EmptyServerRestartDelayMinutes;
            public bool ScheduledRestart => Inner.ScheduledRestart;
            public int ScheduledRestartHours => Inner.ScheduledRestartHours;
            public bool RconEnabled => Inner.RconEnabled;
            public int RconPort => Inner.RconPort;
            public string RconPassword => Inner.RconPassword;
            public bool LogFilteringDisabled => Inner.LogFilteringDisabled;
            public Action<string> LogMessageHandler => Inner.LogMessageHandler;
            public string WorldPreset => Inner.WorldPreset;
            public Dictionary<string, string> WorldModifiers => Inner.WorldModifiers;
            public HashSet<string> WorldKeys => Inner.WorldKeys;
        }
    }
}
