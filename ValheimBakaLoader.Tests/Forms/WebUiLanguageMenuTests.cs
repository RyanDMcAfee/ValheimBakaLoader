using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The globe: the button in the title bar, the menu under it, the row that turns into a
    /// download, and the one road from a row to the whole window being drawn again in
    /// another language.
    /// <para>
    /// Four of these guard a way to lie to a host rather than a way to look wrong, and each
    /// says which: a cancel that claims to have stopped something it did not, a first frame
    /// in the wrong language, a menu that reaches the release page on a boot nobody asked
    /// for, and a sentence captured once instead of asked for every time it is drawn.
    /// </para>
    /// </summary>
    public class WebUiLanguageMenuTests : IDisposable
    {
        private readonly List<string> _scratch = new();

        public void Dispose()
        {
            foreach (var folder in _scratch)
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                catch (IOException) { /* a scanner still holding a temp folder */ }
            }
        }

        private static string AppJs() => AppSourceTree.Web("app.js");

        private static string Html() => AppSourceTree.Web("index.html");

        private static string Css() => AppSourceTree.Web("app.css");

        private static string Body(string opener) => WebUiLanguageGroundworkTests.Body(AppJs(), opener);

        private static Dictionary<string, JsonElement> Catalog()
        {
            var text = File.ReadAllText(Path.Combine(
                AppSourceTree.RepoRoot(), "ValheimBakaLoader", "WebUI", "i18n", "en.json"));
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("keys").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone());
        }

        private static string Lore(string id)
        {
            var entry = Catalog()[id];
            var lore = entry.GetProperty("lore");
            return lore.ValueKind == JsonValueKind.String ? lore.GetString() : lore.ToString();
        }

        // ------------------------------------------------------------------ A. the title bar

        /// <summary>
        /// SPEC item 17. The title bar is a drag handle, so anything interactive put there
        /// has to be excluded from the drag or it moves the window instead of being pressed.
        /// The markup is parsed and walked rather than grepped, because what matters is an
        /// ANCESTRY: the globe carries no excluded class itself, its wrapper does, and
        /// e.target.closest(TB_NO_DRAG) is what actually decides.
        /// </summary>
        [Fact]
        public void Every_interactive_thing_in_the_title_bar_is_out_of_the_drag()
        {
            var excluded = Regex.Match(AppJs(), @"const TB_NO_DRAG=""([^""]+)"";").Groups[1].Value
                .Split(',').Select(s => s.Trim().TrimStart('.')).ToList();
            Assert.Contains("tb-interactive", excluded);

            var bar = TitleBar();
            var interactive = bar.DescendantsAndSelf().Where(Interactive).ToList();
            Assert.True(interactive.Count >= 5,
                "the title bar lost its interactive elements: " + interactive.Count);

            foreach (var el in interactive)
            {
                var covered = el.AncestorsAndSelf().Any(a =>
                    Classes(a).Any(c => excluded.Contains(c)));
                Assert.True(covered,
                    "a press on this would drag the window: " + el.Name + " " +
                    (el.Attribute("id")?.Value ?? el.Attribute("class")?.Value));
            }
        }

        /// <summary>
        /// Left of the Command chip, which is where the SPEC puts it and where a host who
        /// knows one title bar expects the other. Asserted on the order of the markup, since
        /// the bar is a flex row with no ordering of its own.
        /// </summary>
        [Fact]
        public void The_globe_sits_left_of_the_command_chip()
        {
            var html = Html();
            var globe = html.IndexOf("id=\"langBtn\"", StringComparison.Ordinal);
            var chip = html.IndexOf("id=\"cmdchip\"", StringComparison.Ordinal);
            var spacer = html.IndexOf("class=\"tb-spacer\"", StringComparison.Ordinal);

            Assert.True(globe > 0 && chip > 0, "the title bar is missing one of the two");
            Assert.True(spacer < globe, "the globe is on the wordmark's side of the bar");
            Assert.True(globe < chip, "the globe was put to the right of the Command chip");
        }

        /// <summary>
        /// A menu button says so, and says whether it is open. Both are read by a screen
        /// reader and neither is decoration: aria-expanded is the only thing that tells a
        /// reader the press did anything at all.
        /// </summary>
        [Fact]
        public void The_globe_announces_itself_as_a_menu_and_says_when_it_is_open()
        {
            var html = Html();
            var button = Regex.Match(html, @"<div class=""langbtn"" id=""langBtn""[^>]*>").Value;

            Assert.Contains("role=\"button\"", button);
            Assert.Contains("tabindex=\"0\"", button);
            Assert.Contains("aria-haspopup=\"menu\"", button);
            Assert.Contains("aria-expanded=\"false\"", button);
            Assert.Contains("data-i18n-title=\"lang.btn.title\"", button);
            Assert.Contains("data-i18n-aria=\"lang.menu.aria\"", button);
            Assert.Contains("id=\"langMenu\" role=\"menu\"", html);

            // And the two handlers that keep it true.
            Assert.Contains("btn.setAttribute(\"aria-expanded\",\"false\")", Body("function langMenuClose(){"));
            Assert.Contains("btn.setAttribute(\"aria-expanded\",\"true\")", Body("function langMenuOpen(){"));
        }

        // ------------------------------------------------------------------ B. the rows

        /// <summary>
        /// Six states, one line each, and every one of them a catalog entry. The ORDER is
        /// the test: a row that is both installed and older has to read as older, and a row
        /// that is current has to read as current whatever else is true of it, or the menu
        /// tells a host about the pack when they asked about the language.
        /// </summary>
        [Fact]
        public void A_row_says_which_of_its_six_states_it_is_in_and_in_the_right_order()
        {
            var body = Body("function langRowLine(l){");
            var keys = Catalog();

            var order = new[]
            {
                "lang.row.current", "lang.row.built_in", "lang.row.installed",
                "lang.row.older_pack", "lang.row.not_installed", "lang.row.offline",
                "lang.row.not_published",
            };

            var at = -1;
            foreach (var id in order)
            {
                Assert.True(keys.ContainsKey(id), "the catalog has no words for " + id);
                var here = body.IndexOf("T(\"" + id + "\"", StringComparison.Ordinal);
                Assert.True(here > at, "the row states are no longer asked in order at " + id);
                at = here;
            }

            // The two that carry a value carry it as a named slot, never glued on.
            Assert.Contains("T(\"lang.row.older_pack\",{version:l.installedVersion||\"\"})", body);
            Assert.Contains("T(\"lang.row.not_installed\",{size:fmtBytes(l.bytes||0)})", body);

            // And the size is formatted by the lookup, so it reads in the host's own digits
            // and separators rather than in English ones.
            Assert.Contains("\"size\": \"text\"", File.ReadAllText(Path.Combine(
                AppSourceTree.RepoRoot(), "ValheimBakaLoader", "WebUI", "i18n", "en.json")));
        }

        /// <summary>
        /// The machine mark is a tag on the row and a note under the menu, and it is NEVER a
        /// toast. A toast is about something that just happened; this is a standing fact
        /// about the words on screen, and a host who reads a machine translation is told once
        /// where they can see it rather than every time they open something.
        /// </summary>
        [Fact]
        public void The_partial_and_machine_notes_stand_in_the_menu_and_never_toast()
        {
            var menu = Body("function renderLangMenu(){");
            var js = AppJs();

            Assert.Contains("T(\"lang.note.partial\",{count:missing})", menu);
            Assert.Contains("T(\"lang.note.machine\")", menu);
            Assert.Contains("T(\"lang.note.checks_off\")", menu);
            // The footer note is held behind the switch being OFF, not behind it existing.
            Assert.Contains("LANG.list.checkEnabled===false", menu);

            foreach (var id in new[] { "lang.note.partial", "lang.note.machine", "lang.note.checks_off" })
                Assert.DoesNotContain("toast(\"ᛦ \"+T(\"" + id, js);

            // The same sentence, on the sidebar mark, and on a title rather than in a toast.
            var dot = Body("function renderLangDot(){");
            Assert.Contains("T(\"lang.note.partial\",{count:missing})", dot);
            Assert.Contains("dot.title=said", dot);
            Assert.Contains("dot.setAttribute(\"aria-label\",said)", dot);
            Assert.DoesNotContain("toast(", dot);
        }

        /// <summary>
        /// The plural on the partial note is a real plural family, not an s glued to a word.
        /// One new line and two new lines are different sentences in the languages this is
        /// being translated into, and the entry has to carry both for English too.
        /// </summary>
        [Fact]
        public void The_partial_note_is_a_plural_family()
        {
            var entry = Catalog()["lang.note.partial"];

            Assert.Equal("count", entry.GetProperty("plural").GetString());
            var lore = entry.GetProperty("lore");
            Assert.Equal("{count} new line is still in English", lore.GetProperty("one").GetString());
            Assert.Equal("{count} new lines are still in English", lore.GetProperty("other").GetString());
        }

        /// <summary>
        /// The menu is wide enough for the longest status line to sit beside the machine tag
        /// on ONE line. This is a pin on a MEASURED number rather than a taste: at 330px the
        /// tag took about 130px of the row and left the status column too narrow for "Not
        /// downloaded, 2.2 MB", which broke between the number and its unit, so a host read a
        /// size with the MB on the next line. A screenshot alone would not have said which
        /// rows wrapped; the browser probe measures every .lm-sub against its own line-height
        /// and this holds the number that probe was run against. Narrow it and measure again.
        /// </summary>
        [Fact]
        public void The_menu_is_wide_enough_for_a_status_line_beside_the_machine_tag()
        {
            var menu = Regex.Match(Css(), @"\.langmenu\{[^}]*\}").Value;
            var width = Regex.Match(menu, @"width:(\d+)px").Groups[1].Value;

            Assert.Equal(380, int.Parse(width));
            // And it still fits the narrowest window the frame will allow.
            Assert.Contains("DesignMinWidth = 1024",
                File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(),
                    "ValheimBakaLoader", "Forms", "BlendWindow.cs")));
        }

        /// <summary>
        /// Two of the six states have no pack on disk and none to fetch either: the release
        /// page has published none for this version, or the page could not be reached at
        /// all. Both used to be drawn as menu items, with a tab stop and a hover, and both
        /// answered a press with a failure toast saying what the row already said. They are
        /// reasons now, not controls.
        /// <para>
        /// The machine tag stands down on them for a second reason of its own: a language
        /// nothing has published a pack for cannot have a machine translated pack, so the
        /// tag was describing a file that does not exist, and it was doing it in the ninety
        /// pixels the longest sentence in this menu needs to stay on one line.
        /// scripts/ui/lang_row_probe.js measures that in a browser, every state of the row;
        /// this holds the shape the measurement was made of.
        /// </para>
        /// </summary>
        [Fact]
        public void A_row_with_no_pack_behind_it_is_a_reason_rather_than_a_control()
        {
            var app = AppJs();

            Assert.Contains("function langRowInert(l){", app, StringComparison.Ordinal);
            Assert.Contains("return !l.builtIn&&!l.installed&&!l.available&&l.code!==langCurrent();", app, StringComparison.Ordinal);

            // Drawn without the role, without the tab stop and without the tag.
            Assert.Contains("if(!failed&&langRowInert(l))", app, StringComparison.Ordinal);
            Assert.Contains("<div class=\"lm-row off\" data-lang-inert=", app, StringComparison.Ordinal);

            // The delegated handlers only ever reach a [data-lang-row], so an inert row is
            // out of both of them by construction; langPick refuses it as well, for a press
            // that arrives from a stale DOM under a redraw.
            Assert.Contains("if(langRowInert(row)) return false;", app, StringComparison.Ordinal);

            // And it does not light up under the pointer.
            Assert.Contains(".lm-row.off{cursor:default", Css(), StringComparison.Ordinal);
            Assert.Contains(".lm-row.off:hover{background:transparent", Css(), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ C. the download row

        /// <summary>
        /// Determinate the moment the host has reported a byte total, and indeterminate
        /// before that. A bar sitting at zero while the release page is being resolved reads
        /// as stuck; a sweeping band reads as working, and it is the honest shape because
        /// nothing knows the size yet.
        /// </summary>
        [Fact]
        public void The_bar_is_determinate_only_once_there_is_a_total_to_be_a_fraction_of()
        {
            var body = Body("function langProgressHtml(code){");

            Assert.Contains("const total=Number(p.bytesTotal)||0;", body);
            Assert.Contains("total>0", body);
            Assert.Contains("aria-valuenow=\"${pct}\"", body);
            Assert.Contains("class=\"lm-bar indeterminate\"", body);
            Assert.Contains("role=\"progressbar\"", body);
            Assert.Contains("aria-label=\"${esc(T(\"lang.progress.aria\",{language:langNameOf(code)}))}\"", body);

            // The indeterminate arm carries NO aria-valuenow: a value a reader can announce
            // is a claim about how far along this is, and nothing knows that yet.
            var indeterminate = body.Substring(body.IndexOf("lm-bar indeterminate", StringComparison.Ordinal));
            var untilFill = indeterminate.Substring(0, indeterminate.IndexOf("lm-fill", StringComparison.Ordinal));
            Assert.DoesNotContain("aria-valuenow", untilFill);

            // The sweep is CSS, so it costs nothing when the menu is shut.
            Assert.Contains(".lm-bar.indeterminate .lm-fill{", Css());
        }

        /// <summary>
        /// Cancel stays on screen once it is too late and goes grey with the reason on it,
        /// rather than disappearing. A button that vanishes at the moment a host reaches for
        /// it leaves them pressing at nothing and wondering what they hit.
        /// <para>
        /// There are TWO reasons it goes grey and only one of them is worth a tooltip. The
        /// pack being installed is a thing the host needs told, so it is worded. A cancel
        /// already asked for and not yet answered is simply on its way, and a sentence about
        /// it would be a second explanation of the press they just made; that arm goes grey
        /// so a report landing in the gap cannot redraw a live button and invite a second
        /// press at something already stopping.
        /// </para>
        /// </summary>
        [Fact]
        public void Cancel_is_disabled_with_its_reason_once_the_pack_is_being_installed()
        {
            var body = Body("function langProgressHtml(code){");

            Assert.Contains("const tooLate=p.phase===\"installing\";", body);
            Assert.Contains("const asked=LANG.cancelling===code;", body);

            // Grey for either reason.
            Assert.Contains("${tooLate||asked?\" disabled\":\"\"}", body);
            // Worded for exactly one of them: the tooltip hangs off tooLate alone.
            Assert.Contains("${tooLate?` title=\"${esc(T(\"lang.cancel.too_late\"))}\"`:\"\"}", body);
            Assert.DoesNotContain("asked?` title=", body);

            Assert.Equal("This pack is being installed and cannot be stopped now",
                Lore("lang.cancel.too_late"));
        }

        /// <summary>
        /// THE CANCEL HONESTY. The service answers whether the press actually stopped
        /// anything, and the page says "could not be stopped" ONLY on that answer being
        /// false. Nothing here writes a literal true, and nothing here toasts "cancelled" off
        /// the press: the cancelled toast comes from the download's own ending, which is the
        /// only place that knows the staging folder went away.
        /// </summary>
        [Fact]
        public void The_page_says_a_cancel_failed_only_when_the_host_said_so()
        {
            var cancel = Body("async function langCancel(code){");

            Assert.Contains("const r=await rpc(\"lang.cancel\",{code});", cancel);
            Assert.Contains("if(!r.cancelled){", cancel);
            Assert.Contains("T(\"lang.toast.too_late\",{language:langNameOf(code)})", cancel);
            Assert.DoesNotContain("cancelled:true", cancel);
            Assert.DoesNotContain("cancelled=true", cancel);
            // The cancelled toast is NOT here. It belongs to the ending, one level up.
            Assert.DoesNotContain("lang.toast.cancelled", cancel);

            var download = Body("async function langDownload(code){");
            Assert.Contains("if(r.cancelled){", download);
            Assert.Contains("T(\"lang.toast.cancelled\")", download);
            Assert.Equal("Download cancelled. Nothing changed.", Lore("lang.toast.cancelled"));
        }

        /// <summary>
        /// A failure keeps the row, the reason and a way to try again. A toast is gone in five
        /// seconds; the row is what a host comes back to.
        /// </summary>
        [Fact]
        public void A_failed_download_leaves_the_reason_on_the_row_and_a_try_again_beside_it()
        {
            var row = Body("function langRowHtml(l){");
            var download = Body("async function langDownload(code){");

            Assert.Contains("LANG.failed&&LANG.failed.code===l.code", row);
            Assert.Contains("langReasonText(failed.reasonId,failed.reasonParams)||T(\"common.error.unknown\")", row);
            Assert.Contains("data-lang-retry=\"${esc(l.code)}\"", row);
            Assert.Contains("T(\"lang.retry\")", row);

            Assert.Contains("LANG.failed={code,reasonId:r.reasonId,reasonParams:r.reasonParams};", download);
            Assert.Contains("T(\"lang.toast.failed\",{language:langNameOf(code)", download);

            // The machine tag stands down while the row carries a failure, and ONLY then. A
            // failing row already has a button beside it, and with the tag there too the
            // browser probe measured the reason at 99px and six lines deep at three words a
            // line; without it, 236px and two. A sentence a host has to work at is not a
            // sentence that told them anything.
            Assert.Contains("const machine=(l.status===\"machine\"&&!l.builtIn&&!failed)", row);

            // And the Try again runs the same download rather than a second road into it.
            Assert.Contains("langDownload(retry.getAttribute(\"data-lang-retry\"))", AppJs());
        }

        /// <summary>
        /// Cancel and Try again live INSIDE the row they belong to, so a press on either one
        /// must not also pick the language underneath. This is the nested-touchable bleed,
        /// and on a menu it would mean pressing Cancel switched the interface.
        /// </summary>
        [Fact]
        public void A_press_on_cancel_or_try_again_never_reaches_the_row_beneath_it()
        {
            var js = AppJs();
            var handler = js.Substring(js.IndexOf("$(\"#langMenu\")?.addEventListener(\"click\"", StringComparison.Ordinal));
            handler = handler.Substring(0, handler.IndexOf("});", StringComparison.Ordinal));

            var cancel = handler.IndexOf("data-lang-cancel", StringComparison.Ordinal);
            var retry = handler.IndexOf("data-lang-retry", StringComparison.Ordinal);
            var row = handler.IndexOf("data-lang-row", StringComparison.Ordinal);

            Assert.True(cancel > 0 && retry > 0 && row > 0, "the menu handler lost one of its three");
            Assert.True(cancel < row && retry < row,
                "the row is picked before the buttons inside it are, so a cancel also switches");
            // Each of the two returns rather than falling through to the row.
            Assert.Contains("langCancel(cancel.getAttribute(\"data-lang-cancel\"));\n    return;", handler);
            Assert.Contains("{langDownload(retry.getAttribute(\"data-lang-retry\"));return;}", handler);
        }

        // ------------------------------------------------------------------ D. the boot

        /// <summary>
        /// SPEC item 20. The English catalog is loaded FIRST and held, because it is the
        /// fallback every other catalog is read over. Then the saved language, and only then
        /// the walk. Walking between the two is the whole defect: the document would be
        /// painted in English and painted again a fetch later, which is the flash.
        /// </summary>
        [Fact]
        public void The_boot_loads_the_pack_between_the_english_catalog_and_the_only_walk()
        {
            var js = AppJs();
            var boot = js.Substring(js.IndexOf("const I18N_READY=", StringComparison.Ordinal));
            boot = boot.Substring(0, boot.IndexOf("window.BAKA_I18N_READY", StringComparison.Ordinal));

            Assert.Contains(".then(cat=>{EN_CATALOG=cat;window.I18N.load(cat,\"en\");return langBootCatalog();})", boot);
            Assert.Contains(".then(()=>walk(true))", boot);

            // Exactly one walk on the road that worked. Two is the flash.
            Assert.Equal(1, Regex.Matches(boot, Regex.Escape("walk(true)")).Count);
            Assert.Equal(1, Regex.Matches(boot, Regex.Escape("walk(false)")).Count);

            var english = boot.IndexOf("window.I18N.load(cat,\"en\")", StringComparison.Ordinal);
            var pack = boot.IndexOf("langBootCatalog()", StringComparison.Ordinal);
            var walk = boot.IndexOf("walk(true)", StringComparison.Ordinal);
            Assert.True(english < pack && pack < walk,
                "the order is English, then the pack, then one walk");
        }

        /// <summary>
        /// And what the boot actually does with the answer: both attributes before the words,
        /// because Chromium picks its Han face off lang and every measurement after that
        /// reads the face the choice landed on; the faces before the words, because a catalog
        /// swapped in ahead of the rules that draw it paints one frame in a face that cannot.
        /// </summary>
        [Fact]
        public void The_boot_sets_the_attributes_and_the_faces_before_the_words()
        {
            var body = Body("async function langBootCatalog(){");

            Assert.Contains("Native.call(\"lang.status\",{})", body);
            Assert.Contains("if(!st.stringsUrl||st.current===\"en\") return false;", body);

            var faces = body.IndexOf("langInjectFonts(st.fonts)", StringComparison.Ordinal);
            var attrs = body.IndexOf("setLanguageAttributes(st.current)", StringComparison.Ordinal);
            var words = body.IndexOf("window.I18N.load(cat,st.current)", StringComparison.Ordinal);
            Assert.True(faces > 0 && attrs > 0 && words > 0, "the boot no longer does all three");
            Assert.True(faces < words && attrs < words, "the words were loaded first");

            // It never throws: a pack that cannot be read is a window in English, which is
            // exactly what the window was before any of this existed.
            Assert.Contains("catch(e){", body);
            Assert.Contains("return false;", body);
        }

        /// <summary>
        /// THE BOOT ASKS FOR NOTHING. lang.list is the call that is allowed to reach the
        /// release page, and it is made when the host opens the globe or expands the card
        /// that holds the player-message select. A window that merely started has asked
        /// nothing, and must make no request on the host's behalf.
        /// </summary>
        [Fact]
        public void Nothing_on_the_boot_path_asks_for_the_list_that_may_reach_the_network()
        {
            var js = AppJs();

            Assert.DoesNotContain("lang.list", Body("async function langBootCatalog(){"));

            // Every caller of langRefresh, named. Three, and each is a host acting.
            var callers = Regex.Matches(js, @"[^\w$.]langRefresh\(\)")
                .Cast<Match>().Select(m => Line(js, m.Index)).ToList();
            Assert.Equal(4, callers.Count);   // the declaration line plus the three calls
            Assert.Contains(callers, l => l.Contains("async function langRefresh()", StringComparison.Ordinal));
            Assert.Contains(callers, l => l.Trim() == "langRefresh();");                     // the globe opening
            Assert.Contains(callers, l => l.Contains("upkeepCard", StringComparison.Ordinal));  // the card expanding
            Assert.Contains(callers, l => l.Contains("await langRefresh();", StringComparison.Ordinal)); // after a pack lands

            // The globe's own call sits in langMenuOpen and nowhere earlier.
            Assert.Contains("langRefresh();", Body("function langMenuOpen(){"));
        }

        // ------------------------------------------------------------------ E. the switch

        /// <summary>
        /// One road, and it is the road applyLanguage was already on: no reload anywhere. A
        /// reload would take the Saga scrollback, both search boxes and whatever is unsaved
        /// in the config editor, and none of those is worth a language switch.
        /// </summary>
        [Fact]
        public void The_switch_redraws_the_window_and_never_reloads_it()
        {
            var body = Body("async function switchLanguage(payload){");

            Assert.Contains("applyLanguage(code)", body);
            foreach (var reload in new[] { "location.reload", "location.href", "window.location" })
                Assert.DoesNotContain(reload, body);

            // English is the one case with no fetch at all, because its catalog was read at
            // boot and is still held.
            Assert.Contains("if(!EN_CATALOG) return false;", body);
            Assert.Contains("window.I18N.load(EN_CATALOG,\"en\")", body);
            Assert.Contains("langInjectFonts(null)", body);
        }

        /// <summary>
        /// applyLanguage has to draw the three surfaces this work added, and the menu is the
        /// awkward one: it is the only surface that can be OPEN while the switch runs, since
        /// a host picks a row and the menu is still under their pointer when the window comes
        /// back in the new language.
        /// </summary>
        [Fact]
        public void The_language_switch_draws_the_menu_the_mark_and_the_select_again()
        {
            var apply = Body("function applyLanguage(code){");
            var repaint = Body("function repaintBootCopy(){");

            Assert.Contains("renderLangMenu();", apply);
            // Through repaintBootCopy, which applyLanguage runs, so they are not listed twice.
            Assert.Contains("renderPlayerMsgLang();", repaint);
            Assert.Contains("renderLangDot();", repaint);
            Assert.Contains("repaintBootCopy();", apply);
        }

        /// <summary>
        /// THE THUNK RULE, in the shape this surface has. A dialog hands its wording in as a
        /// function so a switch can ask for it again; a menu that is redrawn from state does
        /// the same thing by holding NO WORDED STRING AT ALL. Every sentence in the menu is
        /// asked for inside a render, so the redraw is the whole of the re-wording.
        /// </summary>
        [Fact]
        public void The_menu_holds_facts_and_asks_for_its_words_every_time_it_is_drawn()
        {
            var js = AppJs();
            var state = Body("const LANG={");

            // Nothing in the state object is a catalog lookup: a sentence stored there would
            // keep the language it was stored in, whatever the window then did.
            Assert.DoesNotContain("T(\"", state);

            // And every sentence the menu shows is asked for inside a painter.
            foreach (var painter in new[]
                     {
                         "function langRowLine(l){", "function langPhaseWord(phase){",
                         "function langProgressHtml(code){", "function langRowHtml(l){",
                         "function renderLangMenu(){", "function renderLangDot(){",
                         "function renderPlayerMsgLang(){",
                     })
                Assert.Contains("T(\"", Body(painter));

            // The reason a download ended is looked up by id at draw time too, through the
            // table the page already had, so an ending nothing has words for renders nothing
            // rather than a dotted name.
            Assert.Contains("const row=LANG_REASONS.find(r=>r.named===id);", js);
        }

        // ------------------------------------------------------------------ F. the settings row

        /// <summary>
        /// The player-message select writes ONE preference, through the one road every other
        /// row on that card takes. It is a root user preference about wording, which is why
        /// it sits beside the Norse-names switch rather than on a server's own page.
        /// </summary>
        [Fact]
        public void The_player_message_select_saves_through_userprefs_and_reads_back_from_it()
        {
            var js = AppJs();
            var html = Html();

            Assert.Contains("rpc(\"userprefs.save\",{prefs:{PlayerMessageLanguage:asked}})", js);
            Assert.Contains("LANG.playerMessages=String(up.PlayerMessageLanguage||\"same\");", js);

            // The label, the control and the help line, all three, and the help line is the
            // one that has to describe the running product rather than the plan.
            Assert.Contains("data-i18n=\"settings.player_messages.label\"", html);
            Assert.Contains("id=\"selPlayerMsgLang\"", html);
            Assert.Contains("data-i18n=\"settings.player_messages.help\"", html);
            Assert.Equal("The language of the in-game restart countdown and of the Discord posts.",
                Lore("settings.player_messages.help"));
        }

        /// <summary>
        /// The options are the languages a pack is actually installed for, because a language
        /// nobody has downloaded has no words to write a countdown in. And whatever is SAVED
        /// is always one of them, even before the list has been asked for, so the card never
        /// shows a choice the host did not make.
        /// </summary>
        [Fact]
        public void The_select_offers_what_is_installed_and_never_loses_what_is_saved()
        {
            var body = Body("function renderPlayerMsgLang(){");

            Assert.Contains("langRows().filter(l=>l.installed||l.builtIn)", body);
            Assert.Contains("T(\"settings.player_messages.same\")", body);
            Assert.Contains("if(!opts.some(o=>o.code===saved)) opts.push", body);
            Assert.Contains("sel.value=saved;", body);
            Assert.Equal("Same as the interface", Lore("settings.player_messages.same"));
        }

        /// <summary>
        /// The card asks for the list once it is OPEN, and never while it is closing. The
        /// state has to be read after the press has been handled rather than during it,
        /// because two listeners sit on that header and the one that actually opens the card
        /// is added later: wireCollapsible("upkeepHead", ...) runs much further down app.js
        /// than this wiring does, and listeners fire in the order they were added.
        /// <para>
        /// Read inside the dispatch, the class is the one the card is LEAVING, which had this
        /// exactly backwards: a browser probe against a stubbed bridge showed expanding the
        /// card asking for nothing and collapsing it asking for lang.list, so the select was
        /// never filled from the list and the app reached the release page for a card the
        /// host had just put away. Both halves of that are worth a test: the wrong words in
        /// the select, and a request nobody made.
        /// </para>
        /// </summary>
        [Fact]
        public void The_upkeep_card_asks_once_it_is_open_and_never_while_it_is_closing()
        {
            var js = AppJs();

            // Deferred, so the answer is the state the card ARRIVED at.
            Assert.Contains("const langUpkeepAsk=()=>setTimeout(", js);
            Assert.Contains("if($(\"#upkeepCard\")?.classList.contains(\"open\")) langRefresh();", js);

            // Both roads reach it. The header is operable from the keyboard and that road
            // never produces a click: wireCollapsible's own keydown calls the toggle direct.
            Assert.Contains("$(\"#upkeepHead\")?.addEventListener(\"click\",langUpkeepAsk);", js);
            var keys = js.Substring(js.IndexOf("$(\"#upkeepHead\")?.addEventListener(\"keydown\"", StringComparison.Ordinal));
            keys = keys.Substring(0, keys.IndexOf("});", StringComparison.Ordinal));
            Assert.Contains("langUpkeepAsk()", keys);
            foreach (var key in new[] { "Enter", "\" \"", "Spacebar" })
                Assert.Contains(key, keys);

            // And the fact that makes the deferral necessary, so a later reader who is
            // tempted to fold it away can see what they would be walking back into.
            var wiring = js.IndexOf("$(\"#upkeepHead\")?.addEventListener(\"click\",langUpkeepAsk);", StringComparison.Ordinal);
            // The CALL, not the comment above the wiring that names it. Matching the bare
            // name found that comment instead, which is the same class of mistake as reading
            // the class inside the dispatch: the first thing that looks right is not it.
            var toggle = js.IndexOf("wireCollapsible(\"upkeepHead\",$(", StringComparison.Ordinal);
            Assert.True(wiring > 0 && toggle > 0, "one of the two listeners on that header is gone");
            Assert.True(wiring < toggle,
                "the toggle is now wired first, which does not make reading the class inside " +
                "the dispatch safe: it makes it wrong the other way round");
        }

        // ------------------------------------------------------------------ G. the events

        /// <summary>
        /// A switch in one window is a switch in all of them: the preference is one document,
        /// and a second window left in the old language would be showing something that is no
        /// longer true. The follower re-reads the status rather than trusting the event's own
        /// code, because the event says WHAT changed and the status says where its words are.
        /// </summary>
        [Fact]
        public void A_switch_in_another_window_is_followed_here()
        {
            var js = AppJs();
            var handler = js.Substring(js.IndexOf("Native.on(\"lang.changed\"", StringComparison.Ordinal));
            handler = handler.Substring(0, handler.IndexOf("\n});", StringComparison.Ordinal));

            Assert.Contains("if(d.code===langCurrent()) return;", handler);
            Assert.Contains("Native.call(\"lang.status\",{})", handler);
            Assert.Contains("switchLanguage({code:st.current,stringsUrl:st.stringsUrl,", handler);
            Assert.Contains("catch(e=>console.warn(", handler);
        }

        /// <summary>
        /// The bar is painted in place rather than by rebuilding the menu on every report.
        /// Rebuilding would restart the sweep sixty times over a large pack and throw away
        /// the row the pointer is on; and a report for a pack that is no longer the one
        /// coming down is dropped rather than painted over the one that is.
        /// </summary>
        [Fact]
        public void A_progress_report_paints_the_bar_in_place_and_only_for_the_pack_in_flight()
        {
            var js = AppJs();
            var handler = js.Substring(js.IndexOf("Native.on(\"lang.downloadProgress\"", StringComparison.Ordinal));
            handler = handler.Substring(0, handler.IndexOf("\n});", StringComparison.Ordinal));

            Assert.Contains("if(LANG.busyCode&&d.code!==LANG.busyCode) return;", handler);
            Assert.Contains("langPaintProgress();", handler);
            Assert.DoesNotContain("toast(", handler);

            var paint = Body("function langPaintProgress(){");
            Assert.Contains("row.getAttribute(\"data-lang-busy\")!==LANG.busyCode", paint);
            Assert.Contains("{renderLangMenu();return false;}", paint);
        }

        // ------------------------------------------------------------------ H. one owner

        /// <summary>
        /// One owner per element. Everything the page writes into the menu has no data-i18n
        /// on it, because a walker and a painter writing the same node take turns and the
        /// loser is whichever ran first. The globe is the other way round: the markup owns
        /// its title and its label, and app.js never writes either.
        /// <para>
        /// The player-message select is the one element with a HANDOVER rather than an
        /// owner, and it is worth saying exactly where the handover is. The markup carries
        /// the one option every install has, with its id on it, because renderPlayerMsgLang
        /// writes into an option and an option is not somewhere the walker can put the
        /// English back: with nothing in the markup the select read
        /// "settings.player_messages.same" for the whole of the gap between app.js being
        /// evaluated and the catalog arriving. The painter takes the element over on its
        /// FIRST run, which is a statement at the bottom of that block and therefore long
        /// before any walk, and what it writes carries no id. So the walker never meets
        /// that option, the two never take turns over it, and the rule above still holds.
        /// </para>
        /// </summary>
        [Fact]
        public void Each_new_element_is_written_by_exactly_one_of_the_two()
        {
            var html = Html();
            var js = AppJs();

            // The menu is empty in the markup: every row of it is an answer from the host.
            Assert.Contains("<div class=\"langmenu\" id=\"langMenu\" role=\"menu\" aria-labelledby=\"langBtn\"></div>", html);

            // The globe's words are the markup's, and the painter never touches them.
            Assert.DoesNotContain("langBtn\").title=", js);
            Assert.DoesNotContain("#langBtn\").textContent", js);

            // The select carries exactly one option in the markup, the one every install
            // has, and it carries its id so the walker words it if the painter somehow
            // never runs at all.
            Assert.Contains(
                "<select id=\"selPlayerMsgLang\" style=\"max-width:190px\">" +
                "<option value=\"same\" data-i18n=\"settings.player_messages.same\">" +
                "Same as the interface</option></select>", html);
            // And nothing else: a second option in the markup would be a language the host
            // has not downloaded, offered by a file that cannot know what is on disk.
            Assert.Equal(1, Regex.Matches(
                html.Substring(html.IndexOf("<select id=\"selPlayerMsgLang\"", StringComparison.Ordinal),
                    html.IndexOf("</select>", html.IndexOf("<select id=\"selPlayerMsgLang\"", StringComparison.Ordinal),
                        StringComparison.Ordinal) -
                    html.IndexOf("<select id=\"selPlayerMsgLang\"", StringComparison.Ordinal)),
                "<option").Count);
            // The painter takes the element over before anything can walk it: the call is a
            // statement in app.js, not something a promise resolves to.
            Assert.Contains("\nrenderPlayerMsgLang();", js);
            // And what the painter writes carries no id, so the walker never meets it.
            Assert.Contains("`<option value=\"${esc(o.code)}\"", js);
            Assert.DoesNotContain("<option value=\"${esc(o.code)}\" data-i18n", js);

            // The dot carries no words of its own at all: its whole content is a tooltip, and
            // it is role=img rather than aria-hidden so the label the painter writes on it is
            // something a screen reader can actually reach.
            Assert.Contains("<span class=\"langdot\" id=\"langDot\" role=\"img\"></span>", html);
        }

        /// <summary>
        /// Every id this block asks the catalog for exists, and the catalog check's other
        /// direction (nothing orphaned) is the gate. This is the half that would otherwise
        /// only be found by a host seeing a dotted name in a menu.
        /// </summary>
        [Fact]
        public void Every_id_the_globe_asks_for_is_in_the_catalog()
        {
            var js = AppJs();
            var keys = Catalog();

            var block = js.Substring(js.IndexOf("/* ---------- THE GLOBE ----------", StringComparison.Ordinal));
            block = block.Substring(0, block.IndexOf("Native.on(\"lang.changed\"", StringComparison.Ordinal));

            var asked = Regex.Matches(block, @"T\(""(lang\.[a-z0-9_.]+|settings\.[a-z0-9_.]+|common\.[a-z0-9_.]+)""")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();

            Assert.True(asked.Count >= 20, "the globe asks for suspiciously few sentences: " + asked.Count);
            foreach (var id in asked)
                Assert.True(keys.ContainsKey(id), "the globe asks for an id the catalog does not have: " + id);
        }

        // ------------------------------------------------------------------ I. the first frame

        /// <summary>
        /// THE FIRST FRAME, for the two surfaces this work put on screen before anything has
        /// been fetched. The select and the mark are both painted while app.js is still being
        /// evaluated, which is long before the English catalog lands, so both would otherwise
        /// carry a dotted id on the frame a host actually sees first. The floor static markup
        /// has (its English written into index.html, which the walker only ever replaces) does
        /// not exist for either: the select's options are written by a painter and the mark's
        /// whole content is a tooltip.
        /// <para>
        /// So the rule is the gate's own: a function called as a statement at column zero
        /// either asks the catalog for nothing, or is one of the painters repaintBootCopy runs
        /// again the moment the words arrive. Both of these ask, so both are repainted, and
        /// this holds it from the other end by dropping each in turn and watching the gate go
        /// red. A gate that cannot fail is not a gate.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("renderPlayerMsgLang")]
        [InlineData("renderLangDot")]
        public void A_globe_painter_dropped_from_the_repaint_is_refused(string painter)
        {
            var js = AppJs();

            // It is painted at column zero, which is what puts it on the first frame at all.
            Assert.Contains("\n" + painter + "();", js);
            // And it asks the catalog for words, which is what makes that a problem.
            Assert.Contains("T(\"", Body("function " + painter + "(){"));

            var said = RepoScript.Run(RepoScript.Python(), FirstFrameGate(),
                Fixture("try{" + painter + "();}catch(_){}", "/* dropped */"));

            Assert.False(said.Ok, painter + " is not repainted and the gate was happy:\n" + said);
            Assert.Contains(painter, said.Output);
        }

        /// <summary>
        /// And the shipped tree, through the same gate, with both of them in place.
        /// </summary>
        [Fact]
        public void The_globe_paints_no_catalog_id_on_the_first_frame()
        {
            var repaint = Body("function repaintBootCopy(){");
            Assert.Contains("try{renderPlayerMsgLang();}catch(_){}", repaint);
            Assert.Contains("try{renderLangDot();}catch(_){}", repaint);

            var said = RepoScript.Run(RepoScript.Python(), FirstFrameGate());
            Assert.True(said.Ok, "app.js paints an id before the catalog arrives:\n" + said);
            Assert.Contains("TOTAL 0", said.Output);
        }

        // ------------------------------------------------------------------ helpers

        private static string FirstFrameGate() =>
            RepoScript.At("scripts", "i18n", "check_first_frame.py");

        /// <summary>The shipped app.js with one edit, written somewhere throwaway. The edit
        /// has to bite: a fixture that quietly failed to change anything would make the
        /// assertion above meaningless.</summary>
        private string Fixture(string find, string replace)
        {
            var source = AppJs();
            Assert.Contains(find, source);
            var folder = Path.Combine(Path.GetTempPath(), "vbl-langframe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            _scratch.Add(folder);
            var path = Path.Combine(folder, "app.js");
            File.WriteAllText(path, source.Replace(find, replace), new UTF8Encoding(false));
            return path;
        }

        private static XElement TitleBar()
        {
            var html = Html();
            var start = html.IndexOf("<div class=\"titlebar\" id=\"titlebar\">", StringComparison.Ordinal);
            var end = html.IndexOf("<div class=\"body\">", StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "the title bar is not where it was");

            var fragment = html.Substring(start, end - start).Trim();
            // Back to the last close of the bar itself.
            fragment = fragment.Substring(0, fragment.LastIndexOf("</div>", StringComparison.Ordinal) + "</div>".Length);
            return XElement.Parse(fragment);
        }

        private static IEnumerable<string> Classes(XElement el) =>
            (el.Attribute("class")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

        private static bool Interactive(XElement el)
        {
            if (el.Attribute("role")?.Value == "button") return true;
            var classes = Classes(el).ToList();
            return classes.Contains("winbtn") || classes.Contains("cmdchip") || classes.Contains("langbtn");
        }

        private static string Line(string source, int at)
        {
            var start = source.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
            var end = source.IndexOf('\n', at);
            return source.Substring(start, (end < 0 ? source.Length : end) - start);
        }
    }
}
