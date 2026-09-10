using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>How the dedicated server on disk got there, which decides who may update it.</summary>
    public enum ServerInstallKind
    {
        /// <summary>No manifest, or one BakaLoader cannot place. Only the Steam client can help.</summary>
        Unknown = 0,

        /// <summary>A library the Steam client owns. Steam updates it; steamcmd must never touch it.</summary>
        SteamLibrary = 1,

        /// <summary>A folder steamcmd installed into, or a copy with steamcmd's own manifest beside it.</summary>
        Standalone = 2,
    }

    /// <summary>Where a server install lives and who owns it.</summary>
    public sealed class ServerInstallInfo
    {
        public ServerInstallKind Kind;

        /// <summary>The folder that holds valheim_server.exe.</summary>
        public string InstallDir;

        /// <summary>The appmanifest that describes this install, when there is one.</summary>
        public string ManifestPath;

        /// <summary>For a Steam library: the folder that holds "steamapps".</summary>
        public string LibraryRoot;

        /// <summary>A plain sentence saying why the kind is Unknown. Null otherwise.</summary>
        public string Reason;
    }

    /// <summary>Where an update has got to.</summary>
    public enum ServerUpdatePhase
    {
        Idle = 0,
        BackingUp = 1,
        AskingSteam = 2,
        Downloading = 3,
        Verifying = 4,
        RunningSteamCmd = 5,
        Finished = 6,
        Failed = 7,
        Cancelled = 8,
    }

    /// <summary>One progress push for the condition bar.</summary>
    public sealed class ServerUpdateProgress
    {
        public string ProfileName;

        public ServerUpdatePhase Phase;

        /// <summary>Zero to one hundred, or minus one when there is no number to show yet.</summary>
        public int Percent = -1;

        public long BytesDone;

        public long BytesTotal;

        /// <summary>The last line steamcmd printed, when there was one.</summary>
        public string Line;

        /// <summary>A plain sentence for the host to read.</summary>
        public string Message;
    }

    /// <summary>Everything one update needs to know.</summary>
    public sealed class ServerUpdateRequest
    {
        public string ProfileName;

        public string ServerExePath;

        public string SaveDataFolder;

        public bool BackupWorldsFirst;

        public bool StartAfter;
    }

    /// <summary>How an update ended.</summary>
    public sealed class ServerUpdateResult
    {
        public bool Ok;

        public string BuildId;

        /// <summary>A plain sentence for the host when Ok is false. Null on success.</summary>
        public string Reason;

        public bool StartAfter;

        public string ProfileName;

        /// <summary>
        /// True when the host called the wait off rather than anything going wrong. The bar
        /// treats it quietly: nothing failed, and Steam carries on downloading by itself.
        /// </summary>
        public bool Cancelled;
    }

    /// <summary>
    /// Updates the Valheim dedicated server a profile points at, without the host leaving
    /// BakaLoader: the Steam client is asked to do it for an install the client owns, and
    /// steamcmd does it for a standalone folder. One update at a time per install folder.
    /// </summary>
    public interface IServerUpdateService
    {
        /// <summary>Who owns this install. Pure and fast: no hashing, no network.</summary>
        ServerInstallInfo Classify(string serverExePath);

        /// <summary>True while an update is running for the folder this executable sits in.</summary>
        bool IsRunning(string serverExePath);

        /// <summary>
        /// Runs the update. Never throws: every outcome comes back as a result. The worlds
        /// backup is the caller's, so the same copy-aside the launch guard uses is the one
        /// that runs here; returning false from it stops the update before anything is written.
        /// </summary>
        Task<ServerUpdateResult> UpdateAsync(ServerUpdateRequest req, Func<Task<bool>> backupWorlds, CancellationToken ct);

        /// <summary>
        /// Asks a running update to stop, and answers whether that was honoured. Only a wait
        /// on the Steam client can be called off; a steamcmd run, a worlds backup and a verify
        /// pass all answer false and carry on, because a half written install is worse than a
        /// slow one.
        /// </summary>
        bool Cancel(string serverExePath);

        event EventHandler<ServerUpdateProgress> Progress;

        /// <summary>Fires once for every update that actually started, whether it worked or not.</summary>
        event EventHandler<ServerUpdateResult> Completed;
    }

    /// <summary>Reads the install. Split out so the update service can be tested without one on disk.</summary>
    public interface IServerBuildProbe
    {
        ServerBuildInfo Probe(string serverExePath);

        ServerInstallInfo Classify(string serverExePath);
    }

    /// <inheritdoc cref="IServerBuildProbe"/>
    public class ServerBuildProbe : IServerBuildProbe
    {
        public ServerBuildInfo Probe(string serverExePath) => ServerBuildTracker.Probe(serverExePath);

        public ServerInstallInfo Classify(string serverExePath) => ServerBuildTracker.ClassifyInstall(serverExePath);
    }

    /// <summary>Hands a steam:// link to the Steam client. Its own seam so tests never shell out.</summary>
    public interface ISteamHandoff
    {
        bool Open(string url);
    }

    /// <inheritdoc cref="ISteamHandoff"/>
    public class SteamHandoff : ISteamHandoff
    {
        public bool Open(string url)
        {
            try
            {
                using var started = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <inheritdoc cref="IServerUpdateService"/>
    public class ServerUpdateService : IServerUpdateService
    {
        /// <summary>Verify and download whatever is queued, without the host hunting through Steam.</summary>
        public const string SteamValidateUrl = "steam://validate/" + ServerBuildTracker.SteamAppId;

        /// <summary>Fallback for a Steam client that will not take a validate request.</summary>
        public const string SteamInstallUrl = "steam://install/" + ServerBuildTracker.SteamAppId;

        private const string UnknownInstallReason =
            "BakaLoader cannot tell how this server was installed, so it cannot update it for you. "
            + "Open Steam and update Valheim Dedicated Server there.";

        private const string AlreadyRunningReason = "An update is already running for this install.";

        /// <summary>The exact sentence the launch guard uses when a copy aside came back empty.</summary>
        private const string BackupFailedReason = "No worlds were copied aside, so the update was not started.";

        private const string CancelledReason = "The update was cancelled.";

        private readonly ConcurrentDictionary<string, Operation> Running =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ISteamCmdRunner SteamCmd;
        private readonly IServerBuildProbe Builds;
        private readonly ISteamHandoff Steam;
        private readonly IApplicationLogger Logger;

        public ServerUpdateService(
            ISteamCmdRunner steamCmd,
            IServerBuildProbe builds,
            ISteamHandoff steam,
            IApplicationLogger logger)
        {
            SteamCmd = steamCmd;
            Builds = builds;
            Steam = steam;
            Logger = logger;
        }

        public event EventHandler<ServerUpdateProgress> Progress;

        public event EventHandler<ServerUpdateResult> Completed;

        /// <summary>How often Steam's manifest is re-read while it downloads.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>How long the whole wait on Steam may take before BakaLoader gives up on it.</summary>
        public TimeSpan SteamTimeout { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>How long Steam may sit at the same byte count before BakaLoader gives up on it.</summary>
        public TimeSpan SteamStallTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>The wait between polls. Replaced in tests so a poll loop runs in milliseconds.</summary>
        public Func<TimeSpan, CancellationToken, Task> Wait { get; set; } =
            (span, ct) => Task.Delay(span, ct);

        /// <summary>The clock the timeouts read. Replaced in tests.</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public ServerInstallInfo Classify(string serverExePath) => Builds.Classify(serverExePath);

        public bool IsRunning(string serverExePath)
        {
            var key = InstallKey(serverExePath);
            return key != null && Running.ContainsKey(key);
        }

        public bool Cancel(string serverExePath)
        {
            var key = InstallKey(serverExePath);
            if (key == null || !Running.TryGetValue(key, out var op)) return false;

            // Cancelling means "stop watching Steam", which is safe at any moment: Steam carries
            // on with the download by itself. A steamcmd run is mid-write by definition, so it
            // is left alone however far along it says it is, and so is a backup and a verify
            // pass. Anything but the wait answers false and nothing happens.
            if (!op.Cancellable) return false;
            if (op.Phase != ServerUpdatePhase.AskingSteam && op.Phase != ServerUpdatePhase.Downloading) return false;

            op.CancelRequested = true;
            try { op.Cancellation.Cancel(); } catch { /* already gone */ }
            return true;
        }

        public async Task<ServerUpdateResult> UpdateAsync(
            ServerUpdateRequest req, Func<Task<bool>> backupWorlds, CancellationToken ct)
        {
            var result = new ServerUpdateResult
            {
                ProfileName = req?.ProfileName,
                StartAfter = req?.StartAfter ?? false,
            };

            if (req == null || string.IsNullOrWhiteSpace(req.ServerExePath))
            {
                result.Reason = "This profile has no server executable set, so there is nothing to update.";
                return result;
            }

            var install = Classify(req.ServerExePath) ?? new ServerInstallInfo { Kind = ServerInstallKind.Unknown };
            if (install.Kind == ServerInstallKind.Unknown || string.IsNullOrWhiteSpace(install.InstallDir))
            {
                result.Reason = UnknownInstallReason;
                return result;
            }

            var key = InstallKey(req.ServerExePath);
            var op = new Operation
            {
                ProfileName = req.ProfileName,
                Phase = ServerUpdatePhase.Idle,

                // Deliberately NOT linked to the caller's token. Everything that writes into
                // the install folder, and the worlds backup that has to finish before it, runs
                // to the end whatever the caller does: a cancelled steamcmd keeps writing while
                // BakaLoader thinks the update failed and lets go of the per-install lock, so a
                // second run could start on top of it. The caller's token is linked in for the
                // one phase where stopping is safe, which is the wait on the Steam client.
                Cancellation = new CancellationTokenSource(),
                CallerToken = ct,
            };

            // One update per install folder, so two profiles pointed at the same server cannot
            // both drive steamcmd through the same files.
            if (key == null || !Running.TryAdd(key, op))
            {
                op.Cancellation.Dispose();
                result.Reason = AlreadyRunningReason;
                return result;
            }

            try
            {
                await RunAsync(req, install, backupWorlds, op, result).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Server update: the update failed unexpectedly.");
                Fail(op, result, "The update stopped unexpectedly: " + FirstLine(e.Message));
            }
            finally
            {
                Running.TryRemove(key, out _);
                op.Cancellation.Dispose();
            }

            Completed?.Invoke(this, result);
            return result;
        }

        // ------------------------------------------------------------------ the operation

        private async Task RunAsync(
            ServerUpdateRequest req,
            ServerInstallInfo install,
            Func<Task<bool>> backupWorlds,
            Operation op,
            ServerUpdateResult result)
        {
            if (req.BackupWorldsFirst)
            {
                Report(op, ServerUpdatePhase.BackingUp, -1, 0, 0, null, "Backing up worlds");

                bool copied;
                try
                {
                    copied = backupWorlds == null || await backupWorlds().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Server update: the worlds backup threw, so the update was not started.");
                    copied = false;
                }

                // Nothing is written to the install until the worlds are safely copied aside.
                if (!copied)
                {
                    Fail(op, result, BackupFailedReason);
                    return;
                }
            }

            if (op.CancelRequested)
            {
                Cancelled(op, result);
                return;
            }

            bool updated;
            switch (install.Kind)
            {
                case ServerInstallKind.SteamLibrary:
                    updated = await UpdateThroughSteamAsync(req, op, result).ConfigureAwait(false);
                    break;

                case ServerInstallKind.Standalone:
                    updated = await UpdateThroughSteamCmdAsync(req, install, op, result).ConfigureAwait(false);
                    break;

                default:
                    // Only a folder BakaLoader is certain nobody else owns is handed to
                    // steamcmd, so an install kind that is not one of the two above stops here.
                    Fail(op, result, UnknownInstallReason);
                    return;
            }

            if (!updated) return;

            var after = SafeProbe(req.ServerExePath);
            result.Ok = true;
            result.BuildId = after?.BuildId ?? after?.Identity;
            Report(
                op, ServerUpdatePhase.Finished, 100, 0, 0, null,
                req.StartAfter ? "Update finished. Starting the server." : "Update finished.");
        }

        /// <summary>
        /// The Steam client owns this folder, so it does the work: BakaLoader asks it to verify
        /// the app, then watches the manifest until Steam says the install is whole again.
        /// </summary>
        private async Task<bool> UpdateThroughSteamAsync(
            ServerUpdateRequest req, Operation op, ServerUpdateResult result)
        {
            // Only the wait on Steam can be called off, and only while it is still a wait.
            // This is also the one phase the caller's own token is allowed to reach: stopping
            // here writes nothing and leaves nothing half done, because Steam owns the folder.
            op.Cancellable = true;
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(
                op.Cancellation.Token, op.CallerToken);
            Report(op, ServerUpdatePhase.AskingSteam, -1, 0, 0, null, "Asking Steam to download the update");

            if (!Steam.Open(SteamValidateUrl) && !Steam.Open(SteamInstallUrl))
            {
                Fail(op, result, "Steam did not open, so the update could not be started. Open Steam and update Valheim Dedicated Server there.");
                return false;
            }

            var startedAt = UtcNow();
            var lastChange = startedAt;
            var lastBytes = -1L;
            var lastStaged = -1L;
            var lastFlags = long.MinValue;
            var sawDownload = false;

            while (true)
            {
                if (op.CancelRequested)
                {
                    Cancelled(op, result);
                    return false;
                }

                try
                {
                    await Wait(PollInterval, waiting.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Cancelled(op, result);
                    return false;
                }

                var info = SafeProbe(req.ServerExePath);

                // A manifest Steam is part way through rewriting cannot be read, and reads as
                // a fingerprint rather than a build. That is "still working", never "finished".
                var readable = info != null && info.Source == ServerBuildSource.Manifest;

                if (readable && !info.UpdatePending && info.StateFlags == SteamInstalledStateFlags)
                {
                    Report(op, ServerUpdatePhase.Verifying, 100, info.BytesDownloaded, info.BytesToDownload, null, "Verifying the install");
                    return true;
                }

                if (readable)
                {
                    var done = info.BytesToDownload > 0
                        ? Math.Min(info.BytesDownloaded, info.BytesToDownload)
                        : info.BytesDownloaded;

                    // Steam moves three counters, not one: the download, the staging pass it
                    // runs after the last byte lands, and its own StateFlags. Any of them
                    // moving means it is still working, so all three reset the stall clock.
                    if (done != lastBytes || info.BytesStaged != lastStaged || info.StateFlags != lastFlags)
                    {
                        lastBytes = done;
                        lastStaged = info.BytesStaged;
                        lastFlags = info.StateFlags;
                        lastChange = UtcNow();
                    }

                    sawDownload = sawDownload || done > 0 || info.BytesStaged > 0;

                    if (info.BytesToDownload > 0)
                    {
                        var percent = (int)Math.Round(100.0 * done / info.BytesToDownload);
                        Report(
                            op, ServerUpdatePhase.Downloading, Clamp(percent), done, info.BytesToDownload, null,
                            "Steam is downloading: " + Megabytes(done) + " of " + Megabytes(info.BytesToDownload) + " MB");
                    }
                }

                var now = UtcNow();
                if (now - startedAt > SteamTimeout)
                {
                    Fail(
                        op, result,
                        sawDownload
                            ? "Steam did not finish the update in " + (int)SteamTimeout.TotalMinutes + " minutes. Open Steam to check."
                            : "Steam has not started the download. Open Steam to check.");
                    return false;
                }

                // The stall clock only runs once bytes have actually moved. Before that, a
                // download can legitimately sit at zero for a long time: Steam queues it behind
                // another one, or spends the first minutes allocating the file on disk. Only
                // the overall timeout limits that wait.
                if (sawDownload && now - lastChange > SteamStallTimeout)
                {
                    Fail(op, result, "Steam stopped downloading part way through. Open Steam to check.");
                    return false;
                }
            }
        }

        /// <summary>
        /// A folder nobody else owns, so steamcmd does the work here. Its output is the only
        /// progress there is, so every line is parsed and passed on.
        /// </summary>
        private async Task<bool> UpdateThroughSteamCmdAsync(
            ServerUpdateRequest req, ServerInstallInfo install, Operation op, ServerUpdateResult result)
        {
            Report(op, ServerUpdatePhase.RunningSteamCmd, -1, 0, 0, null, "Running steamcmd");

            // Nothing on this path takes a token that anyone can fire. steamcmd is replacing
            // the files of a real install, so the only safe end to the run is steamcmd's own,
            // and the per-install lock is held until it has had it.
            var exe = await SteamCmd.EnsureInstalledAsync(CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(exe))
            {
                Fail(op, result, "steamcmd could not be set up, so the update did not run.");
                return false;
            }

            // A steamcmd that has just been unpacked updates itself and quits before it ever
            // reads the script it was handed, so that run is done again rather than reported.
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var run = new RunTally();
                var exit = await SteamCmd
                    .RunAsync(exe, install.InstallDir, line => OnSteamCmdLine(op, line, run), CancellationToken.None)
                    .ConfigureAwait(false);

                if (run.Success) return true;

                var selfUpdated = exit == 0 && !run.SawUpdateState && !run.Failed;
                if (selfUpdated && attempt == 1)
                {
                    Logger.Information("Server update: steamcmd updated itself and exited, so it is being run again.");
                    continue;
                }

                Fail(
                    op, result,
                    !string.IsNullOrWhiteSpace(run.LastLine)
                        ? run.LastLine
                        : "steamcmd stopped with exit code " + exit.ToString(CultureInfo.InvariantCulture) + ".");
                return false;
            }

            Fail(op, result, "steamcmd did not run the update.");
            return false;
        }

        private void OnSteamCmdLine(Operation op, string line, RunTally run)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            run.LastLine = line.Trim();
            if (SteamCmdOutput.MentionsUpdateState(line)) run.SawUpdateState = true;

            Logger.Information("[steamcmd] {0}", run.LastLine);

            var read = SteamCmdOutput.Read(line);
            switch (read.Signal)
            {
                case SteamCmdSignal.Success:
                    run.Success = true;
                    break;

                case SteamCmdSignal.Error:
                    run.Failed = true;
                    break;

                case SteamCmdSignal.Progress:
                    var percent = read.Percent >= 0
                        ? Clamp((int)Math.Round(read.Percent))
                        : -1;
                    Report(
                        op,
                        // Never "Downloading" here. That phase is the one the host is offered a
                        // stop button for, and a steamcmd run must never be offered one: it is
                        // writing into the install folder from its first byte to its last.
                        read.IsVerifying ? ServerUpdatePhase.Verifying : ServerUpdatePhase.RunningSteamCmd,
                        percent,
                        read.BytesDone,
                        read.BytesTotal,
                        run.LastLine,
                        read.IsVerifying
                            ? "Verifying the install"
                            : "Steam is downloading: " + Megabytes(read.BytesDone) + " of " + Megabytes(read.BytesTotal) + " MB");
                    break;

                default:
                    // Chatter. It is already in the log, and the phase has not moved.
                    break;
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Steam's "installed, nothing queued" StateFlags value.</summary>
        private const long SteamInstalledStateFlags = 4;

        private void Report(
            Operation op, ServerUpdatePhase phase, int percent, long done, long total, string line, string message)
        {
            op.Phase = phase;
            Progress?.Invoke(this, new ServerUpdateProgress
            {
                ProfileName = op.ProfileName,
                Phase = phase,
                Percent = percent,
                BytesDone = done,
                BytesTotal = total,
                Line = line,
                Message = message,
            });
        }

        private void Fail(Operation op, ServerUpdateResult result, string reason)
        {
            result.Ok = false;
            result.Reason = reason;
            Report(op, ServerUpdatePhase.Failed, -1, 0, 0, null, reason);
        }

        private void Cancelled(Operation op, ServerUpdateResult result)
        {
            result.Ok = false;
            result.Cancelled = true;
            result.Reason = CancelledReason;
            Report(op, ServerUpdatePhase.Cancelled, -1, 0, 0, null, CancelledReason);
        }

        private ServerBuildInfo SafeProbe(string serverExePath)
        {
            try { return Builds.Probe(serverExePath); }
            catch (Exception e)
            {
                Logger.Debug("Server update: the install could not be probed: {0}", e.Message);
                return null;
            }
        }

        /// <summary>The install folder, normalised, which is what one-at-a-time is keyed on.</summary>
        private static string InstallKey(string serverExePath)
        {
            if (string.IsNullOrWhiteSpace(serverExePath)) return null;
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(serverExePath));
                return dir?.TrimEnd('\\', '/');
            }
            catch
            {
                return null;
            }
        }

        private static int Clamp(int percent) => percent < 0 ? 0 : percent > 100 ? 100 : percent;

        private static string Megabytes(long bytes)
            => Math.Max(0, bytes / 1048576).ToString(CultureInfo.InvariantCulture);

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "no reason given";
            var cut = text.IndexOf('\n');
            return (cut < 0 ? text : text[..cut]).Trim();
        }

        /// <summary>One running update, so a second click and a cancel both know what to do.</summary>
        private sealed class Operation
        {
            public string ProfileName;

            /// <summary>The service's own stop signal. Only Cancel ever fires it.</summary>
            public CancellationTokenSource Cancellation;

            /// <summary>
            /// The token the caller handed in. Honoured only while BakaLoader is watching
            /// Steam, and never joined to anything that writes into the install.
            /// </summary>
            public CancellationToken CallerToken;

            private int PhaseValue;

            public ServerUpdatePhase Phase
            {
                get => (ServerUpdatePhase)Volatile.Read(ref PhaseValue);
                set => Volatile.Write(ref PhaseValue, (int)value);
            }

            private int CancellableValue;

            /// <summary>True only while the operation is a wait that can be walked away from.</summary>
            public bool Cancellable
            {
                get => Volatile.Read(ref CancellableValue) != 0;
                set => Volatile.Write(ref CancellableValue, value ? 1 : 0);
            }

            private int CancelValue;

            public bool CancelRequested
            {
                get => Volatile.Read(ref CancelValue) != 0;
                set => Volatile.Write(ref CancelValue, value ? 1 : 0);
            }
        }

        /// <summary>What one steamcmd run said, gathered as its lines arrive.</summary>
        private sealed class RunTally
        {
            public bool Success;

            public bool Failed;

            public bool SawUpdateState;

            public string LastLine;
        }
    }
}
