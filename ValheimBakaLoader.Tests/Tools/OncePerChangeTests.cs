using System;
using System.Linq;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// A line that is written every time something RUNS, held to being written once per time
    /// something CHANGES.
    /// <para>
    /// WHY THIS EXISTS. Two findings from the real-app walk of 1.2.6, and they are the same
    /// finding twice. A restart window that cannot write BepInEx because another server is up
    /// wrote "A newer BepInEx pack (5.4.2351) is waiting for every server on this install to
    /// stop." at Information on every window: two windows a minute apart on the walk, both at
    /// Information, while the three endings sixty lines further down the same method already
    /// went quiet on a repeat. And one server start wrote "No readable world header for
    /// 'WalkWorld126' ..." nine times, because every start path builds its options through one
    /// method and the Settings hall asks about the same world again.
    /// </para>
    /// <para>
    /// The shape was written out by hand in the bridge, which is how a sibling branch came to be
    /// left off it. It is one class now, and this is the test the hand-written copies never had:
    /// the gate is DRIVEN over two windows rather than read.
    /// </para>
    /// </summary>
    public class OncePerChangeTests
    {
        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];

        // ------------------------------------------------------------------ the gate itself

        /// <summary>
        /// The whole rule in one assertion: the first window is news, the second window on the
        /// same ending is not, and a different ending is news again.
        /// </summary>
        [Fact]
        public void A_second_window_on_the_same_ending_is_not_news()
        {
            var said = new OncePerChange();

            Assert.True(said.Changed("Second Sunset", "defer:5.4.2351"),
                "the first window about a profile has to be news: nothing has been said about it yet");
            Assert.False(said.Changed("Second Sunset", "defer:5.4.2351"),
                "a second window that ended exactly as the first did is the noise this exists to stop");
            Assert.False(said.Changed("Second Sunset", "defer:5.4.2351"));

            Assert.True(said.Changed("Second Sunset", "defer:5.4.2352"),
                "a NEWER pack is a different fact and has to be said out loud");
            Assert.True(said.Changed("Second Sunset", "expired:5.4.2352"),
                "a different reason for waiting is a different fact");
            Assert.False(said.Changed("Second Sunset", "expired:5.4.2352"));
        }

        /// <summary>
        /// Two realms on one install end their windows separately, so one going quiet must not
        /// take the other's first line with it.
        /// </summary>
        [Fact]
        public void Two_subjects_are_held_apart()
        {
            var said = new OncePerChange();

            Assert.True(said.Changed("Second Sunset", "defer:5.4.2351"));
            Assert.True(said.Changed("Walk 1.2.6", "defer:5.4.2351"),
                "the second realm's first window was swallowed by the first realm's");
            Assert.False(said.Changed("Second Sunset", "defer:5.4.2351"));
            Assert.False(said.Changed("Walk 1.2.6", "defer:5.4.2351"));
        }

        /// <summary>
        /// A profile name is a Windows name and a world is a folder, so the subject is matched
        /// without case. The state is the caller's own wording of a fact and is matched exactly:
        /// a reason code that changed case changed.
        /// </summary>
        [Fact]
        public void The_subject_ignores_case_and_the_state_does_not()
        {
            var said = new OncePerChange();

            Assert.True(said.Changed("Second Sunset", "current"));
            Assert.False(said.Changed("second sunset", "current"),
                "the same realm under a different capitalisation read as a realm nothing had been said about");

            Assert.True(said.Changed("Second Sunset", "Current"),
                "a state that changed case is a different fact and was dropped as a repeat");
        }

        [Fact]
        public void Nothing_and_an_empty_state_are_told_apart_from_a_real_one()
        {
            var said = new OncePerChange();

            Assert.Null(said.Last("Second Sunset"));
            Assert.True(said.Changed("Second Sunset", null),
                "the first answer about a subject is news even when the state is nothing");
            Assert.Equal("", said.Last("Second Sunset"));
            Assert.False(said.Changed("Second Sunset", null));

            Assert.True(said.Changed("Second Sunset", "defer:5.4.2351"));
            Assert.True(said.Forget("Second Sunset"));
            Assert.Null(said.Last("Second Sunset"));
            Assert.True(said.Changed("Second Sunset", "defer:5.4.2351"),
                "a subject that was forgotten is news again, which is the whole point of forgetting it");
            Assert.False(said.Forget("a realm nothing was ever said about"));
        }

        // -------------------------------------------- F8: all six endings of a restart window

        /// <summary>
        /// Every ending of ApplyBepInExUpdateAsync that writes a line goes through the gate, and
        /// writes the SAME sentence at both levels so a host reading the log sees the same words
        /// whether it is the first window or the fortieth.
        /// <para>
        /// The three deferral endings are the ones 1.2.6 left out. They are named here by the
        /// state word each one records, so a seventh ending added without a gate shows up as a
        /// line this test cannot find rather than as noise in somebody's log.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("defer:", "A newer BepInEx pack ({0}) is waiting for every server on this install to stop.")]
        [InlineData("expired:", "A newer BepInEx pack ({0}) is waiting: the restart window ran out before the write began.")]
        [InlineData("busy:", "A newer BepInEx pack ({0}) is waiting: another BepInEx write is running.")]
        public void Every_window_ending_is_gated_and_keeps_its_wording(string state, string sentence)
        {
            var bridge = Bridge();
            var method = ApplyWindow(bridge);

            Assert.Contains("BepInExWindowStateChanged(profile, \"" + state, method, StringComparison.Ordinal);

            var quoted = "\"" + sentence + "\"";
            Assert.Equal(2, Count(method, quoted));

            // And the pair is Information then Debug, in that order, rather than two of either.
            var at = method.IndexOf(quoted, StringComparison.Ordinal);
            var before = method.LastIndexOf("Logger.", at, StringComparison.Ordinal);
            Assert.Equal("Logger.Information", method.Substring(before, "Logger.Information".Length));

            var second = method.IndexOf(quoted, at + quoted.Length, StringComparison.Ordinal);
            var alsoBefore = method.LastIndexOf("Logger.", second, StringComparison.Ordinal);
            Assert.Equal("Logger.Debug", method.Substring(alsoBefore, "Logger.Debug".Length));
        }

        /// <summary>
        /// The sentence the one ungated Information line in the restart window writes, up to the
        /// end of its first literal. Named here rather than described, because naming it is the
        /// whole of how this rule is exact: everything else in the method has to be gated.
        /// </summary>
        private const string TheWriteItself =
            "BepInEx for profile {0} went from {1} to {2} at the restart window; what it ";

        /// <summary>Every sentence the window writes at Information, in the order it writes them.</summary>
        private static readonly string[] WindowSentences =
        {
            "A newer BepInEx pack ({0}) is waiting for every server on this install to stop.",
            "A newer BepInEx pack ({0}) is waiting: the restart window ran out before the write began.",
            "A newer BepInEx pack ({0}) is waiting: another BepInEx write is running.",
            "BepInEx for profile {0} was left as it was at the restart window ({1}).",
            "BepInEx for profile {0} already held the current pack at the restart window.",
            TheWriteItself,
        };

        /// <summary>
        /// No ending left behind, ENUMERATED rather than counted. The rule this replaced asked
        /// that the gates outnumbered the loud lines by at least one less, which a seventh ungated
        /// Information line satisfies just as happily as a gated one: six gates and seven lines is
        /// 6 >= 6. So the lines are listed by the words they write, and a line added later fails
        /// this rule with its own sentence in the message.
        /// <para>
        /// ONE line is allowed to stand ungated and it is named: the WRITE itself, which is an
        /// event rather than a state. It still RECORDS the state, so a refusal after it is news
        /// again, and the difference this rule reads is exactly that: the five gated lines have the
        /// gate as their condition, with nothing between the two but the call, and the write has it
        /// as a statement, which ends in a semicolon.
        /// </para>
        /// </summary>
        [Fact]
        public void Every_information_line_in_the_window_is_gated_except_the_write()
        {
            var method = ApplyWindow(Bridge());

            var said = Regex.Matches(method, @"Logger\.Information\(\s*""((?:[^""\\]|\\.)*)""")
                .Select(m => m.Groups[1].Value)
                .ToArray();

            Assert.Equal(WindowSentences, said);

            foreach (var sentence in WindowSentences)
            {
                var quoted = "\"" + sentence;
                var at = method.IndexOf(quoted, StringComparison.Ordinal);
                var gate = method.LastIndexOf("BepInExWindowStateChanged(", at, StringComparison.Ordinal);
                Assert.True(gate > 0, "nothing records a state before \"" + sentence + "\"");

                var between = method.Substring(gate, at - gate);
                if (ReferenceEquals(sentence, TheWriteItself))
                {
                    Assert.Contains(";", between);
                    Assert.Equal(1, Count(method, quoted));
                    continue;
                }

                Assert.DoesNotContain(";", between);
                Assert.Equal(2, Count(method, quoted));
            }
        }

        // -------------------------------------------- F13: the world header, once per start

        /// <summary>The sentence a world with no readable header writes, quoted as the source has it.</summary>
        private const string NoHeaderLine =
            "\"No readable world header for '{0}' under {1}, so nothing was brought in and the start goes ahead.\"";

        /// <summary>
        /// Once per START per world, which is what the finding asked for and not what the first cut
        /// did. Keyed on the world and the folder ALONE, the line was written once and then never
        /// again for the life of the window: a second start of the same world said nothing about it,
        /// and every repeat inside one start was dropped on the floor. The state carries the start
        /// it was asked in, so a new start is a new fact, and the repeats inside one start are
        /// written at Verbose rather than dropped: a host chasing why a world came up with nothing
        /// of its own needs to see every ask, and Verbose is the level the Detailed log switch
        /// turns on.
        /// </summary>
        [Fact]
        public void The_world_header_line_is_written_once_per_start()
        {
            var bridge = Bridge();
            var at = bridge.IndexOf("private void ImportWorldKeysOnFirstMeeting(", StringComparison.Ordinal);
            Assert.True(at > 0, "the bridge no longer has a first meeting with a world");
            var method = bridge.Substring(at, bridge.IndexOf("\n        }", at, StringComparison.Ordinal) - at);

            Assert.Contains("if (WorldHeaderStateChanged(world, saveFolder, WorldHeaderNoneState()))",
                method, StringComparison.Ordinal);
            Assert.Contains("WorldHeaderStateChanged(world, saveFolder, outcome.Kind.ToString());",
                method, StringComparison.Ordinal);

            // ONE line out loud and one repeat, the SAME words at both levels, Debug first and
            // Verbose for the repeat. Dropping the repeat is what this replaced, so a single hit
            // here is the defect rather than the fix.
            Assert.Equal(2, Count(method, NoHeaderLine));
            var first = method.IndexOf(NoHeaderLine, StringComparison.Ordinal);
            var before = method.LastIndexOf("AppLogger.", first, StringComparison.Ordinal);
            Assert.Equal("AppLogger.Debug", method.Substring(before, "AppLogger.Debug".Length));

            var second = method.IndexOf(NoHeaderLine, first + NoHeaderLine.Length, StringComparison.Ordinal);
            var alsoBefore = method.LastIndexOf("AppLogger.", second, StringComparison.Ordinal);
            Assert.Equal("AppLogger.Verbose", method.Substring(alsoBefore, "AppLogger.Verbose".Length));

            // The state is the start, so the gate can tell two starts apart at all.
            var state = bridge.IndexOf("private string WorldHeaderNoneState()", StringComparison.Ordinal);
            Assert.True(state > 0, "the bridge no longer words the no-header state");
            Assert.Contains("\"none@\" + Volatile.Read(ref StartAttemptId)",
                bridge.Substring(state, 300), StringComparison.Ordinal);

            // The subject is both halves, because two realms can hold a world of the same name in
            // two save folders and one of them going quiet must not silence the other.
            var helper = bridge.IndexOf("private bool WorldHeaderStateChanged(", StringComparison.Ordinal);
            Assert.True(helper > 0);
            Assert.Contains("(world ?? \"\") + \"\\n\" + (saveFolder ?? \"\")",
                bridge.Substring(helper, 400), StringComparison.Ordinal);
        }

        /// <summary>
        /// The gate DRIVEN over two starts of one world, which is the rule the first cut could not
        /// have passed: the first ask of a start is news, every ask after it inside that start is
        /// not, and the next start is news again. One line per start, however many times the start
        /// path asks.
        /// </summary>
        [Fact]
        public void A_second_start_of_the_same_world_says_it_again_once()
        {
            var said = new OncePerChange();
            const string world = "WalkWorld126\nD:\\saves";

            var loud = 0;
            for (var start = 1; start <= 2; start++)
            {
                // The nine asks one start really made on the walk: the launch check, the start, and
                // the Settings hall reading the same world again.
                for (var ask = 0; ask < 9; ask++)
                {
                    if (said.Changed(world, "none@" + start)) loud++;
                }

                Assert.Equal(start, loud);
            }

            // And a world that GAINS a readable header clears the note, so a header that later
            // stops reading is news again inside the very same start.
            Assert.True(said.Changed(world, "Imported"));
            Assert.True(said.Changed(world, "none@2"));
        }

        /// <summary>
        /// One press of Start is two calls, and it writes ONE line. server.launchCheck asks what a
        /// start would mean on this build and server.start then starts; both build the options, so
        /// both ask the world for its header. Counting each of them as its own start would put the
        /// line in the log twice for one press, so the check opens the attempt and the start joins
        /// the one that is open. A start with no check in front of it opens its own.
        /// </summary>
        [Fact]
        public void One_press_of_start_is_one_start_however_many_calls_it_takes()
        {
            var bridge = Bridge();

            var open = bridge.IndexOf("private int OpenStartAttempt()", StringComparison.Ordinal);
            Assert.True(open > 0, "the bridge no longer names a start attempt");
            Assert.Contains("Interlocked.CompareExchange(ref StartAttemptOpen, 1, 0) == 0",
                bridge.Substring(open, 400), StringComparison.Ordinal);
            Assert.Contains("Interlocked.Increment(ref StartAttemptId);",
                bridge.Substring(open, 400), StringComparison.Ordinal);

            Assert.Contains("private void CloseStartAttempt() => Interlocked.Exchange(ref StartAttemptOpen, 0);",
                bridge, StringComparison.Ordinal);

            // Every road into a start opens one, and the ones that cannot have been checked first
            // (auto start, adopting a process, the relaunch after an update) open their own. Each
            // road is sliced to where it ENDS rather than taken as a window of characters: a window
            // over the launch check reached into server.start below it, and the rule went on
            // passing with nothing in the check at all.
            foreach (var road in new[]
            {
                "RegisterRpc(\"server.launchCheck\"",
                "RegisterRpc(\"server.start\"",
                "private void StartAfterUpdate(",
            })
            {
                Assert.Contains("OpenStartAttempt();", Road(bridge, road), StringComparison.Ordinal);
            }

            Assert.Contains("OpenStartAttempt();\n                Server.StartAutomatically(", bridge, StringComparison.Ordinal);
            Assert.Contains("OpenStartAttempt();\n                    Server.AdoptProcess(", bridge, StringComparison.Ordinal);

            // And every one of them closes it, or the first cancelled guard would silence the line
            // for the rest of the session rather than for one press.
            Assert.Equal(4, Count(bridge, "CloseStartAttempt();"));
        }

        /// <summary>
        /// And both of the bridge's gates are the one class rather than two hand-written copies of
        /// its three lines, which is how the three deferral endings came to be left out.
        /// </summary>
        [Fact]
        public void The_bridge_holds_no_hand_written_copy_of_the_gate()
        {
            var bridge = Bridge();

            Assert.Contains("private readonly Tools.Logging.OncePerChange BepInExWindowReported = new();",
                bridge, StringComparison.Ordinal);
            Assert.Contains("private readonly Tools.Logging.OncePerChange WorldHeaderReported = new();",
                bridge, StringComparison.Ordinal);

            // The tell of a hand-written copy: a dictionary of strings read, compared and written
            // back inside the bridge for one of these two subjects.
            Assert.DoesNotContain("BepInExWindowReported[key]", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("WorldHeaderReported[key]", bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// One road into a start, from its opening to the next RPC registration or the next method
        /// at the class's own indent, whichever comes first. Sliced rather than taken as a window
        /// of characters: a 1400-character window over server.launchCheck reached into server.start
        /// below it, so the rule went on passing with nothing in the check at all.
        /// </summary>
        private static string Road(string bridge, string opening)
        {
            var at = bridge.IndexOf(opening, StringComparison.Ordinal);
            Assert.True(at > 0, "the bridge no longer has " + opening);

            var ends = new[]
            {
                bridge.IndexOf("RegisterRpc(\"", at + 13, StringComparison.Ordinal),
                bridge.IndexOf("\n        }", at, StringComparison.Ordinal),
            }.Where(end => end > at).ToList();

            return ends.Count == 0 ? bridge.Substring(at) : bridge.Substring(at, ends.Min() - at);
        }

        private static string ApplyWindow(string bridge)
        {
            var at = bridge.IndexOf("private async Task ApplyBepInExUpdateAsync(", StringComparison.Ordinal);
            Assert.True(at > 0, "the bridge no longer applies a BepInEx update at a restart window");
            var end = bridge.IndexOf("\n        }", at, StringComparison.Ordinal);
            Assert.True(end > at);
            return bridge.Substring(at, end - at);
        }

        private static int Count(string text, string needle)
        {
            var found = 0;
            for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
                found++;
            return found;
        }
    }
}
