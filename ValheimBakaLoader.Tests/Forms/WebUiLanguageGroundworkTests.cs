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

        [Theory]
        [InlineData("ru", "'Cinzel','Forum',Georgia")]
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
        /// The Latin face is FIRST in every stack, because CSS fallback is per character.
        /// With Inter first, every digit, server name and version string keeps Inter and
        /// only the characters Inter lacks reach the packed face. Put the CJK face first and
        /// the whole numeric texture of the app changes without ever looking broken, which
        /// is the easiest way to get this wrong.
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
                var families = line.Substring(line.IndexOf(':') + 1).Split(',');
                Assert.True(families[0].Trim().StartsWith("'Inter'", StringComparison.Ordinal)
                            || families[0].Trim().StartsWith("'JetBrains Mono'", StringComparison.Ordinal),
                    "a CJK face was put ahead of the Latin one in: " + line.Trim());
                // Packed face, then the Windows face that always exists, then the generics.
                Assert.True(families[1].Trim().StartsWith("'Noto", StringComparison.Ordinal), line.Trim());
                Assert.True(families[2].Trim().StartsWith("'Yu Gothic UI'", StringComparison.Ordinal)
                            || families[2].Trim().StartsWith("'Microsoft ", StringComparison.Ordinal), line.Trim());
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
        /// The pack's faces are declared now and pointed at real files later. A declaration
        /// costs nothing until a character it covers is rendered in a stack that names it,
        /// and no stack names one while the language is English, so an English install never
        /// asks for any of these.
        /// </summary>
        [Theory]
        [InlineData("Forum", "ru", "Forum-cyrillic.woff2")]
        [InlineData("Noto Sans JP", "ja", "NotoSansJP-jp.woff2")]
        [InlineData("Noto Serif JP", "ja", "NotoSerifJP-jp.woff2")]
        [InlineData("Noto Sans SC", "zh-Hans", "NotoSansSC-sc.woff2")]
        [InlineData("Noto Serif SC", "zh-Hans", "NotoSerifSC-sc.woff2")]
        [InlineData("Noto Sans TC", "zh-Hant", "NotoSansTC-tc.woff2")]
        [InlineData("Noto Serif TC", "zh-Hant", "NotoSerifTC-tc.woff2")]
        public void A_pack_face_is_declared_and_addressed_on_the_apps_own_origin(
            string family, string code, string file)
        {
            var css = Css();
            var face = css.Split('\n').FirstOrDefault(l => l.Contains("font-family:'" + family + "'", StringComparison.Ordinal)
                                                           && l.StartsWith("@font-face", StringComparison.Ordinal));

            Assert.True(face != null, "no @font-face declares " + family);
            Assert.Contains("https://app.baka/lang/" + code + "/" + ShippedVersion() + "/fonts/" + file, face);
            Assert.Contains("font-display:swap", face);
            Assert.Contains("unicode-range:", face);
        }

        /// <summary>
        /// Belt and braces on the per character rule: if a stack is ever reordered, a range
        /// that stops short of Latin means Latin still cannot be served from a CJK file.
        /// </summary>
        [Fact]
        public void No_pack_face_claims_the_latin_range()
        {
            foreach (var line in Css().Split('\n').Where(l => l.StartsWith("@font-face", StringComparison.Ordinal)
                                                              && l.Contains("https://app.baka/lang/", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain("U+0000-00FF", line);
                Assert.DoesNotContain("U+0100-02BA", line);
            }
        }

        /// <summary>
        /// The pack folder is named after the app version, so the address in the stylesheet
        /// has to move with a release. Pinned here rather than left to be noticed.
        /// </summary>
        [Fact]
        public void The_pack_address_carries_the_version_this_build_ships()
        {
            var version = ShippedVersion();

            var faces = Css().Split('\n')
                .Where(l => l.StartsWith("@font-face", StringComparison.Ordinal))
                .ToList();

            var addresses = faces
                .SelectMany(l => Regex.Matches(l, @"https://app\.baka/lang/[^/]+/([^/]+)/").Cast<Match>())
                .ToList();

            Assert.Equal(7, addresses.Count);
            foreach (var m in addresses) Assert.Equal(version, m.Groups[1].Value);
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
    }
}
