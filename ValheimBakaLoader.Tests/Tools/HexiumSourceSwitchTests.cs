using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The promise the switch makes: off means BakaLoader does not contact hexium.gg at
    /// all, however many mods are installed and however often a scan runs.
    /// <para>
    /// This drives the real scan step over a real <see cref="HexiumClient"/> whose
    /// transport writes down every request. An empty list of requests is the proof, and it
    /// is a stronger one than any flag the code could set, because it is the network side
    /// of the seam rather than the intention on this side of it.
    /// </para>
    /// </summary>
    public class HexiumSourceSwitchTests : BaseTest
    {
        private static string FixtureJson() =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Resources", "hexium", "index_small.json"));

        private (HexiumScanStep Step, RecordingHttpHandler Handler) Build()
        {
            var provider = new RecordingHttpClientProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(FixtureJson(), System.Text.Encoding.UTF8, "application/json"),
            });
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());
            return (new HexiumScanStep(client, GetService<IApplicationLogger>()), provider.Handler);
        }

        private static List<InstalledMod> Rows() => new()
        {
            new InstalledMod { Author = "DrakeMods", ModName = "LockSmith", InstalledVersion = "0.3.5", LatestVersion = "0.3.5" },
            new InstalledMod { Author = "Orjat", ModName = "SlavesIncorporated", InstalledVersion = "1.0.5", LatestVersion = "1.0.5" },
            new InstalledMod { Author = "Nobody", ModName = "NotOnEitherSite", InstalledVersion = "1.0.0" },
        };

        [Fact]
        public async Task With_the_switch_off_not_one_request_is_made()
        {
            var (step, handler) = Build();
            var rows = Rows();

            // Ten scans of three mods each. If anything reached out, this would say so.
            for (var i = 0; i < 10; i++)
            {
                var matched = await step.ApplyAsync(rows, enabled: false);
                Assert.Equal(0, matched);
            }

            Assert.Empty(handler.Requests);
            Assert.All(rows, row =>
            {
                Assert.Null(row.HexiumLatestVersion);
                Assert.Null(row.HexiumOwner);
                Assert.Null(row.HexiumName);
                Assert.False(row.HexiumNewer);
            });
        }

        [Fact]
        public async Task With_the_switch_on_the_rows_are_filled_from_one_request()
        {
            var (step, handler) = Build();
            var rows = Rows();

            var matched = await step.ApplyAsync(rows, enabled: true);

            Assert.Equal(2, matched);
            Assert.Single(handler.Requests);        // one index read serves every row
            Assert.Equal("valheim.hexium.gg", handler.Hosts[0]);

            Assert.Equal("0.3.6", rows[0].HexiumLatestVersion);
            Assert.Equal("DrakeMods", rows[0].HexiumOwner);
            Assert.True(rows[0].HexiumNewer);       // ahead of the install and of Thunderstore

            Assert.Equal("1.0.666", rows[1].HexiumLatestVersion);
            Assert.True(rows[1].HexiumNewer);

            // A mod neither site carries is left exactly as it came in.
            Assert.Null(rows[2].HexiumLatestVersion);
            Assert.False(rows[2].HexiumNewer);
        }

        [Fact]
        public async Task Turning_it_off_again_stops_the_requests_dead()
        {
            var (step, handler) = Build();
            var rows = Rows();

            await step.ApplyAsync(rows, enabled: true);
            Assert.Single(handler.Requests);

            for (var i = 0; i < 10; i++) await step.ApplyAsync(Rows(), enabled: false);

            Assert.Single(handler.Requests);   // still the one from before
        }

        [Fact]
        public async Task The_step_never_touches_what_thunderstore_put_on_the_row()
        {
            var (step, _) = Build();
            var rows = Rows();
            rows[0].ThunderstoreNamespace = "DrakeMods";
            rows[0].ThunderstoreName = "LockSmith";
            rows[0].LatestVersion = "0.3.5";

            await step.ApplyAsync(rows, enabled: true);

            Assert.Equal("0.3.5", rows[0].LatestVersion);
            Assert.Equal("DrakeMods", rows[0].ThunderstoreNamespace);
            Assert.Equal("LockSmith", rows[0].ThunderstoreName);
            Assert.Equal("0.3.5", rows[0].InstalledVersion);
        }

        [Fact]
        public async Task A_site_that_is_down_costs_the_scan_nothing_but_the_marks()
        {
            var provider = new RecordingHttpClientProvider(_ => throw new HttpRequestException("the network is gone"));
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());
            var step = new HexiumScanStep(client, GetService<IApplicationLogger>());

            var rows = Rows();
            rows[0].LatestVersion = "0.3.9";

            var matched = await step.ApplyAsync(rows, enabled: true);

            Assert.Equal(0, matched);
            Assert.Null(rows[0].HexiumLatestVersion);
            // And the Thunderstore side of the row is exactly what it was, so the scan the
            // host actually asked for is unaffected by the other site being down.
            Assert.Equal("0.3.9", rows[0].LatestVersion);
            Assert.True(rows[0].UpdateAvailable);
        }

        [Fact]
        public async Task A_client_that_throws_is_swallowed_row_by_row()
        {
            var step = new HexiumScanStep(new ThrowingClient(), GetService<IApplicationLogger>());
            var rows = Rows();

            var matched = await step.ApplyAsync(rows, enabled: true);

            Assert.Equal(0, matched);
            Assert.All(rows, row => Assert.Null(row.HexiumLatestVersion));
        }

        [Fact]
        public async Task Nothing_at_all_to_scan_is_answered_without_a_request()
        {
            var (step, handler) = Build();

            Assert.Equal(0, await step.ApplyAsync(null, enabled: true));
            Assert.Equal(0, await step.ApplyAsync(new List<InstalledMod>(), enabled: true));
            Assert.Empty(handler.Requests);
        }

        /// <summary>A client that breaks its own contract, to prove the step survives it.</summary>
        private sealed class ThrowingClient : IHexiumClient
        {
            public Task<HexiumPackage> GetPackageAsync(string fullName) =>
                throw new InvalidOperationException("this client does not keep its promises");

            public Task<HexiumLookup> LookupAsync(string fullName) =>
                throw new InvalidOperationException("this client does not keep its promises");

            public Task<bool> RefreshAsync() =>
                throw new InvalidOperationException("this client does not keep its promises");
        }
    }
}
