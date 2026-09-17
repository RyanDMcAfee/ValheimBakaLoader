using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Whether BakaLoader may replace itself right now, asked of the whole application rather
    /// than of one window.
    /// <para>
    /// BakaLoader opens one window per auto-start profile, and each window kept its own session
    /// registry. The self-update check read that registry, so a window whose own profile was
    /// stopped answered "nothing is running" while the window beside it had a world full of
    /// vikings. Nothing refused, the dialog offered "Update and relaunch now", and the update
    /// went: the watchdog is launched the moment staging succeeds, the process never exits
    /// because the other window keeps the app alive, and two minutes later robocopy writes the
    /// new files over a running install and then fails on the locked exe. Half old, half new,
    /// no relaunch, and nobody told.
    /// </para>
    /// </summary>
    public class UpdateGateTests : BaseTest, IDisposable
    {
        private readonly List<ValheimServer> Servers = new();
        private readonly string SandboxDir;

        // A record book of its own. Starting a server runs the companion plugin install pass,
        // and that pass clears the record for the realm it is starting, which walks over what a
        // class reading the shared record had written. See
        // CompanionPluginStatusTests.Every_test_class_that_touches_the_record_keeps_a_book_of_its_own.
        private readonly IDisposable OwnRecords;

        public UpdateGateTests()
        {
            OwnRecords = CompanionPluginStatus.BeginOwnRecords();

            // The same throwaway sandbox the other server tests use: Start()'s path validation
            // is real, the process launch is not (BaseTest swaps in MockProcessProvider).
            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-updgate-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            foreach (var server in Servers)
            {
                try { server.Dispose(); } catch { /* best effort */ }
            }

            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        // ------------------------------------------------------- (a) two windows, one world up

        /// <summary>
        /// The blocking bug, stated as a test. Two windows, one session each; the second window's
        /// profile is stopped and the first window's is up. The old per-window scan is run right
        /// beside the new one so the difference between them is the assertion, not a memory of
        /// what the code used to say.
        /// </summary>
        [Fact]
        public void A_second_windows_stopped_profile_does_not_hide_the_first_windows_live_world()
        {
            var windowA = SessionRunning("Alpha");
            var windowB = SessionStopped("Beta");

            var registry = new ServerSessionRegistry();
            registry.Register(windowA);
            registry.Register(windowB);

            // What window B used to ask: its own sessions, and nothing else. This is the answer
            // that let an update go ahead on top of a live world.
            var perWindowAnswer = new[] { windowB }.Any(s => s.Server.Status != ServerStatus.Stopped);
            Assert.False(perWindowAnswer);

            // What it asks now.
            Assert.True(UpdateGate.AnyServerBusy(registry.All));
        }

        /// <summary>
        /// And the refusal that answer feeds. A host in window B clicking Update must be turned
        /// down, with the reason that is actually true for them.
        /// </summary>
        [Fact]
        public void The_refusal_window_B_gets_names_the_server_running_in_window_A()
        {
            var registry = new ServerSessionRegistry();
            registry.Register(SessionRunning("Alpha"));
            registry.Register(SessionStopped("Beta"));

            Assert.Equal("serverBusy", BlendWindow.SelfUpdateRefusal(
                checkEnabled: true,
                anyServerRunning: UpdateGate.AnyServerBusy(registry.All),
                anyServerUpdateRunning: false));
        }

        /// <summary>
        /// The gate is only worth anything if the RPC actually reads it. The check lives in one
        /// method on the bridge, and this is a source read of that method: a scan of this
        /// window's own Sessions put back there would pass every other test in this file.
        /// </summary>
        [Fact]
        public void The_running_check_is_app_wide_and_not_per_window()
        {
            var body = BridgeMethodSource("private bool AnyServerRunning()");

            Assert.Contains("UpdateGate.AnyServerBusy", body);
            Assert.Contains("AllSessions()", body);
            Assert.DoesNotContain("Sessions.Values", body);

            // Same for the restart path, which had the identical blind spot.
            var restart = BridgeMethodSource("session.Server.CheckForAppUpdateOnRestart = async () =>");
            Assert.Contains("UpdateGate.AnyServerBusy", restart);
            Assert.Contains("AllSessions()", restart);
            Assert.DoesNotContain("Sessions.Values.Any", restart);
        }

        /// <summary>
        /// And one level under that: AllSessions() has to be the app-wide list, and a session has
        /// to reach it. An AllSessions() rewritten to hand back this window's own dictionary would
        /// leave every test above passing while the blind spot was back, because they all build
        /// the registry themselves.
        /// </summary>
        [Fact]
        public void The_app_wide_list_really_is_the_registrys_and_every_session_joins_it()
        {
            var all = BridgeMethodSource("private IReadOnlyCollection<ServerSession> AllSessions()");
            Assert.Contains("SessionRegistry", all);

            // The one place a session is made is the one place it has to be registered.
            var create = BridgeMethodSource("private ServerSession GetOrCreateSession(string profileName)");
            Assert.Contains("new ServerSession(", create);
            Assert.Contains("SessionRegistry?.Register(session)", create);

            // And a window that closes takes its own sessions back out, or the last status a
            // closed window held would pin the update shut for the rest of the process.
            var closed = BridgeMethodSource("protected override void OnFormClosed(FormClosedEventArgs e)");
            Assert.Contains("ForgetSession(session)", closed);
        }

        // ------------------------------------------- the refusal both callers actually work from

        /// <summary>
        /// The whole decision, driven the way the RPC drives it: an application-wide registry
        /// holding one session per window, the gate read over all of it, and the refusal built
        /// from that answer. Two windows, one world up, and the host clicking Update in the quiet
        /// window is the case that used to go ahead.
        /// </summary>
        [Fact]
        public void Two_windows_with_one_world_up_refuse_the_update_the_host_asked_for()
        {
            var registry = new ServerSessionRegistry();
            registry.Register(SessionRunning("Alpha"));   // window A, a world full of vikings
            registry.Register(SessionStopped("Beta"));    // window B, where the host is clicking

            Assert.Equal("serverBusy", BlendWindow.SelfUpdateRefusal(
                checkEnabled: true,
                anyServerRunning: UpdateGate.AnyServerBusy(registry.All),
                anyServerUpdateRunning: false,
                onCooldown: false));
        }

        /// <summary>
        /// Both windows stopped is not the same as both windows finished. A crash with auto
        /// restart on, and a launch the guard has not answered yet, both leave a server reading
        /// Stopped with its relaunch already scheduled, and that server is about to be running
        /// again while the file swap is happening.
        /// </summary>
        [Fact]
        public async Task Two_stopped_windows_with_one_relaunch_pending_refuse_it_too()
        {
            var quiet = SessionStopped("Alpha");

            var server = NewServer();
            var asked = new TaskCompletionSource<bool>();
            var hold = new TaskCompletionSource<LaunchDecision>();
            server.ConfirmLaunchAsync = _ => { asked.TrySetResult(true); return hold.Task; };
            server.Start(Options("Beta"));
            await WithTimeout(asked.Task);

            var registry = new ServerSessionRegistry();
            registry.Register(quiet);
            registry.Register(new ServerSession("Beta", server));

            Assert.Equal(ServerStatus.Stopped, server.Status);
            Assert.True(server.RelaunchPending);

            Assert.Equal("serverBusy", BlendWindow.SelfUpdateRefusal(
                checkEnabled: true,
                anyServerRunning: UpdateGate.AnyServerBusy(registry.All),
                anyServerUpdateRunning: false,
                onCooldown: false));

            hold.TrySetResult(LaunchDecision.Hold("test over"));
        }

        [Fact]
        public void With_both_windows_stopped_and_nothing_pending_the_host_gets_their_update()
        {
            var registry = new ServerSessionRegistry();
            registry.Register(SessionStopped("Alpha"));
            registry.Register(SessionStopped("Beta"));

            Assert.Null(BlendWindow.SelfUpdateRefusal(
                checkEnabled: true,
                anyServerRunning: UpdateGate.AnyServerBusy(registry.All),
                anyServerUpdateRunning: false,
                onCooldown: false));
        }

        // ------------------------------------------------- the same answer, from the restart hook

        /// <summary>
        /// The unattended path asks a slightly different question: the session that is restarting
        /// is about to be down anyway, so it asks whether anything ELSE is live. This is that
        /// question over the application-wide registry, which is the shape the hook uses.
        /// </summary>
        [Fact]
        public void The_restart_hook_sees_the_world_running_in_the_other_window()
        {
            var windowA = SessionRunning("Alpha");
            var windowB = SessionStopped("Beta");

            var registry = new ServerSessionRegistry();
            registry.Register(windowA);
            registry.Register(windowB);

            // Window B's own profile is restarting: its server is stopped, and window A's is not.
            Assert.True(OthersLive(registry, windowB));
        }

        /// <summary>
        /// And the restarting session itself is never the reason it refuses, or a restart with
        /// auto-update on could never install anything at all.
        /// </summary>
        [Fact]
        public void The_restarting_session_does_not_count_itself_as_the_live_one()
        {
            var restarting = SessionRunning("Alpha");
            var quiet = SessionStopped("Beta");

            var registry = new ServerSessionRegistry();
            registry.Register(restarting);
            registry.Register(quiet);

            Assert.False(OthersLive(registry, restarting));
            Assert.True(OthersLive(registry, quiet));
        }

        /// <summary>
        /// The restart hook's own question: is anything other than the session being restarted
        /// still busy? Written the way the hook writes it, over the same registry.
        /// </summary>
        private static bool OthersLive(IServerSessionRegistry registry, ServerSession session)
            => UpdateGate.AnyServerBusy(registry.All.Where(s => !ReferenceEquals(s, session)));

        // ------------------------------------------- (b) stopped, but on its way back up anyway

        /// <summary>
        /// A crash with auto-restart on leaves the status at Stopped with the relaunch already
        /// scheduled, and the launch-hold retry does the same. Reading the status alone sees a
        /// finished server a moment before it starts itself again.
        /// </summary>
        [Theory]
        [InlineData(ServerStatus.Stopped, false, false)]
        [InlineData(ServerStatus.Stopped, true, true)]   // crashed, relaunch scheduled
        [InlineData(ServerStatus.Starting, false, true)]
        [InlineData(ServerStatus.Running, false, true)]
        [InlineData(ServerStatus.Stopping, false, true)]
        public void A_stopped_server_with_a_relaunch_pending_is_still_busy(
            ServerStatus status, bool relaunchPending, bool busy)
        {
            Assert.Equal(busy, UpdateGate.IsBusy(status, relaunchPending));
        }

        /// <summary>
        /// The same thing on a real server rather than on two booleans: a launch the guard has
        /// not answered yet has no status of its own, so the status still reads Stopped while a
        /// server is very much on its way up.
        /// </summary>
        [Fact]
        public async Task A_launch_the_guard_has_not_answered_counts_as_busy()
        {
            var server = NewServer();
            var asked = new TaskCompletionSource<bool>();
            var hold = new TaskCompletionSource<LaunchDecision>();
            server.ConfirmLaunchAsync = _ => { asked.TrySetResult(true); return hold.Task; };

            server.Start(Options("Held"));
            await WithTimeout(asked.Task);

            var session = new ServerSession("Held", server);
            var registry = new ServerSessionRegistry();
            registry.Register(session);

            Assert.Equal(ServerStatus.Stopped, server.Status);
            Assert.True(server.RelaunchPending);
            Assert.True(UpdateGate.AnyServerBusy(registry.All));

            hold.TrySetResult(LaunchDecision.Hold("test over"));
        }

        /// <summary>
        /// And that a restart really does reach the property the gate reads. A server told to
        /// restart has its relaunch pending from that moment, which is the same flag a crash
        /// with auto-restart on sets before the status lands on Stopped.
        /// </summary>
        [Fact]
        public void A_server_told_to_restart_reports_its_relaunch_as_pending()
        {
            var server = NewServer();
            server.Start(Options("Restarter"));
            server.Logger.Information("Game server connected");
            Assert.Equal(ServerStatus.Running, server.Status);
            Assert.False(server.RelaunchPending);

            server.Restart();

            Assert.True(server.RelaunchPending);
            Assert.True(UpdateGate.IsBusy(new ServerSession("Restarter", server)));
        }

        // ------------------------------------------------------- (c) nothing running, nothing due

        [Fact]
        public void With_every_profile_stopped_and_nothing_scheduled_the_update_may_go_ahead()
        {
            var registry = new ServerSessionRegistry();
            registry.Register(SessionStopped("Alpha"));
            registry.Register(SessionStopped("Beta"));

            Assert.False(UpdateGate.AnyServerBusy(registry.All));
            Assert.Null(BlendWindow.SelfUpdateRefusal(
                checkEnabled: true,
                anyServerRunning: UpdateGate.AnyServerBusy(registry.All),
                anyServerUpdateRunning: false));
        }

        /// <summary>
        /// A bridge built with no windows at all sees no sessions, which is not the same as
        /// seeing a running one: an empty registry must not refuse everything forever.
        /// </summary>
        [Fact]
        public void An_empty_registry_is_not_busy()
        {
            Assert.False(UpdateGate.AnyServerBusy(new ServerSessionRegistry().All));
            Assert.False(UpdateGate.AnyServerBusy(null));
        }

        // ------------------------------------------------------------------- the registry itself

        [Fact]
        public void The_registry_holds_each_session_once_and_lets_go_on_unregister()
        {
            var registry = new ServerSessionRegistry();
            var session = SessionStopped("Alpha");

            registry.Register(session);
            registry.Register(session);
            Assert.Single(registry.All);

            registry.Unregister(session);
            Assert.Empty(registry.All);

            // Nulls and strangers are quietly ignored rather than thrown over.
            registry.Register(null);
            registry.Unregister(null);
            registry.Unregister(SessionStopped("Never registered"));
            Assert.Empty(registry.All);
        }

        /// <summary>
        /// Two windows can hold a profile of the same name, and both of their servers are real.
        /// A set keyed on the name would drop one of them, and the one it dropped could be the
        /// one with the world up.
        /// </summary>
        [Fact]
        public void Two_sessions_with_the_same_profile_name_are_two_entries()
        {
            var registry = new ServerSessionRegistry();
            registry.Register(SessionRunning("Final Sunset"));
            registry.Register(SessionStopped("Final Sunset"));

            Assert.Equal(2, registry.All.Count);
            Assert.True(UpdateGate.AnyServerBusy(registry.All));
        }

        [Fact]
        public void The_registry_comes_out_of_the_container_as_one_shared_instance()
        {
            var first = GetService<IServerSessionRegistry>();
            var second = GetService<IServerSessionRegistry>();

            Assert.NotNull(first);
            Assert.Same(first, second);
        }

        // ----------------------------------------------------------------------------- helpers

        private ValheimServer NewServer()
        {
            var server = GetService<ValheimServer>();
            Servers.Add(server);
            return server;
        }

        private ServerSession SessionStopped(string profile) => new(profile, NewServer());

        private ServerSession SessionRunning(string profile)
        {
            var server = NewServer();
            server.ServerKey = profile;
            server.Start(Options(profile));

            // The dedicated server announces itself on stdout; that line is what moves the
            // status to Running, exactly as it does against the real process.
            server.Logger.Information("Game server connected");
            Assert.Equal(ServerStatus.Running, server.Status);

            return new ServerSession(profile, server);
        }

        private ValheimServerOptions Options(string name) => new()
        {
            Name = name,
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
        };

        private static async Task<T> WithTimeout<T>(Task<T> task)
        {
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(task, done);
            return await task;
        }

        /// <summary>
        /// The source of one method on the bridge, from its signature to the blank line that
        /// ends its body. Reading the whole file would let a matching line anywhere else satisfy
        /// the check, which is the opposite of what these guards are for.
        /// </summary>
        private static string BridgeMethodSource(string signature)
        {
            var source = File.ReadAllText(BridgePath());

            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " is gone from BlendWindow.Bridge.cs");

            // Every method body in this file is brace balanced, so the body ends at the brace
            // that closes the one opening it.
            var open = source.IndexOf('{', start);
            Assert.True(open > start, "could not find the body of " + signature);

            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
            }

            Assert.True(false, "could not find the end of " + signature);
            return null;
        }

        private static string BridgePath([CallerFilePath] string thisFile = "")
        {
            // <repo>/ValheimBakaLoader.Tests/Tools/UpdateGateTests.cs
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile), "..", ".."));
            return Path.Combine(repo, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
        }
    }
}
