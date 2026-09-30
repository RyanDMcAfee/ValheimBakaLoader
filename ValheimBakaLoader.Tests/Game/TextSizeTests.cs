using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// Text size: three sizes, one number each, and the two places that number has to reach.
    /// </summary>
    /// <remarks>
    /// The window is one web page, so making its text bigger is the page's ZOOM rather than a
    /// second set of font sizes beside the first. A zoom moves the text, the boxes it sits in
    /// and the gaps between them together, which is the only way a size setting stays laid out.
    /// <para>
    /// One thing it does NOT reach, and it would have shipped looking like a bug: zooming the
    /// page shrinks the viewport the halls are laid out in, so at 1.45 a 1024 pixel window hands
    /// the page 706 CSS pixels, narrower than anything in the stylesheet, and the window's own
    /// minimum has to grow with the zoom.
    /// <para>
    /// And one thing it reaches that the first cut of this feature thought it did not. A canvas
    /// font is a number of CSS pixels, but a page zoom raises devicePixelRatio and the Atlas is
    /// transformed by that ratio, so its labels grow with the zoom on their own. Multiplying
    /// them by the factor as well drew them about half again too big at Extra large.
    /// </para>
    /// </remarks>
    public class TextSizeTests : BaseTest
    {
        // ------------------------------------------------------------------ the three numbers

        [Theory]
        [InlineData("normal", 1.0)]
        [InlineData("large", 1.2)]
        [InlineData("xlarge", 1.45)]
        public void Each_size_means_one_number(string size, double factor)
        {
            Assert.Equal(factor, TextSizes.Factor(size));
        }

        /// <summary>
        /// A spelling this build does not know reads as Normal. A window at a zoom of nothing,
        /// or at a zoom of zero, is a window nobody can use, and a hand-edited preferences file
        /// is the one road that can carry a spelling this build has never heard of.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        [InlineData("huge")]
        [InlineData("1.45")]
        [InlineData("XXLARGE")]
        public void A_spelling_this_build_does_not_know_reads_as_normal(string size)
        {
            Assert.Equal(1.0, TextSizes.Factor(size));
            Assert.Equal(TextSizes.Normal, TextSizes.Normalize(size));
        }

        [Theory]
        [InlineData("NORMAL", "normal")]
        [InlineData(" Large ", "large")]
        [InlineData("XLarge", "xlarge")]
        public void Case_and_space_are_forgiven_and_nothing_else_is(string asked, string stored)
        {
            Assert.Equal(stored, TextSizes.Normalize(asked));
        }

        [Fact]
        public void The_three_are_offered_in_the_order_they_grow()
        {
            Assert.Equal(new[] { "normal", "large", "xlarge" }, TextSizes.All);

            var factors = TextSizes.All.Select(TextSizes.Factor).ToList();
            Assert.Equal(factors.OrderBy(f => f).ToList(), factors);
        }

        // ------------------------------------------------------------------ the preference

        [Fact]
        public void The_preference_round_trips_and_defaults_to_normal()
        {
            Assert.Equal(TextSizes.Normal, UserPreferences.GetDefault().TextSize);
            Assert.Equal(TextSizes.Normal, UserPreferences.FromFile(new UserPreferencesFile()).TextSize);

            var prefs = UserPreferences.GetDefault();
            prefs.TextSize = TextSizes.ExtraLarge;
            Assert.Equal(TextSizes.ExtraLarge, UserPreferences.FromFile(prefs.ToFile()).TextSize);

            // And it has a place on disk under a key that is part of the compatibility contract.
            var field = typeof(UserPreferencesFile).GetProperty("TextSize");
            Assert.NotNull(field);
            var json = field.GetCustomAttributes()
                .OfType<Newtonsoft.Json.JsonPropertyAttribute>()
                .SingleOrDefault();
            Assert.Equal("textSize", json?.PropertyName);
        }

        // ------------------------------------------------------------------ the window

        /// <summary>
        /// The window really zooms, the window's own minimum really follows the zoom, and the
        /// same helper computes that minimum in all three places it is set. A minimum that
        /// followed the zoom in two of them and not the third would let a host drag the window
        /// down to a size the halls are not laid out in.
        /// </summary>
        [Fact]
        public void The_window_zooms_and_its_minimum_grows_with_the_zoom()
        {
            var window = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.cs");

            Assert.Contains("internal void ApplyTextSize(string size)", window);
            Assert.Contains("WebView.ZoomFactor = TextZoom;", window);
            Assert.Contains("TextZoom = TextSizes.Factor(size);", window);

            // One helper, and no hand-rolled minimum left beside it.
            Assert.Contains("private static Size MinimumFor(float scale, double zoom", window);
            Assert.Equal(3, Regex.Matches(window, Regex.Escape("MinimumFor(")).Count - 1);
            Assert.DoesNotContain("Math.Min(ScaleUnits(DesignMinWidth, scale), maxWidth)", window);

            // And it is applied before the first frame rather than after it: a host who chose
            // Extra large must not read one frame at the size they did not choose, every launch.
            var zoomAt = window.IndexOf("ApplyTextSize(ReadSavedTextSize());", StringComparison.Ordinal);
            var navigateAt = window.IndexOf("core.Navigate($\"https://{VirtualHost}/index.html\");",
                StringComparison.Ordinal);
            Assert.True(zoomAt > 0 && navigateAt > zoomAt,
                "the zoom is applied after the page is navigated to, so the first frame is the wrong size");
        }

        /// <summary>
        /// A save that carried the size moves the window then and there. Waiting for the next
        /// launch would read as a setting that does nothing, because the host is looking at the
        /// window they chose it in.
        /// </summary>
        [Fact]
        public void A_save_that_carried_the_size_moves_the_window_at_once()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

            Assert.Contains("Apply(\"TextSize\", v =>", bridge);
            Assert.Contains("TextSizes.Normalize(v.Value<string>())", bridge);
            Assert.Contains("if (textSizeMoved)", bridge);
            Assert.Contains("ApplyTextSize(prefs.TextSize);", bridge);

            // The spelling and nothing else. A number rode along beside it for a scaler on the
            // page, and that scaler was counting the zoom twice.
            Assert.DoesNotContain("TextSizeFactor", bridge);
        }

        // ------------------------------------------------------------------ the canvas

        /// <summary>
        /// The canvas fonts are the DESIGN sizes and nothing multiplies them, because the zoom
        /// already reaches them. Every one of them still goes through one helper, so a literal
        /// cannot creep back in.
        /// </summary>
        /// <remarks>
        /// THE PREMISE THIS FEATURE WAS BUILT ON WAS WRONG, and it was written down in three
        /// places. "A font on a 2D context is a number of CSS pixels the zoom does not touch"
        /// is true of the NUMBER and false of what is drawn. A WebView2 ZoomFactor is a
        /// Chromium page zoom: it shrinks the CSS viewport and raises devicePixelRatio by the
        /// same factor. The Atlas sizes its backing store as the wrap's CSS size times
        /// devicePixelRatio and then draws through setTransform(dpr, ...), so its drawing space
        /// is CSS pixels, exactly like the stylesheet's. An unchanged "11px" canvas font
        /// therefore grows in DEVICE pixels by the zoom, the same as the 11px text beside it.
        /// <para>
        /// Measured in Chromium with Chromium's own page zoom (Emulation.setDeviceMetricsOverride
        /// at viewport/z with deviceScaleFactor z): the same literal 11px monospace drew ink
        /// 43x10 device pixels at z=1.00, 51x12 at z=1.20 and 61x14 at z=1.45, with no scaler
        /// anywhere. Multiplying by the factor on top of that made the map's labels about half
        /// again taller than the words around them at Extra large.
        /// </para>
        /// </remarks>
        [Fact]
        public void No_canvas_font_is_multiplied_because_the_zoom_already_reaches_it()
        {
            var app = AppSourceTree.Web("app.js");

            Assert.Contains("function atlasFont(px){", app);

            // The helper hands the design pixels straight through. Nothing scales them.
            var helper = app.Substring(app.IndexOf("function atlasFont(px){", StringComparison.Ordinal));
            helper = helper.Substring(0, helper.IndexOf("\n}", StringComparison.Ordinal));
            Assert.DoesNotContain("TEXT_SCALE", helper);
            Assert.DoesNotContain("*", helper);
            Assert.Contains("px+\"px 'IBM Plex Mono',monospace\"", helper);

            // And the page keeps no scaler at all, so there is nothing left to multiply with.
            Assert.DoesNotContain("TEXT_SCALE", app);
            Assert.DoesNotContain("textScaleFor", app);
            Assert.DoesNotContain("TextSizeFactor", app);

            // The two the map draws with, unchanged.
            Assert.Contains("g.font=atlasFont(11);", app);
            Assert.Contains("ctx.font=atlasFont(10);", app);

            // Both canvases the map draws text on put their drawing space in CSS pixels, which
            // is the whole reason no multiplier is needed. A canvas drawn without that
            // transform WOULD need one, so the transform is the thing held here.
            Assert.Equal(2, Regex.Matches(app, Regex.Escape("setTransform(dpr,0,0,dpr,0,0)")).Count);
            Assert.Contains("const W=Math.round(s.w*dpr),H=Math.round(s.h*dpr);", app);

            // And nothing sets a canvas font any other way. A literal here is invisible in
            // every screenshot taken at Normal.
            var literals = Regex.Matches(app, @"\.font\s*=\s*[""'`]")
                .Cast<Match>()
                .Select(m => app.Substring(Math.Max(0, m.Index - 30), 70).Replace("\n", " "))
                .ToList();
            Assert.True(literals.Count == 0,
                "a canvas font is written as a literal rather than through atlasFont: "
                + string.Join(" | ", literals));
        }

        /// <summary>
        /// And the three places that told the reader the opposite say the true thing now. A
        /// comment is part of the contract: this one is what the double scaling was built on,
        /// and the sentence a host reads was written from it.
        /// </summary>
        [Fact]
        public void Nothing_left_in_the_tree_says_the_zoom_cannot_reach_a_canvas()
        {
            foreach (var (what, text) in new[]
            {
                ("TextSizes.cs", AppSourceTree.Read("ValheimBakaLoader", "Game", "TextSizes.cs")),
                ("index.html", AppSourceTree.Web("index.html")),
                ("app.js", AppSourceTree.Web("app.js")),
                ("BlendWindow.Bridge.cs",
                    AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs")),
            })
            {
                foreach (var claim in new[]
                {
                    "the zoom does not touch", "where a zoom reaches nothing",
                    "it reaches nothing a canvas draws", "multiplies those by the same",
                    "multiplies its canvas fonts", "multiply its own fonts",
                })
                    Assert.False(text.Contains(claim, StringComparison.Ordinal),
                        what + " still says " + JsonQuote(claim) + ", which is the premise the"
                        + " double scaling was built on: a page zoom raises devicePixelRatio and"
                        + " the canvas is transformed by it, so canvas text grows with the zoom");
            }
        }

        private static string JsonQuote(string s) => "\"" + s + "\"";
    }
}
