using System.Collections.Generic;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The guard on the host-typed extra launch arguments. Valheim 1.0 added
    /// two flags that must never reach a dedicated server: -demomode turns off
    /// all world saving, and -joinserverwithcharacter makes the game try to
    /// join a server instead of hosting one.
    /// </summary>
    public class ValheimServerArgumentTests
    {
        [Theory]
        [InlineData("-demomode", "")]
        [InlineData("-joinserverwithcharacter", "")]
        [InlineData("-demomode -joinserverwithcharacter", "")]
        [InlineData("-crossplay -demomode -console", "-crossplay -console")]
        [InlineData("-demomode -console", "-console")]
        [InlineData("-console -joinserverwithcharacter", "-console")]
        // The flag is matched however the host cased it.
        [InlineData("-DemoMode -console", "-console")]
        [InlineData("-JOINSERVERWITHCHARACTER -console", "-console")]
        public void BlockedFlagsAreRemoved(string typed, string expected)
        {
            Assert.Equal(expected, ValheimServer.SanitizeAdditionalArgs(typed));
        }

        [Theory]
        // Whole-token matching only: a longer flag that merely starts with the
        // blocked name, or a value that contains it, is the host's business.
        [InlineData("-demomodex")]
        [InlineData("-nodemomode")]
        [InlineData("-name \"demomode\"")]
        [InlineData("-crossplay -console")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void EverythingElseIsPassedThroughUntouched(string typed)
        {
            Assert.Equal(typed, ValheimServer.SanitizeAdditionalArgs(typed));
        }

        [Fact]
        public void QuotedValuesSurviveTheRemoval()
        {
            var sanitized = ValheimServer.SanitizeAdditionalArgs(
                "-demomode -logfile \"C:\\my server logs\\out.txt\"");

            Assert.Equal("-logfile \"C:\\my server logs\\out.txt\"", sanitized);
        }

        [Fact]
        public void RemovedFlagsAreReportedToTheCaller()
        {
            ValheimServer.SanitizeAdditionalArgs(
                "-demomode -console -JoinServerWithCharacter", out var removed);

            Assert.Equal(new List<string> { "-demomode", "-JoinServerWithCharacter" }, removed);
        }

        [Fact]
        public void NothingIsReportedWhenNothingWasRemoved()
        {
            ValheimServer.SanitizeAdditionalArgs("-crossplay -console", out var removed);

            Assert.Empty(removed);
        }
    }
}
