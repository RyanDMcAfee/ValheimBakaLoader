using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The line under each of the two Directories boxes, as a table.
    /// <para>
    /// The rule is split from the disk on purpose. Judge is handed a row of facts and
    /// answers the same way on every machine, so the cases that matter are the ones that
    /// are awkward to arrange for real: a folder that exists and refuses a write, an exe
    /// with no managed assemblies beside it, a path Windows will not even parse. None of
    /// these tests touches a disk, and none of them needs a Valheim install to run.
    /// </para>
    /// </summary>
    public class PathCheckTests
    {
        private const string Exe = PathCheck.KindExe;
        private const string Dir = PathCheck.KindDir;

        private static PathFacts Facts(
            bool file = false, bool folder = false, bool sibling = false,
            bool parent = false, bool writable = false, bool unreadable = false,
            bool isFile = false, bool isFolder = false)
            => new()
            {
                FileExists = file,
                DirectoryExists = folder,
                SiblingDataFolder = sibling,
                ParentExists = parent,
                Writable = writable,
                Unreadable = unreadable,
                PathIsFile = isFile,
                PathIsDirectory = isFolder,
            };

        /// <summary>A double quote, written as its code point so a test about quotes is not
        /// read through a row of escapes.</summary>
        private const char Quote = (char)34;

        /// <summary>The same string with a double quote on either side of it.</summary>
        private static string Quoted(string inner) => Quote.ToString() + inner + Quote.ToString();

        // ------------------------------------------------------------------ the exe box

        /// <summary>
        /// A real dedicated server: the right file name, the file is there, and the
        /// managed assemblies are in the folder beside it. Nothing to say.
        /// </summary>
        [Fact]
        public void A_real_server_install_has_nothing_to_say()
        {
            var answer = PathCheck.Judge(
                Exe, @"D:\Steam\Valheim dedicated server\valheim_server.exe",
                Facts(file: true, sibling: true));

            Assert.Null(answer.ProblemId);
            Assert.True(answer.Exists);
            Assert.True(answer.LooksRight);
        }

        /// <summary>
        /// The file the host picked is there and is not the one a dedicated server starts
        /// from. This is said before "there is no file", because there IS one: the reader
        /// pointed at the game's own launcher, and being told it is missing is confusing.
        /// </summary>
        [Theory]
        [InlineData(@"D:\Steam\Valheim\valheim.exe")]
        [InlineData(@"D:\Steam\Valheim dedicated server\start_headless_server.bat")]
        [InlineData(@"D:\Steam\Valheim dedicated server")]
        public void A_file_that_is_not_the_server_is_named_as_the_wrong_file(string path)
        {
            var answer = PathCheck.Judge(Exe, path, Facts(file: true, sibling: true));

            Assert.Equal(PathCheck.ProblemExeWrongName, answer.ProblemId);
            Assert.False(answer.LooksRight);
        }

        [Fact]
        public void A_server_exe_that_is_not_there_says_so()
        {
            var answer = PathCheck.Judge(
                Exe, @"E:\gone\valheim_server.exe", Facts(file: false, sibling: true));

            Assert.Equal(PathCheck.ProblemExeMissing, answer.ProblemId);
            Assert.False(answer.Exists);
            Assert.False(answer.LooksRight);
        }

        /// <summary>
        /// The exe copied out of its install. It starts and dies with nothing on screen,
        /// because what a launch actually loads is the folder beside it.
        /// </summary>
        [Fact]
        public void A_server_exe_with_no_data_folder_beside_it_is_half_an_install()
        {
            var answer = PathCheck.Judge(
                Exe, @"E:\copied\valheim_server.exe", Facts(file: true, sibling: false));

            Assert.Equal(PathCheck.ProblemExeNoDataFolder, answer.ProblemId);
            Assert.True(answer.Exists);
            Assert.False(answer.LooksRight);
        }

        /// <summary>The name is matched whatever case it was typed in.</summary>
        [Fact]
        public void The_server_file_name_is_matched_without_regard_to_case()
        {
            var answer = PathCheck.Judge(
                Exe, @"D:\Steam\Valheim dedicated server\VALHEIM_SERVER.EXE",
                Facts(file: true, sibling: true));

            Assert.Null(answer.ProblemId);
            Assert.True(answer.LooksRight);
        }

        // --------------------------------------------------------------- the folder box

        [Fact]
        public void A_folder_that_is_there_and_takes_a_write_has_nothing_to_say()
        {
            var answer = PathCheck.Judge(Dir, @"D:\ValheimSaves", Facts(folder: true, writable: true));

            Assert.Null(answer.ProblemId);
            Assert.True(answer.Exists);
            Assert.True(answer.LooksRight);
            Assert.True(answer.Writable);
        }

        /// <summary>
        /// A folder that will not take a write is the worst of the folder cases, because
        /// everything about it looks right until the first world save fails.
        /// </summary>
        [Fact]
        public void A_folder_that_refuses_a_write_is_named_as_such()
        {
            var answer = PathCheck.Judge(Dir, @"C:\Program Files\Saves", Facts(folder: true, writable: false));

            Assert.Equal(PathCheck.ProblemDirReadOnly, answer.ProblemId);
            Assert.True(answer.Exists);
            Assert.False(answer.Writable);
            Assert.False(answer.LooksRight);
        }

        /// <summary>
        /// Not there yet is fine as long as the folder above it is: the server makes its
        /// own save folder on first start. Said out loud anyway, because a host who has
        /// just typed a path wants to know it was understood. This is the one answer that
        /// carries a sentence AND still counts as right.
        /// </summary>
        [Fact]
        public void A_folder_that_can_be_created_says_it_will_be_and_is_still_right()
        {
            var answer = PathCheck.Judge(Dir, @"D:\Games\NewSaves", Facts(folder: false, parent: true));

            Assert.Equal(PathCheck.ProblemDirWillBeCreated, answer.ProblemId);
            Assert.True(answer.LooksRight);
            Assert.False(answer.Exists);
        }

        [Fact]
        public void A_folder_whose_parent_is_missing_cannot_be_created_either()
        {
            var answer = PathCheck.Judge(Dir, @"Q:\nowhere\at\all", Facts(folder: false, parent: false));

            Assert.Equal(PathCheck.ProblemDirMissingParent, answer.ProblemId);
            Assert.False(answer.LooksRight);
        }

        /// <summary>
        /// A FILE in the folder box. The answer used to be the cheerful one: no folder is
        /// there and the folder above it is, so the rule fell through to "the folder is not
        /// there yet, it is created the first time the server starts". It is not going to
        /// be. Windows will not make a folder over a file of that name, so the sentence was
        /// a promise the first start would break, on the one screen where it could still
        /// have been fixed in a second.
        /// <para>
        /// The parent being there or not makes no difference, which is the point of both
        /// rows: a file at this path settles the question before either of those arms is
        /// reached.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_file_where_the_save_folder_should_be_is_named_as_a_file(bool parent)
        {
            var answer = PathCheck.Judge(
                Dir, @"D:\Saves\worlds_local.fwl", Facts(folder: false, parent: parent, isFile: true));

            Assert.Equal(PathCheck.ProblemDirIsFile, answer.ProblemId);
            Assert.True(answer.Exists);
            Assert.False(answer.LooksRight);
            Assert.False(answer.Writable);
        }

        /// <summary>
        /// The mirror, and it read wrong in two different ways before this.
        /// <para>
        /// Pasting the install FOLDER into the executable box is the ordinary way to get
        /// here, and the answer was "a dedicated server is started from valheim_server.exe,
        /// and this path names another file", which names a file that is not one and sends
        /// the reader looking at the name they typed rather than at what it points to. The
        /// second row is the awkward one: a folder that happens to be called
        /// valheim_server.exe passed the name rule, failed File.Exists, and was answered
        /// with "there is no file at this path" about a path with something plainly at it.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(@"D:\Steam\steamapps\common\Valheim dedicated server")]
        [InlineData(@"D:\Steam\steamapps\common\valheim_server.exe")]
        public void A_folder_where_the_server_program_should_be_is_named_as_a_folder(string path)
        {
            var answer = PathCheck.Judge(Exe, path, Facts(file: false, isFolder: true));

            Assert.Equal(PathCheck.ProblemExeIsFolder, answer.ProblemId);
            Assert.True(answer.Exists);
            Assert.False(answer.LooksRight);
        }

        /// <summary>
        /// And neither new arm fires on a row that does not carry the fact, which is what
        /// keeps every answer that was already right exactly where it was. The old rows
        /// were all written before these two facts existed, so every one of them says
        /// false to both, and that has to go on meaning what it always meant.
        /// </summary>
        [Fact]
        public void A_path_with_nothing_at_it_is_answered_the_way_it_always_was()
        {
            Assert.Equal(PathCheck.ProblemDirWillBeCreated,
                PathCheck.Judge(Dir, @"D:\Games\NewSaves", Facts(parent: true)).ProblemId);
            Assert.Equal(PathCheck.ProblemDirMissingParent,
                PathCheck.Judge(Dir, @"Q:\nowhere\at\all", Facts()).ProblemId);
            Assert.Equal(PathCheck.ProblemExeMissing,
                PathCheck.Judge(Exe, @"E:\gone\valheim_server.exe", Facts(sibling: true)).ProblemId);
            Assert.Equal(PathCheck.ProblemExeWrongName,
                PathCheck.Judge(Exe, @"D:\Steam\Valheim\valheim.exe", Facts(file: true, sibling: true)).ProblemId);
        }

        /// <summary>
        /// Both of them off the real disk, end to end, in a temporary folder this test owns
        /// and removes. This is the half a table cannot answer: whether Look actually asks
        /// the question the rule now reads.
        /// </summary>
        [Fact]
        public void A_real_file_and_a_real_folder_are_each_named_for_what_they_are()
        {
            var folder = Path.Combine(Path.GetTempPath(), "baka-pathcheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                // A file where a save folder was asked for.
                var file = Path.Combine(folder, "worlds_local");
                File.WriteAllText(file, "");

                var facts = PathCheck.Look(PathCheck.KindDir, file);
                Assert.True(facts.PathIsFile);
                Assert.False(facts.DirectoryExists);
                Assert.Equal(PathCheck.ProblemDirIsFile, PathCheck.Check(PathCheck.KindDir, file).ProblemId);

                // A folder where the server program was asked for, both ways round: a
                // folder with an ordinary name, and one named like the program itself.
                var named = Path.Combine(folder, PathCheck.ServerExeName);
                Directory.CreateDirectory(named);

                foreach (var asFolder in new[] { folder, named })
                {
                    var look = PathCheck.Look(PathCheck.KindExe, asFolder);
                    Assert.True(look.PathIsDirectory, asFolder + " is not being seen as a folder");
                    Assert.False(look.FileExists);
                    Assert.Equal(PathCheck.ProblemExeIsFolder,
                        PathCheck.Check(PathCheck.KindExe, asFolder).ProblemId);
                }

                // And the ordinary case is untouched: a real file at a real path is still a
                // file, and neither new fact is set on it.
                var exe = Path.Combine(named, PathCheck.ServerExeName);
                File.WriteAllText(exe, "");
                Directory.CreateDirectory(Path.Combine(named, PathCheck.ServerDataFolderName));

                var real = PathCheck.Look(PathCheck.KindExe, exe);
                Assert.True(real.FileExists);
                Assert.False(real.PathIsDirectory);
                Assert.Null(PathCheck.Check(PathCheck.KindExe, exe).ProblemId);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { /* the temp folder can go later */ }
            }
        }

        // ------------------------------------------------------------------ both boxes

        /// <summary>
        /// An empty box is the normal state of both of these: it means "use the default",
        /// which the note under the section says once rather than under each box.
        /// </summary>
        [Theory]
        [InlineData(PathCheck.KindExe)]
        [InlineData(PathCheck.KindDir)]
        public void An_empty_box_is_not_a_problem(string kind)
        {
            foreach (var typed in new[] { "", "   ", null })
            {
                var answer = PathCheck.Judge(kind, typed, Facts());
                Assert.Null(answer.ProblemId);
                Assert.Equal("", answer.Expanded);
            }
        }

        [Theory]
        [InlineData(PathCheck.KindExe, @"K:\a<b>c\valheim_server.exe")]
        [InlineData(PathCheck.KindDir, @"K:\a|b\saves")]
        public void A_path_windows_will_not_parse_says_that_and_nothing_else(string kind, string path)
        {
            var answer = PathCheck.Judge(kind, path, Facts(unreadable: true));

            Assert.Equal(PathCheck.ProblemUnreadable, answer.ProblemId);
            Assert.False(answer.LooksRight);
            Assert.False(answer.Exists);
        }

        [Fact]
        public void An_unknown_kind_is_a_programming_mistake_rather_than_an_answer()
            => Assert.Throws<ArgumentException>(() => PathCheck.Judge("shoe", @"C:\x", Facts()));

        // ------------------------------------------------------------- expand and resolve

        [Fact]
        public void Expanding_fills_in_the_variables_and_never_throws()
        {
            Assert.Equal("", PathCheck.Expand(null));
            Assert.Equal("", PathCheck.Expand("   "));
            Assert.Equal(@"C:\plain\path", PathCheck.Expand(@"  C:\plain\path  "));
            Assert.DoesNotContain("%", PathCheck.Expand(@"%USERPROFILE%\AppData"));
        }

        /// <summary>
        /// Explorer's own "Copy as path" wraps what it puts on the clipboard in double
        /// quotes, which makes a quoted path the commonest way one arrives in these boxes,
        /// and it used to arrive unusable: the closing quote was part of the string, so the
        /// name no longer ended in valheim_server.exe and the line under the box said the
        /// file was the wrong one. The file name was right. A pair around the whole thing
        /// comes off; a quote anywhere else stays, and is a mark Windows will not take.
        /// </summary>
        [Fact]
        public void A_path_pasted_with_the_quotes_explorer_puts_round_it_is_read_as_the_path()
        {
            const string real = @"D:\Steam\Valheim dedicated server\valheim_server.exe";
            var quoted = Quoted(real);

            Assert.Equal(real, PathCheck.Expand(quoted));
            Assert.Equal(real, PathCheck.Expand("  " + quoted + "  "));

            // And the whole way through: the name is judged on the path rather than on the
            // quotes round it. This is the answer that used to be exe_wrong_name.
            var answer = PathCheck.Judge(Exe, PathCheck.Expand(quoted), Facts(file: true, sibling: true));
            Assert.Null(answer.ProblemId);
            Assert.True(answer.LooksRight);

            // A lone quote is not a pair and is left where it is.
            Assert.Equal(Quoted("") + @"C:\half", PathCheck.Expand(Quoted("") + @"C:\half"));
            Assert.Equal("", PathCheck.Expand(Quoted("")));
            Assert.Equal("", PathCheck.Expand(Quoted("   ")));
        }

        /// <summary>
        /// The marks Windows keeps out of a file name, and the names it keeps for devices.
        /// <para>
        /// This is a written rule rather than a caught exception because there is no
        /// exception to catch. Driven against the real disk before it was written: .NET 6
        /// answers Path.GetFullPath(@"K:\a&lt;b&gt;c") with the same string, File.Exists
        /// answers false, and every row below reached the disk and came back as a missing
        /// file or a missing parent. The reader was told to go looking for a file rather
        /// than told the path could not be one.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(PathCheck.KindExe, @"K:\a<b>c\valheim_server.exe")]
        [InlineData(PathCheck.KindDir, @"K:\a|b\saves")]
        [InlineData(PathCheck.KindExe, @"C:\a*b\valheim_server.exe")]
        [InlineData(PathCheck.KindDir, @"C:\Steam\sa?ves")]
        [InlineData(PathCheck.KindDir, "::::")]
        [InlineData(PathCheck.KindDir, @"C:\Saves\CON")]
        [InlineData(PathCheck.KindDir, @"C:\Saves\nul.txt")]
        [InlineData(PathCheck.KindExe, @"C:\lpt1\valheim_server.exe")]
        public void A_path_windows_will_not_take_is_answered_as_such_off_the_real_disk(string kind, string path)
        {
            Assert.True(PathCheck.WindowsWillNotTake(path), path + " is not being turned away");

            var facts = PathCheck.Look(kind, path);
            Assert.True(facts.Unreadable);
            Assert.False(facts.FileExists);
            Assert.False(facts.DirectoryExists);

            var answer = PathCheck.Check(kind, path);
            Assert.Equal(PathCheck.ProblemUnreadable, answer.ProblemId);
            Assert.False(answer.LooksRight);
        }

        /// <summary>
        /// And the other way: an ordinary path is not turned away by the rule. The drive
        /// letter's own colon, a device name that is only the START of a real name, and a
        /// folder with a full stop in it are all paths Windows takes.
        /// </summary>
        [Theory]
        [InlineData(@"C:\Steam\Valheim dedicated server\valheim_server.exe")]
        [InlineData(@"D:\Saves\Valheim")]
        [InlineData(@"C:\Saves\CONFIG\worlds_local")]
        [InlineData(@"C:\Saves\v1.2.0")]
        [InlineData(@"\\hall\share\Valheim")]
        [InlineData(@"\\?\C:\Saves\Valheim")]
        [InlineData(@"Valheim\worlds_local")]
        public void An_ordinary_path_is_not_turned_away_by_the_rule(string path)
            => Assert.False(PathCheck.WindowsWillNotTake(path), path + " is being turned away");

        [Fact]
        public void A_profile_path_wins_and_an_empty_one_falls_back_to_the_app_wide_value()
        {
            var own = PathCheck.Effective(@"E:\own\valheim_server.exe", @"C:\app\valheim_server.exe");
            Assert.Equal(@"E:\own\valheim_server.exe", own.Path);
            Assert.Equal(PathCheck.SourceProfile, own.Source);

            foreach (var empty in new[] { null, "", "   " })
            {
                var fell = PathCheck.Effective(empty, @"C:\app\valheim_server.exe");
                Assert.Equal(@"C:\app\valheim_server.exe", fell.Path);
                Assert.Equal(PathCheck.SourceDefault, fell.Source);
            }
        }

        /// <summary>
        /// Look never throws, whatever is in the box. It is fed by a text field a host is
        /// still typing into, so half a path is the normal case rather than the odd one.
        /// Reads only: nothing here creates or writes anything.
        /// <para>
        /// Each row says which answer it expects rather than only that nothing blew up.
        /// The earlier shape of this asserted that a path was not both a file and
        /// unreadable, which was true of every row in the table and of every row that
        /// could ever be put in it, so it stayed green over an arm that never fired.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(PathCheck.KindExe, "", false)]
        [InlineData(PathCheck.KindExe, "   ", false)]
        [InlineData(PathCheck.KindExe, @"C:\Steam\Valheim dedi", false)]
        [InlineData(PathCheck.KindDir, @"D:\Saves\half", false)]
        [InlineData(PathCheck.KindExe, @"C:\a<b>\valheim_server.exe", true)]
        [InlineData(PathCheck.KindDir, @"C:\a|b", true)]
        [InlineData(PathCheck.KindDir, "::::", true)]
        public void Looking_at_a_half_typed_path_answers_rather_than_throws(
            string kind, string path, bool unreadable)
        {
            var facts = PathCheck.Look(kind, path);

            Assert.Equal(unreadable, facts.Unreadable);
            // An unreadable path is unreadable and nothing else: the rest of the struct is
            // never filled in, because none of those questions was ever asked.
            if (unreadable)
            {
                Assert.False(facts.FileExists);
                Assert.False(facts.DirectoryExists);
                Assert.False(facts.ParentExists);
                Assert.False(facts.SiblingDataFolder);
                Assert.False(facts.Writable);
            }
        }

        /// <summary>
        /// A folder that really is there and really does take a write, checked end to end
        /// through the real disk, in a temporary folder this test owns and removes.
        /// </summary>
        [Fact]
        public void A_temporary_folder_checks_out_as_there_and_writable()
        {
            var folder = Path.Combine(Path.GetTempPath(), "baka-pathcheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var answer = PathCheck.Check(PathCheck.KindDir, folder);

                Assert.True(answer.Exists);
                Assert.True(answer.Writable);
                Assert.True(answer.LooksRight);
                Assert.Null(answer.ProblemId);

                // The probe cleans up after itself: the folder is as empty as it was.
                Assert.Empty(Directory.GetFileSystemEntries(folder));

                // And a child of it that does not exist is the creatable case.
                var child = PathCheck.Check(PathCheck.KindDir, Path.Combine(folder, "worlds"));
                Assert.Equal(PathCheck.ProblemDirWillBeCreated, child.ProblemId);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { /* the temp folder can go later */ }
            }
        }

        /// <summary>
        /// A whole install, pasted the way Explorer hands it over, judged end to end off the
        /// real disk: an empty file called valheim_server.exe with the managed folder beside
        /// it, in a temporary folder this test owns and removes. Quoted and bare both answer
        /// the same, which is the fix; before it, the quoted one was the wrong file.
        /// </summary>
        [Fact]
        public void A_quoted_install_path_checks_out_the_same_as_the_bare_one()
        {
            var folder = Path.Combine(Path.GetTempPath(), "baka-pathcheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var exe = Path.Combine(folder, PathCheck.ServerExeName);
                File.WriteAllText(exe, "");
                Directory.CreateDirectory(Path.Combine(folder, PathCheck.ServerDataFolderName));

                foreach (var typed in new[] { exe, Quoted(exe), "  " + Quoted(exe) + "  " })
                {
                    var answer = PathCheck.Check(PathCheck.KindExe, typed);

                    Assert.Equal(exe, answer.Expanded);
                    Assert.Null(answer.ProblemId);
                    Assert.True(answer.Exists);
                    Assert.True(answer.LooksRight);
                }
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { /* the temp folder can go later */ }
            }
        }

        // ------------------------------------------------------------------ the pickers

        /// <summary>
        /// What a picker answers. Two shapes rather than a null, so the page never has to
        /// tell "nothing was chosen" apart from "the call failed" by looking at the reply.
        /// </summary>
        [Fact]
        public void A_cancelled_picker_answers_cancelled_and_a_chosen_one_answers_a_path()
        {
            var chosen = JsonDocument.Parse(
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    BlendWindow.PickReply(true, @"  D:\Steam\Valheim dedicated server\valheim_server.exe  ")));
            Assert.Equal(@"D:\Steam\Valheim dedicated server\valheim_server.exe",
                chosen.RootElement.GetProperty("path").GetString());
            Assert.False(chosen.RootElement.TryGetProperty("cancelled", out _));

            foreach (var nothing in new[] { null, "", "   " })
            {
                var cancelled = JsonDocument.Parse(
                    Newtonsoft.Json.JsonConvert.SerializeObject(BlendWindow.PickReply(true, nothing)));
                Assert.True(cancelled.RootElement.GetProperty("cancelled").GetBoolean());
                Assert.False(cancelled.RootElement.TryGetProperty("path", out _));
            }

            var refused = JsonDocument.Parse(
                Newtonsoft.Json.JsonConvert.SerializeObject(BlendWindow.PickReply(false, @"D:\ignored")));
            Assert.True(refused.RootElement.GetProperty("cancelled").GetBoolean());
            Assert.False(refused.RootElement.TryGetProperty("path", out _));
        }

        /// <summary>
        /// Both pickers open their dialog on the window's own thread. An RPC handler is not
        /// reliably on it: WebView2 raises the message there, but a handler that has awaited
        /// carries on wherever the scheduler puts it, and a modal opened from that thread
        /// either throws or puts up a window the app can be closed behind.
        /// </summary>
        [Fact]
        public void Both_pickers_open_their_dialog_on_the_windows_own_thread()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];

            foreach (var rpc in new[] { "shell.pickFile", "shell.pickFolder" })
            {
                var start = bridge.IndexOf("RegisterRpc(\"" + rpc + "\"", StringComparison.Ordinal);
                Assert.True(start > 0, rpc + " is not registered");
                var body = bridge.Substring(start, 1400);
                Assert.Contains("Task.FromResult(RunOnUiThread(() =>", body);
                Assert.Contains("PickReply(dialog.ShowDialog(this)", body);
            }

            // And the marshalling itself is the real thing rather than a comment about it.
            Assert.Contains("private T RunOnUiThread<T>(Func<T> work)", bridge);
            Assert.Contains("if (!InvokeRequired) return work();", bridge);
            Assert.Contains("return (T)Invoke(work);", bridge);

            // The file picker only ever offers the one file name, and only the two closed
            // keywords are accepted: a raw path from the page can reach neither dialog.
            Assert.Contains("Filter = PathCheck.ServerExeName + \"|\" + PathCheck.ServerExeName,", bridge);
            Assert.Contains("throw new ArgumentException($\"Unknown shell.pickFile kind: {kind}\");", bridge);
            Assert.Contains("throw new ArgumentException($\"Unknown shell.pickFolder kind: {kind}\");", bridge);
        }

        // -------------------------------------------------------- the ids, end to end

        /// <summary>
        /// Every problem the rule can name has a sentence in the page's closed list and an
        /// entry in the catalog. This is the gate for the next one somebody adds: a rule
        /// that can answer with an id nothing can read shows an empty line under the box
        /// and nobody finds out why.
        /// </summary>
        [Fact]
        public void Every_problem_the_rule_can_name_has_a_sentence_the_page_can_read()
        {
            var source = AppSourceTree.Read("ValheimBakaLoader", "Tools", "PathCheck.cs");
            var ids = Regex.Matches(source, @"public const string Problem[A-Za-z]+ = ""([a-z0-9_.]+)"";")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(10, ids.Count);

            var js = AppSourceTree.Web("app.js");
            using var catalog = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var keys = catalog.RootElement.GetProperty("keys");

            foreach (var id in ids)
            {
                Assert.Contains("textId:\"" + id + "\"", js);
                Assert.True(keys.TryGetProperty(id, out var entry), "the catalog has no " + id);
                Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("lore").GetString()));
            }

            // And nothing else: a sentence in the page's list that the rule can never send
            // is a line no host will ever read.
            var listed = Regex.Matches(js, @"textId:""(paths\.problem\.[a-z_]+)""")
                .Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal).ToList(), listed);
        }

        /// <summary>
        /// paths.check answers and never refuses. The page shows one sentence and carries
        /// on, and Save Config behaves exactly as it did before whatever was said.
        /// </summary>
        [Fact]
        public void The_check_adds_no_refusal_to_anything()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];
            var start = bridge.IndexOf("RegisterRpc(\"paths.check\"", StringComparison.Ordinal);
            Assert.True(start > 0, "paths.check is not registered");
            var body = bridge.Substring(start, bridge.IndexOf("RegisterRpc(\"setup.status\"", start, StringComparison.Ordinal) - start);

            // The only throw in it is the closed-keyword guard every other keyword RPC has.
            Assert.Single(Regex.Matches(body, @"\bthrow\b"));
            Assert.Contains("Unknown paths.check kind", body);

            // Save Config is untouched: it still refuses exactly one thing, a world name
            // that could not be a folder, and nothing about a path. Read to the handler's
            // own end rather than by a length, so the whole of it is covered.
            var js = AppSourceTree.Web("app.js");
            var pressed = js.IndexOf("$(\"#saveCfgBtn\").addEventListener", StringComparison.Ordinal);
            Assert.True(pressed > 0, "app.js no longer wires Save Config");
            var save = js.Substring(pressed, js.IndexOf("\n});\n", pressed, StringComparison.Ordinal) - pressed);
            Assert.DoesNotContain("paths.check", save);
            Assert.DoesNotContain("problemId", save);
            // The only thing it turns away is still a world name that could not be a folder.
            Assert.Contains("const typedProblem=worldNewProblem();", save);
        }

        /// <summary>
        /// The check never asks the disk on the window's own thread.
        /// <para>
        /// An RPC handler runs where WebView2 raises the message, which is the UI thread,
        /// and everything a handler does before its first await is done with the window's
        /// painting stopped. This one asks the disk about a path a host is in the middle of
        /// typing, on a debounce timer, and two of those questions have no upper bound: a
        /// folder on a machine that is not answering holds Directory.Exists until SMB gives
        /// up, and a drive that has spun down holds it until the platters are back. So the
        /// window froze, once per pause in the typing, on a path nobody could reach.
        /// </para>
        /// <para>
        /// Two things make that safe and this gate is on both: the disk work goes to a
        /// worker, and the wait over it has a budget. Running out is an ANSWER with a
        /// sentence of its own rather than a refusal, because one sentence under the box is
        /// the only thing this whole surface was ever allowed to do.
        /// </para>
        /// </summary>
        [Fact]
        public void The_check_asks_the_disk_on_a_worker_and_gives_up_on_a_budget()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];
            var start = bridge.IndexOf("RegisterRpc(\"paths.check\"", StringComparison.Ordinal);
            Assert.True(start > 0, "paths.check is not registered");
            var body = bridge.Substring(
                start, bridge.IndexOf("RegisterRpc(\"setup.status\"", start, StringComparison.Ordinal) - start);

            // The handler awaits, so the disk cannot be reached before the first await.
            Assert.Contains("RegisterRpc(\"paths.check\", async p =>", body);
            // Look is the whole of the disk work, and it is the thing on the worker.
            Assert.Contains("var looking = Task.Run(() => PathCheck.Look(kind, expanded));", body);
            // And the wait over it is bounded rather than open.
            Assert.Contains("await Task.WhenAny(looking, Task.Delay(PathCheckBudget));", body);
            Assert.Contains("if (finished != looking)", body);
            Assert.Contains("answer = PathCheck.CouldNotBeChecked(expanded);", body);

            // The worker is abandoned rather than cancelled, because none of the calls in
            // Look takes a token, so something has to READ what it raises on the way out
            // or it is an unobserved task exception waiting for a collection to notice.
            Assert.Contains("_ = looking.ContinueWith(", body);
            Assert.Contains("TaskContinuationOptions.OnlyOnFaulted);", body);

            // Nothing synchronous left: the old shape called Check, which is Expand plus
            // Look plus Judge in one line, straight down the UI thread.
            Assert.DoesNotContain("PathCheck.Check(", body);
            Assert.DoesNotContain("Task.FromResult", body);

            // The budget is a real TimeSpan with a number in it rather than a name that
            // resolves to nothing, and it is short enough to be a budget.
            var declared = Regex.Match(bridge,
                @"PathCheckBudget = TimeSpan\.FromMilliseconds\((\d+)\);");
            Assert.True(declared.Success, "the budget is no longer declared as a TimeSpan in ms");
            var ms = int.Parse(declared.Groups[1].Value);
            Assert.InRange(ms, 250, 5000);

            // Running out is an answer with a sentence, and it says nothing about the path
            // being wrong: a host whose drive was asleep has not typed anything bad.
            var timedOut = PathCheck.CouldNotBeChecked(@"\\hall\share\Valheim");
            Assert.Equal(PathCheck.ProblemTimedOut, timedOut.ProblemId);
            Assert.Equal(@"\\hall\share\Valheim", timedOut.Expanded);
            Assert.False(timedOut.Exists);
            Assert.False(timedOut.LooksRight);
            Assert.False(timedOut.Writable);
            Assert.Equal("", PathCheck.CouldNotBeChecked(null).Expanded);

            // The other half is the page's, and it was already there: a slow answer for an
            // older keystroke must never land on a newer one. A worker makes late answers
            // ordinary rather than rare, so this is now load bearing.
            var js = AppSourceTree.Web("app.js");
            Assert.Contains("const seq=(_pathCheckSeq[kind]=(_pathCheckSeq[kind]||0)+1);", js);
            Assert.Contains("if(seq!==_pathCheckSeq[kind]) return;", js);
            // And the page has a sentence for the id, or the line under the box is blank
            // on the one answer the host most needs a word for.
            Assert.Contains("textId:\"" + PathCheck.ProblemTimedOut + "\"", js);
        }

        /// <summary>
        /// The write probe is documented as what it is. Everything else on this surface
        /// reads a path; this one writes a file into whatever folder the host has typed,
        /// once each time the typing pauses, and a reader who takes paths.check for a
        /// read-only question is reading it wrong.
        /// </summary>
        [Fact]
        public void The_write_probe_says_that_it_writes_into_the_folder_that_was_typed()
        {
            var source = AppSourceTree.Read("ValheimBakaLoader", "Tools", "PathCheck.cs");
            var at = source.IndexOf("private static bool TakesAWrite(", StringComparison.Ordinal);
            Assert.True(at > 0, "PathCheck no longer has the write probe");

            // The comment above it, read back to the start of its own docstring.
            var opens = source.LastIndexOf("/// <summary>", at, StringComparison.Ordinal);
            Assert.True(opens > 0, "the write probe has no docstring");
            var doc = source.Substring(opens, at - opens);

            Assert.Contains("CREATES AND DELETES A TEMPORARY FILE IN THE FOLDER THE HOST", doc);
            Assert.Contains("HAS TYPED", doc);
            Assert.Contains("once each time the typing pauses", doc);
            // And that it is the line the budget above it runs out on.
            Assert.Contains("worker", doc);

            // The claim is true: the probe deletes itself with the handle.
            Assert.Contains("FileOptions.DeleteOnClose", source.Substring(at, 600));
        }

        /// <summary>
        /// The two kind words are one vocabulary across the wire: the page's table, the
        /// three RPCs and the rule all spell them the same way.
        /// </summary>
        [Fact]
        public void The_page_and_the_rule_use_the_same_two_words_for_a_kind()
        {
            Assert.Equal("exe", PathCheck.KindExe);
            Assert.Equal("dir", PathCheck.KindDir);

            var js = AppSourceTree.Web("app.js");
            Assert.Contains("{kind:\"exe\", input:\"fServerExe\"", js);
            Assert.Contains("{kind:\"dir\", input:\"fSaveDir\"", js);
            Assert.Contains("rpc(\"paths.check\",{kind,path:typed})", js);
            Assert.Contains("rpc(d.pick,{kind:d.kind})", js);

            var kinds = new HashSet<string>(
                Regex.Matches(js, @"pick:""(shell\.pick[A-Za-z]+)""").Cast<Match>().Select(m => m.Groups[1].Value));
            Assert.Equal(new[] { "shell.pickFile", "shell.pickFolder" }.OrderBy(x => x, StringComparer.Ordinal),
                kinds.OrderBy(x => x, StringComparer.Ordinal));
        }
    }
}
