using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The empty-server decision on its own: two switches and the Windows half, the shared delay,
    /// whether the server is up and how many are on it. Every answer the server acts on comes out
    /// of here, so every combination is asked here rather than through a process.
    /// </summary>
    public class EmptyServerPlanTests
    {
        [Theory]
        //        restart stop   windows  expected
        [InlineData(false, false, false, EmptyServerAction.None)]
        [InlineData(true,  false, false, EmptyServerAction.Restart)]
        [InlineData(false, true,  false, EmptyServerAction.Stop)]
        [InlineData(false, true,  true,  EmptyServerAction.StopThenWindows)]
        // (a) both on: the stop wins
        [InlineData(true,  true,  false, EmptyServerAction.Stop)]
        [InlineData(true,  true,  true,  EmptyServerAction.StopThenWindows)]
        // (g) the Windows half with the stop off is never a power-off
        [InlineData(false, false, true,  EmptyServerAction.None)]
        [InlineData(true,  false, true,  EmptyServerAction.Restart)]
        public void Each_set_of_switches_has_one_answer(bool restart, bool stop, bool windows, EmptyServerAction expected)
        {
            var plan = EmptyServerPlan.Decide(restart, stop, windows, 5, running: true, activePlayers: 0);

            Assert.Equal(expected, plan.Action);
            Assert.Equal(5, plan.DelayMinutes);
            Assert.Equal(expected == EmptyServerAction.StopThenWindows, plan.ShutsDownWindows);
            Assert.Equal(expected is EmptyServerAction.Stop or EmptyServerAction.StopThenWindows, plan.Stops);
        }

        [Fact]
        public void Both_switches_on_says_the_restart_was_set_aside_and_only_then()
        {
            Assert.True(EmptyServerPlan.Decide(true, true, false, 5, true, 0).RestartIgnored);
            Assert.True(EmptyServerPlan.Decide(true, true, true, 5, true, 0).RestartIgnored);

            Assert.False(EmptyServerPlan.Decide(true, false, false, 5, true, 0).RestartIgnored);
            Assert.False(EmptyServerPlan.Decide(false, true, true, 5, true, 0).RestartIgnored);
            Assert.False(EmptyServerPlan.Decide(false, false, false, 5, true, 0).RestartIgnored);
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 1)]
        [InlineData(false, 3)]
        public void A_server_that_is_down_or_has_somebody_on_it_waits_for_nothing(bool running, int players)
        {
            var plan = EmptyServerPlan.Decide(true, true, true, 5, running, players);
            Assert.Equal(EmptyServerAction.None, plan.Action);
            Assert.False(plan.Stops);
            Assert.False(plan.ShutsDownWindows);
            Assert.False(plan.RestartIgnored);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-30, 1)]
        [InlineData(1, 1)]
        [InlineData(45, 45)]
        [InlineData(int.MaxValue, EmptyServerPlan.MaxDelayMinutes)]
        public void The_delay_is_held_inside_what_a_timer_can_wait(int asked, int expected)
        {
            Assert.Equal(expected, EmptyServerPlan.Decide(false, true, false, asked, true, 0).DelayMinutes);

            // and the longest one really is inside a timer's reach, which tops out a little
            // under 24.9 days
            Assert.True((long)EmptyServerPlan.MaxDelayMinutes * 60 * 1000 < int.MaxValue);
        }

        [Fact]
        public void The_switches_record_is_read_the_same_as_the_loose_values()
        {
            var switches = new EmptyServerSwitches(Restart: true, Stop: true, StopShutsDownWindows: true, DelayMinutes: 7);
            Assert.Equal(EmptyServerPlan.Decide(true, true, true, 7, true, 0),
                         EmptyServerPlan.Decide(switches, running: true, activePlayers: 0));

            Assert.Equal(EmptyServerAction.None, EmptyServerPlan.Decide(null, running: true, activePlayers: 0).Action);
        }
    }
}
