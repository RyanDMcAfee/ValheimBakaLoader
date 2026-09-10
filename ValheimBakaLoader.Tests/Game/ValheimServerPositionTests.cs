using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The roster's Position column is filled from one "playerlist" reply covering everybody
    /// online, not a call per player. This is the parse that turns that one reply into the
    /// map the column reads.
    /// </summary>
    public class ValheimServerPositionTests
    {
        [Fact]
        public void Every_player_in_one_reply_is_read_at_once()
        {
            var reply = string.Join("\n",
                "Broheim/76561198012345678/-1245 (123.4, 30.6, -870.2)",
                "Sigrun/76561198087654321/-9911 (-12.9, 5.1, 44.4)");

            var found = ValheimServer.ParseAllPositions(reply);

            Assert.Equal(2, found.Count);
            Assert.Equal("123, 31, -870", found["Broheim"]);
            Assert.Equal("-13, 5, 44", found["Sigrun"]);
        }

        [Fact]
        public void The_log_clock_the_rcon_channel_prepends_is_not_read_as_part_of_the_name()
        {
            // The RCON channel echoes each line with the server's own log stamp in front. Left
            // alone it lands inside the name, and the name is the key the roster looks up.
            var reply = "09/10/2026 14:05:06: Broheim/76561198012345678/-1245 (10.0, 2.0, 3.0)";

            var found = ValheimServer.ParseAllPositions(reply);

            Assert.True(found.ContainsKey("Broheim"), "the log clock was read as part of the name");
            Assert.Equal("10, 2, 3", found["Broheim"]);
        }

        [Fact]
        public void A_console_prefix_is_stripped_too()
        {
            var found = ValheimServer.ParseAllPositions("Console: Sigrun/765611980/-99 (1.2, 3.4, 5.6)");

            Assert.Equal("1, 3, 6", found["Sigrun"]);
        }

        [Fact]
        public void A_name_with_a_space_in_it_survives()
        {
            var found = ValheimServer.ParseAllPositions("Baka Ryan/76561198012345678/-1 (0.0, 0.0, 0.0)");

            Assert.Equal("0, 0, 0", found["Baka Ryan"]);
        }

        [Fact]
        public void A_reply_with_nobody_in_it_reads_as_nobody()
        {
            Assert.Empty(ValheimServer.ParseAllPositions("No players online"));
            Assert.Empty(ValheimServer.ParseAllPositions(""));
            Assert.Empty(ValheimServer.ParseAllPositions(null));
        }

        [Fact]
        public void Lookup_is_case_insensitive_because_the_roster_and_the_reply_disagree_on_case()
        {
            var found = ValheimServer.ParseAllPositions("Broheim/765/-1 (7.7, 8.8, 9.9)");

            Assert.True(found.ContainsKey("BROHEIM"));
            Assert.Equal("8, 9, 10", found["broheim"]);
        }
    }
}
