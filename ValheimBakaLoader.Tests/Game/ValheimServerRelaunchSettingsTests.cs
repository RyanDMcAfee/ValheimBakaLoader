using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// What a relaunch comes back up on. Valheim reads its whole configuration off the command
    /// line at launch, and a relaunch reuses the options the session was started with, so a
    /// setting the host saved while the world was up used to be ignored by restart after
    /// restart: only a Stop and a fresh Start (or closing the app) ever read the profile again.
    /// <para>
    /// These tests drive the real lifecycle - start, the process exit that a restart waits on,
    /// and the relaunch that follows - and read the command line the relaunch actually built,
    /// because that string is the whole of what the game is told.
    /// </para>
    /// </summary>
    public class ValheimServerRelaunchSettingsTests : BaseTest, IDisposable
    {
        private readonly ValheimServer Server;

        // The same throwaway sandbox the other server tests use: path validation is real, the
        // process launch is not (BaseTest swaps in MockProcessProvider).
        private readonly string SandboxDir;

        public ValheimServerRelaunchSettingsTests()
        {
            Server = GetService<ValheimServer>();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-relaunch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Server.Dispose(); } catch { /* best effort */ }
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
        }

        // ----------------------------------------------- the relaunch reads the profile again

        [Fact]
        public async Task A_restart_with_no_options_comes_back_up_on_what_was_saved_on_disk()
        {
            var saved = Options();
            saved.WorldName = "Saved World";
            saved.WorldModifiers = new Dictionary<string, string> { ["combat"] = "hard" };
            Server.RefreshOptions = () => saved;

            var first = await RunningServer();
            Assert.Contains(@"-world ""Relaunch Test World""", first);

            Server.Restart();
            var second = await Relaunch();

            // The command line is the proof: the game is told about the new world and the new
            // difficulty, which is exactly what the old relaunch never did.
            Assert.Contains(@"-world ""Saved World""", second);
            Assert.Contains("-modifier combat hard", second);
            Assert.Equal("Saved World", Server.Options.WorldName);
        }

        [Fact]
        public async Task The_scheduled_and_crash_paths_read_it_too_because_they_share_the_relaunch()
        {
            // Every automatic restart lands in the same resume step, so this drives that step
            // through the reason a scheduled restart carries rather than a second code path.
            var saved = Options();
            saved.WorldName = "Saved World";
            Server.RefreshOptions = () => saved;

            await RunningServer();

            Server.Restart(reason: LaunchReasons.Scheduled);
            var second = await Relaunch();

            Assert.Contains(@"-world ""Saved World""", second);
        }

        [Fact]
        public async Task Options_handed_to_the_restart_still_win_over_what_is_on_disk()
        {
            // A caller that brings its own options is saying which ones to use. The disk read
            // exists for the relaunch that has nothing to go on, not to overrule a caller.
            var saved = Options();
            saved.WorldName = "Saved World";
            Server.RefreshOptions = () => saved;

            await RunningServer();

            var handed = Options();
            handed.WorldName = "Handed World";
            Server.Restart(handed);
            var second = await Relaunch();

            Assert.Contains(@"-world ""Handed World""", second);
        }

        // -------------------------------------------------- a refresh that cannot answer

        [Fact]
        public async Task A_profile_that_cannot_be_read_leaves_the_relaunch_on_what_it_is_running()
        {
            Server.RefreshOptions = () => null;

            await RunningServer();

            Server.Restart();
            var second = await Relaunch();

            Assert.Contains(@"-world ""Relaunch Test World""", second);
            Assert.Equal("Relaunch Test World", Server.Options.WorldName);
        }

        [Fact]
        public async Task A_refresh_that_throws_never_takes_the_relaunch_down_with_it()
        {
            Server.RefreshOptions = () => throw new InvalidOperationException("the settings file is open elsewhere");

            await RunningServer();

            Server.Restart();
            var second = await Relaunch();

            // The server came back, on the settings it was already running.
            Assert.Equal(ServerStatus.Starting, Server.Status);
            Assert.Contains(@"-world ""Relaunch Test World""", second);
        }

        [Fact]
        public async Task With_no_hook_wired_a_restart_behaves_exactly_as_before()
        {
            await RunningServer();

            Server.Restart();
            var second = await Relaunch();

            Assert.Contains(@"-world ""Relaunch Test World""", second);
        }

        // ------------------------------------------------------- the difference rule itself

        [Fact]
        public void Two_identical_sets_of_settings_are_no_difference_at_all()
        {
            Assert.False(ValheimServerOptions.RelaunchWouldDiffer(Options(), Options()));
        }

        [Fact]
        public void A_changed_world_modifier_is_a_difference()
        {
            var running = Options();
            running.WorldModifiers = new Dictionary<string, string> { ["combat"] = "hard" };

            var saved = Options();
            saved.WorldModifiers = new Dictionary<string, string> { ["combat"] = "veryhard" };

            Assert.True(ValheimServerOptions.RelaunchWouldDiffer(running, saved));
        }

        [Fact]
        public void The_same_modifiers_in_another_order_are_not_a_difference()
        {
            // A dictionary hands its entries over in insertion order, and the host choosing the
            // same difficulty by a different route must never raise a restart they do not need.
            var running = Options();
            running.WorldModifiers = new Dictionary<string, string>
            {
                ["combat"] = "hard",
                ["deathpenalty"] = "casual",
                ["portals"] = "veryhard",
            };

            var saved = Options();
            saved.WorldModifiers = new Dictionary<string, string>
            {
                ["portals"] = "veryhard",
                ["combat"] = "hard",
                ["deathpenalty"] = "casual",
            };

            Assert.False(ValheimServerOptions.RelaunchWouldDiffer(running, saved));
        }

        [Fact]
        public void A_changed_port_is_a_difference()
        {
            var saved = Options();
            saved.Port = 2466;

            Assert.True(ValheimServerOptions.RelaunchWouldDiffer(Options(), saved));
        }

        [Fact]
        public void Nothing_to_compare_is_never_called_a_difference()
        {
            // server.state asks this on every refresh, so it has to be quiet about anything it
            // cannot answer rather than asking the host to restart for nothing.
            Assert.False(ValheimServerOptions.RelaunchWouldDiffer(null, Options()));
            Assert.False(ValheimServerOptions.RelaunchWouldDiffer(Options(), null));

            var same = Options();
            Assert.False(ValheimServerOptions.RelaunchWouldDiffer(same, same));
        }

        [Fact]
        public void A_save_folder_that_is_not_there_falls_back_to_the_fields()
        {
            // The command line cannot be built when a path will not validate, and a host whose
            // drive is unplugged still deserves a true answer about their own settings.
            var running = Options();
            running.SaveDataFolderPath = Path.Combine(SandboxDir, "gone");

            var saved = Options();
            saved.SaveDataFolderPath = Path.Combine(SandboxDir, "gone");
            saved.WorldName = "Saved World";

            Assert.True(ValheimServerOptions.RelaunchWouldDiffer(running, saved));
            Assert.False(ValheimServerOptions.RelaunchWouldDiffer(running, running));
        }

        [Fact]
        public void The_fingerprint_follows_the_settings_and_never_carries_the_password()
        {
            var running = Options();
            var changed = Options();
            changed.WorldModifiers = new Dictionary<string, string> { ["combat"] = "hard" };

            var before = ValheimServerOptions.RelaunchSignature(running);
            var after = ValheimServerOptions.RelaunchSignature(changed);

            Assert.False(string.IsNullOrWhiteSpace(before));
            Assert.NotEqual(before, after);
            Assert.Equal(before, ValheimServerOptions.RelaunchSignature(Options()));
            Assert.DoesNotContain("hunter2", before);
            Assert.Null(ValheimServerOptions.RelaunchSignature(null));
        }

        // ------------------------------------------------------------------ the wiring itself

        [Fact]
        public void Both_relaunch_paths_ask_for_the_saved_settings()
        {
            // The gate is on the source because what is guarded against is a relaunch path
            // somebody adds (or quietly moves) later, not a value this run produces.
            var restart = SourceBetween(
                "public void Restart(IValheimServerOptions options = null, string reason = null)",
                "private void BeginShutdown(bool restartAfter)");
            var resume = SourceBetween(
                "private async Task ResumeAfterStopAsync()",
                "#endregion");

            Assert.Contains("RefreshOptionsForRelaunch", restart);
            Assert.Contains("RefreshOptionsForRelaunch", resume);
        }

        // ------------------------------------------------------------------------- plumbing

        /// <summary>
        /// Starts the server and takes it to Running the way the game does, through the log
        /// line the status rule watches for. Hands back the command line it launched with.
        /// </summary>
        private async Task<string> RunningServer()
        {
            Server.Start(Options());
            await WaitUntil(() => Server.GetTrackedProcess() != null);

            var args = Server.GetTrackedProcess().StartInfo.Arguments;
            Server.Logger.Information("Game server connected");
            await WaitUntil(() => Server.Status == ServerStatus.Running);
            return args;
        }

        /// <summary>
        /// Finishes the restart that is in flight: the old process exits (nothing kills it in a
        /// test, so the exit is raised here), the resume step runs, and the command line the
        /// relaunch built comes back.
        /// </summary>
        private async Task<string> Relaunch()
        {
            await WaitUntil(() => Server.Status == ServerStatus.Stopping);

            var old = Server.GetTrackedProcess();
            Assert.NotNull(old);
            RaiseExited(old);

            await WaitUntil(() => Server.Status == ServerStatus.Starting, 8000);
            var process = Server.GetTrackedProcess();
            Assert.NotNull(process);
            return process.StartInfo.Arguments;
        }

        /// <summary>
        /// Fires a process's Exited event without a process ever having run. The mock provider
        /// never starts (or kills) anything, so this is the only way to drive the exit the
        /// restart machinery waits on.
        /// </summary>
        private static void RaiseExited(Process process)
        {
            var onExited = typeof(Process).GetMethod(
                "OnExited", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(onExited);
            onExited.Invoke(process, null);
        }

        private ValheimServerOptions Options() => new()
        {
            Name = "Relaunch Test Server",
            WorldName = "Relaunch Test World",
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

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 8000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(condition(), "The condition never came true.");
        }

        /// <summary>The slice of ValheimServer.cs between two declarations, both included.</summary>
        private static string SourceBetween(string from, string to, [CallerFilePath] string thisFile = "")
        {
            // <repo>/ValheimBakaLoader.Tests/Game/ValheimServerRelaunchSettingsTests.cs
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile), "..", ".."));
            var source = File.ReadAllText(Path.Combine(repo, "ValheimBakaLoader", "Game", "ValheimServer.cs"));

            var start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, from + " is gone from ValheimServer.cs");

            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            Assert.True(end > start, "could not find " + to + " after " + from);

            return source[start..end];
        }
    }
}
