using System.Collections.Generic;
using System.Reflection;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The guard on the host-typed extra launch arguments. Valheim 1.0 added
    /// two flags that must never reach a dedicated server: -demomode turns off
    /// all world saving, and -joinserverwithcharacter makes the game try to
    /// join a server instead of hosting one. A third, -resetmodifiers, is
    /// BakaLoader's own: it already emits one at the head of the world flags,
    /// and the extra arguments go on the end, so a second one would land after
    /// the modifiers and clear the very keys they had just written.
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
        [InlineData("-resetmodifiers", "")]
        [InlineData("-resetmodifiers -console", "-console")]
        [InlineData("-console -resetmodifiers", "-console")]
        [InlineData("-crossplay -resetmodifiers -console", "-crossplay -console")]
        // The flag is matched however the host cased it.
        [InlineData("-DemoMode -console", "-console")]
        [InlineData("-JOINSERVERWITHCHARACTER -console", "-console")]
        [InlineData("-ResetModifiers -console", "-console")]
        public void BlockedFlagsAreRemoved(string typed, string expected)
        {
            Assert.Equal(expected, ValheimServer.SanitizeAdditionalArgs(typed));
        }

        [Theory]
        // Whole-token matching only: a longer flag that merely starts with the
        // blocked name, or a value that contains it, is the host's business.
        [InlineData("-demomodex")]
        [InlineData("-nodemomode")]
        [InlineData("-resetmodifiersx")]
        [InlineData("-noresetmodifiers")]
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
        public void AHostTypedResetIsReportedLikeTheRest()
        {
            ValheimServer.SanitizeAdditionalArgs("-console -resetmodifiers", out var removed);

            Assert.Equal(new List<string> { "-resetmodifiers" }, removed);
        }

        [Fact]
        public void NothingIsReportedWhenNothingWasRemoved()
        {
            ValheimServer.SanitizeAdditionalArgs("-crossplay -console", out var removed);

            Assert.Empty(removed);
        }

        // ---- baka_spawn: the 4th argument is a star level OR an item quality ----
        //
        // BuildSpawn used to pass the 4th argument only when the catalog entry had a star
        // level, so every quality the picker collected for a tool, weapon or piece of armour
        // was thrown away at the last step and the server was told 0. The picker offered the
        // box, the host filled it in, and the item arrived at quality 1 every time.

        private static string BuildSpawn(ItemCatalogEntry entry, int amount, int levelOrQuality,
            string coords = "100.0,200.0,30.0")
        {
            var build = typeof(ValheimServer).GetMethod(
                "BuildSpawn", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(build);

            return (string)build.Invoke(null, new object[] { entry, amount, levelOrQuality, coords });
        }

        private static ItemCatalogEntry Creature(string prefab) =>
            new ItemCatalogEntry { PrefabName = prefab, HasLevel = true, HasQuality = false };

        private static ItemCatalogEntry Equipment(string prefab) =>
            new ItemCatalogEntry { PrefabName = prefab, HasLevel = false, HasQuality = true };

        private static ItemCatalogEntry PlainItem(string prefab) =>
            new ItemCatalogEntry { PrefabName = prefab, HasLevel = false, HasQuality = false };

        [Fact]
        public void ACreatureCarriesItsStarLevel()
        {
            Assert.Equal("baka_spawn Lox 100.0,200.0,30.0 3 2", BuildSpawn(Creature("Lox"), 3, 2));
        }

        [Fact]
        public void AnItemWithQualitiesCarriesItsQuality()
        {
            Assert.Equal("baka_spawn PickaxeBronze 100.0,200.0,30.0 1 3",
                BuildSpawn(Equipment("PickaxeBronze"), 1, 3));
        }

        [Fact]
        public void AnItemWithNeitherSendsZero()
        {
            Assert.Equal("baka_spawn Wood 100.0,200.0,30.0 50 0", BuildSpawn(PlainItem("Wood"), 50, 4));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-9999)]
        public void TheAmountNeverDropsBelowOne(int amount)
        {
            Assert.Equal("baka_spawn Boar 100.0,200.0,30.0 1 0", BuildSpawn(Creature("Boar"), amount, 0));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-4)]
        public void ANegativeLevelOrQualityIsSentAsZero(int levelOrQuality)
        {
            Assert.Equal("baka_spawn Lox 100.0,200.0,30.0 2 0",
                BuildSpawn(Creature("Lox"), 2, levelOrQuality));
            Assert.Equal("baka_spawn PickaxeBronze 100.0,200.0,30.0 2 0",
                BuildSpawn(Equipment("PickaxeBronze"), 2, levelOrQuality));
        }

        [Fact]
        public void TheCoordinatesGoThroughAsGiven()
        {
            Assert.Equal("baka_spawn MeadPoisonResist -1234.5,678.9,30.0 6 0",
                BuildSpawn(PlainItem("MeadPoisonResist"), 6, 0, "-1234.5,678.9,30.0"));
        }
    }
}
