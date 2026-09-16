using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The spawn used to report success for any reply at all, because the only thing it looked
    /// at was whether the socket had answered. The console answers a refusal just as readily as
    /// a success, so on the day Valheim 1.0.12 turned PlayerProfile.s_bypassCheatChecks into a
    /// property the server said "Command 'baka_spawn ...' failed: System.MissingFieldException"
    /// to every spawn the host asked for, and the interface toasted a cheerful confirmation over
    /// the top of it. One item on the ground, no quality, and nothing on screen saying so.
    ///
    /// These pin the rule that replaced it: the opening word of the server's own line decides,
    /// and the line itself is what the host gets shown.
    /// </summary>
    public class ValheimServerSpawnReplyTests
    {
        // ---- the two lines that mean it worked ----

        /// <summary>Commander spawns in place and reports what it made.</summary>
        [Fact]
        public void CommanderSpawnedLineIsASuccess()
        {
            var result = ValheimServer.ParseSpawnReply(
                "Spawned 6x MeadPoisonResist as 1 stack, placed at (1, 2, 3)");

            Assert.True(result.Ok);
            Assert.Equal("Spawned 6x MeadPoisonResist as 1 stack, placed at (1, 2, 3)", result.Message);
        }

        /// <summary>
        /// The spawn helper cannot instantiate from the console thread, so it queues the work to
        /// the Unity main thread and says so. Queued is as good as done for the host: the command
        /// was understood and accepted.
        /// </summary>
        [Fact]
        public void SpawnHelperQueuedLineIsASuccess()
        {
            var result = ValheimServer.ParseSpawnReply(
                "Queued spawn: 6x MeadPoisonResist (level 0) at (...)");

            Assert.True(result.Ok);
            Assert.Equal("Queued spawn: 6x MeadPoisonResist (level 0) at (...)", result.Message);
        }

        // ---- and everything else, which does not ----

        /// <summary>
        /// The reply that was there all along while the interface said the spawn had worked.
        /// A MissingFieldException reads as a failure now and the host sees the exception text.
        /// </summary>
        [Fact]
        public void ACommandThatThrewIsAFailureAndKeepsItsWords()
        {
            var result = ValheimServer.ParseSpawnReply(
                "Command 'baka_spawn MeadPoisonResist 1,2,3 6 0' failed: "
                + "System.MissingFieldException: Field 'PlayerProfile.s_bypassCheatChecks' not found.");

            Assert.False(result.Ok);
            Assert.Contains("MissingFieldException", result.Message);
            Assert.Contains("baka_spawn", result.Message);
        }

        [Fact]
        public void AnUnknownPrefabIsAFailure()
        {
            var result = ValheimServer.ParseSpawnReply("Error: prefab 'X' not found in ZNetScene");

            Assert.False(result.Ok);
            Assert.Equal("Error: prefab 'X' not found in ZNetScene", result.Message);
        }

        [Fact]
        public void TheUsageLineIsAFailure()
        {
            var result = ValheimServer.ParseSpawnReply("Usage: baka_spawn <prefab> <x,z,y> [amount] [level]");

            Assert.False(result.Ok);
            Assert.Equal("Usage: baka_spawn <prefab> <x,z,y> [amount] [level]", result.Message);
        }

        /// <summary>
        /// No reply at all, and the empty body an unreachable or silent console sends, mean the
        /// same thing: the command may well have run, but nothing said so, and the whole point
        /// of this is to stop guessing on the host's behalf.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\r\n")]
        public void NothingComingBackIsAFailureThatSaysSo(string reply)
        {
            var result = ValheimServer.ParseSpawnReply(reply);

            Assert.False(result.Ok);
            Assert.Equal("no reply from the server", result.Message);
        }

        // ---- what the RCON channel wraps the line in ----

        /// <summary>
        /// Some setups echo the console line back with the server's log clock and a "Console:"
        /// tag in front of it, the same two prefixes the roster reader has always had to strip.
        /// Neither is part of what the command answered, and leaving them on would read a real
        /// success as a failure.
        /// </summary>
        [Theory]
        [InlineData("Console: Spawned 1x Lox at level 2, placed at (1, 2, 3)")]
        [InlineData("09/16/2026 12:34:56: Spawned 1x Lox at level 2, placed at (1, 2, 3)")]
        public void ThePrefixesTheChannelAddsAreNotPartOfTheAnswer(string reply)
        {
            var result = ValheimServer.ParseSpawnReply(reply);

            Assert.True(result.Ok);
            Assert.Equal("Spawned 1x Lox at level 2, placed at (1, 2, 3)", result.Message);
        }

        /// <summary>
        /// A failure can arrive with a stack trace hanging off it. The first line already names
        /// what went wrong, and it is the only part a toast has room for.
        /// </summary>
        [Fact]
        public void OnlyTheFirstSpokenLineIsCarried()
        {
            var result = ValheimServer.ParseSpawnReply(
                "\r\nError: prefab 'X' not found in ZNetScene\r\n"
                + "  at BakaLoaderCommander.Spawn (System.String prefabName)\r\n");

            Assert.False(result.Ok);
            Assert.Equal("Error: prefab 'X' not found in ZNetScene", result.Message);
        }

        /// <summary>
        /// The test is the opening of the line, not a search through it: a refusal that happens
        /// to mention the word further along is still a refusal.
        /// </summary>
        [Theory]
        [InlineData("Error: nothing was Spawned")]
        [InlineData("Nothing to spawn; Queued spawn was never reached")]
        public void TheWordHasToOpenTheLine(string reply)
        {
            Assert.False(ValheimServer.ParseSpawnReply(reply).Ok);
        }
    }
}
