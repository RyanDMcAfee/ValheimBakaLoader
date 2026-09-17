using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// What pressing Scan actually does now, and the rules the row keeps afterwards.
    /// <para>
    /// A scan used to read whatever BakaLoader happened to be holding, which for fifteen
    /// minutes at a time was nothing new at all. It now asks both sites again, and the
    /// promise that the second site is silent while its switch is off has to survive that:
    /// a forced scan with the switch off still contacts nobody.
    /// </para>
    /// </summary>
    public class ScanRefreshTests : BaseTest
    {
        private static string FixtureJson() =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Resources", "hexium", "index_small.json"));

        private (HexiumScanStep Step, HexiumClient Client, RecordingHttpHandler Handler) Build()
        {
            var provider = new RecordingHttpClientProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(FixtureJson(), System.Text.Encoding.UTF8, "application/json"),
            });
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());
            return (new HexiumScanStep(client, GetService<IApplicationLogger>()), client, provider.Handler);
        }

        private static List<InstalledMod> Rows() => new()
        {
            new InstalledMod { Author = "DrakeMods", ModName = "LockSmith", InstalledVersion = "0.3.5", LatestVersion = "0.3.5" },
            new InstalledMod { Author = "Orjat", ModName = "SlavesIncorporated", InstalledVersion = "1.0.5", LatestVersion = "1.0.5" },
        };

        // --- The second site follows the same press, and the same switch ---

        [Fact]
        public async Task A_forced_scan_with_the_switch_off_still_contacts_nobody()
        {
            var (step, _, handler) = Build();

            // Ten forced scans. The force is inside the gate, not around it.
            for (var i = 0; i < 10; i++)
                Assert.Equal(0, await step.ApplyAsync(Rows(), enabled: false, force: true));

            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task A_forced_scan_with_the_switch_on_reads_the_second_site_again()
        {
            var now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            var (step, client, _) = Build();
            client.UtcNow = () => now;

            await step.ApplyAsync(Rows(), enabled: true);
            Assert.Equal(1, client.FetchCount);

            // An ordinary scan five minutes later reads what is held, which is the whole
            // reason a host pressing Scan saw nothing change.
            now = now.AddMinutes(5);
            await step.ApplyAsync(Rows(), enabled: true);
            Assert.Equal(1, client.FetchCount);

            // Pressing Scan asks again.
            await step.ApplyAsync(Rows(), enabled: true, force: true);
            Assert.Equal(2, client.FetchCount);
        }

        [Fact]
        public async Task Two_presses_in_a_row_are_still_one_read_of_the_second_site()
        {
            var now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            var (step, client, _) = Build();
            client.UtcNow = () => now;

            await step.ApplyAsync(Rows(), enabled: true, force: true);
            Assert.Equal(1, client.FetchCount);

            now = now.AddSeconds(10);
            await step.ApplyAsync(Rows(), enabled: true, force: true);
            Assert.Equal(1, client.FetchCount);

            now = now.AddSeconds(51);
            await step.ApplyAsync(Rows(), enabled: true, force: true);
            Assert.Equal(2, client.FetchCount);
        }

        [Fact]
        public async Task A_pause_the_site_asked_for_outranks_a_press_of_Scan()
        {
            var now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            var provider = new RecordingHttpClientProvider(_ =>
            {
                var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
                return busy;
            });
            var client = new HexiumClient(provider, GetService<IApplicationLogger>()) { UtcNow = () => now };

            Assert.False(await client.RefreshAsync());
            Assert.Single(provider.Handler.Requests);

            // The site said to wait. Nothing a host presses turns that into a second knock.
            now = now.AddSeconds(90);
            Assert.False(await client.RefreshAsync());
            Assert.Single(provider.Handler.Requests);
        }

        // --- What a row says when the list came back without it ---

        [Fact]
        public void A_list_that_came_back_without_the_package_is_what_marks_a_row_not_listed()
        {
            // The site answered and this package was not in it: worth saying on the row.
            Assert.True(BlendWindow.NotListedNow(packageFound: false, listWasRead: true, "Vapok", "XPortalNetworks"));

            // Nobody could be asked: the row says nothing rather than something wrong.
            Assert.False(BlendWindow.NotListedNow(packageFound: false, listWasRead: false, "Vapok", "XPortalNetworks"));

            // It is there: nothing to say.
            Assert.False(BlendWindow.NotListedNow(packageFound: true, listWasRead: true, "Vapok", "XPortalNetworks"));

            // A folder that does not name a package was never expected in the list.
            Assert.False(BlendWindow.NotListedNow(packageFound: false, listWasRead: true, "", "XPortalNetworks"));
            Assert.False(BlendWindow.NotListedNow(packageFound: false, listWasRead: true, "Vapok", null));
        }

        /// <summary>
        /// The note needs a list read lately, not just a list. A read that fails leaves the
        /// last good list standing on purpose, so after a long run and a refresh that could
        /// not get through, the list in hand can be hours old. Versions off it are still
        /// worth showing; "this mod has been pulled" off it is not, and hours behind is the
        /// exact state this release exists to stop reading as fact.
        /// </summary>
        [Fact]
        public void A_list_read_hours_ago_is_not_grounds_for_saying_a_mod_was_pulled()
        {
            var now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

            // Read moments ago, and within the window an ordinary check reads it in.
            Assert.True(BlendWindow.ListIsRecentEnoughToJudge(now.AddSeconds(-5), now));
            Assert.True(BlendWindow.ListIsRecentEnoughToJudge(now.AddMinutes(-14), now));

            // Past it, and after the kind of gap a failed refresh leaves behind.
            Assert.False(BlendWindow.ListIsRecentEnoughToJudge(now.AddMinutes(-15), now));
            Assert.False(BlendWindow.ListIsRecentEnoughToJudge(now.AddHours(-6), now));

            // Nothing was ever read at all.
            Assert.False(BlendWindow.ListIsRecentEnoughToJudge(null, now));
        }

        [Fact]
        public void The_row_carries_the_note_and_offers_no_update()
        {
            var mod = new InstalledMod
            {
                Author = "Vapok",
                ModName = "XPortalNetworks",
                InstalledVersion = "2.0.3",
                LatestVersion = null,
                NotListedOnThunderstore = true,
            };

            var row = BlendWindow.BuildModDto(mod);
            var json = Newtonsoft.Json.Linq.JObject.FromObject(row);

            Assert.True(json.Value<bool>("notListed"));
            Assert.False(json.Value<bool>("UpdateAvailable"));

            // Nothing was removed or rolled back to say it, and the next scan that finds
            // the package clears it.
            mod.NotListedOnThunderstore = false;
            mod.LatestVersion = "2.0.5";
            var cleared = Newtonsoft.Json.Linq.JObject.FromObject(BlendWindow.BuildModDto(mod));
            Assert.False(cleared.Value<bool>("notListed"));
            Assert.True(cleared.Value<bool>("UpdateAvailable"));
        }

        // --- Checking one mod by hand ---

        [Fact]
        public void Checking_one_mod_never_says_an_update_is_waiting_on_a_copy_from_the_other_site()
        {
            // An ordinary copy: Thunderstore is ahead, so there is an update waiting.
            Assert.True(BlendWindow.CheckOneSaysUpdateWaiting("2.0.5", "2.0.3", installedFromHexium: false));

            // The same two numbers on a copy the host took from Hexium. BakaLoader leaves
            // those alone everywhere else and this is no exception; the swap back is its
            // own deliberate action.
            Assert.False(BlendWindow.CheckOneSaysUpdateWaiting("2.0.5", "2.0.3", installedFromHexium: true));

            // Already current, and ahead of the site.
            Assert.False(BlendWindow.CheckOneSaysUpdateWaiting("2.0.5", "2.0.5", installedFromHexium: false));
            Assert.False(BlendWindow.CheckOneSaysUpdateWaiting("2.0.5", "2.1.0", installedFromHexium: false));
        }

        /// <summary>
        /// The other half of that rule, which is what the answer needs to stop reading as
        /// "you have the newest" on a copy that plainly does not.
        /// </summary>
        [Fact]
        public void Checking_one_mod_says_when_thunderstore_has_moved_past_a_copy_from_the_other_site()
        {
            // Held, and Thunderstore is ahead. Neither an update waiting nor the newest.
            Assert.True(BlendWindow.CheckOneSaysThunderstoreMovedPast("2.0.5", "2.0.3", installedFromHexium: true));
            Assert.False(BlendWindow.CheckOneSaysUpdateWaiting("2.0.5", "2.0.3", installedFromHexium: true));

            // Held and level with Thunderstore: nothing to say.
            Assert.False(BlendWindow.CheckOneSaysThunderstoreMovedPast("2.0.5", "2.0.5", installedFromHexium: true));
            Assert.False(BlendWindow.CheckOneSaysThunderstoreMovedPast("2.0.5", "2.1.0", installedFromHexium: true));

            // An ordinary copy is never "held": it has an update waiting instead.
            Assert.False(BlendWindow.CheckOneSaysThunderstoreMovedPast("2.0.5", "2.0.3", installedFromHexium: false));
        }

        [Fact]
        public void One_package_cannot_be_asked_about_twice_in_ten_seconds()
        {
            var now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(BlendWindow.CheckOneOnCooldown(now.AddSeconds(-1), now));
            Assert.True(BlendWindow.CheckOneOnCooldown(now.AddSeconds(-9), now));
            Assert.False(BlendWindow.CheckOneOnCooldown(now.AddSeconds(-10), now));
            Assert.False(BlendWindow.CheckOneOnCooldown(now.AddSeconds(-600), now));

            // Never asked about before.
            Assert.False(BlendWindow.CheckOneOnCooldown(DateTime.MinValue, now));
        }
    }
}
