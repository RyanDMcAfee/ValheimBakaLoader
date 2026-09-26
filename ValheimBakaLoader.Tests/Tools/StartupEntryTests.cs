using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Start with Windows: the one Run key entry, and the hive it belongs in.
    /// <para>
    /// WHY THIS EXISTS. Issue 18's reporter sent in a log full of a line that said nothing had
    /// gone wrong: "Could not read the startup entry under HKEY_LOCAL_MACHINE: Requested
    /// registry access is not allowed." Every operation, reads included, opened the Run key
    /// for WRITING and tried the machine wide hive first, and an ordinary account is refused
    /// on that hive every time. It had done that since 0.9.24, on every save of the Upkeep
    /// card, because the card posts all eleven of its switches together and the bridge applied
    /// this one on the KEY'S PRESENCE rather than on the value moving.
    /// </para>
    /// <para>
    /// The line in the log was the harmless half. The other half: an entry written during a
    /// run as administrator lives in the machine hive, an ordinary run cannot open that hive
    /// for writing, so the read never found it and turning the switch OFF left Windows still
    /// starting BakaLoader with nothing in the window to say so.
    /// </para>
    /// </summary>
    public class StartupEntryTests
    {
        private const string Name = "Valheim BakaLoader";
        private const string ExePath = @"C:\Apps\BakaLoader\ValheimBakaLoader.exe";
        private const string StalePath = @"D:\Old\ValheimBakaLoader.exe";

        /// <summary>
        /// A disk where the copy an entry names is gone: the host moved or renamed the folder,
        /// which is the one case a launch puts right on its own.
        /// </summary>
        private static readonly Func<string, bool> MovedAway = _ => false;

        /// <summary>
        /// And one where it is still installed: two copies, and which of them Windows should
        /// start is a question only the host can answer.
        /// </summary>
        private static readonly Func<string, bool> StillInstalled = _ => true;

        // ----------------------------------------------------------------- the fake registry

        /// <summary>
        /// An in-memory Run key, one dictionary per hive, that also keeps the log of what was
        /// asked of it. The same shape as MockProcessProvider: it answers like the real thing
        /// and touches nothing real.
        /// </summary>
        private sealed class FakeRunKeyRegistry : IRunKeyRegistry
        {
            private readonly Dictionary<RunKeyHive, Dictionary<string, string>> Hives = new()
            {
                [RunKeyHive.CurrentUser] = new(StringComparer.OrdinalIgnoreCase),
                [RunKeyHive.LocalMachine] = new(StringComparer.OrdinalIgnoreCase),
            };

            /// <summary>Hives whose delete is refused, which is what "not elevated" looks like.</summary>
            public HashSet<RunKeyHive> RefuseWrites { get; } = new();

            /// <summary>Every call, in order, as "read CurrentUser" / "write" / "delete LocalMachine".</summary>
            public List<string> Calls { get; } = new();

            public void Put(RunKeyHive hive, string entryName, string value) =>
                Hives[hive][entryName] = value;

            public string Value(RunKeyHive hive, string entryName) =>
                Hives[hive].TryGetValue(entryName, out var v) ? v : null;

            public bool Has(RunKeyHive hive, string entryName) => Hives[hive].ContainsKey(entryName);

            public string Read(RunKeyHive hive, string entryName)
            {
                Calls.Add("read " + hive);
                return Value(hive, entryName);
            }

            /// <summary>
            /// What a refused write says, in the shape the real one answers with: the kind of
            /// problem and the sentence Windows wrote. The log line has to carry it.
            /// </summary>
            public string WriteProblem { get; set; } =
                "UnauthorizedAccessException: Access to the registry key is denied.";

            public bool Write(string entryName, string value, out string problem)
            {
                // The interface has no hive here on purpose: a write only ever names the
                // current user's hive, so there is no way to ask for the other one.
                Calls.Add("write " + RunKeyHive.CurrentUser);
                if (RefuseWrites.Contains(RunKeyHive.CurrentUser))
                {
                    problem = WriteProblem;
                    return false;
                }

                problem = null;
                Hives[RunKeyHive.CurrentUser][entryName] = value;
                return true;
            }

            public RunKeyDeleteOutcome Delete(RunKeyHive hive, string entryName)
            {
                Calls.Add("delete " + hive);
                if (RefuseWrites.Contains(hive)) return RunKeyDeleteOutcome.Refused;
                if (!Hives[hive].ContainsKey(entryName)) return RunKeyDeleteOutcome.NotThere;
                Hives[hive].Remove(entryName);
                return RunKeyDeleteOutcome.Removed;
            }
        }

        /// <summary>
        /// A Serilog logger that keeps what it was written, the same one the connection bounds
        /// tests use. Debug is the level a release build writes at, which is the level the old
        /// "could not read" line arrived on.
        /// </summary>
        private sealed class Remembering : ILogger
        {
            public LoggingLevelSwitch Dial { get; } = new(LogEventLevel.Verbose);

            public List<LogEvent> Events { get; } = new();

            public List<string> Lines => Events.Select(e => e.RenderMessage()).ToList();

            public void Write(LogEvent logEvent)
            {
                if (logEvent == null || !IsEnabled(logEvent.Level)) return;
                Events.Add(logEvent);
            }

            public bool IsEnabled(LogEventLevel level) => level >= Dial.MinimumLevel;
        }

        // ------------------------------------------------------- 1. reads look, and say nothing

        /// <summary>
        /// A read asks BOTH hives and writes nothing to the log for either of them. The old
        /// shape asked the machine hive for write access first, was refused, and logged the
        /// refusal at Debug, which a release build writes: the reporter's log was full of it.
        /// </summary>
        [Fact]
        public void A_read_looks_in_both_hives_and_logs_nothing()
        {
            var registry = new FakeRunKeyRegistry();
            var logger = new Remembering();

            // Nothing registered, switch already off: there is nothing to do but look.
            var outcome = new StartupEntry(registry).Apply(false, Name, ExePath, logger);

            Assert.False(outcome.Changed);
            Assert.Null(outcome.NoticeId);
            Assert.Null(outcome.AlsoNoticeId);

            // Both hives, and nothing but reads. There are four of them rather than two
            // because every road ends by asking NotesFor for the sentence the card is about
            // to draw, which is the same method the card itself asks: that is what stops a
            // save naming a note the next open of the card would contradict.
            Assert.Equal(4, registry.Calls.Count);
            Assert.All(registry.Calls, call => Assert.StartsWith("read ", call));
            Assert.Contains("read " + RunKeyHive.CurrentUser, registry.Calls);
            Assert.Contains("read " + RunKeyHive.LocalMachine, registry.Calls);
            Assert.Empty(logger.Lines);
        }

        /// <summary>
        /// And the real registry really opens read only. This is the rule that would have
        /// failed on the shipped 1.2.4 code: WithRunKey opened every hive with writable: true,
        /// for reads as well, which is what made the refusal happen at all.
        /// </summary>
        [Fact]
        public void The_windows_registry_opens_a_read_without_asking_to_write()
        {
            var source = AppSourceTree.Read("ValheimBakaLoader", "Tools", "RunKeyRegistry.cs");

            var read = Between(source, "public string Read(", "public bool Write(");
            Assert.Contains("OpenSubKey(RunKeyPath, writable: false)", read);
            Assert.DoesNotContain("writable: true", read);

            // And a write names the current user's hive and no other. An entry under
            // HKEY_LOCAL_MACHINE is one this application does not create.
            var write = Between(source, "public bool Write(", "public RunKeyDeleteOutcome Delete(");
            Assert.Contains("Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)", write);
            Assert.DoesNotContain("Registry.LocalMachine", write);
        }

        // ------------------------------------------------------------- 2. the switch turned on

        [Fact]
        public void Turning_it_on_writes_the_current_users_hive_and_never_the_machines()
        {
            var registry = new FakeRunKeyRegistry();
            var logger = new Remembering();

            var outcome = new StartupEntry(registry).Apply(true, Name, ExePath, logger);

            Assert.True(outcome.Changed);
            Assert.Null(outcome.NoticeId);
            Assert.Equal(ExePath, registry.Value(RunKeyHive.CurrentUser, Name));
            Assert.False(registry.Has(RunKeyHive.LocalMachine, Name));
            Assert.DoesNotContain("delete " + RunKeyHive.LocalMachine, registry.Calls);
            Assert.Single(logger.Lines);
        }

        /// <summary>
        /// An entry for every account that already points at this exe is doing the job. A
        /// second one under this account would only be another entry for the switch to have to
        /// take away, and taking it away is the half that needs elevation.
        /// </summary>
        [Fact]
        public void Turning_it_on_over_a_machine_entry_that_already_points_here_writes_nothing()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.LocalMachine, Name, ExePath);
            var logger = new Remembering();

            var outcome = new StartupEntry(registry).Apply(true, Name, ExePath, logger);

            Assert.False(outcome.Changed);
            Assert.False(registry.Has(RunKeyHive.CurrentUser, Name));
            Assert.DoesNotContain("write " + RunKeyHive.CurrentUser, registry.Calls);
            Assert.Empty(logger.Lines);
        }

        /// <summary>
        /// BakaLoader moved since it was registered, so the entry is re-pointed where it
        /// actually is. In the current user's hive, which is where this application's entries
        /// live now.
        /// </summary>
        [Fact]
        public void A_stale_path_is_re_pointed_in_the_current_users_hive()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.CurrentUser, Name, @"D:\Old\ValheimBakaLoader.exe");
            var logger = new Remembering();

            // The copy that entry names is GONE, which is what a move or a rename leaves
            // behind. Said here rather than left to whatever is on this machine's drives,
            // because the line the log gets turns on it.
            var outcome = new StartupEntry(registry, MovedAway).Apply(true, Name, ExePath, logger);

            Assert.True(outcome.Changed);
            Assert.Equal(ExePath, registry.Value(RunKeyHive.CurrentUser, Name));
            Assert.False(registry.Has(RunKeyHive.LocalMachine, Name));
            Assert.Single(logger.Lines);
            Assert.Contains("has moved", logger.Lines[0]);

            // And an entry already pointing here is left exactly where it is.
            var second = new StartupEntry(registry, MovedAway).Apply(true, Name, ExePath, logger);
            Assert.False(second.Changed);
        }

        // ------------------------------------------------------------ 3. the switch turned off

        [Fact]
        public void Turning_it_off_deletes_the_current_users_entry()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.CurrentUser, Name, ExePath);
            var logger = new Remembering();

            var outcome = new StartupEntry(registry).Apply(false, Name, ExePath, logger);

            Assert.True(outcome.Changed);
            Assert.Null(outcome.NoticeId);
            Assert.False(registry.Has(RunKeyHive.CurrentUser, Name));
            Assert.Single(logger.Lines);
        }

        /// <summary>
        /// The case the reporter was in. The entry lives in the machine hive because BakaLoader
        /// was once run as administrator; this run is not, so Windows refuses the delete. The
        /// switch cannot simply read off and say nothing, because Windows will go on starting
        /// the app: the card gets a note, and the log gets ONE line, once.
        /// </summary>
        [Fact]
        public void A_machine_entry_that_cannot_be_removed_is_said_once_and_answered_to_the_page()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.LocalMachine, Name, ExePath);
            registry.RefuseWrites.Add(RunKeyHive.LocalMachine);
            var logger = new Remembering();
            var entry = new StartupEntry(registry);

            var first = entry.Apply(false, Name, ExePath, logger);

            Assert.Equal(StartupEntry.MachineHiveNoticeId, first.NoticeId);
            Assert.True(registry.Has(RunKeyHive.LocalMachine, Name));
            var warnings = logger.Events.Where(e => e.Level == LogEventLevel.Warning).ToList();
            Assert.Single(warnings);
            Assert.Contains("HKEY_LOCAL_MACHINE", warnings[0].RenderMessage());
            Assert.Contains(Name, warnings[0].RenderMessage());

            // A second move of the switch in the same session says the same thing, so it says
            // nothing. The note is still answered, because the page has to go on drawing it.
            var before = logger.Events.Count;
            var second = entry.Apply(false, Name, ExePath, logger);

            Assert.Equal(StartupEntry.MachineHiveNoticeId, second.NoticeId);
            Assert.Equal(before, logger.Events.Count);
        }

        /// <summary>
        /// An entry of the same name in the machine hive that names ANOTHER copy of BakaLoader
        /// is not this install's to take away. Removing it would turn startup off for a copy
        /// whose switch the host never touched, so the delete is not even tried, and nothing is
        /// said about a refusal that never happened. The path compare is the one TurnOn uses,
        /// case and all, because Windows paths do not care about case.
        /// </summary>
        [Fact]
        public void Turning_it_off_leaves_a_machine_entry_that_names_another_copy_alone()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.CurrentUser, Name, ExePath);
            registry.Put(RunKeyHive.LocalMachine, Name, StalePath);
            var logger = new Remembering();

            var outcome = new StartupEntry(registry, StillInstalled).Apply(false, Name, ExePath, logger);

            Assert.True(outcome.Changed);
            Assert.False(registry.Has(RunKeyHive.CurrentUser, Name));
            Assert.DoesNotContain("delete " + RunKeyHive.LocalMachine, registry.Calls);
            Assert.Equal(StalePath, registry.Value(RunKeyHive.LocalMachine, Name));

            // That entry is still going to start a copy of BakaLoader for every account on
            // this PC, so the save says so. It used to answer nothing here while a plain read
            // of the card answered the machine note, which is two sentences for one state.
            Assert.Equal(StartupEntry.MachineOtherPathNoticeId, outcome.NoticeId);
            Assert.Equal(StalePath, outcome.NoticePath);
            Assert.Null(outcome.AlsoNoticeId);

            // Nothing was refused, so nothing is warned about. The logger keeps every level,
            // and the one line it did keep is the Information line for the delete that landed:
            // asserting "no Warning" over a logger that had recorded nothing at all would
            // prove nothing about warnings.
            Assert.Single(logger.Events);
            Assert.Equal(LogEventLevel.Information, logger.Events[0].Level);
            Assert.Empty(logger.Events.Where(e => e.Level == LogEventLevel.Warning));

            // And the shape that IS this install's to take away is still taken away, whatever
            // case the path was written in.
            var mine = new FakeRunKeyRegistry();
            mine.Put(RunKeyHive.LocalMachine, Name, ExePath.ToUpperInvariant());

            var second = new StartupEntry(mine, StillInstalled).Apply(false, Name, ExePath, logger);

            Assert.True(second.Changed);
            Assert.False(mine.Has(RunKeyHive.LocalMachine, Name));
            Assert.Contains("delete " + RunKeyHive.LocalMachine, mine.Calls);
        }

        /// <summary>
        /// Turning it ON while the machine hive names another copy. This account's entry is
        /// still this application's road, so it is written, and the host is told what the
        /// second entry will do: until now a second entry simply appeared and nothing was said,
        /// so a host wondering which copy Windows was starting had nowhere to read it.
        /// </summary>
        [Fact]
        public void Turning_it_on_over_a_machine_entry_that_names_another_copy_says_so()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.LocalMachine, Name, StalePath);
            var logger = new Remembering();

            var outcome = new StartupEntry(registry, StillInstalled).Apply(true, Name, ExePath, logger);

            Assert.True(outcome.Changed);
            Assert.Equal(ExePath, registry.Value(RunKeyHive.CurrentUser, Name));
            Assert.Equal(StalePath, registry.Value(RunKeyHive.LocalMachine, Name));
            Assert.Equal(StartupEntry.MachineOtherPathNoticeId, outcome.NoticeId);
            Assert.Equal(StalePath, outcome.NoticePath);
            Assert.Single(logger.Lines);

            // A second move of the switch with this account's entry already in place still
            // answers the note, because the machine entry has not gone anywhere.
            var again = new StartupEntry(registry, StillInstalled).Apply(true, Name, ExePath, logger);

            Assert.False(again.Changed);
            Assert.Equal(StartupEntry.MachineOtherPathNoticeId, again.NoticeId);
            Assert.Equal(StalePath, again.NoticePath);
        }

        /// <summary>
        /// Windows refusing the write for this account. The preference is saved either way,
        /// which is the whole problem: without a note the switch reads ON while Windows was
        /// never told, and the old shape answered a bare outcome and wrote nothing down.
        /// </summary>
        [Fact]
        public void A_write_Windows_refused_is_said_once_and_answered_to_the_page()
        {
            var registry = new FakeRunKeyRegistry();
            registry.RefuseWrites.Add(RunKeyHive.CurrentUser);
            var logger = new Remembering();
            var entry = new StartupEntry(registry, StillInstalled);
            var prefs = new UserPreferences();

            // The save road, in order: the preference moves, and then the registry is asked.
            Assert.True(StartupHelper.ApplySavedValue(
                prefs, JObject.Parse("{\"StartWithWindows\":true}")["StartWithWindows"]));
            var outcome = entry.Apply(prefs.StartWithWindows, Name, ExePath, logger);

            // The switch state is still saved. It is the card that has to carry the bad news.
            Assert.True(prefs.StartWithWindows);
            Assert.False(outcome.Changed);
            Assert.Equal(StartupEntry.RefusedWriteNoticeId, outcome.NoticeId);
            Assert.False(registry.Has(RunKeyHive.CurrentUser, Name));

            var warnings = logger.Events.Where(e => e.Level == LogEventLevel.Warning).ToList();
            Assert.Single(warnings);
            Assert.Contains("UnauthorizedAccessException", warnings[0].RenderMessage());
            Assert.Contains("Access to the registry key is denied", warnings[0].RenderMessage());

            // A second move of the switch in the same session meets the same refusal, so it
            // says nothing more. The note is still answered, because the card goes on drawing it.
            var second = entry.Apply(true, Name, ExePath, logger);

            Assert.Equal(StartupEntry.RefusedWriteNoticeId, second.NoticeId);
            Assert.Single(logger.Events.Where(e => e.Level == LogEventLevel.Warning));

            // And a launch that would have written the missing entry says the same thing rather
            // than going quiet, because it is the same refusal on the same road.
            var launch = new StartupEntry(registry, MovedAway);
            var atLaunch = launch.RepointIfMoved(true, Name, ExePath, logger);

            Assert.False(atLaunch.Changed);
            Assert.Equal(StartupEntry.RefusedWriteNoticeId, atLaunch.NoticeId);
        }

        /// <summary>
        /// And the note is what a plain READ says too, so a host who turned the switch off in
        /// an earlier session still finds it under the switch when they open the card. That is
        /// the road the preferences answer takes.
        /// </summary>
        [Fact]
        public void The_note_is_answered_from_a_read_alone_while_the_machine_entry_stands()
        {
            var registry = new FakeRunKeyRegistry();
            var entry = new StartupEntry(registry);

            Assert.Null(entry.NoticeFor(false, Name, ExePath));

            registry.Put(RunKeyHive.LocalMachine, Name, ExePath);
            Assert.Equal(StartupEntry.MachineHiveNoticeId, entry.NoticeFor(false, Name, ExePath).Id);

            // With the switch ON there is nothing wrong: the entry is doing what it says.
            Assert.Null(entry.NoticeFor(true, Name, ExePath));
        }

        /// <summary>Every id the host side can answer with is a sentence the catalog owns.</summary>
        [Fact]
        public void The_notice_id_is_in_the_catalog_and_on_the_page()
        {
            var catalog = AppSourceTree.Web("i18n/en.json");
            var app = AppSourceTree.Web("app.js");

            // All six of them, because an id the page has no words for draws nothing at all:
            // a note this side can answer and that side cannot read is a switch lying quietly.
            var ids = new[]
            {
                StartupEntry.MachineHiveNoticeId,
                StartupEntry.MachineOtherPathNoticeId,
                StartupEntry.OtherCopyNoticeId,
                StartupEntry.OtherCopyStuckNoticeId,
                StartupEntry.RefusedWriteNoticeId,
                StartupEntry.RefusedDeleteNoticeId,
            };

            foreach (var id in ids)
            {
                Assert.Contains("\"" + id + "\"", catalog);
                Assert.Contains(id, app);
            }

            Assert.Contains("renderStartWinNote(up);", app);

            // The three that name a path have a slot for it, and the answer carries it.
            Assert.Contains("{path}", Lore(catalog, StartupEntry.MachineOtherPathNoticeId));
            Assert.Contains("{path}", Lore(catalog, StartupEntry.OtherCopyNoticeId));
            Assert.Contains("{path}", Lore(catalog, StartupEntry.OtherCopyStuckNoticeId));

            // And the one that replaces the other_copy sentence when the switch cannot clear
            // the entry does NOT go on offering the remedy that walks back into the refusal.
            Assert.DoesNotContain(
                "off and then on again", Lore(catalog, StartupEntry.OtherCopyStuckNoticeId));
            Assert.Contains(
                "Task Manager", Lore(catalog, StartupEntry.OtherCopyStuckNoticeId));
            Assert.Contains("T(START_WIN_NOTES[k].textId,{path:path})", app);
            Assert.Contains("drawStartWinNote(\"#startWinNote\",S.startWinNotice,S.startWinNoticePath)", app);
            Assert.Contains(
                "drawStartWinNote(\"#startWinNoteAlso\",S.startWinNoticeAlso,S.startWinNoticeAlsoPath)",
                app);

            // And the elements they are drawn into really exist. Two, because the machine
            // wide key and this account's own key can be wrong at the same time.
            Assert.Contains("id=\"startWinNote\"", AppSourceTree.Web("index.html"));
            Assert.Contains("id=\"startWinNoteAlso\"", AppSourceTree.Web("index.html"));
        }

        /// <summary>The English of one catalog id, read the way the lookup reads it.</summary>
        private static string Lore(string catalog, string id) =>
            Newtonsoft.Json.Linq.JObject.Parse(catalog)["keys"]?[id]?["lore"]?.ToString() ?? "";

        // ------------------------------------------ 4. an ordinary save writes nothing at all

        /// <summary>
        /// The Upkeep card posts all eleven of its switches in one object, so this key arrives
        /// on a save that came from Detailed log or from Show Norse names. The key arriving is
        /// not the switch moving, and until 1.2.5 the difference was not made: every save of
        /// that card opened the Windows registry for a preference nobody had touched.
        /// </summary>
        [Fact]
        public void A_save_that_carries_the_switch_without_moving_it_is_not_a_move()
        {
            var prefs = new UserPreferences();
            var card = JObject.Parse("{\"DetailedLog\":true,\"StartWithWindows\":false}");

            Assert.False(StartupHelper.ApplySavedValue(prefs, card["StartWithWindows"]));
            Assert.False(prefs.StartWithWindows);

            // And a host who HAS it on, flipping one of the others, is not written about either.
            prefs.StartWithWindows = true;
            var second = JObject.Parse("{\"ForceIPv4\":true,\"StartWithWindows\":true}");
            Assert.False(StartupHelper.ApplySavedValue(prefs, second["StartWithWindows"]));
            Assert.True(prefs.StartWithWindows);

            // Moving it is the one case that answers yes, both ways round.
            Assert.True(StartupHelper.ApplySavedValue(
                prefs, JObject.Parse("{\"StartWithWindows\":false}")["StartWithWindows"]));
            Assert.False(prefs.StartWithWindows);
            Assert.True(StartupHelper.ApplySavedValue(
                prefs, JObject.Parse("{\"StartWithWindows\":true}")["StartWithWindows"]));
            Assert.True(prefs.StartWithWindows);

            // A save that carried no such key at all changes nothing and says nothing.
            Assert.False(StartupHelper.ApplySavedValue(prefs, null));
            Assert.True(prefs.StartWithWindows);
        }

        /// <summary>
        /// And the save itself goes through that decision rather than round it. The bridge
        /// applies a key on PRESENCE, so this is the line that keeps the presence of the key
        /// from being read as a move, the same way the Detailed log one does. The save still
        /// READS the Run key, to work out whether the card has a note to draw, and a read of
        /// either hive is read only and allowed.
        /// </summary>
        [Fact]
        public void The_save_writes_the_registry_only_through_the_comparison()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

            Assert.Contains(
                "Apply(\"StartWithWindows\", v =>\n                    {\n"
                + "                        if (StartupHelper.ApplySavedValue(prefs, v)) startWithWindowsMoved = true;",
                bridge);
            Assert.Contains("if (startWithWindowsMoved)\n", bridge);

            // The shape that shipped before: the key arriving was the whole of the condition.
            Assert.DoesNotContain(
                "if (dto.TryGetValue(\"StartWithWindows\", StringComparison.OrdinalIgnoreCase, out _))",
                bridge);

            // And the notes the card draws ride back on the same answer every other preference
            // does, so a page that asks for the preferences already has them. Two of them,
            // because the machine wide key and this account's own key can be wrong at once and
            // the first must not hide the second.
            Assert.Contains("var startup = startupNotes ?? StartupHelper.NotesFor(", bridge);
            Assert.Contains("StartupNoticeId = startup?.Notice?.Id,", bridge);
            Assert.Contains("StartupNoticePath = startup?.Notice?.Path,", bridge);
            Assert.Contains("StartupAlsoNoticeId = startup?.Also?.Id,", bridge);
            Assert.Contains("StartupAlsoNoticePath = startup?.Also?.Path,", bridge);
        }

        // ----------------------- 5. an install that MOVED, and the road that still re-points it

        /// <summary>
        /// The gap rule 4 opened, written down so it cannot open again unnoticed. Until 1.2.4
        /// the bridge applied this preference on the KEY'S PRESENCE, so every save of the
        /// Upkeep card walked the re-point road, and an entry left pointing at a folder
        /// BakaLoader had been moved out of was quietly put right on the way past. Closing that
        /// road was right, and it took the self-heal with it: the host who renames or moves the
        /// folder is not the host who then goes and flips Start with Windows off and on.
        /// </summary>
        [Fact]
        public void A_save_that_did_not_move_the_switch_does_not_re_point_a_moved_install()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.CurrentUser, Name, StalePath);
            var prefs = new UserPreferences { StartWithWindows = true };
            var entry = new StartupEntry(registry, MovedAway);

            // The bridge's decision, written out: the helper answers whether the switch MOVED,
            // and only a move reaches the entry. The fake is on the far side of that decision,
            // so what it was asked is the whole of the answer rather than a shape in a file.
            void Save(string card)
            {
                if (StartupHelper.ApplySavedValue(prefs, JObject.Parse(card)["StartWithWindows"]))
                    entry.Apply(prefs.StartWithWindows, Name, ExePath, null);
            }

            // Every switch on that card posts together, so this is what a save of any of the
            // other ten carries for this one.
            Save("{\"ForceIPv4\":true,\"StartWithWindows\":true}");

            Assert.Empty(registry.Calls);
            Assert.Equal(StalePath, registry.Value(RunKeyHive.CurrentUser, Name));

            // And the same two lines with a save that DID move it walk straight into the
            // entry, which is what makes the two assertions above real ones: the road exists,
            // it is open, and the unchanged save simply does not take it.
            Save("{\"StartWithWindows\":false}");

            Assert.NotEmpty(registry.Calls);
            Assert.False(registry.Has(RunKeyHive.CurrentUser, Name));
        }

        /// <summary>
        /// So the self-heal lives at launch instead, which is the first thing that happens
        /// after the folder moves. The switch is on, this account has an entry, and the entry
        /// names somewhere BakaLoader no longer is: the entry is put right, once, quietly.
        /// </summary>
        [Fact]
        public void A_launch_re_points_this_accounts_entry_after_the_app_moved()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.CurrentUser, Name, StalePath);
            var logger = new Remembering();

            // The copy that entry names is gone, which is what a move or a rename leaves
            // behind. A copy that is still there is the other test, and the other rule.
            var entry = new StartupEntry(registry, MovedAway);

            var outcome = entry.RepointIfMoved(true, Name, ExePath, logger);

            Assert.True(outcome.Changed);
            Assert.Equal(ExePath, registry.Value(RunKeyHive.CurrentUser, Name));
            Assert.False(registry.Has(RunKeyHive.LocalMachine, Name));
            Assert.Single(logger.Lines);
            Assert.Contains("moved", logger.Lines[0]);

            // The launch after that one finds it already pointing here and writes nothing.
            var again = entry.RepointIfMoved(true, Name, ExePath, logger);
            Assert.False(again.Changed);
            Assert.Single(logger.Lines);
        }

        /// <summary>
        /// A launch with the switch OFF looks at nothing and writes nothing. Turning it off is
        /// the host saying no, and the next launch must not undo that.
        /// <para>
        /// The machine hive is not this application's to write either, so an entry there is
        /// read and left exactly where it is. One that already names THIS executable is also
        /// the end of the road, because a second entry under this account would only mean
        /// Windows started BakaLoader twice.
        /// </para>
        /// </summary>
        [Fact]
        public void A_launch_writes_nothing_with_the_switch_off_or_for_the_machine_hive()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.CurrentUser, Name, StalePath);
            var logger = new Remembering();

            var off = new StartupEntry(registry, MovedAway).RepointIfMoved(false, Name, ExePath, logger);

            Assert.False(off.Changed);
            Assert.Null(off.NoticeId);
            Assert.Equal(StalePath, registry.Value(RunKeyHive.CurrentUser, Name));
            Assert.Empty(registry.Calls);

            var machine = new FakeRunKeyRegistry();
            machine.Put(RunKeyHive.LocalMachine, Name, ExePath);
            var elevated = new StartupEntry(machine, MovedAway).RepointIfMoved(true, Name, ExePath, logger);

            Assert.False(elevated.Changed);
            Assert.Equal(ExePath, machine.Value(RunKeyHive.LocalMachine, Name));
            Assert.False(machine.Has(RunKeyHive.CurrentUser, Name));
            Assert.DoesNotContain("write " + RunKeyHive.CurrentUser, machine.Calls);
            Assert.DoesNotContain("delete " + RunKeyHive.LocalMachine, machine.Calls);

            Assert.Empty(logger.Lines);
        }

        /// <summary>
        /// The switch is on and there is NO entry in either hive. A cleanup tool took it away,
        /// the preferences file was restored onto a machine whose registry did not come with
        /// it, or a second Windows account opened the same install. Until now the launch read
        /// that as nothing to re-point and went quiet, so the card read on for ever while
        /// Windows had never been told: only flipping the switch off and on repaired it.
        /// </summary>
        [Fact]
        public void A_launch_writes_the_entry_again_when_the_switch_is_on_and_there_is_none()
        {
            var registry = new FakeRunKeyRegistry();
            var logger = new Remembering();
            var entry = new StartupEntry(registry, MovedAway);

            var outcome = entry.RepointIfMoved(true, Name, ExePath, logger);

            Assert.True(outcome.Changed);
            Assert.Null(outcome.NoticeId);
            Assert.Equal(ExePath, registry.Value(RunKeyHive.CurrentUser, Name));
            Assert.False(registry.Has(RunKeyHive.LocalMachine, Name));
            Assert.Single(logger.Lines);
            Assert.Contains("missing", logger.Lines[0]);

            // The launch after that one finds it and writes nothing more.
            var again = entry.RepointIfMoved(true, Name, ExePath, logger);
            Assert.False(again.Changed);
            Assert.Single(logger.Lines);

            // And an entry for every account that already names this copy is doing the job, so
            // the launch writes nothing under this account either.
            var covered = new FakeRunKeyRegistry();
            covered.Put(RunKeyHive.LocalMachine, Name, ExePath);
            var none = new StartupEntry(covered, MovedAway).RepointIfMoved(true, Name, ExePath, logger);

            Assert.False(none.Changed);
            Assert.Null(none.NoticeId);
            Assert.False(covered.Has(RunKeyHive.CurrentUser, Name));
            Assert.DoesNotContain("write " + RunKeyHive.CurrentUser, covered.Calls);
        }

        /// <summary>
        /// And the re-point turns on the copy being GONE, not on this launch being the latest
        /// one. A host who updates by extracting to a new folder and then opens the old copy
        /// once must not have startup quietly handed back to the old version for ever: which of
        /// two installed copies Windows starts is theirs to say, so the card says which one it
        /// is and the entry is left alone.
        /// </summary>
        [Fact]
        public void A_launch_leaves_an_entry_alone_while_the_copy_it_names_is_still_there()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.CurrentUser, Name, StalePath);
            var logger = new Remembering();
            var entry = new StartupEntry(registry, StillInstalled);

            var outcome = entry.RepointIfMoved(true, Name, ExePath, logger);

            Assert.False(outcome.Changed);
            Assert.Equal(StartupEntry.OtherCopyNoticeId, outcome.NoticeId);
            Assert.Equal(StalePath, outcome.NoticePath);
            Assert.Equal(StalePath, registry.Value(RunKeyHive.CurrentUser, Name));
            Assert.DoesNotContain("write " + RunKeyHive.CurrentUser, registry.Calls);
            Assert.Empty(logger.Lines);

            // The card is told the same thing from a read alone, which is the road it takes:
            // the launch's own answer is read by nobody.
            var standing = entry.NoticeFor(true, Name, ExePath);
            Assert.Equal(StartupEntry.OtherCopyNoticeId, standing.Id);
            Assert.Equal(StalePath, standing.Path);

            // Gone from disk, and the same launch puts it right, with nothing to say.
            var moved = new FakeRunKeyRegistry();
            moved.Put(RunKeyHive.CurrentUser, Name, StalePath);
            var gone = new StartupEntry(moved, MovedAway);

            Assert.True(gone.RepointIfMoved(true, Name, ExePath, logger).Changed);
            Assert.Equal(ExePath, moved.Value(RunKeyHive.CurrentUser, Name));
            Assert.Null(gone.NoticeFor(true, Name, ExePath));

            // And an entry that already names this copy is not looked for on disk at all.
            var here = new FakeRunKeyRegistry();
            here.Put(RunKeyHive.CurrentUser, Name, ExePath);
            var asked = false;
            var sameCopy = new StartupEntry(here, _ => { asked = true; return true; });

            var nothing = sameCopy.RepointIfMoved(true, Name, ExePath, logger);

            Assert.False(nothing.Changed);
            Assert.Null(nothing.NoticeId);
            Assert.False(asked);
            Assert.DoesNotContain("write " + RunKeyHive.CurrentUser, here.Calls);
        }

        /// <summary>
        /// Issue 18's reporter's own shape, one folder move later. The entry lives in the
        /// machine hive because BakaLoader was once run as administrator, and it names the old
        /// folder. Nothing this run can do reaches that hive, so the switch must not read on in
        /// silence while Windows starts a copy that is not this one, for every account.
        /// </summary>
        [Fact]
        public void A_machine_entry_that_names_another_copy_is_said_with_the_switch_on_as_well()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.LocalMachine, Name, StalePath);
            var logger = new Remembering();
            var entry = new StartupEntry(registry, StillInstalled);

            var launch = entry.RepointIfMoved(true, Name, ExePath, logger);

            // The machine hive is left exactly where it is, and said out loud. This account's
            // own entry is written all the same, which it was not until 1.2.5: it is this
            // account's to get right whatever the other hive holds, and the switch reads on.
            Assert.True(launch.Changed);
            Assert.Equal(StartupEntry.MachineOtherPathNoticeId, launch.NoticeId);
            Assert.Equal(StalePath, launch.NoticePath);
            Assert.Null(launch.AlsoNoticeId);
            Assert.Equal(ExePath, registry.Value(RunKeyHive.CurrentUser, Name));
            Assert.Equal(StalePath, registry.Value(RunKeyHive.LocalMachine, Name));
            Assert.DoesNotContain("delete " + RunKeyHive.LocalMachine, registry.Calls);
            Assert.Single(logger.Lines);

            // With the preference ON, which is the answer that used to be null: a card opened
            // in this state said nothing at all.
            var on = entry.NoticeFor(true, Name, ExePath);
            Assert.Equal(StartupEntry.MachineOtherPathNoticeId, on.Id);
            Assert.Equal(StalePath, on.Path);

            // And with it off, because Windows goes on starting that copy either way.
            var off = entry.NoticeFor(false, Name, ExePath);
            Assert.Equal(StartupEntry.MachineOtherPathNoticeId, off.Id);
            Assert.Equal(StalePath, off.Path);

            // One that names THIS copy with the switch on is doing what the switch says, so
            // there is still nothing to say about it.
            var mine = new FakeRunKeyRegistry();
            mine.Put(RunKeyHive.LocalMachine, Name, ExePath);
            Assert.Null(new StartupEntry(mine, StillInstalled).NoticeFor(true, Name, ExePath));
        }

        /// <summary>
        /// And the launch really walks that road: once, beside the log level line, before the
        /// splash screen opens. A method nothing calls is a capability that is not there.
        /// </summary>
        [Fact]
        public void The_launch_is_where_the_re_point_is_called_from()
        {
            var program = AppSourceTree.Read("ValheimBakaLoader", "Program.cs");

            Assert.Contains("RepointStartupEntry(container);", program);
            Assert.Contains("StartupHelper.RepointMovedEntry(", program);

            // Called from Main, and exactly once: a re-point per launch, not per anything else.
            var main = Between(program, "public static void Main(string[] args)", "private static void AnnounceLogLevel");
            Assert.Contains("RepointStartupEntry(container);", main);
            Assert.Equal(2, program.Split("RepointStartupEntry").Length - 1);

            // And the save road stays closed: this is not a second way into an ordinary save.
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            Assert.DoesNotContain("RepointMovedEntry", bridge);
        }

        // ------------------------- 6. the two refusals, and the note that outlives the save

        /// <summary>
        /// Windows refusing to take THIS ACCOUNT'S entry away. The machine hive half of this
        /// has been said once and answered to the card since 1.2.5; the user hive half fell
        /// through in silence, which is the very shape this whole road exists to end: the
        /// preference is saved OFF, the entry still names this exe, Windows goes on starting
        /// BakaLoader, and neither the card nor the log said a word.
        /// <para>
        /// A per user Run key that cannot be opened for writing is rarer than the machine one
        /// and not rare at all on a managed PC: a policy, a security tool or a permission
        /// somebody tightened by hand all land here, and so does the key simply not opening.
        /// </para>
        /// </summary>
        [Fact]
        public void A_delete_Windows_refused_is_said_once_and_answered_to_the_page()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.CurrentUser, Name, ExePath);
            registry.RefuseWrites.Add(RunKeyHive.CurrentUser);
            var logger = new Remembering();
            var entry = new StartupEntry(registry, StillInstalled);
            var prefs = new UserPreferences { StartWithWindows = true };

            // The save road, in order: the switch moves to off, and then the registry is asked.
            Assert.True(StartupHelper.ApplySavedValue(
                prefs, JObject.Parse("{\"StartWithWindows\":false}")["StartWithWindows"]));
            var outcome = entry.Apply(prefs.StartWithWindows, Name, ExePath, logger);

            // The preference is saved either way, which is the whole problem.
            Assert.False(prefs.StartWithWindows);
            Assert.False(outcome.Changed);
            Assert.Equal(StartupEntry.RefusedDeleteNoticeId, outcome.NoticeId);
            Assert.True(registry.Has(RunKeyHive.CurrentUser, Name));
            Assert.Equal(ExePath, registry.Value(RunKeyHive.CurrentUser, Name));

            var warnings = logger.Events.Where(e => e.Level == LogEventLevel.Warning).ToList();
            Assert.Single(warnings);
            Assert.Contains(Name, warnings[0].RenderMessage());
            Assert.Contains("Task Manager", warnings[0].RenderMessage());

            // One line per run, like the machine hive one: the refusal does not change, and a
            // line repeated on every save is a line nobody reads. The note is still answered.
            var second = entry.Apply(false, Name, ExePath, logger);

            Assert.Equal(StartupEntry.RefusedDeleteNoticeId, second.NoticeId);
            Assert.Single(logger.Events.Where(e => e.Level == LogEventLevel.Warning));

            // And the card finds it again from a read alone, so the note stands after the
            // session that produced it rather than living only in that one save's answer.
            var standing = entry.NoticeFor(false, Name, ExePath);
            Assert.Equal(StartupEntry.RefusedDeleteNoticeId, standing.Id);
            Assert.Null(standing.Path);

            // A delete that lands leaves nothing to say, on the save and on the read after it.
            var willing = new FakeRunKeyRegistry();
            willing.Put(RunKeyHive.CurrentUser, Name, ExePath);
            var clean = new StartupEntry(willing, StillInstalled);

            Assert.True(clean.Apply(false, Name, ExePath, logger).Changed);
            Assert.Null(clean.NoticeFor(false, Name, ExePath));
        }

        /// <summary>
        /// The refused WRITE, asked for again the way the card asks for it. The save that met
        /// the refusal answers the note once, and every drawing of the card after that comes
        /// from <see cref="StartupEntry.NoticeFor"/>: a note that only the save can produce is
        /// a note that is gone the moment the host closes the window.
        /// <para>
        /// It matters most on the launch road, because Program.RepointStartupEntry is void and
        /// throws the outcome away. A host whose Run key cannot be written would otherwise
        /// restart into a card with the switch reading on, nothing starting with Windows, and
        /// not a word anywhere but the application log.
        /// </para>
        /// </summary>
        [Fact]
        public void A_refused_write_is_still_the_note_when_the_card_is_opened_after_it()
        {
            var registry = new FakeRunKeyRegistry();
            registry.RefuseWrites.Add(RunKeyHive.CurrentUser);
            var logger = new Remembering();
            var entry = new StartupEntry(registry, StillInstalled);

            Assert.Equal(StartupEntry.RefusedWriteNoticeId,
                entry.Apply(true, Name, ExePath, logger).NoticeId);

            var standing = entry.NoticeFor(true, Name, ExePath);
            Assert.Equal(StartupEntry.RefusedWriteNoticeId, standing.Id);
            Assert.Null(standing.Path);

            // The launch road, whose answer nobody reads, says the same thing to the card.
            var launch = new StartupEntry(registry, MovedAway);

            Assert.Equal(StartupEntry.RefusedWriteNoticeId,
                launch.RepointIfMoved(true, Name, ExePath, logger).NoticeId);
            Assert.Equal(StartupEntry.RefusedWriteNoticeId,
                launch.NoticeFor(true, Name, ExePath).Id);

            // And a run where nothing was refused says nothing. The note is a fact about a
            // write this run actually tried, not about an account that has no entry yet.
            var quiet = new StartupEntry(new FakeRunKeyRegistry(), StillInstalled);
            Assert.Null(quiet.NoticeFor(true, Name, ExePath));

            // A write that lands afterwards takes the note away with it.
            registry.RefuseWrites.Clear();

            Assert.True(entry.Apply(true, Name, ExePath, logger).Changed);
            Assert.Null(entry.NoticeFor(true, Name, ExePath));

            // And the application really asks the SAME StartupEntry on both roads. The refusal
            // is remembered on the instance, so a helper that built a fresh one per call would
            // answer null from the card and lose the note the launch produced, which is the
            // whole of what this test is about.
            var helper = AppSourceTree.Read("ValheimBakaLoader", "Tools", "StartupHelper.cs");

            Assert.Contains(
                "private static readonly StartupEntry Entry = new(new WindowsRunKeyRegistry());",
                helper);

            // All THREE roads, not two. The refusal is remembered on the instance, so a save
            // that built its own StartupEntry would answer out of a memory the card never
            // reads, and the launch and the card would go on asking the one that is empty.
            Assert.Contains("Entry.Apply(", Between(
                helper, "public static StartupOutcome ApplyStartupSetting",
                "public static StartupOutcome RepointMovedEntry"));
            Assert.Contains("Entry.RepointIfMoved(", Between(
                helper, "public static StartupOutcome RepointMovedEntry", "public static StartupNotes NotesFor"));
            Assert.Contains("Entry.NotesFor(", Between(
                helper, "public static StartupNotes NotesFor", "public static bool ApplySavedValue"));
        }

        // ------------------- 7. the combinations the first pass never drove, and the log's words

        /// <summary>
        /// Turning it ON over an entry of this account's that names another copy of BakaLoader
        /// which is STILL INSTALLED. The entry is re-pointed, because moving this switch is how
        /// a host with two copies says which one they mean. What the log must NOT say is that
        /// BakaLoader has moved: nothing moved, there are simply two of it, and a host reading
        /// that line would go hunting for a folder that was never renamed.
        /// </summary>
        [Fact]
        public void Turning_it_on_over_another_installed_copy_says_re_pointed_and_not_moved()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.CurrentUser, Name, StalePath);
            var logger = new Remembering();

            var outcome = new StartupEntry(registry, StillInstalled).Apply(true, Name, ExePath, logger);

            Assert.True(outcome.Changed);
            Assert.Equal(ExePath, registry.Value(RunKeyHive.CurrentUser, Name));
            Assert.Null(outcome.NoticeId);
            Assert.Single(logger.Lines);
            Assert.Contains("re-pointed from another copy", logger.Lines[0]);
            Assert.Contains(StalePath, logger.Lines[0]);
            Assert.DoesNotContain("has moved", logger.Lines[0]);

            // The same move over a copy that is GONE from disk is the move it always was, and
            // says so. The two lines are told apart by the same FileExists seam the launch
            // uses to decide whether to re-point at all.
            var moved = new FakeRunKeyRegistry();
            moved.Put(RunKeyHive.CurrentUser, Name, StalePath);
            var second = new Remembering();

            Assert.True(new StartupEntry(moved, MovedAway).Apply(true, Name, ExePath, second).Changed);
            Assert.Single(second.Lines);
            Assert.Contains("has moved", second.Lines[0]);
        }

        /// <summary>
        /// Turning it ON while the machine hive already starts THIS copy for every account and
        /// this account has an entry of its own naming a DIFFERENT one. Until 1.2.5 the road
        /// ended on the machine match and that stale entry simply stayed: Windows started this
        /// copy for every account AND that one for this account, the card said nothing, and the
        /// switch read on over a state nobody had asked for. Only entries naming this exe are
        /// left standing for this account now.
        /// </summary>
        [Fact]
        public void Turning_it_on_clears_this_accounts_stale_entry_under_a_machine_entry_for_this_copy()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.LocalMachine, Name, ExePath);
            registry.Put(RunKeyHive.CurrentUser, Name, StalePath);
            var logger = new Remembering();
            var entry = new StartupEntry(registry, StillInstalled);

            var outcome = entry.Apply(true, Name, ExePath, logger);

            Assert.True(outcome.Changed);
            Assert.False(registry.Has(RunKeyHive.CurrentUser, Name));
            Assert.Equal(ExePath, registry.Value(RunKeyHive.LocalMachine, Name));

            // No second entry is written under this account either: the machine one already
            // starts this copy, and two entries would start BakaLoader twice.
            Assert.DoesNotContain("write " + RunKeyHive.CurrentUser, registry.Calls);
            Assert.DoesNotContain("delete " + RunKeyHive.LocalMachine, registry.Calls);
            Assert.Single(logger.Lines);
            Assert.Null(outcome.NoticeId);
            Assert.Null(outcome.AlsoNoticeId);
            Assert.Null(entry.NoticeFor(true, Name, ExePath));

            // An entry of this account's that already names THIS copy is left exactly where it
            // is. It names the right executable, and this road is not the one that takes
            // entries away.
            var mine = new FakeRunKeyRegistry();
            mine.Put(RunKeyHive.LocalMachine, Name, ExePath);
            mine.Put(RunKeyHive.CurrentUser, Name, ExePath);

            var quiet = new StartupEntry(mine, StillInstalled).Apply(true, Name, ExePath, logger);

            Assert.False(quiet.Changed);
            Assert.True(mine.Has(RunKeyHive.CurrentUser, Name));
            Assert.DoesNotContain("delete " + RunKeyHive.CurrentUser, mine.Calls);

            // And when Windows refuses that delete, the entry stands, the log says so once,
            // and the card gets the sentence whose way out still works.
            //
            // It used to fall through to other_copy, whose remedy is "turn this switch off and
            // then on again to point Windows at the copy you are using now". Off walks TurnOff,
            // which asks for the same delete Windows just refused. On walks back to here and
            // asks again. The host does exactly what the card told them, twice, and nothing
            // moves. The way out that works is the Startup tab, and it is a different sentence.
            var walled = new FakeRunKeyRegistry();
            walled.Put(RunKeyHive.LocalMachine, Name, ExePath);
            walled.Put(RunKeyHive.CurrentUser, Name, StalePath);
            walled.RefuseWrites.Add(RunKeyHive.CurrentUser);
            var refused = new Remembering();
            var stubborn = new StartupEntry(walled, StillInstalled);

            var stuck = stubborn.Apply(true, Name, ExePath, refused);

            Assert.False(stuck.Changed);
            Assert.Equal(StalePath, walled.Value(RunKeyHive.CurrentUser, Name));
            Assert.Equal(StartupEntry.OtherCopyStuckNoticeId, stuck.NoticeId);
            Assert.Equal(StalePath, stuck.NoticePath);
            Assert.Null(stuck.AlsoNoticeId);

            // One Warning, carrying the entry name, and nothing else in the log: the refusal is
            // the same refusal every time the host moves the switch.
            var warnings = refused.Events
                .Where(e => e.Level == LogEventLevel.Warning).ToList();
            Assert.Single(warnings);
            Assert.Contains("refused to remove the startup entry", warnings[0].RenderMessage());
            Assert.Single(refused.Events);

            // The card says the same thing on every open after it, because the entry it names
            // is still standing there to be read rather than remembered for the run.
            Assert.Equal(StartupEntry.OtherCopyStuckNoticeId,
                stubborn.NoticeFor(true, Name, ExePath).Id);
            Assert.Equal(StartupEntry.OtherCopyStuckNoticeId,
                new StartupEntry(walled, StillInstalled).NoticeFor(true, Name, ExePath).Id);

            // A second save says it once and no more.
            stubborn.Apply(true, Name, ExePath, refused);
            Assert.Single(refused.Events);
        }

        /// <summary>
        /// The LAUNCH over the same state, which until this pass never reached it. A machine
        /// entry naming THIS copy ended <see cref="StartupEntry.RepointIfMoved"/> on its first
        /// line, so a host who moved the folder and never touched the switch again kept a stale
        /// per-account entry forever: Windows started this copy for every account AND that one
        /// for them. The save road learned to clear it; the launch follows the same rule, and
        /// answers the same sentence when Windows will not let it.
        /// </summary>
        [Fact]
        public void A_launch_clears_this_accounts_stale_entry_under_a_machine_entry_for_this_copy()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.LocalMachine, Name, ExePath);
            registry.Put(RunKeyHive.CurrentUser, Name, StalePath);
            var logger = new Remembering();
            var entry = new StartupEntry(registry, StillInstalled);

            var outcome = entry.RepointIfMoved(true, Name, ExePath, logger);

            Assert.True(outcome.Changed);
            Assert.False(registry.Has(RunKeyHive.CurrentUser, Name));
            Assert.Equal(ExePath, registry.Value(RunKeyHive.LocalMachine, Name));
            Assert.Null(outcome.NoticeId);

            // Still no second entry of this account's own, and the machine hive is never
            // written or deleted by a launch.
            Assert.DoesNotContain("write " + RunKeyHive.CurrentUser, registry.Calls);
            Assert.DoesNotContain("delete " + RunKeyHive.LocalMachine, registry.Calls);
            Assert.Single(logger.Lines);
            Assert.Contains("named another copy of BakaLoader", logger.Lines[0]);

            // An entry that already names THIS copy is left exactly where it is: a launch is
            // not the road that takes entries away.
            var mine = new FakeRunKeyRegistry();
            mine.Put(RunKeyHive.LocalMachine, Name, ExePath);
            mine.Put(RunKeyHive.CurrentUser, Name, ExePath);

            var quiet = new StartupEntry(mine, StillInstalled).RepointIfMoved(true, Name, ExePath, logger);

            Assert.False(quiet.Changed);
            Assert.True(mine.Has(RunKeyHive.CurrentUser, Name));
            Assert.DoesNotContain("delete " + RunKeyHive.CurrentUser, mine.Calls);

            // And the refusal, on the launch road, answers the same sentence the save does.
            var walled = new FakeRunKeyRegistry();
            walled.Put(RunKeyHive.LocalMachine, Name, ExePath);
            walled.Put(RunKeyHive.CurrentUser, Name, StalePath);
            walled.RefuseWrites.Add(RunKeyHive.CurrentUser);
            var refused = new Remembering();
            var stubborn = new StartupEntry(walled, MovedAway);

            var stuck = stubborn.RepointIfMoved(true, Name, ExePath, refused);

            Assert.False(stuck.Changed);
            Assert.Equal(StalePath, walled.Value(RunKeyHive.CurrentUser, Name));
            Assert.Equal(StartupEntry.OtherCopyStuckNoticeId, stuck.NoticeId);
            Assert.Equal(StalePath, stuck.NoticePath);
            Assert.Equal(StartupEntry.OtherCopyStuckNoticeId,
                stubborn.NoticeFor(true, Name, ExePath).Id);

            var warnings = refused.Events.Where(e => e.Level == LogEventLevel.Warning).ToList();
            Assert.Single(warnings);
            Assert.Contains("refused to remove the startup entry", warnings[0].RenderMessage());

            Assert.Single(refused.Events);

            // That last one ran over a disk where the copy the entry names is GONE, which is
            // the road that used to answer null and leave the card blank. The entry is stale
            // either way, and what cannot be put right here is the delete, not the other copy.
            var installed = new FakeRunKeyRegistry();
            installed.Put(RunKeyHive.LocalMachine, Name, ExePath);
            installed.Put(RunKeyHive.CurrentUser, Name, StalePath);
            installed.RefuseWrites.Add(RunKeyHive.CurrentUser);

            Assert.Equal(StartupEntry.OtherCopyStuckNoticeId,
                new StartupEntry(installed, StillInstalled)
                    .RepointIfMoved(true, Name, ExePath, new Remembering()).NoticeId);
        }

        /// <summary>
        /// The switch reads OFF, the machine hive holds nothing, and this account's entry is
        /// still standing. The state is the same whether Windows refused the delete, the
        /// preferences file was reset, or it was carried over from another machine, so the
        /// answer comes from the READ rather than from anything this run remembers, and it is
        /// the same answer whichever copy of BakaLoader that leftover entry happens to name.
        /// </summary>
        [Fact]
        public void The_switch_off_over_a_standing_entry_is_answered_whichever_copy_it_names()
        {
            var mine = new FakeRunKeyRegistry();
            mine.Put(RunKeyHive.CurrentUser, Name, ExePath);

            // A fresh StartupEntry: nothing was refused during THIS run, so there is nothing
            // in memory to answer from.
            var standing = new StartupEntry(mine, StillInstalled).NotesFor(false, Name, ExePath);

            Assert.Equal(StartupEntry.RefusedDeleteNoticeId, standing.Notice.Id);
            Assert.Null(standing.Notice.Path);
            Assert.Null(standing.Also);

            // The same sentence when it names another copy. It names no path, so it is true of
            // both, and the way out it gives, the Startup tab, clears either one.
            var other = new FakeRunKeyRegistry();
            other.Put(RunKeyHive.CurrentUser, Name, StalePath);

            Assert.Equal(StartupEntry.RefusedDeleteNoticeId,
                new StartupEntry(other, StillInstalled).NoticeFor(false, Name, ExePath).Id);
            Assert.Equal(StartupEntry.RefusedDeleteNoticeId,
                new StartupEntry(other, MovedAway).NoticeFor(false, Name, ExePath).Id);

            // And an account with no entry at all, which is what the switch reading off is
            // supposed to mean, says nothing.
            Assert.Null(new StartupEntry(new FakeRunKeyRegistry()).NoticeFor(false, Name, ExePath));
        }

        /// <summary>
        /// The refused WRITE again, over an entry that is still standing and names a copy of
        /// BakaLoader that is not on disk any more. Until this pass the card said nothing at
        /// all in that state. The refusal is remembered for the run, but the read behind the
        /// card only asked for it when this account had NO entry, so a leftover entry from a
        /// folder that was moved or renamed sent the answer down the other road, and that road
        /// ended in null whether or not a write had just been refused.
        /// <para>
        /// Both roads that can meet a refusal reach exactly that shape. The save walks
        /// <c>TurnOn</c>, which writes because the entry names something else. The launch walks
        /// <see cref="StartupEntry.RepointIfMoved"/>, which writes because the copy that entry
        /// names has gone from disk. Windows says no to either one, the registry keeps a value
        /// pointing at a path that is not there, nothing starts with Windows, and the switch
        /// reads on. A blank card over that is the one state this whole road exists to end.
        /// </para>
        /// </summary>
        [Fact]
        public void A_refused_write_over_an_entry_naming_a_copy_that_is_gone_is_still_the_note()
        {
            // THE SAVE ROAD. The host moves the switch on, this account's entry names a copy
            // that has been deleted, and Windows refuses the write that would put it right.
            var saving = new FakeRunKeyRegistry();
            saving.Put(RunKeyHive.CurrentUser, Name, StalePath);
            saving.RefuseWrites.Add(RunKeyHive.CurrentUser);
            var logger = new Remembering();
            var entry = new StartupEntry(saving, MovedAway);

            var save = entry.Apply(true, Name, ExePath, logger);

            Assert.False(save.Changed);
            Assert.Equal(StalePath, saving.Value(RunKeyHive.CurrentUser, Name));
            Assert.Equal(StartupEntry.RefusedWriteNoticeId, save.NoticeId);
            Assert.Null(save.NoticePath);

            // And the card finds it again on every open after that, which is the half that was
            // missing: the sentence the save handed back used to go with the window.
            Assert.Equal(StartupEntry.RefusedWriteNoticeId, entry.NoticeFor(true, Name, ExePath).Id);
            Assert.Single(logger.Events.Where(e => e.Level == LogEventLevel.Warning));

            // THE LAUNCH ROAD, whose answer Program.RepointStartupEntry throws away, over the
            // same registry and the same disk.
            var starting = new FakeRunKeyRegistry();
            starting.Put(RunKeyHive.CurrentUser, Name, StalePath);
            starting.RefuseWrites.Add(RunKeyHive.CurrentUser);
            var launch = new StartupEntry(starting, MovedAway);

            Assert.Equal(StartupEntry.RefusedWriteNoticeId,
                launch.RepointIfMoved(true, Name, ExePath, new Remembering()).NoticeId);
            Assert.Equal(StartupEntry.RefusedWriteNoticeId, launch.NoticeFor(true, Name, ExePath).Id);

            // A run where nothing was refused still says nothing over that same registry. An
            // entry naming a copy that is gone is the moved install, and the next launch puts
            // that one right on its own: the note is a fact about a write this run really tried.
            var quiet = new FakeRunKeyRegistry();
            quiet.Put(RunKeyHive.CurrentUser, Name, StalePath);

            Assert.Null(new StartupEntry(quiet, MovedAway).NoticeFor(true, Name, ExePath));

            // And when the copy that entry names is STILL INSTALLED the other copy note goes on
            // winning, refusal or no refusal: that one is two copies and a question only the
            // host can answer, and it names the path Windows would start.
            var installed = new FakeRunKeyRegistry();
            installed.Put(RunKeyHive.CurrentUser, Name, StalePath);
            installed.RefuseWrites.Add(RunKeyHive.CurrentUser);
            var two = new StartupEntry(installed, StillInstalled);

            Assert.Equal(StartupEntry.OtherCopyNoticeId,
                two.Apply(true, Name, ExePath, new Remembering()).NoticeId);
            Assert.Equal(StartupEntry.OtherCopyNoticeId, two.NoticeFor(true, Name, ExePath).Id);
        }

        /// <summary>
        /// Both keys wrong at once. A machine wide entry naming another copy is a fact about
        /// the PC; a write or a delete Windows refused is a fact about this account. Until
        /// 1.2.5 the machine sentence simply won, so the save that met the refusal said it once
        /// and the next open of the card quietly replaced it with the machine one: a warning
        /// the host saw for a moment and could never get back. Both stand now, and the save and
        /// the read answer the same pair, because both come from the same method.
        /// </summary>
        [Fact]
        public void A_refusal_stands_under_the_machine_note_rather_than_behind_it()
        {
            // The switch going OFF, with the delete refused.
            var off = new FakeRunKeyRegistry();
            off.Put(RunKeyHive.LocalMachine, Name, StalePath);
            off.Put(RunKeyHive.CurrentUser, Name, ExePath);
            off.RefuseWrites.Add(RunKeyHive.CurrentUser);
            var offEntry = new StartupEntry(off, StillInstalled);

            var offSave = offEntry.Apply(false, Name, ExePath, new Remembering());

            Assert.Equal(StartupEntry.MachineOtherPathNoticeId, offSave.NoticeId);
            Assert.Equal(StalePath, offSave.NoticePath);
            Assert.Equal(StartupEntry.RefusedDeleteNoticeId, offSave.AlsoNoticeId);

            var offStanding = offEntry.NotesFor(false, Name, ExePath);
            Assert.Equal(offSave.NoticeId, offStanding.Notice.Id);
            Assert.Equal(offSave.NoticePath, offStanding.Notice.Path);
            Assert.Equal(offSave.AlsoNoticeId, offStanding.Also.Id);

            // And the switch going ON, with the write refused, which leaves nothing on disk at
            // all for the read behind the card to find.
            var on = new FakeRunKeyRegistry();
            on.Put(RunKeyHive.LocalMachine, Name, StalePath);
            on.RefuseWrites.Add(RunKeyHive.CurrentUser);
            var onEntry = new StartupEntry(on, StillInstalled);

            var onSave = onEntry.Apply(true, Name, ExePath, new Remembering());

            Assert.Equal(StartupEntry.MachineOtherPathNoticeId, onSave.NoticeId);
            Assert.Equal(StartupEntry.RefusedWriteNoticeId, onSave.AlsoNoticeId);

            var onStanding = onEntry.NotesFor(true, Name, ExePath);
            Assert.Equal(onSave.NoticeId, onStanding.Notice.Id);
            Assert.Equal(onSave.AlsoNoticeId, onStanding.Also.Id);

            // A second note never stands on its own. With nothing to say about the machine,
            // this account's own sentence IS the note.
            var alone = new FakeRunKeyRegistry();
            alone.RefuseWrites.Add(RunKeyHive.CurrentUser);

            var loneSave = new StartupEntry(alone, StillInstalled).Apply(true, Name, ExePath, new Remembering());

            Assert.Equal(StartupEntry.RefusedWriteNoticeId, loneSave.NoticeId);
            Assert.Null(loneSave.AlsoNoticeId);
        }

        /// <summary>
        /// And the launch road under that same machine wide entry. Until 1.2.5 it read the
        /// machine hive and returned, so this account's entry was never written and a refusal
        /// was never even met, let alone remembered. Two things came of that: a host whose Run
        /// key could not be written restarted into a card with nothing under the switch, and a
        /// host who cleared the machine row partway through the session was left with the
        /// switch reading on, nothing in either hive, and a blank card.
        /// </summary>
        [Fact]
        public void A_launch_under_a_machine_entry_for_another_copy_still_puts_this_account_right()
        {
            var registry = new FakeRunKeyRegistry();
            registry.Put(RunKeyHive.LocalMachine, Name, StalePath);
            registry.RefuseWrites.Add(RunKeyHive.CurrentUser);
            var logger = new Remembering();
            var entry = new StartupEntry(registry, MovedAway);

            var launch = entry.RepointIfMoved(true, Name, ExePath, logger);

            // The write was ATTEMPTED, which is the whole of it.
            Assert.Contains("write " + RunKeyHive.CurrentUser, registry.Calls);
            Assert.False(launch.Changed);
            Assert.Equal(StartupEntry.MachineOtherPathNoticeId, launch.NoticeId);
            Assert.Equal(StalePath, launch.NoticePath);
            Assert.Equal(StartupEntry.RefusedWriteNoticeId, launch.AlsoNoticeId);
            Assert.Single(logger.Events.Where(e => e.Level == LogEventLevel.Warning));

            // The machine hive is still not this application's to touch.
            Assert.Equal(StalePath, registry.Value(RunKeyHive.LocalMachine, Name));
            Assert.DoesNotContain("delete " + RunKeyHive.LocalMachine, registry.Calls);

            // Somebody clears that machine row while the window is open. The card does not go
            // blank with the switch reading on, because the refusal is remembered for the run.
            registry.Delete(RunKeyHive.LocalMachine, Name);
            var after = entry.NotesFor(true, Name, ExePath);

            Assert.Equal(StartupEntry.RefusedWriteNoticeId, after.Notice.Id);
            Assert.Null(after.Also);

            // And a launch that Windows does NOT refuse writes this account's entry under that
            // same machine wide row, because the switch says to start this copy and the other
            // hive is one no ordinary run can correct.
            var willing = new FakeRunKeyRegistry();
            willing.Put(RunKeyHive.LocalMachine, Name, StalePath);
            var second = new Remembering();

            var wrote = new StartupEntry(willing, MovedAway).RepointIfMoved(true, Name, ExePath, second);

            Assert.True(wrote.Changed);
            Assert.Equal(ExePath, willing.Value(RunKeyHive.CurrentUser, Name));
            Assert.Equal(StalePath, willing.Value(RunKeyHive.LocalMachine, Name));
            Assert.Equal(StartupEntry.MachineOtherPathNoticeId, wrote.NoticeId);
            Assert.Null(wrote.AlsoNoticeId);
            Assert.Single(second.Lines);
        }

        /// <summary>One region of a source file, named by the two lines that bound it.</summary>
        private static string Between(string source, string from, string to)
        {
            var a = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(a > 0, "the source no longer holds " + from);
            var b = source.IndexOf(to, a, StringComparison.Ordinal);
            Assert.True(b > a, "the source no longer holds " + to + " after " + from);
            return source.Substring(a, b - a);
        }
    }
}
