using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The catalog, the lookup that reads it, and the two gates that guard both.
    /// <para>
    /// Half of this file runs the shipped gates and insists they are happy. The other
    /// half feeds each gate a catalog with exactly one thing wrong with it and insists
    /// it says so, because a gate nobody has ever seen fail is a gate nobody knows
    /// works. Every fixture here would have gone green under the gate this replaces.
    /// </para>
    /// </summary>
    public class WebUiCatalogTests : IDisposable
    {
        private static string CheckCatalog => RepoScript.At("scripts", "i18n", "check_catalog.py");
        private static string SelfTest => RepoScript.At("scripts", "i18n", "i18n_selftest.js");

        private readonly List<string> _scratch = new();

        public void Dispose()
        {
            foreach (var folder in _scratch)
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                catch (IOException) { /* a virus scanner still holding it; it is a temp folder */ }
            }
        }

        /// <summary>A throwaway folder, cleaned up when the class is done with it.</summary>
        private string Scratch()
        {
            var folder = Path.Combine(Path.GetTempPath(), "vbl-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            _scratch.Add(folder);
            return folder;
        }

        private static void Write(string folder, string name, string text) =>
            File.WriteAllText(Path.Combine(folder, name), text, new UTF8Encoding(false));

        /// <summary>The gate over a folder of fixtures, with no completeness arm.</summary>
        private static RepoScript.Result CheckFolder(string folder) =>
            RepoScript.Run(RepoScript.Python(), CheckCatalog, "--dir", folder);

        /// <summary>The smallest catalog that passes, as a starting point to break.</summary>
        private const string CleanEnglish = @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": {
    ""hall.title"": { ""lore"": ""Hearth Status"", ""plain"": ""Server Status"" },
    ""hall.greet"": { ""lore"": ""Welcome back, {name}"", ""params"": { ""name"": ""text"" } }
  }
}";

        // ------------------------------------------------------- A. the shipped files

        /// <summary>
        /// The gate the copy gate runs, run here too, so a commit that never goes near
        /// bash still cannot land a catalog the page cannot read.
        /// </summary>
        [Fact]
        public void The_shipped_catalog_passes_every_check()
        {
            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog);
            Assert.True(said.Ok, "the shipped catalog does not pass its own gate:\n" + said);
            Assert.Contains("TOTAL 0", said.Output);
        }

        /// <summary>The lookup's own self test, nineteen cases, no browser needed.</summary>
        [Fact]
        public void The_lookup_passes_its_self_test()
        {
            var said = RepoScript.Run(RepoScript.Node(), SelfTest);
            Assert.True(said.Ok, "the lookup's self test failed:\n" + said);
            Assert.Contains("SELFTEST PASS", said.Output);
        }

        /// <summary>
        /// English is the source of record and ships inside the app, so it has to be
        /// there and it has to say which app it belongs to.
        /// </summary>
        [Fact]
        public void English_ships_in_the_app_and_names_the_version_it_belongs_to()
        {
            var path = RepoScript.At("ValheimBakaLoader", "WebUI", "i18n", "en.json");
            Assert.True(File.Exists(path), "the English catalog is not in the app folder");

            var text = File.ReadAllText(path);
            Assert.Contains("\"language\": \"en\"", text);

            var csproj = File.ReadAllText(RepoScript.At("ValheimBakaLoader", "ValheimBakaLoader.csproj"));
            var version = Regex.Match(csproj, @"<Version>([^<]+)</Version>").Groups[1].Value.Trim();
            Assert.Contains("\"appVersion\": \"" + version + "\"", text);
        }

        /// <summary>
        /// A catalog is copy, so the copy gate has to be reading it. The gate names the
        /// folder; this proves the folder still holds what the gate expects to find.
        /// </summary>
        [Fact]
        public void The_copy_gate_runs_both_catalog_gates()
        {
            var gate = File.ReadAllText(RepoScript.At("scripts", "copy-gate", "copy_gate.sh"));

            Assert.Contains("i18n_selftest.js", gate);
            Assert.Contains("check_catalog.py", gate);
            Assert.Contains("node --check \"$APP/WebUI/i18n.js\"", gate);
        }

        // ------------------------------------------- B. every rule, proved by breaking it

        /// <summary>
        /// Runs the gate over one broken catalog and insists it names the problem.
        /// </summary>
        private void Refuses(string label, string catalog, string expected, string alsoWrite = null, string alsoNamed = null)
        {
            var folder = Scratch();
            Write(folder, "en.json", catalog);
            if (alsoWrite != null) Write(folder, alsoNamed, alsoWrite);

            var said = CheckFolder(folder);
            Assert.False(said.Ok, label + ": the gate passed a catalog it should have refused:\n" + said);
            Assert.Contains(expected, said.Output);
        }

        [Fact]
        public void A_clean_fixture_passes_so_the_negatives_below_mean_something()
        {
            var folder = Scratch();
            Write(folder, "en.json", CleanEnglish);
            var said = CheckFolder(folder);
            Assert.True(said.Ok, "the clean fixture does not pass:\n" + said);
        }

        [Fact]
        public void An_id_that_is_the_English_text_is_refused()
        {
            Refuses("English as the key", @"{
  ""_meta"": { ""language"": ""en"" },
  ""keys"": { ""Hearth Status"": { ""lore"": ""Hearth Status"" } }
}", "not a dotted lower case name");
        }

        [Fact]
        public void An_id_written_twice_in_the_file_is_refused()
        {
            // json keeps the last one and says nothing, so the first sentence silently
            // stops being the one anybody reads.
            Refuses("duplicate id", @"{
  ""_meta"": { ""language"": ""en"" },
  ""keys"": {
    ""hall.title"": { ""lore"": ""Hearth Status"" },
    ""hall.title"": { ""lore"": ""Server Status"" }
  }
}", "id written twice in the file");
        }

        [Fact]
        public void A_plain_register_with_no_lore_is_refused()
        {
            Refuses("half an entry", @"{
  ""_meta"": { ""language"": ""en"" },
  ""keys"": { ""hall.title"": { ""plain"": ""Server Status"" } }
}", "no lore value");
        }

        [Fact]
        public void The_two_registers_have_to_carry_the_same_slots()
        {
            Refuses("slot dropped between registers", @"{
  ""_meta"": { ""language"": ""en"" },
  ""keys"": { ""hall.greet"": {
    ""lore"": ""Welcome back, {name}"",
    ""plain"": ""Welcome back"",
    ""params"": { ""name"": ""text"" } } }
}", "do not carry the same slots");
        }

        [Fact]
        public void Declared_parameters_and_the_slots_actually_used_have_to_agree()
        {
            Refuses("params drifted", @"{
  ""_meta"": { ""language"": ""en"" },
  ""keys"": { ""hall.greet"": {
    ""lore"": ""Welcome back, {name}"",
    ""params"": { ""who"": ""text"" } } }
}", "params and slots disagree");
        }

        [Fact]
        public void A_Russian_plural_missing_a_category_is_refused()
        {
            // one, few, many, other. A translator handed an English two case plural fills
            // in two of the four and the other two render as the wrong word all year.
            var english = @"{
  ""_meta"": { ""language"": ""en"" },
  ""keys"": { ""mods.scanned"": {
    ""lore"": { ""one"": ""{count} mod"", ""other"": ""{count} mods"" },
    ""params"": { ""count"": ""number"" }, ""plural"": ""count"" } }
}";
            var russian = @"{
  ""_meta"": { ""language"": ""ru"" },
  ""keys"": { ""mods.scanned"": {
    ""translation"": { ""one"": ""{count} мод"", ""other"": ""{count} мода"" },
    ""params"": { ""count"": ""number"" }, ""plural"": ""count"" } }
}";
            var folder = Scratch();
            Write(folder, "en.json", english);
            Write(folder, "ru.json", russian);

            var said = CheckFolder(folder);
            Assert.False(said.Ok, "an incomplete Russian plural passed:\n" + said);
            Assert.Contains("missing plural categories for ru", said.Output);
            Assert.Contains("few", said.Output);
            Assert.Contains("many", said.Output);
        }

        [Fact]
        public void A_translation_that_loses_a_slot_is_refused()
        {
            var english = @"{
  ""_meta"": { ""language"": ""en"" },
  ""keys"": { ""hall.greet"": { ""lore"": ""Welcome back, {name}"", ""params"": { ""name"": ""text"" } } }
}";
            var russian = @"{
  ""_meta"": { ""language"": ""ru"" },
  ""keys"": { ""hall.greet"": { ""translation"": ""С возвращением"" } }
}";
            var folder = Scratch();
            Write(folder, "en.json", english);
            Write(folder, "ru.json", russian);

            var said = CheckFolder(folder);
            Assert.False(said.Ok, "a translation with the slot eaten passed:\n" + said);
            Assert.Contains("does not carry the English slots", said.Output);
        }

        [Fact]
        public void A_translation_that_renames_the_product_is_refused()
        {
            var english = @"{
  ""_meta"": { ""language"": ""en"" },
  ""keys"": { ""app.hello"": { ""lore"": ""BakaLoader is watching the hearth"" } }
}";
            var russian = @"{
  ""_meta"": { ""language"": ""ru"" },
  ""keys"": { ""app.hello"": { ""translation"": ""Бакалоадер следит за очагом"" } }
}";
            var folder = Scratch();
            Write(folder, "en.json", english);
            Write(folder, "ru.json", russian);

            var said = CheckFolder(folder);
            Assert.False(said.Ok, "a translated product name passed:\n" + said);
            Assert.Contains("BakaLoader does not survive", said.Output);
        }

        [Fact]
        public void Markup_in_a_value_is_refused_unless_the_entry_says_so()
        {
            Refuses("markup with no allowsHtml", @"{
  ""_meta"": { ""language"": ""en"" },
  ""keys"": { ""hall.note"": { ""lore"": ""Read the <b>log</b>"" } }
}", "carries markup");

            // And the same value passes once the entry has said it means it.
            var folder = Scratch();
            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"" },
  ""keys"": { ""hall.note"": { ""lore"": ""Read the <b>log</b>"", ""allowsHtml"": true } }
}");
            Assert.True(CheckFolder(folder).Ok, "allowsHtml did not let the value through");
        }

        [Fact]
        public void A_long_dash_is_refused_in_English_and_allowed_in_Russian()
        {
            Refuses("long dash in English", "{\n" +
                "  \"_meta\": { \"language\": \"en\" },\n" +
                "  \"keys\": { \"hall.note\": { \"lore\": \"Hearth — status\" } }\n" +
                "}", "U+2014, which en does not allow");

            // Russian needs it: it stands in for the omitted copula, which is how the
            // language writes "X is Y". The rule is a function of the language.
            var folder = Scratch();
            Write(folder, "en.json", "{\n  \"_meta\": { \"language\": \"en\" },\n  \"keys\": { \"hall.note\": { \"lore\": \"Hearth status\" } }\n}");
            Write(folder, "ru.json", "{\n  \"_meta\": { \"language\": \"ru\" },\n  \"keys\": { \"hall.note\": { \"translation\": \"Очаг — состояние\" } }\n}");
            var said = CheckFolder(folder);
            Assert.True(said.Ok, "Russian was refused the dash its grammar requires:\n" + said);
        }

        [Fact]
        public void A_catalog_that_does_not_parse_is_a_finding_rather_than_a_traceback()
        {
            Refuses("broken JSON", "{ \"_meta\": { \"language\": \"en\" }, \"keys\": { ", "not valid JSON");
        }

        // ------------------------------------------- C. the completeness arm, both ways

        /// <summary>
        /// An id the interface asks for and the catalog has never heard of. Today it
        /// renders the dotted id on screen; the gate catches it at commit time.
        /// </summary>
        [Fact]
        public void An_id_the_interface_asks_for_and_the_catalog_lacks_is_refused()
        {
            var folder = Scratch();
            Write(folder, "en.json", CleanEnglish);
            Write(folder, "fake-app.js", "function paint(){ return T(\"hall.nowhere\"); }\n");
            Write(folder, "fake.html", "<div data-i18n=\"hall.title\">Hearth Status</div>\n");

            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));

            Assert.False(said.Ok, "an id with no entry behind it passed:\n" + said);
            Assert.Contains("hall.nowhere", said.Output);
        }

        /// <summary>
        /// And the other direction: a key nothing asks for. A translator is paid per
        /// row, and a reviewer walks every sentence; an orphan costs both of them.
        /// </summary>
        [Fact]
        public void A_key_nothing_asks_for_is_refused()
        {
            var folder = Scratch();
            Write(folder, "en.json", CleanEnglish);
            Write(folder, "fake-app.js", "function paint(){ return T(\"hall.greet\",{name:\"x\"}); }\n");
            Write(folder, "fake.html", "<div>nothing here carries an id</div>\n");

            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));

            Assert.False(said.Ok, "an orphan key passed:\n" + said);
            Assert.Contains("orphan key: nothing asks for hall.title", said.Output);
        }

        /// <summary>
        /// A key still reached through the TT() bridge counts as used. Without this the
        /// gate would force every sentence to move in one commit, which is the one thing
        /// the bridge exists to avoid.
        /// </summary>
        [Fact]
        public void A_key_still_reached_through_the_bridge_is_not_an_orphan()
        {
            var folder = Scratch();
            Write(folder, "en.json", CleanEnglish);
            Write(folder, "fake-app.js",
                "function paint(){ return TT(\"Hearth Status\")+T(\"hall.greet\",{name:\"x\"}); }\n");
            Write(folder, "fake.html", "<div>nothing here carries an id</div>\n");

            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));

            Assert.True(said.Ok, "a key the bridge still reaches was called an orphan:\n" + said);
        }

        /// <summary>The swap table and one sentence it rewords, for the two tests below.</summary>
        private const string BridgedApp =
            "const TERM_PAIRS=[\n" +
            "  [\"embers doused\",\"server stopped\"],\n" +
            "  [\"Hearth\",\"Dashboard\"],\n" +
            "];\n" +
            "function paint(){ return TT(\"STOPPED and embers doused\"); }\n";

        /// <summary>
        /// The regression an English screenshot cannot show. A key whose lore is a
        /// sentence TT() still carries takes that call site over the moment it lands,
        /// because the bridge asks the catalog first. If the swap WOULD have reworded
        /// that sentence and the entry names no plain register, a host reading plain
        /// wording silently starts getting the Norse one instead.
        /// </summary>
        [Fact]
        public void A_key_that_takes_a_bridged_sentence_and_drops_its_plain_wording_is_refused()
        {
            var folder = Scratch();
            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.state"": { ""lore"": ""STOPPED and embers doused"" } }
}");
            Write(folder, "fake-app.js", BridgedApp);
            Write(folder, "fake.html", "<div>nothing here carries an id</div>\n");

            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));

            Assert.False(said.Ok, "a silently dropped plain register passed:\n" + said);
            Assert.Contains("hall.state", said.Output);
            Assert.Contains("plain register", said.Output);
        }

        /// <summary>
        /// And the same catalog with the plain wording written down passes, so the rule
        /// above is refusing the missing register rather than the sentence.
        /// </summary>
        [Fact]
        public void The_same_key_passes_once_it_names_its_plain_wording()
        {
            var folder = Scratch();
            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.state"": { ""lore"": ""STOPPED and embers doused"",
                               ""plain"": ""STOPPED and server stopped"" } }
}");
            Write(folder, "fake-app.js", BridgedApp);
            Write(folder, "fake.html", "<div>nothing here carries an id</div>\n");

            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));

            Assert.True(said.Ok, "a key that names both registers was refused:\n" + said);
        }

        // ------------------------------- C2. the same regression on the static half

        /// <summary>
        /// The swap table and the selector list, for the static register tests below.
        /// One pair that rewords a word on screen, and a selector list in the two
        /// shapes the real one uses: a bare class, and a descendant pair.
        /// </summary>
        private const string StaticSwapApp =
            "const TERM_PAIRS=[\n" +
            "  [\"Layers\",\"Backups\"],\n" +
            "];\n" +
            "const TERM_STATIC_SEL=\"h1,.microlabel,.pitem .k\";\n";

        /// <summary>
        /// The walker's half of the same regression. applyTerms rewrites the text of
        /// every element under TERM_STATIC_SEL through the swap, so the moment the
        /// walker starts writing that element out of the catalog the entry has to say
        /// what the swap said. An entry that does not is a host with plain wording on
        /// reading a different word after the catalog lands than before it, which no
        /// English screenshot and no gate before this one can show.
        /// </summary>
        [Fact]
        public void A_static_label_the_swap_rewords_and_the_entry_does_not_is_refused()
        {
            var folder = Scratch();
            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""map.layers.label"": { ""lore"": ""Layers"" } }
}");
            Write(folder, "fake-app.js", StaticSwapApp);
            Write(folder, "fake.html", "<div class=\"microlabel\" data-i18n=\"map.layers.label\">Layers</div>\n");

            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));

            Assert.False(said.Ok, "a static label that quietly stopped swapping passed:\n" + said);
            Assert.Contains("map.layers.label", said.Output);
            Assert.Contains("written into an element", said.Output);
            Assert.Contains("Backups", said.Output);
        }

        /// <summary>
        /// The same catalog with the plain wording written down passes, and it has to be
        /// the wording the swap produced rather than any plain-sounding word: a register
        /// that says something else is the same regression in a nicer hat.
        /// </summary>
        [Fact]
        public void The_static_label_passes_when_its_plain_register_is_the_swap_s_own_word()
        {
            var folder = Scratch();
            Write(folder, "fake-app.js", StaticSwapApp);
            Write(folder, "fake.html", "<div class=\"microlabel\" data-i18n=\"map.layers.label\">Layers</div>\n");

            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""map.layers.label"": { ""lore"": ""Layers"", ""plain"": ""Backups"" } }
}");
            var agreed = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));
            Assert.True(agreed.Ok, "a static label that names the swap's own word was refused:\n" + agreed);

            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""map.layers.label"": { ""lore"": ""Layers"", ""plain"": ""Saves"" } }
}");
            var invented = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));
            Assert.False(invented.Ok, "a plain register that disagrees with the swap passed:\n" + invented);
            Assert.Contains("where the swap on the same element says", invented.Output);
        }

        /// <summary>
        /// And the other direction, which is the easier mistake to make while moving
        /// sentences: naming a plain register on a label nothing rewords today. The
        /// walker would start swapping a word that has never swapped, so English moves
        /// on the very screen this phase promised not to move.
        /// </summary>
        [Fact]
        public void A_plain_register_on_a_label_nothing_rewords_is_refused()
        {
            var folder = Scratch();
            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""map.note"": { ""lore"": ""Waypoints"", ""plain"": ""Markers"" } }
}");
            Write(folder, "fake-app.js", StaticSwapApp);
            Write(folder, "fake.html", "<div class=\"subval\" data-i18n=\"map.note\">Waypoints</div>\n");

            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));

            Assert.False(said.Ok, "a plain register nothing asked for passed:\n" + said);
            Assert.Contains("where it never moved", said.Output);
        }

        /// <summary>
        /// The rule answers a selector list, so a selector shape it cannot read has to
        /// be a finding rather than a quiet false. A gate that silently stops covering
        /// half the page is worse than one that was never written.
        /// </summary>
        [Fact]
        public void A_selector_shape_the_rule_cannot_read_is_a_finding_rather_than_a_pass()
        {
            var folder = Scratch();
            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""map.layers.label"": { ""lore"": ""Layers"", ""plain"": ""Backups"" } }
}");
            Write(folder, "fake-app.js",
                "const TERM_PAIRS=[\n  [\"Layers\",\"Backups\"],\n];\n"
                + "const TERM_STATIC_SEL=\".microlabel,.a > .b\";\n");
            Write(folder, "fake.html", "<div class=\"microlabel\" data-i18n=\"map.layers.label\">Layers</div>\n");

            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));

            Assert.False(said.Ok, "an unreadable selector passed:\n" + said);
            Assert.Contains("cannot read", said.Output);
            Assert.Single(Regex.Matches(said.Output, "cannot read"));   // once, not once per element
        }

        /// <summary>
        /// And the rule stands down the day TERM_PAIRS goes. After that the catalog is
        /// the only thing there is and a plain register may say whatever a writer wants,
        /// so a rule still insisting on the old regex table would block the commit that
        /// finishes the job.
        /// </summary>
        [Fact]
        public void The_static_register_rule_stands_down_once_the_swap_table_is_gone()
        {
            var folder = Scratch();
            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""map.layers.label"": { ""lore"": ""Layers"" } }
}");
            Write(folder, "fake-app.js", "const TERM_STATIC_SEL=\"h1,.microlabel\";\n");
            Write(folder, "fake.html", "<div class=\"microlabel\" data-i18n=\"map.layers.label\">Layers</div>\n");

            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));

            Assert.True(said.Ok, "the rule outlived the swap it was written for:\n" + said);
        }

        /// <summary>
        /// The one place the shipped catalog stops the swap on purpose, written down
        /// here so the exemption is a decision rather than a hole. The Map hall's layer
        /// bar was being renamed "Backups" by the Barrow's pair whenever plain wording
        /// was on; the Barrow's own heading still swaps, through an entry that shares
        /// the text so the bridge refuses it.
        /// </summary>
        [Fact]
        public void The_shipped_exemption_is_the_map_layer_bar_and_the_barrow_still_swaps()
        {
            var gate = File.ReadAllText(CheckCatalog);

            var open = gate.IndexOf("SWAP_COLLISIONS = {", StringComparison.Ordinal);
            Assert.True(open > 0, "the gate no longer names its exemptions");
            var block = gate.Substring(open, gate.IndexOf("\n}", open, StringComparison.Ordinal) - open);

            Assert.Contains("\"atlas.layers.label\": \"Backups\",", block);
            // one exemption, so the list cannot quietly become a place to put anything
            // the swap disagrees with.
            Assert.Single(Regex.Matches(block, "\"[a-z][a-z0-9_.]*\": \""));

            var catalog = AppSourceTree.Web("i18n/en.json");
            Assert.Contains("\"atlas.layers.label\": { \"lore\": \"Layers\" }", catalog);
            Assert.Contains("\"barrow.sec.layers\": { \"lore\": \"Layers\", \"plain\": \"Backups\" }", catalog);
            // The Barrow's heading no longer leans on the bridge refusing a word it sees
            // twice: it asks for its own id, and the plain register on that entry is what
            // keeps the swap it always ran. The Map's bar asks for the other id in markup,
            // and the exemption is what stops the swap reaching it there.
            Assert.Contains("${esc(T(\"barrow.sec.layers\"))}", AppSourceTree.Web("app.js"));
            Assert.DoesNotContain("TT(\"Layers\")", AppSourceTree.Web("app.js"));
            Assert.Contains("data-i18n=\"atlas.layers.label\"", AppSourceTree.Web("index.html"));
        }

        // ------------------------- C3. and the same regression on the run-time half

        /// <summary>
        /// A fake app.js with the swap table and a call site that asks for its words by
        /// id, which is what every migrated call site looks like. No TT() anywhere, so
        /// the bridge rule above cannot see this one at all.
        /// </summary>
        private const string KeyedApp =
            "const TERM_PAIRS=[\n" +
            "  [\"embers doused\",\"server stopped\"],\n" +
            "  [\"Hearth\",\"Dashboard\"],\n" +
            "];\n" +
            "const TERM_STATIC_SEL=\"h1,.microlabel\";\n" +
            "function paint(){ return T(\"hall.state\"); }\n";

        private static RepoScript.Result CheckKeyed(string folder)
        {
            Write(folder, "fake-app.js", KeyedApp);
            Write(folder, "fake.html", "<div>nothing here carries an id</div>\n");
            return RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));
        }

        /// <summary>
        /// The migration's own version of the regression, and the one the other two rules
        /// structurally cannot catch. Once a call site says T("id") the English sentence
        /// is gone from app.js, so the bridge rule has nothing to match on and the static
        /// rule is looking at index.html. But TERM_PAIRS is still here, which means that
        /// sentence WAS being reworded for a host reading plain wording right up until the
        /// call site changed. An entry that answers it and names no plain register moves
        /// those words, and only with the switch the other way, where no screenshot is.
        /// </summary>
        [Fact]
        public void A_keyed_call_site_whose_entry_drops_its_plain_wording_is_refused()
        {
            var folder = Scratch();
            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.state"": { ""lore"": ""STOPPED and embers doused"" } }
}");
            var said = CheckKeyed(folder);

            Assert.False(said.Ok, "a keyed call site with a silently dropped register passed:\n" + said);
            Assert.Contains("hall.state", said.Output);
            Assert.Contains("STOPPED and server stopped", said.Output);
        }

        /// <summary>
        /// The same catalog with the swap's own wording written down passes, so the rule
        /// above refuses the missing register rather than the sentence.
        /// </summary>
        [Fact]
        public void The_keyed_call_site_passes_when_its_plain_register_is_the_swap_s_own_word()
        {
            var folder = Scratch();
            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.state"": { ""lore"": ""STOPPED and embers doused"",
                               ""plain"": ""STOPPED and server stopped"" } }
}");
            Assert.True(CheckKeyed(folder).Ok, "an entry that names both registers was refused");

            // And a plain register that is not what the swap said is refused too: the
            // English has to be what ships today, not what reads best to whoever moved it.
            var invented = Scratch();
            Write(invented, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.state"": { ""lore"": ""STOPPED and embers doused"",
                               ""plain"": ""STOPPED, the server is down"" } }
}");
            var said = CheckKeyed(invented);
            Assert.False(said.Ok, "a plain register that disagrees with the swap passed:\n" + said);
            Assert.Contains("names the plain register", said.Output);
        }

        /// <summary>
        /// And the mirror image: a plain register on a sentence the swap never touched
        /// moves English where nothing moves it today, which is the same defect pointing
        /// the other way. Caught here because a migration is exactly when somebody is
        /// tempted to improve the wording on the way past.
        /// </summary>
        [Fact]
        public void A_plain_register_on_a_keyed_sentence_nothing_rewords_is_refused()
        {
            var folder = Scratch();
            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.state"": { ""lore"": ""Update finished."",
                               ""plain"": ""The update finished."" } }
}");
            var said = CheckKeyed(folder);

            Assert.False(said.Ok, "a plain register nothing asked for passed:\n" + said);
            Assert.Contains("nothing rewords that sentence today", said.Output);
        }

        /// <summary>
        /// And this rule stands down with the other two the day TERM_PAIRS goes.
        /// </summary>
        [Fact]
        public void The_keyed_register_rule_stands_down_once_the_swap_table_is_gone()
        {
            var folder = Scratch();
            Write(folder, "en.json", @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.state"": { ""lore"": ""STOPPED and embers doused"",
                               ""plain"": ""STOPPED, the server is down"" } }
}");
            Write(folder, "fake-app.js",
                "const TERM_STATIC_SEL=\"h1\";\nfunction paint(){ return T(\"hall.state\"); }\n");
            Write(folder, "fake.html", "<div>nothing here carries an id</div>\n");

            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));

            Assert.True(said.Ok, "the rule outlived the swap it was written for:\n" + said);
        }

        /// <summary>
        /// The exemption list for the run-time rule is empty, and it has to stay a
        /// decision rather than a drawer. Every id a T() call site asks for today came
        /// out of a TT() call site, so the swap did reach all of them.
        /// </summary>
        [Fact]
        public void The_keyed_register_rule_exempts_nothing_yet()
        {
            var gate = File.ReadAllText(CheckCatalog);

            var open = gate.IndexOf("DYNAMIC_SWAP_EXEMPT = {", StringComparison.Ordinal);
            Assert.True(open > 0, "the gate no longer names its run-time exemptions");
            var block = gate.Substring(open, gate.IndexOf("\n\n", open, StringComparison.Ordinal) - open);
            Assert.DoesNotContain("\": \"", block);
        }

        // --------------------------------------------------- D. the lookup is in the room

        /// <summary>
        /// app.js reads its words out of i18n.js, so i18n.js has to be loaded first, and
        /// with the same cache stamp: a stale catalog served out of the disk cache after
        /// an update is exactly the failure the stamp was added for.
        /// </summary>
        [Fact]
        public void The_lookup_is_included_before_the_app_and_carries_the_cache_stamp()
        {
            var html = AppSourceTree.Web("index.html");

            var lookup = html.IndexOf("BAKA_ASSET(\"i18n.js\")", StringComparison.Ordinal);
            var app = html.IndexOf("BAKA_ASSET(\"app.js\")", StringComparison.Ordinal);

            Assert.True(lookup > 0, "index.html does not include i18n.js");
            Assert.True(app > 0, "index.html does not include app.js");
            Assert.True(lookup < app, "i18n.js is included after app.js, which reads it");
            Assert.Contains("BAKA_ASSET_PLAIN(this)", html);
        }

        /// <summary>
        /// The catalog is fetched through the same stamp, for the same reason, and the
        /// walk over the static half runs once it has arrived.
        /// </summary>
        [Fact]
        public void The_English_catalog_is_fetched_through_the_stamp_and_walked_when_it_lands()
        {
            var js = AppSourceTree.Web("app.js");

            Assert.Contains("window.BAKA_ASSET(\"i18n/en.json\")", js);
            Assert.Contains("window.I18N.load(cat,\"en\")", js);
            Assert.Contains("window.I18N.applyStatic(document)", js);
            Assert.Contains("window.I18N.setRegister(()=>PLAIN)", js);
        }

        /// <summary>
        /// TT() is the bridge: a sentence the catalog knows is answered by the catalog,
        /// and everything else still goes through the old swap. Both arms have to be
        /// there, or half the product silently stops following the register.
        /// </summary>
        [Fact]
        public void The_bridge_asks_the_catalog_first_and_keeps_the_old_swap_underneath()
        {
            var js = AppSourceTree.Web("app.js");
            var bridge = Between(js, "function TT(s){", "\n}\n");

            Assert.Contains("window.I18N.idFor(s)", bridge);
            Assert.Contains("window.I18N.T(id)", bridge);
            Assert.Contains("return plainify(s);", bridge);
            Assert.Contains("L2b deletes the second arm", js);
        }

        /// <summary>
        /// The one letter name belongs to the lookup now. The switch reader that used to
        /// own it was renamed rather than left to shadow it, because a const at the top
        /// level of a classic script wins over anything on window.
        /// </summary>
        [Fact]
        public void The_switch_reader_gave_the_name_T_back_to_the_lookup()
        {
            var js = AppSourceTree.Web("app.js");

            Assert.Contains("const swOn=id=>$(\"#\"+id).classList.contains(\"on\");", js);
            Assert.DoesNotContain("const T=id=>$(\"#\"+id)", js);
            Assert.Contains("const T=(id,params)=>window.I18N?window.I18N.T(id,params)", js);

            // And nothing reads a switch through the old name any more.
            Assert.DoesNotContain("T(\"tCheckUpd\")", js);
            Assert.DoesNotContain("T(\"tRcon\")", js);
        }

        // ------------------- C4. the ids a table holds, and the English beside them

        /// <summary>The smallest table-driven catalog that passes, to break below.</summary>
        private const string TableEnglish = @"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": {
    ""wg.combat.label"": { ""lore"": ""Combat"" },
    ""wg.combat.intro"": { ""lore"": ""How hard the fighting is."" }
  }
}";

        private const string TableApp =
            "const TERM_PAIRS=[\n  [\"Hearth\",\"Server\"],\n];\n" +
            "const WG={combat:{label:\"Combat\",labelId:\"wg.combat.label\",introId:\"wg.combat.intro\"}};\n";

        private RepoScript.Result OverTable(string app, string catalog = TableEnglish)
        {
            var folder = Scratch();
            Write(folder, "en.json", catalog);
            Write(folder, "fake-app.js", app);
            Write(folder, "fake.html", "<div>nothing here carries an id</div>\n");
            return RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder,
                "--app", Path.Combine(folder, "fake-app.js"),
                "--html", Path.Combine(folder, "fake.html"));
        }

        /// <summary>
        /// A table that renders its wording in a loop never spells an id inside a T("...")
        /// call, so the completeness arm could not see one. The world dials are 65 such
        /// sentences. The convention that fixes it is a property whose name ends in Id,
        /// and this is the gate reading it.
        /// </summary>
        [Fact]
        public void An_id_a_table_holds_in_an_Id_property_counts_as_asked_for()
        {
            var said = OverTable(TableApp);
            Assert.True(said.Ok, "a table that names both ids was refused:\n" + said);
        }

        /// <summary>And without the property the same two entries are orphans, which is
        /// what makes the rule above load-bearing rather than decorative.</summary>
        [Fact]
        public void A_table_that_names_no_ids_leaves_its_entries_orphaned()
        {
            var said = OverTable("const TERM_PAIRS=[\n  [\"Hearth\",\"Server\"],\n];\n"
                                 + "const WG={combat:{label:\"Combat\"}};\n");
            Assert.False(said.Ok, "a table holding only English passed:\n" + said);
            Assert.Contains("orphan key: nothing asks for wg.combat.intro", said.Output);
        }

        /// <summary>A typo in a table id is caught the way a bad T() call is, rather than
        /// rendered on screen as a dotted name.</summary>
        [Fact]
        public void A_typo_in_a_table_id_is_refused()
        {
            var said = OverTable(TableApp.Replace("wg.combat.labl", "x")
                                         .Replace("labelId:\"wg.combat.label\"", "labelId:\"wg.combat.labl\""));
            Assert.False(said.Ok, "a table id with no entry behind it passed:\n" + said);
            Assert.Contains("asks for an id the English catalog does not have: wg.combat.labl", said.Output);
        }

        /// <summary>
        /// Where a table keeps its English beside the id, because something else still
        /// reads that English, the two are one sentence written twice and they drift. The
        /// gate holds them together: edit either half alone and it says so.
        /// </summary>
        [Fact]
        public void The_English_a_table_keeps_beside_an_id_has_to_be_the_catalogs()
        {
            var said = OverTable(TableApp.Replace("label:\"Combat\"", "label:\"Fighting\""));
            Assert.False(said.Ok, "a table whose English had drifted passed:\n" + said);
            Assert.Contains("label beside wg.combat.label says 'Fighting' where the catalog says 'Combat'",
                            said.Output);
        }

        /// <summary>
        /// And the rule only ever reads a value that is SHAPED like an id, which is what
        /// keeps it away from the buildId and PlayerId properties the page already has.
        /// Without that, every build number in the preview data would be an id the
        /// catalog was missing.
        /// </summary>
        [Fact]
        public void An_Id_property_that_is_not_a_catalog_id_is_left_alone()
        {
            var said = OverTable(TableApp
                + "const BUILD={buildId:\"19503481\",targetBuildId:\"19640213\",PlayerId:\"7656119801\"};\n");
            Assert.True(said.Ok, "a build number was read as a catalog id:\n" + said);
        }

        /// <summary>The shipped page really does use the convention, so the rules above
        /// are guarding something rather than describing a fixture.</summary>
        [Fact]
        public void The_shipped_world_dials_hold_their_ids_that_way()
        {
            var js = AppSourceTree.Web("app.js");
            Assert.Contains("labelId:\"world.wg.combat.label\"", js);
            Assert.Contains("explainId:\"world.wg.portals.veryhard.explain\"", js);
            Assert.Contains("label:\"Combat\",labelId:\"world.wg.combat.label\"", js);
        }

        private static string Between(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from >= 0, "could not find " + start);
            var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.True(to > from, "could not find " + end + " after " + start);
            return text.Substring(from, to - from);
        }
    }
}
