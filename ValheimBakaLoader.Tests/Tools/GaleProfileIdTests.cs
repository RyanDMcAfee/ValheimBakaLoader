using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The shape check on a pasted Gale profile id. Every row below was put to the real
    /// service once, by hand, on 2026-10-12: the ones accepted here answered 404 (a well formed
    /// id nobody owns) and the ones refused here answered 400 with "Must be a base-64 encoded
    /// UUID or a 6-character alphanumeric string". The tests themselves never reach it.
    /// </summary>
    public class GaleProfileIdTests
    {
        [Theory]
        [InlineData("ABCDEF")]
        [InlineData("abcdef")]
        [InlineData("ab12CD")]
        [InlineData("AAAAAAAAAAAAAAAAAAAAAA")]
        [InlineData("AAAAAAAAAAAAAAAAAAAAAQ")]
        [InlineData("AAAAAAAAAAAA-_AAAAAAAA")]
        [InlineData("Zm9vYmFyLWJhei0xMjM0NQ")]
        [InlineData("_AAAAAAAAAAAAAAAAAAAAA")]                   // an underscore first
        [InlineData("-b12cd34_ef56gh78ij90g")]                   // a dash first
        [InlineData("Ab_9-xY_z0Qr7_Lm-NoPqA")]                   // both, all through it
        public void The_two_shapes_the_service_takes_are_accepted(string id)
        {
            var check = GaleProfileId.Check(id);
            Assert.True(check.Ok);
            Assert.Equal(id, check.Id);
            Assert.Null(check.SentenceId);
            Assert.False(check.Blank);
        }

        [Theory]
        [InlineData("ABCDE")]                                    // five
        [InlineData("ABCDEFG")]                                  // seven
        [InlineData("ab_12C")]                                   // underscore in a short code
        [InlineData("ab cd")]
        [InlineData("not-valid")]
        [InlineData("AAAAAAAAAAAAAAAAAAAAA")]                    // twenty one
        [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")]                 // padded
        [InlineData("AAAAAAAAAAAAAAAAAAAAAB")]                   // trailing bits set
        [InlineData("AAAAAAAAAAAAAAAAAAAAA_")]                   // trailing bits set
        [InlineData("AAAAAAAAAAAA+/AAAAAAAA")]                   // the other base64 alphabet
        [InlineData("00000000-0000-0000-0000-000000000000")]     // a UUID, but not in base64
        [InlineData("https://gale.kesomannen.com/api/profile/ABCDEF")]
        [InlineData("ABCDEF\nABCDEF")]
        public void Anything_else_is_refused_with_one_sentence(string id)
        {
            var check = GaleProfileId.Check(id);
            Assert.False(check.Ok);
            Assert.False(check.Blank);
            Assert.Null(check.Id);
            Assert.Equal("mods.gale.id.bad", check.SentenceId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        public void Blank_is_off_not_a_mistake(string id)
        {
            var check = GaleProfileId.Check(id);
            Assert.True(check.Blank);
            Assert.False(check.Ok);
            Assert.Null(check.SentenceId);
        }

        /// <summary>
        /// The page cannot call <see cref="GaleProfileId.Check"/>, so it carries the two shapes as
        /// one pattern (the Discord hall's mod profile code box). That pattern has to take and
        /// refuse exactly what this side does, on every row above that a box can hold.
        /// </summary>
        [Theory]
        [InlineData("ABCDEF")]
        [InlineData("ab12CD")]
        [InlineData("AAAAAAAAAAAAAAAAAAAAAQ")]
        [InlineData("_AAAAAAAAAAAAAAAAAAAAA")]
        [InlineData("-b12cd34_ef56gh78ij90g")]
        [InlineData("Ab_9-xY_z0Qr7_Lm-NoPqA")]
        [InlineData("ABCDE")]
        [InlineData("ABCDEFG")]
        [InlineData("ab_12C")]
        [InlineData("not-valid")]
        [InlineData("AAAAAAAAAAAAAAAAAAAAA")]
        [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")]
        [InlineData("AAAAAAAAAAAAAAAAAAAAAB")]
        [InlineData("AAAAAAAAAAAAAAAAAAAAA_")]
        [InlineData("AAAAAAAAAAAA+/AAAAAAAA")]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        [InlineData("a1b2c3d4-e5f6")]
        public void The_page_pattern_takes_exactly_what_the_check_takes(string id)
        {
            Assert.Equal(GaleProfileId.IsWellFormed(id),
                System.Text.RegularExpressions.Regex.IsMatch(id, GaleProfileId.PagePattern));
        }

        [Fact]
        public void Spaces_a_paste_picks_up_are_forgiven_and_dropped()
        {
            var check = GaleProfileId.Check("  ab12CD\r\n");
            Assert.True(check.Ok);
            Assert.Equal("ab12CD", check.Id);
        }

        [Fact]
        public void A_trailing_newline_is_not_part_of_a_well_formed_id()
        {
            // A dollar anchor would take "ABCDEF\n" as six characters and an end.
            Assert.False(GaleProfileId.IsWellFormed("ABCDEF\n"));
            Assert.False(GaleProfileId.IsWellFormed("AAAAAAAAAAAAAAAAAAAAAA\n"));
        }
    }
}
