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
    }
}
