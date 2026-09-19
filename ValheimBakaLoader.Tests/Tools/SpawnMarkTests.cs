using BakaLoaderSpawn;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The companion plugins compile against the game's own assemblies, so nothing in this
    /// solution can call into them. SpawnMark is one of the two deliberate exceptions: it is
    /// the half of baka_spawn that touches no game type at all, it compiles into BakaLoader
    /// itself through the ordinary source glob, and so the one rule that decides whether a
    /// conjured object carries the game's cheated mark can be held to account on every build,
    /// on any machine, with no dedicated server installed.
    /// <para>
    /// The other half, the spawn loop, can only be exercised against a running server. What
    /// can be pinned about it lives in CompanionPluginSourceTests, and the stopped-window walk
    /// is in Resources\Commander\CLOSED-TEST-CHECKLIST.md.
    /// </para>
    /// </summary>
    public class SpawnMarkTests
    {
        // ------------------------------------------------------------------
        //  The rule
        // ------------------------------------------------------------------

        /// <summary>
        /// Every combination there is. Two inputs, so the table IS the proof rather than a
        /// sample of it.
        /// <list type="bullet">
        /// <item>entry off: nothing is marked, which is the whole point of the change.</item>
        /// <item>entry on, bypass off: exactly what the game's own spawn command does.</item>
        /// <item>entry on, bypass on: a server running with cheat checks bypassed has already
        /// decided none of this counts, and vanilla writes nothing there either.</item>
        /// </list>
        /// </summary>
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void ShouldMark_IsTheHostsAnswerAndTheBypassTogether(bool configMarkSpawned, bool bypassed, bool expected)
        {
            Assert.Equal(expected, SpawnMark.ShouldMark(configMarkSpawned, bypassed));
        }

        /// <summary>
        /// THE REQUEST, stated on its own so it cannot be lost in the table above: with the
        /// entry left alone, nothing BakaLoader spawns is marked. A marked item's tooltip says
        /// it was summoned through cheating, and while one sits in a player's inventory that
        /// player's achievement progress is paused, so a host handing somebody a replacement
        /// axe used to cost them their achievements.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ShouldMark_MarksNothingAtTheDefaultSetting(bool bypassed)
        {
            Assert.False(SpawnMark.ShouldMark(SpawnMark.ConfigDefault, bypassed));
        }

        /// <summary>
        /// Turning the entry on has to restore the game's own behaviour exactly, or the entry
        /// is not the escape hatch it is described as.
        /// </summary>
        [Fact]
        public void ShouldMark_TurnedOnMatchesWhatVanillaWrites()
        {
            // Vanilla's line is Set(ZDOVars.s_cheated, !PlayerProfile.s_bypassCheatChecks).
            for (var bypassed = 0; bypassed < 2; bypassed++)
                Assert.Equal(bypassed == 0, SpawnMark.ShouldMark(true, bypassed == 1));
        }

        // ------------------------------------------------------------------
        //  The config entry
        // ------------------------------------------------------------------

        /// <summary>
        /// OFF. A host replacing lost gear should not have to cost a player their achievements
        /// to do it. This is the line that would have to change for that to stop being true,
        /// and it is the line a host reads as "Default value: false" in their config file.
        /// </summary>
        [Fact]
        public void TheEntryDefaultsToOff()
        {
            Assert.False(SpawnMark.ConfigDefault);
        }

        /// <summary>
        /// The section and the key are what a host types in BepInEx\config, what the wiki
        /// names, and what a rename would silently reset for everybody who had set it.
        /// </summary>
        [Fact]
        public void TheEntryKeepsItsSectionAndItsKey()
        {
            Assert.Equal("Spawning", SpawnMark.ConfigSection);
            Assert.Equal("MarkSpawnedAsCheated", SpawnMark.ConfigKey);
        }

        /// <summary>
        /// The description is the only explanation a host ever gets, because it is written into
        /// their config file above the key. It has to say what the flag does to a player, which
        /// way the default sits, and that turning it off does not un-mark anything already
        /// spawned. A host who reads "Mark spawned items as cheated" and nothing else cannot
        /// tell whether it costs anybody anything.
        /// <para>
        /// It also has to say that the change needs a restart. BepInEx parses a .cfg once, as
        /// the server starts, and keeps no watcher on it, so a host who edits this entry mid
        /// session and spawns sees no difference and concludes the setting does nothing. This
        /// text is the only place that misreading can be headed off.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("achievement")]
        [InlineData("tooltip")]
        [InlineData("drops")]
        [InlineData("default")]
        [InlineData("keep the mark they were given")]
        [InlineData("next time the server starts")]
        public void TheEntryExplainsItselfToTheHost(string expected)
        {
            Assert.Contains(expected, SpawnMark.ConfigDescription);
        }

        /// <summary>
        /// BepInEx writes the description into the .cfg one "## " line at a time, so a carriage
        /// return inside it lands in the file as a stray character at the end of a comment
        /// line. The source is CRLF like every file here, which is exactly how one gets in.
        /// </summary>
        [Fact]
        public void TheDescriptionCarriesNoCarriageReturns()
        {
            Assert.DoesNotContain("\r", SpawnMark.ConfigDescription);
        }
    }
}
