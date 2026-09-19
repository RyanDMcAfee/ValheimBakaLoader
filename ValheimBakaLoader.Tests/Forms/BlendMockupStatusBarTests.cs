using System;
using System.IO;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The visual spec's status bar says what the shipped one says.
    /// <para>
    /// design-mockups/blend.html is the drawing the interface was built from, and it is
    /// still what somebody opens to see what a surface is meant to look like. Its status
    /// bar had drifted: the RCON and tick segments were two bare words with no ids on
    /// them, while the shipped bar had long since split each into a labelled span the
    /// language walker can reach and a value span the page writes into. A drawing that no
    /// longer matches the thing it draws sends the next reader to the wrong shape.
    /// </para>
    /// </summary>
    public class BlendMockupStatusBarTests
    {
        private static string Mockup() =>
            AppSourceTree.Lf(File.ReadAllText(
                Path.Combine(AppSourceTree.RepoRoot(), "design-mockups", "blend.html")));

        private static string Shipped() => AppSourceTree.Web("index.html");

        /// <summary>One status-bar segment out of a page, by a word inside it.</summary>
        private static string Segment(string page, string holding)
        {
            var at = page.IndexOf(holding, StringComparison.Ordinal);
            Assert.True(at >= 0, "no status-bar segment holding " + holding);
            var start = page.LastIndexOf("<span class=\"seg\"", at, StringComparison.Ordinal);
            Assert.True(start >= 0, "the segment holding " + holding + " is not a seg");
            var end = page.IndexOf("</span></span>", start, StringComparison.Ordinal);
            Assert.True(end > start, "the segment holding " + holding + " is not closed");
            return Regex.Replace(page.Substring(start, end - start + "</span></span>".Length), @"\s+", " ");
        }

        [Theory]
        [InlineData("status.rcon.label")]
        [InlineData("status.tick.label")]
        public void The_mockup_status_bar_carries_the_shipped_markup(string id)
        {
            Assert.Equal(Segment(Shipped(), id), Segment(Mockup(), id));
        }

        /// <summary>
        /// The two words on their own, with nothing around them, are exactly what was
        /// there before. A gate that only looked for the new markup would pass on a bar
        /// carrying both shapes at once.
        /// </summary>
        [Fact]
        public void The_bare_words_are_gone_from_the_mockup()
        {
            var mockup = Mockup();
            Assert.DoesNotContain("<span class=\"seg\">RCON ", mockup, StringComparison.Ordinal);
            Assert.DoesNotContain("<span class=\"seg\">tick ", mockup, StringComparison.Ordinal);
        }
    }
}
