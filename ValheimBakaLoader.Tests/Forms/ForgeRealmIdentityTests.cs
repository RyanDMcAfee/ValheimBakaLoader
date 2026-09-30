using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// What a newly forged realm gets of its own, and what it must never inherit.
    /// <para>
    /// WHY THIS EXISTS. The forge had no password field and no community-listing switch, and
    /// servers.create seeded the new profile from whichever realm the host happened to be
    /// standing on. So a new realm came up on THAT realm's password, THAT realm's RCON
    /// password, and THAT realm's Public flag: a realm founded from a listed one went up
    /// listed in the game's public browser, on a password nobody had been told, sharing an
    /// RCON secret with its parent. Nothing said so anywhere.
    /// </para>
    /// </summary>
    public class ForgeRealmIdentityTests
    {
        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];

        /// <summary>The servers.create handler, from its registration to the next one.</summary>
        private static string CreateHandler()
        {
            var bridge = Bridge();
            var from = bridge.IndexOf("RegisterRpc(\"servers.create\"", StringComparison.Ordinal);
            Assert.True(from > 0, "the bridge no longer registers servers.create");
            var to = bridge.IndexOf("RegisterRpc(", from + 20, StringComparison.Ordinal);
            Assert.True(to > from, "the servers.create handler no longer ends at the next registration");
            return bridge.Substring(from, to - from);
        }

        // ---------------------------------------------------------------- the password

        /// <summary>
        /// A realm without a password of its own is refused, by name, before anything is
        /// provisioned. The refusal is a named one so the page can word it.
        /// </summary>
        [Fact]
        public void A_realm_with_no_password_is_refused_by_name()
        {
            var handler = CreateHandler();

            Assert.Contains("var password = p.Value<string>(\"password\") ?? string.Empty;", handler, StringComparison.Ordinal);
            Assert.Contains("HostFacingException(\"servers.create.passwordRequired\"", handler, StringComparison.Ordinal);
            Assert.Contains("created.Password = password;", handler, StringComparison.Ordinal);

            // The refusal is raised before the install is provisioned or the world is copied:
            // a realm half forged and then refused leaves folders behind.
            var refusal = handler.IndexOf("servers.create.passwordRequired", StringComparison.Ordinal);
            foreach (var later in new[] { "ProvisionInstall(", "WorldStore.CopyWorldAs(", "ServerPrefsProvider.SavePreferences(created)" })
            {
                var at = handler.IndexOf(later, StringComparison.Ordinal);
                Assert.True(at > refusal,
                    "a realm with no password is refused only after " + later + " has already run");
            }
        }

        // ---------------------------------------------------------------- the listing

        /// <summary>
        /// A realm is NOT listed in the game's community browser unless the host asked for
        /// it, whatever the realm it was forged from is set to.
        /// </summary>
        [Fact]
        public void A_new_realm_is_private_unless_the_host_asked_otherwise()
        {
            var handler = CreateHandler();

            Assert.Contains("var listPublicly = p.Value<bool?>(\"public\") ?? false;", handler, StringComparison.Ordinal);
            Assert.Contains("created.Public = listPublicly;", handler, StringComparison.Ordinal);

            // And the page never sends it on: the switch is drawn off and read as it stands.
            var app = AppSourceTree.Web("app.js");
            Assert.Contains("<div class=\"toggle\" id=\"wsPublic\">", app, StringComparison.Ordinal);
            Assert.Contains("public:!!(pubT&&on(pubT))", app, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- the RCON secret

        /// <summary>
        /// The RCON password is the new realm's own. Two realms sharing one means the secret a
        /// host hands out for one of them opens the other as well, and the seeded copy handed
        /// every new realm the base realm's.
        /// </summary>
        [Fact]
        public void The_rcon_password_is_this_realms_own_and_not_the_one_it_was_seeded_from()
        {
            Assert.Contains("created.RconPassword = NewRconSecret();", CreateHandler(), StringComparison.Ordinal);

            var maker = typeof(BlendWindow).GetMethod("NewRconSecret",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(maker != null, "the bridge no longer has a way to make an RCON secret");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < 200; i++)
            {
                var secret = (string)maker.Invoke(null, Array.Empty<object>());

                Assert.False(string.IsNullOrWhiteSpace(secret));
                Assert.True(secret.Length >= 16, "an RCON secret of " + secret.Length + " characters is guessable");
                // Letters and digits only: it travels on a command line and into a config file.
                Assert.Matches(new Regex("^[A-Za-z0-9]+$"), secret);
                // And none of the look-alikes, because a host reads this off a screen.
                foreach (var confusable in new[] { 'l', 'o', 'B', 'I', 'O', '0', '1' })
                    Assert.DoesNotContain(confusable.ToString(), secret, StringComparison.Ordinal);

                Assert.True(seen.Add(secret), "two realms forged in a row got the same RCON secret");
            }

            Assert.Equal(200, seen.Count);
        }

        // ---------------------------------------------------------------- the copy question

        /// <summary>
        /// Whether a world can be copied is answered by the SAME code that refuses the copy,
        /// so the forge's note and the forge's refusal can never disagree.
        /// </summary>
        [Fact]
        public void The_copy_question_is_answered_by_the_gate_that_refuses_the_copy()
        {
            var bridge = Bridge();
            var from = bridge.IndexOf("RegisterRpc(\"servers.copyCheck\"", StringComparison.Ordinal);
            Assert.True(from > 0, "the bridge does not answer whether a world can be copied");
            var to = bridge.IndexOf("RegisterRpc(\"servers.create\"", from, StringComparison.Ordinal);
            Assert.True(to > from, "servers.copyCheck no longer sits in front of servers.create");
            var check = bridge.Substring(from, to - from);

            Assert.Contains("RefuseWhileTheWorldIsBeingWritten(world, sourceFolder);", check, StringComparison.Ordinal);
            Assert.Contains("refusal.MessageId", check, StringComparison.Ordinal);

            // Every reason it can answer with is one servers.create really throws, so a
            // renamed refusal takes both sides with it rather than leaving the note silent.
            var named = Regex.Matches(check, "Refused\\(\"([^\"]+)\"")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.NotEmpty(named);
            foreach (var id in named)
                Assert.Contains("HostFacingException(\"" + id + "\"", bridge, StringComparison.Ordinal);
        }
    }
}
