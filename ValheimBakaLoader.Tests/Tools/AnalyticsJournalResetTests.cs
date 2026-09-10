using System;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Clearing the statistics journal. The live journal sits in the host's own profile, beside
    /// their save data, so the file work is driven here against a folder of its own rather than
    /// through the service. What is under test is that the old journal is set ASIDE rather than
    /// thrown away, and that exactly one set-aside copy is ever kept - which only holds while
    /// the name a copy is given and the pattern that clears the earlier ones agree.
    /// </summary>
    public class AnalyticsJournalResetTests : IDisposable
    {
        private readonly string Folder =
            Path.Combine(Path.GetTempPath(), "vbl-journal-tests-" + Guid.NewGuid().ToString("N"));

        public AnalyticsJournalResetTests() => Directory.CreateDirectory(Folder);

        public void Dispose()
        {
            try { Directory.Delete(Folder, recursive: true); } catch { /* best effort */ }
        }

        private string Journal => Path.Combine(Folder, "analytics.json");

        [Fact]
        public void The_old_journal_is_set_aside_rather_than_thrown_away()
        {
            File.WriteAllText(Journal, "{\"events\":[{\"k\":\"start\"}]}");

            var moved = AnalyticsService.SetJournalAside(Journal, new DateTime(2026, 9, 10, 14, 5, 6));

            Assert.NotNull(moved);
            Assert.False(File.Exists(Journal));
            Assert.True(File.Exists(moved));
            Assert.Equal("analytics.json.bak-20260910-140506", Path.GetFileName(moved));

            // Months of counted history: it has to still be in there afterwards.
            Assert.Equal("{\"events\":[{\"k\":\"start\"}]}", File.ReadAllText(moved));
        }

        [Fact]
        public void Only_one_set_aside_copy_is_ever_kept()
        {
            File.WriteAllText(Journal, "first");
            var first = AnalyticsService.SetJournalAside(Journal, new DateTime(2026, 1, 1, 1, 1, 1));

            File.WriteAllText(Journal, "second");
            var second = AnalyticsService.SetJournalAside(Journal, new DateTime(2026, 2, 2, 2, 2, 2));

            var left = Directory.GetFiles(Folder).Select(Path.GetFileName).ToList();

            Assert.Single(left);
            Assert.Equal(Path.GetFileName(second), left[0]);
            Assert.False(File.Exists(first));
            Assert.Equal("second", File.ReadAllText(second));
        }

        [Fact]
        public void A_name_a_copy_can_be_given_is_a_name_the_sweep_can_find()
        {
            // The half of "keep one" that breaks silently: change the name a copy is given, or
            // the pattern that clears the earlier ones, and a host who clears the numbers every
            // week leaves a year of journals behind with nothing ever saying so.
            var copy = AnalyticsService.JournalBackupName(Journal, new DateTime(2026, 9, 10, 14, 5, 6));
            File.WriteAllText(copy, "old");

            var found = Directory.GetFiles(Folder, AnalyticsService.JournalBackupPattern(Journal));

            Assert.Single(found);
            Assert.Equal(copy, found[0]);
        }

        [Fact]
        public void A_copy_sits_beside_the_journal_it_came_from()
        {
            var copy = AnalyticsService.JournalBackupName(Journal, DateTime.Now);

            Assert.Equal(Folder, Path.GetDirectoryName(copy));
            Assert.StartsWith("analytics.json.bak-", Path.GetFileName(copy), StringComparison.Ordinal);
        }

        [Fact]
        public void Nothing_on_disk_means_nothing_to_set_aside()
        {
            Assert.Null(AnalyticsService.SetJournalAside(Journal, DateTime.Now));
            Assert.Null(AnalyticsService.SetJournalAside(null, DateTime.Now));
            Assert.Null(AnalyticsService.SetJournalAside("   ", DateTime.Now));
        }
    }
}
