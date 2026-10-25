using System;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The name a test copy is made under (1.3.0, issue 27): the source's in-game name made safe
    /// for a folder, the suffix, and a number when the name is taken. The page offers names by
    /// the same rule (testCopyName in app.js), so the table here is the one both sides answer to.
    /// </summary>
    public class TestCopyNamingTests
    {
        [Theory]
        [InlineData("Baka Gaijin", "Baka Gaijin")]
        [InlineData("Baka's Hall!", "Bakas Hall")]
        [InlineData("  Second  Sunset  ", "Second  Sunset")]
        [InlineData("Ragnar/Valhalla:2", "RagnarValhalla2")]
        [InlineData("under_score-dash", "under_score-dash")]
        [InlineData("\tTabbed\nline ", "Tabbedline")]
        [InlineData("Tr\u00f6ll H\u00f6hle", "Tr\u00f6ll H\u00f6hle")]
        [InlineData("\u30f4\u30a1\u30eb\u30cf\u30e9", "\u30f4\u30a1\u30eb\u30cf\u30e9")]          // katakana
        [InlineData("\u0939\u093f\u0928\u094d\u0926\u0940", "\u0939\u093f\u0928\u094d\u0926\u0940")] // devanagari, its vowel signs are marks
        [InlineData("\uff11\uff12\uff13", "\uff11\uff12\uff13")]                                  // fullwidth digits are decimal digits
        [InlineData("\u216b\u00b2", "")]                                                            // a roman numeral and a superscript are numbers, not digits
        [InlineData("Dragon \U0001F409 Keep", "Dragon  Keep")]
        [InlineData("!!!", "")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void Only_letters_digits_space_underscore_and_dash_are_kept_and_the_ends_trimmed(string name, string expected)
        {
            Assert.Equal(expected, TestCopyNaming.Clean(name));
        }

        /// <summary>
        /// Half of a pair (a name cut in the middle of an emoji) is dropped rather than throwing.
        /// Kept out of the table above because a theory's data is serialized for discovery.
        /// </summary>
        [Fact]
        public void A_lone_surrogate_is_dropped_and_never_throws()
        {
            Assert.Equal("abc", TestCopyNaming.Clean("abc" + '\ud800'));
            Assert.Equal("abc", TestCopyNaming.Clean('\udc00' + "abc"));
        }

        /// <summary>
        /// An accent typed as a mark after its letter is joined onto it first, so the name comes
        /// out the same whichever way the keyboard produced it.
        /// </summary>
        [Fact]
        public void A_decomposed_accent_comes_out_as_the_composed_letter()
        {
            Assert.Equal("Tr\u00f6ll", TestCopyNaming.Clean("Tro\u0308ll"));
            Assert.Equal(TestCopyNaming.Clean("Tr\u00f6ll"), TestCopyNaming.Clean("Tro\u0308ll"));
        }

        [Fact]
        public void The_base_is_the_in_game_name_or_the_profile_name_when_nothing_of_it_is_left()
        {
            Assert.Equal("Baka Gaijin", TestCopyNaming.BaseFor("Baka Gaijin", "Default"));
            Assert.Equal("Default", TestCopyNaming.BaseFor(null, "Default"));
            Assert.Equal("Default", TestCopyNaming.BaseFor("   ", "Default"));
            Assert.Equal("Homes", TestCopyNaming.BaseFor("!!!", "Home's"));
            Assert.Equal("", TestCopyNaming.BaseFor("!!!", "???"));
        }

        [Fact]
        public void A_taken_name_in_any_case_gets_the_next_number_after_the_suffix()
        {
            Assert.Equal("_bakaT", TestCopyNaming.Suffix);
            Assert.Equal("Baka Gaijin_bakaT", TestCopyNaming.Propose("Baka Gaijin", "Default", new[] { "Default", "Second Sunset" }));
            Assert.Equal("Baka Gaijin_bakaT2", TestCopyNaming.Propose("Baka Gaijin", "Default", new[] { "baka gaijin_BAKAT" }));
            Assert.Equal("Baka Gaijin_bakaT3", TestCopyNaming.Propose("Baka Gaijin", "Default",
                new[] { "Baka Gaijin_bakaT", "BAKA GAIJIN_bakaT2", null }));
            Assert.Equal("Default_bakaT", TestCopyNaming.Propose(null, "Default", Array.Empty<string>()));
            Assert.Equal("_bakaT", TestCopyNaming.Propose("!!!", "???", null));
        }

        /// <summary>The count has no ceiling, so what comes back is always free.</summary>
        [Fact]
        public void The_count_goes_past_any_ceiling_rather_than_handing_back_a_taken_name()
        {
            var taken = new string[200];
            taken[0] = "Home_bakaT";
            for (var n = 2; n < 200; n++) taken[n - 1] = "Home_bakaT" + n;
            taken[199] = "Home";

            Assert.Equal("Home_bakaT200", TestCopyNaming.Propose("Home", "Home", taken));
        }

        [Fact]
        public void A_name_sent_by_the_page_is_held_to_the_rule_and_an_empty_one_is_offered_for_it()
        {
            var taken = new[] { "Default", "Baka Gaijin_bakaT" };

            // What the dialog offered comes through as it was.
            Assert.Equal("Baka Gaijin_bakaT2", TestCopyNaming.NameFor("Baka Gaijin_bakaT2", "Baka Gaijin", "Default", taken));
            // A typed name loses what a folder name should not hold.
            Assert.Equal("Proving 3", TestCopyNaming.NameFor(" Proving #3? ", "Baka Gaijin", "Default", taken));
            // Nothing left of it: the name the host would have offered.
            Assert.Equal("Baka Gaijin_bakaT2", TestCopyNaming.NameFor("   ", "Baka Gaijin", "Default", taken));
            Assert.Equal("Baka Gaijin_bakaT2", TestCopyNaming.NameFor("!?", "Baka Gaijin", "Default", taken));
            Assert.Equal("Baka Gaijin_bakaT2", TestCopyNaming.NameFor(null, "Baka Gaijin", "Default", taken));
            // A taken name is not renumbered: servers.create refuses it by name.
            Assert.Equal("baka gaijin_bakat", TestCopyNaming.NameFor("baka gaijin_bakat", "Baka Gaijin", "Default", taken));
        }
    }
}
