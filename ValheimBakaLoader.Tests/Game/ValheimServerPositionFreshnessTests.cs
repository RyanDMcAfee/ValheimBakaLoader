using System;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The roster's Position column is filled from a poll, and a poll can stop answering while
    /// the server is still up: an RCON reply that never comes back leaves the last coordinates
    /// sitting in the cache. A frozen number reads exactly like a live one, so the column has
    /// to stop showing it. Its plain hyphen says "not reported"; a stale coordinate says
    /// "they are standing there", and only one of those is true.
    /// </summary>
    public class ValheimServerPositionFreshnessTests : BaseTest, IDisposable
    {
        private readonly ValheimServer Server;

        public ValheimServerPositionFreshnessTests() => Server = GetService<ValheimServer>();

        public void Dispose()
        {
            try { Server.Dispose(); } catch { /* best effort */ }
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void A_coordinate_nobody_has_confirmed_lately_is_not_served_at_all()
        {
            var taken = new DateTime(2026, 9, 10, 14, 0, 0, DateTimeKind.Utc);
            Server.RecordPositions(
                ValheimServer.ParseAllPositions("Broheim/76561198012345678/-1245 (123.4, 30.6, -870.2)"),
                taken);

            // Fresh: a few polls old at most.
            Assert.Equal("123, 31, -870", Server.GetCachedPosition("Broheim", taken.AddSeconds(6)));

            // Past the window: the roster gets nothing rather than a number from the last
            // reply that ever came back.
            Assert.Null(Server.GetCachedPosition("Broheim", taken.AddMinutes(5)));
        }

        [Fact]
        public void A_reply_that_no_longer_names_somebody_drops_them_rather_than_leaving_them_standing()
        {
            var taken = new DateTime(2026, 9, 10, 14, 0, 0, DateTimeKind.Utc);
            Server.RecordPositions(
                ValheimServer.ParseAllPositions("Broheim/765/-1 (1.0, 2.0, 3.0)\nSigrun/766/-2 (4.0, 5.0, 6.0)"),
                taken);

            Server.RecordPositions(
                ValheimServer.ParseAllPositions("Broheim/765/-1 (7.0, 8.0, 9.0)"),
                taken.AddSeconds(5));

            Assert.Equal("7, 8, 9", Server.GetCachedPosition("Broheim", taken.AddSeconds(6)));
            Assert.Null(Server.GetCachedPosition("Sigrun", taken.AddSeconds(6)));
        }

        [Fact]
        public void The_freshness_window_is_several_polls_wide_so_one_missed_reply_does_not_blank_the_column()
        {
            var taken = new DateTime(2026, 9, 10, 14, 0, 0, DateTimeKind.Utc);

            Assert.True(ValheimServer.PositionIsFresh(taken, taken));
            Assert.True(ValheimServer.PositionIsFresh(taken, taken.AddSeconds(ValheimServer.PositionStaleSeconds - 1)));
            Assert.False(ValheimServer.PositionIsFresh(taken, taken.AddSeconds(ValheimServer.PositionStaleSeconds)));
        }

        [Fact]
        public void A_negative_coordinate_is_written_with_a_plain_hyphen()
        {
            // The roster prints this verbatim, and a minus sign or a dash character in it would
            // travel straight into the interface.
            var found = ValheimServer.ParseAllPositions("Sigrun/766/-2 (-12.9, 5.1, -44.4)");

            var position = found["Sigrun"];
            Assert.Equal("-13, 5, -44", position);
            foreach (var c in position)
            {
                Assert.True(c < 128, $"'{c}' is not a plain ASCII character");
            }
        }
    }
}
