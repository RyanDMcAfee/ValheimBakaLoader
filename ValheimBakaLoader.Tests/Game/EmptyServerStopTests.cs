using BakaLoaderSave;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Data;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Processes;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// Stop when empty (issue 28): the server stops, with a world save, once it has sat empty for
    /// the realm's delay, and when the host asks for it Windows shuts down after the stop.
    /// <para>
    /// These drive the real server. The process is the mock provider's, which writes a close
    /// request down instead of killing anything; the players come and go through the same log
    /// lines the game prints; the timers wait on gates this class opens, so a test says which
    /// timer reaches the end of its wait first instead of sitting through minutes of it; and the
    /// power switch is a fake that writes its calls down. Nothing here starts a server, and
    /// nothing here can reach the machine's own power switch.
    /// </para>
    /// <para>
    /// The decision itself is EmptyServerPlan's and is asked every combination in
    /// EmptyServerPlanTests. What is held here is what the server does with the answer: the
    /// order of the stop and the update check, what a join or a host's own Stop does to the
    /// wait, and that Windows is asked exactly once and only after the stop has landed.
    /// </para>
    /// </summary>
    [Collection(WallClockCollection.Name)]
    public class EmptyServerStopTests : IDisposable
    {
        private const string JoinLine = "Got connection SteamID {0}";
        private const string SpawnLine = "Got character ZDOID from {0} : {1}:1";
        private const string DisconnectLine = "Closing socket {0}";
        private const string SaveLine = "World save (5/5) done. Total time [1,234ms]";

        /// <summary>The sentence Windows shows, written out here rather than read off the real class.</summary>
        private const string Comment =
            "BakaLoader: the Valheim server stopped after sitting empty. Windows shuts down in one minute; run shutdown /a to cancel.";

        private static readonly TimeSpan StopWait = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan UpdateWait = TimeSpan.FromMinutes(10);

        private readonly ServiceProvider Services;
        private readonly ValheimServer Server;
        private readonly MockProcessProvider Processes = new();
        private readonly RecordingRcon Rcon = new();
        private readonly FakePowerOff Power = new();
        private readonly Gates Waits = new();
        private readonly ConcurrentQueue<string> Lines = new();
        private readonly string SandboxDir;
        private readonly IDisposable OwnRecords;
        private readonly TimeSpan OriginalDrain = ValheimServer.ExitLineDrain;
        private int Crashes;

        public EmptyServerStopTests()
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
            Server.EmptyWait = Waits.Wait;
            Server.PowerOff = Power;
            Server.ServerCrashed += (_, _) => Interlocked.Increment(ref Crashes);
            Services.GetRequiredService<IApplicationLogger>().LogReceived += line => Lines.Enqueue(line ?? "");

            ValheimServer.ExitLineDrain = TimeSpan.FromMilliseconds(100);

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-emptystop-" + Guid.NewGuid().ToString("N"));
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

        // ------------------------------------------------------------------ the setting (build 1, k)

        [Fact]
        public void A_profile_from_1_3_0_reads_both_switches_off_and_saves_them_as_false()
        {
            var older = JsonConvert.DeserializeObject<ServerPreferencesFile>(
                "{\"profileName\":\"Old\",\"emptyServerRestart\":true,\"emptyServerRestartDelayMinutes\":9}");
            var prefs = ServerPreferences.FromFile(older);

            Assert.False(prefs.EmptyServerStop);
            Assert.False(prefs.EmptyServerStopShutsDownWindows);
            Assert.True(prefs.EmptyServerRestart);
            Assert.Equal(9, prefs.EmptyServerRestartDelayMinutes);

            // Written the way the store writes it: both keys present and false, never null and
            // never missing, so the next reader does not have to fall back to a default.
            var written = JObject.Parse(JsonConvert.SerializeObject(prefs.ToFile(), Formatting.Indented));
            Assert.Equal(JTokenType.Boolean, written["emptyServerStop"]?.Type);
            Assert.Equal(JTokenType.Boolean, written["emptyServerStopShutsDownWindows"]?.Type);
            Assert.False(written.Value<bool>("emptyServerStop"));
            Assert.False(written.Value<bool>("emptyServerStopShutsDownWindows"));

            // and the defaults of a brand new realm are off too
            Assert.False(new ServerPreferences().EmptyServerStop);
            Assert.False(new ServerPreferences().EmptyServerStopShutsDownWindows);
        }

        [Fact]
        public void Both_switches_go_to_disk_and_back_and_reach_the_server_options()
        {
            var store = Services.GetRequiredService<IServerPreferencesProvider>();
            store.SavePreferences(new ServerPreferences
            {
                ProfileName = "Round Trip",
                EmptyServerStop = true,
                EmptyServerStopShutsDownWindows = true,
                EmptyServerRestartDelayMinutes = 12,
            });

            var back = store.LoadPreferences("Round Trip");
            Assert.True(back.EmptyServerStop);
            Assert.True(back.EmptyServerStopShutsDownWindows);
            Assert.Equal(12, back.EmptyServerRestartDelayMinutes);

            var options = ValheimServerOptions.FromPreferences(back, new UserPreferences(), null);
            Assert.True(options.EmptyServerStop);
            Assert.True(options.EmptyServerStopShutsDownWindows);
            IValheimServerOptions seen = options;
            Assert.True(seen.EmptyServerStop);
            Assert.True(seen.EmptyServerStopShutsDownWindows);
        }

        [Fact]
        public void The_page_reads_both_keys_and_its_save_lands_them()
        {
            // profiles.get hands the page the profile as a JObject, and profiles.save reads the
            // page's object back by property name. Both keys have to survive both directions.
            var dto = BlendWindow.BuildProfilePrefsDto(
                new ServerPreferences { ProfileName = "Dto", EmptyServerStop = true }, new UserPreferences());
            Assert.True(dto.Value<bool>("EmptyServerStop"));
            Assert.False(dto.Value<bool>("EmptyServerStopShutsDownWindows"));

            dto["EmptyServerStopShutsDownWindows"] = true;
            var saved = dto.ToObject<ServerPreferences>();
            Assert.True(saved.EmptyServerStop);
            Assert.True(saved.EmptyServerStopShutsDownWindows);
        }

        // ------------------------------------------------------------------ the timer (build 2, 3, 5)

        /// <summary>
        /// (a) Both switches on, straight out of a profile file: the stop runs, the restart does
        /// not, and the log says once that the restart switch was set aside.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task Both_switches_on_in_the_file_the_stop_runs_the_restart_does_not_and_one_line_says_so()
        {
            var file = JsonConvert.DeserializeObject<ServerPreferencesFile>(
                "{\"profileName\":\"Both\",\"emptyServerRestart\":true,\"emptyServerStop\":true,\"emptyServerRestartDelayMinutes\":3}");
            var prefs = ServerPreferences.FromFile(file);
            prefs.Name = "Empty Stop Server";
            prefs.WorldName = "Empty Stop World";
            prefs.Password = "hunter2";
            prefs.ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe");
            prefs.SaveDataFolderPath = Path.Combine(SandboxDir, "saves");
            var options = ValheimServerOptions.FromPreferences(prefs, new UserPreferences(), null);

            var process = await RunningServer(options);
            JoinAndLeave("4242", "Bjorn");

            await WaitUntil(() => Waits.Count(StopWait) == 1);
            Waits.Open(StopWait);
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            RaiseExited(process);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            await Task.Delay(300);
            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.False(Server.RelaunchPending);
            Assert.Null(Server.GetTrackedProcess());

            Assert.Single(Lines, l => l.Contains("the stop wins and the restart switch is ignored"));
            Assert.Single(Lines, l => l.Contains("Server is empty; stopping in 3 minute(s)."));
            Assert.Single(Lines, l => l.Contains("Server still empty; stopping now."));
            Assert.DoesNotContain(Lines, l => l.Contains("scheduling restart"));
            Assert.DoesNotContain(Lines, l => l.Contains("restarting now"));
            Assert.Empty(Power.Calls);
        }

        /// <summary>
        /// (b) A join before the wait is out cancels it: nothing fires and Windows is not asked.
        /// The next time the server empties, the wait starts again from the beginning.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_join_before_the_wait_is_out_cancels_the_stop_and_the_next_leave_starts_a_fresh_one()
        {
            var process = await RunningServer(Options(stop: true, windows: true));

            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1);

            // minute N-1: somebody comes back
            Feed(JoinLine, "4242");
            Feed(SpawnLine, "Bjorn", "1");
            await WaitUntil(() => Waits.Cancelled(StopWait) == 1);
            Assert.Single(Lines, l => l.Contains("A player joined; the empty-server stop is cancelled."));

            // The old gate opening now is the minute running out on a wait that was cancelled.
            Waits.Open(StopWait);
            await Task.Delay(300);
            Assert.Empty(Processes.KillRequests);
            Assert.Equal(ServerStatus.Running, Server.Status);
            Assert.Empty(Power.Calls);

            // they leave again: a fresh wait, from the start
            Feed(DisconnectLine, "4242");
            await WaitUntil(() => Waits.Count(StopWait) == 2);
            Waits.Open(StopWait);
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            RaiseExited(process);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.Single(Power.Calls);
        }

        /// <summary>
        /// (c) The host stops the server by hand during the wait. The wait is cancelled on the
        /// way down, so it never fires, and Windows owes the host nothing: the stop that landed
        /// was theirs, not the timer's.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_stop_by_hand_during_the_wait_cancels_it_and_never_shuts_windows_down()
        {
            var process = await RunningServer(Options(stop: true, windows: true));
            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1);

            Server.Stop();
            Assert.Equal(1, Waits.Cancelled(StopWait));
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            RaiseExited(process);
            Assert.Equal(ServerStatus.Stopped, Server.Status);

            Waits.Open(StopWait);
            await Task.Delay(300);

            Assert.Empty(Power.Calls);
            Assert.Single(Processes.KillRequests);
            Assert.DoesNotContain(Lines, l => l.Contains("stopping now"));
        }

        /// <summary>
        /// (d) The empty update check has found updates and is a moment from restarting when the
        /// stop's wait runs out. The stop cancels the check first, so there is one stop and no
        /// restart, and the check's answer arriving afterwards changes nothing.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task The_stop_cancels_an_update_check_that_is_about_to_restart_one_stop_no_restart()
        {
            var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Server.GetPendingModUpdateCount = () => { asked.TrySetResult(true); return answer.Task; };

            var process = await RunningServer(Options(stop: true));
            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1 && Waits.Count(UpdateWait) == 1);

            // The check's own wait is out and it is asking how many updates there are.
            Waits.Open(UpdateWait);
            await asked.Task;

            // The stop's wait runs out while it asks.
            Waits.Open(StopWait);
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            Assert.Equal(ServerStatus.Stopping, Server.Status);
            Assert.True(Waits.Cancelled(UpdateWait) >= 1, "the update check was not cancelled by the stop");

            // and now the answer: two updates
            answer.SetResult(2);
            await Task.Delay(300);
            RaiseExited(process);
            await Task.Delay(300);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.False(Server.RelaunchPending);
            Assert.Single(Processes.KillRequests);
            Assert.DoesNotContain(Lines, l => l.Contains("restarting to auto-update"));
            Assert.Single(Lines, l => l.Contains("Server still empty; stopping now."));
        }

        /// <summary>
        /// (d, the other order) The update check finds updates while the stop is still waiting.
        /// Restarting now would bring the server back up with nobody on it and no leave to start
        /// the stop again, so it would run empty all night: the check stands aside for the stop.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task An_update_check_that_finds_updates_while_a_stop_is_due_leaves_the_server_to_the_stop()
        {
            Server.GetPendingModUpdateCount = () => Task.FromResult(3);

            var process = await RunningServer(Options(stop: true));
            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1 && Waits.Count(UpdateWait) == 1);

            Waits.Open(UpdateWait);
            await WaitUntil(() => Lines.Any(l => l.Contains("the server is due to stop while empty, so it is not restarted for them")));
            Assert.Equal(ServerStatus.Running, Server.Status);
            Assert.Empty(Processes.KillRequests);

            Waits.Open(StopWait);
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            RaiseExited(process);
            await Task.Delay(300);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.False(Server.RelaunchPending);
            Assert.DoesNotContain(Lines, l => l.Contains("restarting to auto-update"));
        }

        /// <summary>
        /// (e) Restart on Crash is on, and the process the stop closed exits with a code that is
        /// not zero, which is what a closed process often does. The stop went out as a stop, so
        /// the status was Stopping when the exit arrived: it is not a crash and nothing relaunches.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task Restart_on_crash_does_not_relaunch_a_server_the_empty_timer_stopped()
        {
            var options = Options(stop: true);
            options.AutoRestart = true;
            options.AutoRestartDelay = 0;
            await RunningServer(options);

            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1);
            Waits.Open(StopWait);
            await WaitUntil(() => Processes.KillRequests.Count == 1);

            // The exit the process's own Exited handler would hand on, with a code of 1.
            var exit = typeof(ValheimServer).GetMethod("HandleProcessExit", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(exit);
            exit.Invoke(Server, new object[] { 1, true });

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.Equal(0, Crashes);
            Assert.False(Server.RelaunchPending);
            await Task.Delay(400);
            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.Null(Server.GetTrackedProcess());
            Assert.DoesNotContain(Lines, l => l.Contains("Auto-restarting server"));
        }

        /// <summary>
        /// (f) The Windows switch is on and the stop never lands: the close request went out and
        /// the process never exited. Windows is only ever asked once the server is Stopped, so a
        /// stop that hangs leaves the machine on.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_stop_that_never_lands_never_shuts_windows_down()
        {
            await RunningServer(Options(stop: true, windows: true));
            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1);
            Waits.Open(StopWait);

            await WaitUntil(() => Processes.KillRequests.Count == 1);
            await Task.Delay(500);

            Assert.Equal(ServerStatus.Stopping, Server.Status);
            Assert.Empty(Power.Calls);
        }

        /// <summary>
        /// (g) The Windows switch on with the stop switch off is no ask at all: no wait starts,
        /// and a stop by hand afterwards does not shut anything down either.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task The_windows_switch_without_the_stop_switch_never_shuts_windows_down()
        {
            var process = await RunningServer(Options(stop: false, windows: true));
            JoinAndLeave("4242", "Bjorn");
            await Task.Delay(300);

            Assert.Equal(0, Waits.Count(StopWait));
            Assert.DoesNotContain(Lines, l => l.Contains("stopping in"));

            Server.Stop();
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            RaiseExited(process);
            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.Empty(Power.Calls);
        }

        /// <summary>
        /// (h) One stop asks Windows exactly once, for a minute, with the plain sentence, and
        /// only after the server has landed on Stopped. A second exit report changes nothing.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task One_stop_asks_windows_once_for_a_minute_with_the_plain_sentence()
        {
            var process = await RunningServer(Options(stop: true, windows: true));
            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1);
            Assert.Single(Lines, l => l.Contains("Server is empty; stopping in 3 minute(s), then Windows shuts down."));

            Waits.Open(StopWait);
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            Assert.Empty(Power.Calls);   // not while it is still going down

            RaiseExited(process);
            Assert.Equal(ServerStatus.Stopped, Server.Status);
            RaiseExited(process);

            var call = Assert.Single(Power.Calls);
            Assert.Equal(60, call.Seconds);
            Assert.Equal(Comment, call.Comment);
            Assert.DoesNotContain('-', call.Comment);
            Assert.DoesNotContain((char)0x2013, call.Comment);   // en dash
            Assert.DoesNotContain((char)0x2014, call.Comment);   // em dash
            Assert.True(call.Comment.All(c => c >= 32 && c < 127), "the sentence is not plain ASCII");
            Assert.True(call.Comment.Length <= 512);
            Assert.Equal(0, Power.Cancels);

            Assert.Single(Lines, l => l.Contains("Windows shuts down in 60 seconds. Run shutdown /a in a command prompt to cancel."));
        }

        /// <summary>
        /// Issue 31, scenario 12. A 1.3.1 stop when empty with the Windows switch off lands on
        /// Stopped as a plain stop: no relaunch pending and no power-off due, so the queued
        /// BepInEx write may run there. Read inside a Stopped handler, which is where the
        /// bridge reads it, after the server's own handler has run.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_stop_when_empty_without_the_power_off_is_a_stop_the_queue_runs_at()
        {
            var seen = new ConcurrentQueue<(bool Relaunch, bool PowerOff)>();
            Server.StatusChanged += (_, status) =>
            {
                if (status == ServerStatus.Stopped)
                    seen.Enqueue((Server.RelaunchPending, Server.StoppedWithWindowsPowerOffDue));
            };

            var process = await RunningServer(Options(stop: true));
            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1);
            Waits.Open(StopWait);
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            RaiseExited(process);

            var at = Assert.Single(seen);
            Assert.False(at.Relaunch);
            Assert.False(at.PowerOff);
            Assert.True(BepInExQueue.FiresAtStop(at.Relaunch, at.PowerOff));
            Assert.Empty(Power.Calls);
        }

        /// <summary>
        /// Issue 31, scenario 12. With the Windows power-off due the same stop is one the queue
        /// waits past: a minute from now Windows goes down, and a write cut there leaves an
        /// install that loads neither version. The flag is still true in the Stopped handler,
        /// although the power-off itself has already taken its own flag, and the next start
        /// clears it.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task With_the_power_off_due_the_stop_leaves_the_queue_for_the_next_start()
        {
            var seen = new ConcurrentQueue<bool>();
            Server.StatusChanged += (_, status) =>
            {
                if (status == ServerStatus.Stopped) seen.Enqueue(Server.StoppedWithWindowsPowerOffDue);
            };

            var process = await RunningServer(Options(stop: true, windows: true));
            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1);
            Waits.Open(StopWait);
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            RaiseExited(process);

            Assert.True(Assert.Single(seen));
            Assert.False(BepInExQueue.FiresAtStop(Server.RelaunchPending, Server.StoppedWithWindowsPowerOffDue));
            Assert.Single(Power.Calls);

            await RunningServer(Options(stop: true, windows: true));
            Assert.False(Server.StoppedWithWindowsPowerOffDue);
        }

        /// <summary>
        /// With RCON on, the stop takes the road every stop takes: the save is asked for first,
        /// the close request waits for the save line, and the exit carries no unsaved warning.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task With_rcon_on_the_stop_asks_for_the_save_before_the_close()
        {
            var process = await RunningServer(Options(stop: true, rcon: true));
            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1);
            Waits.Open(StopWait);

            await WaitUntil(() => Rcon.Sent.Contains(SavePlan.Verb));
            await Task.Delay(200);
            Assert.Empty(Processes.KillRequests);
            Assert.Equal(ServerStatus.Stopping, Server.Status);

            Feed(SaveLine);
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            RaiseExited(process);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.Null(Server.UnsavedStop);
        }

        /// <summary>
        /// The switches are asked again when the wait is out: a host who turns the Windows half
        /// off during the wait gets the stop and keeps the machine, and one who turns the stop
        /// off keeps the server.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_switch_turned_off_during_the_wait_is_off_when_the_wait_ends()
        {
            var saved = new EmptyServerSwitches(Restart: false, Stop: true, StopShutsDownWindows: true, DelayMinutes: 3);
            Server.ReadEmptyServerSwitches = () => saved;

            var process = await RunningServer(Options(stop: false));   // started with the switches off
            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1);         // saved while it ran, read live

            saved = saved with { Stop = false };
            Waits.Open(StopWait);
            await WaitUntil(() => Lines.Any(l => l.Contains("Stop when empty was turned off during the wait")));
            Assert.Equal(ServerStatus.Running, Server.Status);
            Assert.Empty(Processes.KillRequests);

            saved = saved with { Stop = true, StopShutsDownWindows = false };
            Feed(JoinLine, "5151");
            Feed(SpawnLine, "Freya", "2");
            Feed(DisconnectLine, "5151");
            await WaitUntil(() => Waits.Count(StopWait) == 2);

            // The wait that ended without a stop let go of itself: the join above found no stop
            // due, so it had nothing to cancel and said nothing.
            Assert.DoesNotContain(Lines, l => l.Contains("A player joined; the empty-server stop is cancelled."));

            Waits.Open(StopWait);
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            RaiseExited(process);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.Empty(Power.Calls);
        }

        /// <summary>
        /// A power switch that refuses (shutdown.exe answered with an error) is logged, and the
        /// stop itself has still landed: nothing about the refusal reaches back into the server.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_refused_power_off_is_logged_and_the_server_stays_stopped()
        {
            Power.Refuse = true;
            var process = await RunningServer(Options(stop: true, windows: true));
            JoinAndLeave("4242", "Bjorn");
            await WaitUntil(() => Waits.Count(StopWait) == 1);
            Waits.Open(StopWait);
            await WaitUntil(() => Processes.KillRequests.Count == 1);
            RaiseExited(process);

            Assert.Equal(ServerStatus.Stopped, Server.Status);
            Assert.Single(Power.Calls);
            Assert.Contains(Lines, l => l.Contains("Windows did not accept the shutdown, so it stays on."));
            Assert.DoesNotContain(Lines, l => l.Contains("Windows shuts down in 60 seconds"));
        }

        // ------------------------------------------------------------------ the wiring

        [Fact]
        public void Every_session_is_handed_its_switches_and_the_power_switch()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

            var start = bridge.IndexOf("private ServerSession GetOrCreateSession(", StringComparison.Ordinal);
            var end = bridge.IndexOf("private IReadOnlyCollection<ServerSession> AllSessions()", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start);
            Assert.Contains("WireEmptyStopHooks(session)", bridge[start..end]);

            var wiring = bridge.IndexOf("private void WireEmptyStopHooks(", StringComparison.Ordinal);
            Assert.True(wiring >= 0, "WireEmptyStopHooks is gone from the bridge");
            var wiringEnd = bridge.IndexOf("\n        }\n", wiring, StringComparison.Ordinal);
            var body = bridge[wiring..wiringEnd];

            // The session's own profile, read each time, and all four values from it.
            Assert.Contains("ServerPrefsProvider.LoadPreferences(profile)", body);
            Assert.DoesNotContain("ActiveProfileName", body);
            Assert.Contains("prefs.EmptyServerRestart, prefs.EmptyServerStop", body);
            Assert.Contains("prefs.EmptyServerStopShutsDownWindows, prefs.EmptyServerRestartDelayMinutes", body);
            Assert.Contains("session.Server.PowerOff = WindowsPower;", body);
        }

        // ------------------------------------------------------------------ plumbing

        private ValheimServerOptions Options(bool stop, bool windows = false, bool rcon = false) => new()
        {
            Name = "Empty Stop Server",
            WorldName = "Empty Stop World",
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
            EmptyServerStop = stop,
            EmptyServerStopShutsDownWindows = windows,
            EmptyServerRestartDelayMinutes = 3,
            ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
            SaveDataFolderPath = Path.Combine(SandboxDir, "saves"),
        };

        private async Task<Process> RunningServer(ValheimServerOptions options)
        {
            Server.Start(options);
            await WaitUntil(() => Server.GetTrackedProcess() != null);
            Feed("Game server connected");
            await WaitUntil(() => Server.Status == ServerStatus.Running);
            return Server.GetTrackedProcess();
        }

        private void JoinAndLeave(string steamId, string name)
        {
            Feed(JoinLine, steamId);
            Feed(SpawnLine, name, "1");
            Feed(DisconnectLine, steamId);
        }

        private void Feed(string template, params object[] args)
            => Server.Logger.Information(args.Length == 0 ? template : string.Format(template, args));

        private static void RaiseExited(Process process)
        {
            var onExited = typeof(Process).GetMethod("OnExited", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(onExited);
            onExited.Invoke(process, null);
        }

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 20000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(condition(), "The condition never came true.");
        }

        /// <summary>
        /// Every wait a timer asks for, held until the test opens it. A wait whose token is
        /// cancelled ends the way Task.Delay ends, with the cancellation, and is counted.
        /// </summary>
        private sealed class Gates
        {
            private readonly object Lock = new();
            private readonly List<(TimeSpan Wait, TaskCompletionSource<bool> Done, CancellationToken Token)> Asked = new();

            public Task Wait(TimeSpan wait, CancellationToken token)
            {
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (Lock) Asked.Add((wait, done, token));
                return done.Task.WaitAsync(token);
            }

            public int Count(TimeSpan wait)
            {
                lock (Lock) return Asked.Count(a => a.Wait == wait);
            }

            public int Cancelled(TimeSpan wait)
            {
                lock (Lock) return Asked.Count(a => a.Wait == wait && a.Token.IsCancellationRequested);
            }

            public void Open(TimeSpan wait)
            {
                lock (Lock)
                {
                    foreach (var a in Asked.Where(a => a.Wait == wait)) a.Done.TrySetResult(true);
                }
            }
        }

        /// <summary>Writes down every call; never reaches the machine.</summary>
        private sealed class FakePowerOff : IWindowsPowerOff
        {
            public readonly ConcurrentQueue<(int Seconds, string Comment)> Calls = new();
            public int Cancels;
            public bool Refuse;

            public void Schedule(int seconds, string comment)
            {
                Calls.Enqueue((seconds, comment));
                if (Refuse) throw new InvalidOperationException("shutdown.exe answered 1190");
            }

            public void Cancel() => Interlocked.Increment(ref Cancels);
        }

        /// <summary>Answers every command the way the bundled RCON plugin answers baka_save, and writes it down.</summary>
        private sealed class RecordingRcon : IRconClient
        {
            public readonly ConcurrentQueue<string> Sent = new();

            public bool IsConnected { get; private set; }

            public Task<bool> ConnectAsync(string host, int port, string password)
            {
                IsConnected = true;
                return Task.FromResult(true);
            }

            public Task<string> SendCommandAsync(string command)
            {
                Sent.Enqueue(command ?? "");
                return Task.FromResult(SavePlan.StartedReply);
            }

            public void Disconnect() => IsConnected = false;
        }
    }
}
