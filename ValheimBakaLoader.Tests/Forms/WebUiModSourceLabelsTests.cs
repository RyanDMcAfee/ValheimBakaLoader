using System;
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
    /// line somebody trims later.
    /// </para>
    /// </summary>
    public class WebUiModSourceLabelsTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

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

        // ------------------------------------------------- A. both readings, one setting

        [Fact]
        public void The_two_header_buttons_carry_a_wording_for_each_side_of_the_switch()
        {
            var labels = Labels();

            // Off: the one site a scan reads, said the way it has always been said.
            Assert.Contains("TT(\"Add from Thunderstore\")", labels);
            Assert.Contains("TT(\"Scan Thunderstore\")", labels);

            // On: both sites are read, so neither button names only one of them.
            Assert.Contains("TT(\"Add from link\")", labels);
            Assert.Contains("TT(\"Scan mod sites\")", labels);

            // And the setting is what picks between them, not the moment of the last scan
            // or anything else that happens to be in hand.
            Assert.Contains("const both=!!S.hexium;", labels);
            Assert.Contains("both?TT(\"Add from link\"):TT(\"Add from Thunderstore\")", labels);
            Assert.Contains("both?TT(\"Scan mod sites\"):TT(\"Scan Thunderstore\")", labels);
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

        [Fact]
        public void The_line_under_the_heading_names_whichever_sites_were_read()
        {
            var line = IndexLine();

            Assert.Contains("const both=!!S.hexium;", line);
            Assert.Contains("both?TT(\"Thunderstore and Hexium checked\"):TT(\"Thunderstore index checked\")", line);

            // The nothing-read-yet half follows the same setting, so the two halves of the
            // line can never disagree about how many sites are in play.
            Assert.Contains(
                "both?TT(\"Thunderstore and Hexium not read yet\"):TT(\"Thunderstore index not read yet\")", line);
        }

        [Fact]
        public void The_add_dialog_titles_and_asks_for_whichever_links_it_takes()
        {
            var add = AddDialog();

            Assert.Contains("const both=!!S.hexium;", add);
            Assert.Contains("both?TT(\"Add mod from a link\"):TT(\"Add mod from Thunderstore\")", add);

            // The box says what may be pasted into it, and with the second site on that is
            // a link from either of them.
            Assert.Contains("TT(\"paste a Thunderstore or Hexium link\")", add);
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
            Assert.Contains("setHexiumSource(T(\"tUseHexium\"));", js);
            Assert.Contains("()=>setHexiumSource(T(\"tUseHexium\"))", js);

            // Nothing writes the mirror behind that function's back.
            Assert.DoesNotContain("S.hexium=T(\"tUseHexium\")", js);
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
            var check = Between(AppJs(), "async function checkOneNow(mod){", "/* Update a single mod");

            Assert.Contains("applyCheckOneToRow(row,r);", check);

            // The row is written in one place now, so a later edit cannot fix the toast and
            // leave a second copy of the row rules behind.
            Assert.DoesNotContain("row.notListed=!!r.notListed;", check);
            Assert.DoesNotContain("row.UpdateAvailable=!!r.updateAvailable;", check);
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
            // the same fact about the row read twice.
            Assert.Contains(
                ":m.notListed?`<span class=\"pill grey\" title=\"${esc(TT(MOD_NOT_LISTED_TIP))}\">", render);
            Assert.Contains("${m.notListed?` title=\"${esc(TT(MOD_NOT_LISTED_TIP))}\"`:\"\"}", render);

            // And it is still read before the up-to-date reading, so a row whose Latest
            // nobody knows never says CURRENT.
            Assert.True(
                render.IndexOf("m.notListed?`<span class=\"pill grey\"", StringComparison.Ordinal)
                < render.IndexOf("TT(\"Current\")", StringComparison.Ordinal),
                "the not-listed pill must be read before the current/update pill");
        }
    }
}
