using System;
using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// What the app does with a version string it cannot read, in the three places that used
    /// to read the same answer three different wrong ways.
    /// <para>
    /// AssemblyHelper.CompareVersion answers -2 for a version it could not parse, and -2 is
    /// neither greater than zero nor equal to it. So the self-updater's
    /// <c>CompareVersion(tag) != 1</c> filed an unreadable tag under "already current", the
    /// language pack lookup's <c>&lt;= 0</c> filed it under "at or below my version", and the
    /// pack's own minAppVersion gate's <c>&gt; 0</c> filed it under "old enough, go ahead". One
    /// release tagged v1.2.9.0 would have switched updating off for every host and made the log
    /// assert the opposite, and the app ships a comparer that reads that tag perfectly well.
    /// </para>
    /// </summary>
    public class UnreadableVersionTests : BaseTest
    {
        // ------------------------------------------------------------- the four-component tag

        [Theory]
        [InlineData("1.2.6.0", "1.2.5", 1)]
        [InlineData("v1.2.9.0", "1.2.5", 1)]
        [InlineData("v1.3.0.0", "1.2.5", 1)]
        [InlineData("v2.0.0.0", "1.2.5", 1)]
        [InlineData("1.2.5.0", "1.2.5", 0)]
        [InlineData("1.2.4.0", "1.2.5", -1)]
        public void A_four_component_tag_is_read_as_a_version_rather_than_as_nothing(
            string tag, string against, int expected)
        {
            Assert.Equal(expected, AssemblyHelper.CompareVersion(tag, against));
        }

        /// <summary>
        /// The comparer the mod side has always used reads the same tags the same way, which is
        /// the whole reason this is a fallback rather than a second opinion. Two comparers in one
        /// app disagreeing about what a version is was the defect.
        /// </summary>
        [Theory]
        [InlineData("1.2.6.0", "1.2.5")]
        [InlineData("1.2.9.0", "1.2.5")]
        public void The_two_comparers_this_app_ships_now_agree_about_a_four_component_tag(
            string tag, string against)
        {
            Assert.Equal(
                Math.Sign(ValheimBakaLoader.Tools.SemVer.Compare(tag, against)),
                Math.Sign(AssemblyHelper.CompareVersion(tag, against)));
        }

        /// <summary>
        /// And a tag that is genuinely not a version is still not one. A fallback that answered
        /// "0, the same" for "nightly" would be a worse bug than the one it replaced.
        /// </summary>
        [Theory]
        [InlineData("nightly")]
        [InlineData("latest")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("main")]
        public void A_tag_that_is_not_a_version_at_all_is_still_unreadable(string tag)
        {
            Assert.Equal(-2, AssemblyHelper.CompareVersion(tag, "1.2.5"));
        }

        /// <summary>The ordinary shapes are untouched, which is what makes this additive.</summary>
        [Theory]
        [InlineData("1.2.6", "1.2.5", 1)]
        [InlineData("v1.2.6", "1.2.5", 1)]
        [InlineData("1.2.5", "1.2.5", 0)]
        [InlineData("1.2.4", "1.2.5", -1)]
        [InlineData("1.10.0", "1.9.0", 1)]
        public void The_shapes_that_always_worked_still_answer_the_same(
            string tag, string against, int expected)
        {
            Assert.Equal(expected, AssemblyHelper.CompareVersion(tag, against));
        }

        // ------------------------------------------------------- the self-updater's own gate

        /// <summary>
        /// The line the self-updater reads. It is spelled out here rather than driven through
        /// the service, because what went wrong was the SHAPE of the comparison rather than
        /// anything the service does with it: -2 fell into the same branch as -1 and 0.
        /// </summary>
        [Theory]
        [InlineData("v1.2.9.0", false)]   // the tag that used to say "you are current"
        [InlineData("v1.3.0.0", false)]
        [InlineData("v1.2.9", false)]
        [InlineData("v1.2.5", true)]
        [InlineData("v1.2.4", true)]
        public void A_newer_release_is_never_filed_as_already_current(string tag, bool current)
        {
            var answer = AssemblyHelper.CompareVersion(tag, "1.2.5");
            Assert.Equal(current, answer != 1);
        }

        // -------------------------------------------------- the language pack's tag filter

        [Theory]
        [InlineData("1.2.4", true)]
        [InlineData("v1.2.4", true)]
        [InlineData("1.2.9.0", true)]
        [InlineData("nightly", false)]
        [InlineData("latest", false)]
        [InlineData("", false)]
        public void Only_a_tag_the_app_can_read_is_a_candidate_for_a_pack(string tag, bool readable)
        {
            Assert.Equal(readable, LanguagePackService.Readable(tag));
        }

        /// <summary>
        /// The gate the pack lookup applies, as the lookup applies it: readable first, then at
        /// or below the running version. Before the readable test, "nightly" and "v1.9.0.0" both
        /// passed the second half because -2 is less than zero, and the manifest every digest,
        /// weight and pack address in that flow is derived from came off one of them.
        /// </summary>
        [Theory]
        [InlineData("v1.2.4", true)]
        [InlineData("v1.2.5", true)]
        [InlineData("v1.9.0", false)]     // newer, correctly excluded, and always was
        [InlineData("v1.9.0.0", false)]   // newer, and used to be INCLUDED
        [InlineData("nightly", false)]    // not a version, and used to be included
        public void A_pack_candidate_is_readable_and_at_or_below_the_running_version(
            string tag, bool candidate)
        {
            var version = tag.TrimStart('v', 'V');
            var allowed = LanguagePackService.Readable(version)
                && AssemblyHelper.CompareVersion(version, "1.2.5") <= 0;

            Assert.Equal(candidate, allowed);
        }

        /// <summary>
        /// And the ordering. VersionOrder fell back to string.CompareOrdinal whenever the
        /// comparison answered -2, so "nightly" outranked "1.2.4" on its first character. An
        /// unreadable name sorts lowest now, which is where a name nobody can read belongs.
        /// </summary>
        [Fact]
        public void An_unreadable_name_sorts_below_every_real_version()
        {
            var order = VersionOrderInstance();
            var sorted = new[] { "1.2.0", "nightly", "1.2.4", "latest", "1.2.9.0" }
                .OrderByDescending(v => v, order)
                .ToArray();

            Assert.Equal("1.2.9.0", sorted[0]);
            Assert.Equal("1.2.4", sorted[1]);
            Assert.Equal("1.2.0", sorted[2]);
            Assert.Contains(sorted[3], new[] { "nightly", "latest" });
            Assert.Contains(sorted[4], new[] { "nightly", "latest" });
        }

        private static System.Collections.Generic.IComparer<string> VersionOrderInstance()
        {
            var type = typeof(LanguagePackService).GetNestedType(
                "VersionOrder",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            Assert.NotNull(type);
            return (System.Collections.Generic.IComparer<string>)Activator.CreateInstance(type);
        }

        // ------------------------------------------------- the pack's own minAppVersion gate

        /// <summary>
        /// A minAppVersion that is present and unreadable is not a comparison that came out
        /// favourable: it is a manifest field this app cannot honour, and the safe answer on an
        /// untrusted document is to refuse. The gate's own comment promises a refusal "by
        /// comparing versions rather than text", and -2 is not a comparison.
        /// </summary>
        [Theory]
        [InlineData("1.3.0", true)]
        [InlineData("1.3", true)]
        [InlineData("1.3.0.0", true)]    // the four-component spelling that used to be allowed
        [InlineData("latest", true)]     // and the one that was never a version at all
        [InlineData("1.2.5", false)]
        [InlineData("1.2.0", false)]
        [InlineData("1.2.0.0", false)]
        public void A_pack_naming_an_app_this_one_is_not_is_refused_however_it_is_spelled(
            string minAppVersion, bool refused)
        {
            var answer = AssemblyHelper.CompareVersion(minAppVersion, "1.2.5");
            Assert.Equal(refused, answer > 0 || answer == -2);
        }

        /// <summary>
        /// The gate in the source really reads it that way. The value is decided inside a private
        /// method on a service that wants a whole container and a live GitHub, so the shape is
        /// held here and the behaviour by the table above.
        /// <para>
        /// 1.2.6 split the two answers apart. Both are still a refusal, which is what the table
        /// above holds, but an unreadable field is a damaged manifest and not an old app, so it
        /// carries its own reason rather than the sentence that tells the host to go and update
        /// BakaLoader, which would not change the answer.
        /// </para>
        /// </summary>
        [Fact]
        public void The_pack_gate_in_the_source_refuses_the_unreadable_answer()
        {
            var source = AppSourceTree.Read("ValheimBakaLoader", "Tools", "LanguagePackService.cs");
            Assert.Contains("if (against == -2)", source);
            Assert.Contains("LanguagePackReasons.MinUnreadable", source);
            Assert.Contains("if (against > 0)", source);

            // And the unreadable branch is the one that answers first, or the newer-app sentence
            // would swallow it again.
            var unreadable = source.IndexOf("if (against == -2)", StringComparison.Ordinal);
            var newer = source.IndexOf("if (against > 0)", StringComparison.Ordinal);
            Assert.True(unreadable > 0 && newer > unreadable,
                "the newer-app branch is read before the unreadable one");
        }
    }
}
