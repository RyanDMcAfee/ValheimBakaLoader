using System;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// Numbers, sizes, clocks, spans and sort order, all of which belong to the host's
    /// own language and none of which a hand rolled function can know.
    /// <para>
    /// Six formatters in app.js wrote their own output: bytes with a hard coded decimal
    /// point, a relative time in English letters, a duration in d/h/m/s, two clocks
    /// built out of padStart. Eight more call sites asked toLocale* or localeCompare
    /// with no language at all, which means whatever Windows happens to be set to
    /// rather than the language the window is being read in. All of them go through
    /// the lookup now, which reads the active language out of Intl.
    /// </para>
    /// <para>
    /// English does not move. Every option set is pinned to the shape the halls already
    /// showed, and the lookup's own self test asserts those shapes byte for byte.
    /// </para>
    /// </summary>
    public class WebUiFormatterLocaleTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        /// <summary>
        /// One accessor, read at call time rather than captured, because index.html
        /// retries a failed include at the plain address and that can land after this
        /// file has already run.
        /// </summary>
        [Fact]
        public void The_formatters_reach_the_lookup_through_one_accessor()
        {
            var js = AppJs();

            Assert.Contains("const intl=()=>window.I18N;", js);
            Assert.Contains("const LOC=()=>{const L=intl();return L?L.locale():undefined;};", js);
        }

        [Fact]
        public void Every_formatter_asks_the_lookup_first()
        {
            var js = AppJs();

            Assert.Contains("return L?L.fmtTime(t):pad(t.getHours())", js);                 // fmtT
            Assert.Contains(":L?L.fmtRelative(s)", js);                                     // agoAt
            Assert.Contains("const L=intl(); if(L) return L.fmtBytes(n);", js);              // fmtBytes
            Assert.Contains("const L=intl(); if(L) return L.fmtDuration(sec);", js);         // skDur
            Assert.Contains("L?L.fmtRelative(Math.round(sec/60),\"minute\")", js);           // atlasAge
            Assert.Contains("const L=intl();return L?L.fmtTime(d):pad(d.getHours())", js);   // clock
        }

        /// <summary>
        /// Every one keeps the shape it had for a host reading English. The arm below
        /// the lookup is the old code, untouched, so a window with no lookup at all is
        /// a window in English rather than a window full of dashes.
        /// </summary>
        [Fact]
        public void The_English_shapes_are_still_written_down_beneath_each_one()
        {
            var js = AppJs();

            Assert.Contains("if(n<1048576) return (n/1024).toFixed(1)+\" KB\";", js);
            Assert.Contains("if(d>0) return d+\"d \"+h+\"h\";", js);
            Assert.Contains(":s<3600?Math.floor(s/60)+\"m ago\"", js);
        }

        /// <summary>
        /// The status bar clock follows the language rather than being built once at
        /// boot. A formatter made before the language was chosen keeps writing in the
        /// language that was active then, for the life of the window.
        /// </summary>
        [Fact]
        public void The_status_clock_formatter_is_asked_for_per_paint()
        {
            var js = AppJs();

            Assert.Contains("const CLOCK_OPTS={", js);
            Assert.Contains("hourCycle:\"h23\"", js);
            Assert.Contains("timeZoneName:\"short\"", js);
            Assert.Contains("const L=intl(); if(L) return L.dateTimeFormat(CLOCK_OPTS);", js);
            Assert.Contains("const f=clockFmt();", js);

            // And the one built once at boot is gone.
            Assert.DoesNotContain("const CLOCK_FMT=", js);
        }

        /// <summary>
        /// No call anywhere asks for "whatever Windows is set to" any more. The eight
        /// that did are the ones a host on Russian Windows reading an English window
        /// would have found dates in Russian inside.
        /// </summary>
        [Fact]
        public void No_date_or_number_is_formatted_without_naming_the_language()
        {
            var js = AppJs();

            Assert.Empty(Regex.Matches(js, @"toLocale(Date|Time)?String\(\)"));

            var all = Regex.Matches(js, @"\.toLocale(?:Date|Time)?String\(").Count;
            var named = Regex.Matches(js, @"\.toLocale(?:Date|Time)?String\(LOC\(\)\)").Count;
            Assert.True(all >= 5, "only found " + all + " toLocale call sites");
            Assert.Equal(all, named);
        }

        /// <summary>
        /// Sorting is the quiet one. Chinese expects pinyin order and Swedish puts a
        /// after z, and a byte comparison knows neither. Both helpers live in one place
        /// so a new sort cannot quietly reintroduce the old call.
        /// </summary>
        [Fact]
        public void Sorting_goes_through_the_two_helpers_and_nowhere_else()
        {
            var js = AppJs();

            // Twice: the fallback arm of cmpText and the fallback arm of cmpExact.
            Assert.Equal(2, Regex.Matches(js, @"\.localeCompare\(").Count);
            Assert.Contains("const cmpText=(a,b)=>{const L=intl();return L?L.compare(a,b)", js);
            Assert.Contains("const cmpExact=(a,b)=>{const L=intl();return L?L.compare(a,b,{})", js);

            // And the sorts that used to call it directly now call a helper.
            Assert.Contains("s.sort((a,b)=>m*cmpText(a.ModName||\"\",b.ModName||\"\"));", js);
            Assert.Contains("const txt=(a,b,f)=>cmpText(f(a)||\"\",f(b)||\"\");", js);
            Assert.Contains("||cmpExact(a.displayName||\"\",b.displayName||\"\"));", js);
        }
    }
}
