using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The version comparison every "is there a newer one?" question in the app rests
    /// on. The table below is the contract: semantic-versioning precedence, with a
    /// leading v tolerated, missing parts read as zero, a fourth part kept as a
    /// tie-break, and anything unreadable sorting lowest without ever counting as
    /// newer. Nothing here is allowed to throw.
    /// </summary>
    public class SemVerTests
    {
        // --- The core, component by component, as numbers rather than text ---

        [Theory]
        [InlineData("1.0.7", "1.0.666", -1)]    // 666 is a number, not text: 7 is lower
        [InlineData("1.0.666", "1.0.7", 1)]
        [InlineData("1.0.69", "1.0.7", 1)]
        [InlineData("2.0.0", "1.99.99", 1)]
        [InlineData("1.2.3", "1.2.3", 0)]
        [InlineData("1.2", "1.2.0", 0)]         // a missing part reads as zero
        [InlineData("1.2.0.0", "1.2", 0)]
        [InlineData("1.0.0.1", "1.0.0", 1)]     // the fourth part is a tie-break
        [InlineData("1.0.0", "1.0.0.1", -1)]
        [InlineData("1.0.0.2", "1.0.0.10", -1)]
        [InlineData("v1.2.3", "1.2.3", 0)]      // a leading v is tolerated
        [InlineData("V1.2.4", "1.2.3", 1)]
        [InlineData("1.2.3+build9", "1.2.3", 0)] // build metadata carries no precedence
        [InlineData("1.2.3+a", "1.2.3+b", 0)]
        public void Compares_numeric_cores(string a, string b, int expected)
        {
            Assert.Equal(expected, SemVer.Compare(a, b));
        }

        // --- Pre-release precedence ---

        [Theory]
        [InlineData("2.0.13-beta.1", "2.0.13", -1)]   // a pre-release is below its release
        [InlineData("2.0.13", "2.0.13-beta.1", 1)]
        [InlineData("2.0.13-beta.1", "2.0.11", 1)]    // but still above the release before it
        [InlineData("2.0.13-beta.1", "2.0.13-beta.2", -1)]
        [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]  // numeric identifiers compare as numbers
        [InlineData("1.0.0-beta.10", "1.0.0-beta.2", 1)]
        [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
        [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]   // more identifiers wins a tie
        [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)] // a number ranks below text
        [InlineData("1.0.0-rc.1", "1.0.0-rc.1", 0)]
        [InlineData("1.0.0-rc.1+build", "1.0.0-rc.1", 0)]
        public void Compares_pre_release_precedence(string a, string b, int expected)
        {
            Assert.Equal(expected, SemVer.Compare(a, b));
        }

        /// <summary>
        /// The whole semantic-versioning example chain, in order, each rung against the
        /// next. This is the case the old comparison got wrong: it threw the suffix away,
        /// so every rung read as equal to 1.0.0.
        /// </summary>
        [Fact]
        public void Orders_the_semver_example_chain()
        {
            var chain = new[]
            {
                "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
                "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0",
            };

            for (var i = 0; i < chain.Length - 1; i++)
            {
                Assert.Equal(-1, SemVer.Compare(chain[i], chain[i + 1]));
                Assert.Equal(1, SemVer.Compare(chain[i + 1], chain[i]));
                Assert.True(SemVer.IsNewer(chain[i + 1], chain[i]));
                Assert.False(SemVer.IsNewer(chain[i], chain[i + 1]));
            }
        }

        // --- Unreadable versions sort lowest and are never newer ---

        [Theory]
        [InlineData("unknown", "1.0.0", -1)]
        [InlineData("1.0.0", "unknown", 1)]
        [InlineData("", "1.0.0", -1)]
        [InlineData("   ", "1.0.0", -1)]
        [InlineData(null, "1.0.0", -1)]
        [InlineData("garbage", "0.0.0", -1)]   // lower than even the lowest real version
        [InlineData("unknown", "nonsense", 0)] // two unreadable versions rank the same
        [InlineData("-1.0.0", "0.0.1", -1)]
        public void Unreadable_versions_sort_lowest(string a, string b, int expected)
        {
            Assert.Equal(expected, SemVer.Compare(a, b));
        }

        [Theory]
        [InlineData("garbage", "1.0.0")]
        [InlineData("unknown", "1.0.0")]
        [InlineData("", "1.0.0")]
        [InlineData(null, "1.0.0")]
        [InlineData("1.0.0", null)]
        [InlineData("1.0.0", "")]
        public void Unreadable_is_never_newer(string latest, string installed)
        {
            Assert.False(SemVer.IsNewer(latest, installed));
        }

        /// <summary>
        /// The one behaviour change this brings to the Thunderstore path: a folder sitting
        /// on a suffixed version is now offered the plain release, because the suffix is
        /// no longer thrown away. Everything without a suffix answers as it always did.
        /// </summary>
        [Fact]
        public void Suffixed_installed_version_is_now_offered_its_release()
        {
            Assert.True(SemVer.IsNewer("2.0.13", "2.0.13-beta.1"));
            Assert.False(SemVer.IsNewer("2.0.13-beta.1", "2.0.13"));

            // And the ordinary case is untouched.
            Assert.True(SemVer.IsNewer("1.66.0", "1.65.0"));
            Assert.False(SemVer.IsNewer("1.65.0", "1.66.0"));
            Assert.False(SemVer.IsNewer("1.65.0", "1.65.0"));
        }

        [Fact]
        public void Reads_pre_release_and_readability()
        {
            Assert.True(SemVer.IsPreRelease("2.0.13-beta.1"));
            Assert.False(SemVer.IsPreRelease("2.0.13"));
            Assert.False(SemVer.IsPreRelease("2.0.13+build"));
            Assert.False(SemVer.IsPreRelease("unknown"));

            Assert.True(SemVer.IsReadable("1.0"));
            Assert.False(SemVer.IsReadable("unknown"));
            Assert.False(SemVer.IsReadable(null));
        }

        [Fact]
        public void Compares_release_lines_ignoring_the_suffix()
        {
            Assert.Equal(0, SemVer.CompareCore("2.0.13-beta.1", "2.0.13"));
            Assert.Equal(1, SemVer.CompareCore("2.0.14-beta.1", "2.0.13"));
            Assert.Equal(-1, SemVer.CompareCore("2.0.12-beta.9", "2.0.13-beta.1"));
        }

        [Theory]
        [InlineData("99999999999999.0.0", "1.0.0")]   // past what an int would hold
        [InlineData("1.2.x", "1.2.0")]
        [InlineData("....", "1.0.0")]
        [InlineData("1.0.0-", "1.0.0")]
        [InlineData("+", "1.0.0")]
        [InlineData("v", "1.0.0")]
        public void Never_throws_on_anything(string a, string b)
        {
            // The answer is not the point here; not throwing is.
            SemVer.Compare(a, b);
            SemVer.Compare(b, a);
            SemVer.IsNewer(a, b);
            SemVer.IsNewer(b, a);
        }

        [Fact]
        public void Big_numbers_are_read_as_numbers()
        {
            Assert.Equal(1, SemVer.Compare("99999999999999.0.0", "1.0.0"));
            Assert.Equal(-1, SemVer.Compare("1.0.0", "99999999999999.0.0"));
        }

        [Fact]
        public void A_trailing_dash_leaves_the_core_standing()
        {
            // "1.0.0-" carries an empty suffix, which is no suffix at all.
            Assert.Equal(0, SemVer.Compare("1.0.0-", "1.0.0"));
        }
    }
}
