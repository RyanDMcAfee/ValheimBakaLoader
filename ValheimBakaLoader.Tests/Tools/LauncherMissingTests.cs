using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// BakaLoader with no launcher.
    /// <para>
    /// WHY THIS EXISTS. On 2026-10-11 Windows Defender's cloud heuristic called the frozen
    /// launcher Trojan:Win32/Bearfoos.B!ml during the 1.2.11 self-update, quarantined it out of
    /// the live install, took it again every time it was put back, and the watchdog read the
    /// missing exe as a failed update and rolled the install back to 1.2.10. The app and the
    /// server stayed down. The launcher is the SDK's generic apphost and nothing more: the app
    /// is ValheimBakaLoader.dll, so <c>dotnet ValheimBakaLoader.dll</c> runs it with no
    /// launcher at all. These hold every road that has to know that: the rule itself, the
    /// watchdog's copy of the rule run against real folders, a whole update driven through the
    /// real script with the launcher gone, and the script that ships for a host to start.
    /// </para>
    /// </summary>
    public class LauncherMissingTests
    {
        // ----------------------------------------------------------------------- the rule

        [Theory]
        [InlineData(true, true, LaunchRoute.Launcher)]
        [InlineData(true, false, LaunchRoute.Launcher)]
        [InlineData(false, true, LaunchRoute.Dotnet)]
        [InlineData(false, false, LaunchRoute.None)]
        public void The_launcher_wins_when_it_is_there_and_the_library_is_the_way_in_when_it_is_not(
            bool launcher, bool library, LaunchRoute expected)
        {
            Assert.Equal(expected, LauncherRoute.For(launcher, library));
        }

        /// <summary>
        /// The watchdog cannot call into the app, because it runs after the app has closed, so
        /// the rule is written a second time in PowerShell. This runs THAT copy, cut out of the
        /// script the service really generates, against real folders, and holds it to the app's
        /// answer for each: launcher and library, library alone (the quarantine), launcher
        /// alone, and neither.
        /// </summary>
        [Fact]
        public void The_watchdog_answers_the_same_route_as_the_app_for_real_folders()
        {
            var root = TempRoot();
            try
            {
                var both = Folder(root, "both", LauncherRoute.LauncherName, LauncherRoute.LibraryName);
                var taken = Folder(root, "launcher taken", LauncherRoute.LibraryName);
                var exeOnly = Folder(root, "launcher only", LauncherRoute.LauncherName);
                var empty = Folder(root, "neither");

                var function = LaunchRouteFunction(WatchdogScript(Paths(root)));
                var folders = new[] { both, taken, exeOnly, empty };
                var command = function + "\n" + string.Join("\n",
                    folders.Select(f => "Get-LaunchRoute '" + f.Replace("'", "''") + "'"));

                var said = RepoScript.Run("powershell", "-NoProfile", "-NonInteractive", "-Command", command);
                Assert.True(said.Ok, "the watchdog's route function did not run: " + said);

                var answers = said.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                Assert.Equal(
                    folders.Select(f => LauncherRoute.In(f).ToString()).ToArray(),
                    answers);
                Assert.Equal(new[] { "Launcher", "Dotnet", "Launcher", "None" }, answers);
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// The two places the old script turned a missing launcher into a rollback or a dead
        /// end. The copy of the launcher only rethrows into the rollback when the old launcher
        /// is still there to put back; and nothing relaunches through the launcher alone any
        /// more, so a missing exe is never the end of the update.
        /// </summary>
        [Fact]
        public void A_missing_launcher_is_never_a_reason_to_roll_back()
        {
            var script = WatchdogScript(Paths(@"C:\Temp\Baka"));

            Assert.Contains("if ((Get-LaunchRoute $installDir) -ne 'Dotnet') { throw (", script);

            // The downloaded launcher is read once, into memory, and a read that fails is a
            // launcher that never arrived; only the write into the install can reach the throw.
            Assert.Contains("[System.IO.File]::ReadAllBytes($exeFile.FullName)", script);
            Assert.Contains("[System.IO.File]::WriteAllBytes($exePath, $newLauncher)", script);
            Assert.DoesNotContain("Copy-Item -Path $exeFile.FullName", script);

            // Every relaunch goes through the one function that knows about dotnet.
            Assert.DoesNotContain("if (Test-Path $exePath) { Start-Process", script);
            Assert.Equal(1, Regex.Matches(script, @"Start-Process -FilePath \$exePath").Count);
            Assert.Contains("Write-Note $note\r\n  Start-BakaLoader".Replace("\r\n", Newline(script)), script);
            Assert.Contains("# 5. Relaunch the updated app", script);
            var relaunch = script.Substring(script.IndexOf("# 5. Relaunch the updated app", StringComparison.Ordinal));
            Assert.Contains("Start-BakaLoader", relaunch.Split('\n')[1]);
        }

        /// <summary>
        /// The folder the update is written into is the app's own folder. It used to be the
        /// folder of the process image, which through dotnet is the .NET install: the update
        /// would have been robocopied over Program Files\dotnet.
        /// </summary>
        [Fact]
        public void The_update_is_written_into_the_apps_own_folder_not_the_hosts()
        {
            var service = AppSourceTree.Files()["AppUpdateService.cs"];

            Assert.Contains("var installDir = LauncherRoute.InstallFolder();", service);
            Assert.DoesNotContain("MainModule", service);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                LauncherRoute.InstallFolder());
        }

        // ------------------------------------------------- the real watchdog, start to end

        /// <summary>
        /// The incident, driven through the real script. The download arrives without its
        /// launcher (taken out of the zip, the way a security tool does it), and the install has
        /// already lost its own. The update must go in, must not be rolled back, and must start
        /// BakaLoader through dotnet, saying so in the note the app reads on its next launch.
        /// <para>
        /// The library here is a stand-in that is not a real assembly, so the dotnet it starts
        /// ends at once and no app ever opens; the note then says it ended straight away. Either
        /// note proves the route was taken, which is the point.
        /// </para>
        /// </summary>
        [Fact]
        public void An_update_whose_launcher_was_taken_installs_and_starts_through_dotnet()
        {
            var root = TempRoot();
            try
            {
                var paths = Paths(root);
                Directory.CreateDirectory(paths.Install);
                File.WriteAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName), "old library");
                File.WriteAllText(Path.Combine(paths.Install, "marker.txt"), "old");

                MakeZip(paths.Zip,
                    (LauncherRoute.LibraryName, "new library"),
                    ("marker.txt", "new"));

                var note = RunWatchdog(paths);

                // In, and not rolled back.
                Assert.Equal("new", File.ReadAllText(Path.Combine(paths.Install, "marker.txt")));
                Assert.Equal("new library", File.ReadAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName)));
                Assert.False(File.Exists(Path.Combine(paths.Install, LauncherRoute.LauncherName)));

                // Said, and started the other way.
                Assert.NotNull(note);
                Assert.Contains("was taken out of the download", note);
                Assert.Contains("through dotnet", note);
                Assert.Contains(LauncherRoute.ScriptName, note);
                Assert.DoesNotContain("put back", note);
                Assert.False(AppUpdateService.IsRoutineNote(note), "a missing launcher is a note the host should see");
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// The launcher arrives and is written, and then will not start: the shape of a file a
        /// security tool has blocked without removing. The old script ended there with nothing
        /// running. Now it goes through dotnet. The stand-in launcher is a few bytes of text,
        /// which Windows refuses to start with no dialog, so nothing ever opens.
        /// </summary>
        [Fact]
        public void A_launcher_that_will_not_start_falls_back_to_dotnet()
        {
            var root = TempRoot();
            try
            {
                var paths = Paths(root);
                Directory.CreateDirectory(paths.Install);
                File.WriteAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName), "old library");

                MakeZip(paths.Zip,
                    (LauncherRoute.LauncherName, "not a program"),
                    (LauncherRoute.LibraryName, "new library"));

                var note = RunWatchdog(paths);

                Assert.Equal("not a program", File.ReadAllText(Path.Combine(paths.Install, LauncherRoute.LauncherName)));
                Assert.Equal("new library", File.ReadAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName)));

                Assert.NotNull(note);
                Assert.Contains("would not start", note);
                Assert.Contains("through dotnet", note);
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// The road 1.2.11 most likely took back to 1.2.10. The download's launcher lands in
        /// the extraction folder and a scanner takes hold of it there, so it is on disk and
        /// cannot be read, while the install still has its own launcher. Before the fix the
        /// copy of that unreadable file threw, the install still had a launcher, and the whole
        /// update was rolled back over it. A downloaded launcher that cannot be read is a
        /// launcher that never arrived: the installed one is kept and the update goes in.
        /// <para>
        /// The file is held the way an on-access scan holds it, opened with no sharing the
        /// moment it can be opened, and kept that way for the whole run.
        /// </para>
        /// </summary>
        [Fact]
        public void A_downloaded_launcher_held_by_a_scanner_keeps_the_installed_one_and_rolls_nothing_back()
        {
            var root = TempRoot();
            try
            {
                var paths = Paths(root);
                Directory.CreateDirectory(paths.Install);
                File.WriteAllText(paths.Exe, "frozen launcher");
                File.WriteAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName), "old library");
                File.WriteAllText(Path.Combine(paths.Install, "marker.txt"), "old");

                MakeZip(paths.Zip,
                    (LauncherRoute.LauncherName, "frozen launcher"),
                    (LauncherRoute.LibraryName, "new library"),
                    ("marker.txt", "new"));

                string note;
                using (var scanner = new HoldWhenItAppears(
                    Path.Combine(paths.Extract, LauncherRoute.LauncherName), FileShare.None))
                {
                    note = RunWatchdog(paths);
                    Assert.True(scanner.Held, "the stand-in scanner never got hold of the downloaded launcher");
                }

                // In, not rolled back, and the installed launcher untouched.
                Assert.Equal("new", File.ReadAllText(Path.Combine(paths.Install, "marker.txt")));
                Assert.Equal("new library", File.ReadAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName)));
                Assert.Equal("frozen launcher", File.ReadAllText(paths.Exe));

                Assert.NotNull(note);
                Assert.DoesNotContain("put back", note);
                Assert.Contains("already installed was kept", note);
                Assert.False(AppUpdateService.IsRoutineNote(note), "a launcher a scanner held is a note the host should see");
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// A downloaded launcher cut short reads perfectly well and is still not the launcher.
        /// Written over the good one, every start through it would fail until the next update.
        /// The stand-in here takes the file as soon as it exists, which in practice is while the
        /// extraction is still writing it: the plain extraction then stops, the entry by entry
        /// pass is refused the launcher, and the cut file is left in the extraction folder. That
        /// is the half written launcher a refused write leaves behind, and it must never count
        /// as arrived. A launcher cut short after a clean extraction is caught by its size
        /// instead, which the test below drives directly.
        /// </summary>
        [Fact]
        public void A_downloaded_launcher_cut_short_is_never_written_over_the_installed_one()
        {
            var root = TempRoot();
            try
            {
                var paths = Paths(root);
                Directory.CreateDirectory(paths.Install);
                File.WriteAllText(paths.Exe, "old launcher");
                File.WriteAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName), "old library");

                MakeZip(paths.Zip,
                    (LauncherRoute.LauncherName, "a new launcher that is longer"),
                    (LauncherRoute.LibraryName, "new library"));

                string note;
                using (var cutter = new HoldWhenItAppears(
                    Path.Combine(paths.Extract, LauncherRoute.LauncherName), FileShare.None, cutTo: 3))
                {
                    note = RunWatchdog(paths);
                    Assert.True(cutter.Held, "the stand-in never got hold of the downloaded launcher");
                }

                Assert.Equal("old launcher", File.ReadAllText(paths.Exe));
                Assert.Equal("new library", File.ReadAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName)));

                Assert.NotNull(note);
                Assert.True(
                    note.Contains("where the download says") || note.Contains("was taken out of the download"),
                    "the cut launcher was not let go: " + note);
                Assert.Contains("already installed was kept", note);
                Assert.DoesNotContain("put back", note);
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// The size check itself, cut out of the generated script and run against a real zip:
        /// the length the zip gives the launcher at the path it landed on, nested one folder
        /// deep the way a release may be, and -1 for a path the zip does not hold. And the check
        /// sits between the read and the decision, so a launcher of the wrong size never reaches
        /// the write.
        /// </summary>
        [Fact]
        public void The_launcher_is_held_to_the_size_the_zip_gives_it()
        {
            var root = TempRoot();
            try
            {
                var paths = Paths(root);
                MakeZip(paths.Zip,
                    ("pkg/" + LauncherRoute.LauncherName, "twelve bytes"),
                    ("pkg/" + LauncherRoute.LibraryName, "x"));

                var script = WatchdogScript(paths);
                var from = script.IndexOf("function Get-LauncherEntryLength", StringComparison.Ordinal);
                var to = script.IndexOf(Newline(script) + "}" + Newline(script), from, StringComparison.Ordinal);
                Assert.True(from >= 0 && to > from, "Get-LauncherEntryLength is missing from the watchdog");
                var function = script.Substring(from, to - from + 3);

                string Quote(string s) => "'" + s.Replace("'", "''") + "'";
                var command = "$extractDir = " + Quote(paths.Extract) + "\n$zipPath = " + Quote(paths.Zip) + "\n"
                    + function + "\n"
                    + "Get-LauncherEntryLength " + Quote(Path.Combine(paths.Extract, "pkg", LauncherRoute.LauncherName)) + "\n"
                    + "Get-LauncherEntryLength " + Quote(Path.Combine(paths.Extract, LauncherRoute.LauncherName)) + "\n";

                var said = RepoScript.Run("powershell", "-NoProfile", "-NonInteractive", "-Command", command);
                Assert.True(said.Ok, "the size function did not run: " + said);
                var answers = said.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                Assert.Equal(new[] { "12", "-1" }, answers);

                var read = script.IndexOf("[System.IO.File]::ReadAllBytes($exeFile.FullName)", StringComparison.Ordinal);
                var check = script.IndexOf("$expected -ne $newLauncher.Length", StringComparison.Ordinal);
                var write = script.IndexOf("[System.IO.File]::WriteAllBytes($exePath, $newLauncher)", StringComparison.Ordinal);
                Assert.True(read > 0 && check > read && write > check, "the size is not checked between the read and the write");
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// The launcher entry that will not extract at all. A directory entry named like the
        /// launcher sits ahead of the launcher itself, so the plain extraction stops on the
        /// launcher and only the entry by entry pass can bring in the library and the rest
        /// behind it. The launcher is let go, the installed one is kept, and nothing is rolled
        /// back.
        /// </summary>
        [Fact]
        public void A_launcher_that_will_not_extract_is_let_go_and_the_rest_of_the_release_still_lands()
        {
            var root = TempRoot();
            try
            {
                var paths = Paths(root);
                Directory.CreateDirectory(paths.Install);
                File.WriteAllText(paths.Exe, "frozen launcher");
                File.WriteAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName), "old library");
                File.WriteAllText(Path.Combine(paths.Install, "marker.txt"), "old");

                MakeZip(paths.Zip,
                    (LauncherRoute.LauncherName + "/", null),
                    (LauncherRoute.LauncherName, "new launcher"),
                    (LauncherRoute.LibraryName, "new library"),
                    ("marker.txt", "new"));

                var note = RunWatchdog(paths);

                Assert.Equal("new", File.ReadAllText(Path.Combine(paths.Install, "marker.txt")));
                Assert.Equal("new library", File.ReadAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName)));
                Assert.Equal("frozen launcher", File.ReadAllText(paths.Exe));

                Assert.NotNull(note);
                Assert.Contains("was taken out of the download", note);
                Assert.Contains("already installed was kept", note);
                Assert.DoesNotContain("put back", note);
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// The launcher arrives and reads fine, and it is the WRITE into the install that fails,
        /// with no launcher in the install to put back. That is the launcher taken and the app
        /// intact, so the update stays in. The write is made to fail by a folder standing where
        /// the launcher goes: the install has no launcher file, and nothing can be written there.
        /// </summary>
        [Fact]
        public void A_launcher_that_cannot_be_written_into_an_install_with_none_rolls_nothing_back()
        {
            var root = TempRoot();
            try
            {
                var paths = Paths(root);
                Directory.CreateDirectory(paths.Install);
                Directory.CreateDirectory(paths.Exe);
                File.WriteAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName), "old library");
                File.WriteAllText(Path.Combine(paths.Install, "marker.txt"), "old");

                MakeZip(paths.Zip,
                    (LauncherRoute.LauncherName, "new launcher"),
                    (LauncherRoute.LibraryName, "new library"),
                    ("marker.txt", "new"));

                var note = RunWatchdog(paths);

                Assert.Equal("new", File.ReadAllText(Path.Combine(paths.Install, "marker.txt")));
                Assert.Equal("new library", File.ReadAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName)));
                Assert.False(File.Exists(Path.Combine(paths.Exe, LauncherRoute.LauncherName)),
                    "the launcher was copied into the folder in its place");

                Assert.NotNull(note);
                Assert.Contains("could not be written", note);
                Assert.Contains("nothing was rolled back", note);
                Assert.DoesNotContain("put back", note);
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// And the one launcher failure that still rolls back: the new launcher reads fine, the
        /// installed one is held so it cannot be written over, and it is still there to put
        /// back. The library and the rest go back to what they were, and the relaunch after the
        /// rollback goes through the same Start-BakaLoader as every other relaunch.
        /// </summary>
        [Fact]
        public void A_readable_launcher_that_cannot_replace_the_installed_one_still_rolls_back()
        {
            var root = TempRoot();
            try
            {
                var paths = Paths(root);
                Directory.CreateDirectory(paths.Install);
                File.WriteAllText(paths.Exe, "old launcher");
                File.WriteAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName), "old library");
                File.WriteAllText(Path.Combine(paths.Install, "marker.txt"), "old");

                MakeZip(paths.Zip,
                    (LauncherRoute.LauncherName, "new launcher"),
                    (LauncherRoute.LibraryName, "new library"),
                    ("marker.txt", "new"));

                string note;
                using (new FileStream(paths.Exe, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    note = RunWatchdog(paths);
                }

                Assert.Equal("old", File.ReadAllText(Path.Combine(paths.Install, "marker.txt")));
                Assert.Equal("old library", File.ReadAllText(Path.Combine(paths.Install, LauncherRoute.LibraryName)));
                Assert.Equal("old launcher", File.ReadAllText(paths.Exe));

                Assert.NotNull(note);
                Assert.Contains("put back", note);
                // Start-BakaLoader ran on the rollback road: the stand-in launcher will not
                // start, so it went on through dotnet and said so.
                Assert.Contains("would not start", note);
                Assert.Contains("through dotnet", note);
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ------------------------------------------------------------ the script that ships

        /// <summary>
        /// BakaLoader.cmd is content of the app project, copied to the output and to a publish,
        /// so it lands beside the exe in the folder the release zip is made from. This test
        /// project references the app, and content flows to a referencing project the same way,
        /// so the file beside this assembly is the build's own copy of it.
        /// </summary>
        [Fact]
        public void BakaLoader_cmd_is_in_the_published_content_and_lands_beside_the_launcher()
        {
            var csproj = XDocument.Parse(File.ReadAllText(
                RepoScript.At("ValheimBakaLoader", "ValheimBakaLoader.csproj")));

            var item = csproj.Descendants("Content")
                .SingleOrDefault(e => (string)e.Attribute("Include") == LauncherRoute.ScriptName);
            Assert.NotNull(item);
            Assert.Equal("PreserveNewest", (string)item.Element("CopyToOutputDirectory"));
            Assert.Equal("PreserveNewest", (string)item.Element("CopyToPublishDirectory"));
            Assert.Null(item.Attribute("Condition"));

            // Except a single file publish, which has no library beside the exe for it to start.
            var singleFile = item.Elements("CopyToPublishDirectory")
                .SingleOrDefault(e => e.Attribute("Condition") != null);
            Assert.NotNull(singleFile);
            Assert.Equal("Never", (string)singleFile);
            Assert.Contains("$(PublishSingleFile)", (string)singleFile.Attribute("Condition"));

            var built = Path.Combine(AppContext.BaseDirectory, LauncherRoute.ScriptName);
            Assert.True(File.Exists(built), "the build did not copy " + LauncherRoute.ScriptName + " beside the app");
            Assert.Equal(
                File.ReadAllBytes(RepoScript.At("ValheimBakaLoader", LauncherRoute.ScriptName)),
                File.ReadAllBytes(built));
        }

        /// <summary>
        /// What the script says and how it is written. CRLF, because cmd reads a batch file with
        /// bare line feeds badly; plain ASCII with no byte order mark, because cmd runs the bytes
        /// it is given; the app through dotnet with nothing in front of it, because conhost
        /// --headless is a hidden-console pattern security heuristics look for and the app closes
        /// the dotnet console itself; the Desktop Runtime 6 checked, because a dotnet that cannot run
        /// the app would end on a console nobody can see; and one line naming the runtime when it
        /// is missing.
        /// </summary>
        [Fact]
        public void BakaLoader_cmd_starts_the_library_through_dotnet_with_no_window_and_names_the_runtime()
        {
            var bytes = File.ReadAllBytes(RepoScript.At("ValheimBakaLoader", LauncherRoute.ScriptName));
            Assert.All(bytes, b => Assert.True(b < 0x80, "BakaLoader.cmd is not plain ASCII"));

            var text = Encoding.ASCII.GetString(bytes);
            Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
            Assert.EndsWith("\r\n", text);

            Assert.Contains("start \"\" /d \"%HERE%.\" \"%DOTNET%\" \"%HERE%ValheimBakaLoader.dll\"", text);
            Assert.DoesNotContain("conhost", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--headless", text);
            Assert.Contains("findstr /b /c:\"Microsoft.WindowsDesktop.App 6.\"", text);
            Assert.Contains("https://dotnet.microsoft.com/download/dotnet/6.0", text);
            Assert.DoesNotContain("ValheimBakaLoader.exe\" ", text);

            // And the README sends a host there with the same runtime link.
            var readme = File.ReadAllText(RepoScript.At("README.md"));
            Assert.Contains(LauncherRoute.ScriptName, readme);
            Assert.Contains("https://dotnet.microsoft.com/download/dotnet/6.0", readme);

            // And the repository keeps it CRLF whatever the checkout does.
            Assert.Contains("ValheimBakaLoader/BakaLoader.cmd text eol=crlf",
                File.ReadAllText(RepoScript.At(".gitattributes")));
        }

        // -------------------------------------------------------------- the line at startup

        /// <summary>
        /// The one line the Saga gets when this run started without its launcher: from the
        /// catalog, naming the script, and only on the Dotnet route.
        /// </summary>
        [Fact]
        public void A_run_without_its_launcher_says_so_once_and_names_the_script()
        {
            var line = HostCatalog.EmbeddedEnglish.Say(
                "host.saga.launcher_missing", ("script", LauncherRoute.ScriptName));

            Assert.Contains("BakaLoader.cmd", line);
            Assert.Contains("dotnet", line);
            Assert.DoesNotContain("{script}", line);
            Assert.DoesNotContain("host.saga", line);

            var program = AppSourceTree.Read("ValheimBakaLoader", "Program.cs");
            Assert.Contains("SayWhenTheLauncherIsMissing(container);", program);
            Assert.Contains("LauncherRoute.MissingLauncherLine(LauncherRoute.InstallFolder())", program);

            // Only on the Dotnet route, against real folders.
            var root = TempRoot();
            try
            {
                Assert.Null(LauncherRoute.MissingLauncherLine(
                    Folder(root, "both", LauncherRoute.LauncherName, LauncherRoute.LibraryName)));
                Assert.Null(LauncherRoute.MissingLauncherLine(Folder(root, "neither")));
                Assert.Equal(HostCatalog.T("host.saga.launcher_missing", ("script", LauncherRoute.ScriptName)),
                    LauncherRoute.MissingLauncherLine(Folder(root, "taken", LauncherRoute.LibraryName)));
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ----------------------------------------------------------------------- fixtures

        internal sealed class WatchdogPaths
        {
            public string Root { get; init; }
            public string Work => Path.Combine(Root, "work");
            public string Zip => Path.Combine(Work, "update.zip");
            public string Extract => Path.Combine(Work, "extracted");
            public string Install => Path.Combine(Root, "install folder");
            public string Exe => Path.Combine(Install, LauncherRoute.LauncherName);
            public string Report => Path.Combine(Root, "report", "update-report.txt");
        }

        internal static WatchdogPaths Paths(string root) => new() { Root = root };

        /// <summary>
        /// Runs the real watchdog over a temp tree and answers the note it left. The app's PID
        /// is one that cannot be running, so the wait ends at once; the report is moved into the
        /// temp tree so nothing is ever written where a real BakaLoader would read it.
        /// </summary>
        internal static string RunWatchdog(WatchdogPaths paths)
        {
            var script = WatchdogScript(paths);
            script = Regex.Replace(script, @"(?m)^\$reportPath = '.*'\r?$",
                "$reportPath = '" + paths.Report.Replace("'", "''") + "'");
            Assert.Contains(paths.Report.Replace("'", "''"), script);

            var file = Path.Combine(paths.Root, "update-watchdog.ps1");
            AppUpdateService.WriteWatchdogScript(file, script);

            var said = RepoScript.Run("powershell", "-NoProfile", "-NonInteractive",
                "-ExecutionPolicy", "Bypass", "-File", file);
            Assert.True(said.ExitCode == 0 || said.ExitCode == 1, "the watchdog did not run: " + said);

            return File.Exists(paths.Report) ? File.ReadAllText(paths.Report).Trim() : null;
        }

        /// <summary>The script the watchdog runs, built the way the service builds it.</summary>
        internal static string WatchdogScript(WatchdogPaths paths)
        {
            var build = typeof(AppUpdateService).GetMethod(
                "BuildWatchdogScript", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(build);

            return (string)build.Invoke(null, new object[]
            {
                2147480000,
                paths.Zip,
                paths.Extract,
                paths.Install,
                paths.Exe,
                paths.Work,
            });
        }

        /// <summary>The route function, cut out of the generated script between its markers.</summary>
        private static string LaunchRouteFunction(string script)
        {
            var from = script.IndexOf("# BEGIN launch route", StringComparison.Ordinal);
            var to = script.IndexOf("# END launch route", StringComparison.Ordinal);
            Assert.True(from >= 0 && to > from, "the launch route markers are missing from the watchdog");
            return script.Substring(from, to - from);
        }

        private static string Newline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

        internal static void MakeZip(string zipPath, params (string Name, string Text)[] entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (text == null) continue; // a directory entry
                using var writer = new StreamWriter(entry.Open());
                writer.Write(text);
            }
        }

        /// <summary>
        /// A stand-in for an on-access scan: it opens a file the moment the file can be opened,
        /// with the sharing it is given, and holds it until it is disposed.
        /// </summary>
        private sealed class HoldWhenItAppears : IDisposable
        {
            private readonly System.Threading.CancellationTokenSource _stop = new();
            private readonly System.Threading.Thread _thread;
            private FileStream _held;

            /// <param name="cutTo">When given, the file is cut to this many bytes as it is taken,
            /// the way a write stopped part way leaves it, and then held.</param>
            public HoldWhenItAppears(string path, FileShare share, int? cutTo = null)
            {
                _thread = new System.Threading.Thread(() =>
                {
                    while (!_stop.IsCancellationRequested && _held == null)
                    {
                        try
                        {
                            var stream = new FileStream(path, FileMode.Open,
                                cutTo.HasValue ? FileAccess.ReadWrite : FileAccess.Read, share);
                            if (cutTo.HasValue)
                            {
                                stream.SetLength(cutTo.Value);
                                stream.Flush(true);
                                stream.Dispose();
                                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            }
                            _held = stream;
                        }
                        catch (IOException) { System.Threading.Thread.Yield(); }
                        catch (UnauthorizedAccessException) { System.Threading.Thread.Yield(); }
                    }
                    _stop.Token.WaitHandle.WaitOne();
                }) { IsBackground = true };
                _thread.Start();
            }

            public bool Held => _held != null;

            public void Dispose()
            {
                _stop.Cancel();
                _thread.Join(5000);
                _held?.Dispose();
            }
        }

        private static string Folder(string root, string name, params string[] files)
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            foreach (var file in files) File.WriteAllText(Path.Combine(folder, file), "x");
            return folder;
        }

        /// <summary>
        /// A temp folder with a space, a quote and a non-ASCII letter in its name, because every
        /// path the watchdog is handed is a host's folder and hosts name folders.
        /// </summary>
        internal static string TempRoot()
        {
            var root = Path.Combine(Path.GetTempPath(),
                "baka launcher's " + "\u00e4" + " " + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            return root;
        }

        internal static void TryDelete(string folder)
        {
            try { Directory.Delete(folder, true); } catch { /* best effort */ }
        }
    }
}
