using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The ground a second language stands on: the font stacks, the two attributes that
    /// choose them, the titlebar convention that lets a language control be added later,
    /// and the copy gate following the copy when it moves into a catalog.
    /// <para>
    /// None of this shows a host anything new. It is the part that is expensive to retrofit
    /// and cheap to put in now, and every one of these gates guards a thing that would
    /// otherwise be discovered by a native reader looking at a screenshot.
    /// </para>
    /// </summary>
    public class WebUiLanguageGroundworkTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        private static string Css() => AppSourceTree.Web("app.css");

        private static string ShippedVersion()
        {
            var csproj = File.ReadAllText(Path.Combine(
                AppSourceTree.RepoRoot(), "ValheimBakaLoader", "ValheimBakaLoader.csproj"));
            var match = Regex.Match(csproj, @"<Version>([^<]+)</Version>");
            Assert.True(match.Success, "the app csproj has no Version");
            return match.Groups[1].Value.Trim();
        }

        // ------------------------------------------------------------------ A. the tokens

        /// <summary>
        /// Four tokens, and no family name written anywhere else. That is what keeps a
        /// language switch a four-line change rather than a sweep through 1200 lines of CSS.
        /// </summary>
        [Fact]
        public void Every_font_family_in_the_product_goes_through_a_token()
        {
            var css = Css();

            Assert.Contains("--serif:'Cinzel',Georgia,'Times New Roman',serif;", css);
            Assert.Contains("--serif-small:var(--serif);", css);
            Assert.Contains("--mono:'JetBrains Mono',Consolas,'Cascadia Mono',monospace;", css);
            Assert.Contains("--sans:'Inter',system-ui,'Segoe UI',sans-serif;", css);

            // No rule names a family directly. @font-face declares them; nothing else may.
            var rules = string.Join("\n", css.Split('\n').Where(l => !l.TrimStart().StartsWith("@font-face", StringComparison.Ordinal)));
            foreach (var family in new[] { "font-family:'Cinzel'", "font-family:'Inter'", "font-family:'JetBrains Mono'" })
                Assert.DoesNotContain(family, rules);
        }

        /// <summary>
        /// Thirteen of the fifteen serif rules sit at 13px or smaller; only the Hearth state
        /// line and the page heading are above it. A CJK display serif at 13px on a dark
        /// ground is thin and blurry, so the small ones take their own token and the two
        /// large ones keep the display face, which is where the brand voice actually lives.
        /// The eleventh is the BepInEx row's name, at 12.5px above the mods table, and the
        /// last two came with the Settings hall's Directories work: the word Currently
        /// above each path, and the title on the unsaved notice.
        /// </summary>
        [Fact]
        public void The_small_serif_rules_and_the_display_ones_are_separate_tokens()
        {
            var css = Css();

            Assert.Equal(13, Regex.Matches(css, Regex.Escape("font-family:var(--serif-small)")).Count);

            var display = Regex.Matches(css, @"font-family:var\(--serif\)[^}]*")
                .Cast<Match>().Select(m => m.Value).ToList();
            Assert.Equal(2, display.Count);
            Assert.Contains(display, r => r.Contains("font-size:19px", StringComparison.Ordinal));
            Assert.Contains(display, r => r.Contains("font-size:17px", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ B. the stacks

        /// <remarks>
        /// Russian names Baka Roman rather than Forum, and the difference is not cosmetic:
        /// Forum is a Reserved Font Name under OFL 1.1 clause 3, so the subset the pack
        /// carries is renamed and the manifest publishes it under the new name. A stack
        /// naming Forum matched nothing and fell through to Georgia, which put the Hearth
        /// state in a system serif for every Russian reader.
        /// </remarks>
        [Theory]
        [InlineData("ru", "'Cinzel','Baka Roman',Georgia")]
        [InlineData("ja", "'Cinzel','Noto Serif JP','Yu Gothic UI'")]
        [InlineData("zh-Hans", "'Cinzel','Noto Serif SC','Microsoft YaHei UI'")]
        [InlineData("zh-Hant", "'Cinzel','Noto Serif TC','Microsoft JhengHei UI'")]
        public void Each_language_has_a_block_of_its_own(string code, string serif)
        {
            var css = Css();

            Assert.Contains("html[data-lang=\"" + code + "\"]{", css);
            Assert.Contains("--serif:" + serif, css);
        }

        /// <summary>
        /// The Latin face is FIRST among the faces that cover Latin, because CSS fallback is
        /// per character. With Inter ahead of the full CJK face, every digit, server name and
        /// version string keeps Inter and only the characters Inter lacks reach the packed
        /// face. Put the full CJK face first and the whole numeric texture of the app changes
        /// without ever looking broken, which is the easiest way to get this wrong.
        /// <para>
        /// ONE family is allowed in front of Inter, and only one: the Marks face. U+2026,
        /// U+00B7 and, in Simplified, the curly quotes are full-width characters of the
        /// script whose code points live in Latin-1 and General Punctuation, which Inter
        /// declares and therefore wins. The pack publishes the same file a second time under
        /// "&lt;family&gt; Marks" with a range of exactly those marks, so putting it first
        /// pulls back the handful of marks and nothing else. Its name has to end in Marks,
        /// which is what this holds: a full face smuggled into that slot would take the
        /// digits with it.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("ja")]
        [InlineData("zh-Hans")]
        [InlineData("zh-Hant")]
        public void The_latin_face_stays_first_and_the_windows_face_sits_behind_the_pack(string code)
        {
            var block = Block(Css(), "html[data-lang=\"" + code + "\"]{");

            foreach (var line in block.Split('\n').Where(l => l.Contains("--sans:") || l.Contains("--mono:")))
            {
                var families = line.Substring(line.IndexOf(':') + 1).Split(',')
                    .Select(f => f.Trim()).ToList();

                // The marks-only face, if there is one, and nothing else, may stand first.
                if (families[0].StartsWith("'Noto", StringComparison.Ordinal))
                {
                    Assert.True(families[0].EndsWith("Marks'", StringComparison.Ordinal),
                        "a full CJK face was put ahead of the Latin one in: " + line.Trim());
                    families.RemoveAt(0);
                }

                Assert.True(families[0].StartsWith("'Inter'", StringComparison.Ordinal)
                            || families[0].StartsWith("'JetBrains Mono'", StringComparison.Ordinal),
                    "a CJK face was put ahead of the Latin one in: " + line.Trim());
                // Packed face, then the Windows face that always exists, then the generics.
                Assert.True(families[1].StartsWith("'Noto", StringComparison.Ordinal)
                            && !families[1].EndsWith("Marks'", StringComparison.Ordinal), line.Trim());
                Assert.True(families[2].StartsWith("'Yu Gothic UI'", StringComparison.Ordinal)
                            || families[2].StartsWith("'Microsoft ", StringComparison.Ordinal), line.Trim());
            }
        }

        /// <summary>
        /// Under a CJK language the small serif rules take the body face. Russian does not
        /// need the split: Forum reads perfectly well at 10px.
        /// </summary>
        [Fact]
        public void The_small_serif_falls_to_the_body_face_for_cjk_and_stays_put_for_russian()
        {
            var css = Css();

            Assert.Contains("--serif-small:var(--sans);", Block(css, "html[data-lang=\"ja\"]{"));
            Assert.Contains("--serif-small:var(--sans);", Block(css, "html[data-lang=\"zh-Hans\"]{"));
            Assert.Contains("--serif-small:var(--sans);", Block(css, "html[data-lang=\"zh-Hant\"]{"));
            Assert.Contains("--serif-small:var(--serif);", Block(css, "html[data-lang=\"ru\"]{"));
        }

        // ------------------------------------------------------------------ C. the pack faces

        /// <summary>
        /// THE STYLESHEET DECLARES NO PACK FACE AT ALL, and that is the fix rather than a
        /// regression. Seven rules used to stand at the top of app.css, each addressing one
        /// pack's own folder by name. The font store is content addressed now, and since
        /// 1.2.1 it is on a host of its own: a face lives at
        /// https://lang.baka/_fonts/&lt;sha256&gt;.woff2,
        /// shared by every language and every version whose bytes are the same, so the
        /// address a stylesheet could write down does not exist. A rule written against the
        /// old shape would ask for a file that is not there on every single switch, and look
        /// exactly like a font that failed to load.
        /// <para>
        /// Only the six bundled Latin faces remain, and each of those is addressed beside the
        /// page, which is what makes an English install ask the pack store for nothing.
        /// </para>
        /// </summary>
        [Fact]
        public void The_stylesheet_declares_no_pack_face_and_composes_no_pack_address()
        {
            var faces = Css().Split('\n')
                .Where(l => l.StartsWith("@font-face", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(6, faces.Count);
            foreach (var face in faces)
            {
                Assert.DoesNotContain("app.baka", face);
                Assert.Contains("src:url('fonts/", face);
            }

            // Not in a rule either: nothing the browser reads may name the pack origin. The
            // comment where the block used to stand explains both addresses and is meant to,
            // so the declarations are read with the comments stripped out.
            var rules = Regex.Replace(Css(), @"/\*.*?\*/", "", RegexOptions.Singleline);
            Assert.DoesNotContain("https://app.baka/lang/", rules);
        }

        /// <summary>
        /// The faces are written at runtime instead, by the side that knows the hashes. One
        /// element, owned by one function, replaced whole on every switch and removed when
        /// there is nothing to declare, which is how an English window ends up with no pack
        /// faces standing at all.
        /// <para>
        /// The address is the one the host reported and is never composed here. That is the
        /// same rule the manifest URL follows on the C# side, and for the same reason: an
        /// address this page builds is an address that can be built wrongly.
        /// </para>
        /// </summary>
        [Fact]
        public void The_pack_faces_are_written_at_runtime_from_the_addresses_the_host_reported()
        {
            var js = AppJs();
            var body = Body(js, "function langInjectFonts(fonts){");

            Assert.Contains("document.getElementById(\"langFonts\")", body);
            Assert.Contains("style.id=\"langFonts\"", body);
            // Nothing to declare: the element goes rather than being left holding the last
            // language's faces over the new one.
            Assert.Contains("if(!list.length){if(style)style.remove();return 0;}", body);
            Assert.Contains("\"@font-face{font-family:'\"", body);
            Assert.Contains("\"font-display:swap;\"", body);
            // The format word is READ off the address, never assumed. The store admits five
            // extensions and a src whose declared format does not match the bytes is a src the
            // browser skips: a .ttf face declared woff2 installs, hashes, sniffs clean and then
            // never draws, which is the quietest way a language can fail.
            Assert.Contains("\"src:url('\"+clean(f.url)+\"') format('\"+langFontFormat(clean(f.url))+\"');\"", body);
            Assert.DoesNotContain("format('woff2');\"", body);
            Assert.Contains("f.unicodeRange?\"unicode-range:\"+clean(f.unicodeRange)+\";\":\"\"", body);

            // A stylesheet built out of a file somebody else wrote. The three characters that
            // could end a declaration early are dropped, quotes and angle brackets with them.
            Assert.Contains("replace(/[;{}\"'<>\\\\]/g,\"\")", body);

            // No address is composed on this side. The only /lang/ literal in app.js is the
            // one the harness fetches, and nothing builds a fonts path out of a version.
            Assert.DoesNotContain("\"https://app.baka/lang/", js);
        }

        /// <summary>
        /// The page's format words and the store's admitted extensions are one list read from
        /// two ends, so the day a sixth extension is admitted the page stops declaring it as
        /// woff2. Each of the five gets its own CSS format word, and the store's assumption for
        /// a face carried with no extension at all is the page's default for the same reason.
        /// <para>
        /// This is the pairing the review found missing: the service grew four extensions and
        /// the page kept saying woff2, which the browser answers by skipping the src. Nothing
        /// throws, nothing logs, the face simply never draws.
        /// </para>
        /// </summary>
        [Fact]
        public void Every_extension_the_store_admits_has_its_own_format_word_on_the_page()
        {
            var service = AppSourceTree.Files()["LanguagePackService.cs"];

            var declared = Regex.Match(service, @"FaceExtensions\s*=\s*\{([^}]+)\}");
            Assert.True(declared.Success, "the service no longer names the extensions it admits");

            var admitted = Regex.Matches(declared.Groups[1].Value, "\"([^\"]+)\"")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.Equal(new[] { "woff2", "woff", "ttf", "otf", "ttc" }, admitted);

            var body = Body(AppJs(), "function langFontFormat(url){");

            // woff2 is the default rather than a case, because it is also what the store
            // assumes for a face published with no extension at all.
            Assert.Contains("case \"woff\": return \"woff\";", body);
            Assert.Contains("case \"ttf\": return \"truetype\";", body);
            Assert.Contains("case \"otf\": return \"opentype\";", body);
            Assert.Contains("case \"ttc\": return \"collection\";", body);
            Assert.Contains("default: return \"woff2\";", body);

            // The other end of the pairing used to be the window's own content-type table, and
            // since 1.2.1 there is no such table: a pack file is served by the folder mapper,
            // which names the type itself. So the src's declared format is the ONLY place a
            // face's kind is stated to the browser, which is what makes the five cases above
            // load-bearing rather than belt and braces, and the window must not grow a second
            // owner for the same fact.
            var serving = AppSourceTree.Files()["BlendWindow.cs"];
            Assert.DoesNotContain("font/woff2", serving, StringComparison.Ordinal);
            Assert.DoesNotContain("LanguageContentType", serving, StringComparison.Ordinal);
        }

        /// <summary>
        /// The faces arrive with the switch, so the switch has to hand them over before the
        /// words are loaded: a catalog swapped in ahead of the rules it needs paints one
        /// frame in a face that cannot draw it.
        /// </summary>
        [Fact]
        public void The_switch_injects_the_faces_before_it_loads_the_words()
        {
            var body = Body(AppJs(), "async function switchLanguage(payload){");

            var faces = body.IndexOf("langInjectFonts(p.fonts)", StringComparison.Ordinal);
            var words = body.IndexOf("window.I18N.load(cat,code)", StringComparison.Ordinal);
            Assert.True(faces >= 0 && words >= 0, "the switch no longer does both");
            Assert.True(faces < words, "the words were loaded before the faces that draw them");
        }

        // ------------------------------------------------------------------ D. lang and data-lang

        /// <summary>
        /// Both attributes, always together. Chromium picks its Han face off <c>lang</c>
        /// (without it Japanese renders with Simplified glyph shapes, which is the first
        /// thing a native reviewer flags and which no automated check here could catch),
        /// and the CSS blocks hang off <c>data-lang</c>.
        /// </summary>
        [Fact]
        public void One_helper_sets_both_attributes_and_boot_calls_it()
        {
            var js = AppJs();

            Assert.Contains("function setLanguageAttributes(code){", js);
            Assert.Contains("root.lang=tag;", js);
            Assert.Contains("root.dataset.lang=tag;", js);
            Assert.Contains("setLanguageAttributes(\"en\");", js);
        }

        /// <summary>
        /// Nothing in any stack covers the Runic block, so the ten rune spans keep falling
        /// through to Segoe UI Historic exactly as they do today. This is only ever visible
        /// in a screenshot, so it is pinned here.
        /// </summary>
        [Fact]
        public void No_language_stack_disturbs_the_runes()
        {
            var runes = Regex.Matches(AppSourceTree.Web("index.html"), "class=\"rune\"").Count;
            Assert.Equal(10, runes);

            foreach (Match m in Regex.Matches(Css(), @"html\[data-lang=""[^""]+""\]\{[^}]*\}"))
                Assert.DoesNotContain("Historic", m.Value);
        }

        // ------------------------------------------------------------------ E. the titlebar

        /// <summary>
        /// The titlebar is a drag handle, so anything interactive put there has to be
        /// excluded or it moves the window instead of being clicked. One class name means
        /// the next control added there does not have to edit this selector, and the three
        /// original names stay listed so nothing existing changed.
        /// </summary>
        [Fact]
        public void The_titlebar_drag_handler_leaves_interactive_elements_alone()
        {
            var js = AppJs();

            Assert.Contains("const TB_NO_DRAG=\".cmdchip,.winbtns,.winbtn,.tb-interactive\";", js);
            Assert.Contains("if(e.target.closest(TB_NO_DRAG)) return;", js);
        }

        // ------------------------------------------------------------------ F. the copy gate

        private static string Gate() => AppSourceTree.Lf(File.ReadAllText(
            Path.Combine(AppSourceTree.RepoRoot(), "scripts", "copy-gate", "copy_gate.sh")));

        /// <summary>
        /// The gate has to follow the copy. The day a string moves out of app.js and into a
        /// catalog, a gate that only knows the old file list goes green while scanning a
        /// file that no longer holds any copy, and a false green is worse than no gate.
        /// </summary>
        [Fact]
        public void The_copy_gate_scans_the_catalogs_as_well_as_the_source()
        {
            var gate = Gate();

            Assert.Contains("I18N_DIR=\"$APP/WebUI/i18n\"", gate);
            Assert.Contains("sw_scancat.py", gate);
            // Every one of checks 1 to 4 has a catalog arm and says so in its output.
            Assert.True(Regex.Matches(gate, Regex.Escape("cat_note")).Count >= 6,
                "not every check reports what it scanned");
        }

        /// <summary>
        /// The one allowed C# hit is named, not counted. A count passes just as happily when
        /// the one hit is a different string than the one somebody vouched for.
        /// </summary>
        [Fact]
        public void The_gate_allowlists_by_name_rather_than_by_count()
        {
            var gate = Gate();
            var allowlist = File.ReadAllText(Path.Combine(
                AppSourceTree.RepoRoot(), "scripts", "copy-gate", "allowlist.txt"));

            Assert.Contains("ALLOWLIST=\"$HERE/allowlist.txt\"", gate);
            Assert.DoesNotContain("[ \"$n\" != \"1\" ] && fail=1", gate);
            Assert.Contains("[1 - General]", allowlist);
        }

        /// <summary>
        /// The dash rule is a function of the language. Russian needs U+2014 as required
        /// punctuation, not decoration, so the English rule cannot simply be applied to
        /// every catalog. English keeps none, absolutely.
        /// </summary>
        [Fact]
        public void The_dash_rule_is_per_language_and_english_still_allows_none()
        {
            var rules = File.ReadAllText(Path.Combine(
                AppSourceTree.RepoRoot(), "scripts", "copy-gate", "lang_dashes.json"));

            Assert.Contains("\"en\": []", rules);
            Assert.Contains("\"ru\": [\"U+2014\", \"U+2013\"]", rules);
            Assert.Contains("errors=\"replace\"", Gate().Contains("sw_dashes.py")
                ? File.ReadAllText(Path.Combine(AppSourceTree.RepoRoot(), "scripts", "copy-gate", "sw_dashes.py"))
                : "");
        }

        /// <summary>
        /// The catalog folder holds a real English catalog, so the copy gate's catalog
        /// arm is live rather than scanning an empty skeleton. What is IN it, and whether
        /// the interface and the catalog agree both ways, is WebUiCatalogTests; this only
        /// insists the arm above has something to read.
        /// <para>
        /// And that _meta names a catalog REVISION. It is the number that decides whether
        /// a patch release costs a host four pack downloads or none: a release whose
        /// catalog revision has not moved can copy the pack it already has to the new
        /// version folder and touch the network zero times. Two large CJK packs ride on
        /// that one integer, so it is asserted here rather than left to be noticed.
        /// </para>
        /// </summary>
        [Fact]
        public void The_catalog_folder_holds_a_real_english_catalog()
        {
            var folder = Path.Combine(
                AppSourceTree.RepoRoot(), "ValheimBakaLoader", "WebUI", "i18n");
            var en = File.ReadAllText(Path.Combine(folder, "en.json"));

            Assert.Contains("\"language\": \"en\"", en);
            Assert.Contains("\"keys\"", en);
            Assert.DoesNotContain("\"keys\": {}", en);
            Assert.Contains("\"lore\"", en);

            using var parsed = System.Text.Json.JsonDocument.Parse(en);
            var meta = parsed.RootElement.GetProperty("_meta");
            Assert.True(meta.TryGetProperty("catalog", out var revision),
                "_meta names no catalog revision, so a pack cannot be reused across a release");
            Assert.True(revision.TryGetInt32(out var number) && number >= 1,
                "the catalog revision is not a whole number from 1 up");
            Assert.Equal(ShippedVersion(),
                meta.GetProperty("appVersion").GetString());
        }

        // ------------------------------------------------------------------ helpers

        private static string Block(string css, string selector)
        {
            var start = css.IndexOf(selector, StringComparison.Ordinal);
            Assert.True(start >= 0, "no block for " + selector);
            var end = css.IndexOf('}', start);
            return css.Substring(start, end - start);
        }

        /// <summary>One function's body, brace matched from its opening line.</summary>
        internal static string Body(string source, string opener)
        {
            var start = source.IndexOf(opener, StringComparison.Ordinal);
            Assert.True(start >= 0, "app.js no longer declares " + opener);

            var index = start + opener.Length - 1;   // the opening brace itself
            var depth = 0;
            for (; index < source.Length; index++)
            {
                if (source[index] == '{') depth++;
                else if (source[index] == '}' && --depth == 0) break;
            }
            return source.Substring(start, Math.Min(index + 1, source.Length) - start);
        }
    }
}
