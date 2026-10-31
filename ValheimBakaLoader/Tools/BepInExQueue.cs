using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The host's yes to a BepInEx write that could not happen yet, kept until it can.
    /// <para>
    /// WHY THIS EXISTS (issue 31). A restart window turned a new pack down because it was too
    /// young for a write nobody watched, and the bar it raised offered Install it now. The press
    /// came while the relaunched server was Starting, so it was refused; the refusal was one
    /// toast, and nothing held the host's answer. Their next two full stops did nothing with it.
    /// This is the answer, written down: on disk, so closing the app does not lose it, and
    /// carrying every question the host already answered for the write, so the write that
    /// finally runs is the one they said yes to.
    /// </para>
    /// <para>
    /// The JSON names are a never-rename contract, like every other key in userprefs.json.
    /// </para>
    /// </summary>
    public sealed class BepInExQueuedUpdate
    {
        /// <summary>The pack the host was shown. The write takes whatever pack is current when it runs.</summary>
        [JsonProperty("version")] public string Version { get; set; }

        [JsonProperty("queuedUtc")] public DateTime QueuedUtc { get; set; }

        [JsonProperty("takeCurrentPack")] public bool TakeCurrentPack { get; set; }

        [JsonProperty("overOutside")] public bool OverOutside { get; set; }

        [JsonProperty("overUnrecognised")] public bool OverUnrecognised { get; set; }

        [JsonProperty("overDrivenElsewhere")] public bool OverDrivenElsewhere { get; set; }

        [JsonProperty("overForeign")] public bool OverForeign { get; set; }

        /// <summary>The answers a press carried, kept for the write that runs later.</summary>
        public static BepInExQueuedUpdate From(string version, BepInExWriteOptions options, DateTime nowUtc)
        {
            options ??= BepInExWriteOptions.Manual;
            return new BepInExQueuedUpdate
            {
                Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
                QueuedUtc = nowUtc,
                // The queue's whole job is the CURRENT pack: a host who said yes to 5.4.2360 on
                // Tuesday and stops their world on Friday wants Friday's pack, not a fetch of a
                // version the site may have moved on from.
                TakeCurrentPack = true,
                OverOutside = options.OverOutside,
                OverUnrecognised = options.OverUnrecognised,
                OverDrivenElsewhere = options.OverDrivenElsewhere,
                OverForeign = options.OverForeign,
            };
        }

        /// <summary>
        /// The write this queue stands for. Not unattended: the host pressed for it, so the
        /// rules that hold a write nobody asked for (the 72 hour soak among them) do not apply.
        /// </summary>
        public BepInExWriteOptions ToWriteOptions() => new()
        {
            Unattended = false,
            TakeCurrentPack = TakeCurrentPack,
            OverOutside = OverOutside,
            OverUnrecognised = OverUnrecognised,
            OverDrivenElsewhere = OverDrivenElsewhere,
            OverForeign = OverForeign,
        };

        /// <summary>True when the two are the same yes, and not a newer one given in the meantime.</summary>
        public bool SameAs(BepInExQueuedUpdate other)
            => other != null && other.QueuedUtc == QueuedUtc
               && string.Equals(other.Version, Version, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How one attempt at a queued write ended.</summary>
    public enum BepInExQueueEnding
    {
        /// <summary>Nothing was queued.</summary>
        NothingQueued,

        /// <summary>A server on this base is still up, so nothing was tried. The queue stays.</summary>
        ServersUp,

        /// <summary>Another write holds the one slot. The queue stays.</summary>
        Busy,

        /// <summary>The write ran and the queue is gone.</summary>
        Written,

        /// <summary>The write failed for a reason that passes (offline, a held file). The queue stays.</summary>
        Kept,

        /// <summary>The write failed for a reason trying again cannot fix. The queue is gone.</summary>
        Dropped,
    }

    /// <summary>What one attempt came to, for the caller to record and say.</summary>
    public sealed class BepInExQueueRun
    {
        public BepInExQueueEnding Ending { get; init; }

        /// <summary>The queue the attempt read, or null when there was none.</summary>
        public BepInExQueuedUpdate Queued { get; init; }

        /// <summary>What the write came to, when it ran.</summary>
        public BepInExInstallResult Result { get; init; }

        /// <summary>What the write threw, when it threw.</summary>
        public Exception Error { get; init; }

        /// <summary>The servers still up, when that is why nothing was tried.</summary>
        public IReadOnlyList<string> StillUp { get; init; } = Array.Empty<string>();

        /// <summary>True when the queue is still on disk after this attempt.</summary>
        public bool QueueStays => Ending is BepInExQueueEnding.ServersUp or BepInExQueueEnding.Busy
            or BepInExQueueEnding.Kept;
    }

    /// <summary>The five things a queued write needs from whoever runs it.</summary>
    public sealed class BepInExQueueSteps
    {
        /// <summary>The queue on disk now, or null.</summary>
        public Func<BepInExQueuedUpdate> Load { get; init; }

        /// <summary>Takes the queue off disk, but only when it is still the one handed in.</summary>
        public Action<BepInExQueuedUpdate> Clear { get; init; }

        /// <summary>Every server holding this base right now, each named once.</summary>
        public Func<IReadOnlyList<string>> StillUp { get; init; }

        /// <summary>
        /// Takes the one write slot without waiting. Null when the caller already holds it,
        /// which is Stop, install and start again: its own latch IS the write slot.
        /// </summary>
        public Func<bool> TryBegin { get; init; }

        /// <summary>Hands the slot back. Called once for every slot taken here, and only then.</summary>
        public Action End { get; init; }

        /// <summary>The write itself, with the queue's answers.</summary>
        public Func<BepInExWriteOptions, Task<BepInExInstallResult>> Write { get; init; }
    }

    /// <summary>
    /// When a queued write runs, and what its ending does to the queue. The three places it is
    /// tried (a restart window, a plain stop, a start) and Stop, install and start again all
    /// come through <see cref="RunAsync"/>, so they cannot disagree about any of it.
    /// </summary>
    public static class BepInExQueue
    {
        /// <summary>
        /// The refusals that pass on their own: a server still up, a file something else holds
        /// open, a site that did not answer, another write in the slot. The host's yes is kept
        /// through every one of them, with the reason standing in the bar. Every other refusal
        /// (an archive that did not match its listing, an install that changed shape since the
        /// yes) is one trying again cannot fix, so the queue goes and the reason stands instead.
        /// </summary>
        public static readonly IReadOnlyList<string> KeepIds = new[]
        {
            "bepinex.serversRunning", "bepinex.locked", "bepinex.offline", "bepinex.busy",
        };

        /// <summary>
        /// True when a write that threw this keeps the queue. A clock that ran out is the
        /// offline case by another road: a stalled download hit the window's own deadline.
        /// </summary>
        public static bool Keeps(Exception error)
        {
            if (error is OperationCanceledException or TimeoutException) return true;
            var id = HostFacingException.IdOf(error);
            return id != null && KeepIds.Contains(id, StringComparer.Ordinal);
        }

        /// <summary>
        /// Whether a stop that just landed on Stopped is one the queue may run at. A stop that
        /// is the first half of a restart is not: the restart window runs it in its own slot,
        /// between the stop and the relaunch. A stop with the Windows power-off due is not
        /// either: a minute from now Windows shuts down, and a write cut there leaves an
        /// install that loads neither version, so the queue waits for the next start.
        /// </summary>
        public static bool FiresAtStop(bool relaunchPending, bool windowsPowerOffDue)
            => !relaunchPending && !windowsPowerOffDue;

        /// <summary>
        /// One attempt at the queued write. Never throws: every ending is an answer.
        /// </summary>
        public static async Task<BepInExQueueRun> RunAsync(BepInExQueueSteps steps)
        {
            var queued = steps.Load?.Invoke();
            if (queued == null) return new BepInExQueueRun { Ending = BepInExQueueEnding.NothingQueued };

            var up = steps.StillUp?.Invoke() ?? Array.Empty<string>();
            if (up.Count > 0)
                return new BepInExQueueRun { Ending = BepInExQueueEnding.ServersUp, Queued = queued, StillUp = up };

            var took = false;
            if (steps.TryBegin != null)
            {
                if (!steps.TryBegin()) return new BepInExQueueRun { Ending = BepInExQueueEnding.Busy, Queued = queued };
                took = true;
            }

            try
            {
                var result = await steps.Write(queued.ToWriteOptions()).ConfigureAwait(false);
                steps.Clear?.Invoke(queued);
                return new BepInExQueueRun { Ending = BepInExQueueEnding.Written, Queued = queued, Result = result };
            }
            catch (Exception e) when (Keeps(e))
            {
                return new BepInExQueueRun { Ending = BepInExQueueEnding.Kept, Queued = queued, Error = e };
            }
            catch (Exception e)
            {
                steps.Clear?.Invoke(queued);
                return new BepInExQueueRun { Ending = BepInExQueueEnding.Dropped, Queued = queued, Error = e };
            }
            finally
            {
                if (took) steps.End?.Invoke();
            }
        }
    }

    /// <summary>One server Stop, install and start again can stop and start, as it sees one.</summary>
    public interface IBepInExRealm
    {
        string Name { get; }

        bool IsStopped { get; }

        /// <summary>The save wait this server would use if it were stopped now.</summary>
        TimeSpan StopSaveBound { get; }

        /// <summary>Stops it. The server saves its world on the way down.</summary>
        void Stop();

        /// <summary>Starts it again through the ordinary start path, launch guard included.</summary>
        void Start();
    }

    /// <summary>The pieces Stop, install and start again is made of.</summary>
    public sealed class BepInExStopAndInstallSteps
    {
        /// <summary>The one write slot, which is also this action's latch.</summary>
        public Func<bool> TryBegin { get; init; }

        /// <summary>
        /// The refusal a second press gets: the one every BepInEx writer gives when the slot is
        /// taken, because this action and the write are one latch.
        /// </summary>
        public Func<Exception> Busy { get; init; }

        public Action End { get; init; }

        /// <summary>The servers on this base that BakaLoader did not start. Any at all refuses up front.</summary>
        public Func<IReadOnlyList<string>> ForeignHolders { get; init; }

        /// <summary>Every server BakaLoader knows on this base, up or not.</summary>
        public Func<IReadOnlyList<IBepInExRealm>> Realms { get; init; }

        /// <summary>The queued write, run with the slot already held.</summary>
        public Func<Task<BepInExQueueRun>> RunQueued { get; init; }

        /// <summary>How long to wait for every server to be down, given their save waits.</summary>
        public Func<IEnumerable<TimeSpan>, TimeSpan> Deadline { get; init; }

        /// <summary>The pause between looks. A test hands in one that returns at once.</summary>
        public Func<TimeSpan, Task> Delay { get; init; } = d => Task.Delay(d);

        public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;
    }

    /// <summary>What Stop, install and start again came to.</summary>
    public sealed class BepInExStopAndInstallResult
    {
        public BepInExQueueRun Run { get; init; }

        /// <summary>The servers it started again, which are exactly the ones it stopped.</summary>
        public IReadOnlyList<string> Started { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Stop, install and start again (owner decision D1): stops the servers on this base that
    /// are up, writes the queued BepInEx, and starts again ONLY the servers it stopped.
    /// <para>
    /// A realm that was already stopped stays stopped, because the host stopped it. A server
    /// BakaLoader did not start is refused up front and nothing is stopped, because nothing
    /// here can stop it and stopping the others for a write that still cannot run would be an
    /// outage for nothing. A server that does not go down in time stops the action there:
    /// nothing is written and nothing already stopped is started, so the host is not handed a
    /// half-done state with a world coming back up beside one that would not go down.
    /// </para>
    /// </summary>
    public static class BepInExStopAndInstall
    {
        public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

        public static async Task<BepInExStopAndInstallResult> RunAsync(BepInExStopAndInstallSteps steps)
        {
            if (!steps.TryBegin()) throw steps.Busy();

            try
            {
                var foreign = steps.ForeignHolders?.Invoke() ?? Array.Empty<string>();
                if (foreign.Count > 0) throw BepInExService.ServersRunning(foreign);

                var snapshot = (steps.Realms?.Invoke() ?? Array.Empty<IBepInExRealm>())
                    .Where(r => r != null && !r.IsStopped)
                    .ToList();

                foreach (var realm in snapshot) realm.Stop();

                var deadline = steps.Clock() + steps.Deadline(snapshot.Select(r => r.StopSaveBound));
                while (snapshot.Any(r => !r.IsStopped) && steps.Clock() < deadline)
                {
                    // Stop() does nothing while a server is still Starting, so it is asked again
                    // on every look, the way the app close asks.
                    foreach (var realm in snapshot.Where(r => !r.IsStopped)) realm.Stop();
                    await steps.Delay(Poll).ConfigureAwait(false);
                }

                var still = snapshot.Where(r => !r.IsStopped).Select(r => r.Name).ToList();
                if (still.Count > 0) throw BepInExService.StopTimedOut(still);

                var run = await steps.RunQueued().ConfigureAwait(false);

                // Started again whatever the write came to. A failed write leaves its reason
                // standing; a world left down after a loader update is an outage.
                var started = new List<string>();
                foreach (var realm in snapshot)
                {
                    realm.Start();
                    started.Add(realm.Name);
                }

                return new BepInExStopAndInstallResult { Run = run, Started = started };
            }
            finally
            {
                steps.End();
            }
        }
    }
}
