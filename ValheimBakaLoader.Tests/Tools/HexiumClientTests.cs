using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The reader for the second mod site, proved against a small fixture cut from one
    /// capture of the real index. No test here opens a connection: every answer comes
    /// from <see cref="RecordingHttpHandler"/>, and the list of requests it writes down
    /// is what proves the client asks once per cache window and not at all until it is
    /// asked to.
    /// <para>
    /// The fixture keeps the shapes that matter and that Thunderstore never shows:
    /// versions listed out of order, pre-release versions beside their releases, owners
    /// carrying hyphens and dots, and a version the site has withdrawn.
    /// </para>
    /// </summary>
    public class HexiumClientTests : BaseTest
    {
        private static string FixturePath =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "hexium", "index_small.json");

        private static string FixtureJson() => File.ReadAllText(FixturePath);

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

        private (HexiumClient Client, RecordingHttpHandler Handler) Build(
            Func<HttpRequestMessage, HttpResponseMessage> responder = null)
        {
            var provider = new RecordingHttpClientProvider(responder ?? (_ => Json(FixtureJson())));
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());
            return (client, provider.Handler);
        }

        // --- Reading the index ---

        [Fact]
        public async Task Reads_the_fixture_and_matches_on_the_exact_full_name()
        {
            var (client, handler) = Build();

            var package = await client.GetPackageAsync("DrakeMods-LockSmith");

            Assert.NotNull(package);
            Assert.Equal("DrakeMods", package.Owner);
            Assert.Equal("LockSmith", package.Name);
            Assert.Single(handler.Requests);
            Assert.Equal(HexiumClient.V1IndexUrl, handler.Requests[0]);
        }

        [Fact]
        public async Task A_package_the_site_does_not_carry_answers_not_found_rather_than_unreachable()
        {
            var (client, _) = Build();

            var lookup = await client.LookupAsync("Nobody-NotARealMod");

            Assert.True(lookup.IndexAvailable);
            Assert.False(lookup.Found);
            Assert.Null(lookup.Package);
        }

        /// <summary>
        /// Hexium does not sort its versions array, so the highest has to be worked out
        /// rather than read off the top. This package lists 1.0.7 first and 1.0.666
        /// third, and 666 is a number.
        /// </summary>
        [Fact]
        public async Task The_highest_version_is_computed_not_taken_from_the_top_of_the_list()
        {
            var (client, _) = Build();

            var package = await client.GetPackageAsync("Orjat-SlavesIncorporated");

            Assert.Equal("1.0.7", package.Versions[0].VersionNumber);   // what the site listed first
            Assert.Equal("1.0.666", package.LatestStable.VersionNumber); // what is actually highest
            Assert.Equal("1.0.666", package.LatestAny.VersionNumber);
        }

        [Fact]
        public async Task Pre_releases_are_left_out_unless_the_installed_version_is_one()
        {
            var (client, _) = Build();

            var package = await client.GetPackageAsync("ArgusMagnus-ServersideQoL_AutoStore");

            // The site's own newest upload is a beta, and a stable install is never
            // nudged onto it.
            Assert.Equal("2.0.13-beta.1", package.Versions[0].VersionNumber);
            Assert.Equal("2.0.11", package.LatestStable.VersionNumber);
            Assert.Equal("2.0.11", package.LatestFor("2.0.8").VersionNumber);
            Assert.Equal("2.0.11", package.LatestFor(null).VersionNumber);

            // Somebody already running a beta is shown the newer beta.
            Assert.Equal("2.0.13-beta.1", package.LatestFor("2.0.11-beta.1").VersionNumber);
            Assert.Equal("2.0.13-beta.1", package.LatestAny.VersionNumber);
        }

        [Fact]
        public async Task A_pre_release_on_an_older_line_does_not_count()
        {
            var (client, _) = Build();
            var package = await client.GetPackageAsync("ArgusMagnus-ServersideQoL_AutoStore");

            // Installed at a beta on a line above everything the site holds: the highest
            // that counts is still the newest full release, never a lower beta.
            var pick = package.LatestFor("9.9.9-beta.1");
            Assert.Equal("2.0.11", pick.VersionNumber);
        }

        [Fact]
        public async Task A_withdrawn_version_is_never_offered()
        {
            var (client, _) = Build();

            var package = await client.GetPackageAsync("DrakeMods-LockSmith");

            Assert.Equal("9.9.9", package.Versions[0].VersionNumber);
            Assert.False(package.Versions[0].IsActive);
            Assert.Equal("0.3.6", package.LatestStable.VersionNumber);
            Assert.Null(package.Find("9.9.9"));
            Assert.NotNull(package.Find("0.3.6"));
        }

        [Theory]
        [InlineData("bruceirons-team-Oarsmen", "bruceirons-team", "Oarsmen")]
        [InlineData("Kurios.ZeuS-JavaHeim", "Kurios.ZeuS", "JavaHeim")]
        public async Task Owners_with_hyphens_and_dots_resolve_by_full_name(string fullName, string owner, string name)
        {
            var (client, _) = Build();

            var package = await client.GetPackageAsync(fullName);

            Assert.NotNull(package);
            Assert.Equal(owner, package.Owner);
            Assert.Equal(name, package.Name);
        }

        [Fact]
        public async Task Matching_is_case_sensitive_so_a_lookalike_name_is_a_different_package()
        {
            var (client, _) = Build();

            // The two sites both hold packages whose names differ only by their capitals,
            // so a near miss has to be a miss.
            Assert.Null(await client.GetPackageAsync("drakemods-locksmith"));
            Assert.NotNull(await client.GetPackageAsync("DrakeMods-LockSmith"));
        }

        // --- How often it asks ---

        [Fact]
        public void Nothing_is_asked_for_until_somebody_asks()
        {
            var (_, handler) = Build();

            // Building the client opens no connection: this is half of what makes the
            // switch being off mean no contact at all.
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task The_index_is_fetched_once_per_cache_window()
        {
            var (client, handler) = Build();
            var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
            client.UtcNow = () => now;

            for (var i = 0; i < 20; i++) await client.GetPackageAsync("DrakeMods-LockSmith");
            Assert.Single(handler.Requests);

            // Still inside the window.
            now = now.AddMinutes(14);
            await client.GetPackageAsync("DrakeMods-LockSmith");
            Assert.Single(handler.Requests);

            // Past it.
            now = now.AddMinutes(2);
            await client.GetPackageAsync("DrakeMods-LockSmith");
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public async Task It_sends_a_user_agent_that_names_BakaLoader_and_links_the_project()
        {
            string agent = null;
            var provider = new RecordingHttpClientProvider(request =>
            {
                agent = request.Headers.UserAgent.ToString();
                return Json(FixtureJson());
            });
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());

            await client.GetPackageAsync("DrakeMods-LockSmith");

            Assert.StartsWith("BakaLoader/", agent);
            Assert.Contains("github.com/RyanDMcAfee/ValheimBakaLoader", agent);
        }

        // --- The body arrives packed, because that is what was asked for ---

        private static HttpResponseMessage Packed(string body, string encoding)
        {
            var raw = System.Text.Encoding.UTF8.GetBytes(body);
            using var buffer = new MemoryStream();

            using (Stream packer = encoding switch
            {
                "gzip" => new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionMode.Compress, leaveOpen: true),
                "deflate" => new System.IO.Compression.ZLibStream(buffer, System.IO.Compression.CompressionMode.Compress, leaveOpen: true),
                "raw-deflate" => new System.IO.Compression.DeflateStream(buffer, System.IO.Compression.CompressionMode.Compress, leaveOpen: true),
                _ => throw new ArgumentException("unknown packing"),
            })
            {
                packer.Write(raw, 0, raw.Length);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(buffer.ToArray()),
            };
            response.Content.Headers.ContentEncoding.Add(encoding == "raw-deflate" ? "deflate" : encoding);
            return response;
        }

        /// <summary>
        /// The real site answers packed, and the app hands out plain clients that unpack
        /// nothing by themselves. A body that came back packed and was read as though it
        /// had not would parse as nonsense, which reads exactly like the site being down:
        /// the whole feature would quietly never work.
        /// </summary>
        [Theory]
        [InlineData("gzip")]
        [InlineData("deflate")]
        [InlineData("raw-deflate")]
        public async Task A_packed_body_is_unpacked_before_it_is_read(string encoding)
        {
            var body = FixtureJson();
            var provider = new RecordingHttpClientProvider(_ => Packed(body, encoding));
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());

            var package = await client.GetPackageAsync("DrakeMods-LockSmith");

            Assert.NotNull(package);
            Assert.Equal("0.3.6", package.LatestStable.VersionNumber);
        }

        [Fact]
        public async Task It_asks_for_a_packed_body()
        {
            string accepted = null;
            var provider = new RecordingHttpClientProvider(request =>
            {
                accepted = request.Headers.AcceptEncoding.ToString();
                return Packed(FixtureJson(), "gzip");
            });
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());

            await client.GetPackageAsync("DrakeMods-LockSmith");

            Assert.Contains("gzip", accepted);
        }

        [Fact]
        public async Task A_body_that_never_ends_is_not_read_forever()
        {
            var provider = new RecordingHttpClientProvider(_ => Json(FixtureJson()));
            var client = new HexiumClient(provider, GetService<IApplicationLogger>()) { MaxIndexBytes = 64 };

            var lookup = await client.LookupAsync("DrakeMods-LockSmith");

            Assert.False(lookup.Found);
            Assert.False(lookup.IndexAvailable);
        }

        // --- When the site says no ---

        [Fact]
        public async Task A_429_is_honoured_for_as_long_as_the_site_asked_for()
        {
            var serveFixture = false;
            var provider = new RecordingHttpClientProvider(_ =>
            {
                if (serveFixture) return Json(FixtureJson());

                var response = new HttpResponseMessage((HttpStatusCode)429);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
                return response;
            });
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());
            var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
            client.UtcNow = () => now;

            var first = await client.LookupAsync("DrakeMods-LockSmith");
            Assert.False(first.IndexAvailable);
            Assert.Single(provider.Handler.Requests);

            // Nothing goes out again until the moment the site named, however often it is asked.
            now = now.AddMinutes(20);
            for (var i = 0; i < 5; i++) await client.LookupAsync("DrakeMods-LockSmith");
            Assert.Single(provider.Handler.Requests);

            // And once the pause is over it tries again.
            now = now.AddMinutes(11);
            serveFixture = true;
            var later = await client.LookupAsync("DrakeMods-LockSmith");
            Assert.Equal(2, provider.Handler.Requests.Count);
            Assert.True(later.Found);
        }

        [Fact]
        public async Task A_failure_leaves_the_answer_already_held_standing()
        {
            var fail = false;
            var provider = new RecordingHttpClientProvider(_ => fail
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json(FixtureJson()));
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());
            var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
            client.UtcNow = () => now;

            Assert.NotNull(await client.GetPackageAsync("DrakeMods-LockSmith"));

            fail = true;
            now = now.AddMinutes(16);   // the held copy is stale, so a fetch is tried

            var lookup = await client.LookupAsync("DrakeMods-LockSmith");

            Assert.Equal(2, provider.Handler.Requests.Count);
            Assert.True(lookup.Found);           // the stale answer still stands
            Assert.True(lookup.IndexAvailable);
        }

        [Fact]
        public async Task A_transport_that_throws_never_throws_out_of_the_client()
        {
            var provider = new RecordingHttpClientProvider(_ => throw new HttpRequestException("the network is gone"));
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());

            var lookup = await client.LookupAsync("DrakeMods-LockSmith");

            Assert.False(lookup.IndexAvailable);
            Assert.False(lookup.Found);
            Assert.Contains("Hexium", lookup.Error);
        }

        [Fact]
        public async Task Nonsense_in_the_body_answers_unreachable_rather_than_throwing()
        {
            var provider = new RecordingHttpClientProvider(_ => Json("{not json at all"));
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());

            var lookup = await client.LookupAsync("DrakeMods-LockSmith");

            Assert.False(lookup.Found);
        }

        [Fact]
        public async Task Repeated_failures_back_off_instead_of_hammering_the_site()
        {
            var provider = new RecordingHttpClientProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var client = new HexiumClient(provider, GetService<IApplicationLogger>());
            var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
            client.UtcNow = () => now;

            await client.LookupAsync("DrakeMods-LockSmith");
            Assert.Single(provider.Handler.Requests);

            // A second ask straight away does not go out at all.
            for (var i = 0; i < 10; i++) await client.LookupAsync("DrakeMods-LockSmith");
            Assert.Single(provider.Handler.Requests);

            // A minute later it tries once more, and backs off further when that fails too.
            now = now.AddMinutes(2);
            await client.LookupAsync("DrakeMods-LockSmith");
            Assert.Equal(2, provider.Handler.Requests.Count);

            now = now.AddMinutes(1);
            await client.LookupAsync("DrakeMods-LockSmith");
            Assert.Equal(2, provider.Handler.Requests.Count);
        }

        // --- Nothing here can reach the other site ---

        [Fact]
        public async Task Every_request_goes_to_the_valheim_index_and_nowhere_else()
        {
            var (client, handler) = Build();
            var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
            client.UtcNow = () => now;

            await client.GetPackageAsync("DrakeMods-LockSmith");
            now = now.AddMinutes(16);
            await client.GetPackageAsync("Orjat-SlavesIncorporated");

            Assert.All(handler.Requests, url => Assert.Equal(HexiumClient.V1IndexUrl, url));
            Assert.All(handler.Hosts, host => Assert.Equal("valheim.hexium.gg", host));

            // Never the community listing index, which answers with every game's packages.
            Assert.DoesNotContain(handler.Requests, url => url.Contains("/api/v1/package/valheim", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData("hexium.gg", true)]
        [InlineData("valheim.hexium.gg", true)]
        [InlineData("cdn.hexium.gg", true)]
        [InlineData("CDN.HEXIUM.GG", true)]
        [InlineData("nothexium.gg", false)]
        [InlineData("hexium.gg.example.com", false)]
        [InlineData("hexium.ggx", false)]
        [InlineData("thunderstore.io", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Only_hexium_itself_counts_as_hexium(string host, bool expected)
        {
            Assert.Equal(expected, HexiumClient.IsHexiumHost(host));
        }
    }
}
