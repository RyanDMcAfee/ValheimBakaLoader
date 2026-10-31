using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The BepInEx queue (issue 31): a host's yes to a write that cannot run while a server on
    /// the install is up, kept for the next moment every one of them is down, and Stop, install
    /// and start again, which makes that moment now.
    /// <para>
    /// The rules are driven here against a small stand-in world: realms that are up or down on
    /// one base install, a preference that is the queue on disk, and a write that counts itself.
    /// Who counts as up is the real <see cref="BepInExService.ServersBlockingWrite"/>, so the
    /// names a refusal carries are the names the host reads. Nothing here starts a server or
    /// touches a real install; the paths are under the temp folder and never written to.
    /// </para>
    /// </summary>
    public class BepInExQueueTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "bakaloader-bxqueue");
        private static readonly string BaseExe = Path.Combine(Root, "common", "Valheim dedicated server", "valheim_server.exe");
        private static readonly string IsolatedExe = Path.Combine(Root, "common", ".bakaloader-instances", "Isolated", "valheim_server.exe");

        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"].Replace("\r\n", "\n");

        private static string Server() => AppSourceTree.Files()["ValheimServer.cs"].Replace("\r\n", "\n");

        /// <summary>The body of a method in a source file, from its signature to the next member at its indent.</summary>
        private static string Body(string source, string signature)
        {
            var from = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(from >= 0, "the source no longer holds " + signature);
            var to = source.IndexOf("\n        }\n", from, StringComparison.Ordinal);
            Assert.True(to > from, signature + " no longer closes at its indent");
            return source.Substring(from, to - from);
        }

        /// <summary>The body of an RPC registration, from its name to the next registration.</summary>
        private static string Rpc(string name)
        {
            var bridge = Bridge();
            var from = bridge.IndexOf("RegisterRpc(\"" + name + "\"", StringComparison.Ordinal);
            Assert.True(from >= 0, "the bridge no longer registers " + name);
            var to = bridge.IndexOf("RegisterRpc(\"", from + 12, StringComparison.Ordinal);
            return bridge.Substring(from, (to > from ? to : bridge.Length) - from);
        }

        // ------------------------------------------------------------------ the stand-in world

        private sealed class World
        {
            public BepInExQueuedUpdate Pref;
            public readonly Dictionary<string, bool> Up = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<BepInExWriteOptions> Writes = new();
            public Func<BepInExWriteOptions, Task<BepInExInstallResult>> Writer;
            public bool SlotHeld;

            public World(params string[] realms)
            {
                foreach (var realm in realms) Up[realm] = false;
            }

            public World Queue(string version = "5.4.2360")
            {
                Pref = BepInExQueuedUpdate.From(version, new BepInExWriteOptions { OverOutside = true },
                    new DateTime(2026, 10, 28, 16, 44, 0, DateTimeKind.Utc));
                return this;
            }

            /// <summary>
            /// The profile list the bridge hands the queue. A realm on its way up counts as
            /// stopped, because the loader step runs before the status moves to Starting.
            /// </summary>
            public IEnumerable<BepInExProfileInstall> Installs(string starting = null)
                => Up.Select(kv => new BepInExProfileInstall
                {
                    ProfileName = kv.Key,
                    ServerExePath = BaseExe,
                    Running = kv.Value && !string.Equals(kv.Key, starting, StringComparison.OrdinalIgnoreCase),
                }).ToList();

            public BepInExQueueSteps Steps(string starting = null) => new()
            {
                Load = () => Pref,
                Clear = ran => { if (Pref != null && Pref.SameAs(ran)) Pref = null; },
                StillUp = () => BepInExService.ServersBlockingWrite(BaseExe, Installs(starting), Array.Empty<string>()),
                TryBegin = () => { if (SlotHeld) return false; SlotHeld = true; return true; },
                End = () => SlotHeld = false,
                Write = options =>
                {
                    Writes.Add(options);
                    return Writer?.Invoke(options)
                           ?? Task.FromResult(new BepInExInstallResult { Installed = true, Version = "5.4.2361" });
                },
            };

            public Task<BepInExQueueRun> Run(string starting = null) => BepInExQueue.RunAsync(Steps(starting));
        }

        private sealed class FakeRealm : IBepInExRealm
        {
            public string Name { get; init; }
            public bool IsStopped { get; set; }
            public TimeSpan StopSaveBound { get; init; } = TimeSpan.FromSeconds(30);

            /// <summary>False for a server that will not go down whatever it is asked.</summary>
            public bool Obeys { get; init; } = true;

            public int Stops;
            public int Starts;

            public void Stop()
            {
                Stops++;
                if (Obeys) IsStopped = true;
            }

            public void Start()
            {
                Starts++;
                IsStopped = false;
            }
        }

        private static HostFacingException Busy() => new("bepinex.busy", "BepInEx is already being written.");

        // ------------------------------------------------------------------ scenario 3: one name per server

        /// <summary>
        /// Scenario 3. A base install's one live realm is a running profile AND a process on the
        /// list, and the refusal named it twice: "Default, Valheim dedicated server". The process
        /// a running profile owns is that profile, so it is one name.
        /// </summary>
        [Fact]
        public void One_running_base_profile_and_its_own_process_are_one_name()
        {
            var profiles = new[] { new BepInExProfileInstall { ProfileName = "Default", ServerExePath = BaseExe, Running = true } };

            Assert.Equal(new[] { "Default" }, BepInExService.ServersBlockingWrite(BaseExe, profiles, new[] { BaseExe }));

            // The same image spelled another way is still that profile's own process.
            Assert.Equal(new[] { "Default" },
                BepInExService.ServersBlockingWrite(BaseExe, profiles, new[] { BaseExe.ToUpperInvariant() }));
        }

        /// <summary>Scenario 3. A process nobody here started is still named by its folder, and only it.</summary>
        [Fact]
        public void A_process_bakaloader_did_not_start_is_still_named_by_its_folder()
        {
            var stopped = new[] { new BepInExProfileInstall { ProfileName = "Default", ServerExePath = BaseExe, Running = false } };
            Assert.Equal(new[] { "Valheim dedicated server" },
                BepInExService.ServersBlockingWrite(BaseExe, stopped, new[] { BaseExe }));

            var running = new[] { new BepInExProfileInstall { ProfileName = "Default", ServerExePath = BaseExe, Running = true } };
            Assert.Equal(new[] { "Default", "Isolated" },
                BepInExService.ServersBlockingWrite(BaseExe, running, new[] { BaseExe, IsolatedExe }));

            Assert.Empty(BepInExService.ForeignServersOnly(BaseExe, running, new[] { BaseExe }));
            Assert.Equal(new[] { "Isolated" }, BepInExService.ForeignServersOnly(BaseExe, running, new[] { BaseExe, IsolatedExe }));
        }

        /// <summary>Scenario 3. The write's own refusal reads the same rule, and the waiting and queued bars do too.</summary>
        [Fact]
        public void The_write_and_both_bars_name_the_servers_through_the_one_rule()
        {
            var service = AppSourceTree.Files()["BepInExService.cs"].Replace("\r\n", "\n");
            var refuse = Body(service, "private void RefuseWhileServersAreUp(");
            Assert.Contains("ServersBlockingWrite(baseExePath, profiles, RunningServerImagePaths())", refuse);
            Assert.Contains("throw ServersRunning(blocked);", refuse);

            Assert.Matches(@"waitingProfiles = waiting == null\s*\?\s*Array\.Empty<string>\(\)\s*:\s*BepInEx\.ServersHoldingBase\(", Bridge());
            Assert.Contains("names = BepInEx.ServersHoldingBase(", Body(Bridge(), "private object BepInExQueuedDto()"));
        }

        // ------------------------------------------------------------------ scenarios 4 and 5: it waits for the last one

        /// <summary>
        /// Scenario 4. Queued with A up, the host starts B by hand: B's start does not write
        /// because A is up, and the queue stays. Stop A, then B: it fires once, at B's stop.
        /// </summary>
        [Fact]
        public async Task Queued_with_A_up_a_start_of_B_waits_and_it_fires_once_at_the_last_stop()
        {
            var world = new World("A", "B").Queue();
            world.Up["A"] = true;

            // B's start: the loader step runs with B counted as stopped.
            world.Up["B"] = true;
            var atStart = await world.Run(starting: "B");
            Assert.Equal(BepInExQueueEnding.ServersUp, atStart.Ending);
            Assert.Equal(new[] { "A" }, atStart.StillUp);
            Assert.NotNull(world.Pref);
            Assert.Empty(world.Writes);

            world.Up["A"] = false;
            var atA = await world.Run();
            Assert.Equal(BepInExQueueEnding.ServersUp, atA.Ending);
            Assert.Equal(new[] { "B" }, atA.StillUp);
            Assert.Empty(world.Writes);

            world.Up["B"] = false;
            Assert.Equal(BepInExQueueEnding.Written, (await world.Run()).Ending);
            Assert.Null(world.Pref);

            Assert.Equal(BepInExQueueEnding.NothingQueued, (await world.Run()).Ending);
            Assert.Single(world.Writes);
        }

        /// <summary>Scenario 5. Two realms share the BepInEx and one stops: no write, and the one still up is named.</summary>
        [Fact]
        public async Task Two_realms_one_stops_no_write_and_the_one_still_up_is_named()
        {
            var world = new World("North", "South").Queue();
            world.Up["North"] = true;
            world.Up["South"] = true;

            world.Up["North"] = false;
            var run = await world.Run();
            Assert.Equal(BepInExQueueEnding.ServersUp, run.Ending);
            Assert.Equal(new[] { "South" }, run.StillUp);
            Assert.True(run.QueueStays);
            Assert.Empty(world.Writes);

            world.Up["South"] = false;
            Assert.Equal(BepInExQueueEnding.Written, (await world.Run()).Ending);
            Assert.Single(world.Writes);
        }

        // ------------------------------------------------------------------ scenario 6: the restart window

        /// <summary>
        /// Scenario 6. The queue is the whole of the window's loader step when there is one: it
        /// runs before the consent read and the plan (which holds the soak), and the plan does
        /// not run after it. The order inside the window is driven in ValheimServerLoaderWindowTests.
        /// </summary>
        [Fact]
        public void The_window_runs_the_queue_before_the_plan_and_only_the_queue()
        {
            var body = Body(Bridge(), "private async Task ApplyBepInExUpdateAsync(");
            var queue = body.IndexOf("if (await RunBepInExQueueInWindowAsync(profile, stop)) return;", StringComparison.Ordinal);
            var consent = body.IndexOf("BepInExConsent.Effective(", StringComparison.Ordinal);
            var plan = body.IndexOf("BepInExUnattended.PlanAsync(", StringComparison.Ordinal);
            Assert.True(queue >= 0, "the window no longer runs the queue");
            Assert.True(queue < consent && consent < plan, "the queue no longer comes before the consent and the plan");

            var turn = Body(Bridge(), "private async Task<bool> RunBepInExQueueInWindowAsync(");
            var none = turn.IndexOf("if (BepInExQueued() == null) return false;", StringComparison.Ordinal);
            var run = turn.IndexOf("await TryRunQueuedBepInExAsync(profile,", StringComparison.Ordinal);
            var ran = turn.IndexOf("return true;", StringComparison.Ordinal);
            Assert.True(none >= 0 && run > none && ran > run, "the window's turn at the queue no longer runs it");
            Assert.Contains("_bepInExQueueSkipStart[profile] = true;", turn);
        }

        /// <summary>Scenario 6. The queued write is the host's own press: not unattended, so the soak does not hold it.</summary>
        [Fact]
        public async Task The_queued_write_is_not_unattended()
        {
            var world = new World("Default").Queue();
            await world.Run();

            var options = Assert.Single(world.Writes);
            Assert.False(options.Unattended);
            Assert.True(options.OverOutside);
            Assert.True(options.TakeCurrentPack);
        }

        // ------------------------------------------------------------------ scenario 7: a write that fails

        /// <summary>
        /// Scenario 7. A refusal trying again cannot fix takes the queue off with the failure
        /// standing; one that passes on its own keeps it with the reason. Neither throws, so the
        /// window that ran it goes on to the relaunch.
        /// </summary>
        [Theory]
        [InlineData("bepinex.integrity", false)]
        [InlineData("bepinex.tooLarge", false)]
        [InlineData("bepinex.notALoader", false)]
        [InlineData("bepinex.offline", true)]
        [InlineData("bepinex.locked", true)]
        [InlineData("bepinex.busy", true)]
        [InlineData("bepinex.serversRunning", true)]
        public async Task A_hard_refusal_drops_the_queue_and_a_passing_one_keeps_it(string id, bool kept)
        {
            var world = new World("Default").Queue();
            world.Writer = _ => throw new HostFacingException(id, "refused");

            var run = await world.Run();

            Assert.Equal(kept ? BepInExQueueEnding.Kept : BepInExQueueEnding.Dropped, run.Ending);
            Assert.Equal(id, HostFacingException.IdOf(run.Error));
            Assert.Equal(kept, world.Pref != null);
            Assert.Equal(kept, run.QueueStays);
            Assert.False(world.SlotHeld, "the write slot was left taken");
        }

        /// <summary>Scenario 7. A stalled download that hits the window's clock is the offline case: kept.</summary>
        [Fact]
        public async Task A_stalled_download_cut_by_the_window_clock_keeps_the_queue()
        {
            var world = new World("Default").Queue();
            world.Writer = _ => throw new OperationCanceledException();
            Assert.Equal(BepInExQueueEnding.Kept, (await world.Run()).Ending);
            Assert.NotNull(world.Pref);

            world.Writer = _ => throw new TimeoutException();
            Assert.Equal(BepInExQueueEnding.Kept, (await world.Run()).Ending);

            world.Writer = _ => throw new IOException("the archive did not open");
            Assert.Equal(BepInExQueueEnding.Dropped, (await world.Run()).Ending);
            Assert.Null(world.Pref);
        }

        /// <summary>Scenario 7. The bridge records each ending the way the bar reads it.</summary>
        [Fact]
        public void Every_ending_is_recorded_where_the_bar_reads_it()
        {
            var body = Body(Bridge(), "private async Task<Tools.BepInExQueueRun> TryRunQueuedBepInExAsync(");
            var dropped = body.IndexOf("case Tools.BepInExQueueEnding.Dropped:", StringComparison.Ordinal);
            Assert.True(dropped >= 0);
            Assert.Contains("RecordUnattendedBepInExFailure(profile, run.Error);", body.Substring(dropped));

            var written = body.IndexOf("case Tools.BepInExQueueEnding.Written:", StringComparison.Ordinal);
            Assert.Contains("RecordUnattendedBepInEx(profile, run.Result);", body.Substring(written, dropped - written));
            Assert.Contains("_bepInExQueueReasonId = HostFacingException.IdOf(run.Error)", body);
            Assert.Contains("_bepInExQueueReasonId = \"bepinex.serversRunning\";", body);
        }

        // ------------------------------------------------------------------ scenario 8: the pack actually written

        /// <summary>
        /// Scenario 8. Queued at X with the site now serving X+1: the write takes the current pack
        /// and the written outcome names X+1. The real service half is in BepInExTakeoverTests.
        /// </summary>
        [Fact]
        public async Task The_written_outcome_names_the_pack_that_went_in_not_the_one_queued()
        {
            var world = new World("Default").Queue("5.4.2360");
            var run = await world.Run();

            Assert.Equal("5.4.2360", run.Queued.Version);
            Assert.Equal("5.4.2361", run.Result.Version);

            var outcome = BlendWindow.BepInExOutcomeOf("Default", run.Result, DateTime.UtcNow);
            Assert.Equal("written", outcome.Outcome);
            Assert.Equal("5.4.2361", outcome.ToVersion);
        }

        // ------------------------------------------------------------------ scenario 9: the app closed with a queue

        /// <summary>Scenario 9. The queue is on disk under its own key, and a 1.3.1 document reads none.</summary>
        [Fact]
        public void The_queue_survives_the_app_closing_and_a_1_3_1_document_reads_none()
        {
            var prefs = UserPreferences.GetDefault();
            Assert.Null(prefs.BepInExQueuedUpdate);

            prefs.BepInExQueuedUpdate = new World().Queue("5.4.2360").Pref;

            var document = JsonConvert.SerializeObject(prefs.ToFile());
            Assert.Contains("\"bepInExQueuedUpdate\":", document, StringComparison.Ordinal);
            Assert.Contains("\"takeCurrentPack\":true", document, StringComparison.Ordinal);

            var back = UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(document));
            Assert.NotNull(back.BepInExQueuedUpdate);
            Assert.True(back.BepInExQueuedUpdate.SameAs(prefs.BepInExQueuedUpdate));
            Assert.True(back.BepInExQueuedUpdate.OverOutside);

            var older = JsonConvert.DeserializeObject<UserPreferencesFile>(
                "{\"bepInExMaintained\":true,\"bepInExMaintenanceAsked\":true,\"bepInExNoticeSeenKey\":\"refused|soak|5.4.2360|5.4.23.5|\"}");
            Assert.Null(UserPreferences.FromFile(older).BepInExQueuedUpdate);
        }

        /// <summary>
        /// Scenario 9. A start reads the queue before the consent switch, and the loader step is
        /// the first thing the start installs, so the queued pack is in before the companion plugins.
        /// </summary>
        [Fact]
        public void A_start_reads_the_queue_first_and_the_loader_goes_in_before_the_plugins()
        {
            var start = Body(Bridge(), "private void PrepareBepInExForStart(string profile, string exePath)");
            var queue = start.IndexOf("BepInExQueued() != null", StringComparison.Ordinal);
            var consent = start.IndexOf("BepInExConsent.Effective(", StringComparison.Ordinal);
            Assert.True(queue >= 0 && queue < consent, "the start no longer reads the queue before the consent");

            var plugins = Body(Server(), "private void PrepareCompanionPlugins(string exePath, IValheimServerOptions options)");
            var first = plugins.IndexOf("Install(\"", StringComparison.Ordinal);
            Assert.Equal(first, plugins.IndexOf("Install(\"BepInEx\"", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ scenario 10: once

        /// <summary>Scenario 10. The soak passes while queued: one write at the next window, and nothing at the stop after.</summary>
        [Fact]
        public async Task One_write_clears_the_queue_and_the_next_stop_does_nothing()
        {
            var world = new World("Default").Queue();
            Assert.Equal(BepInExQueueEnding.Written, (await world.Run()).Ending);
            Assert.Null(world.Pref);

            Assert.Equal(BepInExQueueEnding.NothingQueued, (await world.Run()).Ending);
            Assert.Equal(BepInExQueueEnding.NothingQueued, (await world.Run(starting: "Default")).Ending);
            Assert.Single(world.Writes);
        }

        /// <summary>Scenario 10. A newer yes given while a write ran is not the one that write clears.</summary>
        [Fact]
        public void A_newer_yes_is_not_cleared_by_the_write_of_the_older_one()
        {
            var older = BepInExQueuedUpdate.From("5.4.2360", null, new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc));
            var newer = BepInExQueuedUpdate.From("5.4.2360", null, new DateTime(2026, 10, 28, 2, 0, 0, DateTimeKind.Utc));
            Assert.False(newer.SameAs(older));
            Assert.True(older.SameAs(BepInExQueuedUpdate.From("5.4.2360", null, older.QueuedUtc)));
        }

        // ------------------------------------------------------------------ scenarios 11 and 13 to 15: Stop, install and start again

        private static BepInExStopAndInstallSteps Orchestrate(World world, IReadOnlyList<IBepInExRealm> realms,
            Func<Task<BepInExQueueRun>> runQueued = null, IReadOnlyList<string> foreign = null)
        {
            var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            return new BepInExStopAndInstallSteps
            {
                TryBegin = () => { if (world.SlotHeld) return false; world.SlotHeld = true; return true; },
                End = () => world.SlotHeld = false,
                Busy = Busy,
                ForeignHolders = () => foreign ?? Array.Empty<string>(),
                Realms = () => realms,
                RunQueued = runQueued ?? (() =>
                {
                    var steps = world.Steps();
                    return BepInExQueue.RunAsync(new BepInExQueueSteps
                    {
                        Load = steps.Load, Clear = steps.Clear, Write = steps.Write,
                        StillUp = () => realms.Where(r => !r.IsStopped).Select(r => r.Name).ToList(),
                    });
                }),
                Deadline = ValheimServer.CloseDeadlineFor,
                Delay = d => { now += d; return Task.CompletedTask; },
                Clock = () => now,
            };
        }

        /// <summary>
        /// Scenario 11. A double press: the second is told the write is busy, each realm is
        /// stopped once and started once, and there is one write.
        /// </summary>
        [Fact]
        public async Task A_double_press_is_busy_and_each_realm_goes_down_and_up_once()
        {
            var world = new World().Queue();
            var a = new FakeRealm { Name = "A" };
            var b = new FakeRealm { Name = "B" };
            var realms = new IBepInExRealm[] { a, b };

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var steps = Orchestrate(world, realms);
            var inner = steps.RunQueued;
            var first = BepInExStopAndInstall.RunAsync(new BepInExStopAndInstallSteps
            {
                TryBegin = steps.TryBegin, End = steps.End, Busy = steps.Busy, ForeignHolders = steps.ForeignHolders,
                Realms = steps.Realms, Deadline = steps.Deadline, Delay = steps.Delay, Clock = steps.Clock,
                RunQueued = async () => { await gate.Task; return await inner(); },
            });

            var second = await Assert.ThrowsAsync<HostFacingException>(
                () => BepInExStopAndInstall.RunAsync(Orchestrate(world, realms)));
            Assert.Equal("bepinex.busy", second.MessageId);

            gate.SetResult(true);
            var done = await first;

            Assert.Equal(BepInExQueueEnding.Written, done.Run.Ending);
            Assert.Equal(new[] { "A", "B" }, done.Started);
            Assert.Equal(1, a.Stops);
            Assert.Equal(1, b.Stops);
            Assert.Equal(1, a.Starts);
            Assert.Equal(1, b.Starts);
            Assert.Single(world.Writes);
            Assert.False(world.SlotHeld);
        }

        /// <summary>
        /// Scenario 13. A realm that does not stop within CloseDeadlineFor: nothing is written,
        /// the queue stays, the refusal names that realm, and nothing already stopped is started.
        /// </summary>
        [Fact]
        public async Task A_realm_that_will_not_stop_writes_nothing_and_starts_nothing()
        {
            var world = new World().Queue();
            var quick = new FakeRealm { Name = "Quick" };
            var stuck = new FakeRealm { Name = "Stuck", Obeys = false, StopSaveBound = TimeSpan.FromSeconds(45) };
            var ran = 0;

            var refused = await Assert.ThrowsAsync<HostFacingException>(() => BepInExStopAndInstall.RunAsync(
                Orchestrate(world, new IBepInExRealm[] { quick, stuck }, () => { ran++; return Task.FromResult<BepInExQueueRun>(null); })));

            Assert.Equal("bepinex.stopTimedOut", refused.MessageId);
            Assert.Equal("Stuck", refused.Params["names"]);
            Assert.Equal(0, ran);
            Assert.Empty(world.Writes);
            Assert.NotNull(world.Pref);
            Assert.Equal(0, quick.Starts);
            Assert.True(quick.IsStopped);
            Assert.True(stuck.Stops > 1, "a server still Starting was asked to stop only once");
            Assert.False(world.SlotHeld);

            // And the bridge keeps that reason for the bar rather than dropping it with the toast.
            var rpc = Rpc("bepinex.stopAndInstall");
            Assert.Contains("_bepInExQueueReasonId = HostFacingException.IdOf(e);", rpc);
            Assert.Contains("Deadline = ValheimServer.CloseDeadlineFor,", rpc);
        }

        /// <summary>
        /// Scenario 14. A server started outside BakaLoader holds the base: refused up front,
        /// naming its folder, and nothing is stopped.
        /// </summary>
        [Fact]
        public async Task A_server_started_elsewhere_refuses_up_front_and_stops_nothing()
        {
            var world = new World().Queue();
            var mine = new FakeRealm { Name = "Default" };
            var foreign = BepInExService.ForeignServersOnly(BaseExe,
                new[] { new BepInExProfileInstall { ProfileName = "Default", ServerExePath = BaseExe, Running = true } },
                new[] { BaseExe, IsolatedExe });

            var refused = await Assert.ThrowsAsync<HostFacingException>(() => BepInExStopAndInstall.RunAsync(
                Orchestrate(world, new IBepInExRealm[] { mine }, foreign: foreign)));

            Assert.Equal("bepinex.serversRunning", refused.MessageId);
            Assert.Equal("Isolated", refused.Params["names"]);
            Assert.Equal(0, mine.Stops);
            Assert.Empty(world.Writes);
            Assert.NotNull(world.Pref);
            Assert.False(world.SlotHeld);

            Assert.Contains("ForeignHolders = () => BepInEx.ForeignServersHoldingBase(baseExe, KnownInstalls()),",
                Rpc("bepinex.stopAndInstall"));
        }

        /// <summary>Scenario 15. A realm the host had already stopped stays stopped; only the ones it stopped come back.</summary>
        [Fact]
        public async Task A_realm_already_stopped_stays_stopped()
        {
            var world = new World().Queue();
            var up = new FakeRealm { Name = "Up" };
            var off = new FakeRealm { Name = "Off", IsStopped = true };

            var done = await BepInExStopAndInstall.RunAsync(Orchestrate(world, new IBepInExRealm[] { up, off }));

            Assert.Equal(new[] { "Up" }, done.Started);
            Assert.Equal(1, up.Starts);
            Assert.Equal(0, off.Starts);
            Assert.Equal(0, off.Stops);
            Assert.True(off.IsStopped);
            Assert.Single(world.Writes);
        }

        /// <summary>
        /// The stops Stop, install and start again makes are its own, so the plain-stop hook does
        /// not race it for the write; and the start it makes skips the queue it just ran.
        /// </summary>
        [Fact]
        public void Its_own_stops_and_starts_do_not_run_the_queue_a_second_time()
        {
            Assert.Contains("if (_bepInExStoppingForInstall || BepInExQueued() == null) return;",
                Body(Bridge(), "private void RunBepInExQueueAtStop(ServerSession session)"));
            Assert.Contains("_bepInExStoppingForInstall = true;", Rpc("bepinex.stopAndInstall"));
            Assert.Contains("slotHeld: true", Rpc("bepinex.stopAndInstall"));
            Assert.Contains("Window._bepInExQueueSkipStart[Session.ProfileName] = true;", Bridge());
        }

        // ------------------------------------------------------------------ scenario 12: the plain stop

        /// <summary>
        /// Scenario 12. A plain stop fires the queue; the first half of a restart does not (the
        /// window runs it), and neither does a stop with the Windows power-off due. The real
        /// server's flags at its Stopped moment are driven in EmptyServerStopTests.
        /// </summary>
        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        public void Only_a_plain_stop_fires_the_queue(bool relaunchPending, bool powerOffDue, bool fires)
            => Assert.Equal(fires, BepInExQueue.FiresAtStop(relaunchPending, powerOffDue));

        [Fact]
        public void The_stopped_hook_asks_the_rule_with_the_server_own_flags()
        {
            var bridge = Bridge();
            Assert.Contains("if (status == ServerStatus.Stopped) RunBepInExQueueAtStop(session);", bridge);
            Assert.Contains("BepInExQueue.FiresAtStop(server.RelaunchPending, server.StoppedWithWindowsPowerOffDue)",
                Body(bridge, "private void RunBepInExQueueAtStop(ServerSession session)"));
        }

        // ------------------------------------------------------------------ scenarios 16 and 17: Check BepInEx now

        /// <summary>Scenario 16. Nothing found is no outcome, and a site that did not answer records nothing at all.</summary>
        [Fact]
        public void A_check_that_found_nothing_newer_records_nothing()
        {
            var none = BlendWindow.BepInExCheckPlan(null, false, DateTime.UtcNow);
            Assert.Null(none.Outcome);
            Assert.Null(none.Waiting);
            Assert.Equal("current", none.Kind);

            var current = BlendWindow.BepInExCheckPlan(new BepInExCheck { Version = "5.4.2360", Newer = false }, true, DateTime.UtcNow);
            Assert.Null(current.Outcome);
            Assert.Null(current.Waiting);

            // The check is asked before anything is recorded, so its throw leaves the bar as it was.
            var rpc = Rpc("bepinex.check");
            var asked = rpc.IndexOf("await BepInEx.CheckAsync(", StringComparison.Ordinal);
            var recorded = rpc.IndexOf("_bepInExLastUnattended = plan.Outcome;", StringComparison.Ordinal);
            Assert.True(asked >= 0 && recorded > asked);
            Assert.DoesNotContain("catch", rpc.Substring(0, recorded));
        }

        /// <summary>Scenario 16 and 17. A newer pack a window would write is the waiting row; one it would refuse is that refusal.</summary>
        [Fact]
        public void A_newer_pack_is_the_waiting_row_or_the_window_refusal()
        {
            var writable = BlendWindow.BepInExCheckPlan(new BepInExCheck { Version = "5.4.2361", Newer = true }, true, DateTime.UtcNow);
            Assert.Null(writable.Outcome);
            Assert.Equal("5.4.2361", writable.Waiting);
            Assert.Equal("newer", writable.Kind);

            var refused = BlendWindow.BepInExCheckPlan(new BepInExCheck { Version = "5.4.2361", Newer = true }, false, DateTime.UtcNow);
            Assert.Null(refused.Waiting);
        }

        /// <summary>
        /// Scenario 17. The host closed the soak bar for a pack; a check finds the same pack still
        /// soaking. The check's outcome is keyed exactly as the window's was, and the check clears
        /// the seen key only when it IS that key, so the bar comes back once.
        /// </summary>
        [Fact]
        public void A_check_on_a_closed_soak_bar_brings_it_back_by_the_same_key()
        {
            var when = new DateTime(2026, 10, 28, 16, 44, 0, DateTimeKind.Utc);
            var soaking = new BepInExInstallResult
            {
                Skipped = true,
                SkipReason = BepInExSkipReason.Soak,
                Version = "5.4.2360",
                PreviousCoreVersion = "5.4.23.5",
                EligibleUtc = when.AddHours(72),
            };

            var window = BlendWindow.BepInExOutcomeOf("Default", soaking, when);
            var check = BlendWindow.BepInExCheckPlan(new BepInExCheck
            {
                Version = "5.4.2360",
                Newer = true,
                UnattendedRefusal = BepInExSkipReason.Soak,
                EligibleUtc = when.AddHours(72),
                WindowResult = soaking,
            }, false, when.AddDays(1));

            Assert.Equal("soak", check.Kind);
            Assert.NotNull(check.Outcome);
            Assert.Equal(window.Key, check.Outcome.Key);

            var rpc = Rpc("bepinex.check");
            Assert.Contains("if (!string.Equals(pr.BepInExNoticeSeenKey, key, StringComparison.Ordinal)) return false;", rpc);
            Assert.Contains("pr.BepInExNoticeSeenKey = null;", rpc);
        }

        // ------------------------------------------------------------------ scenario 18: cancel

        /// <summary>Scenario 18. Cancel the queue, then stop every realm: nothing is written and the pref is gone.</summary>
        [Fact]
        public async Task Cancel_then_stop_every_realm_writes_nothing()
        {
            var world = new World("A", "B").Queue();
            world.Up["A"] = true;

            world.Pref = null;   // bepinex.unqueue
            world.Up["A"] = false;

            Assert.Equal(BepInExQueueEnding.NothingQueued, (await world.Run()).Ending);
            Assert.Empty(world.Writes);
            Assert.Null(world.Pref);

            var unqueue = Rpc("bepinex.unqueue");
            Assert.Contains("ClearBepInExQueue(null);", unqueue);
            Assert.Contains("if (ran != null && !prefs.BepInExQueuedUpdate.SameAs(ran)) return false;",
                Body(Bridge(), "private void ClearBepInExQueue(Tools.BepInExQueuedUpdate ran)"));
        }

        // ------------------------------------------------------------------ scenario 20: consent off

        /// <summary>
        /// Scenario 20. The queue carries no consent and is not asked about it: the host's own
        /// press still writes with the kept-up-to-date switch off, while the restart window's own
        /// plan stays Skip.
        /// </summary>
        [Fact]
        public async Task With_consent_off_the_queue_still_writes_and_the_window_plan_stays_skip()
        {
            var keys = typeof(BepInExQueuedUpdate).GetProperties()
                .Select(p => p.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName)
                .Where(n => n != null).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "overDrivenElsewhere", "overForeign", "overOutside", "overUnrecognised",
                "queuedUtc", "takeCurrentPack", "version" }, keys);

            var world = new World("Default").Queue();
            Assert.Equal(BepInExQueueEnding.Written, (await world.Run()).Ending);

            var consent = BepInExConsent.Effective(maintained: false, asked: true);
            Assert.Equal(BepInExUnattendedAction.Skip,
                BepInExUnattended.Decide(consent, true, "5.4.2350", "5.4.2360", false));
        }
    }
}
