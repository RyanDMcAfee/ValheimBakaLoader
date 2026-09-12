using System;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// Feature B: a mod row reads "possibly outdated" when its newest Thunderstore release
    /// came out strictly before the game last updated. Either date unknown leaves it false,
    /// and the row always carries the three dated fields the UI reads.
    /// </summary>
    public class BlendWindowPossiblyOutdatedTests
    {
        private static readonly DateTime Game = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        private static JObject Row(DateTime? modReleased, DateTime? gameUpdated) =>
            JObject.FromObject(BlendWindow.BuildModDto(new InstalledMod
            {
                Author = "Owner",
                ModName = "Mod",
                InstalledVersion = "1.0.0",
                LatestReleasedUtc = modReleased,
            }, gameUpdated));

        [Fact]
        public void A_release_before_the_game_update_reads_possibly_outdated()
        {
            var row = Row(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Game);

            Assert.True(row.Value<bool>("possiblyOutdated"));
            Assert.NotNull(row.Value<string>("modUpdatedUtc"));
            Assert.NotNull(row.Value<string>("gameUpdatedUtc"));
        }

        [Fact]
        public void A_release_after_the_game_update_does_not()
        {
            var row = Row(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), Game);

            Assert.False(row.Value<bool>("possiblyOutdated"));
        }

        [Fact]
        public void A_release_on_the_same_moment_does_not_strictly_older_is_the_rule()
        {
            var row = Row(Game, Game);

            Assert.False(row.Value<bool>("possiblyOutdated"));
        }

        [Fact]
        public void An_unknown_mod_date_leaves_it_blank()
        {
            var row = Row(null, Game);

            Assert.False(row.Value<bool>("possiblyOutdated"));
            Assert.Null(row.Value<string>("modUpdatedUtc"));
            Assert.NotNull(row.Value<string>("gameUpdatedUtc"));
        }

        [Fact]
        public void An_unknown_game_date_leaves_it_blank_for_every_row()
        {
            var row = Row(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);

            Assert.False(row.Value<bool>("possiblyOutdated"));
            Assert.NotNull(row.Value<string>("modUpdatedUtc"));
            Assert.Null(row.Value<string>("gameUpdatedUtc"));
        }

        [Fact]
        public void The_single_argument_overload_leaves_the_hint_blank()
        {
            var row = JObject.FromObject(BlendWindow.BuildModDto(new InstalledMod
            {
                Author = "Owner",
                ModName = "Mod",
                InstalledVersion = "1.0.0",
                LatestReleasedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            }));

            Assert.False(row.Value<bool>("possiblyOutdated"));
            Assert.Null(row.Value<string>("gameUpdatedUtc"));
        }

        // Grace period: a mod that predates a Valheim update is not flagged until the update
        // itself is older than a week, so the whole list does not light up the day of a patch.
        private static JObject RowAt(DateTime? modReleased, DateTime? gameUpdated, DateTime nowUtc) =>
            JObject.FromObject(BlendWindow.BuildModDto(new InstalledMod
            {
                Author = "Owner",
                ModName = "Mod",
                InstalledVersion = "1.0.0",
                LatestReleasedUtc = modReleased,
            }, gameUpdated, nowUtc));

        [Fact]
        public void A_fresh_game_update_flags_nothing_within_the_grace_week()
        {
            var now = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
            var patchedTwoDaysAgo = now.AddDays(-2);
            var modFromLastYear = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var row = RowAt(modFromLastYear, patchedTwoDaysAgo, now);

            Assert.False(row.Value<bool>("possiblyOutdated"));
            Assert.NotNull(row.Value<string>("gameUpdatedUtc"));
        }

        [Fact]
        public void Once_the_update_is_past_the_grace_week_a_stale_mod_is_flagged()
        {
            var now = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
            var patchedTenDaysAgo = now.AddDays(-10);
            var modFromBeforeThePatch = now.AddDays(-40);

            var row = RowAt(modFromBeforeThePatch, patchedTenDaysAgo, now);

            Assert.True(row.Value<bool>("possiblyOutdated"));
        }

        [Fact]
        public void A_mod_updated_after_a_long_past_patch_is_not_flagged()
        {
            var now = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
            var patchedTenDaysAgo = now.AddDays(-10);
            var modUpdatedYesterday = now.AddDays(-1);

            var row = RowAt(modUpdatedYesterday, patchedTenDaysAgo, now);

            Assert.False(row.Value<bool>("possiblyOutdated"));
        }
    }
}
