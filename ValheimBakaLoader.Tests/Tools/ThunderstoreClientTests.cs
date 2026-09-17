using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The reader for the Thunderstore package list, and the bug that made this hotfix.
    /// <para>
    /// A host watched XPortalNetworks go 2.0.2, 2.0.3, 2.0.4 in an afternoon while
    /// BakaLoader went on insisting on the older one for hours, and deleting the mod and
    /// pasting its link back in installed the older one again. Two things were behind it:
    /// Thunderstore's full listing is a snapshot the site rebuilds on a timer and serves
    /// from a cache, so it can sit hours behind the package's own page, and BakaLoader
    /// then held its own copy of that snapshot for another fifteen minutes with no way to
    /// say "ask again".
    /// </para>
    /// <para>
    /// Nothing here opens a connection. Every answer comes from
    /// <see cref="RecordingHttpHandler"/>, and the list of addresses it writes down is what
    /// proves which path answered, that a chunk from somewhere else is never fetched, and
    /// that a second press of Scan does not knock twice.
    /// </para>
    /// </summary>
    public class ThunderstoreClientTests : BaseTest
    {
        private const string IndexBlob = "https://gcdn.thunderstore.io/live/blob-storage/sha256/1111.index.blob";
        private const string ChunkOne = "https://gcdn.thunderstore.io/live/blob-storage/sha256/aaaa.chunk_one.blob";
        private const string ChunkTwo = "https://gcdn.thunderstore.io/live/blob-storage/sha256/bbbb.chunk_two.blob";
        private const string OffsiteChunk = "https://thunderstore.io.example.com/live/blob-storage/sha256/cccc.blob";
        private const string LiveXPortal = "https://thunderstore.io/api/experimental/package/Vapok/XPortalNetworks/";

        private static string FixturePath(string name) =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "thunderstore", name);

        private static string Fixture(string name) => File.ReadAllText(FixturePath(name));

        /// <summary>
        /// The listing index and its chunks come down as raw gzip with no header saying so,
        /// which is exactly how Thunderstore serves them, so the fixtures are packed the
        /// same way rather than handed over as text.
        /// </summary>
        private static HttpResponseMessage Packed(string json)
        {
            using var packed = new MemoryStream();
            using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                gzip.Write(bytes, 0, bytes.Length);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(packed.ToArray()) };
        }

        private static HttpResponseMessage Plain(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Redirect(string to)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(to);
            return response;
        }

        private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

        /// <summary>
        /// The site as it really answers: the listing index redirects to a blob, the blob
        /// is the packed list of chunks, each chunk is a packed array of packages, the full
        /// listing carries the same packages a couple of releases behind, and the
        /// per-package address carries the current one.
        /// </summary>
        private static Func<HttpRequestMessage, HttpResponseMessage> Site(
            string listingIndexFixture = "listing_index.json",
            Func<string, HttpResponseMessage> over = null) => request =>
        {
            var url = request.RequestUri?.ToString() ?? "";

            var overridden = over?.Invoke(url);
            if (overridden != null) return overridden;

            if (url == ThunderstoreClient.ListingIndexUrl) return Redirect(IndexBlob);
            if (url == IndexBlob) return Packed(Fixture(listingIndexFixture));
            if (url == ChunkOne) return Packed(Fixture("chunk_one.json"));
            if (url == ChunkTwo) return Packed(Fixture("chunk_two.json"));
            if (url == ThunderstoreClient.V1IndexUrl) return Plain(Fixture("v1_listing.json"));
            if (url == LiveXPortal) return Plain(Fixture("package_live.json"));

            return Status(HttpStatusCode.NotFound);
        };

        private (ThunderstoreClient Client, RecordingHttpHandler Handler) Build(
            Func<HttpRequestMessage, HttpResponseMessage> responder = null)
        {
            var provider = new RecordingHttpClientProvider(responder ?? Site());
            var context = new RestClientContext(GetService<Serilog.ILogger>(), provider);
            return (new ThunderstoreClient(context), provider.Handler);
        }

        // --- Reading the listing index ---

        [Fact]
        public async Task Follows_the_redirect_reads_every_chunk_and_merges_them_into_one_list()
        {
            var (client, handler) = Build();

            var xportal = await client.GetLatestAsync("Vapok", "XPortalNetworks");
            var quickload = await client.GetLatestAsync("aedenthorn", "QuickLoad");

            // The package the host was watching, at the version its own page shows.
            Assert.Equal("2.0.5", xportal.LatestVersion);

            // From the second chunk, so the two really were merged rather than the first
            // one winning and the rest being dropped.
            Assert.Equal("0.2.0", quickload.LatestVersion);

            Assert.Equal(ThunderstoreClient.ListingIndexSource, client.IndexSource);
            Assert.NotNull(client.IndexFetchedUtc);

            Assert.Equal(
                new[] { ThunderstoreClient.ListingIndexUrl, IndexBlob, ChunkOne, ChunkTwo },
                handler.Requests.ToArray());

            // The slow snapshot was never read, because the fresh path answered.
            Assert.DoesNotContain(ThunderstoreClient.V1IndexUrl, handler.Requests);
        }

        [Fact]
        public async Task The_download_address_comes_back_with_the_package()
        {
            var (client, _) = Build();

            var xportal = await client.GetLatestAsync("Vapok", "XPortalNetworks");

            Assert.Equal(
                "https://thunderstore.io/package/download/Vapok/XPortalNetworks/2.0.5/",
                xportal.DownloadUrl);
            Assert.NotNull(xportal.Latest.DateCreated);
        }

        [Fact]
        public async Task A_chunk_that_is_not_on_thunderstore_is_never_fetched()
        {
            var (client, handler) = Build(Site("listing_index_offsite.json"));

            var xportal = await client.GetLatestAsync("Vapok", "XPortalNetworks");

            // The address that is not Thunderstore's was refused before anything was sent
            // to it. "thunderstore.io.example.com" ends with the right letters and is
            // somebody else's machine.
            Assert.DoesNotContain(OffsiteChunk, handler.Requests);
            Assert.DoesNotContain(handler.Hosts, h => h.EndsWith("example.com", StringComparison.Ordinal));

            // And the host is not left with nothing: the full listing answered instead,
            // which is what makes refusing safe.
            Assert.Equal(ThunderstoreClient.V1Source, client.IndexSource);
            Assert.Equal("2.0.3", xportal.LatestVersion);
        }

        [Fact]
        public async Task One_chunk_that_does_not_answer_falls_back_to_the_full_listing_and_says_so()
        {
            var (client, handler) = Build(Site(over: url =>
                url == ChunkTwo ? Status(HttpStatusCode.ServiceUnavailable) : null));

            var xportal = await client.GetLatestAsync("Vapok", "XPortalNetworks");

            // Half a package list would read as though every mod in the missing half had
            // been delisted, so a chunk that fails fails the whole path.
            Assert.Equal(ThunderstoreClient.V1Source, client.IndexSource);
            Assert.Equal("2.0.3", xportal.LatestVersion);
            Assert.Contains(ThunderstoreClient.V1IndexUrl, handler.Requests);
        }

        /// <summary>
        /// A chunk that answers 200 with something that is not a package list is a chunk
        /// that did not answer. It has to be judged on its own contents: the chunks are
        /// read into one shared lookup, so a verdict that asked "is there anything in the
        /// lookup" would have the first chunk vouching for every chunk after it, and an
        /// error document or an empty list would pass as read. The live list is thirteen
        /// chunks, so that is a thirteenth of the community quietly missing from a list
        /// BakaLoader would then publish as the fresh one, with every mod in it marked as
        /// no longer on Thunderstore.
        /// </summary>
        [Theory]
        [InlineData("{\"detail\":\"Not found.\"}")]
        [InlineData("[]")]
        [InlineData("[{\"detail\":\"Not found.\"}]")]
        public async Task A_chunk_that_answers_with_something_that_is_not_a_package_list_falls_back(string body)
        {
            var (client, handler) = Build(Site(over: url => url == ChunkTwo ? Packed(body) : null));

            var xportal = await client.GetLatestAsync("Vapok", "XPortalNetworks");
            var quickload = await client.GetLatestAsync("aedenthorn", "QuickLoad");

            // The fresh path did not answer in full, so the full listing stood in and the
            // interface is told which one answered.
            Assert.Equal(ThunderstoreClient.V1Source, client.IndexSource);
            Assert.Contains(ThunderstoreClient.V1IndexUrl, handler.Requests);

            // And nothing vanished: the package that lives only in the dropped chunk is
            // still in the list, so no row is told its mod has been pulled.
            Assert.Equal("2.0.3", xportal.LatestVersion);
            Assert.NotNull(quickload);
            Assert.Equal("0.2.0", quickload.LatestVersion);
        }

        [Fact]
        public async Task A_full_listing_that_is_not_a_package_list_is_not_read_as_an_empty_community()
        {
            var (client, _) = Build(Site(over: url =>
                url == ThunderstoreClient.ListingIndexUrl ? Status(HttpStatusCode.NotFound)
                : url == ThunderstoreClient.V1IndexUrl ? Plain("{\"detail\":\"Not found.\"}")
                : null));

            var xportal = await client.GetLatestAsync("Vapok", "XPortalNetworks");

            // Neither address gave a list of packages, so there is no list. Reading the
            // error document as "the community has nothing in it" would mark every
            // installed mod as no longer on Thunderstore.
            Assert.Null(xportal);
            Assert.Null(client.IndexSource);
            Assert.Null(client.IndexFetchedUtc);
        }

        [Fact]
        public async Task With_neither_address_answering_nothing_is_claimed()
        {
            var (client, _) = Build(_ => Status(HttpStatusCode.BadGateway));

            var xportal = await client.GetLatestAsync("Vapok", "XPortalNetworks");

            // Null is "could not check", which is what keeps a site outage from showing up
            // as a row full of mods nobody can find any more.
            Assert.Null(xportal);
            Assert.Null(client.IndexSource);
            Assert.Null(client.IndexFetchedUtc);
        }

        // --- The window, and walking past it on purpose ---

        [Fact]
        public async Task The_held_list_stands_inside_the_window_and_a_refresh_walks_past_it()
        {
            var now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            var (client, _) = Build();
            client.UtcNow = () => now;

            await client.GetLatestAsync("Vapok", "XPortalNetworks");
            Assert.Equal(1, client.FetchCount);

            // Five minutes later an ordinary check reads what is held. This is the whole
            // of the reported bug: the site had a new release and BakaLoader would not go
            // and look, because its own copy was still inside its window.
            now = now.AddMinutes(5);
            await client.GetLatestAsync("Vapok", "XPortalNetworks");
            Assert.Equal(1, client.FetchCount);

            // A host pressing Scan means "ask again", and now it does.
            var state = await client.RefreshAsync();
            Assert.True(state.Fetched);
            Assert.False(state.ReusedFresh);
            Assert.Equal(2, client.FetchCount);
        }

        [Fact]
        public async Task A_second_press_inside_the_cooldown_reuses_the_list_that_was_just_read()
        {
            var now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            var (client, _) = Build();
            client.UtcNow = () => now;

            var first = await client.RefreshAsync();
            Assert.True(first.Fetched);
            Assert.Equal(1, client.FetchCount);
            var readAt = client.IndexFetchedUtc;

            // Pressed again straight away. Nothing goes out, the answer says why, and the
            // time on the interface stays the time the list was actually read.
            now = now.AddSeconds(10);
            var again = await client.RefreshAsync();
            Assert.False(again.Fetched);
            Assert.True(again.ReusedFresh);
            Assert.Equal(1, client.FetchCount);
            Assert.Equal(readAt, client.IndexFetchedUtc);

            // Past the cooldown it goes out again.
            now = now.AddSeconds(51);
            var third = await client.RefreshAsync();
            Assert.True(third.Fetched);
            Assert.Equal(2, client.FetchCount);
        }

        [Fact]
        public async Task A_refresh_that_fails_leaves_the_list_that_was_already_held()
        {
            var now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            var down = false;
            var (client, _) = Build(Site(over: _ => down ? Status(HttpStatusCode.BadGateway) : null));
            client.UtcNow = () => now;

            await client.RefreshAsync();
            Assert.Equal("2.0.5", (await client.GetLatestAsync("Vapok", "XPortalNetworks")).LatestVersion);

            down = true;
            now = now.AddMinutes(5);
            var state = await client.RefreshAsync();

            Assert.False(state.Fetched);
            Assert.Equal(ThunderstoreClient.ListingIndexSource, client.IndexSource);
            Assert.Equal("2.0.5", (await client.GetLatestAsync("Vapok", "XPortalNetworks")).LatestVersion);
        }

        // --- Asking about one package directly ---

        [Fact]
        public async Task The_per_package_address_answers_with_the_newest_when_the_listing_is_behind()
        {
            // The listing index is out of the picture, so the held list is the slow
            // snapshot, exactly as the host had it.
            var (client, handler) = Build(Site(over: url =>
                url == ThunderstoreClient.ListingIndexUrl ? Status(HttpStatusCode.NotFound) : null));

            var fromList = await client.GetLatestAsync("Vapok", "XPortalNetworks");
            var live = await client.GetLiveAsync("Vapok", "XPortalNetworks");

            Assert.Equal("2.0.3", fromList.LatestVersion);
            Assert.Equal("2.0.5", live.LatestVersion);
            Assert.Equal(
                "https://thunderstore.io/package/download/Vapok/XPortalNetworks/2.0.5/",
                live.DownloadUrl);
            Assert.Equal("Vapok", live.Namespace);
            Assert.Equal("XPortalNetworks", live.Name);

            Assert.Contains(LiveXPortal, handler.Requests);
        }

        [Fact]
        public async Task A_live_lookup_for_a_package_the_site_does_not_have_answers_nothing()
        {
            var (client, _) = Build();

            var live = await client.GetLiveAsync("Nobody", "NotARealMod");

            Assert.Null(live);
        }

        /// <summary>
        /// The distinction the row depends on. "Thunderstore says there is no such package"
        /// is worth telling a host; "Thunderstore did not answer" is not the same statement
        /// and must never be shown as one, or a bad minute on the internet reads as every
        /// mod having been pulled.
        /// </summary>
        [Fact]
        public async Task A_plain_no_and_a_site_that_did_not_answer_are_told_apart()
        {
            var (client, _) = Build();

            // The site answered: there is no such package.
            var gone = await client.LookupLiveAsync("Nobody", "NotARealMod");
            Assert.True(gone.Answered);
            Assert.False(gone.Found);

            // The site is there and has it.
            var present = await client.LookupLiveAsync("Vapok", "XPortalNetworks");
            Assert.True(present.Answered);
            Assert.True(present.Found);
            Assert.Equal("2.0.5", present.Package.LatestVersion);
        }

        [Theory]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadGateway)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.GatewayTimeout)]
        public async Task A_site_that_will_not_speak_is_never_read_as_a_package_being_gone(HttpStatusCode code)
        {
            var (client, _) = Build(Site(over: url => url == LiveXPortal ? Status(code) : null));

            var lookup = await client.LookupLiveAsync("Vapok", "XPortalNetworks");

            Assert.False(lookup.Answered);
            Assert.False(lookup.Found);
        }

        [Fact]
        public async Task An_answer_with_no_version_in_it_is_not_read_as_a_package_being_gone()
        {
            var (client, _) = Build(Site(over: url =>
                url == LiveXPortal ? Plain("{\"namespace\":\"Vapok\",\"name\":\"XPortalNetworks\"}") : null));

            var lookup = await client.LookupLiveAsync("Vapok", "XPortalNetworks");

            // It said something this cannot read. Calling that "pulled" would be a guess.
            Assert.False(lookup.Answered);
            Assert.False(lookup.Found);
        }

        [Fact]
        public async Task A_live_lookup_with_half_an_identity_never_leaves_the_machine()
        {
            var (client, handler) = Build();

            Assert.Null(await client.GetLiveAsync("Vapok", ""));
            Assert.Null(await client.GetLiveAsync(null, "XPortalNetworks"));

            Assert.Empty(handler.Requests);
        }

        // --- Which addresses count as Thunderstore's ---

        [Theory]
        [InlineData("https://thunderstore.io/c/valheim/api/v1/package-listing-index/", true)]
        [InlineData("https://gcdn.thunderstore.io/live/blob-storage/sha256/a.blob", true)]
        [InlineData("https://valheim.thunderstore.io/api/v1/package/", true)]
        [InlineData("https://thunderstore.io.example.com/a.blob", false)]
        [InlineData("https://notthunderstore.io/a.blob", false)]
        [InlineData("http://gcdn.thunderstore.io/a.blob", false)]
        [InlineData("https://cdn.hexium.gg/upload/1/1.0.0.zip", false)]
        [InlineData("", false)]
        [InlineData("not an address", false)]
        public void Only_thunderstore_addresses_are_ever_fetched(string url, bool allowed)
        {
            Assert.Equal(allowed, ThunderstoreClient.IsThunderstoreAddress(url));
        }
    }
}
