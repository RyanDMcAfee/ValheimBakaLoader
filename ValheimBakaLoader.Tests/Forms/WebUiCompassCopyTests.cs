using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The sixteen points of the compass, worded out of the catalog.
    /// <para>
    /// The wind box under the map named its bearing from a row of sixteen string literals
    /// inside wxCompass. English abbreviates a direction to letters and most languages do
    /// not, and a literal in a function body is a sentence no translator can reach and no
    /// completeness gate can see, so the box read N while the hall around it read Japanese.
    /// </para>
    /// <para>
    /// The points are catalog entries now, named through an *Id table of the same shape as
    /// WX_LABEL, which is the shape check_catalog.py recognises as a table of ids. These
    /// gates hold the table to sixteen points in bearing order, hold every id to the
    /// catalog, and hold the box to being redrawn by the language switch.
    /// </para>
    /// </summary>
    public class WebUiCompassCopyTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        /// <summary>The sixteen points, north first and going clockwise.</summary>
        private static readonly string[] Points =
        {
            "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW",
        };

        private static string Table()
        {
            var source = AppJs();
            var start = source.IndexOf("const WX_COMPASS=[", StringComparison.Ordinal);
            Assert.True(start >= 0, "app.js no longer names the compass points in a table");
            var end = source.IndexOf("];", start, StringComparison.Ordinal);
            Assert.True(end > start, "the compass table is not closed");
            return source.Substring(start, end - start);
        }

        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateObject())
                map[entry.Name] = entry.Value.Clone();
            return map;
        }

        [Fact]
        public void The_table_names_the_sixteen_points_in_bearing_order()
        {
            var pairs = Regex.Matches(
                Table(),
                @"\{point:""(?<point>[NSEW]+)"",\s*pointId:""(?<id>atlas\.compass\.[a-z]+)""\}");

            Assert.Equal(Points.Length, pairs.Count);
            for (var i = 0; i < Points.Length; i++)
            {
                Assert.Equal(Points[i], pairs[i].Groups["point"].Value);
                Assert.Equal("atlas.compass." + Points[i].ToLowerInvariant(), pairs[i].Groups["id"].Value);
            }
        }

        [Fact]
        public void Every_point_is_a_catalog_entry_holding_its_English_abbreviation()
        {
            var catalog = Catalog();
            foreach (var point in Points)
            {
                var id = "atlas.compass." + point.ToLowerInvariant();
                Assert.True(catalog.ContainsKey(id), "the English catalog has no " + id);
                Assert.True(catalog[id].TryGetProperty("lore", out var lore)
                            && lore.ValueKind == JsonValueKind.String,
                    id + " carries no English");
                Assert.Equal(point, lore.GetString());
            }
        }

        [Fact]
        public void The_bearing_is_read_out_of_the_table_and_not_out_of_a_row_of_literals()
        {
            var source = AppJs();
            var start = source.IndexOf("function wxCompass(deg){", StringComparison.Ordinal);
            Assert.True(start >= 0, "app.js no longer names a bearing");
            var end = source.IndexOf("\n}", start, StringComparison.Ordinal);
            var body = source.Substring(start, end - start);

            Assert.Contains("WX_COMPASS[Math.round(deg/22.5)%16]", body, StringComparison.Ordinal);
            Assert.Contains("T(p.pointId)", body, StringComparison.Ordinal);

            // The row of sixteen literals the function used to carry. It is the one shape
            // that renders correctly in English and in no other language.
            Assert.DoesNotContain("\"NNE\",\"NE\"", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The weather engine above is pure: it has no DOM and asks nothing of the page,
        /// which is what lets it be lifted out whole and checked against the game's own
        /// math. Asking the catalog for words is not pure, so the table and the function
        /// that reads it sit BELOW the end marker.
        /// </summary>
        [Fact]
        public void The_weather_engine_stays_pure()
        {
            var source = AppJs();
            var engineEnd = source.IndexOf("/* WX-ENGINE-END */", StringComparison.Ordinal);
            Assert.True(engineEnd > 0, "the weather engine no longer says where it ends");
            var engineStart = source.IndexOf("/* WX-ENGINE-BEGIN", StringComparison.Ordinal);
            var engine = source.Substring(engineStart, engineEnd - engineStart);

            Assert.DoesNotContain("T(", engine, StringComparison.Ordinal);
            Assert.True(source.IndexOf("const WX_COMPASS=[", StringComparison.Ordinal) > engineEnd,
                "the compass table is inside the pure engine");
        }

        /// <summary>
        /// The box is drawn once and never patched, so it follows a language switch only
        /// because applyLanguage draws it again. Without that line the words would change
        /// everywhere except the one place this work was for.
        /// </summary>
        [Fact]
        public void The_language_switch_draws_the_weather_box_again()
        {
            var source = AppJs();
            var start = source.IndexOf("function applyLanguage(code){", StringComparison.Ordinal);
            Assert.True(start >= 0, "app.js no longer has a language switch");
            var end = source.IndexOf("\n}", start, StringComparison.Ordinal);
            var body = source.Substring(start, end - start);

            Assert.Contains("renderWeather();", body, StringComparison.Ordinal);
            Assert.Contains("wxCompass(wind.deg)", AppJs(), StringComparison.Ordinal);
        }
    }
}
