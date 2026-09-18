using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The static half of the interface, now that it carries ids instead of only words.
    /// <para>
    /// Every translatable text node and tooltip on the titlebar, the rail and the
    /// dashboard names a catalog id, and keeps its English in the page as the thing a
    /// window with no catalog still reads. That leaves two ways for the product to go
    /// quietly wrong, and this file closes both. The first is DRIFT: somebody edits the
    /// English in the page and not in the catalog, or the other way round, and the
    /// window says one thing before the walker runs and another after it. The second is
    /// the RE-RUN HAZARD: an element whose words the walker fills and app.js also
    /// writes, where whichever ran last wins and the loser is invisible until a host
    /// hits the state that exposes it.
    /// </para>
    /// </summary>
    public class WebUiStaticCopyTests
    {
        private static string Html() => AppSourceTree.Web("index.html");

        private static string AppJs() => AppSourceTree.Web("app.js");

        /// <summary>The English catalog, id to entry, read the way the page reads it.</summary>
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

        // ------------------------------------------------------------- reading the page

        private static readonly Regex I18nAttr = new(
            @"data-i18n(-title|-placeholder|-aria)?=""([^""]+)""", RegexOptions.Compiled);

        /// <summary>Elements that never have children, so they never change the depth.</summary>
        private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr"
        };

        /// <summary>The end of the tag an index sits inside, quotes respected.</summary>
        private static int TagEnd(string html, int from)
        {
            var quote = '\0';
            for (var i = from; i < html.Length; i++)
            {
                var c = html[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '>') return i;
            }
            return -1;
        }

        /// <summary>The whole opening tag an attribute sits in.</summary>
        private static string TagAround(string html, int attrIndex)
        {
            var start = html.LastIndexOf('<', attrIndex);
            var end = TagEnd(html, attrIndex);
            return start < 0 || end < 0 ? "" : html.Substring(start, end - start + 1);
        }

        /// <summary>
        /// The element's FIRST OWN non blank text, which is the one the walker replaces:
        /// i18n.js setText() looks at direct child text nodes only, so a rune inside a
        /// span is not it. Null when the element has no words of its own.
        /// </summary>
        private static string OwnText(string html, int attrIndex)
        {
            var tagEnd = TagEnd(html, attrIndex);
            if (tagEnd < 0 || html[tagEnd - 1] == '/') return null;

            var depth = 0;
            var i = tagEnd + 1;
            while (i < html.Length)
            {
                var lt = html.IndexOf('<', i);
                if (lt < 0) return null;

                var text = html.Substring(i, lt - i);
                if (depth == 0 && text.Trim().Length > 0) return WebUtility.HtmlDecode(text);

                var end = TagEnd(html, lt);
                if (end < 0) return null;

                if (html[lt + 1] == '/')
                {
                    if (depth == 0) return null;          // our own closing tag
                    depth--;
                }
                else
                {
                    var name = Regex.Match(html.Substring(lt + 1, Math.Min(12, end - lt - 1)), @"^[A-Za-z0-9]+").Value;
                    var selfClosing = html[end - 1] == '/';
                    if (!selfClosing && !Void.Contains(name)) depth++;
                }
                i = end + 1;
            }
            return null;
        }

        private static string Attribute(string tag, string name) =>
            Regex.Match(tag, name + @"=""([^""]*)""").Groups[1] is var group && group.Success
                ? WebUtility.HtmlDecode(group.Value)
                : null;

        // ----------------------------------------------------------- A. page vs catalog

        /// <summary>
        /// Every id the page names has an entry. Without this the walker writes the
        /// dotted id over the English the moment somebody renames a key.
        /// </summary>
        [Fact]
        public void Every_id_the_page_names_is_in_the_English_catalog()
        {
            var catalog = Catalog();
            var missing = I18nAttr.Matches(Html())
                .Select(m => m.Groups[2].Value)
                .Distinct()
                .Where(id => Lore(catalog, id) == null)
                .ToList();

            Assert.True(missing.Count == 0,
                "index.html names ids the English catalog has no lore for: " + string.Join(", ", missing));
        }

        /// <summary>
        /// The English in the page and the English in the catalog are the same sentence.
        /// They are two copies of one string on purpose: the page's copy is what a window
        /// whose catalog never arrived reads, and the catalog's copy is what every
        /// translation is derived from. Two copies drift, so they are checked.
        /// </summary>
        [Fact]
        public void The_words_left_in_the_page_are_the_words_in_the_catalog()
        {
            var html = Html();
            var catalog = Catalog();
            var drifted = new List<string>();

            foreach (Match match in I18nAttr.Matches(html))
            {
                var kind = match.Groups[1].Value;   // "", "-title", "-placeholder", "-aria"
                var id = match.Groups[2].Value;
                var lore = Lore(catalog, id);
                if (lore == null) continue;         // named by the test above

                var tag = TagAround(html, match.Index);
                string page = kind switch
                {
                    "-title" => Attribute(tag, "title"),
                    "-placeholder" => Attribute(tag, "placeholder"),
                    "-aria" => Attribute(tag, "aria-label"),
                    _ => OwnText(html, match.Index),
                };

                if (page == null) continue;         // nothing left in the page to compare
                if (!string.Equals(page, lore, StringComparison.Ordinal))
                    drifted.Add($"{id}: page {Quote(page)} against catalog {Quote(lore)}");
            }

            Assert.True(drifted.Count == 0,
                "the page and the catalog no longer say the same English:\n  " + string.Join("\n  ", drifted));
        }

        private static string Quote(string text) => "\"" + text.Replace(" ", "\\u00a0") + "\"";

        // ------------------------------------------------- B. the re-run hazard

        /// <summary>
        /// The one element that is written both ways on purpose, recorded rather than
        /// waved through. #palConsoleLbl carries pal.console.row for the shut palette and
        /// app.js replaces it with pal.console.prompt while the host types a console
        /// command; the row is hidden whenever the box is empty, so nothing stale is ever
        /// on screen today. It becomes a real fight the day a re-render path walks an
        /// OPEN palette, which is why the entry is here for that change to find.
        /// </summary>
        private static readonly HashSet<string> KnownBothWays = new(StringComparer.Ordinal)
        {
            "palConsoleLbl.textContent",
        };

        private static readonly Regex Alias = new(
            @"\b(?:const|let|var)?\s*([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*\$\(""#([A-Za-z0-9_-]+)""\)",
            RegexOptions.Compiled);

        /// <summary>
        /// An element the walker fills must not also be written by app.js.
        /// <para>
        /// Whichever ran last wins, and neither one knows about the other: the walker
        /// runs once when the catalog lands, app.js writes whenever its state changes.
        /// #douseBtn, #srvMore, #hexiumSwitchLabel and #hUpdPill are all written at run
        /// time, which is why none of them carries an id; their words belong to the
        /// slice that keys run-time messages instead. The alias arm reads a local window
        /// after the alias is taken rather than the whole file, so an ordinary short name
        /// used twice in two functions does not implicate the wrong element.
        /// </para>
        /// </summary>
        [Fact]
        public void Nothing_the_walker_fills_is_also_written_by_app_js()
        {
            var html = Html();
            var js = AppJs();
            var clashes = new List<string>();

            foreach (Match match in I18nAttr.Matches(html))
            {
                var kind = match.Groups[1].Value;
                var tag = TagAround(html, match.Index);
                var id = Attribute(tag, "id");
                if (string.IsNullOrEmpty(id)) continue;

                var properties = kind switch
                {
                    "-title" => new[] { "title" },
                    "-placeholder" => new[] { "placeholder" },
                    "-aria" => new string[0],
                    _ => new[] { "textContent", "innerHTML", "innerText" },
                };

                foreach (var property in properties)
                {
                    if (KnownBothWays.Contains(id + "." + property)) continue;

                    var direct = new Regex(
                        @"(?:\$\(""#" + Regex.Escape(id) + @"""\)|getElementById\(""" + Regex.Escape(id) + @"""\))"
                        + @"(?:\?)?\." + property + @"\s*=[^=]");
                    if (direct.IsMatch(js))
                        clashes.Add($"#{id}.{property} is assigned directly in app.js");

                    foreach (Match alias in Alias.Matches(js))
                    {
                        if (!string.Equals(alias.Groups[2].Value, id, StringComparison.Ordinal)) continue;
                        var name = alias.Groups[1].Value;
                        // A name long enough to be a name (douseBtn, sailBtn) is followed
                        // to the end of the file, because those are taken once at the top
                        // and written hundreds of lines later. A short one (el, b, cap) is
                        // a scratch variable a dozen functions reuse, so it is only
                        // followed while it is plausibly still the same one.
                        var window = name.Length >= 5
                            ? js.Substring(alias.Index)
                            : js.Substring(alias.Index, Math.Min(2000, js.Length - alias.Index));
                        var through = new Regex(@"\b" + Regex.Escape(name) + @"(?:\?)?\." + property + @"\s*=[^=]");
                        if (through.IsMatch(window))
                            clashes.Add($"#{id} is aliased as {name} and {name}.{property} is assigned in app.js");
                    }
                }
            }

            Assert.True(clashes.Count == 0,
                "the walker and app.js would fight over the same words:\n  "
                + string.Join("\n  ", clashes.Distinct()));
        }

        /// <summary>
        /// The elements whose words app.js writes are keyless on purpose, and this says
        /// which ones and proves the reason is still true. It is also the gate above
        /// read backwards: give any of these an id and that gate fails, which is what
        /// makes its silence worth something.
        /// </summary>
        [Fact]
        public void The_run_time_labels_on_the_dashboard_carry_no_id_yet()
        {
            var html = Html();
            var js = AppJs();

            foreach (var id in new[] { "douseBtn", "stokeBtn", "hUpdPill", "hAppUpdPill", "srvMore",
                                       "hexiumSwitchLabel", "hexiumHelp", "hState", "hPid", "hearthSub",
                                       "netPill", "homeVikPill", "modCount", "saveAvg" })
            {
                var open = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
                Assert.True(open > 0, "index.html no longer has #" + id);
                var tag = TagAround(html, open);
                Assert.False(tag.Contains("data-i18n"),
                    "#" + id + " carries an id but app.js writes its words at run time");
            }

            // and the reason: each one really is written from app.js.
            Assert.Contains("douseBtn.textContent=", js);
            Assert.Contains("stokeBtn.textContent=", js);
            Assert.Contains("more.textContent=", js);
            Assert.Contains("label.textContent=T(\"hearth.upkeep.hexium.label\")", js);
            Assert.Contains("function renderSaveAvg()", js);
            Assert.Contains("try{renderSaveAvg();}catch(_){}", js);   // and the second paint runs it

            // The one that came off this list: nothing writes #lastSaveLbl at run time, so
            // the walker owns it outright, and it is keyed with both registers because the
            // Norse names switch rewords it. The selector list that used to name it as well
            // is gone with the swap it fed, so there is one owner now and the register is
            // picked inside the lookup rather than by a regex over what is on screen.
            Assert.Contains("id=\"lastSaveLbl\" data-i18n=\"hearth.saves.last.label\"", html);
            Assert.DoesNotContain("$(\"#lastSaveLbl\").textContent", js);
            Assert.DoesNotContain("const TERM_STATIC_SEL", js);
        }

        /// <summary>
        /// The one allowed both-ways element is really both ways, so the allowlist above
        /// cannot quietly become a place to put anything inconvenient.
        /// </summary>
        [Fact]
        public void The_allowed_both_ways_element_is_really_written_both_ways()
        {
            Assert.Contains("id=\"palConsoleLbl\" data-i18n=\"pal.console.row\"", Html());
            Assert.Contains("$(\"#palConsoleLbl\").textContent=T(\"pal.console.prompt\"", AppJs());
            Assert.Single(KnownBothWays);
        }

        /// <summary>
        /// The horn of mead is the one label on the rail that cannot carry an id.
        /// emberize() rebuilds its words into one span per letter while app.js is still
        /// parsing, so by the time the walker arrives the element has no text node of its
        /// own left: setText() would APPEND the sentence instead of replacing it and the
        /// link would read its words twice. So renderMead() writes the words out of the
        /// catalog and lights the embers over them, in that order, and the second paint
        /// runs it again when the catalog lands. The tooltip is an attribute and is
        /// untouched by emberize, so that half is keyed the ordinary way.
        /// </summary>
        [Fact]
        public void The_mead_link_keys_its_letters_through_its_own_painter()
        {
            var html = Html();
            var js = AppJs();
            var tag = html.Substring(html.IndexOf("<a class=\"mead\"", StringComparison.Ordinal), 220);

            Assert.Contains("data-i18n-title=\"side.mead.title\"", tag);
            Assert.DoesNotContain("data-i18n=\"", tag);

            var painter = js.Substring(js.IndexOf("function renderMead()", StringComparison.Ordinal), 320);
            Assert.Contains("T(\"side.mead.label\")", painter);
            // the write first, the embers after it, or the spans are thrown away
            Assert.True(painter.IndexOf("textContent=", StringComparison.Ordinal)
                        < painter.IndexOf("emberize(", StringComparison.Ordinal),
                "renderMead lights the embers before it writes the words");
            Assert.Contains("try{renderMead();}catch(_){}", js);
        }

        // ------------------------------------------------- C. the walk, and the bridge

        /// <summary>
        /// The walk runs only when a catalog actually landed. With none loaded T()
        /// answers with the id itself, so walking a failed fetch would paint dotted ids
        /// over the English the page carries for exactly that case.
        /// </summary>
        [Fact]
        public void The_walk_runs_only_when_a_catalog_landed()
        {
            Assert.Contains("if(ok&&window.I18N)window.I18N.applyStatic(document)", AppJs());
        }

        /// <summary>
        /// "World" is the rail's Norse caption for the Settings hall AND the label on the
        /// world-name field in the new-realm dialog. One entry for that text would have
        /// bound the dialog's field to the hall's name and a translator would have shipped
        /// the hall's word into the dialog. The dialog is now keyed in its own right, so
        /// the two no longer lean on idFor() refusing a word it sees twice: the rail asks
        /// for its id in the markup and the dialog asks for its own, by name, in app.js.
        /// The two entries still have to be two, which is what this holds.
        /// </summary>
        [Fact]
        public void The_two_meanings_of_World_are_two_entries_each_asked_for_by_name()
        {
            var catalog = Catalog();

            Assert.Equal("World", Lore(catalog, "common.norse.world"));
            Assert.Equal("World", Lore(catalog, "realm.new.world.label"));

            // the rail's caption, in the markup
            Assert.Contains("data-i18n=\"common.norse.world\"", Html());
            // the dialog's field label, in the dialog that builds it
            var js = AppJs();
            var open = js.IndexOf("async function addServerProfile()", StringComparison.Ordinal);
            var shut = js.IndexOf("function flamePal()", open, StringComparison.Ordinal);
            Assert.True(open > 0 && shut > open, "the new-realm dialog moved");
            Assert.Contains("<label>${esc(T(\"realm.new.world.label\"))}</label>",
                            js.Substring(open, shut - open));
            // and the word itself is off the bridge, so nothing depends on idFor refusing it
            Assert.DoesNotContain("TT(\"World\")", AppJs());
        }

        // ----------------------------------------------------------------- D. the rail

        /// <summary>
        /// Every hall on the rail reads its label out of the catalog, and every one that
        /// has a Norse caption reads that too. The nine ids are listed here rather than
        /// derived, because the point of the gate is that a tenth hall added without a
        /// key fails rather than quietly ships an English-only label.
        /// </summary>
        [Fact]
        public void Every_hall_on_the_rail_reads_its_label_and_its_caption_from_the_catalog()
        {
            var html = Html();
            var rail = html.Substring(
                html.IndexOf("<div class=\"navitem active\" data-page=\"hearth\"", StringComparison.Ordinal),
                html.IndexOf("</nav>", StringComparison.Ordinal)
                - html.IndexOf("<div class=\"navitem active\" data-page=\"hearth\"", StringComparison.Ordinal));

            var catalog = Catalog();
            var expected = new (string Page, string Label, string Caption)[]
            {
                ("hearth", "Dashboard", "Hearth"),
                ("vikings", "Players", "Vikings"),
                ("mods", "Mods", null),
                ("runes", "Configs", "Runes"),
                ("world", "Settings", "World"),
                ("atlas", "Map", "Atlas"),
                ("saga", "Log", "Saga"),
                ("herald", "Discord", "Herald"),
                ("skald", "Statistics", "Skald"),
            };

            foreach (var (page, label, caption) in expected)
            {
                var labelId = "side.nav." + page + ".label";
                Assert.Contains("data-i18n=\"" + labelId + "\"", rail);
                Assert.Equal(label, Lore(catalog, labelId));

                if (caption == null) continue;
                var captionId = "common.norse." + caption.ToLowerInvariant();
                Assert.Contains("data-i18n=\"" + captionId + "\"", rail);
                Assert.Equal(caption, Lore(catalog, captionId));
            }
        }

        /// <summary>
        /// One entry per Norse word however many places show it. The dashboard says
        /// Hearth three times over (the rail caption, the page caption, the card
        /// caption) and a translator should be handed that word once. Vikings now says
        /// it four times, because the Statistics hall's players card carries the caption
        /// too; Saga and Herald three each and Atlas and Skald two each, now that the
        /// Map, Log, Discord and Statistics halls carry their page captions and the
        /// Discord hall its card caption. The Settings hall's seed chip brought the
        /// sixth COPY. The numbers are written down rather than derived on purpose:
        /// a new place that spells the same word with a new id fails here.
        /// <para>
        /// Every Norse entry in the catalog is listed, including the ones shown once,
        /// so that a second place saying an already-answered word has to come through
        /// this list rather than quietly minting a second id for it.
        /// </para>
        /// </summary>
        [Fact]
        public void One_entry_answers_every_place_a_Norse_word_is_shown()
        {
            var html = Html();

            var shown = new (string Word, int Times)[]
            {
                ("hearth", 3), ("vikings", 4), ("saga", 3), ("runes", 2), ("world", 2),
                ("atlas", 2), ("herald", 3), ("skald", 2),
                ("forge", 1), ("scrolls", 1), ("heimr", 1), ("rites", 1),
            };

            foreach (var (word, times) in shown)
                Assert.Equal(times, Regex.Matches(html, @"data-i18n=""common\.norse\." + word + @"""").Count);

            // Two Norse captions are drawn in code rather than markup, because the dialogs
            // that show them are built at the moment they open: the Barrow's heading (the
            // Backups dialog, twice, plus the button's tooltip on the saves card) and the
            // Vellum's (the Log settings dialog). They are counted the same way, off the
            // one call the page makes, so a third place saying either word still has to
            // come through this list.
            var js = AppJs();
            var drawn = new (string Word, int Times)[] { ("barrow", 3), ("vellum", 1) };
            foreach (var (word, times) in drawn)
                Assert.Equal(times, Regex.Matches(js, @"(?<![A-Za-z0-9_$])T\(""common\.norse\." + word + @"""\)").Count);

            // and the list really is every Norse entry there is, so a new one cannot
            // be added to the catalog and shown twice without landing here.
            var inCatalog = Catalog().Keys
                .Where(k => k.StartsWith("common.norse.", StringComparison.Ordinal))
                .Select(k => k.Substring("common.norse.".Length))
                .OrderBy(w => w, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(shown.Select(s => s.Word).Concat(drawn.Select(d => d.Word))
                              .OrderBy(w => w, StringComparer.Ordinal).ToList(), inCatalog);

            Assert.Equal(6, Regex.Matches(html, @"data-i18n=""common\.chip\.copy""").Count);
        }

        /// <summary>
        /// The same sentence said in more than one hall is one entry there too, not
        /// only for the Norse words. Two Open buttons in Directories, two sortable
        /// Name columns, two show-hide password chips, and the four foldable headers
        /// that all explain themselves the same way.
        /// </summary>
        [Fact]
        public void One_entry_answers_every_place_a_shared_sentence_is_shown()
        {
            var html = Html();
            var catalog = Catalog();

            Assert.Equal(2, Regex.Matches(html, @"data-i18n=""common\.button\.open""").Count);
            Assert.Equal(2, Regex.Matches(html, @"data-i18n-title=""common\.sort\.by_name""").Count);
            Assert.Equal(2, Regex.Matches(html, @"data-i18n-title=""common\.chip\.show\.title""").Count);
            // The Upkeep card and the three foldable sections of the Settings hall.
            Assert.Equal(4, Regex.Matches(html, @"data-i18n-title=""hearth\.upkeep\.head\.title""").Count);

            Assert.Equal("Open", Lore(catalog, "common.button.open"));
            Assert.Equal("Sort by name", Lore(catalog, "common.sort.by_name"));
            Assert.Equal("Show / hide password", Lore(catalog, "common.chip.show.title"));
            Assert.Equal("Click to expand or collapse", Lore(catalog, "hearth.upkeep.head.title"));
        }
    }
}
