using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The page half of the 1.2.0 pre-freeze pass, held as source rules.
    /// <para>
    /// Each of these was found by driving the real page in a browser, and each is kept
    /// here because a browser probe runs when somebody remembers to run it and this runs
    /// on every build. What a probe can prove and this cannot is that the pixels are
    /// right; what this can prove and a probe cannot is that the mechanism is still
    /// wired the way the measurement was taken against. Both are wanted.
    /// </para>
    /// </summary>
    public class WebUiPreFreezePageTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        private static string Html() => AppSourceTree.Web("index.html");

        private static string Css() => AppSourceTree.Web("app.css");

        private static string Lookup() => AppSourceTree.Web("i18n.js");

        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateObject())
                map[entry.Name] = entry.Value.Clone();
            return map;
        }

        /// <summary>One function's body, from its opening line to the next one at column zero.</summary>
        private static string Body(string name)
        {
            var js = AppJs();
            var at = js.IndexOf("function " + name + "(", StringComparison.Ordinal);
            Assert.True(at >= 0, "app.js no longer declares " + name);
            var end = js.IndexOf("\n}\n", at, StringComparison.Ordinal);
            return end < 0 ? js.Substring(at) : js.Substring(at, end - at);
        }

        // ------------------------------------------------- A1. a pack with holes in it

        /// <summary>
        /// A value that is not a sentence is a HOLE, and a hole falls through to the
        /// English. The shape that made this necessary is real: a pack merge writes the
        /// literal "__missing" for every id no translation batch answered, the id is then
        /// PRESENT in the active catalog, the fallback never ran, and what reached a
        /// host's screen was the dotted id. Two halls were photographed that way.
        /// <para>
        /// The hole is judged on the WORDS rather than on the value, because an en.json
        /// entry is an object with registers in it: a marked id in a real pack carries the
        /// marker, or a null, or an empty string, INSIDE a register while the object
        /// around it still looks like an entry, and a reading that stopped at the value
        /// accepted every one of those. So the walk asks each catalog for the sentence and
        /// keeps the entry that produced one, and has() IS that walk rather than a second
        /// reading that can answer differently.
        /// </para>
        /// <para>
        /// The table for all of it is in scripts/i18n/i18n_selftest.js, which the copy gate
        /// runs; this holds the reading itself in place.
        /// </para>
        /// </summary>
        [Fact]
        public void The_lookup_reads_a_pack_marker_and_every_other_non_sentence_as_absent()
        {
            var js = Lookup();

            // The marker is named once, as a constant, rather than spelled at the two
            // places that would then have to be kept in step.
            Assert.Contains("var MISSING_MARK = \"__missing\";", js);

            // WORDS: the one shape a host can read, and the test everything finishes on.
            Assert.Contains("function words(value) {", js);
            Assert.Contains("return typeof value === \"string\" && value !== MISSING_MARK && value.trim() !== \"\";", js);

            // What can HOLD words: an entry object, or a bare string for a pack that stores
            // one value per id. An array is an object to typeof and is not an entry.
            Assert.Contains("function usable(value) {", js);
            Assert.Contains("if (typeof value === \"string\") return words(value);", js);
            Assert.Contains("return !!value && typeof value === \"object\" && !Array.isArray(value);", js);

            // Every register is a candidate rather than the pick, so a register that is a
            // hole is stepped over instead of painted.
            Assert.Contains("function registersOf(entry) {", js);
            Assert.Contains("if (plainWanted() && usable(entry.plain)) out.push(entry.plain);", js);
            Assert.Contains("if (usable(entry.translation)) out.push(entry.translation);", js);
            Assert.Contains("if (usable(entry.lore)) out.push(entry.lore);", js);

            // A candidate answers only when it ends in words, plural included: a plural
            // object carrying neither this count's category nor "other" is a hole.
            Assert.Contains("if (typeof value === \"object\") value = pluralOf(entry, value, params);", js);
            Assert.Contains("if (words(value)) return value;", js);
            Assert.Contains("return words(value[picked]) ? value[picked] : value.other;", js);

            // Both arms of the walk ask for the WORDS, so a hole in the pack reaches the
            // English and a hole in the English reaches the id rather than the marker.
            Assert.Contains("text = resolveValue(active[id], params);", js);
            Assert.Contains("text = resolveValue(english[id], params);", js);
            Assert.Contains("if (text != null) { note(id); return { entry: english[id], text: text }; }", js);

            // T() and has() are one reading, or a caller that asks before it draws can be
            // told yes and then handed an id.
            Assert.Contains("var answer = answerFor(key, params);", js);
            Assert.Contains("return answerFor(String(id), params) != null;", js);

            // And the value-level shortcut is gone: nothing accepts an entry without
            // asking what it says.
            Assert.DoesNotContain("usable(active[key])", js);
            Assert.DoesNotContain("usable(active[id])", js);
        }

        // ------------------------------------------------------------ A2. the boot cloak

        /// <summary>
        /// The first painted frame of a window whose saved language is not English. The
        /// host says the language before the document exists; the bootstrap hangs a class
        /// on that; app.css hides the body's CHILDREN with visibility, so the layout is
        /// unchanged and the window keeps its own background; and the cloak comes off at
        /// the first of two moments, the second of which cannot fail.
        /// </summary>
        [Fact]
        public void A_saved_language_hides_the_first_frame_and_a_broken_pack_cannot_keep_it_hidden()
        {
            var html = Html();
            var css = Css();
            var js = AppJs();

            // Set only when the first frame would otherwise be wrong. English is not a
            // value the page has to test for: it simply never arrives.
            Assert.Contains("var pending = (typeof code === \"string\" && code && code !== \"en\") ? code : null;", html);
            Assert.Contains("if (!pending) return;", html);
            Assert.Contains("document.documentElement.classList.add(\"lang-pending\");", html);

            // The safety timeout is armed in the bootstrap, BEFORE anything that could
            // fail has been asked for, and it names no language so nothing can refuse it.
            Assert.Contains("setTimeout(function(){ window.BAKA_LANG_REVEAL(); }, 1500);", html);

            // The reveal is defined whether or not a cloak was hung, so app.js has one
            // name to call and no branch of its own.
            Assert.Contains("window.BAKA_LANG_REVEAL = function(said){", html);
            Assert.Contains("if (said && pending && String(said) !== pending) return false;", html);
            Assert.Contains("html.classList.remove(\"lang-pending\");", html);

            // Visibility, not display: every box is laid out at its real size while it is
            // hidden, so the reveal is a repaint and not a reflow. The body's children
            // rather than the body, so the window keeps its own background.
            Assert.Contains("html.lang-pending body>*{visibility:hidden}", css);
            Assert.DoesNotContain("html.lang-pending body{display:none", css);

            // Both call sites. The walk is the end of the boot chain whichever way it
            // went, so it names no language; applyLanguage names one, because a switch
            // into some third language is not the frame the cloak was hung for.
            Assert.Contains("try{if(window.BAKA_LANG_REVEAL)window.BAKA_LANG_REVEAL();}catch(_){}", js);
            Assert.Contains("try{if(window.BAKA_LANG_REVEAL)window.BAKA_LANG_REVEAL(tag);}catch(_){}", js);
        }

        // ------------------------------------------- A3. the select that painted an id

        /// <summary>
        /// renderPlayerMsgLang runs as a statement while app.js is still being evaluated,
        /// which is long before the catalog fetch resolves, and it writes into an option.
        /// T() answers an id it cannot look up with the id, and an option is not somewhere
        /// the static walker can put the English back, because the walker replaces what
        /// markup carries and this had wiped it.
        /// </summary>
        [Fact]
        public void The_player_message_select_never_paints_a_catalog_id()
        {
            var js = AppJs();
            var body = Body("renderPlayerMsgLang");

            // The English is read off the markup ONCE, before the first rebuild replaces
            // it: read on every call it would answer the first time and never again.
            Assert.Contains("const PLAYER_MSG_SAME_EN=(()=>{", js);
            Assert.Contains("$('#selPlayerMsgLang option[value=\"same\"]')", js);

            // has(), not ready(): a window whose en.json never arrived keeps a working
            // select with the languages it has rather than losing the setting altogether.
            Assert.Contains("window.I18N.has(\"settings.player_messages.same\")", body);
            Assert.Contains(":PLAYER_MSG_SAME_EN;", body);
            Assert.Contains("const opts=[{code:\"same\",name:sameWord}];", body);
            Assert.DoesNotContain("name:T(\"settings.player_messages.same\")", body);
        }

        // ------------------------------------------------ A4. one switch, one re-render

        /// <summary>
        /// The host raises LanguageChanged from inside the lang.set call, so the event
        /// reaches the window that asked BEFORE the reply it is waiting for does. Every
        /// guard downstream reads what the window is showing now, which at that instant is
        /// still the old language, so the event read as somebody else's switch and the
        /// window drew itself twice with a second pack fetch in between.
        /// </summary>
        [Fact]
        public void The_window_that_made_the_switch_does_not_follow_its_own_event()
        {
            var js = AppJs();
            var set = Body("langSet");

            Assert.Contains("mine:null,", js);
            // Claimed BEFORE the call, because the event can arrive before the answer.
            var claim = set.IndexOf("LANG.mine=code;", StringComparison.Ordinal);
            var call = set.IndexOf("await rpc(\"lang.set\"", StringComparison.Ordinal);
            Assert.True(claim >= 0 && call > claim, "the claim is not taken before the call");

            // A call that did not land changed nothing and raised nothing, so the claim
            // goes with it: left standing it would swallow another window's next switch.
            Assert.Contains("if(r===FAIL){if(LANG.mine===code)LANG.mine=null;return false;}", set);

            // Consumed once, and before the guard that reads the current language.
            var handler = js.Substring(js.IndexOf("Native.on(\"lang.changed\"", StringComparison.Ordinal));
            handler = handler.Substring(0, handler.IndexOf("\n});", StringComparison.Ordinal));
            var drop = handler.IndexOf("if(LANG.mine&&d.code===LANG.mine){LANG.mine=null;return;}", StringComparison.Ordinal);
            var current = handler.IndexOf("if(d.code===langCurrent()) return;", StringComparison.Ordinal);
            Assert.True(drop >= 0, "the window still follows its own switch");
            Assert.True(current > drop, "the current-language guard runs before the claim is read");
        }

        // ------------------------------------------------------ A5. current, and behind

        /// <summary>
        /// Current and behind are not alternatives. The row showed only the first, so the
        /// one host who most needs to know the words on screen come from an older pack is
        /// the host reading them, and they were the only one the menu did not tell.
        /// </summary>
        [Fact]
        public void The_current_language_on_an_older_pack_says_both_things()
        {
            var body = Body("langRowLine");
            Assert.Contains("(l.installed&&!l.matchesApp&&!l.builtIn)", body);
            Assert.Contains("?T(\"lang.row.current_older_pack\",{version:l.installedVersion||\"\"})", body);
            Assert.Contains(":T(\"lang.row.current\");", body);

            var catalog = Catalog();
            Assert.True(catalog.ContainsKey("lang.row.current_older_pack"));
            Assert.Equal("Current, using the pack from {version}",
                catalog["lang.row.current_older_pack"].GetProperty("lore").GetString());
            // The same slot the row below it uses, so the two sentences stay one idea.
            Assert.Equal("Using the pack from {version}",
                catalog["lang.row.older_pack"].GetProperty("lore").GetString());
        }

        // ------------------------------------------------------- B2. the unit letters

        /// <summary>
        /// "4h 32m" was built by hand at four call sites, each spelling the two unit
        /// letters into the middle of a string, which put two English words where no
        /// translator could reach them. One formatter now, named slots, and the letters in
        /// the catalog. The English is byte identical: hours plain, minutes padded to two.
        /// </summary>
        [Fact]
        public void The_uptime_units_come_out_of_the_catalog_and_the_english_is_unchanged()
        {
            var js = AppJs();
            var body = Body("uptimeSpan");

            Assert.Contains("T(\"common.uptime.hm\",", body);
            Assert.Contains("{hours:String(Math.floor(total/60)),minutes:String(total%60).padStart(2,\"0\")}", body);

            var catalog = Catalog();
            Assert.Equal("{hours}h {minutes}m", catalog["common.uptime.hm"].GetProperty("lore").GetString());

            // Every site that used to spell them is on the formatter now. skDur is the one
            // duration that is NOT this: it goes through Intl.DurationFormat and keeps its
            // hand rolled floor for a window whose lookup never arrived.
            Assert.Contains("return running?uptimeSpan(upMin):\"\";", js);
            Assert.Contains("return uptimeSpan((Date.now()-S.upSince)/60000);", js);
            Assert.Contains("T(\"hearth.card.hstate.running\",{span:uptimeSpan(upMin)})", js);
            Assert.Contains("T(\"hearth.card.hstate.running\",{span:uptimeSpan(up/60000)})", js);

            // And nowhere else: a fifth site spelling "h " back into a string is the
            // regression this number is here to catch. The two left are inside skDur.
            Assert.Equal(2, Regex.Matches(js, "\"h \"").Count);
        }

        // --------------------------------------------------------- B1. the player table

        /// <summary>
        /// Three of the five columns on the Statistics player table were narrower than
        /// their OWN ENGLISH headers at the app's minimum window, and an English header is
        /// one unbreakable word, so it overflowed into the column beside it while Russian
        /// and Japanese broke in half instead. The widths are measured from the widest
        /// rendering of each header in five languages; the wrapper is the floor under it.
        /// </summary>
        [Fact]
        public void The_skald_player_table_holds_its_own_headers_and_scrolls_rather_than_crush_them()
        {
            var html = Html();
            var css = Css();

            var thead = html.Substring(html.IndexOf("<th style=\"width:24%\" data-i18n=\"vikings.col.name\"", StringComparison.Ordinal));
            thead = thead.Substring(0, thead.IndexOf("</tr>", StringComparison.Ordinal));

            // The four that carry a width, and the fifth takes what is left.
            var widths = Regex.Matches(thead, "width:([0-9.]+)%")
                              .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(new[] { 24.0, 20.0, 19.0, 16.5 }, widths);
            Assert.Contains("<th data-i18n=\"vikings.col.seen\">", thead);

            // At the 480px floor those four are 115.2, 96, 91.2 and 79.2, and the fifth
            // takes 98.4. The needs, measured in chromium with a Range over each header in
            // English, Russian, Japanese and both Chinese, plus the 24px of cell padding:
            // 84, 96, 91, 77 and 97. Every one clears.
            var floor = 480.0;
            var needs = new[] { 84.0, 96.0, 91.0, 77.0 };
            for (var i = 0; i < widths.Count; i++)
                Assert.True(widths[i] / 100.0 * floor >= needs[i],
                    "column " + i + " gives " + (widths[i] / 100.0 * floor) + " and needs " + needs[i]);
            Assert.True((100 - widths.Sum()) / 100.0 * floor >= 97.0, "Last seen is short at the floor");

            // The floor itself, and what happens below it.
            Assert.Contains(".sk-players table{table-layout:fixed;min-width:480px}", css);
            Assert.Contains(".sk-players .sk-ptable{overflow-x:auto}", css);
            Assert.Contains("<div class=\"sk-ptable\">", html);
        }

        // ------------------------------------------------ B3 / B4 / B5 / B6. the layout

        /// <summary>
        /// Four rules that no translation could have got round, each measured in a browser
        /// before it was written. The numbers are in the comments beside them in app.css.
        /// </summary>
        [Fact]
        public void The_four_layout_rules_are_in_place_with_nothing_capping_a_label()
        {
            var css = Css();

            // B3: a pill, a filter pill and a condition-bar button size to their own words
            // on one line. "Update available" is 101px of ink in a 92px budget and "Update
            // BakaLoader" 137px in a 120px one, both in ENGLISH.
            Assert.Contains("white-space:nowrap;max-width:none;min-width:34px;text-align:center}", css);
            Assert.Contains(".holdbar .hbacts .btn{max-width:none;flex:none}", css);
            Assert.Equal(2, Regex.Matches(css, Regex.Escape("white-space:nowrap;max-width:none;min-width:34px")).Count);

            // B4: the rail clips a label too long for it rather than painting it over the
            // hall, and the active item's bar was moved back inside the rail so the clip
            // costs nothing. The rail's content box is 91px and the item is 76, so the item
            // starts at 7.5 and -7px puts the whole 3px bar inside.
            Assert.Matches(new Regex(@"\.side\{[^}]*overflow:hidden;", RegexOptions.Singleline), css);
            Assert.Contains(".navitem::before{content:\"\";position:absolute;left:-7px;", css);

            // B5: the condition bar's title was the one label of its size with no
            // per-language rule at all, drawing a twelve character Japanese title at 9px.
            Assert.Contains("html[data-lang=\"ja\"] .holdbar .hbtitle,", css);
            Assert.Contains("html[data-lang=\"zh-Hans\"] .holdbar .hbtitle,", css);
            Assert.Contains("html[data-lang=\"zh-Hant\"] .holdbar .hbtitle{", css);
            Assert.Contains("html[data-lang=\"ru\"] .holdbar .hbtitle{", css);

            // B6: the Russian rail holds 10px at every height. The two short-viewport
            // step-downs are gone: the longest of the nine needs 68.5 of its 76px at 10px
            // with .6px of tracking, measured in the real rail at 1024x680.
            Assert.Matches(new Regex("html\\[data-lang=\"ru\"\\] \\.navitem \\.lbl\\{\\s*font-size:10px; letter-spacing:\\.6px;\\s*\\}"), css);
            Assert.DoesNotContain("html[data-lang=\"ru\"] .navitem .lbl{ font-size:9.5px", css);
            Assert.DoesNotContain("html[data-lang=\"ru\"] .navitem .lbl{ font-size:9px", css);
        }

        // ------------------------------------------------------------- C2. the question

        /// <summary>
        /// The one tap sent the bare command, and the bare command strikes every hostile in
        /// every zone the server has loaded for every player online. The question offers
        /// the three scopes the plugin actually serves, and the command it builds is the
        /// plugin's own spelling.
        /// </summary>
        [Fact]
        public void The_kill_sweep_asks_before_it_runs_and_builds_only_what_the_plugin_serves()
        {
            var js = AppJs();
            var built = Body("killAllCommand");

            // The palette opens the question rather than sending anything.
            Assert.Matches(new Regex("\\}else if\\(cmd===\"kill_monsters\"\\)\\{\\s*killAllModal\\(\\);"), js);
            Assert.DoesNotContain("sendConsole(\"baka_killall\"", js);

            // The three lines, exactly as Resources/KillAll/BakaKillAllPlan.cs parses them.
            Assert.Contains("return {cmd:\"baka_killall near \"+who+\" \"+metres};", built);
            Assert.Contains("return {cmd:\"baka_killall \"+name};", built);
            Assert.Contains("return {cmd:\"baka_killall\"};", built);

            // A refusal is a catalog id, never a sentence: this function does not know what
            // language the window is reading.
            foreach (var id in new[] { "pal.kill.problem.no_player", "pal.kill.problem.radius",
                                       "pal.kill.problem.no_creature", "pal.kill.problem.creature_spaces" })
                Assert.Contains("{problemId:\"" + id + "\"}", built);

            // A player name may hold spaces, because the plugin takes the radius off the
            // END of the line; a prefab name may not, because it takes exactly one word.
            Assert.DoesNotContain("who.replace", built);
            Assert.Contains("if(/\\s/.test(name)) return {problemId:\"pal.kill.problem.creature_spaces\"};", built);

            // The dialog rebuilds from state on a language switch, so what is typed
            // survives one, and it is reachable from the preview without a server.
            var modal = Body("killAllModal");
            Assert.Contains("const again=()=>{", modal);
            Assert.Contains("      again);", modal);

            // With nobody online the radius scope cannot be served, so a remembered "near"
            // goes back to the default rather than standing checked on a greyed row that
            // then answers "Choose the player to measure the radius from" when it is
            // pressed. One condition, one sentence.
            Assert.Contains("if(KILL_SCOPE.scope===\"near\"&&!online.length) KILL_SCOPE.scope=\"everywhere\";", modal);
            Assert.Contains("killAll:()=>{killAllModal();return modalIsOpen();},", js);
            Assert.Contains("killAllSaid:reply=>", js);
        }

        // ------------------------------------------------------------ C1. the reply

        /// <summary>
        /// Seven endings, seven sentences, and not one of them says the sweep ran unless it
        /// did. The reading itself is driven as a table by
        /// scripts/ui/killall_reply_selftest.js, which the copy gate runs; this holds the
        /// wording and the rule that every reply has one.
        /// </summary>
        [Fact]
        public void Every_ending_the_sweep_can_have_is_worded_and_none_of_them_claims_success()
        {
            var js = AppJs();
            var toast = Body("killAllToast");
            var catalog = Catalog();

            foreach (var id in new[] { "pal.kill.toast.complete", "pal.kill.toast.none",
                                       "pal.kill.toast.started", "pal.kill.toast.busy",
                                       "pal.kill.toast.stopped", "pal.kill.toast.silent",
                                       "pal.kill.toast.unreadable", "pal.console.refused.toast" })
            {
                Assert.True(catalog.ContainsKey(id), "the English catalog has no " + id);
                Assert.Contains("T(\"" + id + "\"", toast);
            }

            // The kind that means "it ran and here is what it did" is the only one that
            // claims a finished sweep, and it carries the three numbers.
            Assert.Equal("slain", catalog["pal.kill.toast.complete"].GetProperty("plural").GetString());
            var done = catalog["pal.kill.toast.complete"].GetProperty("lore");
            Assert.Equal("{slain} hostile slain, {unreachable} out of reach, {spared} spared",
                done.GetProperty("one").GetString());
            Assert.Equal("{slain} hostiles slain, {unreachable} out of reach, {spared} spared",
                done.GetProperty("other").GetString());

            // A sweep that threw part way carries them too. What it reached before it threw
            // is work that really happened to somebody's base, and the reader has the
            // numbers: dropping them left a host reading "it stopped part way" with no idea
            // whether one creature fell or four hundred did.
            Assert.Equal("slain", catalog["pal.kill.toast.stopped"].GetProperty("plural").GetString());
            foreach (var register in new[] { "lore", "plain" })
            {
                var stopped = catalog["pal.kill.toast.stopped"].GetProperty(register);
                foreach (var category in new[] { "one", "other" })
                {
                    var sentence = stopped.GetProperty(category).GetString() ?? string.Empty;
                    foreach (var slot in new[] { "{slain}", "{unreachable}", "{spared}" })
                        Assert.Contains(slot, sentence);
                }
            }
            Assert.Contains("{slain:read.slain,unreachable:read.unreachable,spared:read.spared,pluralValue:read.slain}",
                toast);

            // The toast that used to fire on delivery is gone from the catalog as well as
            // from the call site, so nothing can quietly go back to asking for it.
            Assert.False(catalog.ContainsKey("pal.kill_monsters.done.toast"));

            // The rule, in the suite as well as in the gate: the second argument to
            // sendConsole is what to say about the reply, and it is a function.
            Assert.Contains("async function sendConsole(cmd,read){", js);
            Assert.Contains("if(typeof read===\"function\"){", js);
            Assert.Contains("sendConsole(built.cmd,reply=>killAllToast(killAllReply(reply)));", js);
            // The world save reads its reply for the same reason: a modded server answers
            // an unknown verb on the same channel it answers a real one.
            Assert.Contains("sendConsole(\"save\",reply=>consoleRefused(reply)", js);

            // The plugin answers a line it could not parse with its usage sentence and
            // strikes nothing. That is a refusal, not a shape the reader has not met.
            Assert.Contains("if(/^Usage:\\s*baka_killall\\b/i.test(said)) return {kind:\"refused\"};", js);
        }

        /// <summary>
        /// Every route to the sweep goes through the question. The palette's entry was the
        /// one this pass gated, but the server-console picker lists baka_killall as a
        /// complete command, and a complete command in that list is sent on the click: one
        /// tap, the whole blast radius, no dialog and no reader on the answer. The row
        /// opens the same dialog now, so there is no second way in.
        /// </summary>
        [Fact]
        public void The_console_picker_cannot_send_the_bare_sweep_on_one_tap()
        {
            var js = AppJs();

            Assert.Contains("{cmd:\"baka_killall\",args:\"\",descId:\"pal.console.cmd.killall\",asks:\"killall\"},", js);
            Assert.Contains("if(r.dataset.asks===\"killall\"){modalClose();killAllModal();return;}", js);

            // The row carries the marker into the markup, or the handler above reads an
            // attribute nothing sets and the click falls through to the send.
            Assert.Contains("${c.asks?` data-asks=\"${esc(c.asks)}\"`:\"\"}", js);

            // And nowhere in the page does the bare command go out on its own.
            Assert.DoesNotContain("sendConsole(\"baka_killall\")", js);
        }
    }
}
