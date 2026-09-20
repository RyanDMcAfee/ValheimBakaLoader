using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The statistics journal is the other file the broken names were written into, and it is
    /// the one that keeps them for six months. The hall groups a player's events by their
    /// platform id and falls back to matching on the CHARACTER NAME for an event that carries
    /// no id, which is what a death is, so a player whose name was stored broken could have
    /// their deaths counted apart from their visits.
    /// <para>
    /// The fixture was broken on Windows-1252, so every table below names that page. Left to
    /// the machine's own, this whole file would pass here and fail on a Russian or a Japanese
    /// host, where refusing a 1252 shaped name is the correct answer rather than a defect. The
    /// one exception is the last test, which makes the read the app itself makes, names no
    /// page, and says why it is stopping when this machine's is not the fixture's.
    /// </para>
    /// </summary>
    public class AnalyticsJournalRepairTests
    {
        private readonly ITestOutputHelper Output;

        public AnalyticsJournalRepairTests(ITestOutputHelper output) => Output = output;

        private static string FixturePath =>
            Path.Combine(Directory.GetCurrentDirectory(), "Resources", "mojibake", "analytics.json");

        [Fact]
        public void The_fixture_really_does_hold_the_broken_name()
        {
            // Read raw, so a fixture somebody's editor mended on the way in cannot pass.
            var text = File.ReadAllText(FixturePath);
            Assert.Contains(Mojibake.GreekBroken, text);
            Assert.Contains(Mojibake.French, text);
        }

        [Fact]
        public void Every_name_and_character_in_the_journal_is_put_back()
        {
            var events = AnalyticsService.ReadJournal(FixturePath, TextRepair.Page(1252));

            Assert.Equal(5, events.Count);
            Assert.DoesNotContain(events, e => e.PlayerName == Mojibake.GreekBroken);
            Assert.DoesNotContain(events, e => e.Character == Mojibake.GreekBroken);

            var joined = events.Single(e => e.Kind == "join" && e.PlayerKey == "Steam:76561198000000001");
            Assert.Equal(Mojibake.Greek, joined.PlayerName);
            Assert.Equal(Mojibake.Greek, joined.Character);

            // The death carries no platform id at all, which is the event the hall folds in by
            // character name. Broken, it folded into nobody and became a player of its own.
            var death = events.Single(e => e.Kind == "death");
            Assert.True(string.IsNullOrEmpty(death.PlayerKey));
            Assert.Equal(Mojibake.Greek, death.Character);
            Assert.Equal(joined.Character, death.Character);
        }

        [Fact]
        public void An_honest_name_and_an_event_with_no_name_at_all_are_left_as_they_are()
        {
            var events = AnalyticsService.ReadJournal(FixturePath, TextRepair.Page(1252));

            var french = events.Single(e => e.PlayerKey == "Steam:76561198000000002");
            Assert.Equal(Mojibake.French, french.PlayerName);
            Assert.Equal(Mojibake.French, french.Character);

            var start = events.Single(e => e.Kind == "start");
            Assert.Null(start.PlayerName);
            Assert.Null(start.Character);
        }

        [Fact]
        public void A_journal_that_is_not_there_reads_as_no_events_rather_than_as_a_failure()
        {
            var missing = Path.Combine(Path.GetTempPath(), "vbl-no-journal-here.json");
            Assert.Empty(AnalyticsService.ReadJournal(missing, TextRepair.Page(1252)));
            Assert.Empty(AnalyticsService.ReadJournal(null, TextRepair.Page(1252)));
        }

        /// <summary>
        /// The call the app itself makes, which names no page and so reads with the machine's
        /// own. It is what proves the repair is wired into the read rather than merely
        /// available to it, and it is the one test here that cannot be run anywhere: the
        /// fixture's names were broken on 1252, and a host on 1251 or 932 is RIGHT to leave
        /// them exactly as they are. So it says so and stops rather than failing for a correct
        /// refusal. Everything the repair itself does is covered above, on a named page.
        /// </summary>
        [Fact]
        public void The_read_the_app_makes_repairs_the_journal_on_this_machines_own_page()
        {
            if (TextRepair.AnsiPage.CodePage != 1252)
            {
                Output.WriteLine(
                    "Not run: this machine reads in code page " + TextRepair.AnsiPage.CodePage +
                    ", and the fixture's names were broken on 1252. Leaving them alone is the " +
                    "correct answer on this machine, so there is nothing here to prove.");
                return;
            }

            var events = AnalyticsService.ReadJournal(FixturePath);

            var joined = events.Single(e => e.Kind == "join" && e.PlayerKey == "Steam:76561198000000001");
            Assert.Equal(Mojibake.Greek, joined.PlayerName);
            Assert.Equal(Mojibake.Greek, joined.Character);
            Assert.DoesNotContain(events, e => e.PlayerName == Mojibake.GreekBroken);
            Assert.DoesNotContain(events, e => e.Character == Mojibake.GreekBroken);
        }
    }
}
