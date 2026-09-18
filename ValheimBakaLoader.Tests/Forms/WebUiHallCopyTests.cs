using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The Players, Mods, Configs and Settings halls, and the Map, Log, Discord and
    /// Statistics halls after them, now that their static words name catalog ids.
    /// <para>
    /// WebUiStaticCopyTests already proves, for the whole page at once, that every id
    /// it names exists, that the English left in the page and the English in the
    /// catalog are the same sentence, and that nothing the walker fills is also
    /// written by app.js. This file is the INVENTORY those general gates cannot be:
    /// it writes down which words on these four halls are keyed and which are
    /// deliberately not, so that dropping an id, or quietly keying a label whose
    /// words app.js owns at run time, fails here instead of shipping.
    /// </para>
    /// </summary>
    public class WebUiHallCopyTests
    {
        private static string Html() => AppSourceTree.Web("index.html");

        private static string AppJs() => AppSourceTree.Web("app.js");

        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var keys = document.RootElement.GetProperty("keys");
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in keys.EnumerateObject()) map[entry.Name] = entry.Value.Clone();
            return map;
        }

        private static string Lore(Dictionary<string, JsonElement> catalog, string id) =>
            catalog.TryGetValue(id, out var entry) && entry.TryGetProperty("lore", out var lore)
             && lore.ValueKind == JsonValueKind.String
                ? lore.GetString()
                : null;

        /// <summary>One CLDR category of an entry's lore, or null when it is not a plural.</summary>
        private static string Plural(Dictionary<string, JsonElement> catalog, string id, string category) =>
            catalog.TryGetValue(id, out var entry) && entry.TryGetProperty("lore", out var lore)
             && lore.ValueKind == JsonValueKind.Object
             && lore.TryGetProperty(category, out var picked) && picked.ValueKind == JsonValueKind.String
                ? picked.GetString()
                : null;

        /// <summary>The parameter a plural entry chooses its category from.</summary>
        private static string PluralParam(Dictionary<string, JsonElement> catalog, string id) =>
            catalog.TryGetValue(id, out var entry) && entry.TryGetProperty("plural", out var name)
             && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;

        private static readonly Regex AnyId = new(
            @"data-i18n(?:-title|-placeholder|-aria)?=""([^""]+)""", RegexOptions.Compiled);

        /// <summary>The markup of one hall, start marker to the comment that opens the next.
        /// The marker stops at the id rather than the end of the tag, because the four halls
        /// that start hidden carry a style attribute after it.</summary>
        private static string Hall(string page, string until)
        {
            var html = Html();
            var from = html.IndexOf("<section class=\"page\" id=\"page-" + page + "\"", StringComparison.Ordinal);
            Assert.True(from > 0, "index.html no longer has the " + page + " hall");
            var to = html.IndexOf(until, from, StringComparison.Ordinal);
            Assert.True(to > from, "the " + page + " hall no longer ends at " + until);
            return html.Substring(from, to - from);
        }

        /// <summary>Every id the hall names, first mention first.</summary>
        private static List<string> IdsIn(string markup)
        {
            var seen = new List<string>();
            foreach (Match match in AnyId.Matches(markup))
                if (!seen.Contains(match.Groups[1].Value)) seen.Add(match.Groups[1].Value);
            return seen;
        }

        private static void TheHallNames(string markup, string[] expected)
        {
            var catalog = Catalog();
            var found = IdsIn(markup);

            var gone = expected.Where(id => !found.Contains(id)).ToList();
            var extra = found.Where(id => !expected.Contains(id)).ToList();

            Assert.True(gone.Count == 0, "the hall stopped naming: " + string.Join(", ", gone));
            Assert.True(extra.Count == 0,
                "the hall names ids this inventory does not list, which is either a new sentence "
                + "that belongs in it or a label whose words app.js owns: " + string.Join(", ", extra));

            var hollow = expected.Where(id => Lore(catalog, id) == null).ToList();
            Assert.True(hollow.Count == 0, "the catalog has no English for: " + string.Join(", ", hollow));
        }

        // ------------------------------------------------------------------ A. Players

        [Fact]
        public void The_players_hall_reads_its_words_out_of_the_catalog()
        {
            TheHallNames(Hall("vikings", "<!-- ============ PAGE: MODS"), new[]
            {
                "vikings.head.title", "common.norse.vikings", "vikings.caps.head",
                "vikings.caps.note", "vikings.col.name", "common.sort.by_name",
                "vikings.col.status", "vikings.col.status.title", "vikings.col.platform",
                "vikings.col.platform.title", "vikings.col.session",
                "vikings.col.session.title", "vikings.col.playtime",
                "vikings.col.playtime.title", "vikings.col.seen", "vikings.col.seen.title",
                "vikings.col.deaths", "vikings.col.deaths.title", "vikings.col.pos",
                "vikings.col.pos.title",
            });
        }

        /// <summary>
        /// Every sortable column says what it is AND what clicking it does, because a
        /// header that is a verb in English is often a noun in another language and the
        /// tooltip is the only place the sort is named. The table is the one surface on
        /// these four halls the old terminology selector already reached (#page-vikings
        /// th is in TERM_STATIC_SEL), so both halves had to move together.
        /// </summary>
        [Fact]
        public void Every_sortable_player_column_keys_its_word_and_its_tooltip()
        {
            var hall = Hall("vikings", "<!-- ============ PAGE: MODS");
            foreach (Match header in Regex.Matches(hall, @"<th[^>]*\bdata-sort=""([a-z]+)""[^>]*>"))
            {
                Assert.True(header.Value.Contains("data-i18n=\""),
                    "the " + header.Groups[1].Value + " column has no id for its word");
                Assert.True(header.Value.Contains("data-i18n-title=\""),
                    "the " + header.Groups[1].Value + " column has no id for its tooltip");
            }
            // The selector list that swapped these headers underneath the walker is
            // gone. The walker is the only thing that writes them now, and the register
            // it writes is picked inside the lookup out of the entry's own two halves.
            Assert.DoesNotContain("const TERM_STATIC_SEL", AppJs());
        }

        // --------------------------------------------------------------------- B. Mods

        [Fact]
        public void The_mods_hall_reads_its_words_out_of_the_catalog()
        {
            TheHallNames(Hall("mods", "<!-- ============ PAGE: RUNES"), new[]
            {
                "mods.head.title", "mods.search.aria",
                "mods.col.name", "common.sort.by_name", "mods.col.installed",
                "mods.col.installed.title", "mods.col.latest", "mods.col.latest.title",
                "mods.col.status", "mods.col.status.title", "mods.col.possibly_outdated",
                "mods.search.placeholder",
            });
        }

        /// <summary>
        /// Four labels on the Mods hall are keyless because app.js writes them: the
        /// Update all button carries a count, the Possibly outdated tooltip is one of two
        /// sentences chosen by whether the game's update date could be read, and Add and
        /// Scan each have a wording for either side of the second-site switch.
        /// Keying any of them would put the walker and the render in a fight the render
        /// wins on the next paint, leaving the id's English on screen for one frame.
        /// </summary>
        [Fact]
        public void The_counted_mods_labels_stay_with_the_render_that_owns_them()
        {
            var hall = Hall("mods", "<!-- ============ PAGE: RUNES");
            var js = AppJs();

            var updAll = hall.Substring(hall.IndexOf("id=\"updAllBtn\"", StringComparison.Ordinal) - 40, 120);
            Assert.DoesNotContain("data-i18n", updAll);
            Assert.Contains("$(\"#updAllBtn\").innerHTML=", js);

            var column = hall.Substring(hall.IndexOf("id=\"thPossiblyOutdated\"", StringComparison.Ordinal) - 30);
            column = column.Substring(0, column.IndexOf("</th>", StringComparison.Ordinal));
            Assert.Contains("data-i18n=\"mods.col.possibly_outdated\"", column);
            Assert.DoesNotContain("data-i18n-title", column);
            Assert.Contains("th.title=(scanned&&mods.length&&!anyGameDate)", js);
            Assert.Contains("?T(\"mods.col.possibly_outdated.tip.unknown\"):T(\"mods.col.possibly_outdated.tip\");", js);

            // The two header buttons say which sites a scan reads, so their words follow
            // the Upkeep switch and renderModSourceLabels owns them.
            foreach (var id in new[] { "addModBtn", "scanBtn" })
            {
                var button = hall.Substring(hall.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal) - 40, 140);
                Assert.DoesNotContain("data-i18n", button);
            }
            Assert.Contains("add.textContent=both?T(\"mods.add.label.link\"):T(\"mods.add.label\");", js);
            Assert.Contains("scan.textContent=both?T(\"mods.scan.label.sites\"):T(\"mods.scan.label\");", js);
        }

        // ------------------------------------------------------------------ C. Configs

        [Fact]
        public void The_configs_hall_reads_its_words_out_of_the_catalog()
        {
            TheHallNames(Hall("runes", "<!-- ============ PAGE: WORLD"), new[]
            {
                "runes.head.title", "common.norse.runes", "runes.open.label",
                "runes.reload.label", "runes.list.label", "common.norse.scrolls",
                "runes.search.aria", "runes.find.aria", "runes.find.next",
                "runes.search.placeholder", "runes.find.placeholder", "runes.editor.placeholder",
                "runes.find.next.title",
            });
        }

        /// <summary>
        /// The Save button on the Configs hall stays keyless, and this says why rather
        /// than leaving it looking forgotten: resetCfgSaveBtn() writes its words back
        /// every time the confirm state falls away, so an id in the page AND a render in
        /// app.js would be two owners of one button and the loser would be whichever ran
        /// second. The literal moved into the catalog with the render instead, in one
        /// change rather than two, and repaintBootCopy runs that render the moment the
        /// words land, so the button reads out of the catalog from the first frame.
        /// </summary>
        [Fact]
        public void The_config_save_button_reads_its_words_through_the_render_that_rewrites_it()
        {
            var hall = Hall("runes", "<!-- ============ PAGE: WORLD");
            var button = hall.Substring(hall.IndexOf("id=\"cfgSaveBtn\"", StringComparison.Ordinal) - 40, 140);
            var js = AppJs();

            Assert.DoesNotContain("data-i18n", button);
            Assert.Contains("function resetCfgSaveBtn(){", js);
            Assert.Contains("b.textContent=T(\"runes.save.label\");", js);
            Assert.Contains("b.textContent=T(\"runes.save.confirm.label\");", js);
            // Nothing spells the button's words out any more, on either side of the confirm.
            Assert.DoesNotContain("innerHTML=\"ᛉ&nbsp; Save\"", js);
            Assert.DoesNotContain("innerHTML=\"ᛉ&nbsp; Confirm save?\"", js);
            // And the render that owns them runs when the catalog arrives, not only when a
            // scroll is opened, so a host who never opens one still reads the catalog's word.
            Assert.Contains("try{resetCfgSaveBtn();}catch(_){}", js);
        }

        /// <summary>
        /// The three search boxes key BOTH halves now: the sentence a screen reader
        /// announces and the one the box shows. The placeholder was held back while
        /// applyTerms cached and restored those five, because the cache could run before
        /// the catalog fetch resolved and would have put the English seed back over a
        /// pack's word on the first terminology toggle. The cache went with the swap it
        /// served, so both halves move here, in one change, as the note that held them
        /// back said they would have to.
        /// </summary>
        [Fact]
        public void The_search_boxes_key_the_spoken_label_and_the_placeholder_too()
        {
            var html = Html();
            var js = AppJs();

            foreach (var (id, aria, placeholder) in new[]
                     {
                         ("modSearch", "mods.search.aria", "mods.search.placeholder"),
                         ("runeSearch", "runes.search.aria", "runes.search.placeholder"),
                         ("cfgFind", "runes.find.aria", "runes.find.placeholder"),
                     })
            {
                var open = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
                Assert.True(open > 0, "index.html no longer has #" + id);
                var tag = html.Substring(html.LastIndexOf('<', open), html.IndexOf('>', open) - html.LastIndexOf('<', open) + 1);
                Assert.Contains("data-i18n-aria=\"" + aria + "\"", tag);
                Assert.Contains("data-i18n-placeholder=\"" + placeholder + "\"", tag);
            }

            // And the config editor's own placeholder, which is the fifth of the five and
            // the one whose two registers really differ.
            var editor = html.Substring(html.IndexOf("id=\"cfgEditor\"", StringComparison.Ordinal) - 40, 220);
            Assert.Contains("data-i18n-placeholder=\"runes.editor.placeholder\"", editor);

            // The cache itself is gone, so there is no second owner left to fight.
            Assert.DoesNotContain("$(\"#palInput\"),$(\"#cfgEditor\")", js);
            Assert.DoesNotContain("const TERM_ORIG", js);
        }

        // ----------------------------------------------------------------- D. Settings

        [Fact]
        public void The_settings_hall_reads_its_words_out_of_the_catalog()
        {
            TheHallNames(Hall("world", "<!-- ============ PAGE: ATLAS"), new[]
            {
                "world.head.title", "common.norse.world", "world.head.sub", "world.save.label",
                "world.server.label", "common.norse.heimr", "world.field.name",
                "world.field.world", "world.field.seed", "world.field.seed.rune.title",
                "world.field.seed.input.title", "common.chip.copy", "world.seed.copy.title",
                "world.field.password", "common.chip.show.title", "world.pwcheck.label",
                "world.pwcheck.note", "world.field.port", "world.field.visibility",
                "world.public.label", "world.field.crossplay", "world.crossplay.label",
                "world.field.save_interval", "world.field.backups_kept",
                "world.field.backup_short", "world.field.backup_long", "world.sec.modifiers",
                "hearth.upkeep.head.title", "world.mod.combat", "world.mod.combat.help.aria",
                "world.mod.death", "world.mod.death.help.aria", "world.mod.resources",
                "world.mod.resources.help.aria", "world.mod.raids", "world.mod.raids.help.aria",
                "world.mod.portals", "world.mod.portals.help.aria", "world.field.max_players",
                "world.mods.note", "world.sec.advanced", "common.norse.rites",
                "world.field.empty_restart", "world.field.empty_delay",
                "world.field.sched_restart", "world.field.sched_hours",
                "world.field.crash_restart", "world.crash.label", "world.field.crash_delay",
                "world.field.logs", "world.logs.label", "world.field.autostart",
                "world.autostart.label", "world.field.rcon", "world.field.rcon_port",
                "world.field.rcon_password", "world.field.priority", "world.sec.directories",
                "world.field.server_exe", "common.button.open", "world.server_exe.open.title",
                "world.field.save_dir", "world.save_dir.placeholder",
                "world.save_dir.open.title", "world.field.args", "world.setup.reset",
                "world.setup.reset.title", "world.setup.reset.note",
            });
        }

        /// <summary>
        /// The five world dials each key their label and the sentence their ? button
        /// speaks. The English is pinned here because these five are the words a host
        /// compares against the Valheim client on the other monitor: the map's glossary
        /// pass has to hand a translator the game's own term for each of them, and it
        /// cannot do that if the label has quietly been reworded here first.
        /// </summary>
        [Fact]
        public void The_five_world_dials_key_their_label_and_their_help()
        {
            var hall = Hall("world", "<!-- ============ PAGE: ATLAS");
            var catalog = Catalog();

            foreach (var (dial, word) in new[]
                     {
                         ("combat", "Combat"),
                         ("death", "Death Penalty"),
                         ("resources", "Resources"),
                         ("raids", "Raids"),
                         ("portals", "Portals"),
                     })
            {
                Assert.Contains("data-i18n=\"world.mod." + dial + "\"", hall);
                Assert.Contains("data-i18n-aria=\"world.mod." + dial + ".help.aria\"", hall);
                Assert.Equal(word, Lore(catalog, "world.mod." + dial));
                Assert.Equal("What each " + word + " option does",
                    Lore(catalog, "world.mod." + dial + ".help.aria"));
            }
        }

        /// <summary>
        /// The Process Priority list is keys, not copy. app.js writes prio.value back
        /// as the literal "AboveNormal" and C# reads the same word, so translating the
        /// three options would break the field rather than localise it: the same trap
        /// the palette's data-cmd values were split to avoid.
        /// </summary>
        [Fact]
        public void The_process_priority_options_stay_the_words_the_code_reads()
        {
            var hall = Hall("world", "<!-- ============ PAGE: ATLAS");
            var select = hall.Substring(hall.IndexOf("<select id=\"prioSel\">", StringComparison.Ordinal));
            select = select.Substring(0, select.IndexOf("</select>", StringComparison.Ordinal));

            Assert.DoesNotContain("data-i18n", select);
            Assert.Contains("<option>AboveNormal</option>", select);
            Assert.Contains("prio.value=\"AboveNormal\"", AppJs());
        }

        /// <summary>
        /// The three labels on the Settings hall that carry a number carry no id in the
        /// markup, and ask for one at run time. Each is rebuilt from the field beside it
        /// every time that field is typed in, so it is a composed sentence with a named
        /// slot rather than a static node: key the element and the walker starts fighting
        /// updAdvLabels(). Two of the three count something, so the entry carries its
        /// plural categories and updAdvLabels hands the number in rather than choosing a
        /// category itself.
        /// </summary>
        [Fact]
        public void The_advanced_labels_that_carry_a_number_ask_for_their_words_at_run_time()
        {
            var html = Html();
            var js = AppJs();
            var catalog = Catalog();

            foreach (var id in new[] { "tEmptyLbl", "tSchedLbl", "tRconLbl" })
            {
                var open = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
                Assert.True(open > 0, "index.html no longer has #" + id);
                var tag = html.Substring(html.LastIndexOf('<', open), html.IndexOf('>', open) - html.LastIndexOf('<', open) + 1);
                Assert.DoesNotContain("data-i18n", tag);
                Assert.Contains("$(\"#" + id + "\").textContent=", js);
            }

            Assert.Contains("$(\"#tEmptyLbl\").textContent=T(\"world.empty.label\",{minutes});", js);
            Assert.Contains("$(\"#tSchedLbl\").textContent=T(\"world.sched.label\",{hours});", js);
            Assert.Contains("T(\"world.rcon.label\",{port}):T(\"world.rcon.off.label\")", js);

            Assert.Equal("Restart when empty for {minutes} min", Plural(catalog, "world.empty.label", "other"));
            Assert.Equal("minutes", PluralParam(catalog, "world.empty.label"));
            Assert.Equal("Every {hours} h with in-game countdown", Plural(catalog, "world.sched.label", "other"));
            Assert.Equal("hours", PluralParam(catalog, "world.sched.label"));
            Assert.Equal("Bound on port {port}", Lore(catalog, "world.rcon.label"));
            Assert.Equal("Not bound", Lore(catalog, "world.rcon.off.label"));

            // and the label painted before the catalog lands is painted again after it
            Assert.Contains("try{updAdvLabels();}catch(_){}", js);

            // and the one label in that row which is NOT rebuilt, so it is keyed
            Assert.Contains("id=\"tCrashLbl\" data-i18n=\"world.crash.label\"", html);
            Assert.DoesNotContain("$(\"#tCrashLbl\")", js);
        }

        /// <summary>
        /// The two password chips key their tooltip in the markup and their word in the
        /// painter, and the split is the whole point. The tooltip says the same thing in
        /// both states, so the walker can own it. The word says which way the next click
        /// goes and changes under the host's finger, so a data-i18n on the chip would
        /// write SHOW back over a HIDE the next time anything walked the page:
        /// renderEyeChips() owns it instead, painting from the type of the box beside it,
        /// and the boot repaint runs it again when the catalog lands. The English in the
        /// markup is the floor for the frame before that.
        /// </summary>
        [Fact]
        public void The_password_chips_key_the_tooltip_and_paint_their_own_word()
        {
            var html = Html();
            var js = AppJs();

            Assert.Contains("id=\"eyePw\" title=\"Show / hide password\" data-i18n-title=\"common.chip.show.title\"", html);
            Assert.Contains("id=\"eyeRcon\" title=\"Show / hide password\" data-i18n-title=\"common.chip.show.title\"", html);
            Assert.DoesNotContain("id=\"eyePw\" title=\"Show / hide password\" data-i18n=\"", html);
            Assert.DoesNotContain("id=\"eyeRcon\" title=\"Show / hide password\" data-i18n=\"", html);

            Assert.Contains("chip.textContent=box.type===\"password\"?T(\"common.chip.show\"):T(\"common.chip.hide\");", js);
            Assert.DoesNotContain("\"HIDE\"", js);
            Assert.Contains("try{renderEyeChips();}catch(_){}", js);

            var catalog = Catalog();
            Assert.Equal("SHOW", Lore(catalog, "common.chip.show"));
            Assert.Equal("HIDE", Lore(catalog, "common.chip.hide"));
        }

        /// <summary>
        /// The banner on the Players hall keys its sentence and its footnote and not
        /// its button, because the install writes a progress word onto that button and
        /// never puts the first one back.
        /// </summary>
        [Fact]
        public void The_missing_mods_banner_keys_the_words_the_install_does_not_rewrite()
        {
            var hall = Hall("vikings", "<!-- ============ PAGE: MODS");
            var js = AppJs();

            Assert.Contains("class=\"capshead\" data-i18n=\"vikings.caps.head\"", hall);
            Assert.Contains("class=\"capsnote\" data-i18n=\"vikings.caps.note\"", hall);

            var button = hall.Substring(hall.IndexOf("id=\"capsInstallBtn\"", StringComparison.Ordinal) - 40, 130);
            Assert.DoesNotContain("data-i18n", button);
            Assert.Contains("const btn=$(\"#capsInstallBtn\");", js);
            Assert.Contains("btn.disabled=true; btn.textContent=", js);
        }

        // -------------------------------------------------- E. the bridge, still fed

        /// <summary>
        /// Two more words the bridge has to keep refusing, for the reason "World"
        /// already had two entries. "Name" heads the Mods table and also names the
        /// domain in the Waystone wizard; "Password" is a field on this hall and also
        /// a row in the Herald wizard saying whether the server password is shared.
        /// One entry for either would have bound a wizard row to a column header, and
        /// a translator would have shipped the column's word into the wizard. Both
        /// wizards are keyed in their own right now, so neither word leans on idFor()
        /// refusing what it sees twice any more: each surface asks for its own id by
        /// name. The two entries still have to be two, which is what this holds.
        /// </summary>
        [Fact]
        public void The_words_two_halls_share_stay_two_entries_each_asked_for_by_name()
        {
            var catalog = Catalog();
            var js = AppJs();

            Assert.Equal("Name", Lore(catalog, "mods.col.name"));
            Assert.Equal("Name", Lore(catalog, "waystone.wiz.sum.name"));
            Assert.Contains("T(\"waystone.wiz.sum.name\")", js);
            Assert.DoesNotContain("TT(\"Name\")", js);

            Assert.Equal("Password", Lore(catalog, "world.field.password"));
            Assert.Equal("Password", Lore(catalog, "herald.wiz.sum.password"));
            Assert.Contains("T(\"herald.wiz.sum.password\")", js);
            Assert.DoesNotContain("TT(\"Password\")", js);
        }

        /// <summary>
        /// A rune that leads a button, and the space that follows it, are part of the
        /// entry rather than markup around it, exactly as the dashboard's Sail forth
        /// button already is. The gap is a NO BREAK space on purpose: it is what stops
        /// the glyph being left alone on the line when the words below it grow.
        /// </summary>
        [Fact]
        public void A_button_that_leads_with_a_rune_carries_it_in_the_entry()
        {
            var catalog = Catalog();

            Assert.Equal("ᚨ  Add from Thunderstore", Lore(catalog, "mods.add.label"));
            Assert.Equal("ᚨ  Add from link", Lore(catalog, "mods.add.label.link"));
            Assert.Equal("ᛋ  Scan Thunderstore", Lore(catalog, "mods.scan.label"));
            Assert.Equal("ᛋ  Scan mod sites", Lore(catalog, "mods.scan.label.sites"));
            Assert.Equal("ᛃ  Open folder", Lore(catalog, "runes.open.label"));
            Assert.Equal("ᛋ  Reload", Lore(catalog, "runes.reload.label"));
            Assert.Equal("ᛉ  Save Config", Lore(catalog, "world.save.label"));
        }

        /// <summary>
        /// The Norse half of a caption is one entry and the plain half is another, and
        /// the Settings hall shows both at once: Heimr beside Server, Rites beside
        /// ADVANCED, Scrolls beside Config files on the hall next door. The plain
        /// terminology switch hides the caption with CSS, so the two never have to
        /// agree about which is showing.
        /// </summary>
        [Fact]
        public void A_Norse_caption_is_its_own_entry_beside_the_plain_word()
        {
            var catalog = Catalog();
            var html = Html();

            Assert.Equal("Heimr", Lore(catalog, "common.norse.heimr"));
            Assert.Equal("Rites", Lore(catalog, "common.norse.rites"));
            Assert.Equal("Scrolls", Lore(catalog, "common.norse.scrolls"));

            Assert.Contains("data-i18n=\"world.server.label\">Server<span class=\"cnorse\" data-i18n=\"common.norse.heimr\">", html);
            Assert.Contains("data-i18n=\"runes.list.label\">Config files<span class=\"cnorse\" data-i18n=\"common.norse.scrolls\">", html);
            Assert.Contains(".plain-terms .cnorse{display:none}", AppSourceTree.Web("app.css"));
        }

        // ---------------------------------------------------------------------- E. Map

        [Fact]
        public void The_map_hall_reads_its_words_out_of_the_catalog()
        {
            TheHallNames(Hall("atlas", "<!-- ============ PAGE: SAGA"), new[]
            {
                "atlas.head.title",
                "common.norse.atlas",
                "atlas.head.sub",
                "atlas.recenter.label",
                "atlas.recenter.title",
                "atlas.redraw.label",
                "atlas.redraw.title",
                "atlas.layers.label",
                "atlas.layer.portals",
                "atlas.layer.pois",
                "atlas.layer.builds",
                "atlas.layer.pins",
                "atlas.wx.label",
                "atlas.fact.saved",
                "atlas.fact.explored",
                "atlas.fact.event",
                "atlas.wx.now",
                "atlas.wx.wind",
                "atlas.wx.forecast",
                "atlas.roster.label",
                "atlas.roster.norse",
                "atlas.roster.note",
                "atlas.waypoints.label",
            });
        }

        /// <summary>
        /// The layer bar is the same split the palette rows carry: data-layer is the
        /// layer's name in the program and the words beside it are a sentence. A chip
        /// whose label became its key would stop drawing its layer the moment the label
        /// was translated.
        /// </summary>
        [Fact]
        public void Every_map_layer_chip_keys_its_words_and_not_its_name()
        {
            var hall = Hall("atlas", "<!-- ============ PAGE: SAGA");

            foreach (var (name, id) in new[]
            {
                ("portals", "atlas.layer.portals"), ("pois", "atlas.layer.pois"),
                ("builds", "atlas.layer.builds"), ("pins", "atlas.layer.pins"),
            })
            {
                Assert.Contains("data-layer=\"" + name + "\" data-i18n=\"" + id + "\"", hall);
            }

            Assert.Contains("const layer=ch.dataset.layer;", AppJs());
        }

        /// <summary>
        /// The fog chip is the fifth in that bar and the one that cannot carry an id.
        /// emberize() rebuilds its letters into one span each while app.js is still
        /// parsing, long before the catalog lands, so the walker would find no text node
        /// of its own to replace and would APPEND the label after the letters: the chip
        /// would read its words twice. Same reason, and same shape, as the horn of mead.
        /// This is the gate that makes the gap deliberate rather than forgotten, and it
        /// is written against emberize rather than against the one element, so a third
        /// emberized label given an id fails here too.
        /// </summary>
        [Fact]
        public void No_emberised_label_carries_an_id()
        {
            var html = Html();
            var js = AppJs();

            // Both shapes: the element handed straight to emberize, and the one a painter
            // takes an alias to first so it can write the words before lighting them. The
            // second shape is how every label that comes out of the catalog has to do it,
            // so a rule that only knew the first would stop covering them one by one. The
            // alias is read BACKWARDS from the emberize call to the nearest place that name
            // was taken, which is the only direction that cannot run past the function it
            // belongs to and pick up somebody else's el.
            var emberised = Regex.Matches(js, @"emberize\(\$\(""#([A-Za-z0-9_-]+)""\)\)")
                                 .Select(m => m.Groups[1].Value)
                                 .ToList();

            foreach (Match call in Regex.Matches(js, @"emberize\(([A-Za-z_$][A-Za-z0-9_$]*)\)"))
            {
                var name = call.Groups[1].Value;
                var taken = Regex.Matches(js.Substring(0, call.Index),
                    @"\b" + Regex.Escape(name) + @"\s*=\s*\$\(""#([A-Za-z0-9_-]+)""\)").LastOrDefault();
                if (taken != null) emberised.Add(taken.Groups[1].Value);
            }

            emberised = emberised.Distinct().ToList();

            Assert.Contains("lchipFog", emberised);
            Assert.Contains("meadLink", emberised);

            foreach (var id in emberised)
            {
                var open = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
                Assert.True(open > 0, "index.html no longer has #" + id);
                var tag = html.Substring(html.LastIndexOf('<', open), open - html.LastIndexOf('<', open) + 200);
                tag = tag.Substring(0, tag.IndexOf('>') + 1);
                Assert.False(Regex.IsMatch(tag, @"data-i18n="""),
                    "#" + id + " has its letters rebuilt by emberize(), so the walker would "
                    + "append its words instead of replacing them: " + tag);
            }
        }

        // ---------------------------------------------------------------------- F. Log

        [Fact]
        public void The_log_hall_reads_its_words_out_of_the_catalog()
        {
            TheHallNames(Hall("saga", "<!-- ============ PAGE: HERALD"), new[]
            {
                "saga.head.title",
                "common.norse.saga",
                "saga.head.sub",
                "saga.vellum.label",
                "saga.vellum.title",
                "saga.logs.label",
                "saga.filter.all",
                "saga.filter.info",
                "saga.filter.warn",
                "saga.filter.err",
                "saga.filter.net",
                "saga.search.placeholder",
                "saga.new.title",
                "saga.copy.label",
                "saga.copy.title",
            });
        }

        /// <summary>
        /// The five filter pills split their name from their words the same way. And the
        /// console line's placeholder stays keyless on purpose: app.js writes it three
        /// ways at run time, so the walker and app.js would fight over it.
        /// </summary>
        [Fact]
        public void The_log_filters_key_their_words_and_the_console_line_waits()
        {
            var hall = Hall("saga", "<!-- ============ PAGE: HERALD");
            var js = AppJs();

            foreach (var (name, id) in new[]
            {
                ("all", "saga.filter.all"), ("info", "saga.filter.info"), ("warn", "saga.filter.warn"),
                ("err", "saga.filter.err"), ("net", "saga.filter.net"),
            })
            {
                Assert.Contains("data-f=\"" + name + "\" data-i18n=\"" + id + "\"", hall);
            }
            Assert.Contains("filter=p.dataset.f;", js);

            var termIn = hall.Substring(hall.IndexOf("id=\"termIn\"", StringComparison.Ordinal) - 40, 220);
            Assert.DoesNotContain("data-i18n-placeholder", termIn);
            Assert.Contains("tIn.placeholder=gated", js);
            // all three of the ways app.js writes it are catalog sentences now
            Assert.Contains("tIn.placeholder=T(\"saga.term.native.placeholder\");", js);
            Assert.Contains("T(\"saga.term.placeholder\")", js);
            Assert.Contains("T(\"saga.term.gated.placeholder\")", js);
        }

        // ------------------------------------------------------------------ G. Discord

        [Fact]
        public void The_discord_hall_reads_its_words_out_of_the_catalog()
        {
            TheHallNames(Hall("herald", "<!-- ============ PAGE: SKALD"), new[]
            {
                "herald.head.title",
                "common.norse.herald",
                "herald.head.sub",
                "herald.wiz.label",
                "herald.wiz.title",
                "herald.sharing.label",
                "herald.sharing.toggle",
                "herald.sharing.toggle.title",
                "herald.field.webhook",
                "herald.field.webhook.title",
                "herald.field.thread",
                "herald.field.thread.title",
                "herald.thread.placeholder",
                "herald.carries.label",
                "herald.carries.note",
                "herald.share.address",
                "herald.share.address.title",
                "herald.share.password",
                "herald.share.password.title",
                "herald.share.password.warn",
                "herald.share.events",
                "herald.share.events.title",
                "herald.share.events.note",
                "herald.post.label",
                "herald.publish.label",
                "herald.publish.title",
                "herald.remove.label",
                "herald.remove.title",
            });
        }

        /// <summary>
        /// The one sentence this hall does not key, and why it is not an oversight. The
        /// card's opening paragraph has a bold word in the middle of it, so the walker
        /// would translate the words before the bold and leave the rest in English.
        /// The post's status line is app.js's, not the walker's: heraldRenderPost writes
        /// one of two whole catalog sentences into it every time the post appears or goes,
        /// so the element carries no id of its own and the two owners cannot fight.
        /// The webhook box keeps its example address, which is an address rather than a
        /// sentence and is not translated in any language.
        /// </summary>
        [Fact]
        public void The_discord_sentences_that_wait_are_the_ones_with_a_reason()
        {
            var hall = Hall("herald", "<!-- ============ PAGE: SKALD");
            var js = AppJs();

            var paragraph = hall.Substring(hall.IndexOf("BakaLoader keeps <strong>", StringComparison.Ordinal) - 60, 120);
            Assert.DoesNotContain("data-i18n", paragraph);

            var status = hall.Substring(hall.IndexOf("id=\"heraldPostStat\"", StringComparison.Ordinal) - 40, 120);
            Assert.DoesNotContain("data-i18n", status);
            Assert.Contains("el.textContent=HERALD_HAS_POST?T(\"herald.post.placed\"):T(\"herald.post.none\");", js);

            var url = hall.Substring(hall.IndexOf("id=\"heraldUrl\"", StringComparison.Ordinal), 200);
            Assert.Contains("placeholder=\"https://discord.com/api/webhooks/…\"", url);
            Assert.DoesNotContain("data-i18n-placeholder", url.Substring(0, url.IndexOf('>')));
        }

        // --------------------------------------------------------------- H. Statistics

        [Fact]
        public void The_statistics_hall_reads_its_words_out_of_the_catalog()
        {
            TheHallNames(Hall("skald", "<!-- STATUSBAR -->"), new[]
            {
                "skald.head.title",
                "common.norse.skald",
                "skald.reset.label",
                "skald.reset.title",
                "skald.uptime.label",
                "skald.uptime.norse",
                "skald.uptime.caption",
                "skald.starts.label",
                "skald.starts.norse",
                "skald.starts.caption",
                "skald.vikings.label",
                "common.norse.vikings",
                "skald.deaths.label",
                "skald.deaths.sub",
                "skald.mods.label",
                "skald.mods.norse",
                "vikings.col.name",
                "vikings.col.playtime",
                "skald.col.sessions",
                "vikings.col.deaths",
                "vikings.col.seen",
                "skald.feed.label",
                "skald.feed.norse",
            });
        }

        /// <summary>
        /// The player table on this hall says the same five words as the Players hall's
        /// table and asks the catalog for four of them by the same ids, so a translator
        /// is handed each column once. Sessions is this hall's own word: the Players hall
        /// has no such column.
        /// </summary>
        [Fact]
        public void The_statistics_table_shares_the_player_columns()
        {
            var catalog = Catalog();

            Assert.Equal("Player", Lore(catalog, "vikings.col.name"));
            Assert.Equal("Playtime", Lore(catalog, "vikings.col.playtime"));
            Assert.Equal("Deaths", Lore(catalog, "vikings.col.deaths"));
            Assert.Equal("Last seen", Lore(catalog, "vikings.col.seen"));
            Assert.Equal("Sessions", Lore(catalog, "skald.col.sessions"));

            Assert.Equal(2, Regex.Matches(Html(), @"data-i18n=""vikings\.col\.name""").Count);
            Assert.Equal(2, Regex.Matches(Html(), @"data-i18n=""vikings\.col\.playtime""").Count);
            Assert.Equal(2, Regex.Matches(Html(), @"data-i18n=""vikings\.col\.deaths""").Count);
            Assert.Equal(2, Regex.Matches(Html(), @"data-i18n=""vikings\.col\.seen""").Count);

            // Same here: one owner, and the register is the entry's rather than a
            // regex table's.
            Assert.DoesNotContain("const TERM_STATIC_SEL", AppJs());
        }

        /// <summary>
        /// The deaths caption is the one word on these four halls whose two registers
        /// really differ: applyTerms has been rewriting "valkyries dispatched" into
        /// "player deaths" through TERM_STATIC_SEL since before any of this, so the entry
        /// carries both and the swap and the catalog say the same thing. The subtitle
        /// beside it stays keyless, because renderSkald joins a date into it.
        /// </summary>
        [Fact]
        public void The_deaths_caption_carries_both_registers_and_the_subtitle_waits()
        {
            var catalog = Catalog();
            var hall = Hall("skald", "<!-- STATUSBAR -->");

            Assert.Equal("valkyries dispatched", Lore(catalog, "skald.deaths.sub"));
            Assert.Equal("player deaths", catalog["skald.deaths.sub"].GetProperty("plain").GetString());
            // The caption used to be reworded by a pair in the swap table AND named by the
            // selector list, which is two mechanisms for one word. Both are gone: the entry
            // carries both registers and the lookup picks between them.
            Assert.DoesNotContain("[\"valkyries dispatched\",\"player deaths\"]", AppJs());

            var sub = hall.Substring(hall.IndexOf("id=\"skaldSub\"", StringComparison.Ordinal) - 40, 120);
            Assert.DoesNotContain("data-i18n", sub);
            Assert.Contains("$(\"#skaldSub\").textContent=", AppJs());
        }
    }
}
