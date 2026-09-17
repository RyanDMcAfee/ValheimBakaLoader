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
            Assert.Contains("#page-vikings th", AppJs());   // still swapped by applyTerms too
        }

        // --------------------------------------------------------------------- B. Mods

        [Fact]
        public void The_mods_hall_reads_its_words_out_of_the_catalog()
        {
            TheHallNames(Hall("mods", "<!-- ============ PAGE: RUNES"), new[]
            {
                "mods.head.title", "mods.search.aria", "mods.add.label", "mods.scan.label",
                "mods.col.name", "common.sort.by_name", "mods.col.installed",
                "mods.col.installed.title", "mods.col.latest", "mods.col.latest.title",
                "mods.col.status", "mods.col.status.title", "mods.col.possibly_outdated",
            });
        }

        /// <summary>
        /// Two labels on the Mods hall are keyless because app.js writes them: the
        /// Update all button carries a count, and the Possibly outdated tooltip is one
        /// of two sentences chosen by whether the game's update date could be read.
        /// Keying either one would put the walker and the render in a fight the render
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
            Assert.Contains("th.title=(scanned&&mods.length&&!anyGameDate)?MOD_PO_TIP_UNKNOWN:MOD_PO_TIP;", js);
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
                "runes.find.next.title",
            });
        }

        /// <summary>
        /// The Save button on the Configs hall is keyless, and this says why rather
        /// than leaving it looking forgotten: resetCfgSaveBtn() writes the same words
        /// back after the confirm state, so the button has two owners until that
        /// literal moves into the catalog with it, in one change rather than two.
        /// </summary>
        [Fact]
        public void The_config_save_button_waits_for_the_render_that_rewrites_it()
        {
            var hall = Hall("runes", "<!-- ============ PAGE: WORLD");
            var button = hall.Substring(hall.IndexOf("id=\"cfgSaveBtn\"", StringComparison.Ordinal) - 40, 140);

            Assert.DoesNotContain("data-i18n", button);
            Assert.Contains("function resetCfgSaveBtn(){", AppJs());
            Assert.Contains("b.innerHTML=\"ᛉ&nbsp; Save\";", AppJs());
        }

        /// <summary>
        /// The three search boxes on these halls key the sentence a screen reader
        /// reads and NOT the one the box shows, because applyTerms() still caches and
        /// restores those five placeholders. Moving a placeholder into the catalog
        /// while the cache still owns it would restore the English over the catalog's
        /// word on the first terminology toggle. Both halves move when the cache goes.
        /// </summary>
        [Fact]
        public void The_search_boxes_key_the_spoken_label_and_leave_the_placeholder_to_the_cache()
        {
            var html = Html();
            var js = AppJs();

            foreach (var (id, aria) in new[]
                     {
                         ("modSearch", "mods.search.aria"),
                         ("runeSearch", "runes.search.aria"),
                         ("cfgFind", "runes.find.aria"),
                     })
            {
                var open = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
                Assert.True(open > 0, "index.html no longer has #" + id);
                var tag = html.Substring(html.LastIndexOf('<', open), html.IndexOf('>', open) - html.LastIndexOf('<', open) + 1);
                Assert.Contains("data-i18n-aria=\"" + aria + "\"", tag);
                Assert.DoesNotContain("data-i18n-placeholder", tag);
            }

            Assert.Contains("[$(\"#palInput\"),$(\"#cfgEditor\"),$(\"#modSearch\"),$(\"#runeSearch\"),$(\"#cfgFind\")]", js);
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
        /// The three labels on the Settings hall that carry a number carry no id. Each
        /// one is rebuilt from a field beside it every time that field is typed in, so
        /// they are composed sentences rather than static ones and belong to the slice
        /// that keys run-time messages. Read backwards this is also the gate above: key
        /// one of these and the walker starts fighting updAdvLabels().
        /// </summary>
        [Fact]
        public void The_advanced_labels_that_carry_a_number_wait_for_the_message_slice()
        {
            var html = Html();
            var js = AppJs();

            foreach (var id in new[] { "tEmptyLbl", "tSchedLbl", "tRconLbl" })
            {
                var open = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
                Assert.True(open > 0, "index.html no longer has #" + id);
                var tag = html.Substring(html.LastIndexOf('<', open), html.IndexOf('>', open) - html.LastIndexOf('<', open) + 1);
                Assert.DoesNotContain("data-i18n", tag);
                Assert.Contains("$(\"#" + id + "\").textContent=", js);
            }

            // and the one label in that row which is NOT rebuilt, so it is keyed
            Assert.Contains("id=\"tCrashLbl\" data-i18n=\"world.crash.label\"", html);
            Assert.DoesNotContain("$(\"#tCrashLbl\")", js);
        }

        /// <summary>
        /// The two password chips key their tooltip and not their word, because
        /// wireEye() swaps that word between SHOW and HIDE as the host clicks. The
        /// tooltip says the same thing in both states and is never written from app.js,
        /// so it is safe to move now and the word is not.
        /// </summary>
        [Fact]
        public void The_password_chips_key_the_tooltip_the_two_states_share()
        {
            var html = Html();

            Assert.Contains("id=\"eyePw\" title=\"Show / hide password\" data-i18n-title=\"common.chip.show.title\"", html);
            Assert.Contains("id=\"eyeRcon\" title=\"Show / hide password\" data-i18n-title=\"common.chip.show.title\"", html);
            Assert.DoesNotContain("id=\"eyePw\" title=\"Show / hide password\" data-i18n=", html);
            Assert.Contains("$(\"#\"+chipId).textContent=show?\"HIDE\":\"SHOW\";", AppJs());
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
            Assert.Equal("ᛋ  Scan Thunderstore", Lore(catalog, "mods.scan.label"));
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

            var emberised = Regex.Matches(js, @"emberize\(\$\(""#([A-Za-z0-9_-]+)""\)\)")
                                 .Select(m => m.Groups[1].Value)
                                 .Distinct()
                                 .ToList();

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
            Assert.Contains("tIn.placeholder=\"console command… (Enter to send)\";", js);
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
        /// The two sentences this hall does not key, and why each one is not an oversight.
        /// The card's opening paragraph has a bold word in the middle of it, so the walker
        /// would translate the words before the bold and leave the rest in English. The
        /// post's status line is written by heraldRenderPost through TT() every time the
        /// post appears or goes. Both belong to the slice that keys run-time messages.
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
            Assert.Contains("el.textContent=TT(HERALD_HAS_POST", js);

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

            Assert.Contains("#page-skald th", AppJs());   // still swapped by applyTerms too
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
            Assert.Contains("#skDeathsSub", AppJs());     // named by TERM_STATIC_SEL
            Assert.Contains("[\"valkyries dispatched\",\"player deaths\"]", AppJs());

            var sub = hall.Substring(hall.IndexOf("id=\"skaldSub\"", StringComparison.Ordinal) - 40, 120);
            Assert.DoesNotContain("data-i18n", sub);
            Assert.Contains("$(\"#skaldSub\").textContent=", AppJs());
        }
    }
}
