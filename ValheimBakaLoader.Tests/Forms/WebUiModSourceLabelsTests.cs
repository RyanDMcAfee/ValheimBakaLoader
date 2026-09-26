using System;
using System.Collections.Generic;
using System.Text.Json;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// What the Mods hall calls the places it fetches from, and what one check does to the
    /// row it was asked about.
    /// <para>
    /// The hall used to say Thunderstore on every one of its buttons whatever the Upkeep
    /// switch was set to, so a host who had turned the second site on pressed a button
    /// promising one site and got two, and a host who had left it off was the only one
    /// being told the truth. Both readings now come out of the same setting: with the
    /// switch off nothing here names the second site at all, which is the rule the row
    /// menu and the Latest marks already live under.
    /// </para>
    /// <para>
    /// These are gates on the interface's own source, because what they guard against is a
    /// line somebody trims later. Each wording is checked in TWO places, never one: the
    /// call site has to ask for the id, and the catalog has to answer that id with the
    /// sentence. Pinning the English in app.js alone would pass on a hall drawing a dotted
    /// name, and pinning it in the catalog alone would pass on a hall that stopped reading
    /// it.
    /// </para>
    /// </summary>
    public class WebUiModSourceLabelsTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateObject())
                map[entry.Name] = entry.Value.Clone();
            return map;
        }

        /// <summary>The English an id answers with, and a failure naming the id when it has none.</summary>
        private static string Lore(string id)
        {
            var catalog = Catalog();
            Assert.True(catalog.ContainsKey(id), "the English catalog has no " + id);
            Assert.True(catalog[id].TryGetProperty("lore", out var lore)
                        && lore.ValueKind == JsonValueKind.String,
                id + " carries no English");
            return catalog[id].GetProperty("lore").GetString();
        }

        /// <summary>The rune a marked sentence leads with, or null.</summary>
        private static string Mark(string id)
        {
            var catalog = Catalog();
            Assert.True(catalog.ContainsKey(id), "the English catalog has no " + id);
            return catalog[id].TryGetProperty("mark", out var mark) && mark.ValueKind == JsonValueKind.String
                ? mark.GetString()
                : null;
        }

        private static string Between(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            if (start < 0) return "";
            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            return end < 0 ? source.Substring(start) : source.Substring(start, end - start);
        }

        private static string Labels() =>
            Between(AppJs(), "function renderModSourceLabels(){", "/* Both halves of the switch");

        private static string IndexLine() =>
            Between(AppJs(), "function renderModIndexLine(count){", "/* The two buttons in the Mods header");

        private static string AddDialog() =>
            Between(AppJs(), "function addModFlow()", "async function doAddMod");

        private static string RowState() =>
            Between(AppJs(), "function applyCheckOneToRow(row,r){", "/* Asks Thunderstore about one mod");

        private static string CheckOne() =>
            Between(AppJs(), "async function checkOneNow(mod){", "/* Update a single mod");

        // ------------------------------------------------- A. both readings, one setting

        [Fact]
        public void The_two_header_buttons_carry_a_wording_for_each_side_of_the_switch()
        {
            var labels = Labels();

            // Off: the one site a scan reads, said the way it has always been said.
            Assert.Contains("T(\"mods.add.label\")", labels);
            Assert.Contains("T(\"mods.scan.label\")", labels);

            // On: both sites are read, so neither button names only one of them.
            Assert.Contains("T(\"mods.add.label.link\")", labels);
            Assert.Contains("T(\"mods.scan.label.sites\")", labels);

            // And the setting is what picks between them, not the moment of the last scan
            // or anything else that happens to be in hand.
            Assert.Contains("const both=!!S.hexium;", labels);
            Assert.Contains("both?T(\"mods.add.label.link\"):T(\"mods.add.label\")", labels);
            Assert.Contains("both?T(\"mods.scan.label.sites\"):T(\"mods.scan.label\")", labels);

            // The words themselves, read where they live. The rune and the no break space
            // that follows it belong to the entry, so a button keeps its glyph in every
            // language rather than having one glued on at the call site.
            Assert.Equal("ᚨ  Add from Thunderstore", Lore("mods.add.label"));
            Assert.Equal("ᚨ  Add from link", Lore("mods.add.label.link"));
            Assert.Equal("ᛋ  Scan Thunderstore", Lore("mods.scan.label"));
            Assert.Equal("ᛋ  Scan mod sites", Lore("mods.scan.label.sites"));
        }

        [Fact]
        public void The_buttons_keep_the_ids_everything_else_finds_them_by()
        {
            var labels = Labels();

            // Only the words inside them move. A new id here would quietly unhook the
            // click handlers, the busy gating and the walk that photographs them.
            Assert.Contains("$(\"#addModBtn\")", labels);
            Assert.Contains("$(\"#scanBtn\")", labels);

            var html = AppSourceTree.Web("index.html");
            Assert.Contains("id=\"addModBtn\"", html);
            Assert.Contains("id=\"scanBtn\"", html);
        }

        /// <summary>
        /// The words on these two buttons belong to the render now, so neither carries a
        /// data-i18n: an element the walker fills and a render overwrites is a sentence
        /// with two owners, and the loser is whichever ran second. Their English stays in
        /// the page as the floor the window comes up on.
        /// </summary>
        [Fact]
        public void The_render_owns_the_two_button_labels_and_the_walker_leaves_them_alone()
        {
            var html = AppSourceTree.Web("index.html");

            foreach (var id in new[] { "addModBtn", "scanBtn" })
            {
                var open = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
                Assert.True(open > 0, "index.html no longer has #" + id);
                var tag = html.Substring(open, html.IndexOf('>', open) - open);
                Assert.DoesNotContain("data-i18n", tag);
            }

            Assert.Contains(">ᚨ&nbsp; Add from Thunderstore</button>", html);
            Assert.Contains(">ᛋ&nbsp; Scan Thunderstore</button>", html);
        }

        [Fact]
        public void The_line_under_the_heading_names_whichever_sites_were_read()
        {
            var line = IndexLine();

            Assert.Contains("const both=!!S.hexium;", line);
            Assert.Contains("both?T(\"mods.index.line.checked.both\",p):T(\"mods.index.line.checked\",p)", line);

            // The nothing-read-yet half follows the same setting, so the two halves of the
            // line can never disagree about how many sites are in play.
            Assert.Contains(
                "both?T(\"mods.index.line.unread.both\",p):T(\"mods.index.line.unread\",p)", line);

            // Four whole sentences with named slots rather than a count, a joiner and two
            // fragments: a language that puts the clock face first has somewhere to put it.
            Assert.Equal("{count} loaded · Thunderstore index checked {at}",
                Lore("mods.index.line.checked"));
            Assert.Equal("{count} loaded · Thunderstore and Hexium checked {at}",
                Lore("mods.index.line.checked.both"));
            Assert.Equal("{count} loaded · Thunderstore index not read yet",
                Lore("mods.index.line.unread"));
            Assert.Equal("{count} loaded · Thunderstore and Hexium not read yet",
                Lore("mods.index.line.unread.both"));

            // And the hover, which names the address that answered.
            Assert.Contains("T(\"mods.index.via.listing_index\")", line);
            Assert.Contains("T(\"mods.index.via.full_listing\")", line);
            Assert.Equal("via listing index", Lore("mods.index.via.listing_index"));
            Assert.Equal("via full listing", Lore("mods.index.via.full_listing"));
        }

        /// <summary>
        /// The clock face beside "checked" is written by the lookup, the way clock() is, so
        /// the time a list was read and the time a scan was pressed are never in two
        /// different notations on the same line.
        /// </summary>
        [Fact]
        public void The_time_the_list_was_read_is_written_in_the_hosts_own_notation()
        {
            var hhmm = Between(AppJs(), "function hhmm(iso){", "function renderMods()");

            Assert.Contains("const L=intl();", hhmm);
            Assert.Contains("return L?L.fmtTime(d):pad(d.getHours())+\":\"+pad(d.getMinutes());", hhmm);
        }

        [Fact]
        public void The_add_dialog_titles_and_asks_for_whichever_links_it_takes()
        {
            var add = AddDialog();

            Assert.Contains("const both=!!S.hexium;", add);
            Assert.Contains("both?T(\"mods.add.title.link\"):T(\"mods.add.title.store\")", add);
            Assert.Equal("Add mod from a link", Lore("mods.add.title.link"));
            Assert.Equal("Add mod from Thunderstore", Lore("mods.add.title.store"));

            // The box says what may be pasted into it, and with the second site on that is
            // a link from either of them. The example address is an address rather than a
            // sentence, so it stays where it is.
            Assert.Contains("T(\"mods.add.placeholder.link\")", add);
            Assert.Equal("paste a Thunderstore or Hexium link", Lore("mods.add.placeholder.link"));
            Assert.Contains("https://thunderstore.io/c/valheim/p/Author/ModName/", add);
        }

        /// <summary>
        /// A label that is only written at boot sits a whole render behind the switch that
        /// picks it, which is the same class of stale reading the rest of this release is
        /// about. One function owns the setting and redraws the hall, and every way the
        /// switch can move goes through it.
        /// </summary>
        [Fact]
        public void One_function_owns_the_setting_and_redraws_the_hall()
        {
            var js = AppJs();
            var setter = Between(js, "function setHexiumSource(on){", "/* The clock face of an ISO");

            Assert.Contains("S.hexium=!!on;", setter);
            Assert.Contains("renderMods()", setter);

            // On load, on the host's own flip, and on the browser preview's flip.
            Assert.Contains("setHexiumSource(up.UseHexiumSource);", js);
            Assert.Contains("setHexiumSource(swOn(\"tUseHexium\"));", js);
            Assert.Contains("()=>setHexiumSource(swOn(\"tUseHexium\"))", js);

            // Nothing writes the mirror behind that function's back.
            Assert.DoesNotContain("S.hexium=swOn(\"tUseHexium\")", js);
            Assert.DoesNotContain("S.hexium=!!up.UseHexiumSource", js);

            // And the render is what puts the words on the buttons.
            var render = Between(js, "function renderMods(){", "const scanned=S.mods!==null;");
            Assert.Contains("renderModSourceLabels();", render);
        }

        // ------------------------------------- B. what one check does to the row it asked

        /// <summary>
        /// A row cannot hold two answers to one question. A check that comes back saying
        /// the package is no longer listed used to leave whatever the last scan had put on
        /// the row, so the row could carry a "not listed" pill and still offer an Update to
        /// a version the site no longer has, and Update all went on counting it.
        /// </summary>
        [Fact]
        public void A_check_that_finds_the_package_pulled_takes_the_update_off_the_row()
        {
            var state = RowState();

            // The note itself, from the same reply.
            Assert.Contains("row.notListed=!!r.notListed;", state);

            // Nothing was removed or rolled back to say it, so the version last seen stays
            // where it is: only the offer to update to it goes.
            Assert.Contains("}else if(row.notListed){", state);
            var pulled = Between(state, "}else if(row.notListed){", "return row;");
            Assert.Contains("row.UpdateAvailable=false;", pulled);
            Assert.DoesNotContain("row.LatestVersion", pulled);
        }

        [Fact]
        public void A_check_nobody_could_answer_leaves_the_row_exactly_where_it_was()
        {
            var state = RowState();

            // "the site answered and this was not in it" is the only case that touches the
            // row's update state. A reply that found nothing and is not saying the package
            // was pulled falls through both branches, the way the scan path leaves a row
            // alone when no list came back.
            Assert.Contains("else if(row.notListed)", state);
            Assert.DoesNotContain("else{", state);

            // And the versions are only ever written from a reply that found the package.
            var found = Between(state, "if(r.found){", "}else if(row.notListed){");
            Assert.Contains("row.LatestVersion=r.latestVersion;", found);
            Assert.Contains("row.UpdateAvailable=!!r.updateAvailable;", found);
        }

        [Fact]
        public void The_check_writes_the_row_through_that_one_helper()
        {
            var check = CheckOne();

            Assert.Contains("applyCheckOneToRow(row,r);", check);

            // The row is written in one place now, so a later edit cannot fix the toast and
            // leave a second copy of the row rules behind.
            Assert.DoesNotContain("row.notListed=!!r.notListed;", check);
            Assert.DoesNotContain("row.UpdateAvailable=!!r.updateAvailable;", check);
        }

        /// <summary>
        /// Every answer a single check can come back with says so out loud, and every one
        /// of those sentences is the catalog's. The three that carry a value name it in a
        /// slot rather than being glued together here, because a language that puts the
        /// version after the verb has nowhere to put it otherwise.
        /// </summary>
        [Fact]
        public void Every_answer_a_check_can_give_reads_its_words_out_of_the_catalog()
        {
            var check = CheckOne();

            foreach (var id in new[]
            {
                "mods.check_one.cooldown.toast", "mods.check_one.bad_name.toast",
                "mods.check_one.unreachable.toast", "mods.check_one.current.toast",
            })
            {
                Assert.Contains("T(\"" + id + "\")", check);
            }

            foreach (var id in new[]
            {
                "mods.check_one.not_listed.toast", "mods.check_one.held.toast",
                "mods.check_one.newer.toast",
            })
            {
                Assert.Contains("T(\"" + id + "\",{", check);
            }

            Assert.Equal("that one was just checked; give it a moment",
                Lore("mods.check_one.cooldown.toast"));
            Assert.Equal("this folder does not name a Thunderstore package",
                Lore("mods.check_one.bad_name.toast"));
            Assert.Equal("Thunderstore did not answer. Try again in a little while.",
                Lore("mods.check_one.unreachable.toast"));
            Assert.Equal("{name} is not listed on Thunderstore right now",
                Lore("mods.check_one.not_listed.toast"));
            Assert.Equal(
                "Thunderstore has {version} · this copy came from Hexium, so BakaLoader leaves it where it is",
                Lore("mods.check_one.held.toast"));
            Assert.Equal("{version} is the newest on Thunderstore", Lore("mods.check_one.newer.toast"));
            Assert.Equal("you have the newest", Lore("mods.check_one.current.toast"));

            // A toast leads with a rune, and the entry says which, so the day the rune is
            // read out of the catalog the glyph does not have to be found again.
            Assert.Equal("ᛋ", Mark("mods.check_one.cooldown.toast"));
            Assert.Equal("ᚦ", Mark("mods.check_one.bad_name.toast"));
            Assert.Equal("ᚦ", Mark("mods.check_one.unreachable.toast"));
            Assert.Equal("ᛋ", Mark("mods.check_one.not_listed.toast"));
            Assert.Equal("ᛋ", Mark("mods.check_one.held.toast"));
            Assert.Equal("ᛋ", Mark("mods.check_one.newer.toast"));
            Assert.Equal("ᛋ", Mark("mods.check_one.current.toast"));
        }

        /// <summary>
        /// The row menu's new entry and the reason a delisted row cannot be updated. Both
        /// are read out of the catalog like every other line of that menu, because a
        /// disabled entry still says its reason out loud.
        /// </summary>
        [Fact]
        public void The_row_menu_says_what_one_check_is_and_why_a_pulled_mod_cannot_update()
        {
            var menu = Between(AppJs(), "function modRowItems(mod){",
                "/* What one check's answer does to the row");

            Assert.Contains("T(\"mods.menu.check_one\")", menu);
            Assert.Contains("T(\"mods.menu.check_one.tip.bundled\")", menu);
            Assert.Contains("T(\"mods.menu.check_one.tip.no_package\")", menu);
            Assert.Contains("mod.notListed?T(\"mods.menu.update.tip.not_listed\")", menu);

            Assert.Equal("Check this mod now", Lore("mods.menu.check_one"));
            Assert.Equal("This plugin ships inside BakaLoader, so there is no Thunderstore package to ask about.",
                Lore("mods.menu.check_one.tip.bundled"));
            Assert.Equal("This folder does not name a Thunderstore package, so there is nothing to ask about.",
                Lore("mods.menu.check_one.tip.no_package"));
            Assert.Equal(
                "Thunderstore is not listing this mod at the moment, so there is nothing to update it to.",
                Lore("mods.menu.update.tip.not_listed"));
        }

        /// <summary>
        /// A scan says it has begun and says what it found, and the second sentence counts
        /// its updates through a plural the language picks rather than an English "s".
        /// </summary>
        [Fact]
        public void The_scan_toasts_are_the_catalogs_and_the_count_is_a_real_plural()
        {
            var js = AppJs();
            var catalog = Catalog();

            Assert.Contains("T(\"mods.scan.begun.toast\")", js);
            Assert.Contains("T(\"mods.scan.done.toast\",{count:rows.length,updates:u})", js);

            Assert.Equal("Thunderstore scan begun", Lore("mods.scan.begun.toast"));

            var done = catalog["mods.scan.done.toast"];
            Assert.Equal("updates", done.GetProperty("plural").GetString());
            Assert.Equal("Scan complete · {count} mods · {updates} update",
                done.GetProperty("lore").GetProperty("one").GetString());
            Assert.Equal("Scan complete · {count} mods · {updates} updates",
                done.GetProperty("lore").GetProperty("other").GetString());
        }

        // ------------------------------------------------------------- C. the quiet pill

        [Fact]
        public void The_not_listed_pill_is_built_like_every_other_pill()
        {
            var css = AppSourceTree.Web("app.css");

            Assert.Contains(".pill.grey{", css);
            var grey = Between(css, ".pill.grey{", "}");

            // A border and a ground, the way .green, .amber, .blue and .ember all have one:
            // without them the pill rendered as bare text in a row of styled ones.
            Assert.Contains("background:", grey);
            Assert.Contains("border:1px solid", grey);
            Assert.Contains("color:", grey);
        }

        [Fact]
        public void The_row_reaches_for_that_variant_and_carries_the_latest_cells_own_note()
        {
            var render = Between(AppJs(), "function renderMods(){", "/* \"showing N of M\", and only while");

            // The pill and the Latest cell say the same thing on hover, because they are
            // the same fact about the row read twice, so they ask for one id.
            Assert.Contains(
                ":m.notListed?`<span class=\"pill grey\" title=\"${esc(T(\"mods.status.not_listed.tip\"))}\">",
                render);
            Assert.Contains(
                "${blind?` title=\"${esc(T(\"mods.status.unchecked.tip\"))}\"`"
                + ":(m.notListed?` title=\"${esc(T(\"mods.status.not_listed.tip\"))}\"`:\"\")}",
                render);

            // And it is still read before the up-to-date reading, so a row whose Latest
            // nobody knows never says CURRENT.
            Assert.True(
                render.IndexOf("m.notListed?`<span class=\"pill grey\"", StringComparison.Ordinal)
                < render.IndexOf("T(\"mods.status.current\")", StringComparison.Ordinal),
                "the not-listed pill must be read before the current/update pill");

            Assert.Equal("not listed", Lore("mods.status.not_listed"));
            Assert.Equal("not listed on Thunderstore right now", Lore("mods.status.not_listed.tip"));

            // A scan that never reached the site is the whole table's version of the same
            // rule, and it is read BEFORE both of the readings that claim to know a latest
            // version, so no row says CURRENT against a site that was never asked.
            Assert.Contains(
                ":blind?`<span class=\"pill grey\" title=\"${esc(T(\"mods.status.unchecked.tip\"))}\">",
                render);
            Assert.True(
                render.IndexOf(":blind?`<span class=\"pill grey\"", StringComparison.Ordinal)
                < render.IndexOf("m.notListed?`<span class=\"pill grey\"", StringComparison.Ordinal),
                "the unchecked pill must be read before the not-listed one");
        }
    }
}
