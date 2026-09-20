using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The sentences that leave the app without going through a page: the restart countdown
    /// over RCON, and every Discord post. They are the half of the product no screenshot
    /// shows and no browser walk reaches, so what guards them is that every site which used
    /// to spell one out now asks the catalog for it, and that the catalog has the words.
    /// </summary>
    public class HostSentenceCopyTests
    {
        private static string Server() => AppSourceTree.Files()["ValheimServer.cs"];

        private static string Webhooks() => AppSourceTree.Files()["DiscordWebhookService.cs"];

        private static string StatusPost() => AppSourceTree.Files()["DiscordStatusService.cs"];

        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];

        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateObject())
                map[entry.Name] = entry.Value.Clone();
            return map;
        }

        private static List<string> IdsAskedFor(params string[] sources) =>
            sources
                .SelectMany(s => Regex.Matches(s, "HostCatalog\\.T\\(\\s*\"([^\"]+)\"").Cast<Match>())
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        // ------------------------------------------------------------------ the sites moved

        /// <summary>
        /// The countdown a player reads in the middle of their screen. Three sentences and a
        /// suffix, and the span of time inside the first one, which is the piece a ternary on
        /// an "s" could never have said in Russian.
        /// </summary>
        [Fact]
        public void The_restart_countdown_is_written_from_the_catalog()
        {
            var server = Server();

            Assert.Contains("HostCatalog.T(\"host.countdown.restart_in\", (\"time\", PlayerTime(remaining)))", server, StringComparison.Ordinal);
            Assert.Equal(2, Regex.Matches(server, "HostCatalog\\.T\\(\"host\\.countdown\\.restart_now\"\\)").Count);
            Assert.Contains("HostCatalog.T(\"host.countdown.cancelled\")", server, StringComparison.Ordinal);
            Assert.Contains("HostCatalog.T(\"host.countdown.update_note\", (\"count\", modUpdateCount))", server, StringComparison.Ordinal);

            foreach (var id in new[] { "host.time.hours", "host.time.minutes", "host.time.seconds" })
                Assert.Contains("HostCatalog.T(\"" + id + "\"", server, StringComparison.Ordinal);

            // And none of the English is still spelled out where it was.
            Assert.DoesNotContain("Server restarting in", server, StringComparison.Ordinal);
            Assert.DoesNotContain("Server restarting NOW", server, StringComparison.Ordinal);
            Assert.DoesNotContain("Server restart cancelled", server, StringComparison.Ordinal);
            Assert.DoesNotContain("mod update{", server, StringComparison.Ordinal);
        }

        /// <summary>
        /// The window's own countdown chip is NOT one of these. It is read by the host in the
        /// interface, not by a player on the server, so it goes to the INTERFACE catalog
        /// rather than this one: the two audiences can be reading two different languages.
        /// <para>
        /// What the chip carries over the bridge is an id and a number, never a sentence. It
        /// used to carry "Restart in 5 minutes", built here in English, and the page toasted
        /// that string verbatim: a host reading the window in Russian read the chip in
        /// English, and no gate saw it because a raw literal handed to an event is not an
        /// exception, not a log line, and has no dash in it. These lines are what stop it
        /// coming back.
        /// </para>
        /// </summary>
        [Fact]
        public void The_windows_own_countdown_chip_carries_an_id_and_not_a_sentence()
        {
            var server = Server();

            Assert.Contains("CountdownTick?.Invoke(this, CountdownChip.RestartIn(remaining));", server, StringComparison.Ordinal);
            Assert.Contains("CountdownTick?.Invoke(this, CountdownChip.RestartNow);", server, StringComparison.Ordinal);

            // And the English that used to be built here is gone, along with the helper that
            // built it. FormatTime existed for that one call site and nothing else.
            Assert.DoesNotContain("Restart in {", server, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Restarting now\"", server, StringComparison.Ordinal);
            Assert.DoesNotContain("FormatTime(", server, StringComparison.Ordinal);
            Assert.DoesNotContain("\"1 minute\"", server, StringComparison.Ordinal);
            Assert.DoesNotContain("\"1 hour\"", server, StringComparison.Ordinal);
            Assert.DoesNotContain("\"1 second\"", server, StringComparison.Ordinal);
        }

        /// <summary>
        /// The other half of the same seam: the bridge forwards the id, the unit and the
        /// count, the page has words for all three tiers, and it asks for them.
        /// </summary>
        [Fact]
        public void The_countdown_chip_is_written_from_the_interface_catalog()
        {
            Assert.Contains("id = chip?.Id, unit = chip?.Unit, count = chip?.Count ?? 0", Bridge(), StringComparison.Ordinal);

            var app = AppSourceTree.Web("app.js");
            Assert.Contains("hearth.countdown.restart_in", app, StringComparison.Ordinal);
            Assert.Contains("hearth.countdown.restart_now", app, StringComparison.Ordinal);
            Assert.DoesNotContain("+d.message", app, StringComparison.Ordinal);

            var catalog = Catalog();
            foreach (var id in new[]
            {
                "hearth.countdown.restart_in", "hearth.countdown.restart_now",
                "hearth.countdown.hours", "hearth.countdown.minutes", "hearth.countdown.seconds",
            })
            {
                Assert.True(catalog.ContainsKey(id), "the catalog has no " + id);
            }
        }

        /// <summary>
        /// A6. Splitting the chip changed two things that were not the page's words: the
        /// EventArgs type on ValheimServer.CountdownTick, and the "message" key the
        /// server.countdown event carried. The first is a C# break and is noted where it
        /// happened; the second is a contract and is mended here. The key is back, holding the
        /// same English it always held, beside the id and the number rather than instead of
        /// them.
        /// </summary>
        [Fact]
        public void The_countdown_event_still_carries_the_english_it_always_carried()
        {
            var bridge = Bridge();

            Assert.Contains(
                "count = chip?.Count ?? 0, message = CountdownMessage(chip), profile",
                bridge,
                StringComparison.Ordinal);

            // The tick that ends a countdown carries no chip, so it has nothing to say.
            Assert.Null(BlendWindow.CountdownMessage(null));

            Assert.Equal("Restarting now", BlendWindow.CountdownMessage(ValheimServer.CountdownChip.RestartNow));
        }

        /// <summary>
        /// And the English is the English, to the byte. These are the exact sentences the old
        /// hand-rolled FormatTime produced, tier by tier and singular by plural, which is what
        /// makes restoring the key a repair rather than a new string nobody has read.
        /// </summary>
        [Theory]
        [InlineData(7200, "Restart in 2 hours")]
        [InlineData(3600, "Restart in 1 hour")]
        [InlineData(300, "Restart in 5 minutes")]
        [InlineData(60, "Restart in 1 minute")]
        [InlineData(30, "Restart in 30 seconds")]
        [InlineData(1, "Restart in 1 second")]
        public void The_chips_english_is_the_english_the_old_helper_wrote(int seconds, string english)
        {
            Assert.Equal(english, BlendWindow.CountdownMessage(ValheimServer.CountdownChip.RestartIn(seconds)));
        }

        /// <summary>
        /// It is not written down here, it is read out of the one catalog that owns it. A
        /// literal would be the same sentence with two owners, and the page's copy is the one
        /// a translator is handed.
        /// </summary>
        [Fact]
        public void The_chips_english_comes_out_of_the_catalog_rather_than_out_of_the_bridge()
        {
            var bridge = Bridge();

            Assert.Contains("HostCatalog.PageEnglish(\"hearth.countdown.restart_in\"", bridge, StringComparison.Ordinal);
            Assert.Contains("HostCatalog.PageEnglish(\"hearth.countdown.restart_now\")", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Restart in \"", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Restarting now\"", bridge, StringComparison.Ordinal);

            // And it is English whatever the players are being written to in, because what
            // reads it is a program rather than a person.
            Assert.Null(HostCatalog.PageEnglish("host.countdown.restart_now"));
            Assert.Null(HostCatalog.PageEnglish(null));
        }

        /// <summary>Every Discord post, title and body.</summary>
        [Theory]
        [InlineData("host.discord.started")]
        [InlineData("host.discord.stopped")]
        [InlineData("host.discord.crashed")]
        [InlineData("host.discord.joined")]
        [InlineData("host.discord.left")]
        [InlineData("host.discord.held")]
        [InlineData("host.discord.updated")]
        [InlineData("host.discord.update_failed")]
        [InlineData("host.discord.legacy_world")]
        public void Every_discord_post_is_written_from_the_catalog(string prefix)
        {
            var webhooks = Webhooks();

            Assert.Contains("HostCatalog.T(\"" + prefix + ".title\")", webhooks, StringComparison.Ordinal);
            Assert.Contains("HostCatalog.T(\"" + prefix + ".body", webhooks, StringComparison.Ordinal);
        }

        /// <summary>
        /// And nothing of the old wording is left in the file. A sentence half moved is a
        /// language that reads as two.
        /// </summary>
        [Theory]
        [InlineData("is now online")]
        [InlineData("has been shut down")]
        [InlineData("has crashed")]
        [InlineData("joined **")]
        [InlineData("left **")]
        [InlineData("was not started automatically")]
        [InlineData("finished updating")]
        [InlineData("was not updated")]
        [InlineData("save format")]
        [InlineData("Server Started")]
        [InlineData("Player Joined")]
        public void No_discord_post_still_spells_its_own_english(string fragment)
        {
            Assert.DoesNotContain(fragment, Webhooks(), StringComparison.Ordinal);
        }

        /// <summary>
        /// The self-editing status post: every field name and the handful of values that are
        /// words rather than a name, a count or a Discord timestamp.
        /// </summary>
        [Fact]
        public void The_status_post_is_written_from_the_catalog()
        {
            var post = StatusPost();

            foreach (var id in new[]
            {
                "host.status.title", "host.status.footer", "host.status.field.status",
                "host.status.field.players", "host.status.field.world", "host.status.field.address",
                "host.status.field.mods", "host.status.field.last_mod_update",
                "host.status.field.next_restart", "host.status.field.password",
                "host.status.value.unknown", "host.status.value.mods_installed",
                "host.status.value.vanilla", "host.status.value.not_scheduled",
            })
            {
                Assert.Contains("HostCatalog.T(\"" + id + "\"", post, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("name = \"Status\"", post, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Players online\"", post, StringComparison.Ordinal);
            Assert.DoesNotContain("not scheduled", post, StringComparison.Ordinal);
            Assert.DoesNotContain("this post updates itself", post, StringComparison.Ordinal);

            // The bare hyphen stays: it is the empty-value placeholder, not a sentence.
            Assert.Contains("\"-\"", post, StringComparison.Ordinal);
        }

        /// <summary>
        /// The one word the snapshot carries. It is built in the bridge and read in the post,
        /// so it belongs to the players' language rather than to the window's.
        /// </summary>
        [Fact]
        public void The_servers_state_word_is_written_from_the_catalog()
        {
            var bridge = Bridge();

            foreach (var id in new[] { "online", "starting", "stopping", "offline" })
                Assert.Contains("HostCatalog.T(\"host.status.state." + id + "\")", bridge, StringComparison.Ordinal);

            Assert.DoesNotContain("ServerStatus.Running => \"Online\"", bridge, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ the catalog has the words

        /// <summary>
        /// Every id the C# side asks for is in the English catalog. The catalog gate asks the
        /// same question from the other direction, in the copy gate; this is it in the suite,
        /// so a build that never runs the gate still cannot ship a Discord post that says
        /// "host.discord.started.title".
        /// </summary>
        [Fact]
        public void Every_id_the_host_side_asks_for_has_words()
        {
            var catalog = Catalog();
            var asked = IdsAskedFor(Server(), Webhooks(), StatusPost(), Bridge());

            Assert.True(asked.Count >= 40, "the host side asks for " + asked.Count + " sentences");

            foreach (var id in asked)
            {
                Assert.True(id.StartsWith(HostCatalog.Prefix, StringComparison.Ordinal),
                    id + " is not a host sentence");
                Assert.True(catalog.ContainsKey(id), "the catalog has no " + id);
                Assert.True(HostCatalog.EmbeddedEnglish.Has(id), "the embedded catalog has no " + id);
            }
        }

        /// <summary>
        /// The lookup is handed a literal id, never an expression that picks one. It is a
        /// small discipline and it is what lets the catalog gate see which ids the C# side
        /// asks for at all; a ternary inside the call and every one of these sentences reads
        /// as a key nobody asks for.
        /// </summary>
        [Fact]
        public void The_lookup_is_always_handed_a_literal_id()
        {
            foreach (var source in new[] { Server(), Webhooks(), StatusPost(), Bridge() })
            {
                // A call wrapped onto the next line is still handed a literal, so the scan
                // steps over the whitespace the wrap is made of rather than reading it as an
                // expression.
                foreach (Match call in Regex.Matches(source, "HostCatalog\\.T\\(\\s*(.{0,20})", RegexOptions.Singleline))
                {
                    Assert.StartsWith("\"host.", call.Groups[1].Value, StringComparison.Ordinal);
                }
            }
        }

        /// <summary>
        /// The catalog is embedded rather than only copied beside the exe. The install folder
        /// can be read only, and a self update robocopies over it.
        /// </summary>
        [Fact]
        public void The_project_embeds_the_english_catalog()
        {
            var csproj = AppSourceTree.Read("ValheimBakaLoader", "ValheimBakaLoader.csproj");

            Assert.Contains(
                "<EmbeddedResource Include=\"WebUI\\i18n\\en.json\" LogicalName=\"ValheimBakaLoader.WebUI.i18n.en.json\" />",
                csproj,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// And the catalog gate knows where to look for the C# half. Without this the
        /// completeness check reads all forty of these as keys nobody asks for.
        /// </summary>
        [Fact]
        public void The_catalog_gate_reads_the_host_side_too()
        {
            var gate = AppSourceTree.Read("scripts", "i18n", "check_catalog.py");

            Assert.Contains("HOST_T_CALL", gate, StringComparison.Ordinal);
            Assert.Contains("HostCatalog", gate, StringComparison.Ordinal);
            Assert.Contains("host=host_ids(", gate, StringComparison.Ordinal);
        }
    }
}
