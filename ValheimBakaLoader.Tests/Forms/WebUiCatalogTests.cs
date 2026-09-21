using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
            // Both registers on the fixture, so this is testing the dash rule and only
            // that: Hearth is in the Norse register and a sentence that speaks in it
            // owes a plain wording, which is a different rule with its own tests.
            Refuses("long dash in English", "{\n" +
                "  \"_meta\": { \"language\": \"en\" },\n" +
                "  \"keys\": { \"hall.note\": { \"lore\": \"Hearth — status\", \"plain\": \"Server — status\" } }\n" +
                "}", "U+2014, which en does not allow");

            // Russian needs it: it stands in for the omitted copula, which is how the
            // language writes "X is Y". The rule is a function of the language.
            var folder = Scratch();
            Write(folder, "en.json", "{\n  \"_meta\": { \"language\": \"en\" },\n  \"keys\": { \"hall.note\": { \"lore\": \"Hearth status\", \"plain\": \"Server status\" } }\n}");
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
        /// The orphan rule is strict, and it is the bridge going that made it so. While
        /// TT() was up, a key was also "used" when its English sentence was still spelled
        /// out at a call site, because that is how a migrated call site and one that had
        /// not moved yet stayed in step. There is no such call site left, so being asked
        /// for by id is the only way a key earns its place.
        /// </summary>
        [Fact]
        public void A_sentence_spelled_out_in_the_source_no_longer_excuses_an_orphan()
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

            Assert.False(said.Ok, "an orphan was excused by a sentence in the source:\n" + said);
            Assert.Contains("orphan key: nothing asks for hall.title", said.Output);
        }

        // -------------------------- C2. the Norse register, which is what is left to guard

        /// <summary>
        /// The register list the rule reads, small enough to write out here. A fixture of
        /// its own rather than the shipped one, so these prove the RULE and the test below
        /// proves the LIST.
        /// </summary>
        private const string NorseTerms = @"{
  ""terms"": [""hearth"", ""saga"", ""realm"", ""realms""],
  ""caption_suffix"": ""norse"",
  ""caption_prefix"": ""common.norse."",
  ""exempt"": { ""hall.quotes"": ""It names the word rather than speaking in it."" }
}";

        /// <summary>
        /// The catalog in one folder and the register list in another, because the gate
        /// reads every .json in the folder it is pointed at AS A CATALOG. A list dropped
        /// in beside the fixture comes back as three findings about a file that was never
        /// a catalog, which is a confusing way to fail a test about something else.
        /// </summary>
        private RepoScript.Result CheckRegister(string catalog)
        {
            var folder = Scratch();
            var beside = Scratch();
            Write(folder, "en.json", catalog);
            Write(beside, "norse.json", NorseTerms);
            return RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder, "--norse", Path.Combine(beside, "norse.json"));
        }

        /// <summary>
        /// The regression no English screenshot can show, and the one rule left watching
        /// for it. A sentence that speaks in the Norse register and names only one wording
        /// is read AS IT STANDS by a host who turned the Norse names off, so that host gets
        /// one hall saying world and the next saying realm.
        /// <para>
        /// Three rules used to watch this, one per road a sentence could take into the old
        /// swap table: through TT(), through a T() call site, and through the walker. The
        /// table is gone and all three roads lead to the same entry now, so the question is
        /// asked of the entry directly.
        /// </para>
        /// </summary>
        [Fact]
        public void A_sentence_in_the_norse_register_with_no_plain_wording_is_refused()
        {
            var said = CheckRegister(@"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.state"": { ""lore"": ""The hearth is cold"" } }
}");

            Assert.False(said.Ok, "a sentence with one register passed:\n" + said);
            Assert.Contains("hall.state speaks in the Norse register (hearth)", said.Output);
            Assert.Contains("names no plain wording", said.Output);
        }

        /// <summary>
        /// And the same catalog with the plain wording beside it passes, so the rule is
        /// refusing the missing register rather than the word.
        /// </summary>
        [Fact]
        public void The_same_sentence_passes_once_it_names_its_plain_wording()
        {
            var said = CheckRegister(@"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.state"": { ""lore"": ""The hearth is cold"",
                               ""plain"": ""The server is stopped"" } }
}");

            Assert.True(said.Ok, "a sentence that names both registers was refused:\n" + said);
        }

        /// <summary>
        /// The word matches whole and ignores case, which is half of what the old table
        /// got wrong: it held the realm pair in lower case only, so a sentence opening
        /// on "Realm" was never reworded and nine of them shipped that way. The other
        /// half was the plural: the table put a word boundary after realm, so realms
        /// never matched either. Whole-word matching means a plural is its own entry in
        /// the register, which is why the list below names both.
        /// </summary>
        [Theory]
        [InlineData("Realm archived")]
        [InlineData("Archived realms")]
        [InlineData("one realm only")]
        public void The_register_is_matched_whole_and_without_case(string lore)
        {
            var said = CheckRegister(@"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.state"": { ""lore"": """ + lore + @""" } }
}");

            Assert.False(said.Ok, "the register missed " + lore + ":\n" + said);
            Assert.Contains("hall.state speaks in the Norse register", said.Output);
        }

        /// <summary>
        /// A Norse CAPTION is the Norse word: the small line beside a plain label in the
        /// sidebar and the card headers. CSS hides every one of them when the switch is
        /// off, so a plain wording there is a sentence nobody can ever read. Exempt by the
        /// shape of the id, because a list of them would go stale the day one is added.
        /// </summary>
        [Fact]
        public void A_norse_caption_needs_no_plain_wording_because_the_page_hides_it()
        {
            var said = CheckRegister(@"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": {
    ""common.norse.hearth"": { ""lore"": ""Hearth"" },
    ""skald.uptime.norse"": { ""lore"": ""Hearth burned"" }
  }
}");

            Assert.True(said.Ok, "a Norse caption was asked for a wording nobody reads:\n" + said);
        }

        /// <summary>
        /// A sentence that QUOTES a Norse word rather than speaking in it is exempt by
        /// name, with the reason written beside it in the list.
        /// </summary>
        [Fact]
        public void A_named_exemption_passes_and_an_unnamed_one_does_not()
        {
            var exempt = CheckRegister(@"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.quotes"": { ""lore"": ""Keeps Hearth beside the plain name."" } }
}");
            Assert.True(exempt.Ok, "a named exemption was refused:\n" + exempt);

            var other = CheckRegister(@"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.other"": { ""lore"": ""Keeps Hearth beside the plain name."" } }
}");
            Assert.False(other.Ok, "an unnamed sentence was let through:\n" + other);
        }

        /// <summary>
        /// And the exemption cannot rot. An id that is exempted but no longer says
        /// anything in the register is reported, so the list stays a set of live decisions
        /// rather than a drawer nobody empties.
        /// </summary>
        [Fact]
        public void An_exemption_the_rule_no_longer_flags_is_reported()
        {
            var said = CheckRegister(@"{
  ""_meta"": { ""language"": ""en"", ""appVersion"": ""1.2.0"", ""catalog"": 1 },
  ""keys"": { ""hall.quotes"": { ""lore"": ""Nothing in here is a Norse word."" } }
}");

            Assert.False(said.Ok, "a stale exemption passed unnoticed:\n" + said);
            Assert.Contains("drop the exemption", said.Output);
        }

        /// <summary>
        /// The list itself: short, and every live exemption carries its reason. This is
        /// the test that reads the SHIPPED file rather than a fixture.
        /// </summary>
        [Fact]
        public void The_shipped_register_list_is_short_and_says_why_it_exempts_what_it_does()
        {
            var path = Path.Combine(AppSourceTree.RepoRoot(), "scripts", "i18n", "norse_terms.json");
            using var file = JsonDocument.Parse(File.ReadAllText(path));
            var root = file.RootElement;

            var terms = root.GetProperty("terms").EnumerateArray().Select(t => t.GetString()).ToList();
            Assert.InRange(terms.Count, 10, 60);       // a register, not a swap table
            foreach (var word in new[] { "hearth", "saga", "barrow", "waystone", "skald", "realm" })
                Assert.Contains(word, terms);
            // Whole-word matching, so a plural that is read on screen is its own entry.
            foreach (var word in new[] { "realms", "vikings" })
                Assert.Contains(word, terms);
            // The verb the old table mistook for a noun, and the reason it is not here.
            Assert.DoesNotContain("forge", terms);

            var exempt = root.GetProperty("exempt").EnumerateObject()
                .Where(p => !p.Name.StartsWith("_", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, exempt.Count);
            foreach (var entry in exempt)
                Assert.True(entry.Value.GetString()?.Length > 80,
                    entry.Name + " is exempted without a reason worth reading");

            // The one decision that is recorded rather than enforced: the Map hall's
            // layer bar. The old table rewrote it to Backups whenever the Norse names
            // were off, naming a bar on one hall after a feature on another. Layer is
            // not a Norse word and is not in the register, so nothing flags it; it is
            // written down here and there so the reasoning cannot be lost.
            var decided = root.GetProperty("_decisions");
            Assert.True(decided.TryGetProperty("atlas.layers.label", out var why),
                "the Map hall's layer bar lost the note that explains it");
            Assert.Contains("map layers", why.GetString());

            var catalog = File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(),
                "ValheimBakaLoader", "WebUI", "i18n", "en.json"));
            Assert.Contains("\"atlas.layers.label\": { \"lore\": \"Layers\" }", catalog);
            Assert.Contains("\"barrow.sec.layers\": { \"lore\": \"Layers\", \"plain\": \"Backups\" }", catalog);
        }

        // ------------------------- C3. the swap table itself, and everything that fed it

        /// <summary>
        /// The bridge and the table under it are gone, and nothing may put either back.
        /// TT() asked the catalog first and ran 129 ordered regular expressions over
        /// already-rendered English when the catalog could not answer; that second arm is
        /// a mechanism no second language survives, because a JavaScript word boundary is
        /// defined against ASCII and the table was a bare substring replace in Cyrillic
        /// and in Han.
        /// </summary>
        [Fact]
        public void The_bridge_the_swap_table_and_the_caches_are_all_gone()
        {
            var js = AppSourceTree.Web("app.js");

            foreach (var gone in new[]
            {
                "function TT(", "function plainify(", "const TERM_PAIRS=", "const TERM_RES=",
                "const TERM_STATIC_SEL", "const TERM_ORIG", "const TERM_TITLE_ORIG",
            })
                Assert.False(js.Contains(gone, StringComparison.Ordinal), gone + " is still here");

            // And the reverse map the bridge needed goes with it: English text back to an
            // id was only ever there so a half-migrated call site could agree with a
            // migrated one, and a second way to name a sentence is a second thing to keep
            // in step.
            var lookup = AppSourceTree.Web("i18n.js");
            Assert.DoesNotContain("function idFor(", lookup);
            Assert.DoesNotContain("buildReverse", lookup);
        }

        /// <summary>
        /// One re-render path, and no reload anywhere in it. A reload would take the Saga
        /// scrollback, both search boxes and whatever is unsaved in the config editor, and
        /// that last one is the reason this was built rather than bought.
        /// </summary>
        [Fact]
        public void The_language_switch_is_one_path_that_redraws_in_place()
        {
            var js = AppSourceTree.Web("app.js");

            Assert.Contains("function applyLanguage(code){", js);
            Assert.Contains("function applyTerms(){return applyLanguage();}", js);
            // the attributes, the static walk, the painters, and the two surfaces a walk
            // cannot reach
            Assert.Contains("const tag=setLanguageAttributes(code", js);
            Assert.Contains("window.I18N.applyStatic(document);}catch(_){}", js);
            Assert.Contains("try{repaintBootCopy();}catch(_){}", js);
            Assert.Contains("try{redrawOpenModal();}catch(_){}", js);
            Assert.Contains("try{redrawContextMenu();}catch(_){}", js);

            // and nothing anywhere reaches for a reload
            foreach (var gone in new[] { "location.reload", "location.href=", "window.location=" })
                Assert.False(js.Contains(gone, StringComparison.Ordinal),
                    "the language switch grew a reload: " + gone);
        }

        /// <summary>
        /// The seam the preview and the layout probe drive, and the pseudo-locale catalog
        /// they drive it with. A switch that quietly misses a surface cannot be seen in
        /// English, because English after the switch looks exactly like English before it.
        /// </summary>
        [Fact]
        public void The_preview_can_load_a_catalog_and_the_pseudo_locale_is_beside_the_page()
        {
            var js = AppSourceTree.Web("app.js");

            Assert.Contains("setLanguage:(code,catalog)=>{", js);
            Assert.Contains("if(catalog&&window.I18N) window.I18N.load(catalog,code);", js);
            Assert.Contains("return applyLanguage(code);", js);
            Assert.Contains("loadLanguage:async code=>{", js);

            var folder = Path.Combine(AppSourceTree.RepoRoot(), "ValheimBakaLoader", "WebUI", "i18n");
            Assert.True(File.Exists(Path.Combine(folder, "xx.json")),
                "the pseudo locale is not beside the page");

            using var made = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "xx.json")));
            using var english = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "en.json")));

            Assert.Equal("xx", made.RootElement.GetProperty("_meta").GetProperty("language").GetString());

            var theirs = made.RootElement.GetProperty("keys");
            var ours = english.RootElement.GetProperty("keys");
            Assert.Equal(ours.EnumerateObject().Count(), theirs.EnumerateObject().Count());

            foreach (var id in new[] { "common.button.cancel", "side.nav.hearth.label" })
            {
                var value = theirs.GetProperty(id).GetProperty("translation").GetString();
                var source = ours.GetProperty(id).GetProperty("lore").GetString();
                Assert.StartsWith("[\u1E8A", value);
                Assert.Contains(source, value);            // the English survives inside it
                Assert.True(value.Length > source.Length * 1.3,
                    id + " was not padded, so an overflow would not show under xx");
            }
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

        // ---------------- C5. the revision number, against the English that was published
        //
        // Tools/LanguagePackService.TryUnchangedCatalog keeps a pack this machine already
        // holds whenever the manifest names the catalog number that pack carries. So a
        // release that changes English and leaves _meta.catalog alone is a release every
        // host with a pack quietly refuses, and they read the new sentences in English for
        // ever. 1.2.1 walked into exactly that: thirty new ids, two reworded, and the 1 that
        // 1.2.0 shipped still in _meta. Nothing could say so, because nothing in the tree
        // remembered 1.2.0's English. scripts/i18n/released_catalogs.json remembers it now,
        // as a fingerprint, and these are the two directions of the rule that reads it.

        private static string Releases => RepoScript.At("scripts", "i18n", "released_catalogs.json");

        /// <summary>The shipped catalog, in a folder of its own, against the real release list.</summary>
        private RepoScript.Result OverReleases(string catalog)
        {
            var folder = Scratch();
            Write(folder, "en.json", catalog);
            return RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder, "--releases", Releases);
        }

        private static string ShippedEnglish() => File.ReadAllText(Path.Combine(
            AppSourceTree.RepoRoot(), "ValheimBakaLoader", "WebUI", "i18n", "en.json"));

        /// <summary>The last release the list remembers: its version and the revision it shipped.</summary>
        private static (string Version, int Catalog) LastRelease()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Releases));
            var last = doc.RootElement.GetProperty("releases").EnumerateArray().Last();
            return (last.GetProperty("version").GetString(), last.GetProperty("catalog").GetInt32());
        }

        /// <summary>
        /// English that has moved since the last release, under the revision that release
        /// shipped, has to be refused. Driven over a scratch copy of the real shipped catalog
        /// with one sentence reworded and the revision put back to the last release's, which
        /// is the state a tree is really in when somebody edits a sentence and forgets the
        /// number. Read from the list rather than written down, so it keeps meaning the same
        /// thing after every release.
        /// </summary>
        [Fact]
        public void English_that_moved_since_the_last_release_needs_a_higher_revision()
        {
            var (version, catalog) = LastRelease();
            var english = ShippedEnglish();

            var at = english.IndexOf("\"lore\": \"", StringComparison.Ordinal);
            Assert.True(at > 0, "the shipped catalog has no lore sentence to reword");
            english = english.Insert(at + "\"lore\": \"".Length, "Reworded. ");
            english = System.Text.RegularExpressions.Regex.Replace(
                english, "\"catalog\":\\s*\\d+", "\"catalog\": " + catalog, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));

            var said = OverReleases(english);

            Assert.False(said.Ok, "the gate was happy with the revision that leaves every host behind:\n" + said);
            Assert.Contains("the English changed since " + version, said.Output);
            Assert.Contains("_meta.catalog is " + catalog, said.Output);
            Assert.Contains("has to go up, to " + (catalog + 1) + " or beyond", said.Output);
        }

        /// <summary>And the same catalog with the revision moved, which is what ships.</summary>
        [Fact]
        public void The_same_English_with_the_revision_moved_is_accepted()
        {
            var said = OverReleases(ShippedEnglish());

            Assert.True(said.Ok, "the revision that reaches every host was refused:\n" + said);
            Assert.Contains("TOTAL 0", said.Output);
        }

        /// <summary>
        /// A gate whose own record has gone missing says so rather than passing. This one
        /// stands between a reworded sentence and a host who never sees it, and it has no
        /// second opinion to fall back on.
        /// </summary>
        [Fact]
        public void A_missing_release_list_is_a_finding_rather_than_a_quiet_pass()
        {
            var folder = Scratch();
            Write(folder, "en.json", ShippedEnglish());
            var said = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder, "--releases", Path.Combine(folder, "no-such-file.json"));

            Assert.False(said.Ok, "the rule stood down when its own record was not there:\n" + said);
            Assert.Contains("the released catalogs list is missing", said.Output);
        }

        /// <summary>
        /// The record itself: the last release that is OUT is written down with the
        /// revision it shipped, and the version being built is NOT. Recording a version is release day's job, after its English is
        /// final; doing it from here would mean the gate was comparing this build against
        /// itself and could never say anything again.
        /// </summary>
        [Fact]
        public void The_release_list_holds_the_release_that_is_out_and_not_the_one_being_built()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Releases));
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("_about", out var about),
                "the release list no longer carries the recipe that makes it readable alone");
            var recipe = string.Join(" ", about.EnumerateArray().Select(line => line.GetString()));
            Assert.Contains("sha256", recipe);
            Assert.Contains("id, a tab, the lore field, a tab", recipe);

            var releases = root.GetProperty("releases").EnumerateArray().ToList();
            Assert.NotEmpty(releases);

            var last = releases[releases.Count - 1];
            Assert.Equal(64, last.GetProperty("fingerprint").GetString().Length);

            // The version this tree is building is read off the project, so the rule keeps
            // meaning "recorded after it is out" without naming a release here.
            var building = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(), "ValheimBakaLoader", "ValheimBakaLoader.csproj")),
                "<Version>([0-9.]+)</Version>").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(building), "the project no longer names its version");

            Assert.DoesNotContain(releases, r => r.GetProperty("version").GetString() == building);
            Assert.True(Version.Parse(last.GetProperty("version").GetString()) < Version.Parse(building),
                "the last recorded release is not older than the one being built");
        }

        /// <summary>
        /// Release day's own mode, both ways. Recording the same English under the same
        /// version twice changes nothing and is not a failure; recording DIFFERENT English
        /// under a version that has already gone out is refused, because the hosts holding
        /// that pack are the fact this file is about and a rewritten record would tell the
        /// gate a lie about them.
        /// </summary>
        [Fact]
        public void Recording_a_release_is_repeatable_and_refuses_to_rewrite_one()
        {
            var folder = Scratch();
            Write(folder, "en.json", ShippedEnglish());
            var book = Path.Combine(folder, "book.json");
            Write(folder, "book.json", "{ \"_about\": [], \"releases\": [] }");

            var first = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder, "--releases", book, "--record", "9.9.9");
            Assert.True(first.Ok, "the first recording did not go through:\n" + first);
            Assert.Contains("recorded 9.9.9", first.Output);

            var again = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder, "--releases", book, "--record", "9.9.9");
            Assert.True(again.Ok, "recording the same English twice was treated as a failure:\n" + again);
            Assert.Contains("already recorded, unchanged", again.Output);

            // One sentence reworded, and the same version asked for again.
            Write(folder, "en.json",
                ShippedEnglish().Replace("\"lore\": \"Passive enemies\"", "\"lore\": \"Peaceful enemies\""));
            var rewrite = RepoScript.Run(RepoScript.Python(), CheckCatalog,
                "--dir", folder, "--releases", book, "--record", "9.9.9");
            Assert.False(rewrite.Ok, "a release that has gone out was quietly rewritten:\n" + rewrite);
            Assert.Contains("already recorded with different English", rewrite.Output);

            // And nothing was written: the record still says what it said.
            using var doc = JsonDocument.Parse(File.ReadAllText(book));
            var releases = doc.RootElement.GetProperty("releases").EnumerateArray().ToList();
            Assert.Single(releases);
            Assert.Equal("9.9.9", releases[0].GetProperty("version").GetString());
        }
    }
}
