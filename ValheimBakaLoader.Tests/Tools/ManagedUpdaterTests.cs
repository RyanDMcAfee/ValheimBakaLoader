using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Serilog.Events;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The self update without the dropper shape.
    /// <para>
    /// WHY THIS EXISTS. Until 1.2.13 the swap was a PowerShell script, written into a temp folder
    /// and started hidden, that unpacked a zip, wrote files beside an exe, started that exe, with a
    /// Run key naming it: on 2026-10-11 Defender scored that shape as a dropper, from behaviour
    /// rather than bytes. The swap is now the app's own library started a second time through
    /// dotnet with --apply-update. These drive that updater for real: a dotnet process on a temp
    /// tree, the same way the app starts it. Every install it works on holds stand-ins, never the
    /// app, except the one that proves the updater can replace the library it is running from, and
    /// that one is told to start nothing, so no test ever starts BakaLoader.
    /// </para>
    /// <para>
    /// And none of it is ever seen. Every updater a test starts is passed --no-window and the
    /// environment switch beside it, with no console of its own, so it opens no form and starts
    /// nothing with a console; the stand-in for the app is PING.EXE with no console either. A run
    /// that put an "Updating BakaLoader" window over whatever the owner was doing is the reason.
    /// </para>
    /// </summary>
    [Collection(WallClockCollection.Name)]
    public class ManagedUpdaterTests
    {
        private const int NotRunning = 2147480000;

        // ------------------------------------------------------------- the updater, end to end

        /// <summary>
        /// A plain update, from the install itself, the way the app starts it: the updater is the
        /// library in that folder, so it has that very file loaded while it writes the new one
        /// over it. The library and everything else land, a nested file lands, the staged folder
        /// is gone afterwards, and RESULT.txt says the update went in.
        /// </summary>
        [Fact]
        public void A_plain_update_replaces_the_library_the_updater_is_running_from()
        {
            var tree = Tree.Make();
            try
            {
                CopyRunnableApp(tree.Install);
                File.WriteAllText(Path.Combine(tree.Install, "marker.txt"), "old");
                Stage(tree,
                    (LauncherRoute.LibraryName, "new library"),
                    ("marker.txt", "new"),
                    (Path.Combine("WebUI", "added.txt"), "nested"));

                var run = RunUpdater(Path.Combine(tree.Install, LauncherRoute.LibraryName), tree, NotRunning,
                    ManagedUpdater.NoStartSwitch);

                Assert.Equal(0, run.ExitCode);
                Assert.Equal("new library", File.ReadAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName)));
                Assert.Equal("new", File.ReadAllText(Path.Combine(tree.Install, "marker.txt")));
                Assert.Equal("nested", File.ReadAllText(Path.Combine(tree.Install, "WebUI", "added.txt")));
                Assert.False(Directory.Exists(tree.Pending), "the staged files were left behind");

                Assert.NotNull(run.Result);
                Assert.Equal(UpdateResult.Updated, run.Result.Outcome);
                Assert.Equal(UpdateResult.StartSkipped, run.Result.Start);
                Assert.Equal(UpdateResult.LauncherMissing, run.Result.Launcher);
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// The incident: a scanner takes the launcher out of the install while the updater is
        /// waiting for the app to close. The update still goes in, nothing is rolled back, and the
        /// app is started through dotnet; the stand-in library ends straight away, which proves the
        /// route was taken and is written down too.
        /// </summary>
        [Fact]
        public void A_launcher_taken_during_the_update_rolls_nothing_back_and_starts_through_dotnet()
        {
            var tree = Tree.Make();
            using var app = StandInApp();
            try
            {
                File.WriteAllText(tree.Exe, "stand-in launcher");
                File.WriteAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName), "old library");
                File.WriteAllText(Path.Combine(tree.Install, "marker.txt"), "old");
                Stage(tree, (LauncherRoute.LibraryName, "new library"), ("marker.txt", "new"));

                using var updater = StartUpdater(UpdaterFromTestBuild(), tree, app.Id);
                Thread.Sleep(1500);
                Assert.False(updater.HasExited, "the updater did not wait for the app");
                AssertNoWindow(updater);

                File.Delete(tree.Exe);  // the scanner
                app.Kill();             // the app closes

                var run = Finish(updater, tree);

                Assert.Equal(0, run.ExitCode);
                Assert.Equal("new library", File.ReadAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName)));
                Assert.Equal("new", File.ReadAllText(Path.Combine(tree.Install, "marker.txt")));
                Assert.False(File.Exists(tree.Exe));

                Assert.Equal(UpdateResult.Updated, run.Result.Outcome);
                Assert.Equal(UpdateResult.LauncherMissing, run.Result.Launcher);
                Assert.Equal(UpdateResult.StartDotnet, run.Result.Start);
                Assert.Equal(UpdateResult.ProblemEnded, run.Result.StartProblem);

                var lines = ManagedUpdater.SagaLines(run.Result, "9.9.9");
                Assert.Contains(lines, l => !l.Warning && l.Line.Contains("9.9.9"));
                Assert.Contains(lines, l => l.Warning && l.Line.Contains("through dotnet") && l.Line.Contains(LauncherRoute.ScriptName));
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// The road 1.2.11 took back to 1.2.10: the launcher in the download is held by a scanner,
        /// on disk and unreadable, while the install still has its own. A launcher that cannot be
        /// read is a launcher that never arrived: the installed one is kept, the rest goes in, and
        /// nothing is rolled back.
        /// </summary>
        [Fact]
        public void A_launcher_held_in_the_download_keeps_the_installed_one_and_rolls_nothing_back()
        {
            var tree = Tree.Make();
            try
            {
                File.WriteAllText(tree.Exe, "old launcher");
                File.WriteAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName), "old library");
                File.WriteAllText(Path.Combine(tree.Install, "marker.txt"), "old");
                Stage(tree,
                    (LauncherRoute.LauncherName, "new launcher"),
                    (LauncherRoute.LibraryName, "new library"),
                    ("marker.txt", "new"));

                Run run;
                using (new FileStream(Path.Combine(tree.Pending, LauncherRoute.LauncherName), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    run = RunUpdater(UpdaterFromTestBuild(), tree, NotRunning);
                }

                Assert.Equal(0, run.ExitCode);
                Assert.Equal("old launcher", File.ReadAllText(tree.Exe));
                Assert.Equal("new library", File.ReadAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName)));
                Assert.Equal("new", File.ReadAllText(Path.Combine(tree.Install, "marker.txt")));

                Assert.Equal(UpdateResult.Updated, run.Result.Outcome);
                Assert.Equal(UpdateResult.LauncherHeld, run.Result.LauncherDownload);
                Assert.False(string.IsNullOrWhiteSpace(run.Result.LauncherDetail));

                // The stand-in launcher is not the frozen one, so it is never started.
                Assert.Equal(UpdateResult.LauncherOther, run.Result.Launcher);
                Assert.Equal(UpdateResult.StartDotnet, run.Result.Start);

                var lines = ManagedUpdater.SagaLines(run.Result, "9.9.9");
                Assert.Contains(lines, l => l.Warning && l.Line.Contains("already installed was kept"));
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// A file that cannot be set aside or written puts back everything already written. The
        /// file is held with no sharing at all, so it can be neither renamed nor read; it sorts
        /// last, so the library and the marker have already been replaced by the time it is
        /// reached, and both have to come back.
        /// </summary>
        [Fact]
        public void A_failed_copy_puts_every_file_back()
        {
            var tree = Tree.Make();
            try
            {
                File.WriteAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName), "old library");
                File.WriteAllText(Path.Combine(tree.Install, "marker.txt"), "old");
                Directory.CreateDirectory(Path.Combine(tree.Install, "zz"));
                var locked = Path.Combine(tree.Install, "zz", "locked.bin");
                File.WriteAllText(locked, "old lock");
                Stage(tree,
                    (LauncherRoute.LibraryName, "new library"),
                    ("marker.txt", "new"),
                    (Path.Combine("zz", "locked.bin"), "new lock"),
                    ("brand-new.txt", "created"));

                Run run;
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    run = RunUpdater(UpdaterFromTestBuild(), tree, NotRunning);
                }

                Assert.Equal(1, run.ExitCode);
                Assert.Equal("old library", File.ReadAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName)));
                Assert.Equal("old", File.ReadAllText(Path.Combine(tree.Install, "marker.txt")));
                Assert.Equal("old lock", File.ReadAllText(locked));
                Assert.False(File.Exists(Path.Combine(tree.Install, "brand-new.txt")), "a file the update added was left behind");

                Assert.Equal(UpdateResult.RolledBack, run.Result.Outcome);
                Assert.Equal(Path.Combine("zz", "locked.bin"), run.Result.File);
                Assert.Equal(0, run.Result.Unrestored);
                Assert.Null(run.Result.Kept);

                // Every file went back, so nothing set aside is worth keeping.
                Assert.False(Directory.Exists(Path.Combine(tree.Install, ManagedUpdater.RollbackFolderName)),
                    "a rollback that put everything back left its folder behind");

                // The copy that was there before is started again, the same way as after any update.
                Assert.Equal(UpdateResult.StartDotnet, run.Result.Start);

                var lines = ManagedUpdater.SagaLines(run.Result, "9.9.9");
                Assert.Contains(lines, l => l.Warning && l.Line.Contains("put back") && l.Line.Contains("locked.bin"));
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// An app that never closes. The wait is bounded, nothing on disk is touched, nothing is
        /// started beside the copy that is still running, and RESULT.txt says why.
        /// </summary>
        [Fact]
        public void An_app_that_never_closes_is_waited_for_a_bounded_time_and_nothing_is_written()
        {
            var tree = Tree.Make();
            using var app = StandInApp();
            try
            {
                File.WriteAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName), "old library");
                Stage(tree, (LauncherRoute.LibraryName, "new library"));

                var clock = Stopwatch.StartNew();
                using var updater = StartUpdater(UpdaterFromTestBuild(), tree, app.Id, ManagedUpdater.WaitSwitch, "2");
                Thread.Sleep(1000);
                AssertNoWindow(updater);
                var run = Finish(updater, tree);
                clock.Stop();
                Assert.True(run.Result != null, "the updater wrote no RESULT.txt, exit " + run.ExitCode);

                Assert.False(app.HasExited);
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "the wait was not bounded: " + clock.Elapsed);
                Assert.Equal(1, run.ExitCode);
                Assert.Equal("old library", File.ReadAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName)));

                Assert.Equal(UpdateResult.StillRunning, run.Result.Outcome);
                Assert.Equal(UpdateResult.StartNone, run.Result.Start);

                var lines = ManagedUpdater.SagaLines(run.Result, "9.9.9");
                Assert.Contains(lines, l => l.Warning && l.Line.Contains("still running"));
            }
            finally
            {
                try { app.Kill(); } catch { /* gone */ }
                tree.Delete();
            }
        }

        /// <summary>
        /// The wait the shipped updater holds to outlasts the longest close the app can take: the
        /// longest save wait plus the grace the close gives a server. A shorter one drops the update
        /// of a host whose world saves slowly.
        /// </summary>
        [Fact]
        public void The_shipped_wait_outlasts_the_longest_app_close()
        {
            var longestClose = ValheimServer.CloseDeadlineFor(new[] { ValheimServer.StopSaveCap });
            Assert.True(ManagedUpdater.CloseWait > longestClose,
                "the updater waits " + ManagedUpdater.CloseWait + " but an app close can take " + longestClose);
            Assert.Equal(TimeSpan.FromMinutes(4), ManagedUpdater.CloseWait);
        }

        // ------------------------------------------------------------- staging: the unpacking

        [Theory]
        [InlineData("../evil.txt")]
        [InlineData("..\\evil.txt")]
        [InlineData("sub/../../evil.txt")]
        [InlineData("/evil.txt")]
        [InlineData("C:/evil.txt")]
        [InlineData("file.txt:stream")]
        [InlineData("update-rollback/x.txt")]
        public void A_zip_naming_a_file_outside_its_folder_is_refused_whole(string name)
        {
            var tree = Tree.Make();
            try
            {
                var zip = Path.Combine(tree.Root, "release.zip");
                MakeZip(zip, (LauncherRoute.LibraryName, "new library"), (name, "planted"));

                var unpacked = AppUpdateService.ExtractToPending(zip, tree.Pending);

                Assert.Equal(name, unpacked.Refused);
                Assert.False(File.Exists(Path.Combine(tree.Install, "evil.txt")));
                Assert.False(File.Exists(Path.Combine(tree.Root, "evil.txt")));
                Assert.Empty(Directory.Exists(tree.Pending)
                    ? Directory.EnumerateFiles(tree.Pending, "*", SearchOption.AllDirectories)
                    : Array.Empty<string>());
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// A staged folder a crash left behind is cleared before the next release goes in, so
        /// nothing from it rides along into the install.
        /// </summary>
        [Fact]
        public void A_staged_folder_left_by_a_crash_is_cleared_first()
        {
            var tree = Tree.Make();
            try
            {
                Directory.CreateDirectory(Path.Combine(tree.Pending, "old"));
                File.WriteAllText(Path.Combine(tree.Pending, "stale.txt"), "from a crash");
                File.WriteAllText(Path.Combine(tree.Pending, "old", "stale.dll"), "from a crash");

                var zip = Path.Combine(tree.Root, "release.zip");
                MakeZip(zip, (LauncherRoute.LibraryName, "new library"), ("marker.txt", "new"));

                var unpacked = AppUpdateService.ExtractToPending(zip, tree.Pending);

                Assert.Null(unpacked.Refused);
                Assert.Equal(2, unpacked.Files);
                Assert.False(File.Exists(Path.Combine(tree.Pending, "stale.txt")));
                Assert.False(Directory.Exists(Path.Combine(tree.Pending, "old")));
                Assert.Equal("new library", File.ReadAllText(Path.Combine(tree.Pending, LauncherRoute.LibraryName)));

                // And the leftovers an update sets aside go at the next start.
                var aside = Path.Combine(tree.Install, ManagedUpdater.RollbackFolderName, "run");
                Directory.CreateDirectory(aside);
                File.WriteAllText(Path.Combine(aside, LauncherRoute.LibraryName), "old library");
                ManagedUpdater.CleanLeftovers(tree.Install);
                Assert.False(Directory.Exists(Path.Combine(tree.Install, ManagedUpdater.RollbackFolderName)));
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// The launcher every release ships is left out of the update altogether, so no update
        /// writes that file. A launcher with any other bytes is unpacked as before. And a zip that
        /// nests the app a folder deep is laid out the way the install is.
        /// </summary>
        [Fact]
        public void The_frozen_launcher_is_left_out_and_any_other_launcher_is_unpacked()
        {
            var tree = Tree.Make();
            try
            {
                var frozen = File.ReadAllBytes(FrozenLauncherFile());
                var zip = Path.Combine(tree.Root, "frozen.zip");
                MakeZipBytes(zip,
                    ("pkg/" + LauncherRoute.LauncherName, frozen),
                    ("pkg/" + LauncherRoute.LibraryName, new byte[] { 1, 2, 3 }),
                    ("pkg/WebUI/app.js", new byte[] { 4 }));

                var unpacked = AppUpdateService.ExtractToPending(zip, tree.Pending);

                Assert.True(unpacked.LauncherSkipped);
                Assert.False(File.Exists(Path.Combine(tree.Pending, LauncherRoute.LauncherName)));
                Assert.True(File.Exists(Path.Combine(tree.Pending, LauncherRoute.LibraryName)));
                Assert.True(File.Exists(Path.Combine(tree.Pending, "WebUI", "app.js")));

                var other = Path.Combine(tree.Root, "other.zip");
                MakeZip(other, (LauncherRoute.LauncherName, "a launcher made on purpose"), (LauncherRoute.LibraryName, "x"));
                unpacked = AppUpdateService.ExtractToPending(other, tree.Pending);

                Assert.False(unpacked.LauncherSkipped);
                Assert.Equal("a launcher made on purpose", File.ReadAllText(Path.Combine(tree.Pending, LauncherRoute.LauncherName)));
            }
            finally
            {
                tree.Delete();
            }
        }

        // ------------------------------------------------------------- the start, and the record

        /// <summary>
        /// The route the updater starts BakaLoader by. The launcher only when it is the frozen one
        /// (a copy of its bytes here, which is hashed and never started); a launcher with other
        /// bytes, or none, means dotnet and the library; nothing at all means nothing to start.
        /// </summary>
        [Fact]
        public void The_launcher_is_started_only_when_it_is_the_frozen_one()
        {
            var tree = Tree.Make();
            try
            {
                string Folder(string name, byte[] exe, bool library)
                {
                    var folder = Path.Combine(tree.Root, name);
                    Directory.CreateDirectory(folder);
                    if (exe != null) File.WriteAllBytes(Path.Combine(folder, LauncherRoute.LauncherName), exe);
                    if (library) File.WriteAllText(Path.Combine(folder, LauncherRoute.LibraryName), "x");
                    return folder;
                }

                var frozen = File.ReadAllBytes(FrozenLauncherFile());
                Assert.Equal(LaunchRoute.Launcher, ManagedUpdater.ChooseStart(Folder("frozen", frozen, true)));
                Assert.Equal(LaunchRoute.Dotnet, ManagedUpdater.ChooseStart(Folder("other", new byte[] { 1 }, true)));
                Assert.Equal(LaunchRoute.Dotnet, ManagedUpdater.ChooseStart(Folder("taken", null, true)));
                Assert.Equal(LaunchRoute.None, ManagedUpdater.ChooseStart(Folder("neither", null, false)));
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// The constant the app holds is the launcher the build ships and the hash the frozen
        /// launcher tests hold, so a launcher re-frozen on purpose cannot leave it behind.
        /// </summary>
        [Fact]
        public void The_frozen_hash_the_app_holds_is_the_launcher_it_ships()
        {
            Assert.Equal(FrozenAppHostTests.FrozenSha256, LauncherRoute.FrozenLauncherSha256);
            Assert.True(LauncherRoute.IsFrozenLauncher(FrozenLauncherFile()));
            Assert.False(LauncherRoute.IsFrozenLauncher(new byte[] { 1, 2, 3 }));
        }

        [Fact]
        public void RESULT_txt_reads_back_as_it_was_written_and_says_one_word_per_line()
        {
            var written = new UpdateResult
            {
                Outcome = UpdateResult.RolledBack,
                File = Path.Combine("zz", "locked.bin"),
                Detail = "The process cannot access the file\r\nbecause it is in use.",
                Unrestored = 2,
                LauncherDownload = UpdateResult.LauncherHeld,
                LauncherDetail = "held",
                Launcher = UpdateResult.LauncherFrozen,
                Start = UpdateResult.StartLauncher,
                StartProblem = UpdateResult.ProblemEnded,
                StartDetail = "-2147450749",
            };

            var text = written.ToText();
            Assert.Contains("result: rolled_back\r\n", text);
            Assert.Contains("detail: The process cannot access the file because it is in use.\r\n", text);
            Assert.DoesNotContain("\u2014", text);

            var read = UpdateResult.Parse(text);
            Assert.Equal(written.Outcome, read.Outcome);
            Assert.Equal(written.File, read.File);
            Assert.Equal("The process cannot access the file because it is in use.", read.Detail);
            Assert.Equal(2, read.Unrestored);
            Assert.Equal(written.LauncherDownload, read.LauncherDownload);
            Assert.Equal(written.Launcher, read.Launcher);
            Assert.Equal(written.Start, read.Start);
            Assert.Equal(written.StartProblem, read.StartProblem);
            Assert.Equal(written.StartDetail, read.StartDetail);

            Assert.Null(UpdateResult.Parse(""));
            Assert.Null(UpdateResult.Parse("# nothing but a comment\r\n"));
        }

        /// <summary>
        /// Every line a RESULT.txt can turn into is a finished sentence out of the catalog: no id,
        /// no empty slot, no dash standing in for punctuation.
        /// </summary>
        [Fact]
        public void Every_Saga_line_comes_out_of_the_catalog_finished()
        {
            var results = new List<UpdateResult>();
            foreach (var outcome in new[] { UpdateResult.Updated, UpdateResult.RolledBack, UpdateResult.StillRunning, UpdateResult.NothingStaged })
            foreach (var download in new[] { UpdateResult.LauncherNone, UpdateResult.LauncherHeld, UpdateResult.LauncherUnwritable })
            foreach (var problem in new[] { "", UpdateResult.ProblemNoDotnet, UpdateResult.ProblemEnded, UpdateResult.ProblemFailed, UpdateResult.ProblemNothing })
            {
                results.Add(new UpdateResult
                {
                    Outcome = outcome, File = "marker.txt", Detail = "denied", Unrestored = 1, WaitedMinutes = 4,
                    LauncherDownload = download, LauncherDetail = "held", Launcher = UpdateResult.LauncherMissing,
                    Start = UpdateResult.StartDotnet, StartProblem = problem, StartDetail = "1",
                });
            }

            var said = results.SelectMany(r => ManagedUpdater.SagaLines(r, "1.2.13")).Select(l => l.Line).Distinct().ToList();

            Assert.True(said.Count >= 12, "fewer lines than the cases: " + said.Count);
            Assert.All(said, line =>
            {
                Assert.DoesNotContain("host.", line);
                Assert.DoesNotContain("{", line);
                Assert.DoesNotContain("\u2014", line);
                Assert.DoesNotContain("\u2013", line);
                Assert.DoesNotContain(" - ", line);
            });

            // The good ending is an ordinary line; everything else is something to look at.
            var good = ManagedUpdater.SagaLines(new UpdateResult { Outcome = UpdateResult.Updated, Launcher = UpdateResult.LauncherFrozen, Start = UpdateResult.StartLauncher }, "1.2.13");
            Assert.Single(good);
            Assert.False(good[0].Warning);
            Assert.Contains("1.2.13", good[0].Line);
        }

        [Fact]
        public void RESULT_txt_is_read_once_and_taken_away()
        {
            var tree = Tree.Make();
            try
            {
                Directory.CreateDirectory(tree.Work);
                File.WriteAllText(Path.Combine(tree.Work, ManagedUpdater.ResultFileName),
                    new UpdateResult { Outcome = UpdateResult.Updated }.ToText());

                Assert.Equal(UpdateResult.Updated, ManagedUpdater.ReadAndClearResult(tree.Work).Outcome);
                Assert.Null(ManagedUpdater.ReadAndClearResult(tree.Work));
                Assert.False(File.Exists(Path.Combine(tree.Work, ManagedUpdater.ResultFileName)));
            }
            finally
            {
                tree.Delete();
            }
        }

        // ------------------------------------------------------------- the seam between the two

        /// <summary>
        /// What the app starts and what the updater reads are the same thing: the dotnet host, the
        /// library from the install, then --apply-update, the pid, the staged folder and the
        /// install. Nothing hidden: no shell, no window style, no console trick.
        /// </summary>
        [Fact]
        public void The_updater_is_started_the_way_it_reads_its_arguments()
        {
            var install = Path.Combine(Path.GetTempPath(), "baka install");
            var pending = ManagedUpdater.PendingFolder(install);

            var psi = AppUpdateService.UpdaterStartInfo(@"C:\Program Files\dotnet\dotnet.exe", install, pending, 4242);

            Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", psi.FileName);
            Assert.Equal(new[]
            {
                Path.Combine(install, LauncherRoute.LibraryName), ManagedUpdater.ApplyUpdateSwitch, "4242", pending, install,
            }, psi.ArgumentList.ToArray());
            Assert.False(psi.UseShellExecute);
            Assert.False(psi.CreateNoWindow);
            Assert.Equal(ProcessWindowStyle.Normal, psi.WindowStyle);

            var plan = ManagedUpdater.Parse(psi.ArgumentList.Skip(1).ToArray());
            Assert.NotNull(plan);
            Assert.Equal(4242, plan.Pid);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(pending)), plan.Pending);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(install)), plan.Install);
            Assert.Equal(ManagedUpdater.CloseWait, plan.Wait);
            Assert.True(plan.Start);
            Assert.Equal(ManagedUpdater.WorkFolder(), plan.Work);

            // The app's start always shows the host their update; only a test turns it off.
            Assert.DoesNotContain(ManagedUpdater.NoWindowSwitch, psi.ArgumentList);
            var quiet = ManagedUpdater.Parse(psi.ArgumentList.Skip(1).Append(ManagedUpdater.NoWindowSwitch).ToArray());
            Assert.False(quiet.Window);

            // Pointed anywhere but update-pending in that install, it does nothing.
            Assert.Null(ManagedUpdater.Parse(new[] { ManagedUpdater.ApplyUpdateSwitch, "1", Path.GetTempPath(), install }));
            Assert.Null(ManagedUpdater.Parse(new[] { ManagedUpdater.ApplyUpdateSwitch, "x", pending, install }));
            Assert.Null(ManagedUpdater.Parse(new[] { ManagedUpdater.ApplyUpdateSwitch, "1" }));
            Assert.True(ManagedUpdater.IsApplyUpdate(new[] { ManagedUpdater.ApplyUpdateSwitch }));
            Assert.False(ManagedUpdater.IsApplyUpdate(Array.Empty<string>()));
        }

        /// <summary>
        /// The branch is the first thing Program.Main decides after the window settings, so the
        /// updater never opens the log, the preferences or the startup entry.
        /// </summary>
        [Fact]
        public void Program_Main_hands_the_updater_off_before_anything_of_the_app_runs()
        {
            var program = AppSourceTree.Lf(AppSourceTree.Read("ValheimBakaLoader", "Program.cs"));

            var branch = program.IndexOf("if (ManagedUpdater.IsApplyUpdate(args))", StringComparison.Ordinal);
            var app = program.IndexOf("RunApp(args);", StringComparison.Ordinal);
            var repoint = program.IndexOf("RepointStartupEntry(container);", StringComparison.Ordinal);
            var services = program.IndexOf("var services = new ServiceCollection();", StringComparison.Ordinal);

            Assert.True(branch > 0 && app > branch && services > app && repoint > services,
                "the updater branch is not ahead of the app in Program.Main");
            Assert.Contains("Environment.ExitCode = ManagedUpdater.Run(args);\n                return;", program);
        }

        /// <summary>
        /// The shape Defender scored, kept out of every file that updates the app: no PowerShell,
        /// no script written to disk, no hidden window, no console host trick, and nothing that
        /// writes a Run key.
        /// </summary>
        [Fact]
        public void No_part_of_the_update_is_a_script_a_hidden_window_or_a_Run_key()
        {
            var files = AppSourceTree.Files();
            foreach (var name in new[] { "AppUpdateService.cs", "ManagedUpdater.cs", "ManagedUpdaterWindow.cs" })
            {
                var code = string.Join("\n", AppSourceTree.Lf(files[name]).Split('\n')
                    .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

                Assert.DoesNotContain("powershell", code, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(".ps1", code, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("robocopy", code, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("conhost", code, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("CreateNoWindow = true", code);
                Assert.DoesNotContain("ProcessWindowStyle.Hidden", code);
                Assert.DoesNotContain("Registry", code);
                Assert.DoesNotContain("StartupHelper", code);
                Assert.DoesNotContain("RunKey", code);
            }

            Assert.Null(typeof(AppUpdateService).GetMethod("BuildWatchdogScript",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
        }

        /// <summary>
        /// The switch every test passes, and the environment switch beside it: either one is a run
        /// with no window. Nothing else is.
        /// </summary>
        [Fact]
        public void No_window_is_read_from_the_switch_and_from_the_environment()
        {
            var install = Path.Combine(Path.GetTempPath(), "baka install");
            var args = ManagedUpdater.Arguments(1, ManagedUpdater.PendingFolder(install), install).ToArray();

            var before = Environment.GetEnvironmentVariable(ManagedUpdater.NoWindowVariable);
            try
            {
                Environment.SetEnvironmentVariable(ManagedUpdater.NoWindowVariable, null);
                Assert.True(ManagedUpdater.Parse(args).Window);
                Assert.False(ManagedUpdater.Parse(args.Append(ManagedUpdater.NoWindowSwitch).ToArray()).Window);

                Environment.SetEnvironmentVariable(ManagedUpdater.NoWindowVariable, "1");
                Assert.False(ManagedUpdater.Parse(args).Window);

                Environment.SetEnvironmentVariable(ManagedUpdater.NoWindowVariable, "0");
                Assert.True(ManagedUpdater.Parse(args).Window);
            }
            finally
            {
                Environment.SetEnvironmentVariable(ManagedUpdater.NoWindowVariable, before);
            }

            // And the one door every end to end test goes through always passes both.
            var suite = AppSourceTree.Lf(File.ReadAllText(RepoScript.At("ValheimBakaLoader.Tests", "Tools", "ManagedUpdaterTests.cs")));
            var start = suite.IndexOf("private static Process StartUpdater(string " + "library", StringComparison.Ordinal);
            var body = suite.Substring(start, suite.IndexOf("private static void AssertNoWindow(Process " + "updater", start, StringComparison.Ordinal) - start);
            Assert.Contains("psi.ArgumentList.Add(ManagedUpdater.NoWindowSwitch);", body);
            Assert.Contains("psi.Environment[ManagedUpdater.NoWindowVariable] = \"1\";", body);
            Assert.Contains("CreateNoWindow = true,", body);
        }

        /// <summary>
        /// A rollback that could not put every file back keeps the files it set aside, because they
        /// are the only copy left of what was there before, and says where they are. The file is
        /// held open for reading only: it can be copied aside but neither renamed nor written, so
        /// the copy over it fails, and putting the copy back over it fails the same way. Driven
        /// through dotnet like every other run here, with no window.
        /// </summary>
        [Fact]
        public void A_restore_that_could_not_finish_keeps_the_old_files_and_says_where()
        {
            var tree = Tree.Make();
            try
            {
                File.WriteAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName), "old library");
                Directory.CreateDirectory(Path.Combine(tree.Install, "zz"));
                var held = Path.Combine(tree.Install, "zz", "held.bin");
                File.WriteAllText(held, "old held");
                Stage(tree,
                    (LauncherRoute.LibraryName, "new library"),
                    (Path.Combine("zz", "held.bin"), "new held"));

                Run run;
                using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    run = RunUpdater(UpdaterFromTestBuild(), tree, NotRunning);
                }

                Assert.Equal(1, run.ExitCode);
                Assert.Equal(UpdateResult.RolledBack, run.Result.Outcome);
                Assert.Equal(1, run.Result.Unrestored);
                Assert.Equal("old library", File.ReadAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName)));

                // The copy aside is still there, marked, with the old bytes in it.
                Assert.False(string.IsNullOrWhiteSpace(run.Result.Kept), "RESULT.txt does not say where the old files are");
                var kept = run.Result.Kept;
                Assert.StartsWith(Path.Combine(tree.Install, ManagedUpdater.RollbackFolderName), kept, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("old held", File.ReadAllText(Path.Combine(kept, "zz", "held.bin")));
                Assert.True(File.Exists(Path.Combine(kept, ManagedUpdater.KeptMarkerName)));

                // And the next start's clean, which is also the next update's, leaves it alone while
                // still clearing a run that is not kept.
                var other = Path.Combine(tree.Install, ManagedUpdater.RollbackFolderName, "an-earlier-run");
                Directory.CreateDirectory(other);
                File.WriteAllText(Path.Combine(other, "x.txt"), "x");
                ManagedUpdater.CleanLeftovers(tree.Install);
                Assert.Equal("old held", File.ReadAllText(Path.Combine(kept, "zz", "held.bin")));
                Assert.False(Directory.Exists(other), "a run that is not kept was not cleaned");

                // The Saga never says the earlier files were put back, and names the folder.
                var lines = ManagedUpdater.SagaLines(run.Result, "9.9.9");
                Assert.DoesNotContain(lines, l => l.Line.Contains("were put back"));
                Assert.Contains(lines, l => l.Warning && l.Line.Contains("only partly worked") && l.Line.Contains("held.bin"));
                Assert.Contains(lines, l => l.Warning && l.Line.Contains(kept) && l.Line.Contains("1 of the files"));
            }
            finally
            {
                tree.Delete();
            }
        }

        // ------------------------------------------------------------- the window, never in a test

        /// <summary>
        /// The rule Run reads before it makes a window, held without making one: a window only for
        /// a real update that was not told to show nothing.
        /// </summary>
        [Fact]
        public void The_window_is_only_for_a_real_update_that_was_not_told_to_show_nothing()
        {
            var install = Path.Combine(Path.GetTempPath(), "baka install");
            var args = ManagedUpdater.Arguments(1, ManagedUpdater.PendingFolder(install), install).ToArray();

            var before = Environment.GetEnvironmentVariable(ManagedUpdater.NoWindowVariable);
            try
            {
                Environment.SetEnvironmentVariable(ManagedUpdater.NoWindowVariable, null);
                var plain = ManagedUpdater.Parse(args);
                var quiet = ManagedUpdater.Parse(args.Append(ManagedUpdater.NoWindowSwitch).ToArray());

                Environment.SetEnvironmentVariable(ManagedUpdater.NoWindowVariable, "1");
                var fromEnvironment = ManagedUpdater.Parse(args);

                Assert.True(ManagedUpdater.ShowsWindow(plain, holdsApp: true));
                Assert.False(ManagedUpdater.ShowsWindow(quiet, holdsApp: true));
                Assert.False(ManagedUpdater.ShowsWindow(fromEnvironment, holdsApp: true));
                Assert.False(ManagedUpdater.ShowsWindow(plain, holdsApp: false));
                Assert.False(ManagedUpdater.ShowsWindow(null, holdsApp: true));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ManagedUpdater.NoWindowVariable, before);
            }
        }

        /// <summary>
        /// Every way the updater could make or show a window sits below the guard in Run, and the
        /// window is only ever built through MakeWindow, which is what lets the next test swap it.
        /// The window itself is WinForms and lives in the Windows app (ManagedUpdaterWindow.cs),
        /// not in ManagedUpdater.cs, which compiles into Core and names no toolkit: Core on its
        /// own answers no window, the app hands its own over in Program.Main, and the one
        /// construction of the window is in the app's file.
        /// </summary>
        [Fact]
        public void Every_window_the_updater_makes_comes_after_the_guard()
        {
            var code = AppSourceTree.Lf(AppSourceTree.Files()["ManagedUpdater.cs"]);

            var guard = code.IndexOf("if (!ShowsWindow(plan, HoldsTheApp(plan.Pending))) return Apply(plan, null).ExitCode;", StringComparison.Ordinal);
            Assert.True(guard > 0, "the guard in Run is gone");
            Assert.True(code.IndexOf("public static int Run(string[] args)", StringComparison.Ordinal) < guard);

            foreach (var making in new[] { "new UpdaterWindow(", "MakeWindow(", "Application.Run(", ".Show(", ".ShowDialog(", "MessageBox" })
            {
                for (var at = code.IndexOf(making, StringComparison.Ordinal); at >= 0; at = code.IndexOf(making, at + 1, StringComparison.Ordinal))
                {
                    Assert.True(at > guard, making + " comes before the guard in Run");
                }
            }

            Assert.Equal(0, Occurrences(code, "new UpdaterWindow("));
            Assert.Equal(0, Occurrences(code, "Application.Run("));
            Assert.DoesNotContain("System.Windows.Forms", code);
            Assert.Equal(1, Occurrences(code, "window = MakeWindow();"));
            Assert.Contains("internal static Func<IUpdaterWindow> MakeWindow { get; set; } = () => null;", code);

            var window = AppSourceTree.Lf(AppSourceTree.Files()["ManagedUpdaterWindow.cs"]);
            Assert.Equal(1, Occurrences(window, "new UpdaterWindow("));
            Assert.Contains("internal static ManagedUpdater.IUpdaterWindow Make() => new UpdaterWindow();", window);

            var program = AppSourceTree.Lf(AppSourceTree.Read("ValheimBakaLoader", "Program.cs"));
            var handed = program.IndexOf("ManagedUpdater.MakeWindow = ManagedUpdaterWindow.Make;", StringComparison.Ordinal);
            Assert.True(handed > 0, "Program.Main no longer hands the updater its window");
            Assert.True(handed < program.IndexOf("Environment.ExitCode = ManagedUpdater.Run(args);", StringComparison.Ordinal),
                "the window is handed over after Run has already decided");
        }

        /// <summary>
        /// The real Run, in this process, with the window swapped for one that throws and writes
        /// down that it was asked for. Told to show nothing, by the switch or by the environment,
        /// it never asks; with neither, it asks, which proves the swap is the door a window comes
        /// through, and since that door throws, it runs on without one. Nothing is ever drawn: a
        /// guard that stopped working fails here instead.
        /// </summary>
        [Fact]
        public void A_run_told_to_show_nothing_never_asks_for_a_window()
        {
            // Checked first, so the last case below cannot reach a real window even if the code
            // has moved under this test: the Core file makes no window of its own, and the one
            // in the app's file is only reached through the hook swapped below.
            var code = AppSourceTree.Lf(AppSourceTree.Files()["ManagedUpdater.cs"]);
            Assert.Equal(0, Occurrences(code, "new UpdaterWindow("));
            Assert.Equal(1, Occurrences(code, "window = MakeWindow();"));
            Assert.Equal(1, Occurrences(AppSourceTree.Lf(AppSourceTree.Files()["ManagedUpdaterWindow.cs"]), "new UpdaterWindow("));

            var asked = 0;
            var make = ManagedUpdater.MakeWindow;
            var before = Environment.GetEnvironmentVariable(ManagedUpdater.NoWindowVariable);
            ManagedUpdater.MakeWindow = () =>
            {
                Interlocked.Increment(ref asked);
                throw new InvalidOperationException("a test asked for the updater's window");
            };

            try
            {
                int RunOnce(bool noWindowSwitch, string environment)
                {
                    var tree = Tree.Make();
                    try
                    {
                        File.WriteAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName), "old library");
                        Stage(tree, (LauncherRoute.LibraryName, "new library"));

                        Environment.SetEnvironmentVariable(ManagedUpdater.NoWindowVariable, environment);
                        var args = ManagedUpdater.Arguments(NotRunning, tree.Pending, tree.Install)
                            .Concat(new[] { ManagedUpdater.NoStartSwitch, ManagedUpdater.WorkSwitch, tree.Work })
                            .Concat(noWindowSwitch ? new[] { ManagedUpdater.NoWindowSwitch } : Array.Empty<string>())
                            .ToArray();

                        var exit = ManagedUpdater.Run(args);

                        // The update itself went in either way.
                        Assert.Equal("new library", File.ReadAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName)));
                        Assert.Equal(UpdateResult.Updated, ManagedUpdater.ReadAndClearResult(tree.Work).Outcome);
                        return exit;
                    }
                    finally
                    {
                        tree.Delete();
                    }
                }

                Assert.Equal(0, RunOnce(noWindowSwitch: true, environment: null));
                Assert.Equal(0, asked);

                Assert.Equal(0, RunOnce(noWindowSwitch: false, environment: "1"));
                Assert.Equal(0, asked);

                Assert.Equal(0, RunOnce(noWindowSwitch: false, environment: null));
                Assert.Equal(1, asked);
            }
            finally
            {
                ManagedUpdater.MakeWindow = make;
                Environment.SetEnvironmentVariable(ManagedUpdater.NoWindowVariable, before);
            }
        }

        // ------------------------------------------------------------- staging, end to end

        /// <summary>
        /// A release whose SHA256SUMS.txt gives the zip a different sum is not installed: staging
        /// answers DownloadDamaged, the zip is gone, nothing is unpacked beside the install and no
        /// updater is started. Driven through TryStageUpdateAsync with the release, the download
        /// and the list all answered here, the work and install folders in a temp tree, and a
        /// starter that only writes down that it was called.
        /// </summary>
        [Fact]
        public async Task A_checksum_list_that_does_not_match_stops_the_update_before_anything_is_unpacked()
        {
            var tree = Tree.Make();
            try
            {
                var staging = Staging.For(tree, sums: _ => new string('0', 64) + "  " + Staging.AssetName + "\n");

                Assert.Equal(StageOutcome.DownloadDamaged, await staging.Service.TryStageUpdateAsync(userInitiated: true));

                Assert.Contains(staging.Http.Handler.Requests, r => r.EndsWith(AppUpdateService.SumsAssetName));
                staging.AssertNothingStaged();
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// A release that carries the list where the list cannot be fetched is a connection
        /// problem, not a release to install unchecked.
        /// </summary>
        [Fact]
        public async Task A_checksum_list_that_cannot_be_fetched_stops_the_update_as_a_connection_problem()
        {
            var tree = Tree.Make();
            try
            {
                var staging = Staging.For(tree, sums: _ => null);

                Assert.Equal(StageOutcome.NetworkError, await staging.Service.TryStageUpdateAsync(userInitiated: true));

                Assert.Contains(staging.Http.Handler.Requests, r => r.EndsWith(AppUpdateService.SumsAssetName));
                staging.AssertNothingStaged();
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// The control for the two above: the same release with the right sum, and with no list at
        /// all, goes all the way to the starter, with the app unpacked beside the install. Without
        /// this the two refusals could pass on a staging that never got that far.
        /// </summary>
        [Fact]
        public async Task A_checksum_list_that_matches_lets_the_update_through_to_the_updater()
        {
            foreach (var withList in new[] { true, false })
            {
                var tree = Tree.Make();
                try
                {
                    var staging = Staging.For(tree, sums: withList
                        ? zip => Sha256Hex(zip).ToLowerInvariant() + " *" + Staging.AssetName + "\n"
                        : null);

                    Assert.Equal(StageOutcome.Staged, await staging.Service.TryStageUpdateAsync(userInitiated: true));

                    Assert.Single(staging.Started);
                    Assert.Equal(tree.Install, staging.Started[0].Install);
                    Assert.Equal(tree.Pending, staging.Started[0].Pending);
                    Assert.Equal("new library", File.ReadAllText(Path.Combine(tree.Pending, LauncherRoute.LibraryName)));
                    Assert.Equal("old library", File.ReadAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName)));
                    Assert.False(File.Exists(Path.Combine(tree.Work, "update.zip")), "the zip was left in the work folder");
                }
                finally
                {
                    tree.Delete();
                }
            }
        }

        /// <summary>The staging seams are for the suite, never a knob on the service the container hands out.</summary>
        [Fact]
        public void The_staging_seams_are_not_public()
        {
            foreach (var name in new[] { "WorkDirectory", "InstallDirectory", "UpdaterStarter", "EnvironmentReader" })
            {
                var property = typeof(AppUpdateService).GetProperty(name,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(property);
                Assert.False(property.GetMethod?.IsPublic ?? false, name + " is public");
                Assert.False(property.SetMethod?.IsPublic ?? false, name + " is public");
            }

            var window = typeof(ManagedUpdater).GetProperty("MakeWindow",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(window);
            Assert.False(window.SetMethod?.IsPublic ?? false, "MakeWindow is public");
        }

        // ------------------------------------------------------------- the local zip seam

        /// <summary>
        /// With BAKALOADER_UPDATE_FROM_ZIP naming a zip, that zip is the release: GitHub is never
        /// asked, nothing goes over the network (not even the checksum list the GitHub release in
        /// this fixture carries), the zip is copied rather than moved, it is unpacked beside the
        /// install, and the updater is started through the same starter with the install and the
        /// pending folder. The stand-in library is text, so its version reads as "local", and the
        /// one line that says the seam is in use names the zip.
        /// </summary>
        [Fact]
        public async Task A_zip_named_in_the_environment_is_staged_without_asking_GitHub()
        {
            var tree = Tree.Make();
            try
            {
                var local = Path.Combine(tree.Root, "local release", "ValheimBakaLoader-1.2.13-win-x64.zip");
                MakeZip(local, (LauncherRoute.LibraryName, "local library"), ("marker.txt", "local"));
                var logger = new Remembering();
                var staging = Staging.For(tree,
                    sums: zip => Sha256Hex(zip).ToLowerInvariant() + "  " + Staging.AssetName + "\n",
                    environment: Only(AppUpdateService.UpdateFromZipVariable, local),
                    logger: logger);

                Assert.Equal(StageOutcome.Staged, await staging.Service.TryStageUpdateAsync(userInitiated: true));

                staging.GitHub.Verify(c => c.GetLatestReleaseAsync(), Times.Never());
                Assert.Empty(staging.Http.Handler.Requests);
                Assert.Single(staging.Started);
                Assert.Equal(tree.Install, staging.Started[0].Install);
                Assert.Equal(tree.Pending, staging.Started[0].Pending);
                Assert.Equal("local library", File.ReadAllText(Path.Combine(tree.Pending, LauncherRoute.LibraryName)));
                Assert.Equal("local", File.ReadAllText(Path.Combine(tree.Pending, "marker.txt")));
                Assert.Equal("old library", File.ReadAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName)));
                Assert.True(File.Exists(local), "the owner's zip was moved or deleted");
                Assert.False(File.Exists(Path.Combine(tree.Work, "update.zip")), "the copy was left in the work folder");
                Assert.Empty(Directory.EnumerateDirectories(tree.Work, "version-probe-*"));

                Assert.Equal(1, logger.Count(LogEventLevel.Information, local));
                Assert.Equal(1, logger.Count(LogEventLevel.Information, "(version \"local\")"));
                Assert.Equal(0, logger.Count(LogEventLevel.Warning, AppUpdateService.UpdateFromZipVariable));
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// Unset, the check is the GitHub one: the release is asked for, the zip downloaded, the
        /// list fetched, and nothing at all is said about the variable.
        /// </summary>
        [Fact]
        public async Task With_no_zip_named_the_update_goes_to_GitHub()
        {
            var tree = Tree.Make();
            try
            {
                var logger = new Remembering();
                var staging = Staging.For(tree,
                    sums: zip => Sha256Hex(zip).ToLowerInvariant() + "  " + Staging.AssetName + "\n",
                    environment: _ => null,
                    logger: logger);

                Assert.Equal(StageOutcome.Staged, await staging.Service.TryStageUpdateAsync(userInitiated: true));

                staging.GitHub.Verify(c => c.GetLatestReleaseAsync(), Times.Once());
                Assert.Contains(staging.Http.Handler.Requests, r => r.EndsWith(Staging.AssetName));
                Assert.Contains(staging.Http.Handler.Requests, r => r.EndsWith(AppUpdateService.SumsAssetName));
                Assert.Equal("new library", File.ReadAllText(Path.Combine(tree.Pending, LauncherRoute.LibraryName)));
                Assert.Equal(0, logger.Count(LogEventLevel.Information, AppUpdateService.UpdateFromZipVariable));
                Assert.Equal(0, logger.Count(LogEventLevel.Warning, AppUpdateService.UpdateFromZipVariable));
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// Naming a zip that is not there, or a file that is not a zip, is said in a warning and
        /// changes nothing else: GitHub is asked and its release is the one staged.
        /// </summary>
        [Fact]
        public async Task A_zip_named_in_the_environment_that_is_not_there_falls_back_to_GitHub_with_a_warning()
        {
            var tree = Tree.Make();
            try
            {
                var notZip = Path.Combine(tree.Root, "notes.txt");
                File.WriteAllText(notZip, "not a zip");

                foreach (var named in new[] { Path.Combine(tree.Root, "missing", "ValheimBakaLoader-1.2.13-win-x64.zip"), notZip })
                {
                    ManagedUpdater.TryDeleteFolder(tree.Pending);
                    File.Delete(Path.Combine(tree.Root, "release.zip"));
                    var logger = new Remembering();
                    var staging = Staging.For(tree, sums: null,
                        environment: Only(AppUpdateService.UpdateFromZipVariable, named),
                        logger: logger);

                    Assert.Equal(StageOutcome.Staged, await staging.Service.TryStageUpdateAsync(userInitiated: true));

                    staging.GitHub.Verify(c => c.GetLatestReleaseAsync(), Times.Once());
                    Assert.Contains(staging.Http.Handler.Requests, r => r.EndsWith(Staging.AssetName));
                    Assert.Single(staging.Started);
                    Assert.Equal("new library", File.ReadAllText(Path.Combine(tree.Pending, LauncherRoute.LibraryName)));
                    Assert.Equal(1, logger.Count(LogEventLevel.Warning, named));
                    Assert.Equal(0, logger.Count(LogEventLevel.Information, AppUpdateService.UpdateFromZipVariable));
                }
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// The version comes off the library inside the zip, read the way the app reads its own:
        /// the product version of ValheimBakaLoader.dll. Driven with the real library this suite
        /// was built against, so the answer is a real version and not the "local" fallback.
        /// </summary>
        [Fact]
        public void The_local_zip_version_is_the_product_version_of_the_library_inside_it()
        {
            var tree = Tree.Make();
            try
            {
                var library = UpdaterFromTestBuild();
                var expected = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, "ValheimBakaLoader.dll")).ProductVersion?.Trim();
                Assert.False(string.IsNullOrWhiteSpace(expected), "the test build's library carries no product version");

                var zip = Path.Combine(tree.Root, "real.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                    archive.CreateEntryFromFile(library, "ValheimBakaLoader-x/" + LauncherRoute.LibraryName);

                Directory.CreateDirectory(tree.Work);
                Assert.Equal(expected, AppUpdateService.LocalZipVersion(zip, tree.Work));

                var empty = Path.Combine(tree.Root, "empty.zip");
                MakeZip(empty, ("marker.txt", "x"));
                Assert.Equal("local", AppUpdateService.LocalZipVersion(empty, tree.Work));

                Assert.Empty(Directory.EnumerateFileSystemEntries(tree.Work));
            }
            finally
            {
                tree.Delete();
            }
        }

        /// <summary>
        /// An updater started for a local zip does not carry the variable on to the app it starts
        /// again, or that app would stage the same zip at its next check.
        /// </summary>
        [Fact]
        public void The_updater_for_a_local_zip_does_not_hand_the_variable_on()
        {
            // Set in this process for the length of the test so there is something to drop. It
            // names a zip that does not exist, so a staging test elsewhere in the run that read it
            // in this window would only log a warning and go to GitHub as it always does.
            var before = Environment.GetEnvironmentVariable(AppUpdateService.UpdateFromZipVariable);
            try
            {
                Environment.SetEnvironmentVariable(AppUpdateService.UpdateFromZipVariable,
                    Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"), "none.zip"));

                var with = AppUpdateService.UpdaterStartInfo("dotnet.exe", @"C:\install", @"C:\install\update-pending", 42, dropZipVariable: true);
                var without = AppUpdateService.UpdaterStartInfo("dotnet.exe", @"C:\install", @"C:\install\update-pending", 42);

                Assert.False(with.Environment.ContainsKey(AppUpdateService.UpdateFromZipVariable));
                Assert.True(without.Environment.ContainsKey(AppUpdateService.UpdateFromZipVariable),
                    "the control did not see the variable, so the assertion above proves nothing");
                Assert.Equal(without.ArgumentList.ToArray(), with.ArgumentList.ToArray());
                Assert.Equal(without.FileName, with.FileName);
            }
            finally
            {
                Environment.SetEnvironmentVariable(AppUpdateService.UpdateFromZipVariable, before);
            }
        }

        private static Func<string, string> Only(string name, string value) =>
            asked => asked == name ? value : null;

        /// <summary>The application logger without its file sink, keeping the level of each line.</summary>
        private sealed class Remembering : IApplicationLogger
        {
            public List<(LogEventLevel Level, string Text)> Lines { get; } = new();

            public event Action<string> LogReceived;

            public IEnumerable<string> LogBuffer => Lines.Select(l => l.Text);

            public void Write(LogEvent logEvent)
            {
                if (logEvent == null) return;
                var text = logEvent.RenderMessage();
                lock (Lines) Lines.Add((logEvent.Level, text));
                LogReceived?.Invoke(text);
            }

            public bool IsEnabled(LogEventLevel level) => true;

            public int Count(LogEventLevel level, string fragment)
            {
                lock (Lines)
                    return Lines.Count(l => l.Level == level
                        && l.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }
        }

        // ------------------------------------------------------------- the checksum list

        [Fact]
        public void The_checksum_list_is_read_the_way_sha256sum_writes_it()
        {
            var hex = new string('a', 64);
            var other = new string('b', 64);
            var text = "# sums\n" + other + "  lang-ja.zip\r\n" + hex + " *ValheimBakaLoader-1.2.13-win-x64.zip\n" + "short  x.zip\n";

            Assert.Equal(hex, AppUpdateService.ReadPublishedSum(text, "ValheimBakaLoader-1.2.13-win-x64.zip"));
            Assert.Equal(other, AppUpdateService.ReadPublishedSum(text, "LANG-JA.zip"));
            Assert.Null(AppUpdateService.ReadPublishedSum(text, "x.zip"));
            Assert.Null(AppUpdateService.ReadPublishedSum(text, "missing.zip"));
            Assert.Null(AppUpdateService.ReadPublishedSum("", "missing.zip"));
        }

        [Fact]
        public void The_download_is_held_to_the_sum_the_list_gives_it()
        {
            var tree = Tree.Make();
            try
            {
                var zip = Path.Combine(tree.Root, "app.zip");
                File.WriteAllBytes(zip, new byte[] { 1, 2, 3, 4 });
                using var sha = SHA256.Create();
                var right = Convert.ToHexString(sha.ComputeHash(new byte[] { 1, 2, 3, 4 })).ToLowerInvariant();
                var wrong = new string('0', 64);

                Assert.Equal(AppUpdateService.SumCheck.Matched, AppUpdateService.CompareSum(right + "  app.zip\n", "app.zip", zip));
                Assert.Equal(AppUpdateService.SumCheck.Mismatch, AppUpdateService.CompareSum(wrong + "  app.zip\n", "app.zip", zip));
                Assert.Equal(AppUpdateService.SumCheck.NotListed, AppUpdateService.CompareSum(right + "  other.zip\n", "app.zip", zip));
            }
            finally
            {
                tree.Delete();
            }
        }

        // ------------------------------------------------------------- fixtures

        internal sealed class Tree
        {
            public string Root { get; private init; }
            public string Install => Path.Combine(Root, "install folder");
            public string Pending => ManagedUpdater.PendingFolder(Install);
            public string Work => Path.Combine(Root, "work");
            public string Exe => Path.Combine(Install, LauncherRoute.LauncherName);

            public static Tree Make()
            {
                var tree = new Tree { Root = LauncherMissingTests.TempRoot() };
                Directory.CreateDirectory(tree.Install);
                return tree;
            }

            public void Delete()
            {
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(Root)) Directory.Delete(Root, true);
                        return;
                    }
                    catch
                    {
                        Thread.Sleep(300);
                    }
                }
            }
        }

        internal sealed class Run
        {
            public int ExitCode { get; init; }
            public string Text { get; init; } = "";
            public UpdateResult Result { get; init; }
        }

        private static int Occurrences(string text, string what)
        {
            var count = 0;
            for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal)) count++;
            return count;
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        /// <summary>
        /// Staging, wired to a temp tree: one release with the app's zip and, when asked, a
        /// SHA256SUMS.txt; the download and the list answered from memory; and a starter that
        /// writes down the call and starts nothing.
        /// </summary>
        private sealed class Staging
        {
            public const string AssetName = "ValheimBakaLoader-99.0.0-win-x64.zip";
            private const string ZipUrl = "https://example.invalid/" + AssetName;
            private const string SumsUrl = "https://example.invalid/" + AppUpdateService.SumsAssetName;

            public AppUpdateService Service { get; private init; }
            public RecordingHttpClientProvider Http { get; private init; }
            public Mock<IGitHubClient> GitHub { get; private init; }
            public List<(string Install, string Pending)> Started { get; } = new();
            private Tree Tree { get; init; }

            /// <param name="tree">The temp tree the work and install folders are in.</param>
            /// <param name="sums">
            /// Null for a release with no list. Otherwise the list's text for the zip's bytes, or
            /// null from it for a list that answers 404.
            /// </param>
            /// <param name="environment">
            /// What the service reads the environment through. Null for an empty one, so no test
            /// here ever depends on a variable set on the machine running the suite.
            /// </param>
            /// <param name="logger">Where the service logs. Null for a logger that keeps nothing.</param>
            public static Staging For(Tree tree, Func<byte[], string> sums,
                Func<string, string> environment = null, IApplicationLogger logger = null)
            {
                File.WriteAllText(Path.Combine(tree.Install, LauncherRoute.LibraryName), "old library");

                var zipPath = Path.Combine(tree.Root, "release.zip");
                MakeZip(zipPath, (LauncherRoute.LibraryName, "new library"), ("marker.txt", "new"));
                var zip = File.ReadAllBytes(zipPath);

                var assets = new List<GitHubReleaseAsset>
                {
                    new() { Name = AssetName, BrowserDownloadUrl = ZipUrl, Size = zip.LongLength },
                };
                if (sums != null)
                    assets.Add(new GitHubReleaseAsset { Name = AppUpdateService.SumsAssetName, BrowserDownloadUrl = SumsUrl, Size = 1 });

                var gitHub = new Mock<IGitHubClient>();
                gitHub.Setup(c => c.GetLatestReleaseAsync()).ReturnsAsync(new GitHubRelease
                {
                    TagName = "v99.0.0",
                    Assets = assets.ToArray(),
                });

                var http = new RecordingHttpClientProvider(request =>
                {
                    var url = request.RequestUri?.ToString() ?? "";
                    if (url == ZipUrl) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
                    if (url == SumsUrl && sums != null)
                    {
                        var text = sums(zip);
                        return text == null
                            ? new HttpResponseMessage(HttpStatusCode.NotFound)
                            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.ASCII) };
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

                var prefs = new MockUserPreferencesProvider();
                prefs.LoadPreferences().CheckForUpdates = true;
                prefs.LoadPreferences().AutoUpdateBakaLoader = true;

                var staging = new Staging
                {
                    Http = http,
                    GitHub = gitHub,
                    Tree = tree,
                    Service = new AppUpdateService(gitHub.Object, http, prefs, logger ?? Mock.Of<IApplicationLogger>())
                    {
                        WorkDirectory = tree.Work,
                        InstallDirectory = tree.Install,
                        EnvironmentReader = environment ?? (_ => null),
                    },
                };
                staging.Service.UpdaterStarter = (install, pending) => staging.Started.Add((install, pending));
                return staging;
            }

            /// <summary>Nothing unpacked, nothing started, the zip gone, the install as it was.</summary>
            public void AssertNothingStaged()
            {
                Assert.Empty(Started);
                Assert.False(Directory.Exists(Tree.Pending), "the release was unpacked beside the install");
                Assert.False(File.Exists(Path.Combine(Tree.Work, "update.zip")), "the zip was left in the work folder");
                Assert.Equal("old library", File.ReadAllText(Path.Combine(Tree.Install, LauncherRoute.LibraryName)));
            }
        }

        private static void Stage(Tree tree, params (string Relative, string Text)[] files)
        {
            foreach (var (relative, text) in files)
            {
                var path = Path.Combine(tree.Pending, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text);
            }
        }

        /// <summary>The library this suite was built against, run from the test build's own folder.</summary>
        private static string UpdaterFromTestBuild() => Path.Combine(AppContext.BaseDirectory, LauncherRoute.LibraryName);

        /// <summary>
        /// Everything the library needs to run, copied from the test build into an install: every
        /// file and folder there except the launcher, so nothing in the tree can be started as the
        /// app by a launcher.
        /// </summary>
        private static void CopyRunnableApp(string install)
        {
            var from = AppContext.BaseDirectory;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(from, file);
                if (string.Equals(relative, LauncherRoute.LauncherName, StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(install, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite: true);
            }
        }

        /// <summary>
        /// The updater, the way the app starts it, with nothing on the desktop: --no-window and the
        /// environment switch both, and no console. Every end to end test goes through here.
        /// </summary>
        private static Process StartUpdater(string library, Tree tree, int pid, params string[] extra)
        {
            var dotnet = ManagedUpdater.FindDotnet();
            Assert.NotNull(dotnet);

            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                WorkingDirectory = tree.Install,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment[ManagedUpdater.NoWindowVariable] = "1";
            psi.ArgumentList.Add(library);
            foreach (var argument in ManagedUpdater.Arguments(pid, tree.Pending, tree.Install)) psi.ArgumentList.Add(argument);
            psi.ArgumentList.Add(ManagedUpdater.NoWindowSwitch);
            psi.ArgumentList.Add(ManagedUpdater.WorkSwitch);
            psi.ArgumentList.Add(tree.Work);
            foreach (var argument in extra) psi.ArgumentList.Add(argument);

            var started = Process.Start(psi);
            Assert.NotNull(started);
            return started;
        }

        /// <summary>A running updater with no top level window of its own.</summary>
        private static void AssertNoWindow(Process updater)
        {
            updater.Refresh();
            if (updater.HasExited) return;
            Assert.Equal(IntPtr.Zero, updater.MainWindowHandle);
        }

        private static Run Finish(Process updater, Tree tree)
        {
            if (!updater.WaitForExit(120_000))
            {
                try { updater.Kill(true); } catch { /* gone */ }
                Assert.True(false, "the updater did not finish inside two minutes");
            }

            var path = Path.Combine(tree.Work, ManagedUpdater.ResultFileName);
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            return new Run { ExitCode = updater.ExitCode, Text = text, Result = UpdateResult.Parse(text) };
        }

        private static Run RunUpdater(string library, Tree tree, int pid, params string[] extra)
        {
            using var updater = StartUpdater(library, tree, pid, extra);
            var run = Finish(updater, tree);
            Assert.True(run.Result != null, "the updater wrote no RESULT.txt, exit " + run.ExitCode);
            return run;
        }

        /// <summary>A process that stands in for the app: it runs until it is ended.</summary>
        private static Process StandInApp()
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-n", "120", "127.0.0.1" }) psi.ArgumentList.Add(argument);
            var started = Process.Start(psi);
            Assert.NotNull(started);
            return started;
        }

        private static string FrozenLauncherFile() =>
            RepoScript.At("ValheimBakaLoader", "Resources", "host", LauncherRoute.LauncherName);

        internal static void MakeZip(string zipPath, params (string Name, string Text)[] entries) =>
            MakeZipBytes(zipPath, entries.Select(e => (e.Name, e.Text == null ? null : System.Text.Encoding.UTF8.GetBytes(e.Text))).ToArray());

        private static void MakeZipBytes(string zipPath, params (string Name, byte[] Bytes)[] entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (bytes == null) continue;
                using var stream = entry.Open();
                stream.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
