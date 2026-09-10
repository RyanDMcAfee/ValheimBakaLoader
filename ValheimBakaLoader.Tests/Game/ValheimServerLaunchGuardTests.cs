using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The gate every launch goes through. What matters here is that the hook is genuinely
    /// consulted (a held launch really does leave the server down), that a start with no hook
    /// wired behaves exactly as it always did, and that what a launched server ran gets
    /// recorded against the profile.
    /// </summary>
    public class ValheimServerLaunchGuardTests : BaseTest, IDisposable
    {
        private readonly ValheimServer Server;
        private readonly string SandboxDir;

        public ValheimServerLaunchGuardTests()
        {
            Server = GetService<ValheimServer>();

            // Same throwaway sandbox the other server tests use: Start()'s path validation is
            // real, the process launch is not (BaseTest swaps in MockProcessProvider).
            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-guard-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Server.Dispose(); } catch { /* best effort */ }
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
        }

        [Fact]
        public void With_no_guard_wired_a_start_behaves_exactly_as_before()
        {
            Server.Start(Options());

            Assert.Equal(ServerStatus.Starting, Server.Status);
        }

        [Fact]
        public async Task A_held_launch_leaves_the_server_stopped()
        {
            var asked = new TaskCompletionSource<LaunchContext>();
            Server.ConfirmLaunchAsync = context =>
            {
                asked.TrySetResult(context);
                return Task.FromResult(LaunchDecision.Hold("the build changed"));
            };

            Server.Start(Options());
            var seen = await WithTimeout(asked.Task);

            Assert.False(seen.Automatic);
            Assert.Equal(LaunchReasons.Manual, seen.Reason);
            await WaitUntil(() => !Server.LaunchInProgress);
            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.True(Server.CanStart);
        }

        [Fact]
        public async Task A_cleared_launch_starts_the_server()
        {
            Server.ConfirmLaunchAsync = _ => Task.FromResult(LaunchDecision.Go());

            Server.Start(Options());

            await WaitUntil(() => Server.Status == ServerStatus.Starting);
            Assert.Equal(ServerStatus.Starting, Server.Status);
        }

        [Fact]
        public async Task A_guard_that_throws_holds_the_launch_rather_than_starting()
        {
            Server.ConfirmLaunchAsync = _ => throw new InvalidOperationException("guard exploded");

            Server.Start(Options());

            await WaitUntil(() => !Server.LaunchInProgress);
            Assert.Equal(ServerStatus.Stopped, Server.Status);
        }

        // --- a launch that ends with the server still down has to say so -------------------

        [Fact]
        public async Task A_held_launch_reports_that_the_attempt_is_over()
        {
            var settled = new TaskCompletionSource<LaunchSettledEventArgs>();
            Server.LaunchSettled += (s, e) => settled.TrySetResult(e);
            Server.ConfirmLaunchAsync = _ => Task.FromResult(LaunchDecision.Hold("the build changed"));

            Server.Start(Options());
            var seen = await WithTimeout(settled.Task);

            // Status never moved, so this event is the only thing that re-enables the button.
            Assert.True(seen.Held);
            Assert.Null(seen.Error);
            Assert.Equal(LaunchReasons.Manual, seen.Reason);
            Assert.True(Server.CanStart);
        }

        [Fact]
        public async Task A_guard_that_throws_reports_a_failure_rather_than_a_hold()
        {
            var settled = new TaskCompletionSource<LaunchSettledEventArgs>();
            Server.LaunchSettled += (s, e) => settled.TrySetResult(e);
            Server.ConfirmLaunchAsync = _ => throw new InvalidOperationException("guard exploded");

            Server.Start(Options());
            var seen = await WithTimeout(settled.Task);

            Assert.False(seen.Held);
            Assert.False(string.IsNullOrWhiteSpace(seen.Error));
        }

        [Fact]
        public async Task A_bad_exe_path_comes_back_to_the_host_instead_of_only_the_log()
        {
            // The guard moved the start onto a background thread, which swallowed the message
            // that used to travel out of the start call and name the missing file.
            var settled = new TaskCompletionSource<LaunchSettledEventArgs>();
            Server.LaunchSettled += (s, e) => settled.TrySetResult(e);
            Server.ConfirmLaunchAsync = _ => Task.FromResult(LaunchDecision.Go());

            var options = Options();
            options.ServerExePath = Path.Combine(SandboxDir, "not-here", "valheim_server.exe");
            Server.Start(options);

            var seen = await WithTimeout(settled.Task);

            Assert.False(seen.Held);
            Assert.False(string.IsNullOrWhiteSpace(seen.Error));
            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.True(Server.CanStart);
        }

        // --- a staged answer belongs to the launch it was staged for ----------------------

        [Theory]
        // The start the host pressed, and the restart they pressed: that one relaunches with
        // nobody watching but it is still the launch they answered for, so it keeps the answer.
        [InlineData(LaunchReasons.Manual, true)]
        // Everything BakaLoader decided on by itself has to ask again. Consuming the answer
        // here is how an unattended server comes up on a changed build with no backup.
        [InlineData(LaunchReasons.AutoStart, false)]
        [InlineData(LaunchReasons.Crash, false)]
        [InlineData(LaunchReasons.Empty, false)]
        [InlineData(LaunchReasons.Scheduled, false)]
        [InlineData(LaunchReasons.SelfUpdate, false)]
        public void Only_the_launch_the_host_answered_for_may_use_a_staged_answer(string reason, bool allowed)
        {
            var manual = new LaunchContext { Reason = reason, Automatic = false };
            var unattended = new LaunchContext { Reason = reason, Automatic = true };

            // The flag that matters is what the launch IS, not whether anyone happened to be
            // watching: a host's own restart relaunches as an automatic one.
            Assert.Equal(allowed, manual.MayUseStagedAnswer);
            Assert.Equal(allowed, unattended.MayUseStagedAnswer);
        }

        [Fact]
        public async Task A_start_the_host_pressed_may_use_their_answer()
        {
            var asked = new TaskCompletionSource<LaunchContext>();
            Server.ConfirmLaunchAsync = context =>
            {
                asked.TrySetResult(context);
                return Task.FromResult(LaunchDecision.Hold());
            };

            Server.Start(Options());

            Assert.True((await WithTimeout(asked.Task)).MayUseStagedAnswer);
        }

        [Fact]
        public async Task An_auto_start_may_not_use_an_answer_given_about_something_else()
        {
            var asked = new TaskCompletionSource<LaunchContext>();
            Server.ConfirmLaunchAsync = context =>
            {
                asked.TrySetResult(context);
                return Task.FromResult(LaunchDecision.Hold());
            };

            Server.StartAutomatically(Options());

            Assert.False((await WithTimeout(asked.Task)).MayUseStagedAnswer);
        }

        // --- a restart cycle asks before it tears anything down ---------------------------

        [Fact]
        public async Task A_restart_cycle_asks_the_guard_as_an_automatic_launch_before_stopping()
        {
            LaunchContext seen = null;
            var holdFromNowOn = false;
            Server.ConfirmLaunchAsync = context =>
            {
                seen = context;
                return Task.FromResult(holdFromNowOn
                    ? LaunchDecision.Hold("the build changed")
                    : LaunchDecision.Go());
            };

            var settled = new TaskCompletionSource<LaunchSettledEventArgs>();
            Server.LaunchSettled += (s, e) => settled.TrySetResult(e);

            // Get a server up first: the bug only exists when there is something to stop.
            Server.Start(Options());
            await WaitUntil(() => Server.Status == ServerStatus.Starting);
            seen = null;
            holdFromNowOn = true;

            var cleared = await Server.ConfirmAutomaticRestartAsync(LaunchReasons.Scheduled);

            Assert.False(cleared);
            Assert.NotNull(seen);
            Assert.True(seen.Automatic);
            Assert.Equal(LaunchReasons.Scheduled, seen.Reason);

            // The whole point: a held cycle is skipped with the server still up, rather than
            // stopping first and finding out afterwards that it may not come back.
            Assert.NotEqual(ServerStatus.Stopped, Server.Status);

            var result = await WithTimeout(settled.Task);
            Assert.True(result.Held);
        }

        [Fact]
        public async Task A_cleared_restart_cycle_goes_ahead()
        {
            Server.ConfirmLaunchAsync = _ => Task.FromResult(LaunchDecision.Go());

            Assert.True(await Server.ConfirmAutomaticRestartAsync(LaunchReasons.Empty));
        }

        [Fact]
        public async Task A_restart_cycle_with_no_guard_wired_goes_ahead_as_it_always_did()
        {
            Assert.True(await Server.ConfirmAutomaticRestartAsync(LaunchReasons.Scheduled));
        }

        [Fact]
        public async Task A_restart_cycle_is_skipped_when_the_guard_throws()
        {
            Server.ConfirmLaunchAsync = _ => throw new InvalidOperationException("guard exploded");

            Assert.False(await Server.ConfirmAutomaticRestartAsync(LaunchReasons.Scheduled));
        }

        [Fact]
        public async Task A_cleared_launch_settles_nothing_because_the_status_says_it_all()
        {
            var settled = 0;
            Server.LaunchSettled += (s, e) => Interlocked.Increment(ref settled);
            Server.ConfirmLaunchAsync = _ => Task.FromResult(LaunchDecision.Go());

            Server.Start(Options());

            await WaitUntil(() => Server.Status == ServerStatus.Starting);
            Assert.Equal(0, Volatile.Read(ref settled));
        }

        [Fact]
        public async Task An_automatic_start_is_marked_automatic_so_no_dialog_is_opened()
        {
            var asked = new TaskCompletionSource<LaunchContext>();
            Server.ConfirmLaunchAsync = context =>
            {
                asked.TrySetResult(context);
                return Task.FromResult(LaunchDecision.Hold());
            };

            Server.StartAutomatically(Options());
            var seen = await WithTimeout(asked.Task);

            Assert.True(seen.Automatic);
            Assert.Equal(LaunchReasons.AutoStart, seen.Reason);
        }

        [Fact]
        public async Task The_guard_is_told_what_the_profile_last_ran()
        {
            var asked = new TaskCompletionSource<LaunchContext>();
            Server.ConfirmLaunchAsync = context =>
            {
                asked.TrySetResult(context);
                return Task.FromResult(LaunchDecision.Hold());
            };

            var options = Options();
            options.LastLaunchedServerBuild = "21981590";
            options.LastLaunchedGameVersion = "0.220.5";
            Server.Start(options);

            var seen = await WithTimeout(asked.Task);

            Assert.Equal("21981590", seen.LastLaunchedBuild);
            Assert.Equal("0.220.5", seen.LastLaunchedGameVersion);
            Assert.False(seen.HasWorlds);              // the sandbox save folder is empty
            Assert.NotNull(seen.Options);
        }

        [Fact]
        public async Task The_launch_history_travels_from_the_saved_profile_all_the_way_to_the_guard()
        {
            // Setting the option by hand only proves the second half. The half that was broken
            // was the profile to options hop, so this starts from a preferences object exactly
            // as it comes off disk and follows it into the context the guard is handed.
            var prefs = new ServerPreferences
            {
                ProfileName = "Default",
                Name = "Guard Test Server",
                WorldName = "Guard Test World",
                Password = "hunter2",
                Port = 2456,
                SaveInterval = 30,
                BackupCount = 1,
                BackupIntervalShort = 60,
                BackupIntervalLong = 120,
                ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
                SaveDataFolderPath = Path.Combine(SandboxDir, "saves"),
                LastLaunchedServerBuild = "21981590",
                LastLaunchedGameVersion = "0.220.5",
            };

            var options = ValheimServerOptions.FromPreferences(prefs, new UserPreferences(), null);

            Assert.Equal("21981590", options.LastLaunchedServerBuild);
            Assert.Equal("0.220.5", options.LastLaunchedGameVersion);

            var asked = new TaskCompletionSource<LaunchContext>();
            Server.ConfirmLaunchAsync = context =>
            {
                asked.TrySetResult(context);
                return Task.FromResult(LaunchDecision.Hold());
            };

            Server.Start(options);
            var seen = await WithTimeout(asked.Task);

            Assert.Equal("21981590", seen.LastLaunchedBuild);
            Assert.Equal("0.220.5", seen.LastLaunchedGameVersion);
        }

        [Fact]
        public void A_running_server_records_its_build_and_then_its_version()
        {
            var recorded = new System.Collections.Generic.List<(string Build, string Version)>();
            Server.RecordLaunchedBuild = (build, version) => recorded.Add((build, version));

            Server.Start(Options());
            Server.Logger.Information("Game server connected");

            // Reaching Running records first; the version only exists once the banner lands.
            Assert.Single(recorded);
            Assert.Null(recorded[0].Version);

            Server.Logger.Information("Valheim version: 1.0.7 (network version 39)");

            Assert.Equal(2, recorded.Count);
            Assert.Equal("1.0.7", recorded[1].Version);
            Assert.Equal("1.0.7", Server.GameVersion);
            Assert.Equal("39", Server.NetworkVersion);
        }

        // ------------------------------------------------------------------ plumbing

        private ValheimServerOptions Options() => new()
        {
            Name = "Guard Test Server",
            WorldName = "Guard Test World",
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

        private static async Task<T> WithTimeout<T>(Task<T> task)
        {
            var done = await Task.WhenAny(task, Task.Delay(5000));
            Assert.Same(task, done);
            return await task;
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            for (var i = 0; i < 100; i++)
            {
                if (condition()) return;
                await Task.Delay(50);
            }
            Assert.True(condition(), "the condition never came true");
        }
    }
}
