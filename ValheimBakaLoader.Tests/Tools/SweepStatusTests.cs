using System;
using System.IO;
using System.Text.RegularExpressions;
using BakaLoaderClearDrops;
using BakaLoaderKillAll;
using BakaLoaderSweepEcho;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The three sliced sweeps (cleardrops, killall, cleanse) and how their result reaches the
    /// host. A world too big to walk inside the RCON answer used to answer "started" and put
    /// its result in the BepInEx log alone, which BakaLoader does not read and which ships with
    /// console logging off: a host cleared 5245 items and was told nothing. Commander 1.9.2
    /// answers a status verb for each sweep in one shape, and writes every result line to
    /// stdout behind its own tag.
    /// <para>
    /// Both halves of every line are held here: the plugin's own words out of the plans and
    /// BakaSweepEcho.cs (which the app compiles too), and the host side's reading of them in
    /// SweepReplies. The page's reading is held by scripts/ui/killall_reply_selftest.js against
    /// the same strings.
    /// </para>
    /// </summary>
    public class SweepStatusTests
    {
        /// <summary>The one shape every running status line has, whichever sweep it is.</summary>
        private static readonly Regex RunningShape =
            new(@"^(cleardrops|killall|cleanse): running: scanned (\d+) of (\d+), removed (\d+) so far$");

        // ------------------------------------------------------------------
        //  The plugin's status text
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(null, "Alpha", false)]
        [InlineData("", "Alpha", false)]
        [InlineData("   ", "Alpha", false)]
        [InlineData("Alpha", "Alpha", false)]
        [InlineData("alpha ", "Alpha", false)]
        [InlineData("Alpha", "Bravo", true)]
        [InlineData("Bravo", "Alpha", true)]
        public void A_status_poll_for_a_realm_no_longer_on_screen_is_refused(string asked, string onScreen, bool refused)
        {
            Assert.Equal(refused, SweepReplies.IsForAnotherRealm(asked, onScreen));
        }

        [Fact]
        public void The_bridge_refuses_a_poll_for_another_realm_before_it_sends_anything()
        {
            var bridge = Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            var at = bridge.IndexOf("RegisterRpc(\"server.command\"", StringComparison.Ordinal);
            Assert.True(at >= 0);
            var end = bridge.IndexOf("RegisterRpc(\"players.list\"", at, StringComparison.Ordinal);
            var body = bridge.Substring(at, end - at);
            var check = body.IndexOf("SweepReplies.IsForAnotherRealm(p.Value<string>(\"profile\"), ActiveProfileName)", StringComparison.Ordinal);
            var send = body.IndexOf("Server.SendRconCommandAsync(", StringComparison.Ordinal);
            Assert.True(check >= 0, "server.command no longer checks the realm a poll names");
            Assert.True(check < send, "the realm is checked after the line went out");
            Assert.Contains("errorId = \"realmChanged\"", body);
        }

        [Fact]
        public void All_three_running_lines_have_the_one_shape()
        {
            var lines = new[]
            {
                ("cleardrops", ClearDropsPlan.StatusRunning(5000, 184220, 412)),
                ("killall", KillAllPlan.StatusRunning(812, 4120, 300)),
                ("cleanse", CleansePlan.StatusRunning(80000, 240000, 12)),
            };

            foreach (var (verb, line) in lines)
            {
                var m = RunningShape.Match(line);
                Assert.True(m.Success, verb + " status does not have the shared shape: " + line);
                Assert.Equal(verb, m.Groups[1].Value);
            }

            Assert.Equal("cleardrops: running: scanned 5000 of 184220, removed 412 so far", lines[0].Item2);
            Assert.Equal("killall: running: scanned 812 of 4120, removed 300 so far", lines[1].Item2);
            Assert.Equal("cleanse: running: scanned 80000 of 240000, removed 12 so far", lines[2].Item2);
        }

        [Fact]
        public void Each_running_line_opens_with_its_plans_own_prefix()
        {
            Assert.StartsWith(ClearDropsPlan.RunningPrefix, ClearDropsPlan.StatusRunning(1, 2, 0));
            Assert.StartsWith(KillAllPlan.RunningPrefix, KillAllPlan.StatusRunning(1, 2, 0));
            Assert.StartsWith(CleansePlan.RunningPrefix, CleansePlan.StatusRunning(1, 2, 0));
            Assert.Equal("cleardrops: running:", ClearDropsPlan.RunningPrefix);
            Assert.Equal("killall: running:", KillAllPlan.RunningPrefix);
            Assert.Equal("cleanse: running:", CleansePlan.RunningPrefix);
        }

        [Fact]
        public void The_idle_answers_open_with_the_verb_and_say_nothing_is_running()
        {
            Assert.StartsWith("cleardrops: nothing is running", ClearDropsPlan.StatusIdle());
            Assert.StartsWith("killall: nothing is running", KillAllPlan.StatusIdle());
            Assert.StartsWith("cleanse: nothing is running", CleansePlan.StatusIdle());
        }

        [Fact]
        public void The_started_lines_name_the_status_verb_a_host_can_ask()
        {
            Assert.Equal("baka_cleardrops_status", ClearDropsPlan.StatusVerb);
            Assert.Equal("baka_killall_status", KillAllPlan.StatusVerb);
            Assert.Contains("baka_cleardrops_status says how far it has got", ClearDropsPlan.Started(10, 20));
            Assert.Contains("baka_killall_status says how far it has got", KillAllPlan.Started(10));
            Assert.Contains("baka_cleanse_status says how far it has got", CleansePlan.Started(10));
        }

        [Theory]
        [InlineData("cleanse: running: scanned 1 of 2, removed 0 so far", true)]
        // a plugin before 1.9.2, on a server not restarted since BakaLoader updated
        [InlineData("Cleanse running: 80000 of 240000 objects checked.", true)]
        [InlineData("  CLEANSE RUNNING: 1 of 2 objects checked.", true)]
        [InlineData("Cleanse complete: 4 world objects cleared, 1 container rewritten, 9 items cleared.", false)]
        [InlineData("Cleanse started: 240000 objects to check.", false)]
        [InlineData("cleanse: nothing is running.", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void The_cleanse_wait_reads_both_running_shapes(string line, bool running)
        {
            Assert.Equal(running, CleansePlan.IsRunningStatus(line));
        }

        // ------------------------------------------------------------------
        //  The line on stdout, written and read back by one file
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("cleardrops",
            "cleardrops: removed 5245 · kept 2 gear · kept 823 near players · scanned 1107305 objects",
            "[BakaLoader Commander] cleardrops: removed 5245 · kept 2 gear · kept 823 near players · scanned 1107305 objects")]
        [InlineData("cleardrops",
            "cleardrops: stopped early: boom. Up to that point: removed 3 · kept 1 gear · kept 0 near players · scanned 90 objects.",
            "[BakaLoader Commander] cleardrops: stopped early: boom. Up to that point: removed 3 · kept 1 gear · kept 0 near players · scanned 90 objects.")]
        [InlineData("killall",
            "KillAll complete: 37 hostiles slain, 4 out of reach, 162 spared (players, pets & allies)",
            "[BakaLoader Commander] killall: KillAll complete: 37 hostiles slain, 4 out of reach, 162 spared (players, pets & allies)")]
        [InlineData("killall",
            "KillAll stopped early: boom. Up to that point: 1 hostile slain, 0 out of reach, 0 spared (players, pets & allies).",
            "[BakaLoader Commander] killall: KillAll stopped early: boom. Up to that point: 1 hostile slain, 0 out of reach, 0 spared (players, pets & allies).")]
        [InlineData("cleanse",
            "Cleanse complete: 4 world objects cleared, 1 container rewritten, 9 items cleared.",
            "[BakaLoader Commander] cleanse: Cleanse complete: 4 world objects cleared, 1 container rewritten, 9 items cleared.")]
        [InlineData("cleanse",
            "Error: the cleanse could not finish: boom",
            "[BakaLoader Commander] cleanse: Error: the cleanse could not finish: boom")]
        public void A_result_line_goes_out_tagged_and_comes_back_word_for_word(string verb, string result, string printed)
        {
            Assert.Equal(printed, SweepEcho.Line(verb, result));

            Assert.True(SweepEcho.TryRead(printed, out var readVerb, out var readLine));
            Assert.Equal(verb, readVerb);
            Assert.Equal(result, readLine);

            // A server can put a timestamp in front of what Unity prints.
            Assert.True(SweepEcho.TryRead("10/12/2026 15:07:31: " + printed, out readVerb, out readLine));
            Assert.Equal(verb, readVerb);
            Assert.Equal(result, readLine);
        }

        [Theory]
        // the BepInEx log's own spelling of the plugin name is not the tag
        [InlineData("[Info   :BakaLoader Commander] cleardrops: removed 1 · kept 0 gear")]
        [InlineData("[BakaLoader Commander] listening on 127.0.0.1:25575")]
        [InlineData("[BakaLoader Commander] save: started")]
        [InlineData("[BakaLoader Commander] killall:")]
        [InlineData("cleardrops: removed 5 · kept 0 gear")]
        [InlineData("")]
        [InlineData(null)]
        public void Anything_else_is_not_read_as_a_sweep_result(string line)
        {
            Assert.False(SweepEcho.TryRead(line, out _, out _));
        }

        // ------------------------------------------------------------------
        //  The host side's reading
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("baka_cleardrops keepgear=1 radius=32", "cleardrops")]
        [InlineData("baka_cleardrops_status", "cleardrops")]
        [InlineData("baka_killall", "killall")]
        [InlineData("baka_killall near Bjorn 50", "killall")]
        [InlineData("BAKA_KILLALL_STATUS", "killall")]
        [InlineData("baka_cleanse", "cleanse")]
        [InlineData("baka_cleanse_status", "cleanse")]
        [InlineData("baka_save", null)]
        [InlineData("playerlist", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void A_command_names_its_sweep_by_its_first_word(string command, string verb)
        {
            Assert.Equal(verb, SweepReplies.VerbOf(command));
        }

        [Theory]
        [InlineData("cleardrops: running: scanned 5000 of 184220, removed 412 so far", true)]
        [InlineData("killall: running: scanned 812 of 4120, removed 300 so far", true)]
        [InlineData("cleanse: running: scanned 1 of 2, removed 0 so far", true)]
        [InlineData("Cleanse running: 1 of 2 objects checked.", true)]
        [InlineData("cleardrops: started on 5120 ground items out of 184220 objects.", false)]
        [InlineData("KillAll complete: 1 hostile slain, 0 out of reach, 0 spared (players, pets & allies)", false)]
        [InlineData("killall: nothing is running.", false)]
        public void A_status_line_is_read_as_running_in_every_sweeps_shape(string reply, bool running)
        {
            Assert.Equal(running, SweepReplies.IsRunning(reply));
        }

        [Theory]
        [InlineData("cleardrops", "cleardrops: removed 5245 · kept 2 gear · kept 823 near players · scanned 1107305 objects", true)]
        [InlineData("cleardrops", "cleardrops: stopped early: boom. Up to that point: removed 3 · kept 1 gear", true)]
        [InlineData("cleardrops", "cleardrops: started on 5120 ground items out of 184220 objects.", false)]
        [InlineData("cleardrops", "cleardrops: running: scanned 1 of 2, removed 0 so far", false)]
        [InlineData("cleardrops", "cleardrops: already running, 300 ground items still to go.", false)]
        [InlineData("cleardrops", "cleardrops: nothing is running.", false)]
        [InlineData("cleardrops", "Error: the radius has to be a number of metres from 0 to 1000, like 32.", false)]
        [InlineData("killall", "KillAll complete: 37 hostiles slain, 4 out of reach, 162 spared (players, pets & allies)", true)]
        [InlineData("killall", "KillAll stopped early: boom. Up to that point: 0 hostiles slain", true)]
        [InlineData("killall", "KillAll started: 4120 candidates.", false)]
        [InlineData("killall", "killall: running: scanned 1 of 2, removed 0 so far", false)]
        [InlineData("cleanse", "Cleanse complete: nothing in this world carries a cheat mark.", true)]
        [InlineData("cleanse", "Cleanse stopped: 1 player connected while it was running (Bjorn).", true)]
        [InlineData("cleanse", "Cleanse started: 240000 objects to check.", false)]
        [InlineData("cleanse", "Error: 1 player is still connected (Bjorn).", false)]
        // a result read for the wrong sweep is no result of this one
        [InlineData("killall", "Cleanse complete: nothing in this world carries a cheat mark.", false)]
        [InlineData("save", "save: started", false)]
        [InlineData("cleardrops", "", false)]
        [InlineData("cleardrops", null, false)]
        public void Only_a_result_line_is_a_result(string verb, string reply, bool result)
        {
            Assert.Equal(result, SweepReplies.IsResult(verb, reply));
        }

        // ------------------------------------------------------------------
        //  The plugin and the bridge, over the real files
        // ------------------------------------------------------------------

        /// <summary>
        /// The sweeps themselves are fenced behind VALHEIM_PLUGIN and only run on a server, so
        /// what they must do is held over their source: answer the status verb, keep the last
        /// result for it, and hand every ending to the stdout hook Commander sets.
        /// </summary>
        [Fact]
        public void Commander_answers_both_new_status_verbs_and_echoes_every_result()
        {
            var commander = Read("ValheimBakaLoader", "Resources", "Commander", "BakaLoaderCommander.cs");
            Assert.Contains("private const string PluginVersion = \"1.9.2\";", commander);
            Assert.Contains("case KillAllPlan.StatusVerb: return KillAllSweep.Status();", commander);
            Assert.Contains("case BakaLoaderClearDrops.ClearDropsPlan.StatusVerb: return ClearDropsSweep.Status();", commander);
            Assert.Contains("case \"baka_cleanse_status\": return CleanseSweep.Status();", commander);
            Assert.Contains("KillAllSweep.Ended = line => EchoResult(\"killall\", line);", commander);
            Assert.Contains("CleanseSweep.Ended = line => EchoResult(\"cleanse\", line);", commander);
            Assert.Contains("ClearDropsSweep.Ended = line => EchoResult(\"cleardrops\", line);", commander);
            Assert.Contains("UnityEngine.Debug.Log(SweepEcho.Line(verb, line));", commander);

            var build = Read("ValheimBakaLoader", "Resources", "build-plugins.ps1");
            var entry = build.IndexOf("Dir = \"Commander\"", StringComparison.Ordinal);
            var next = build.IndexOf("Dir = \"", entry + 8, StringComparison.Ordinal);
            Assert.Contains("\"BakaSweepEcho.cs\"", build.Substring(entry, next - entry));

            var killall = Read("ValheimBakaLoader", "Resources", "KillAll", "BakaKillAll.cs");
            Assert.Contains("private const string PluginVersion = \"1.8.2\";", killall);
        }

        [Theory]
        [InlineData("Commander", "BakaClearDropsSweep.cs", "ClearDropsPlan.StatusRunning(run.Index, run.Total, run.Removed)")]
        [InlineData("KillAll", "BakaKillAllSweep.cs", "KillAllPlan.StatusRunning(run.Index, run.Matched, run.Slain)")]
        [InlineData("KillAll", "BakaCleanseSweep.cs", "CleansePlan.StatusRunning(run.Index, run.Total, run.ZdosCleared + run.ItemsCleared)")]
        public void Every_sweep_answers_its_status_and_hands_its_ending_to_the_hook(string dir, string file, string running)
        {
            var src = Read("ValheimBakaLoader", "Resources", dir, file);
            Assert.Contains("internal static string Status()", src);
            Assert.Contains(running, src);
            Assert.Contains("internal static Action<string> Ended = null;", src);

            // Every ending reaches the hook: the one inside the first slice AND the one a frame
            // later out of Pump. A sweep that only echoed one of them would leave a small world
            // or a big one silent on stdout.
            var hooked = Regex.Matches(src, @"Finish\((finished|reply)\);|Tell\(Ended, (finished|reply|said)\);").Count;
            Assert.True(hooked >= 2, file + " hands only " + hooked + " of its endings to the hook");
        }

        [Fact]
        public void The_unknown_command_line_offers_every_status_verb()
        {
            var commander = Read("ValheimBakaLoader", "Resources", "Commander", "BakaLoaderCommander.cs");
            Assert.Contains(
                "baka_killall, baka_killall_status, baka_cleardrops, baka_cleardrops_status, baka_save, baka_cleanse, baka_cleanse_status)\";",
                commander);
        }

        [Fact]
        public void The_bridge_logs_a_result_and_lets_the_page_poll_quietly()
        {
            var bridge = Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            Assert.Contains("var quiet = p.Value<bool?>(\"quiet\") ?? false;", bridge);
            Assert.Contains("var response = await Server.SendRconCommandAsync(command, quiet);", bridge);
            Assert.Contains("var logged = LogSweepResult(command, response);", bridge);
            Assert.Contains("return new { ok = response != null, response, logged, errorId = (string)null };", bridge);
            Assert.Contains("var logged = outcome.Ok && LogSweepResult(\"baka_cleanse\", outcome.Response);", bridge);
            Assert.Contains("AppLogger.Information(\"{Line:l}\", \"[RCON] \" + response.Trim());", bridge);
            Assert.Contains("PostEvent(\"server.sweepResult\", new { verb = result.Verb, line = result.Line, profile });", bridge);
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "ValheimBakaLoader.sln")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir;
        }

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));
    }
}
