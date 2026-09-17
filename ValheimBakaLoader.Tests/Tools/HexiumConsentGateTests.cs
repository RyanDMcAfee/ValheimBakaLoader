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
