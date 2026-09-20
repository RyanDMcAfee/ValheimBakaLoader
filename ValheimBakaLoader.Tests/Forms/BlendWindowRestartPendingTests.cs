using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The "restart pending" row: the one place the interface says that the running server is
    /// not on the settings the host has saved. Valheim takes its whole configuration off the
    /// command line at launch, so the gap is real until the server comes back, and the row has
    /// to offer the restart that closes it without ever being the thing that takes a world down
    /// on its own. These gates are on the source because what they guard against is a line
    /// somebody adds later, not a value this run produces.
    /// </summary>
    public class BlendWindowRestartPendingTests
    {
        [Fact]
        public void The_row_is_in_the_condition_order_and_comes_last()
        {
            var order = ConditionOrderSource();

            Assert.Contains("\"restartPending\"", order);

            // Last on purpose: every other condition is a failure or a waiting update, and a
            // settings change that needs a restart must never sit in front of a world save that
            // did not write. The bar shows one condition at a time, worst first.
            var pending = order.LastIndexOf("\"restartPending\"", StringComparison.Ordinal);
            var modUpdates = order.LastIndexOf("\"modUpdates\"", StringComparison.Ordinal);
            Assert.True(modUpdates >= 0, "the mod-updates row is gone from CONDITION_ORDER");
            Assert.True(pending > modUpdates, "restartPending must be the last condition in the order");
        }

        [Fact]
        public void The_row_restarts_the_server_and_never_stops_one()
        {
            var row = ConditionSource();

            // The warned restart the Hearth button runs, reused rather than rebuilt: with
            // players online it broadcasts a countdown before anything goes down.
            Assert.Contains("smartRestart()", row);

            foreach (var forbidden in new[]
            {
                "server.stop", "server.start", "server.kill", "server.stopAll", "servers.stop",
                "lifecycleToggle", "stopServer", "app.quit", "app.close",
                // A bare restart RPC would skip the countdown the warned one broadcasts.
                "rpc(\"server.restart\"",
            })
            {
                Assert.False(row.Contains(forbidden, StringComparison.Ordinal),
                    "the restart-pending row must never reach for " + forbidden);
            }
        }

        [Fact]
        public void A_dismissed_row_is_raised_again_by_the_next_change()
        {
            var row = ConditionSource();

            // The dismissal is keyed to the fingerprint of the saved settings, so waving the row
            // away hides those and only those. A row keyed to nothing would stay hidden for the
            // rest of the session no matter what the host saved afterwards.
            Assert.Contains("RESTART_PENDING_HIDDEN=key", row);
            Assert.Contains("restartPendingSig", File.ReadAllText(WebUiPath("app.js")));
        }

        [Fact]
        public void The_page_says_the_same_thing_where_the_saving_happens()
        {
            var page = File.ReadAllText(WebUiPath("app.js"));
            var markup = File.ReadAllText(WebUiPath("index.html"));
            var catalog = File.ReadAllText(WebUiPath("i18n/en.json"));

            string Lore(string id)
            {
                using var document = JsonDocument.Parse(catalog);
                return document.RootElement.GetProperty("keys").GetProperty(id)
                               .GetProperty("lore").GetString();
            }

            // The three sentences live in the catalog now, so the page is read for the id
            // it asks for and the catalog for the words that id answers with. Asserting
            // only the id would let the wording be emptied; only the wording, in a file
            // nothing on this surface reads any more, would pass on a dead entry.
            Assert.Contains("T(\"world.running.note\")", page);
            Assert.Equal("The server is running. Saved changes apply the next time it starts.",
                         Lore("world.running.note"));

            // The note above Save Config, and the element it is written into.
            Assert.Contains("cfgRunningNote", markup);

            // The confirmation after a save made while the world is up.
            Assert.Contains("T(\"world.saved.running.toast\")", page);
            Assert.Equal("Saved. The running server keeps its current settings until it restarts.",
                         Lore("world.saved.running.toast"));

            // And the world dials that were not saved say so rather than passing under a
            // success toast, which is how a difficulty the host had just set looked saved.
            // The five switches are skipped by that same branch since 1.2.1, so the sentence
            // names them: a host told only about the dials would go looking for the switch they
            // turned on and find it off with nothing having said why.
            Assert.Contains("T(\"world.difficulty.not_saved.toast\")", page);
            Assert.Equal("World difficulty and switches were not saved for this world. Reopen Settings and save again.",
                         Lore("world.difficulty.not_saved.toast"));
        }

        [Fact]
        public void Every_session_is_taught_to_read_its_own_profile_again()
        {
            // One place makes a session, and the hook has to be wired there rather than on the
            // window's current profile: a background realm relaunching while the host is looking
            // at another one must read ITS settings, not the ones on screen.
            var bridge = BridgeSource();

            var start = bridge.IndexOf("private ServerSession GetOrCreateSession(", StringComparison.Ordinal);
            Assert.True(start >= 0, "GetOrCreateSession is gone from the bridge");
            var end = bridge.IndexOf("private IReadOnlyCollection<ServerSession> AllSessions()", start, StringComparison.Ordinal);
            Assert.True(end > start, "could not find the end of GetOrCreateSession");

            Assert.Contains("WireRelaunchSettings(session)", bridge[start..end]);

            var wiring = bridge.IndexOf("private void WireRelaunchSettings(", StringComparison.Ordinal);
            Assert.True(wiring >= 0, "WireRelaunchSettings is gone from the bridge");
            var wiringEnd = bridge.IndexOf("\n        /// <summary>", wiring, StringComparison.Ordinal);
            var body = bridge[wiring..(wiringEnd > wiring ? wiringEnd : bridge.Length)];

            // The session's own profile, and the same builder a start uses, so the launch
            // history the guard reads and the log handler both travel with it.
            Assert.Contains("ServerPrefsProvider.LoadPreferences(profile)", body);
            Assert.Contains("BuildServerOptions(MergeLaunchHistory(prefs))", body);
            Assert.DoesNotContain("ActiveProfileName", body);
        }

        [Fact]
        public void The_state_the_page_reads_carries_the_answer()
        {
            var bridge = BridgeSource();

            var start = bridge.IndexOf("private object BuildServerState(ServerSession session)", StringComparison.Ordinal);
            Assert.True(start >= 0, "BuildServerState is gone from the bridge");
            var end = bridge.IndexOf("private object BuildServersList()", start, StringComparison.Ordinal);
            Assert.True(end > start, "could not find the end of BuildServerState");
            var body = bridge[start..end];

            Assert.Contains("restartPending = pending != null", body);
            Assert.Contains("restartPendingSig", body);

            // The comparison itself answers false for anything it cannot read, so the state
            // never throws over a question the page only asked out of interest.
            var rule = bridge.IndexOf("private IValheimServerOptions SavedOptionsIfRestartPending(", StringComparison.Ordinal);
            Assert.True(rule >= 0, "the restart-pending rule is gone from the bridge");
            Assert.Contains("catch (Exception ex)", bridge[rule..(rule + 1800)]);
        }

        // ------------------------------------------------------------------------- plumbing

        /// <summary>The CONDITION_ORDER declaration, up to the map that follows it.</summary>
        private static string ConditionOrderSource()
        {
            var page = File.ReadAllText(WebUiPath("app.js"));

            var start = page.IndexOf("const CONDITION_ORDER=", StringComparison.Ordinal);
            Assert.True(start >= 0, "CONDITION_ORDER is gone from app.js");

            var end = page.IndexOf("const CONDITIONS=", start, StringComparison.Ordinal);
            Assert.True(end > start, "could not find the end of CONDITION_ORDER");

            return page[start..end];
        }

        /// <summary>
        /// The body of conditionRestartPending, from its declaration to the one that follows it.
        /// Reading the whole file would let a call anywhere else in the page satisfy the gate,
        /// which is the opposite of what it is for.
        /// </summary>
        private static string ConditionSource()
        {
            var page = File.ReadAllText(WebUiPath("app.js"));

            var start = page.IndexOf("function conditionRestartPending(", StringComparison.Ordinal);
            Assert.True(start >= 0, "conditionRestartPending is gone from app.js");

            // Every top-level declaration in this file starts at column 0, so the next one is
            // the end of this function.
            var end = page.IndexOf("\nfunction openUpkeepCard()", start, StringComparison.Ordinal);
            Assert.True(end > start, "could not find the end of conditionRestartPending");

            return page[start..end];
        }

        private static string BridgeSource([CallerFilePath] string thisFile = "")
        {
            // <repo>/ValheimBakaLoader.Tests/Forms/BlendWindowRestartPendingTests.cs
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile), "..", ".."));
            return File.ReadAllText(Path.Combine(repo, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"));
        }

        private static string WebUiPath(string file, [CallerFilePath] string thisFile = "")
        {
            // <repo>/ValheimBakaLoader.Tests/Forms/BlendWindowRestartPendingTests.cs
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile), "..", ".."));
            return Path.Combine(repo, "ValheimBakaLoader", "WebUI", file);
        }
    }
}
