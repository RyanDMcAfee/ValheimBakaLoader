using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The mod row the page reads, where the second mod site is concerned. The keys are
    /// additive: a host who never turned the switch on gets null and false for every one
    /// of them, and none of them touches the flag the update paths read.
    /// </summary>
    public class BlendWindowHexiumRowTests
    {
        private static JObject Row(string installed, string thunderstore, string hexium,
            string source = null, string hexiumOwner = "Owner", string hexiumName = "Mod") =>
            JObject.FromObject(BlendWindow.BuildModDto(new InstalledMod
            {
                Author = "Owner",
                ModName = "Mod",
                InstalledVersion = installed,
                LatestVersion = thunderstore,
                HexiumLatestVersion = hexium,
                HexiumOwner = hexium == null && source == null ? null : hexiumOwner,
                HexiumName = hexium == null && source == null ? null : hexiumName,
                InstalledSource = source,
            }));

        // --- The four combinations the mark has to get right ---

        [Fact]
        public void Installed_below_thunderstore_below_hexium_marks_the_row()
        {
            var row = Row("1.65.0", "1.66.0", "1.67.0");

            Assert.Equal("1.67.0", row.Value<string>("hexiumLatest"));
            Assert.True(row.Value<bool>("hexiumNewer"));
            // The ordinary Thunderstore update is still on offer and still the one the
            // update paths act on.
            Assert.True(row.Value<bool>("UpdateAvailable"));
            Assert.Equal("1.66.0", row.Value<string>("LatestVersion"));
        }

        [Fact]
        public void Installed_below_hexium_below_thunderstore_does_not_mark_the_row()
        {
            // Hexium is ahead of what is installed but behind Thunderstore, so there is
            // nothing to send anybody to the other site for.
            var row = Row("1.65.0", "1.67.0", "1.66.0");

            Assert.Equal("1.66.0", row.Value<string>("hexiumLatest"));
            Assert.False(row.Value<bool>("hexiumNewer"));
            Assert.True(row.Value<bool>("UpdateAvailable"));
        }

        [Fact]
        public void The_two_sites_level_with_each_other_marks_nothing()
        {
            var row = Row("1.65.0", "1.66.0", "1.66.0");

            Assert.Equal("1.66.0", row.Value<string>("hexiumLatest"));
            Assert.False(row.Value<bool>("hexiumNewer"));
        }

        [Fact]
        public void A_package_only_the_second_site_carries_marks_the_row()
        {
            // Thunderstore has nothing at all, so "higher than Thunderstore" is satisfied
            // by there being no Thunderstore version to be behind.
            var row = Row("1.0.0", null, "1.2.0");

            Assert.True(row.Value<bool>("hexiumNewer"));
            Assert.False(row.Value<bool>("UpdateAvailable"));
            Assert.Null(row.Value<string>("LatestVersion"));
        }

        [Fact]
        public void Hexium_level_with_the_install_marks_nothing_even_with_no_thunderstore_version()
        {
            var row = Row("1.2.0", null, "1.2.0");

            Assert.False(row.Value<bool>("hexiumNewer"));
        }

        // --- A pre-release install ---

        [Fact]
        public void A_pre_release_install_is_offered_the_newer_pre_release()
        {
            // The scan hands the row whichever version the pre-release rule picked; the
            // row's job is only to say whether it is ahead of both.
            var row = Row("2.0.11-beta.1", "2.0.11", "2.0.13-beta.1");

            Assert.True(row.Value<bool>("hexiumNewer"));
            // And the plain Thunderstore release is newer than the beta, which is the one
            // behaviour change the new comparison brings to the ordinary path.
            Assert.True(row.Value<bool>("UpdateAvailable"));
        }

        [Fact]
        public void A_pre_release_install_is_not_marked_for_its_own_version()
        {
            var row = Row("2.0.13-beta.1", "2.0.11", "2.0.13-beta.1");

            Assert.False(row.Value<bool>("hexiumNewer"));
            Assert.False(row.Value<bool>("UpdateAvailable"));
        }

        // --- A row whose files came from the second site ---

        [Fact]
        public void A_hexium_copy_says_so_and_is_never_offered_the_thunderstore_update()
        {
            var row = Row("1.0.20", "1.0.22", "1.0.20", source: "hexium");

            Assert.Equal("hexium", row.Value<string>("installedSource"));
            // This is the whole point: Update all, the waiting count and the unattended
            // restart all read UpdateAvailable, and for this row it is false.
            Assert.False(row.Value<bool>("UpdateAvailable"));
            // The row still says Thunderstore has moved on, and offers the swap.
            Assert.True(row.Value<bool>("thunderstoreNewer"));
            Assert.Equal("1.0.22", row.Value<string>("LatestVersion"));
        }

        [Fact]
        public void A_hexium_copy_level_with_thunderstore_offers_nothing()
        {
            var row = Row("1.0.22", "1.0.22", "1.0.22", source: "hexium");

            Assert.False(row.Value<bool>("UpdateAvailable"));
            Assert.False(row.Value<bool>("thunderstoreNewer"));
            Assert.False(row.Value<bool>("hexiumNewer"));
        }

        [Fact]
        public void A_hand_installed_copy_with_no_note_behaves_exactly_as_before()
        {
            // Somebody who fetched a higher build themselves is not offered a step down,
            // because a lower version is not newer.
            var higher = Row("1.0.30", "1.0.22", null);
            Assert.False(higher.Value<bool>("UpdateAvailable"));
            Assert.Null(higher.Value<string>("installedSource"));
            Assert.False(higher.Value<bool>("thunderstoreNewer"));

            // And somebody on a lower one is offered the Thunderstore update, as always.
            var lower = Row("1.0.10", "1.0.22", null);
            Assert.True(lower.Value<bool>("UpdateAvailable"));
        }

        // --- Nothing appears for a host who never turned it on ---

        [Fact]
        public void With_the_switch_off_every_hexium_key_is_empty()
        {
            var row = Row("1.65.0", "1.66.0", null);

            Assert.Null(row.Value<string>("hexiumLatest"));
            Assert.False(row.Value<bool>("hexiumNewer"));
            Assert.Null(row.Value<string>("hexiumUrl"));
            Assert.Null(row.Value<string>("installedSource"));
            Assert.False(row.Value<bool>("thunderstoreNewer"));
            // And the row is exactly the row it was before any of this existed.
            Assert.True(row.Value<bool>("UpdateAvailable"));
        }

        [Fact]
        public void The_page_address_is_built_by_the_app_from_the_identity_the_site_answered_with()
        {
            var row = Row("1.0.0", "1.0.0", "1.0.0", hexiumOwner: "bruceirons-team", hexiumName: "Oarsmen");

            Assert.Equal("https://valheim.hexium.gg/mods/bruceirons-team/Oarsmen", row.Value<string>("hexiumUrl"));
        }

        [Fact]
        public void An_identity_that_is_not_a_plain_pair_yields_no_address_at_all()
        {
            var row = Row("1.0.0", "1.0.0", "1.0.0", hexiumOwner: "../..", hexiumName: "Mod");

            Assert.Null(row.Value<string>("hexiumUrl"));
        }

        [Fact]
        public void Every_key_the_page_already_read_is_still_there()
        {
            var row = Row("1.65.0", "1.66.0", "1.67.0");

            foreach (var key in new[]
            {
                "Author", "ModName", "FullName", "InstalledVersion", "LatestVersion",
                "UpdateAvailable", "PluginDirectory", "IsPatcher", "PatcherDirectory",
                "thunderstoreNamespace", "thunderstoreName", "thunderstoreUrl",
                "possiblyOutdated", "modUpdatedUtc", "gameUpdatedUtc",
            })
            {
                Assert.True(row.ContainsKey(key), "the mod row lost the key " + key);
            }
        }
    }
}
