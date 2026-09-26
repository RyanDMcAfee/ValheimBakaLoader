using BakaLoaderKillAll;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ValheimBakaLoader.Forms;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The cleanse, cut into slices, and the wait the window keeps while it runs.
    /// <para>
    /// WHY THIS EXISTS. Until 1.2.4 baka_cleanse walked every object in the world inside one
    /// call on the Unity main thread. On a long-lived world that pass outlasts the RCON
    /// client's patience, so the host was told the command had timed out while it carried on
    /// and did every bit of its work; and because it held the main thread, the server did not
    /// tick for the whole of it. Neither of those is visible from a small test world, which
    /// is exactly why the walk itself is now a pure thing that can be driven with a budget
    /// that runs out on demand.
    /// </para>
    /// </summary>
    public class CleanseSliceTests
    {
        // ------------------------------------------------------------------ the walk

        /// <summary>
        /// A budget that is spent stops the slice where it stands, and the next slice picks
        /// the walk up at the record after the last one it did. Nothing is done twice and
        /// nothing is skipped.
        /// </summary>
        [Fact]
        public void A_slice_stops_at_its_budget_and_the_next_one_carries_on()
        {
            var run = new CleanseRun { Total = 10 };
            var walked = new List<int>();

            // Spent after every third record.
            var sinceSlice = 0;
            Func<bool> budget = () => ++sinceSlice % 3 == 0;

            var over = CleanseWalk.Advance(run, at => walked.Add(at), budget, null);
            Assert.False(over);
            Assert.Equal(new[] { 0, 1, 2 }, walked);
            Assert.Equal(3, run.Index);
            Assert.Equal(7, run.Remaining);
            Assert.False(run.Finished);

            Assert.False(CleanseWalk.Advance(run, at => walked.Add(at), budget, null));
            Assert.Equal(6, run.Index);

            Assert.False(CleanseWalk.Advance(run, at => walked.Add(at), budget, null));
            Assert.Equal(9, run.Index);

            // The last one runs out of records before it runs out of budget.
            Assert.True(CleanseWalk.Advance(run, at => walked.Add(at), budget, null));
            Assert.Equal(10, run.Index);
            Assert.True(run.Finished);
            Assert.Equal(0, run.Remaining);

            // Every record exactly once, in order.
            Assert.Equal(10, walked.Count);
            for (var i = 0; i < 10; i++) Assert.Equal(i, walked[i]);
        }

        /// <summary>
        /// The budget is asked AFTER a record rather than before one, so a slice always makes
        /// at least one record's worth of progress. A budget that is always spent would
        /// otherwise be a sweep that never moves.
        /// </summary>
        [Fact]
        public void A_budget_that_is_always_spent_still_moves_one_record_a_slice()
        {
            var run = new CleanseRun { Total = 3 };
            var walked = 0;

            for (var slice = 0; slice < 3; slice++)
            {
                var over = CleanseWalk.Advance(run, _ => walked++, () => true, null);
                Assert.Equal(slice == 2, over);
            }

            Assert.Equal(3, walked);
            Assert.True(run.Finished);
        }

        [Fact]
        public void With_no_budget_the_whole_walk_happens_in_one_slice()
        {
            var run = new CleanseRun { Total = 250 };
            var walked = 0;

            Assert.True(CleanseWalk.Advance(run, _ => walked++, null, null));
            Assert.Equal(250, walked);
            Assert.True(run.Finished);
        }

        [Fact]
        public void An_empty_world_finishes_inside_the_first_slice()
        {
            var run = new CleanseRun { Total = 0 };
            Assert.True(CleanseWalk.Advance(run, _ => Assert.Fail("nothing to walk"), () => true, null));
            Assert.True(run.Finished);

            // And a walk that is over stays over: a second slice touches nothing.
            Assert.True(CleanseWalk.Advance(run, _ => Assert.Fail("already finished"), null, null));
        }

        /// <summary>
        /// Somebody connecting mid sweep ends it before the slice touches anything. A
        /// container they have open would take the rewrite and have it written straight back
        /// over by their own client, so the sweep stops and the counts it reached are what it
        /// reports.
        /// </summary>
        [Fact]
        public void A_peer_arriving_ends_the_sweep_before_the_next_record_is_touched()
        {
            var run = new CleanseRun { Total = 100 };
            var walked = 0;
            var arrived = false;

            Assert.False(CleanseWalk.Advance(run, _ => walked++, () => walked >= 5, () => arrived));
            Assert.Equal(5, walked);
            Assert.False(run.Stopped);

            arrived = true;
            Assert.True(CleanseWalk.Advance(run, _ => walked++, () => walked >= 5, () => arrived));

            // Not one more record after they walked in.
            Assert.Equal(5, walked);
            Assert.True(run.Stopped);
            Assert.True(run.Finished);
            Assert.Equal(95, run.Remaining);
        }

        [Fact]
        public void The_slice_budget_is_a_few_milliseconds_of_a_frame_and_not_a_whole_one()
        {
            Assert.True(CleanseWalk.SliceMilliseconds > 0);
            Assert.True(CleanseWalk.SliceMilliseconds <= 16);
        }

        // ------------------------------------------------------------------ what it says

        [Fact]
        public void The_opening_line_names_what_it_is_about_to_walk_and_where_to_look()
        {
            var one = CleansePlan.Started(1);
            Assert.StartsWith(CleansePlan.StartedPrefix, one);
            Assert.Contains("1 object to check", one);
            Assert.Contains("baka_cleanse_status", one);

            Assert.Contains("240,000 objects to check".Replace(",", ""), CleansePlan.Started(240000));
        }

        [Fact]
        public void The_status_line_says_idle_running_or_the_counts_of_the_last_one()
        {
            Assert.Contains("nothing is running", CleansePlan.StatusIdle());

            var running = CleansePlan.StatusRunning(4000, 12345);
            Assert.StartsWith(CleansePlan.RunningPrefix, running);
            Assert.Contains("4000 of 12345 objects checked", running);

            // The third state is the last result line itself, which is one of these.
            Assert.StartsWith(CleansePlan.CompletePrefix, CleansePlan.Reply(1, 2, 3));
            Assert.StartsWith(CleansePlan.CompletePrefix, CleansePlan.NothingToDo());
        }

        /// <summary>
        /// A sweep somebody walked into says so, says who, says what it managed, and says
        /// what to do about it. What it must NOT do is open with "Cleanse complete", because
        /// the world still has marks on it.
        /// </summary>
        [Fact]
        public void A_sweep_that_stopped_for_a_peer_reports_the_counts_and_asks_for_another_run()
        {
            var said = CleansePlan.StoppedForPeers(2, "Bjorn, Sigrun", 12, 3, 40);

            Assert.StartsWith(CleansePlan.StoppedPrefix, said);
            Assert.DoesNotContain(CleansePlan.CompletePrefix, said);
            Assert.Contains("2 players connected", said);
            Assert.Contains("(Bjorn, Sigrun)", said);
            Assert.Contains("12 world objects cleared, 3 containers rewritten, 40 items cleared", said);
            Assert.Contains("Run it again when the server is empty.", said);

            // One player is one player.
            Assert.Contains("1 player connected", CleansePlan.StoppedForPeers(1, "Bjorn", 0, 0, 0));
        }

        /// <summary>
        /// The counts phrase is written once and read by the page with one expression, so the
        /// finished line and the stopped line have to spell it the same way.
        /// </summary>
        [Fact]
        public void The_finished_line_and_the_stopped_line_spell_the_counts_the_same_way()
        {
            var counts = CleansePlan.Counts(5, 6, 7);

            Assert.Contains(counts, CleansePlan.Reply(5, 6, 7));
            Assert.Contains(counts, CleansePlan.StoppedForPeers(1, "Bjorn", 5, 6, 7));

            // And the singulars are the singulars on both.
            Assert.Equal("1 world object cleared, 1 container rewritten, 1 item cleared",
                         CleansePlan.Counts(1, 1, 1));
        }

        [Fact]
        public void A_second_cleanse_while_one_is_walking_is_refused_and_says_how_far_it_has_to_go()
        {
            var said = CleansePlan.AlreadyRunning(900);
            Assert.Contains("900 objects still to check", said);
            Assert.Contains("1 object still to check", CleansePlan.AlreadyRunning(1));
        }

        // ------------------------------------------------------------------ the window's wait

        /// <summary>A fake RCON: it answers from a script and writes down what it was asked.</summary>
        private static Func<string, Task<string>> Rcon(List<string> asked, params string[] answers)
        {
            var at = 0;
            return command =>
            {
                asked.Add(command);
                var answer = at < answers.Length ? answers[at] : answers[answers.Length - 1];
                at++;
                return Task.FromResult(answer);
            };
        }

        private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(1);

        /// <summary>
        /// The shape the window was built for: the sweep answers that it started, says it is
        /// running twice, and then answers with the counts. The page gets the counts exactly
        /// as it did in 1.2.3.
        /// </summary>
        [Fact]
        public async Task The_window_waits_out_a_sweep_that_answers_running_twice_and_then_done()
        {
            var asked = new List<string>();
            var done = CleansePlan.Reply(12, 3, 40);

            var outcome = await BlendWindow.RunCleanseAsync(
                Rcon(asked,
                     CleansePlan.Started(240000),
                     CleansePlan.StatusRunning(80000, 240000),
                     CleansePlan.StatusRunning(160000, 240000),
                     done),
                Quick, TimeSpan.FromMilliseconds(100));

            Assert.True(outcome.Ok);
            Assert.False(outcome.StillRunning);
            Assert.Equal(done, outcome.Response);

            Assert.Equal(new[] { "baka_cleanse", "baka_cleanse_status", "baka_cleanse_status", "baka_cleanse_status" },
                         asked);
        }

        /// <summary>
        /// A world small enough to be walked inside the plugin's first slice answers with its
        /// whole result line, and nothing is polled at all. That is the 1.2.3 shape and it has
        /// to keep working.
        /// </summary>
        [Fact]
        public async Task A_sweep_that_finished_inside_its_own_answer_is_not_polled()
        {
            var asked = new List<string>();
            var done = CleansePlan.Reply(1, 0, 0);

            var outcome = await BlendWindow.RunCleanseAsync(
                Rcon(asked, done), Quick, TimeSpan.FromMilliseconds(100));

            Assert.True(outcome.Ok);
            Assert.Equal(done, outcome.Response);
            Assert.Equal(new[] { "baka_cleanse" }, asked);
        }

        /// <summary>A refusal is an answer, and it goes back whole and unpolled.</summary>
        [Theory]
        [InlineData("Error: 1 player is still connected (Bjorn). Cheat marks can only be cleared on an empty server.")]
        [InlineData("Error: server not ready (world still loading)")]
        [InlineData("Cleanse complete: nothing in this world carries a cheat mark. Anything a player is carrying lives on their own machine and was not touched.")]
        [InlineData("Unknown command: 'baka_cleanse'")]
        public async Task A_refusal_or_a_finished_line_goes_back_exactly_as_the_server_said_it(string reply)
        {
            var asked = new List<string>();

            var outcome = await BlendWindow.RunCleanseAsync(
                Rcon(asked, reply), Quick, TimeSpan.FromMilliseconds(100));

            Assert.True(outcome.Ok);
            Assert.False(outcome.StillRunning);
            Assert.Equal(reply, outcome.Response);
            Assert.Single(asked);
        }

        /// <summary>
        /// A status read that did not get through is not a finished sweep, and it is not a
        /// sweep that is still walking either.
        /// <para>
        /// SendRconCommandAsync answers null when RCON is off, when the server is not Running,
        /// or when the socket would not open. On a wait that has already STARTED a sweep the
        /// live one of those is the server going down under it, and the wait used to read it
        /// as "nothing came back, ask again": it then sat there for the whole ten minutes and
        /// finished by telling the host their cleanse was still running on a server that had
        /// stopped. It ends now, and it says which of the two it is.
        /// </para>
        /// </summary>
        [Fact]
        public async Task A_server_that_stops_answering_ends_the_wait_and_is_not_called_still_running()
        {
            var asked = new List<string>();
            var running = CleansePlan.StatusRunning(2000, 5000);

            var outcome = await BlendWindow.RunCleanseAsync(
                Rcon(asked,
                     CleansePlan.Started(5000),
                     running,
                     null),                                       // and then nothing at all
                Quick, TimeSpan.FromMinutes(10));

            Assert.True(outcome.Ok);
            Assert.True(outcome.NotAnswering);
            // Not the ceiling. The two are worded differently on the page and only one of
            // them says the sweep is still going.
            Assert.False(outcome.StillRunning);
            // The last thing the server did say, so the page has something to log.
            Assert.Equal(running, outcome.Response);

            // It stopped there rather than sitting out a ten-minute ceiling.
            Assert.Equal(new[] { "baka_cleanse", "baka_cleanse_status", "baka_cleanse_status" }, asked);
        }

        /// <summary>
        /// The sweep walks for minutes and the page has nothing to show for it, so the lines
        /// the plugin is already writing are handed to the caller as they arrive: the opening
        /// one, and each status whose counts have MOVED. A status that repeats itself is the
        /// same frame read twice and is not passed on, because a log filling with identical
        /// lines is the shape of movement rather than movement itself.
        /// </summary>
        [Fact]
        public async Task The_wait_reports_the_opening_line_and_every_count_that_moved()
        {
            var asked = new List<string>();
            var seen = new List<string>();
            var done = CleansePlan.Reply(12, 3, 40);

            var outcome = await BlendWindow.RunCleanseAsync(
                Rcon(asked,
                     CleansePlan.Started(240000),
                     CleansePlan.StatusRunning(80000, 240000),
                     CleansePlan.StatusRunning(80000, 240000),    // the same frame again
                     CleansePlan.StatusRunning(160000, 240000),
                     done),
                Quick, TimeSpan.FromMinutes(10), seen.Add);

            Assert.Equal(done, outcome.Response);

            Assert.Equal(new[]
            {
                CleansePlan.Started(240000),
                CleansePlan.StatusRunning(80000, 240000),
                CleansePlan.StatusRunning(160000, 240000),
            }, seen);

            // The result line is not one of them: the page words that itself, and a toast and
            // a log line saying the same thing twice is not feedback.
            Assert.DoesNotContain(done, seen);
        }

        /// <summary>
        /// And a caller that wants none of them hands nothing in, which is what every path
        /// other than the RPC does.
        /// </summary>
        [Fact]
        public async Task A_wait_with_nowhere_to_report_still_waits()
        {
            var asked = new List<string>();
            var done = CleansePlan.Reply(1, 0, 0);

            var outcome = await BlendWindow.RunCleanseAsync(
                Rcon(asked, CleansePlan.Started(10), CleansePlan.StatusRunning(5, 10), done),
                Quick, TimeSpan.FromMinutes(10));

            Assert.Equal(done, outcome.Response);
            Assert.False(outcome.NotAnswering);
        }

        /// <summary>
        /// The ceiling. Nothing has gone wrong when it is reached: the sweep is on the server
        /// and will finish and log its counts, so the answer says that rather than wording a
        /// result the window does not have.
        /// <para>
        /// And it is a CLOCK against that ceiling, not a count of polls. A count of polls is a
        /// count of round trips, and each one is a socket opened, a command sent, an answer
        /// read and the socket closed on top of the wait between them: twelve hundred of those
        /// add up to a good deal more than the ten minutes the toast and the wiki both promise.
        /// </para>
        /// </summary>
        [Fact]
        public async Task A_sweep_that_outlasts_the_ceiling_is_reported_as_still_running()
        {
            var asked = new List<string>();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var outcome = await BlendWindow.RunCleanseAsync(
                Rcon(asked, CleansePlan.Started(900000), CleansePlan.StatusRunning(1, 900000)),
                Quick, TimeSpan.FromMilliseconds(200));

            Assert.True(outcome.Ok);
            Assert.True(outcome.StillRunning);
            Assert.False(outcome.NotAnswering);
            Assert.StartsWith(CleansePlan.RunningPrefix, outcome.Response);

            // It asked more than once and then stopped, rather than running on.
            Assert.True(asked.Count >= 2, "it asked " + asked.Count + " times");

            // The ceiling is the ceiling. A counted wait answered after 200 polls however long
            // each one took, which is the defect: this one is bounded by the clock, so a slow
            // round trip makes the wait FEWER polls rather than a longer wait. Ten times the
            // ceiling is a bound loose enough that a busy build agent cannot fail it and tight
            // enough that a counted wait over the same script could not have passed it.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2),
                "the wait ran for " + clock.Elapsed);
        }

        /// <summary>A command that never got through is not an answer at all.</summary>
        [Fact]
        public async Task A_cleanse_that_did_not_get_through_is_not_ok()
        {
            var asked = new List<string>();

            var outcome = await BlendWindow.RunCleanseAsync(
                Rcon(asked, (string)null), Quick, TimeSpan.FromMilliseconds(100));

            Assert.False(outcome.Ok);
            Assert.Null(outcome.Response);
            Assert.False(outcome.StillRunning);
            Assert.False(outcome.NotAnswering);
            Assert.Single(asked);
        }
    }
}
