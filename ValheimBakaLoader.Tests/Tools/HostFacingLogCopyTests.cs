using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Collections.Generic;
using System.Linq;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The log a HOST reads, which is not the log a machine reads.
    /// <para>
    /// WHY THIS EXISTS. A walk of the real app found four separate copy defects in lines a
    /// host reads on screen in the Recent log and pastes into a report: every name and every
    /// path came out wrapped in its own quotation marks on top of the quotes the sentence
    /// already had (<c>Provisioning isolated install for '"Walk 1.2.5"'</c>), the connection
    /// test printed a raw catalogue id where its verdict sentence should be, and the routine
    /// language pack check wrote a red failure line for a 404 it expects and recovers from.
    /// </para>
    /// </summary>
    public class HostFacingLogCopyTests
    {
        private static LogEvent Say(string template, params object[] values)
        {
            var parser = new MessageTemplateParser();
            var parsed = parser.Parse(template);
            var tokens = parsed.Tokens.OfType<PropertyToken>().ToList();

            var properties = new List<LogEventProperty>();
            for (var i = 0; i < tokens.Count && i < values.Length; i++)
                properties.Add(new LogEventProperty(tokens[i].PropertyName, new ScalarValue(values[i])));

            return new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parsed, properties);
        }

        // ---------------------------------------------------------------- the stray quotes

        /// <summary>
        /// The four real lines the walk caught, each written exactly as the app writes it.
        /// Serilog's own renderer quotes a string property the way JSON would, and every one
        /// of these templates already carries its own quotes or its own context.
        /// </summary>
        [Theory]
        [InlineData("Provisioning isolated install for '{0}' at {1}.",
                    "Provisioning isolated install for 'Walk 1.2.5' at D:\\Games\\Walk.")]
        [InlineData("Created isolated server '{0}': world={1}.",
                    "Created isolated server 'Walk 1.2.5': world=WalkWorld125.")]
        [InlineData("No readable world header for '{0}' under {1}.",
                    "No readable world header for 'WalkWorld125' under D:\\Saves.")]
        [InlineData("Installed {0} v{1} to {2}.",
                    "Installed Smoothbrain-Sailing v1.1.8 to D:\\Games\\plugins.")]
        public void A_name_or_a_path_is_written_once_and_not_wrapped_in_its_own_quotes(
            string template, string expected)
        {
            var values = new object[] { "Walk 1.2.5", "D:\\Games\\Walk", "WalkWorld125" };
            if (template.Contains("world={1}")) values = new object[] { "Walk 1.2.5", "WalkWorld125" };
            if (template.StartsWith("No readable", StringComparison.Ordinal))
                values = new object[] { "WalkWorld125", "D:\\Saves" };
            if (template.StartsWith("Installed", StringComparison.Ordinal))
                values = new object[] { "Smoothbrain-Sailing", "1.1.8", "D:\\Games\\plugins" };

            var line = PipelineLogger.RenderForAHost(Say(template, values));

            Assert.Equal(expected, line);
            Assert.DoesNotContain("''", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\"\"", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// And the shape that made this worth fixing at the renderer rather than at four call
        /// sites: Serilog's own rendering of the same event still carries the quotes, so this
        /// is a real difference and not a test of itself.
        /// </summary>
        [Fact]
        public void Serilogs_own_rendering_is_what_this_is_fixing()
        {
            var e = Say("Provisioning isolated install for '{0}'.", "Walk 1.2.5");

            Assert.Contains("'\"Walk 1.2.5\"'", e.RenderMessage(), StringComparison.Ordinal);
            Assert.Contains("'Walk 1.2.5'", PipelineLogger.RenderForAHost(e), StringComparison.Ordinal);
        }

        /// <summary>
        /// Everything that is not a plain string still goes through Serilog's own renderer, so
        /// numbers, formats and alignments are exactly as they were.
        /// </summary>
        [Fact]
        public void Numbers_formats_and_alignments_are_left_to_serilog()
        {
            Assert.Equal("3 files, 82563 records",
                PipelineLogger.RenderForAHost(Say("{a} files, {b} records", 3, 82563)));

            // A template that asks for a format keeps whatever Serilog does with it.
            var formatted = PipelineLogger.RenderForAHost(Say("{value:l} stays literal", "plain"));
            Assert.Equal("plain stays literal", formatted);
        }

        // ---------------------------------------------------------------- the raw id

        /// <summary>
        /// The connection test writes its verdict SENTENCE beside the short verdict name. The
        /// sentence is an interface id, and the host side used to drop every interface id from
        /// the catalogue it keeps, so the log read
        /// <c>ok (hearth.upkeep.connection.verdict.ok)</c>: the raw id the whole catalogue
        /// exists to keep off a screen.
        /// </summary>
        [Theory]
        [InlineData("ok")]
        [InlineData("noproxy")]
        [InlineData("ipv4")]
        [InlineData("both")]
        [InlineData("none")]
        public void Every_connection_verdict_has_a_sentence_on_the_host_side(string verdict)
        {
            var id = "hearth.upkeep.connection.verdict." + verdict;
            var said = HostCatalog.EmbeddedEnglish.Say(id);

            Assert.False(string.IsNullOrWhiteSpace(said));
            Assert.NotEqual(id, said);
            Assert.DoesNotContain("hearth.upkeep", said, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- the expected 404

        /// <summary>
        /// The language pack check asks GitHub for the release page of the version it runs,
        /// and for a version whose page is not up yet the answer is 404 every time. That is
        /// the ordinary case, and the app recovers from it by falling back to the newest
        /// release that carries packs.
        /// </summary>
        [Fact]
        public void The_release_lookup_names_the_404_it_expects_and_the_client_honours_it()
        {
            var client = AppSourceTree.Read("ValheimBakaLoader", "Tools", "GitHubClient.cs");
            Assert.Contains(".WhenMissingSay(\"No release page yet for this version;", client, StringComparison.Ordinal);

            var rest = AppSourceTree.Read("ValheimBakaLoader.Tools", "Http", "RestApi.cs");
            Assert.Contains("public ApiCall WhenMissingSay(string sentence)", rest, StringComparison.Ordinal);
            Assert.Contains("_missingSentence != null && response.StatusCode == HttpStatusCode.NotFound", rest, StringComparison.Ordinal);
            Assert.Contains("_context.Logger.Debug(\"{sentence}\", _missingSentence);", rest, StringComparison.Ordinal);

            // And only a 404: every other status is still a failure and still says so.
            var at = rest.IndexOf("_missingSentence != null", StringComparison.Ordinal);
            var error = rest.IndexOf("Web request to {url} returned {status}", at, StringComparison.Ordinal);
            Assert.True(error > at, "the ordinary failure line was taken away with the expected one");
        }

        // ---------------------------------------------------------------- the escaped path

        /// <summary>
        /// The Remove mod confirm names the folder a plugin is backed up into, and that folder
        /// is a Windows path with one backslash in it. A catalogue entry is JSON, so a
        /// backslash has to be written twice in the FILE to arrive as one on screen, and
        /// writing it four times is a doubled backslash the host reads.
        /// <para>
        /// Held from the value the lookup really answers with rather than from the file's own
        /// bytes, which is the only end that matters: this is what lands in the dialog.
        /// </para>
        /// </summary>
        [Fact]
        public void The_remove_confirm_names_the_backup_folder_with_one_backslash()
        {
            using var document = System.Text.Json.JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var said = document.RootElement
                .GetProperty("keys")
                .GetProperty("mods.remove.backup.note")
                .GetProperty("lore")
                .GetString();

            Assert.Contains("BepInEx", said, StringComparison.Ordinal);
            Assert.DoesNotContain("\\\\", said, StringComparison.Ordinal);
            Assert.Equal(1, said.Count(c => c == '\\'));
        }

        // ---------------------------------------------------------------- removing a mod

        /// <summary>
        /// Removing a mod while a server is up is a REFUSAL now, not a warning the host could
        /// walk past. Windows will not let a loaded plugin be deleted, so the folder came half
        /// away and the install was left in a state nobody asked for.
        /// </summary>
        [Fact]
        public void Removing_a_mod_while_a_server_is_up_is_refused_by_name()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];
            var from = bridge.IndexOf("RegisterRpc(\"mods.remove\"", StringComparison.Ordinal);
            Assert.True(from > 0, "the bridge no longer registers mods.remove");
            var to = bridge.IndexOf("RegisterRpc(", from + 20, StringComparison.Ordinal);
            var handler = bridge.Substring(from, to - from);

            Assert.Contains("HostFacingException(\"mods.remove.serverRunning\"", handler, StringComparison.Ordinal);

            // The refusal comes BEFORE anything is looked up or touched.
            var refusal = handler.IndexOf("mods.remove.serverRunning", StringComparison.Ordinal);
            var acts = handler.IndexOf("ModRemovalService.RemoveMod(", StringComparison.Ordinal);
            Assert.True(acts > refusal, "the removal runs before the running server is noticed");

            // Anything but Stopped counts, the same rule profiles.* holds.
            Assert.Contains("session.Server.Status != ServerStatus.Stopped", bridge, StringComparison.Ordinal);

            // And the page: a sentence for the refusal, and a confirm whose button is off.
            var app = AppSourceTree.Web("app.js");
            Assert.Contains("named:\"mods.remove.serverRunning\"", app, StringComparison.Ordinal);
            Assert.Contains("{disableOk:running}", app, StringComparison.Ordinal);

            var catalog = AppSourceTree.Web("i18n/en.json");
            Assert.Contains("\"mods.remove.reason.server_running\"", catalog, StringComparison.Ordinal);
        }

        /// <summary>
        /// And the refusal is scoped to the INSTALL, with the page reading the same answer.
        /// <para>
        /// The gate walked every session and refused over the first one that was up, whatever
        /// install it belonged to, while the page asked only about the realm on screen. Two
        /// wrongs out of one disagreement: a confirm whose button was on and whose press was
        /// then refused naming a realm the host was not looking at, and a removal blocked on a
        /// stopped realm whose plugins the running process had never opened. Isolated installs
        /// are the default, so that second one is the normal shape of a box with two realms.
        /// </para>
        /// </summary>
        [Fact]
        public void The_remove_refusal_is_scoped_to_the_install_and_the_page_reads_it()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];

            // The gate is given the folders the removal will touch, not asked about the world.
            Assert.Contains("RunningSessionName(RemovalFolders(p))", bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("RunningSessionName()", bridge, StringComparison.Ordinal);

            // And that list carries the patchers folder only when this mod has a part in it.
            // patchers is a junction to the base install on every isolated realm, so naming it
            // always would put the old refuse-everything rule back; never naming it would let a
            // stopped realm delete out of the folder a running realm has loaded.
            Assert.Contains("ResolvePatcherDirectory(mod)", bridge, StringComparison.Ordinal);
            Assert.Contains("BepInExSubdirectoryOf(exePath, \"patchers\")", bridge, StringComparison.Ordinal);

            // And it answers out of the one rule, which has tests of its own.
            Assert.Contains("Tools.ModRemovalGate.BlockedBy(", bridge, StringComparison.Ordinal);

            // The page asks the host side the same question rather than reading the realm on
            // screen, which is a different question with a different answer.
            Assert.Contains("RegisterRpc(\"mods.removeGuard\"", bridge, StringComparison.Ordinal);

            var app = AppSourceTree.Web("app.js");
            Assert.Contains("rpc(\"mods.removeGuard\"", app, StringComparison.Ordinal);
            Assert.Contains("guard.blockedBy", app, StringComparison.Ordinal);
            Assert.Contains("const running=!!blockedBy;", app, StringComparison.Ordinal);

            // And the note names the realm to stop instead of leaving the host to guess.
            Assert.Contains("mods.remove.running.warn.named", app, StringComparison.Ordinal);

            var catalog = AppSourceTree.Web("i18n/en.json");
            Assert.Contains("\"mods.remove.running.warn.named\"", catalog, StringComparison.Ordinal);
        }
    }
}
