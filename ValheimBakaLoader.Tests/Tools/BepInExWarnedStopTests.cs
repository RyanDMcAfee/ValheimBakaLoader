using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Queue item 20 of 1.3.3 (the 2026-11-03 follow-up on issue 31): Stop, install and start
    /// again warns the players first (D1), and a pack the app already knows about goes in at the
    /// stop of the last server on its BepInEx or before a start with nothing else up (D2).
    /// <para>
    /// Driven against stand-ins: realms on one virtual clock that go down when they are told to
    /// (or when their warning has run out), a write that counts itself, and a site that counts
    /// every time it is asked. The unattended step is the real <see cref="BepInExUnattended.PlanAsync"/>
    /// with those stand-ins behind it, so the consent-first order is the rule's own and not the
    /// test's. Each scenario carries the number it has in the 1.3.3 spec.
    /// </para>
    /// </summary>
    public class BepInExWarnedStopTests
    {
        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"].Replace("\r\n", "\n");

        private static string Body(string source, string signature)
        {
            var from = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(from >= 0, "the source no longer holds " + signature);
            var to = source.IndexOf("\n        }\n", from, StringComparison.Ordinal);
            Assert.True(to > from, signature + " no longer closes at its indent");
            return source.Substring(from, to - from);
        }

        private static string Rpc(string name)
        {
            var bridge = Bridge();
            var from = bridge.IndexOf("RegisterRpc(\"" + name + "\"", StringComparison.Ordinal);
            Assert.True(from >= 0, "the bridge no longer registers " + name);
            var to = bridge.IndexOf("RegisterRpc(\"", from + 12, StringComparison.Ordinal);
            return bridge.Substring(from, (to > from ? to : bridge.Length) - from);
        }

        private static readonly TimeSpan OneMinuteWarning =
            TimeSpan.FromSeconds(ValheimServer.ManualRestartCountdownSeconds[0]) + ValheimServer.CountdownFinalPause;

        // ------------------------------------------------------------------ the stand-in world

        /// <summary>One clock and one event log for every realm, so the order across servers is real.</summary>
        private sealed class World
        {
            public DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
            public readonly List<string> Events = new();
            public readonly List<Realm> Realms = new();
            public BepInExQueuedUpdate Queue;
            public bool SlotHeld;
            public int Writes;
            public int SiteAsked;
            public bool Consent = true;
            public string Waiting;
            public string Installed = "5.4.2350";
            public string Latest = "5.4.2361";

            public Realm Add(string name, bool up = true, int players = 0, bool obeys = true)
            {
                var realm = new Realm(this) { Name = name, IsStopped = !up, Players = players, Obeys = obeys };
                Realms.Add(realm);
                return realm;
            }

            public void Tick(TimeSpan by)
            {
                Now += by;
                foreach (var realm in Realms) realm.Tick();
            }

            public IReadOnlyList<string> Up(string except = null) => Realms
                .Where(r => !r.IsStopped && !string.Equals(r.Name, except, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Name).ToList();

            /// <summary>The restart window's unattended step, as the bridge runs it, on the real plan.</summary>
            public async Task Unattended(string except = null)
            {
                var plan = await BepInExUnattended.PlanAsync(
                    Consent,
                    () => new BepInExStatus { Installed = true, PackVersion = Installed, Package = "denikson-BepInExPack_Valheim" },
                    _ => { SiteAsked++; return Task.FromResult(Latest); },
                    () => Up(except).Count > 0);

                if (plan.Action == BepInExUnattendedAction.Defer) { Waiting = plan.Latest; return; }
                if (plan.Action != BepInExUnattendedAction.Apply) return;
                Write();
                Waiting = null;
            }

            public void Write()
            {
                Assert.Empty(Up());
                Writes++;
                Installed = Latest;
                Events.Add("write");
            }

            public string Known() => BepInExAtRest.KnownNewer(Waiting, null, null, null, null, Now);

            /// <summary>What the stop of one realm runs, through the real orchestrator and the real gate.</summary>
            public Task<BepInExAtRestEnding> StopOf(Realm realm, bool relaunch = false, bool powerOff = false)
            {
                realm.IsStopped = true;
                return BepInExAtRest.RunAsync(new BepInExAtRestSteps
                {
                    Allowed = BepInExQueue.FiresAtStop(relaunch, powerOff),
                    Queued = () => Queue != null,
                    RunQueue = () => { Write(); Queue = null; return Task.CompletedTask; },
                    NewerKnown = Known,
                    OthersUp = () => Up(),
                    RunUnattended = () => Unattended(),
                });
            }

            public Task<BepInExAtRestEnding> StartOf(Realm realm, bool queuedAtStart = false) =>
                BepInExAtRest.RunAsync(new BepInExAtRestSteps
                {
                    Queued = () => queuedAtStart,
                    NewerKnown = Known,
                    OthersUp = () => Up(except: realm.Name),
                    RunUnattended = () => Unattended(except: realm.Name),
                });

            public BepInExStopAndInstallSteps StopAndInstall(Action<IReadOnlyList<string>> warned = null,
                IReadOnlyList<string> foreign = null) => new()
            {
                TryBegin = () => { if (SlotHeld) return false; SlotHeld = true; return true; },
                End = () => SlotHeld = false,
                Busy = () => new HostFacingException("bepinex.busy", "BepInEx is already being written."),
                ForeignHolders = () => foreign ?? Array.Empty<string>(),
                Realms = () => Realms,
                RunQueued = () =>
                {
                    Write();
                    Queue = null;
                    return Task.FromResult(new BepInExQueueRun { Ending = BepInExQueueEnding.Written });
                },
                Deadline = ValheimServer.CloseDeadlineFor,
                Warned = warned,
                Delay = d => { Tick(d); return Task.CompletedTask; },
                Clock = () => Now,
            };
        }

        /// <summary>
        /// One server as Stop, install and start again sees it. With players on, a warned stop
        /// starts the minute and the server goes down by itself when it runs out; a plain Stop()
        /// during the warning is recorded as the bug it would be.
        /// </summary>
        private sealed class Realm : IBepInExRealm
        {
            private readonly World World;
            private DateTime? WarningEnds;

            public Realm(World world) => World = world;

            public string Name { get; init; }
            public bool IsStopped { get; set; }
            public int Players { get; set; }
            public bool Obeys { get; init; } = true;
            public TimeSpan StopSaveBound { get; init; } = TimeSpan.FromSeconds(30);
            public int Warnings;
            public int Stops;
            public int Starts;
            public int StopsDuringWarning;

            public bool Warning => WarningEnds != null;

            public TimeSpan WarnedStop()
            {
                if (Players == 0)
                {
                    Stop();
                    return TimeSpan.Zero;
                }

                Warnings++;
                World.Events.Add("warn:" + Name);
                WarningEnds = World.Now + OneMinuteWarning;
                return OneMinuteWarning;
            }

            public void Stop()
            {
                if (Warning) StopsDuringWarning++;
                Stops++;
                if (!Obeys || IsStopped) return;
                IsStopped = true;
                World.Events.Add("down:" + Name);
            }

            public void Start()
            {
                Starts++;
                IsStopped = false;
                World.Events.Add("start:" + Name);
            }

            public void Tick()
            {
                if (WarningEnds == null || World.Now < WarningEnds) return;
                WarningEnds = null;
                Stop();
            }
        }

        // ------------------------------------------------------------------ D1: the warned stop

        /// <summary>
        /// Scenario 1. A and B share a BepInEx and are both up, a newer pack is known and nothing
        /// is queued. A's restart window defers (B is up). The waiting bar's Stop, install and
        /// start again warns, stops both, writes once and starts both.
        /// </summary>
        [Fact]
        public async Task The_waiting_pack_goes_in_once_from_stop_install_and_start_again()
        {
            var world = new World();
            var a = world.Add("A", players: 1);
            var b = world.Add("B", players: 1);

            // A's window: A is down for it, B is not.
            a.IsStopped = true;
            await world.Unattended(except: "A");
            a.IsStopped = false;
            Assert.Equal("5.4.2361", world.Waiting);
            Assert.Equal(0, world.Writes);

            var done = await BepInExStopAndInstall.RunAsync(world.StopAndInstall());

            Assert.Equal(1, world.Writes);
            Assert.Equal(new[] { "A", "B" }, done.Started);
            Assert.Equal(1, a.Warnings);
            Assert.Equal(1, b.Warnings);
            Assert.Equal(1, a.Starts);
            Assert.Equal(1, b.Starts);
            Assert.False(world.SlotHeld);
        }

        /// <summary>
        /// Scenario 7. Players on A, nobody on B. A gets its minute and goes down after it, B goes
        /// down at once, nothing is written until A is down, and both come back. A is never cut
        /// short by the poll's plain stop.
        /// </summary>
        [Fact]
        public async Task A_with_players_is_warned_B_without_stops_at_once_and_the_write_waits_for_both()
        {
            var world = new World();
            var a = world.Add("A", players: 3);
            var b = world.Add("B", players: 0);
            var warned = new List<string>();

            var started = world.Now;
            await BepInExStopAndInstall.RunAsync(world.StopAndInstall(names => warned.AddRange(names)));

            Assert.Equal(new[] { "warn:A", "down:B", "down:A", "write", "start:A", "start:B" }, world.Events);
            Assert.Equal(0, a.StopsDuringWarning);
            Assert.Equal(new[] { "A" }, warned);
            Assert.True(world.Now - started >= OneMinuteWarning, "the write did not wait for the warning");
        }

        /// <summary>
        /// Scenario 8, the orchestrator half (the server half, one countdown and no restart, is in
        /// ValheimServerWarnedStopTests). Every server is warned at the same moment, so two servers
        /// with players cost one minute, not two.
        /// </summary>
        [Fact]
        public async Task Two_warned_servers_wait_one_countdown_not_two()
        {
            var world = new World();
            world.Add("A", players: 1);
            world.Add("B", players: 2);

            var started = world.Now;
            await BepInExStopAndInstall.RunAsync(world.StopAndInstall());

            Assert.Equal(new[] { "warn:A", "warn:B" }, world.Events.Take(2));
            Assert.True(world.Now - started < OneMinuteWarning + OneMinuteWarning,
                "the warnings ran one after the other");
            Assert.Equal(1, world.Writes);
        }

        /// <summary>Scenario 9. A second press during the warning is busy: one countdown per server, one write.</summary>
        [Fact]
        public async Task A_second_press_during_the_warning_is_busy()
        {
            var world = new World();
            var a = world.Add("A", players: 1);

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var steps = world.StopAndInstall();
            var first = BepInExStopAndInstall.RunAsync(new BepInExStopAndInstallSteps
            {
                TryBegin = steps.TryBegin, End = steps.End, Busy = steps.Busy, ForeignHolders = steps.ForeignHolders,
                Realms = steps.Realms, RunQueued = steps.RunQueued, Deadline = steps.Deadline, Clock = steps.Clock,
                Delay = async d => { await gate.Task; steps.Delay(d).Wait(); },
            });

            Assert.True(a.Warning, "the first press did not start the warning");
            var second = await Assert.ThrowsAsync<HostFacingException>(
                () => BepInExStopAndInstall.RunAsync(world.StopAndInstall()));
            Assert.Equal("bepinex.busy", second.MessageId);
            Assert.Contains("already being written", second.Message);

            gate.SetResult(true);
            await first;

            Assert.Equal(1, a.Warnings);
            Assert.Equal(1, world.Writes);
            Assert.Equal(1, a.Starts);
        }

        /// <summary>
        /// Scenario 10. A server still up after its warning AND the close deadline: nothing is
        /// written, nothing started, the sentence names it. And the deadline does count the
        /// warning: a server that goes down a little before warning plus deadline is waited for.
        /// </summary>
        [Fact]
        public async Task A_server_still_up_after_its_warning_and_the_deadline_is_named_and_nothing_is_written()
        {
            var world = new World();
            var quick = world.Add("Quick", players: 0);
            var stuck = world.Add("Stuck", players: 1, obeys: false);
            world.Queue = BepInExQueuedUpdate.From("5.4.2361", null, world.Now);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => BepInExStopAndInstall.RunAsync(world.StopAndInstall()));

            Assert.Equal("bepinex.stopTimedOut", refused.MessageId);
            Assert.Equal("Stuck", refused.Params["names"]);
            Assert.Equal(0, world.Writes);
            Assert.Equal(0, quick.Starts);
            Assert.NotNull(world.Queue);
            Assert.False(world.SlotHeld);
        }

        [Fact]
        public async Task The_deadline_is_the_longest_warning_plus_the_close_deadline()
        {
            var world = new World();
            var slow = world.Add("Slow", players: 1, obeys: false);
            var start = world.Now;
            var lastChance = start + OneMinuteWarning + ValheimServer.CloseDeadlineFor(new[] { slow.StopSaveBound })
                - TimeSpan.FromSeconds(1);

            var steps = world.StopAndInstall();
            var run = BepInExStopAndInstall.RunAsync(new BepInExStopAndInstallSteps
            {
                TryBegin = steps.TryBegin, End = steps.End, Busy = steps.Busy, ForeignHolders = steps.ForeignHolders,
                Realms = steps.Realms, RunQueued = steps.RunQueued, Deadline = steps.Deadline, Clock = steps.Clock,
                Delay = d =>
                {
                    world.Tick(d);
                    if (world.Now >= lastChance) slow.IsStopped = true;
                    return Task.CompletedTask;
                },
            });

            var done = await run;
            Assert.Equal(new[] { "Slow" }, done.Started);
            Assert.Equal(1, world.Writes);
        }

        /// <summary>Scenario 11. A server BakaLoader did not start: refused before anyone is warned or stopped.</summary>
        [Fact]
        public async Task A_foreign_server_is_refused_before_anybody_is_warned()
        {
            var world = new World();
            var a = world.Add("A", players: 4);
            var warned = 0;

            var refused = await Assert.ThrowsAsync<HostFacingException>(() => BepInExStopAndInstall.RunAsync(
                world.StopAndInstall(_ => warned++, foreign: new[] { "Valheim dedicated server" })));

            Assert.Equal("bepinex.serversRunning", refused.MessageId);
            Assert.Equal(0, a.Warnings);
            Assert.Equal(0, a.Stops);
            Assert.Equal(0, warned);
            Assert.Empty(world.Events);
        }

        /// <summary>The bridge hands every realm's warned stop to the action, and tells the page when warnings go out.</summary>
        [Fact]
        public void The_bridge_warns_through_the_server_and_posts_the_warning()
        {
            var bridge = Bridge();
            Assert.Contains("public TimeSpan WarnedStop() => Session.Server.RequestWarnedStop();", bridge);
            Assert.Contains("public bool Warning => Session.Server.IsWarningBeforeStop;", bridge);
            var rpc = Rpc("bepinex.stopAndInstall");
            Assert.Contains("PostEvent(\"bepinex.warned\", new { names });", rpc);
            Assert.Contains("Deadline = ValheimServer.CloseDeadlineFor,", rpc);
        }

        // ------------------------------------------------------------------ D2: the stop and the start

        /// <summary>
        /// Scenario 2. Same base, nothing pressed, a pack known: the host stops A, then B. Stopping
        /// A alone asks nothing and writes nothing; the unattended step runs exactly once, at B's stop.
        /// </summary>
        [Fact]
        public async Task The_known_pack_goes_in_once_at_the_stop_of_the_last_server()
        {
            var world = new World { Waiting = "5.4.2361" };
            var a = world.Add("A");
            var b = world.Add("B");

            Assert.Equal(BepInExAtRestEnding.OthersUp, await world.StopOf(a));
            Assert.Equal(0, world.SiteAsked);
            Assert.Equal(0, world.Writes);

            Assert.Equal(BepInExAtRestEnding.Unattended, await world.StopOf(b));
            Assert.Equal(1, world.SiteAsked);
            Assert.Equal(1, world.Writes);
            Assert.Null(world.Waiting);
        }

        /// <summary>
        /// Scenario 3. One server, consent on, Check now found a pack past its wait. Stop then
        /// Start writes it once, at the stop; the start waits for that write and then finds nothing
        /// left to do. When the stop could not (Windows about to power off), the start writes it.
        /// </summary>
        [Fact]
        public async Task Stop_then_start_writes_once_and_the_start_waits_for_the_write()
        {
            var world = new World { Waiting = "5.4.2361" };
            var only = world.Add("Only");

            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            only.IsStopped = true;
            var stopStep = Task.Run(() => BepInExAtRest.RunAsync(new BepInExAtRestSteps
            {
                Queued = () => false,
                NewerKnown = world.Known,
                OthersUp = () => world.Up(),
                RunUnattended = async () => { await release.Task; await world.Unattended(); },
            }));

            // The start arrives while the stop's write is out: it waits, bounded, and decides after.
            var waited = Task.Run(() => BepInExAtRest.WaitFor(stopStep, TimeSpan.FromSeconds(30)));
            await Task.Delay(100);
            Assert.False(waited.IsCompleted, "the start went ahead over a write that was still running");
            release.SetResult(true);
            Assert.True(await waited);
            Assert.Equal(1, world.Writes);

            Assert.Equal(BepInExAtRestEnding.NothingKnown, await world.StartOf(only));
            Assert.Equal(1, world.Writes);
            Assert.Equal(1, world.SiteAsked);
        }

        [Fact]
        public async Task A_stop_with_the_power_off_due_writes_nothing_and_the_next_start_writes_it()
        {
            var world = new World { Waiting = "5.4.2361" };
            var only = world.Add("Only");

            // Scenario 12: the same rule the queue keeps at a stop.
            Assert.Equal(BepInExAtRestEnding.NotAllowed, await world.StopOf(only, powerOff: true));
            Assert.Equal(0, world.Writes);
            Assert.Equal(0, world.SiteAsked);

            Assert.Equal(BepInExAtRestEnding.Unattended, await world.StartOf(only));
            Assert.Equal(1, world.Writes);
        }

        /// <summary>The first half of a restart is not a stop moment: the window runs the step itself.</summary>
        [Fact]
        public async Task The_stop_half_of_a_restart_leaves_it_to_the_window()
        {
            var world = new World { Waiting = "5.4.2361" };
            var only = world.Add("Only");
            Assert.Equal(BepInExAtRestEnding.NotAllowed, await world.StopOf(only, relaunch: true));
            Assert.Equal(0, world.SiteAsked);
        }

        /// <summary>
        /// Scenario 4. A pack still inside its 72 hour wait is not known for a stop: the stop asks
        /// nothing and writes nothing, and the soak outcome (the bar) is left standing. Past the
        /// wait it is known. Install now is the host's own press, which is not unattended, so the
        /// wait does not hold it.
        /// </summary>
        [Fact]
        public void A_pack_inside_its_wait_is_not_known_to_a_stop_and_Install_now_skips_the_wait()
        {
            var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
            Assert.Null(BepInExAtRest.KnownNewer(null, "refused", BepInExSkipReason.Soak, "5.4.2361", now.AddHours(30), now));
            Assert.Equal("5.4.2361",
                BepInExAtRest.KnownNewer(null, "refused", BepInExSkipReason.Soak, "5.4.2361", now.AddHours(-1), now));

            // The other refusals are decisions a stop cannot change, so they are not known either.
            Assert.Null(BepInExAtRest.KnownNewer(null, "refused", BepInExSkipReason.PreRelease, "5.4.2361", null, now));
            Assert.Null(BepInExAtRest.KnownNewer(null, "refused", BepInExSkipReason.DrivenElsewhere, "5.4.2361", null, now));
            // A write of a known pack that failed on a reason that passes is still known.
            Assert.Equal("5.4.2361", BepInExAtRest.KnownNewer(null, "failed", "bepinex.locked", "5.4.2361", null, now));
            Assert.Null(BepInExAtRest.KnownNewer(null, "failed", "bepinex.integrity", "5.4.2361", null, now));
            Assert.Null(BepInExAtRest.KnownNewer(null, "written", null, "5.4.2361", null, now));

            // Install now is bepinex.update: the host's own press, never unattended.
            Assert.False(new BepInExWriteOptions().Unattended);
            Assert.Contains("await WriteBepInExAsync(null, update: true, options: BepInExOptionsFrom(p))",
                Rpc("bepinex.update"));
            var bridge = Bridge();
            var from = bridge.IndexOf("private static Tools.BepInExWriteOptions BepInExOptionsFrom(", StringComparison.Ordinal);
            Assert.True(from > 0, "the press no longer builds its own options");
            var options = bridge.Substring(from, bridge.IndexOf("};", from, StringComparison.Ordinal) - from);
            Assert.DoesNotContain("Unattended", options);

            // And the bridge reads the standing outcome through this rule, nothing else.
            Assert.Contains("return Tools.BepInExAtRest.KnownNewer(_bepInExUpdateWaiting, last?.Outcome, last?.Reason,",
                Body(Bridge(), "private string BepInExNewerKnown()"));
        }

        /// <summary>
        /// Scenario 5. Consent off with a pack known: a stop and a start write nothing and ask the
        /// site nothing (the plan reads the answer first). Install now still writes: the manual
        /// write reads the answer only to refuse a link of the host's own while BakaLoader looks
        /// after the loader.
        /// </summary>
        [Fact]
        public async Task With_consent_off_a_stop_and_a_start_ask_nothing_and_write_nothing()
        {
            var world = new World { Waiting = "5.4.2361", Consent = false };
            var only = world.Add("Only");

            await world.StopOf(only);
            await world.StartOf(only);

            Assert.Equal(0, world.SiteAsked);
            Assert.Equal(0, world.Writes);

            var write = Body(Bridge(), "private async Task<object> WriteBepInExAsync(");
            Assert.Contains("if (!string.IsNullOrWhiteSpace(url) && Tools.BepInExConsent.Effective(", write);
        }

        /// <summary>Scenario 6. No newer pack known: a stop and a start never ask the site.</summary>
        [Fact]
        public async Task With_nothing_known_a_stop_and_a_start_never_ask_the_site()
        {
            var world = new World();
            var only = world.Add("Only");

            Assert.Equal(BepInExAtRestEnding.NothingKnown, await world.StopOf(only));
            Assert.Equal(BepInExAtRestEnding.NothingKnown, await world.StartOf(only));
            Assert.Equal(0, world.SiteAsked);
            Assert.Equal(0, world.Writes);

            // The bridge asks the cheap question first, before it starts anything at all.
            var stop = Body(Bridge(), "private void RunBepInExQueueAtStop(ServerSession session)");
            Assert.Contains("if (!queued && BepInExNewerKnown() == null) return;", stop);
        }

        /// <summary>Scenario 13. A queued yes and a known pack together at a stop: the queue runs, the unattended step does not.</summary>
        [Fact]
        public async Task A_queued_yes_is_the_whole_step_at_a_stop()
        {
            var world = new World { Waiting = "5.4.2361" };
            var only = world.Add("Only");
            world.Queue = BepInExQueuedUpdate.From("5.4.2361", null, world.Now);

            Assert.Equal(BepInExAtRestEnding.Queue, await world.StopOf(only));
            Assert.Equal(1, world.Writes);
            Assert.Equal(0, world.SiteAsked);
            Assert.Null(world.Queue);
        }

        /// <summary>
        /// The bridge's two moments are wired as the spec has them: the stop under the queue's own
        /// FiresAtStop gate and its own stops left out; the start after the queue step and the
        /// consent read, skipped on the relaunch a window or Stop, install and start again makes,
        /// on the window's clock, and after waiting for a write a stop started.
        /// </summary>
        [Fact]
        public void The_stop_and_the_start_run_the_shared_step_where_the_spec_puts_them()
        {
            var bridge = Bridge();

            var stop = Body(bridge, "private void RunBepInExQueueAtStop(ServerSession session)");
            Assert.Contains("if (_bepInExStoppingForInstall) return;", stop);
            Assert.Contains("Tools.BepInExQueue.FiresAtStop(server.RelaunchPending, server.StoppedWithWindowsPowerOffDue)", stop);
            Assert.Contains("OthersUp = () => BepInEx.ServersHoldingBase(baseExe, KnownInstalls()),", stop);
            Assert.Contains("RunUnattended = () => RunUnattendedBepInExAsync(profile, LiveInstalls(),", stop);
            Assert.Contains("_bepInExStopStep = Task.Run(", stop);

            var start = Body(bridge, "private void PrepareBepInExForStart(string profile, string exePath)");
            var wait = start.IndexOf("Tools.BepInExAtRest.WaitFor(_bepInExStopStep, BepInExWriteWait)", StringComparison.Ordinal);
            var queue = start.IndexOf("TryRunQueuedBepInExAsync(profile,", StringComparison.Ordinal);
            var consent = start.IndexOf("Tools.BepInExConsent.Effective(", StringComparison.Ordinal);
            var d2 = start.IndexOf("RunUnattendedBepInExBeforeStart(profile, queuedAtStart);", StringComparison.Ordinal);
            Assert.True(wait >= 0 && wait < queue && queue < consent && consent < d2,
                "the start no longer waits, runs the queue, reads the answer and then the unattended step, in that order");
            Assert.Contains("if (!skipUnattended && !skipQueue)", start);

            var before = Body(bridge, "private void RunUnattendedBepInExBeforeStart(string profile, bool queuedAtStart)");
            Assert.Contains("ValheimServer.RestartHookBudget,", before);
            Assert.Contains(".GetAwaiter().GetResult();", before);
            Assert.Contains("Queued = () => queuedAtStart,", before);

            // The relaunch a window or Stop, install and start again makes does not run it again.
            Assert.Contains("_bepInExUnattendedSkipStart[profile ?? \"\"] = true;",
                Body(bridge, "private async Task ApplyBepInExUpdateAsync(string profile, CancellationToken stop = default)"));
            Assert.Contains("Window._bepInExUnattendedSkipStart[Session.ProfileName] = true;", bridge);
        }

        /// <summary>
        /// Scenario 17. The restart window's order is unchanged: the queue first (and alone when
        /// there is one), then the unattended plan on the window's own clock, then the relaunch,
        /// which ValheimServerLoaderWindowTests drives on the real server.
        /// </summary>
        [Fact]
        public void The_restart_window_keeps_its_order_and_its_clock()
        {
            var window = Body(Bridge(), "private async Task ApplyBepInExUpdateAsync(string profile, CancellationToken stop = default)");
            var queue = window.IndexOf("if (await RunBepInExQueueInWindowAsync(profile, stop)) return;", StringComparison.Ordinal);
            var step = window.IndexOf("await RunUnattendedBepInExAsync(profile, installs, stop, \"at the restart window\");",
                StringComparison.Ordinal);
            Assert.True(queue >= 0 && step > queue, "the window no longer runs the queue and then the unattended step");

            var helper = Body(Bridge(), "private async Task RunUnattendedBepInExAsync(string profile,");
            var consent = helper.IndexOf("Tools.BepInExConsent.Effective(", StringComparison.Ordinal);
            var plan = helper.IndexOf("Tools.BepInExUnattended.PlanAsync(", StringComparison.Ordinal);
            var expired = helper.IndexOf("if (stop.IsCancellationRequested)", StringComparison.Ordinal);
            var slot = helper.IndexOf("if (!TryBeginBepInExWrite())", StringComparison.Ordinal);
            Assert.True(consent > 0 && plan > consent && expired > plan && slot > expired,
                "the shared step no longer reads the answer, plans, checks the clock and takes the slot in that order");
        }
    }
}
