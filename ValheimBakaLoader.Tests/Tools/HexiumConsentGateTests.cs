using System;
using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The gate that makes an install from the second site something the host did.
    /// mods.installFromHexium asks this gate first and does nothing at all without a yes,
    /// so every way of getting past it is worth a test of its own.
    /// </summary>
    public class HexiumConsentGateTests
    {
        private static HexiumConsentGate Gate(Func<DateTime> clock = null) =>
            new() { UtcNow = clock ?? (() => DateTime.UtcNow) };

        [Fact]
        public void Nothing_is_accepted_until_the_host_has_been_asked()
        {
            var gate = Gate();

            Assert.False(gate.HasOutstanding);
            Assert.False(gate.Take(null, "Owner", "Mod", "1.0.0"));
            Assert.False(gate.Take("", "Owner", "Mod", "1.0.0"));
            Assert.False(gate.Take("made-up-token", "Owner", "Mod", "1.0.0"));
        }

        [Fact]
        public void A_token_from_the_dialog_is_taken_once_and_once_only()
        {
            var gate = Gate();
            var token = gate.Issue("Owner", "Mod", "1.0.0");

            Assert.True(gate.Take(token, "Owner", "Mod", "1.0.0"));

            // A second install call carrying the same token is refused, so a repeated
            // request cannot fetch the same thing twice off one answer.
            Assert.False(gate.Take(token, "Owner", "Mod", "1.0.0"));
            Assert.False(gate.HasOutstanding);
        }

        [Fact]
        public void A_token_is_good_only_for_the_package_and_version_the_host_was_shown()
        {
            var gate = Gate();
            var token = gate.Issue("Owner", "Mod", "1.0.0");

            Assert.False(gate.Take(token, "Owner", "Mod", "2.0.0"));      // a different version
            Assert.False(gate.HasOutstanding);                            // and it was spent trying

            token = gate.Issue("Owner", "Mod", "1.0.0");
            Assert.False(gate.Take(token, "SomebodyElse", "Mod", "1.0.0"));

            token = gate.Issue("Owner", "Mod", "1.0.0");
            Assert.False(gate.Take(token, "Owner", "OtherMod", "1.0.0"));
        }

        [Fact]
        public void A_wrong_token_never_passes_even_for_the_right_package()
        {
            var gate = Gate();
            gate.Issue("Owner", "Mod", "1.0.0");

            Assert.False(gate.Take(Guid.NewGuid().ToString("N"), "Owner", "Mod", "1.0.0"));
        }

        [Fact]
        public void An_answer_left_sitting_goes_stale()
        {
            var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
            var gate = Gate(() => now);
            var token = gate.Issue("Owner", "Mod", "1.0.0");

            now = now.AddMinutes(11);

            Assert.False(gate.Take(token, "Owner", "Mod", "1.0.0"));
        }

        [Fact]
        public void An_answer_inside_the_window_still_stands()
        {
            var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
            var gate = Gate(() => now);
            var token = gate.Issue("Owner", "Mod", "1.0.0");

            now = now.AddMinutes(9);

            Assert.True(gate.Take(token, "Owner", "Mod", "1.0.0"));
        }

        [Fact]
        public void Asking_a_second_question_drops_the_first_answer()
        {
            var gate = Gate();
            var first = gate.Issue("Owner", "Mod", "1.0.0");
            var second = gate.Issue("Owner", "OtherMod", "2.0.0");

            Assert.False(gate.Take(first, "Owner", "Mod", "1.0.0"));

            // And the first refusal did not spend the second answer either, because a
            // refused token clears whatever was standing. The host answers again.
            Assert.False(gate.Take(second, "Owner", "OtherMod", "2.0.0"));
        }

        [Fact]
        public void Clearing_drops_an_answer_without_spending_it()
        {
            var gate = Gate();
            var token = gate.Issue("Owner", "Mod", "1.0.0");
            gate.Clear();

            Assert.False(gate.HasOutstanding);
            Assert.False(gate.Take(token, "Owner", "Mod", "1.0.0"));
        }

        // --- And the install call really does ask it ---

        [Fact]
        public void The_install_call_refuses_before_it_looks_anything_up()
        {
            var handler = Between(BridgeSource(), "RegisterRpc(\"mods.installFromHexium\"", "// --- Capabilities");

            Assert.False(string.IsNullOrEmpty(handler), "the installFromHexium handler is gone");

            var consent = handler.IndexOf("HexiumConsent.Take(", StringComparison.Ordinal);
            var lookup = handler.IndexOf("HexiumClient.LookupAsync", StringComparison.Ordinal);
            var install = handler.IndexOf("InstallFromHexiumAsync", StringComparison.Ordinal);

            Assert.True(consent > 0, "the install call no longer asks the consent gate");
            Assert.True(lookup > consent, "the install call looks the package up before checking consent");
            Assert.True(install > consent, "the install call installs before checking consent");

            // And it is gated on the switch as well, ahead of everything.
            var switchCheck = handler.IndexOf("UseHexiumSource", StringComparison.Ordinal);
            Assert.True(switchCheck > 0 && switchCheck < consent,
                "the install call must check the switch before anything else");
        }

        /// <summary>
        /// "Spent once" has to hold when two calls arrive at the same moment, not only when
        /// they arrive one after the other. The gate read the acceptance and cleared it as
        /// two separate steps with nothing holding them together, so two install calls
        /// carrying the same token could both read it before either cleared it, and both
        /// would be told yes: one answer from the host, two downloads.
        /// <para>
        /// Each round here starts a pair of takes on the thread pool and releases them
        /// together, and runs enough rounds that the unguarded version loses the race.
        /// </para>
        /// </summary>
        [Fact]
        public void Two_takes_at_the_same_moment_cannot_both_succeed()
        {
            const int rounds = 400;
            var doubleSpends = 0;
            var spends = 0;

            for (var round = 0; round < rounds; round++)
            {
                var gate = Gate();
                var token = gate.Issue("Owner", "Mod", "1.0.0");

                using var start = new System.Threading.Barrier(2);
                var answers = new bool[2];

                var pair = new[]
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        start.SignalAndWait();
                        answers[0] = gate.Take(token, "Owner", "Mod", "1.0.0");
                    }),
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        start.SignalAndWait();
                        answers[1] = gate.Take(token, "Owner", "Mod", "1.0.0");
                    }),
                };

                System.Threading.Tasks.Task.WaitAll(pair);

                var taken = (answers[0] ? 1 : 0) + (answers[1] ? 1 : 0);
                if (taken > 1) doubleSpends++;
                if (taken == 1) spends++;

                Assert.False(gate.HasOutstanding);
            }

            Assert.Equal(0, doubleSpends);
            // And the answer was not simply lost by both: exactly one side got it every time.
            Assert.Equal(rounds, spends);
        }

        /// <summary>
        /// A server path that is wrong or not set yet is checked BEFORE the acceptance is
        /// spent. A token is good once, so asking afterwards burned the host's answer on a
        /// refusal they could do nothing about and the dialog had to be read again.
        /// </summary>
        [Fact]
        public void A_missing_plugins_folder_is_found_before_the_acceptance_is_spent()
        {
            var handler = Between(BridgeSource(), "RegisterRpc(\"mods.installFromHexium\"", "// --- Capabilities");

            var pluginsCheck = handler.IndexOf("!Directory.Exists(pluginsDir)", StringComparison.Ordinal);
            var consent = handler.IndexOf("HexiumConsent.Take(", StringComparison.Ordinal);

            Assert.True(pluginsCheck > 0, "the install call no longer checks the plugins folder");
            Assert.True(pluginsCheck < consent,
                "the plugins folder is checked after the acceptance is spent, so a bad path burns it");
        }

        /// <summary>
        /// Every call that reaches the second site stands behind the host's own switch, and
        /// that includes opening one of its pages in the browser. The scan and the install
        /// both checked it; opening a page did not, so a host with the switch off could still
        /// be sent to hexium.gg by a stale row.
        /// </summary>
        [Fact]
        public void Opening_a_hexium_page_is_behind_the_switch_like_the_other_two_calls()
        {
            var bridge = BridgeSource();

            foreach (var call in new[] { "mods.hexiumPrepare", "mods.installFromHexium", "shell.openHexium" })
            {
                var handler = Between(bridge, "RegisterRpc(\"" + call + "\"", "RegisterRpc(\"" + NextAfter(call) + "\"");
                Assert.False(string.IsNullOrEmpty(handler), "the " + call + " handler is gone");
            }

            var open = Between(bridge, "RegisterRpc(\"shell.openHexium\"", "RegisterRpc(\"shell.openThunderstore\"");

            var switchCheck = open.IndexOf("UseHexiumSource", StringComparison.Ordinal);
            var address = open.IndexOf("HexiumUrlParser.PageUrl", StringComparison.Ordinal);
            var start = open.IndexOf("Process.Start", StringComparison.Ordinal);

            Assert.True(switchCheck > 0, "opening a Hexium page no longer checks the switch");
            Assert.True(address > switchCheck, "the address is built before the switch is checked");
            Assert.True(start > switchCheck, "the browser is opened before the switch is checked");
        }

        /// <summary>The handler that follows each of the three, for slicing the bridge source.</summary>
        private static string NextAfter(string call) => call switch
        {
            "mods.hexiumPrepare" => "mods.installFromHexium",
            "mods.installFromHexium" => "caps.get",
            "shell.openHexium" => "shell.openThunderstore",
            _ => throw new ArgumentOutOfRangeException(nameof(call)),
        };

        [Fact]
        public void Nothing_else_in_the_app_installs_from_the_second_site()
        {
            // One caller, and it is the one that asks the gate. Anything else reaching
            // InstallFromHexiumAsync would be a way around the question.
            var callers = AppSourceTree.Files()
                .Where(f => f.Value.Contains("InstallFromHexiumAsync", StringComparison.Ordinal))
                .Select(f => f.Key)
                .OrderBy(f => f)
                .ToArray();

            Assert.Equal(new[] { "BlendWindow.Bridge.cs", "ModUpdateService.cs" }, callers);
        }

        private static string Between(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            if (start < 0) return "";
            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            return end < 0 ? source.Substring(start) : source.Substring(start, end - start);
        }

        private static string BridgeSource() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];
    }
}
