using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Processes;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Proves the update orchestration without an install, a Steam client, or steamcmd on
    /// disk: the worlds go first, one update runs at a time, a copy aside that came back
    /// empty stops everything, a fresh steamcmd that only updated itself is run again, and a
    /// manifest Steam is part way through writing is never mistaken for a finished download.
    /// </summary>
    public class ServerUpdateServiceTests
    {
        private const string ExePath = @"C:\servers\vds10\valheim_server.exe";
        private const string InstallDir = @"C:\servers\vds10";

        // ------------------------------------------------------------------ steamcmd path

        [Fact]
        public async Task The_worlds_are_copied_aside_before_steamcmd_touches_the_install()
        {
            var order = new List<string>();
            var runner = Runner(Success);
            runner.OnRun = () => order.Add("steamcmd");

            var service = Service(runner, Standalone());
            service.Progress += (_, p) => order.Add("phase:" + p.Phase);
            service.Completed += (_, r) => order.Add("completed:" + r.Ok);

            var result = await service.UpdateAsync(
                Request(backup: true, startAfter: true),
                () => { order.Add("backup"); return Task.FromResult(true); },
                CancellationToken.None);

            Assert.True(result.Ok);
            Assert.True(result.StartAfter);
            Assert.Null(result.Reason);

            Assert.Equal("phase:BackingUp", order[0]);
            Assert.Equal("backup", order[1]);
            Assert.True(order.IndexOf("steamcmd") > order.IndexOf("backup"));
            Assert.Equal("completed:True", order[^1]);
            Assert.Equal(InstallDir, runner.InstallDirs.Single());
        }

        [Fact]
        public async Task A_copy_aside_that_came_back_empty_stops_the_update_before_it_starts()
        {
            var runner = Runner(Success);
            var service = Service(runner, Standalone());
            var completions = new List<ServerUpdateResult>();
            service.Completed += (_, r) => completions.Add(r);

            var result = await service.UpdateAsync(
                Request(backup: true), () => Task.FromResult(false), CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal("No worlds were copied aside, so the update was not started.", result.Reason);
            Assert.Equal(0, runner.Runs);
            Assert.Equal(result.Reason, Assert.Single(completions).Reason);
        }

        [Fact]
        public async Task A_second_update_for_the_same_install_is_refused_while_one_runs()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = Runner(Success);
            var service = Service(runner, Standalone());
            var completions = 0;
            service.Completed += (_, _) => Interlocked.Increment(ref completions);

            var first = service.UpdateAsync(Request(backup: true), () => gate.Task, CancellationToken.None);

            // The first update is parked inside its worlds backup.
            Assert.True(SpinUntil(() => service.IsRunning(ExePath)));

            var second = await service.UpdateAsync(Request(backup: true), () => Task.FromResult(true), CancellationToken.None);

            Assert.False(second.Ok);
            Assert.Equal("An update is already running for this install.", second.Reason);
            Assert.Equal(0, completions); // a refusal is not an update that ended

            gate.SetResult(true);
            Assert.True((await first).Ok);
            Assert.Equal(1, runner.Runs);
            Assert.Equal(1, completions);
            Assert.False(service.IsRunning(ExePath));
        }

        [Fact]
        public async Task An_install_BakaLoader_cannot_place_is_refused_with_the_Steam_advice()
        {
            var runner = Runner(Success);
            var service = Service(runner, new ServerInstallInfo
            {
                Kind = ServerInstallKind.Unknown,
                InstallDir = InstallDir,
                Reason = "no manifest",
            });

            var result = await service.UpdateAsync(Request(), () => Task.FromResult(true), CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(
                "BakaLoader cannot tell how this server was installed, so it cannot update it for you. "
                + "Open Steam and update Valheim Dedicated Server there.",
                result.Reason);
            Assert.Equal(0, runner.Runs);
        }

        [Fact]
        public async Task A_fresh_steamcmd_that_only_updated_itself_is_run_again()
        {
            // First run: a brand new steamcmd bootstraps itself and quits with nothing to say.
            var runner = Runner(new[] { Array.Empty<string>(), Success }, new[] { 0, 0 });
            var service = Service(runner, Standalone());
            var progress = new List<ServerUpdateProgress>();
            service.Progress += (_, p) => progress.Add(p);

            var result = await service.UpdateAsync(Request(), () => Task.FromResult(true), CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Equal(2, runner.Runs);

            // A steamcmd run reports RunningSteamCmd throughout, never Downloading: that is
            // the phase the bar offers a stop button for, and this run is writing files.
            var downloading = progress.Single(
                p => p.Phase == ServerUpdatePhase.RunningSteamCmd && p.BytesTotal > 0);
            Assert.Equal(50, downloading.Percent);
            Assert.Equal(444467344L, downloading.BytesDone);
            Assert.Equal(888934688L, downloading.BytesTotal);
            Assert.Equal("Steam is downloading: 423 of 847 MB", downloading.Message);
            Assert.DoesNotContain(progress, p => p.Phase == ServerUpdatePhase.Downloading);
        }

        [Fact]
        public async Task A_steamcmd_that_gave_up_fails_the_update_with_the_line_it_gave_up_on()
        {
            var runner = Runner(
                new[]
                {
                    "Update state (0x61) downloading, progress: 3.00 (10 / 100)",
                    "Error! App '896660' state is 0x202 after update job.",
                },
                exitCode: 8);
            var service = Service(runner, Standalone());

            var result = await service.UpdateAsync(Request(), () => Task.FromResult(true), CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal("Error! App '896660' state is 0x202 after update job.", result.Reason);
            Assert.Equal(1, runner.Runs); // a run that spoke is never repeated
        }

        [Fact]
        public async Task A_steamcmd_run_is_never_called_off_part_way_through()
        {
            var runner = Runner(Success);
            var service = Service(runner, Standalone());

            // Cancel while steamcmd is mid-download: a half written install is worse than
            // waiting, so the run finishes.
            runner.OnLineSent = line => service.Cancel(ExePath);

            var result = await service.UpdateAsync(Request(), () => Task.FromResult(true), CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Null(result.Reason);
        }

        // ------------------------------------------------------------------ Steam library path

        [Fact]
        public async Task Steam_is_asked_to_verify_and_watched_until_the_manifest_says_it_is_whole()
        {
            var steam = new FakeSteam();
            var probe = new FakeProbe { Install = SteamLibrary() };
            probe.Reads.Enqueue(Manifest(pending: true, done: 100, total: 800));
            probe.Reads.Enqueue(Manifest(pending: true, done: 400, total: 800));
            probe.Reads.Enqueue(Manifest(pending: false, done: 800, total: 800, stateFlags: 4));

            var runner = Runner(Success);
            var service = Service(runner, probe, steam);
            var progress = new List<ServerUpdateProgress>();
            service.Progress += (_, p) => progress.Add(p);

            var result = await service.UpdateAsync(Request(), () => Task.FromResult(true), CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Equal("25185644", result.BuildId);
            Assert.Equal("steam://validate/896660", steam.Opened.First());
            Assert.Equal(0, runner.Runs); // steamcmd never runs against a Steam library
            Assert.Contains(progress, p => p.Phase == ServerUpdatePhase.AskingSteam);
            Assert.Contains(progress, p => p.Phase == ServerUpdatePhase.Downloading && p.Percent == 50);
            Assert.Equal(ServerUpdatePhase.Finished, progress[^1].Phase);
        }

        [Fact]
        public async Task A_manifest_that_cannot_be_read_counts_as_still_writing_never_as_finished()
        {
            var probe = new FakeProbe { Install = SteamLibrary() };

            // What a locked, part written .acf probes as: no manifest identity at all. It has
            // nothing pending either, which is exactly why it must not be read as "finished".
            for (var i = 0; i < 200; i++)
            {
                probe.Reads.Enqueue(new ServerBuildInfo
                {
                    Fingerprint = new string('a', 64),
                    Source = ServerBuildSource.Fingerprint,
                    ManifestPath = @"C:\servers\vds10\steamapps\appmanifest_896660.acf",
                });
            }

            var service = Service(Runner(Success), probe, new FakeSteam());

            var result = await service.UpdateAsync(Request(), () => Task.FromResult(true), CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal("Steam has not started the download. Open Steam to check.", result.Reason);
        }

        [Fact]
        public async Task Waiting_on_Steam_can_be_called_off()
        {
            var probe = new FakeProbe { Install = SteamLibrary() };
            for (var i = 0; i < 50; i++) probe.Reads.Enqueue(Manifest(pending: true, done: 0, total: 800));

            var service = Service(Runner(Success), probe, new FakeSteam());
            var completions = new List<ServerUpdateResult>();
            service.Completed += (_, r) => completions.Add(r);

            // Walk away on the first wait, while BakaLoader is still only watching.
            var first = true;
            service.Wait = (span, ct) =>
            {
                if (first)
                {
                    first = false;
                    service.Cancel(ExePath);
                }

                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            };

            var result = await service.UpdateAsync(Request(), () => Task.FromResult(true), CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal("The update was cancelled.", result.Reason);
            Assert.Equal("The update was cancelled.", Assert.Single(completions).Reason);
        }

        [Fact]
        public async Task A_steamcmd_run_never_hears_the_callers_token_and_keeps_the_install_locked()
        {
            // The bridge hands UpdateAsync a token it cancels when the host presses stop. If
            // that token reached the steamcmd run, the wait would be abandoned, the update
            // reported failed and the per-install lock dropped, all while steamcmd carried on
            // writing gigabytes into the install folder: the next click would start a SECOND
            // steamcmd on the same files.
            var caller = new CancellationTokenSource();
            var runner = Runner(Success);
            var service = Service(runner, Standalone());
            var progress = new List<ServerUpdateProgress>();
            service.Progress += (_, p) => progress.Add(p);

            var stopAnswers = new List<bool>();
            var lockedWhileRunning = false;
            runner.OnLineSent = _ =>
            {
                caller.Cancel();                              // the host presses stop
                stopAnswers.Add(service.Cancel(ExePath));     // and so does the bar
                lockedWhileRunning = service.IsRunning(ExePath);
            };

            var result = await service.UpdateAsync(
                Request(), () => Task.FromResult(true), caller.Token);

            // The run finished on its own terms.
            Assert.True(result.Ok);
            Assert.Null(result.Reason);
            Assert.False(result.Cancelled);
            Assert.Equal(1, runner.Runs);

            // Nothing anyone did reached the token the run was given.
            Assert.False(runner.LastToken.IsCancellationRequested);
            Assert.False(runner.LastToken.CanBeCanceled);

            // Cancel refused, every time, and the lock was still held while it did.
            Assert.NotEmpty(stopAnswers);
            Assert.All(stopAnswers, answered => Assert.False(answered));
            Assert.True(lockedWhileRunning);
            Assert.False(service.IsRunning(ExePath));   // and released once steamcmd exited

            // The bar is never offered a stop button for a run that writes files.
            Assert.DoesNotContain(progress, p => p.Phase == ServerUpdatePhase.Downloading);
        }

        [Fact]
        public async Task A_backup_cannot_be_called_off_either()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = Runner(Success);
            var service = Service(runner, Standalone());

            var backing = service.UpdateAsync(Request(backup: true), () => gate.Task, CancellationToken.None);
            Assert.True(SpinUntil(() => service.IsRunning(ExePath)));

            // Mid backup: nothing to stop, because the copy aside has to finish before the
            // install is touched at all.
            Assert.False(service.Cancel(ExePath));

            gate.SetResult(true);
            var result = await backing;
            Assert.True(result.Ok);
            Assert.False(result.Cancelled);
        }

        [Fact]
        public async Task A_download_that_has_not_started_is_given_the_whole_thirty_minutes()
        {
            // Steam queues a download behind another one, or spends the first minutes
            // allocating the file. Nothing has stalled: nothing has begun. Failing at five
            // minutes drops the launch hold and posts a failure while Steam is still working.
            var probe = new FakeProbe { Install = SteamLibrary() };
            for (var i = 0; i < 300; i++) probe.Reads.Enqueue(Manifest(pending: true, done: 0, total: 800));
            probe.Reads.Enqueue(Manifest(pending: true, done: 400, total: 800));
            probe.Reads.Enqueue(Manifest(pending: false, done: 800, total: 800, stateFlags: 4));

            var service = Service(Runner(Success), probe, new FakeSteam());

            var result = await service.UpdateAsync(Request(), () => Task.FromResult(true), CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Null(result.Reason);
        }

        [Fact]
        public async Task Steam_staging_the_files_it_fetched_is_not_a_stalled_download()
        {
            // After the last byte lands, BytesDownloaded stops moving for the whole staging
            // pass. Watching that one counter alone reads a working Steam as a stopped one.
            var probe = new FakeProbe { Install = SteamLibrary() };
            for (var i = 0; i < 300; i++)
            {
                probe.Reads.Enqueue(Manifest(pending: true, done: 800, total: 800, staged: 1000 + i));
            }

            probe.Reads.Enqueue(Manifest(pending: false, done: 800, total: 800, stateFlags: 4));

            var service = Service(Runner(Success), probe, new FakeSteam());

            var result = await service.UpdateAsync(Request(), () => Task.FromResult(true), CancellationToken.None);

            Assert.True(result.Ok);
        }

        [Fact]
        public async Task A_download_that_stopped_part_way_through_is_still_given_up_on()
        {
            // The other side of the same rule: bytes moved and then stopped, which is a stall.
            var probe = new FakeProbe { Install = SteamLibrary() };
            for (var i = 0; i < 400; i++) probe.Reads.Enqueue(Manifest(pending: true, done: 400, total: 800));

            var service = Service(Runner(Success), probe, new FakeSteam());

            var result = await service.UpdateAsync(Request(), () => Task.FromResult(true), CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal("Steam stopped downloading part way through. Open Steam to check.", result.Reason);
            Assert.False(result.Cancelled);
        }

        [Fact]
        public async Task A_wait_that_was_called_off_says_so_on_the_result()
        {
            var probe = new FakeProbe { Install = SteamLibrary() };
            for (var i = 0; i < 50; i++) probe.Reads.Enqueue(Manifest(pending: true, done: 0, total: 800));

            var service = Service(Runner(Success), probe, new FakeSteam());

            var answers = new List<bool>();
            var first = true;
            service.Wait = (span, ct) =>
            {
                if (first)
                {
                    first = false;
                    answers.Add(service.Cancel(ExePath));
                }

                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            };

            var result = await service.UpdateAsync(Request(), () => Task.FromResult(true), CancellationToken.None);

            // The bar needs to tell "the host stopped watching" from "the update broke", so a
            // cancelled operation is flagged rather than read out of the reason text.
            Assert.True(Assert.Single(answers));
            Assert.True(result.Cancelled);
            Assert.False(result.Ok);
        }

        // ------------------------------------------------------------------ the real runner

        [Fact]
        public async Task Steamcmd_runs_from_its_own_folder_rather_than_BakaLoaders()
        {
            // steamcmd unpacks the Steam client next to its WORKING directory. Started with
            // BakaLoader's own folder inherited, it scatters tens of megabytes of client files
            // through it, and fails outright when that folder is not writable.
            var processes = new RecordingProcesses();
            var runner = new SteamCmdRunner(
                new Mock<IHttpClientProvider>().Object, processes, new Mock<IApplicationLogger>().Object);

            var exe = Path.Combine(Path.GetTempPath(), "vbl-steamcmd-home", "steamcmd.exe");
            await runner.RunAsync(exe, InstallDir, _ => { }, CancellationToken.None);

            var started = Assert.Single(processes.Built);
            Assert.Equal(Path.GetDirectoryName(exe), started.StartInfo.WorkingDirectory);
        }

        [Fact]
        public async Task Steamcmd_is_not_tied_to_the_job_object_that_kills_children_on_close()
        {
            // Every other child goes through StartIO, which puts it in a job object with
            // kill-on-close: the instant BakaLoader's process ends, Windows terminates it.
            // Doing that to a steamcmd part way through replacing valheim_server.exe leaves
            // the install a mix of two builds, so this one process starts outside the job.
            var processes = new RecordingProcesses();
            var runner = new SteamCmdRunner(
                new Mock<IHttpClientProvider>().Object, processes, new Mock<IApplicationLogger>().Object);

            await runner.RunAsync(
                Path.Combine(Path.GetTempPath(), "vbl-steamcmd-home", "steamcmd.exe"),
                InstallDir,
                _ => { },
                CancellationToken.None);

            Assert.Equal(0, processes.StartIoCalls);
        }

        [Fact]
        public void A_binary_carrying_Valves_certificate_is_refused_when_the_signature_does_not_verify()
        {
            // A certificate table is public data. Copied onto a modified binary it still reads
            // as "Valve", because reading it never re-hashes the file. Only Windows saying the
            // signature covers these bytes counts.
            Assert.False(SteamCmdRunner.IsTrustedValveBinary(
                @"C:\fake\steamcmd.exe", _ => false, _ => "CN=Valve Corp., O=Valve Corp."));

            // A signature that verifies but names somebody else is no better.
            Assert.False(SteamCmdRunner.IsTrustedValveBinary(
                @"C:\fake\steamcmd.exe", _ => true, _ => "CN=Somebody Else"));

            // No signature to read at all.
            Assert.False(SteamCmdRunner.IsTrustedValveBinary(
                @"C:\fake\steamcmd.exe", _ => true, _ => null));

            // Both halves true is the only way through.
            Assert.True(SteamCmdRunner.IsTrustedValveBinary(
                @"C:\fake\steamcmd.exe", _ => true, _ => "CN=Valve Corp., O=Valve Corp."));
        }

        // ------------------------------------------------------------------ the line parser

        [Fact]
        public void A_progress_line_carries_the_state_the_percentage_and_the_bytes()
        {
            var read = SteamCmdOutput.Read(
                " Update state (0x61) downloading, progress: 12.34 (110000000 / 888934688)");

            Assert.Equal(SteamCmdSignal.Progress, read.Signal);
            Assert.Equal("downloading", read.State);
            Assert.Equal(12.34, read.Percent, 2);
            Assert.Equal(110000000L, read.BytesDone);
            Assert.Equal(888934688L, read.BytesTotal);
            Assert.False(read.IsVerifying);
        }

        [Fact]
        public void A_verifying_line_is_told_apart_from_a_downloading_one()
        {
            var read = SteamCmdOutput.Read("Update state (0x81) verifying update, progress: 55.00 (5 / 9)");

            Assert.Equal(SteamCmdSignal.Progress, read.Signal);
            Assert.True(read.IsVerifying);
        }

        [Fact]
        public void An_install_that_was_already_current_counts_as_a_success()
        {
            Assert.Equal(
                SteamCmdSignal.Success,
                SteamCmdOutput.Read("Success! App '896660' fully installed.").Signal);

            Assert.Equal(
                SteamCmdSignal.Success,
                SteamCmdOutput.Read("Success! App '896660' already up to date.").Signal);
        }

        [Fact]
        public void A_failure_line_and_ordinary_chatter_are_told_apart()
        {
            Assert.Equal(
                SteamCmdSignal.Error,
                SteamCmdOutput.Read("Error! App '896660' state is 0x202 after update job.").Signal);

            var chatter = SteamCmdOutput.Read("Logging in user 'anonymous' to Steam Public...");
            Assert.Equal(SteamCmdSignal.None, chatter.Signal);
            Assert.Equal(-1d, chatter.Percent);

            // The self-update run prints neither of the two lines that decide anything.
            Assert.False(SteamCmdOutput.MentionsUpdateState("Redirecting stderr to steamcmd/logs/stderr.txt"));
            Assert.True(SteamCmdOutput.MentionsUpdateState("Update state (0x61) downloading, progress: 1.00 (1 / 2)"));
        }

        // ------------------------------------------------------------------ helpers

        private static readonly string[] Success =
        {
            "Logging in user 'anonymous' to Steam Public...",
            "Update state (0x61) downloading, progress: 50.00 (444467344 / 888934688)",
            "Success! App '896660' fully installed.",
        };

        private static ServerUpdateRequest Request(bool backup = false, bool startAfter = false)
            => new()
            {
                ProfileName = "Final Sunset",
                ServerExePath = ExePath,
                SaveDataFolder = @"C:\saves",
                BackupWorldsFirst = backup,
                StartAfter = startAfter,
            };

        private static ServerInstallInfo Standalone()
            => new() { Kind = ServerInstallKind.Standalone, InstallDir = InstallDir };

        private static ServerInstallInfo SteamLibrary()
            => new() { Kind = ServerInstallKind.SteamLibrary, InstallDir = InstallDir, LibraryRoot = @"D:\steamlibrary" };

        private static ServerBuildInfo Manifest(
            bool pending, long done, long total, long stateFlags = 6, long staged = 0)
            => new()
            {
                BuildId = "25185644",
                TargetBuildId = "25185644",
                UpdatePending = pending,
                PendingBytes = pending ? total - done : 0,
                BytesDownloaded = done,
                BytesToDownload = total,
                BytesToStage = staged > 0 ? total : 0,
                BytesStaged = staged,
                StateFlags = stateFlags,
                Source = ServerBuildSource.Manifest,
            };

        private static FakeRunner Runner(string[] lines, int exitCode = 0)
            => Runner(new[] { lines }, new[] { exitCode });

        private static FakeRunner Runner(string[][] scripts, int[] exitCodes)
            => new() { Scripts = scripts, ExitCodes = exitCodes };

        private static ServerUpdateService Service(FakeRunner runner, ServerInstallInfo install)
            => Service(runner, new FakeProbe { Install = install }, new FakeSteam());

        private static ServerUpdateService Service(FakeRunner runner, FakeProbe probe, FakeSteam steam)
        {
            var now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            var service = new ServerUpdateService(runner, probe, steam, new Mock<IApplicationLogger>().Object)
            {
                UtcNow = () => now,
            };

            // The poll loop runs on a clock the test owns, so a five minute stall is instant.
            service.Wait = (span, ct) =>
            {
                now = now.Add(span);
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            };

            return service;
        }

        private static bool SpinUntil(Func<bool> condition)
            => SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));

        /// <summary>A steamcmd that never leaves the test: it replays scripted output instead.</summary>
        private sealed class FakeRunner : ISteamCmdRunner
        {
            public string[][] Scripts = { Array.Empty<string>() };

            public int[] ExitCodes = { 0 };

            public int Runs;

            public readonly List<string> InstallDirs = new();

            public Action OnRun;

            public Action<string> OnLineSent;

            public string SteamCmdPath => @"C:\fake\steamcmd\steamcmd.exe";

            public Task<string> EnsureInstalledAsync(CancellationToken ct)
                => Task.FromResult(SteamCmdPath);

            /// <summary>The token the service handed the run. Nothing may ever cancel it.</summary>
            public CancellationToken LastToken;

            public Task<int> RunAsync(string steamCmdPath, string installDir, Action<string> onLine, CancellationToken ct)
            {
                LastToken = ct;
                var attempt = Runs++;
                InstallDirs.Add(installDir);
                OnRun?.Invoke();

                foreach (var line in Scripts[Math.Min(attempt, Scripts.Length - 1)])
                {
                    onLine(line);
                    OnLineSent?.Invoke(line);
                }

                return Task.FromResult(ExitCodes[Math.Min(attempt, ExitCodes.Length - 1)]);
            }
        }

        /// <summary>A tracker that hands out scripted manifest reads.</summary>
        private sealed class FakeProbe : IServerBuildProbe
        {
            public ServerInstallInfo Install = Standalone();

            public readonly Queue<ServerBuildInfo> Reads = new();

            private ServerBuildInfo Last = new()
            {
                BuildId = "25185644",
                Source = ServerBuildSource.Manifest,
                StateFlags = 4,
            };

            public ServerBuildInfo Probe(string serverExePath)
            {
                if (Reads.Count > 0) Last = Reads.Dequeue();
                return Last;
            }

            public ServerInstallInfo Classify(string serverExePath) => Install;
        }

        /// <summary>
        /// A process provider that hands back real un-started Process objects and writes down
        /// what was done with them, so the way steamcmd is launched can be inspected without
        /// launching anything.
        /// </summary>
        private sealed class RecordingProcesses : IProcessProvider
        {
            private readonly ConcurrentDictionary<string, Process> Registry = new();

            public readonly List<Process> Built = new();

            public int StartIoCalls;

            public void AddProcess(string key, Process process) => Registry[key] = process;

            public Process GetProcess(string key) => Registry.TryGetValue(key, out var p) ? p : null;

            public Process AddBackgroundProcess(string key, string command, string args)
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                };

                AddProcess(key, process);
                Built.Add(process);
                return process;
            }

            public void StartIO(Process process) => Interlocked.Increment(ref StartIoCalls);

            public void SafelyKillProcess(string key) { }
        }

        /// <summary>A Steam client that only writes down what it was asked to open.</summary>
        private sealed class FakeSteam : ISteamHandoff
        {
            public readonly List<string> Opened = new();

            public bool Answer = true;

            public bool Open(string url)
            {
                Opened.Add(url);
                return Answer;
            }
        }
    }
}
