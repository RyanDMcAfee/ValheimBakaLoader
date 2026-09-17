using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The two search boxes the 1.1.0 halls carry, and the one rule they both live under:
    /// a search narrows what is DRAWN and nothing else.
    /// <para>
    /// This is the failure worth guarding. A filtered list that also feeds the counters
    /// makes the header lie ("3 loaded" when 63 are installed), makes "Update all (1)"
    /// promise less than it does, and makes the waiting-updates condition go quiet while
    /// updates are still waiting. Worse, a row resolved by its position in a filtered array
    /// acts on whichever mod happens to sit at that index in the unfiltered one, so a
    /// right-click on a searched row could remove a mod the host never touched.
    /// </para>
    /// <para>
    /// These are gates on the interface's own source, because what they guard against is a
    /// line somebody adds later.
    /// </para>
    /// </summary>
    public class WebUiSearchTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        private static string Html() => AppSourceTree.Web("index.html");

        private static string Between(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            if (start < 0) return "";
            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            return end < 0 ? source.Substring(start) : source.Substring(start, end - start);
        }

        private static int Count(string haystack, string needle)
        {
            var n = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        private static string RenderMods() =>
            Between(AppJs(), "function renderMods()", "/* \"showing N of M\", and only while");

        private static string RenderModShowing() =>
            Between(AppJs(), "function renderModShowing(", "async function scanMods()");

        /// <summary>
        /// The English catalog, read the way the page reads it. Several of the words below
        /// left app.js for <c>WebUI/i18n/en.json</c>, so the wording they used to be pinned
        /// by is now pinned where it lives: the call site is checked for the id, and the id
        /// is checked for the sentence. Both halves, or the gate would pass on a row that
        /// asks for a key holding the wrong words.
        /// </summary>
        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateObject())
                map[entry.Name] = entry.Value.Clone();
            return map;
        }

        private static string Lore(string id)
        {
            var catalog = Catalog();
            Assert.True(catalog.ContainsKey(id), "the English catalog has no " + id);
            Assert.True(catalog[id].TryGetProperty("lore", out var lore)
                        && lore.ValueKind == JsonValueKind.String,
                id + " carries no English");
            return catalog[id].GetProperty("lore").GetString();
        }

        /// <summary>Every catalog id a block of app.js asks for by name.</summary>
        private static List<string> IdsAskedIn(string block) =>
            System.Text.RegularExpressions.Regex.Matches(block, @"(?<![A-Za-z0-9_$])T\(""([a-z][a-z0-9_.]*)""\)")
                .Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        // ---------------------------------------------------------------- A. the Mods hall

        [Fact]
        public void The_mods_hall_carries_a_search_box_beside_the_line_that_counts_them()
        {
            var html = Html();
            var head = Between(html, "<section class=\"page\" id=\"page-mods\">", "<div class=\"modprog\"");

            Assert.Contains("id=\"modsSub\"", head);
            Assert.Contains("id=\"modSearch\"", head);
            Assert.Contains("id=\"modShowing\"", head);

            // The box sits after the count line, not somewhere else on the page.
            Assert.True(head.IndexOf("id=\"modsSub\"", StringComparison.Ordinal)
                        < head.IndexOf("id=\"modSearch\"", StringComparison.Ordinal),
                "the search box must stand beside the loaded-count line");
        }

        [Fact]
        public void What_is_typed_lives_in_page_state_so_a_re_render_keeps_it()
        {
            var js = AppJs();

            Assert.Contains("modFilter:\"\"", js);
            Assert.Contains("function setModFilter(value){", js);
            Assert.Contains("S.modFilter=String(value||\"\")", js);
            // The box is written back from state, which is what survives a re-render.
            Assert.Contains("if(box&&box.value!==S.modFilter) box.value=S.modFilter;", js);
        }

        /// <summary>
        /// The heart of it. The filter helper is called from the table body render and from
        /// nowhere else in the file, so no counter can ever be fed a narrowed list.
        /// </summary>
        [Fact]
        public void The_mods_filter_is_applied_in_the_body_render_and_nowhere_else()
        {
            var js = AppJs();

            // Once where it is defined, once where it is used. Any third mention is a
            // second caller, and a second caller is how a counter starts reading it.
            Assert.Equal(2, Count(js, "modsForBody"));

            var render = RenderMods();
            Assert.Contains("const shown=modsForBody(mods);", render);
            Assert.Contains("$(\"#modTable\").innerHTML=shown.map(", render);
        }

        [Fact]
        public void Every_counter_on_the_mods_hall_reads_the_whole_list()
        {
            var render = RenderMods();

            foreach (var line in new[]
            {
                "const mods=sortedMods(S.mods||[]);",           // the sort never sees the search
                "const upd=mods.filter(m=>m.UpdateAvailable);", // the Update all count
                "$(\"#modCount\").textContent=scanned?mods.length",
                "$(\"#sbMods\").textContent=(scanned?mods.length",
                "conditionModUpdates(upd.length);",             // the standing condition
                "renderModIndexLine(mods.length);",
            })
            {
                Assert.True(render.Contains(line, StringComparison.Ordinal),
                    "a counter on the Mods hall no longer reads the whole list: " + line);
            }

            // And the drawn rows are the only thing that reads the narrowed one.
            Assert.Equal(1, Count(render, "shown.map("));
            Assert.Equal(1, Count(render, "modsForBody(mods)"));
        }

        [Fact]
        public void Sorting_never_sees_the_search()
        {
            var sorted = Between(AppJs(), "function sortedMods(mods){", "/* ---------- SEARCH BOXES");

            Assert.DoesNotContain("modFilter", sorted);
            Assert.DoesNotContain("modsForBody", sorted);
        }

        [Fact]
        public void Update_all_and_the_unattended_paths_read_the_whole_list()
        {
            var js = AppJs();

            var updateAll = Between(js, "async function doUpdateAll()", "$(\"#updAllBtn\").addEventListener");
            Assert.Contains("(S.mods||[]).filter(m=>m.UpdateAvailable)", updateAll);
            Assert.DoesNotContain("modFilter", updateAll);
            Assert.DoesNotContain("modsForBody", updateAll);

            var button = Between(js, "$(\"#updAllBtn\").addEventListener", "$(\"#scanBtn\").addEventListener");
            Assert.Contains("const upd=(S.mods||[]).filter(m=>m.UpdateAvailable);", button);
            Assert.DoesNotContain("modsForBody", button);
        }

        /// <summary>
        /// With the table narrowed, Update all is the one button whose reach is wider than
        /// what is on screen, so it says so rather than leaving the host to find out.
        /// </summary>
        [Fact]
        public void Update_all_says_that_it_reaches_past_what_is_shown_while_a_search_is_on()
        {
            var showing = RenderModShowing();

            // The sentence moved into the catalog, so the tooltip is pinned in two places:
            // the call site asks for the id, and the id still holds those exact words.
            Assert.Contains("T(\"mods.showing.update_all.title\")", showing);
            Assert.Equal("updates every mod with an update, not only the ones shown",
                Lore("mods.showing.update_all.title"));
            Assert.Contains("btn.title=on?", showing);
            // "showing N of M" only while something is typed.
            Assert.Contains("el.textContent=on?TT(\"showing\")+\" \"+shown+\" \"+TT(\"of\")+\" \"+total:\"\";", showing);
        }

        /// <summary>
        /// A row is found again by its own name. Resolving it by an index into whatever was
        /// drawn is the bug this rules out: with a search on, index 0 of the table is not
        /// index 0 of the list the app acts on.
        /// </summary>
        [Fact]
        public void Every_mod_row_action_resolves_the_row_by_its_mod_key()
        {
            var js = AppJs();

            Assert.Contains("function modByKey(key){", js);
            Assert.Contains("return (S.mods||[]).find(m=>m&&m.FullName===key)||null;", js);

            // The row itself carries the key, not a position.
            Assert.Contains("<tr data-key=\"${esc(m.FullName)}\">", js);

            // Both row menus and both marks go through the key.
            Assert.Equal(2, Count(js, "e.target.closest(\"tr[data-key]\")"));
            Assert.Equal(2, Count(js, "modByKey(tr.dataset.key)"));
            Assert.Contains("modByKey(mark.dataset.hex)", js);
            Assert.Contains("modByKey(mark.dataset.ts)", js);

            // And nothing resolves a mod row by index any more. (The roster still does, and
            // is left alone: it carries no search box.)
            Assert.DoesNotContain("$(\"#modTable\")._list||[])[+tr.dataset.i]", js);
            Assert.DoesNotContain("<tr data-i=\"${i}\"><td><strong>${esc(m.ModName)}", js);
            Assert.DoesNotContain("list[+mark.dataset.hex]", js);
            Assert.DoesNotContain("list[+mark.dataset.ts]", js);
        }

        [Fact]
        public void A_search_that_matches_nothing_says_so_and_offers_the_way_back()
        {
            var render = RenderMods();

            // The empty state's words are catalog entries now, so the render is read for
            // the ids and the catalog for what those ids say. Both halves matter: an id
            // with no wording behind it draws a dotted name on the table.
            Assert.Contains("title:T(\"mods.empty.no_match.title\")", render);
            Assert.Equal("No mod matches that search", Lore("mods.empty.no_match.title"));
            Assert.Contains("action:{name:\"clearModSearch\",label:T(\"mods.empty.no_match.action\")}", render);
            Assert.Equal("Clear the search", Lore("mods.empty.no_match.action"));
            // And the older empty state, for a server with no mods at all, still stands.
            Assert.Contains("title:T(\"mods.empty.none.title\")", render);
            Assert.Equal("No mods installed", Lore("mods.empty.none.title"));

            // The one reason that counts what it is hiding is still composed, and still on
            // the bridge, because a translator handed "Clear the box to see all " cannot
            // word it. It says TT() out loud at the call site so it is visibly the odd one.
            Assert.Contains("reason:TT(\"Nothing in this server's mod list carries every word that was typed. "
                            + "Clear the box to see all \"+mods.length+\" again.\")", render);

            Assert.Contains("clearModSearch:()=>setModFilter(\"\")", AppJs());
        }

        /// <summary>
        /// The words on a row moved into the catalog, and the haystack has to ask for the
        /// SAME ids the row draws with. That is the whole point of the rule: typing the
        /// word on a pill has to find the row carrying it, in any language. So this reads
        /// both blocks and insists the set of ids matches, rather than listing the six
        /// tags twice and letting one side quietly gain a seventh.
        /// </summary>
        [Fact]
        public void A_search_covers_the_name_the_author_the_folder_the_versions_and_the_tags()
        {
            var text = Between(AppJs(), "function modSearchText(m){", "/* THE ONE PLACE");

            foreach (var field in new[]
            {
                "m.ModName", "m.Author", "m.FullName",
                "lastPathPart(m.PluginDirectory)",
                "m.InstalledVersion", "m.LatestVersion",
            })
            {
                Assert.True(text.Contains(field, StringComparison.Ordinal),
                    "the Mods search no longer covers " + field);
            }

            var expected = new[]
            {
                "mods.possibly_outdated.yes", "mods.status.bundled", "mods.status.current",
                "mods.status.held", "mods.status.not_listed", "mods.status.update",
                "mods.tag.hexium", "mods.tag.patcher",
            };
            Assert.Equal(expected, IdsAskedIn(text).ToArray());

            // And the row itself draws with exactly those, so neither side can drift. The
            // hall's empty states are asked for in the same function and are deliberately
            // NOT in the haystack: they are what the table says when there is no row to
            // search. They are subtracted by name rather than by prefix-and-hope, so a row
            // word that lands under mods.empty. by mistake fails here instead of vanishing.
            var emptyStates = new[]
            {
                "mods.empty.no_match.action", "mods.empty.no_match.title",
                "mods.empty.none.action", "mods.empty.none.reason", "mods.empty.none.title",
                "mods.empty.scanning.reason", "mods.empty.scanning.title",
                "mods.empty.unscanned.action", "mods.empty.unscanned.reason",
                "mods.empty.unscanned.title",
            };
            // The Latest cell's own note is drawn on the row and is deliberately NOT in the
            // haystack: it is the tooltip that explains the quiet pill, not a word the pill
            // says, and a search is over what a row says rather than what it explains.
            var rowTips = new[] { "mods.status.not_listed.tip" };
            var drawn = IdsAskedIn(RenderMods()).Where(id => !rowTips.Contains(id)).ToArray();
            Assert.Equal(emptyStates, drawn.Where(id => id.StartsWith("mods.empty.", StringComparison.Ordinal)).ToArray());
            Assert.Equal(expected, drawn.Where(id => !id.StartsWith("mods.empty.", StringComparison.Ordinal)).ToArray());

            // The words behind the ids are the ones the host reads on the pills.
            Assert.Equal("patcher", Lore("mods.tag.patcher"));
            Assert.Equal("Hexium", Lore("mods.tag.hexium"));
            Assert.Equal("Bundled", Lore("mods.status.bundled"));
            Assert.Equal("Update", Lore("mods.status.update"));
            Assert.Equal("Current", Lore("mods.status.current"));
            Assert.Equal("held", Lore("mods.status.held"));
            Assert.Equal("not listed", Lore("mods.status.not_listed"));
            Assert.Equal("Yes", Lore("mods.possibly_outdated.yes"));
        }

        [Fact]
        public void Every_word_typed_has_to_match_somewhere_and_case_is_ignored()
        {
            var tokens = Between(AppJs(), "function searchTokens(q){", "/* The last piece of a path");

            Assert.Contains("String(q||\"\").trim().toLowerCase().split(/\\s+/).filter(Boolean)", tokens);
            Assert.Contains("const hay=String(haystack||\"\").toLowerCase();", tokens);
            Assert.Contains("return tokens.every(t=>hay.includes(t));", tokens);
        }

        // ---------------------------------------------------------------- B. the Configs hall

        [Fact]
        public void The_configs_hall_carries_a_search_over_its_list_and_a_find_inside_the_file()
        {
            var html = Html();
            var page = Between(html, "<section class=\"page\" id=\"page-runes\">", "<!-- ============ PAGE: WORLD");

            Assert.Contains("id=\"runeSearch\"", page);
            Assert.Contains("id=\"runeShowing\"", page);
            Assert.Contains("id=\"cfgFind\"", page);
            Assert.Contains("id=\"cfgFindCount\"", page);

            // The list search stands over the list of scrolls.
            Assert.True(page.IndexOf("id=\"runeSearch\"", StringComparison.Ordinal)
                        < page.IndexOf("id=\"cfgList\"", StringComparison.Ordinal),
                "the config search must stand over the list it narrows");
            // The find bar stands over the text it searches.
            Assert.True(page.IndexOf("id=\"cfgFind\"", StringComparison.Ordinal)
                        < page.IndexOf("id=\"cfgEditor\"", StringComparison.Ordinal),
                "the find bar must stand over the editor it searches");
        }

        [Fact]
        public void The_configs_search_is_applied_in_the_list_render_and_nowhere_else()
        {
            var js = AppJs();

            // Defined once, called once.
            Assert.Equal(2, Count(js, "cfgFilesForList"));

            var render = Between(js, "function renderCfgList(){", "/* \"showing N of M\" beside the box");
            Assert.Contains("const shown=cfgFilesForList();", render);
            Assert.Contains("$(\"#cfgList\").innerHTML=shown.map(", render);
            // The count line still counts every scroll in the vault.
            Assert.Contains("$(\"#runesSub\").textContent=TT(CFG.files.length+\" rune-scroll\"", render);
        }

        /// <summary>
        /// Every write path acts on the whole document and the whole list. Saving a narrowed
        /// view would write whatever the search happened to be showing over the file.
        /// </summary>
        [Fact]
        public void Saving_reverting_and_reloading_all_act_on_the_whole_file()
        {
            var js = AppJs();

            var save = Between(js, "$(\"#cfgSaveBtn\").addEventListener", "\n/*");
            Assert.Contains("cfgWrite(CFG.file,$(\"#cfgEditor\").value)", save);
            Assert.DoesNotContain("runeFilter", save);
            Assert.DoesNotContain("cfgFilesForList", save);
            Assert.DoesNotContain("cfgFindMatches", save);

            var reload = Between(js, "$(\"#cfgReloadBtn\").addEventListener", "$(\"#cfgSaveBtn\").addEventListener");
            Assert.DoesNotContain("runeFilter", reload);
            Assert.DoesNotContain("cfgFilesForList", reload);

            var refresh = Between(js, "async function refreshCfgList(reRead){", "let cfgConfirmT=null;");
            Assert.Contains("CFG.files=files;", refresh);
            Assert.DoesNotContain("runeFilter", refresh);
            Assert.DoesNotContain("cfgFilesForList", refresh);

            var load = Between(js, "async function loadCfg(f,force){", "async function refreshCfgList");
            Assert.DoesNotContain("runeFilter", load);
            Assert.DoesNotContain("cfgFilesForList", load);

            // And the find box only ever reads the text.
            var find = FindRegion();
            Assert.DoesNotContain("ed.value=", find);
            Assert.DoesNotContain("cfgWrite", find);
        }

        /// <summary>
        /// The whole of the find bar, helpers and listeners together. Every rule below is
        /// read off this one region, because what they guard against is a line added
        /// anywhere in it.
        /// </summary>
        private static string FindRegion() =>
            Between(AppJs(), "/* ---- Find inside the open scroll ----", "function setCfgDirty(d){");

        /// <summary>
        /// THE rule of the find bar, and the one that was broken once: it never moves the
        /// keyboard, and it never leaves a selection in the config file waiting to be typed
        /// over.
        /// <para>
        /// The version this replaces called <c>ed.focus()</c> on every jump and handed the
        /// cursor back only on the typing path. So one Enter, or one click of the Next chip,
        /// put the keyboard inside the config text with the match SELECTED, and the very
        /// next letter the host typed REPLACED the match and marked the file unsaved. One
        /// confirm away from writing that over the host's real BepInEx config.
        /// </para>
        /// <para>
        /// The rule is now absolute and so is the gate: nothing anywhere in the find bar
        /// calls focus, nothing selects a range in the editor, and the jump argument is a
        /// direction and nothing else. A find that has to reach for focus to show its match
        /// is the defect coming back, so the gate is on the whole region rather than on one
        /// clever line. It fails against the source as it stood before the fix.
        /// </para>
        /// </summary>
        [Fact]
        public void The_find_bar_never_moves_the_keyboard_and_never_selects_the_config_text()
        {
            var js = AppJs();
            var find = FindRegion();

            Assert.NotEqual("", find);

            // Not one focus call in the whole bar: not on the editor, not on the box, not
            // on the chip. Nothing it does can decide where the next keystroke lands.
            Assert.DoesNotContain(".focus(", find);
            // And no selection left in the file behind it, which is the other half of how
            // the next keystroke ate the match.
            Assert.DoesNotContain("setSelectionRange", find);
            Assert.DoesNotContain("select()", find);

            // The jump takes a direction and nothing else. The second argument was the
            // switch that decided whether the keyboard came back, and there is no longer
            // anything for it to decide.
            Assert.Contains("function cfgFindGo(step){", find);
            Assert.DoesNotContain("keepTyping", js);
            Assert.DoesNotContain("cfgFindGo(1,", js);
            Assert.DoesNotContain("cfgFindGo(-1,", js);
            // Defined once and called from the four places the bar can be worked, all of
            // them with one argument. A fifth caller is a path this gate has not read.
            Assert.Equal(5, Count(js, "cfgFindGo("));
            Assert.Contains("addEventListener(\"input\",()=>{CFG_FIND_AT=-1;cfgFindGo(1);});", find);
            Assert.Contains("cfgFindGo(e.shiftKey?-1:1);", find);
            Assert.Contains("$(\"#cfgFindNext\")?.addEventListener(\"click\",()=>cfgFindGo(1));", find);

            // A press on the chip must not pull the keyboard out of the box either, so the
            // press itself is stopped from moving it.
            Assert.Contains("$(\"#cfgFindNext\")?.addEventListener(\"mousedown\",e=>e.preventDefault());", find);
        }

        /// <summary>
        /// What the find bar shows instead: the match is painted on a layer behind the text,
        /// which needs no focus to stay visible and cannot be typed over.
        /// </summary>
        [Fact]
        public void The_match_is_painted_on_a_layer_behind_the_text_rather_than_selected()
        {
            var html = Html();
            var find = FindRegion();

            // The layer stands behind the editor, inside the same box.
            Assert.Contains("<div class=\"cfg-edmark\" id=\"cfgEdMark\" aria-hidden=\"true\"></div>", html);
            Assert.True(html.IndexOf("id=\"cfgEdMark\"", StringComparison.Ordinal)
                        < html.IndexOf("id=\"cfgEditor\"", StringComparison.Ordinal),
                "the mark layer must be drawn behind the editor, not over it");

            // It is written from the editor's own text, with the match wrapped and the rest
            // escaped, so nothing a config file contains can become markup.
            Assert.Contains("layer.innerHTML=esc(text.slice(0,at))+\"<mark>\"+esc(text.slice(at,at+len))+\"</mark>\"+esc(text.slice(at+len))+\"\\n\";", find);
            // Same width as the editor, or the lines wrap differently and the mark drifts
            // off the word it belongs to.
            Assert.Contains("layer.style.width=ed.clientWidth+\"px\";", find);
            // And it follows the pane it sits behind.
            Assert.Contains("layer.scrollTop=ed.scrollTop; layer.scrollLeft=ed.scrollLeft;", find);

            // The type-setting on both must match, or the paint lands beside the letters.
            var css = AppSourceTree.Web("app.css");
            var editor = Between(css, ".cfg-editor{", "}");
            var mark = Between(css, ".cfg-edmark{", "}");
            foreach (var rule in new[] { "font-family:var(--mono)", "font-size:11.5px", "line-height:1.65", "padding:2px 4px" })
            {
                Assert.True(editor.Contains(rule, StringComparison.Ordinal), "the editor lost " + rule);
                Assert.True(mark.Contains(rule, StringComparison.Ordinal), "the mark layer lost " + rule);
            }
            // A textarea wraps its own lines this way; the layer has to be told to.
            Assert.Contains("white-space:pre-wrap", mark);
            Assert.Contains("overflow-wrap:break-word", mark);
            Assert.Contains("pointer-events:none", mark);
        }

        /// <summary>
        /// A mark painted over words the host has since retyped points at the wrong place,
        /// so an edit wipes it and the count with it. The words stay in the box: the next
        /// Enter looks again at the text as it now stands.
        /// </summary>
        [Fact]
        public void Editing_the_file_clears_the_mark_that_was_painted_over_the_old_text()
        {
            var js = AppJs();

            Assert.Contains("$(\"#cfgEditor\").addEventListener(\"input\",()=>{if(CFG.file)setCfgDirty(true);cfgFindStale();});", js);
            Assert.Contains("function cfgFindStale(){", js);
            // The same goes for text the app itself puts in the box: a reload from disk and
            // a list refresh both replace the whole document without a keystroke.
            Assert.Contains("if(text!==null){$(\"#cfgEditor\").value=text;cfgFindStale();}", js);
            Assert.Contains("if(t!==null){$(\"#cfgEditor\").value=t;cfgFindStale();}", js);
            // Every place the text is replaced drops the mark. Four writes to the box: the
            // one in loadCfg is covered by the full reset standing beside it, and a fifth
            // added later without a clear is what this count catches.
            Assert.Equal(4, Count(js, "$(\"#cfgEditor\").value="));
            // Opening another scroll, switching realm and Escape all drop it completely.
            Assert.Contains("function cfgFindReset(){", js);
            Assert.Contains("  cfgFindClearMark();", js);
            // A resize rewraps the lines, so the mark is painted again where the words went,
            // and the pane is NOT scrolled: resizing a window is not asking to jump.
            Assert.Contains("if(currentPage===\"runes\"){try{cfgFindPaint(false);}catch(_){}}", js);
        }

        [Fact]
        public void A_scroll_is_found_by_its_file_name_and_by_the_mod_that_wrote_it()
        {
            var text = Between(AppJs(), "function cfgSearchText(f){", "/* THE ONE PLACE the Configs search");

            Assert.Contains("const stem=name.replace(/\\.cfg$/i,\"\");", text);
            Assert.Contains("stem.split(/[.\\-_]+/)", text);
            Assert.Contains("bits.push(m.ModName,m.Author,m.FullName);", text);
        }

        // ---------------------------------------------------------------- C. shared behaviour

        [Fact]
        public void Escape_clears_each_box_and_the_slash_key_reaches_the_one_on_this_hall()
        {
            var js = AppJs();

            Assert.Contains("const PAGE_SEARCH_BOX={mods:\"#modSearch\",runes:\"#runeSearch\"};", js);
            Assert.Contains("if(e.key!==\"/\"||e.ctrlKey||e.metaKey||e.altKey) return;", js);
            // It stands down while a dialog or the palette is open, and while something is
            // already being typed into, so a slash typed in a field stays a slash.
            Assert.Contains("if(palOpen||$(\"#modalBg\")?.classList.contains(\"open\")) return;", js);
            Assert.Contains("if(typingIntoSomething()) return;", js);
            Assert.Contains("box.focus(); box.select();", js);

            // Escape on each box empties it without reaching anything behind it.
            Assert.Contains("$(\"#modSearch\")?.addEventListener(\"keydown\"", js);
            Assert.Contains("$(\"#runeSearch\")?.addEventListener(\"keydown\"", js);
            Assert.Equal(3, Count(js, "e.preventDefault(); e.stopPropagation();\n  setModFilter(\"\");")
                             + Count(js, "e.preventDefault(); e.stopPropagation();\n  setRuneFilter(\"\");")
                             + Count(js, "e.preventDefault(); e.stopPropagation();\n  cfgFindReset();"));
        }

        /// <summary>
        /// A letter typed in the Mods search draws the hall again, and the hall raises the
        /// waiting-updates row as it goes. Without a memory of the dismissal that row came
        /// straight back on the first letter the host typed, so waving it away was not
        /// possible while searching. It is keyed to the count, the way the restart-pending
        /// and plugin-failure rows are keyed to theirs, so a mod that picks up an update
        /// afterwards is news again.
        /// </summary>
        [Fact]
        public void A_waiting_updates_row_the_host_waved_away_stays_away_while_they_search()
        {
            var js = AppJs();
            var condition = Between(js, "let MOD_UPDATES_HIDDEN=null;", "/* Settings saved while the world was up.");

            Assert.Contains("if(!n){MOD_UPDATES_HIDDEN=null;clearCondition(\"modUpdates\");return;}", condition);
            Assert.Contains("const key=String(n);", condition);
            Assert.Contains("if(MOD_UPDATES_HIDDEN===key){clearCondition(\"modUpdates\");return;}", condition);
            Assert.Contains("onDismiss:()=>{MOD_UPDATES_HIDDEN=key;},", condition);
        }

        [Fact]
        public void Both_searches_are_dropped_when_the_helm_turns_to_another_realm()
        {
            var switchServer = Between(AppJs(), "async function switchServer(name){", "/* New-Server wizard");

            Assert.Contains("S.modFilter=\"\";", switchServer);
            Assert.Contains("S.runeFilter=\"\";", switchServer);
            Assert.Contains("cfgFindReset();", switchServer);
        }

        [Fact]
        public void Every_word_the_search_boxes_show_goes_through_the_wording_pass()
        {
            var js = AppJs();

            // "showing N of M" is still built out of two words and two numbers, so its two
            // halves are still bridged by their English. Rewriting that as one keyed
            // sentence with slots belongs with the rest of the composed messages.
            foreach (var phrase in new[] { "TT(\"showing\")", "TT(\"of\")" })
            {
                Assert.True(js.Contains(phrase, StringComparison.Ordinal),
                    "a search-box string no longer goes through TT(): " + phrase);
            }

            // The three that stand on their own now name catalog ids, and the ids still
            // hold the words the boxes showed.
            foreach (var (id, words) in new[]
            {
                ("runes.find.no_match", "no match"),
                ("runes.list.no_match", "no scroll carries that"),
                ("runes.list.empty", "no .cfg scrolls found"),
            })
            {
                Assert.True(js.Contains("T(\"" + id + "\")", StringComparison.Ordinal),
                    "a search-box string no longer goes through the catalog: " + id);
                Assert.Equal(words, Lore(id));
            }

            // Both empty lines in the scroll list are escaped on the way to the DOM. One of
            // them was not, which mattered the moment its words came out of a file.
            var list = Between(js, "function renderCfgList(){", "/* \"showing N of M\" beside the box");
            Assert.Contains("${esc(T(\"runes.list.no_match\"))}", list);
            Assert.Contains("${esc(T(\"runes.list.empty\"))}", list);

            // The placeholders swap with the rest of the wording too.
            Assert.Contains("[$(\"#palInput\"),$(\"#cfgEditor\"),$(\"#modSearch\"),$(\"#runeSearch\"),$(\"#cfgFind\")]", js);
        }

        // ---------------------------------------------------------------- D. the sidebar order

        /// <summary>
        /// Configs sits directly after Mods: the two halls a host moves between while fitting
        /// a mod out. Ids and data-page values are untouched, so every link and every keyboard
        /// route still lands where it did.
        /// </summary>
        [Fact]
        public void The_sidebar_runs_hearth_vikings_mods_runes_world_atlas_saga_herald_skald()
        {
            var html = Html();
            var rail = Between(html, "<div class=\"navitem active\" data-page=\"hearth\"", "</nav>");

            var order = System.Text.RegularExpressions.Regex
                .Matches(rail, "data-page=\"([a-z]+)\"")
                .Select(m => m.Groups[1].Value)
                .ToArray();

            Assert.Equal(
                new[] { "hearth", "vikings", "mods", "runes", "world", "atlas", "saga", "herald", "skald" },
                order);
        }

        /// <summary>
        /// The halls are written in the file in the order the rail lists them. Nothing on
        /// screen depends on it, since a hall is shown by being switched on rather than by
        /// where it sits, but a file whose order argues with the rail is how the next
        /// reordering goes wrong.
        /// </summary>
        [Fact]
        public void The_halls_are_written_in_the_order_the_rail_lists_them()
        {
            var html = Html();

            var rail = Between(html, "<div class=\"navitem active\" data-page=\"hearth\"", "</nav>");
            var railOrder = System.Text.RegularExpressions.Regex
                .Matches(rail, "data-page=\"([a-z]+)\"")
                .Select(m => m.Groups[1].Value)
                .ToArray();

            var sections = System.Text.RegularExpressions.Regex
                .Matches(html, "<section class=\"page[^\"]*\" id=\"page-([a-z]+)\"")
                .Select(m => m.Groups[1].Value)
                .ToArray();

            Assert.Equal(railOrder, sections);
        }

        // ---------------------------------------------------------------- E. the Hexium polish

        /// <summary>
        /// A copy the host took from Hexium that Thunderstore has since moved past is HELD,
        /// not CURRENT. It read CURRENT while the Latest cell beside it showed a higher
        /// number, which says the row is level with the world when it is deliberately not.
        /// </summary>
        [Fact]
        public void A_hexium_row_thunderstore_has_moved_past_reads_held_rather_than_current()
        {
            var render = RenderMods();

            var js = AppJs();

            Assert.Contains("const held=modIsHeld(m);", render);
            // Both halves of the pill still go through the wording pass, one through each
            // road: the word on it asks the catalog by id, and the sentence behind it is a
            // const app.js still spells out, so it takes the TT() bridge. Neither can be
            // left in the old wording while the other moves.
            Assert.Contains("held?`<span class=\"pill amber\" title=\"${esc(TT(MOD_HELD_TIP))}\">${esc(T(\"mods.status.held\"))}</span>`", render);
            Assert.Equal("held", Lore("mods.status.held"));
            Assert.Equal("Current", Lore("mods.status.current"));

            // CURRENT is what is left when nothing newer stands on EITHER site. Both arms
            // are here on purpose: Hexium moving past its own copy counts too.
            var rule = Between(js, "function modIsHeld(m){", "/* THE ONE PLACE the Mods search");
            Assert.Contains("if(!m||m.installedSource!==\"hexium\") return false;", rule);
            Assert.Contains("return !!(m.thunderstoreNewer&&m.LatestVersion)||!!(m.hexiumNewer&&m.hexiumLatest);", rule);

            // One rule, read by the pill and by what the search looks at, so the word on the
            // row and the word the search finds can never drift apart.
            Assert.Equal(3, Count(js, "modIsHeld"));

            var current = render.IndexOf("T(\"mods.status.current\")", StringComparison.Ordinal);
            var heldPill = render.IndexOf("esc(T(\"mods.status.held\"))", StringComparison.Ordinal);
            Assert.True(heldPill > 0 && current > heldPill,
                "the held pill must be decided before the row can fall through to Current");

            Assert.Contains(
                "const MOD_HELD_TIP=\"installed from Hexium; BakaLoader will not replace it on its own\";",
                AppJs());
        }

        /// <summary>
        /// The mark hangs off the Latest cell, and the Latest cell already prints the
        /// Thunderstore version, so a mark carrying that same number said it twice.
        /// </summary>
        [Fact]
        public void A_mark_that_would_repeat_the_number_in_its_own_cell_says_the_words_alone()
        {
            var mark = Between(AppJs(), "function modLatestMark(m)", "/* The transient status");

            Assert.Contains("const cell=String(m.LatestVersion||\"\");", mark);
            // The words are looked up at the call site now and handed in already chosen, so
            // the id is visible to the catalog gate rather than hiding inside a variable.
            // esc() still stands between them and the DOM, which is the half that matters.
            Assert.Contains("const say=(words,version)=>esc(words)+(String(version||\"\")===cell?\"\":\" \"+esc(version));", mark);
            Assert.Contains("say(T(\"mods.mark.thunderstore_newer\"),m.LatestVersion)", mark);
            Assert.Contains("say(T(\"mods.mark.hexium_newer\"),m.hexiumLatest)", mark);
            Assert.Equal("newer on Thunderstore", Lore("mods.mark.thunderstore_newer"));
            Assert.Equal("newer on Hexium", Lore("mods.mark.hexium_newer"));
        }

        /// <summary>
        /// Which site an install answer came from. The two Hexium calls spell it Source and
        /// the Thunderstore add now carries source, so the page reads either.
        /// </summary>
        [Fact]
        public void The_page_reads_the_source_of_an_install_answer_in_either_spelling()
        {
            var js = AppJs();

            Assert.Contains("function modResultSource(r){", js);
            Assert.Contains("return String(r.Source??r.source??\"\");", js);
            Assert.Contains("modResultSource(r)===\"hexium\"", js);
        }
    }
}
