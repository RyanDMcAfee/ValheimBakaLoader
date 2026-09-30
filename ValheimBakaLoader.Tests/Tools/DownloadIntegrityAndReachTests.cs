using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The remaining halves of the download work: what a mod package is held to before it
    /// replaces a folder the server loads from, which client every remote read goes out on,
    /// how far down the release list a pack lookup can see, and what a failed connection is
    /// remembered by.
    /// </summary>
    public class DownloadIntegrityAndReachTests : BaseTest, IDisposable
    {
        private readonly string Root = Path.Combine(
            Path.GetTempPath(), "bakaloader-reach-" + Guid.NewGuid().ToString("N"));

        public DownloadIntegrityAndReachTests() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        private string WriteFile(string name, byte[] bytes)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        // ------------------------------------------- the Thunderstore package, before it lands

        /// <summary>
        /// The Hexium path in this same file has compared its downloads with the listed size
        /// since it was written, and the BepInEx installer with the size and the digest. The
        /// Thunderstore install and update paths replace a folder the server loads from and
        /// were held to nothing at all: a package damaged in transit unpacked without an
        /// exception, the host's working mod was backed up and cleared, and what went in its
        /// place was a file BepInEx would fail to load with no record anywhere of why.
        /// </summary>
        [Fact]
        public void A_package_that_arrived_short_is_named_rather_than_written()
        {
            var published = new ThunderstorePackageVersion { VersionNumber = "1.0.1", FileSize = 317 };
            var path = WriteFile("short.zip", new byte[158]);

            var said = ModUpdateService.DescribeDamage(published, path, written: 158);

            Assert.NotNull(said);
            Assert.Contains("158", said);
            Assert.Contains("317", said);
        }

        [Fact]
        public void A_package_the_right_length_with_the_wrong_bytes_is_caught_by_the_digest()
        {
            var bytes = new byte[256];
            new Random(3).NextBytes(bytes);

            var arrived = bytes.ToArray();
            arrived[9] ^= 0xFF;

            var published = new ThunderstorePackageVersion
            {
                VersionNumber = "1.0.1",
                FileSize = bytes.LongLength,
                Sha256 = Sha256Hex(bytes),
            };

            var path = WriteFile("rewritten.zip", arrived);
            Assert.Contains("digest", ModUpdateService.DescribeDamage(published, path, arrived.LongLength));
        }

        /// <summary>
        /// Thunderstore publishes no per-version size on the package page, only on the
        /// community listing, so a version resolved from the page alone has nothing to be held
        /// to. That is not a reason to refuse it: a guard that turns away every install from
        /// the endpoint the app prefers is worse than the defect.
        /// </summary>
        [Fact]
        public void A_version_the_site_said_nothing_about_is_not_refused()
        {
            var published = new ThunderstorePackageVersion { VersionNumber = "1.0.1" };
            var path = WriteFile("nothing-said.zip", new byte[64]);

            Assert.Null(ModUpdateService.DescribeDamage(published, path, written: 64));
            Assert.Null(ModUpdateService.DescribeDamage(null, path, written: 64));
        }

        /// <summary>
        /// The version hint is only true for a lookup or a 404, which is the download stage. It
        /// used to be added to every failure from the download onward, so a package whose bytes
        /// did not all arrive handed the host ".NET's End of Central Directory record could not
        /// be found" and then sent them to a Versions page that had nothing to tell them.
        /// </summary>
        [Fact]
        public void A_failure_after_the_bytes_started_moving_does_not_send_the_host_to_a_versions_page()
        {
            var zipProblem = new InvalidDataException("End of Central Directory record could not be found.");

            var unpack = ModUpdateService.InstallFailureSentence("unpack", zipProblem, "1.4.6");
            Assert.DoesNotContain("Versions page", unpack);
            Assert.DoesNotContain("Central Directory", unpack);
            Assert.Contains("did not come whole", unpack);

            var install = ModUpdateService.InstallFailureSentence("install", zipProblem, "1.4.6");
            Assert.DoesNotContain("Versions page", install);
        }

        [Fact]
        public void A_pinned_version_that_never_existed_still_points_at_the_versions_page()
        {
            var notFound = new HttpRequestException("Response status code does not indicate success: 404 (Not Found).");

            var said = ModUpdateService.InstallFailureSentence("download", notFound, "9.9.9");
            Assert.Contains("Versions page", said);

            // And a link with no version on it never had that hint, because it never asked for
            // a version that might not exist.
            Assert.DoesNotContain("Versions page",
                ModUpdateService.InstallFailureSentence("download", notFound, null));
        }

        // ------------------------------------------------------ which client a download uses

        /// <summary>
        /// "Install the mods BakaLoader needs" built its own HttpClient, so the Connection
        /// card's two switches, the fifteen second connect timeout and the wire trace all
        /// missed it, and it took the whole body into one in-memory byte array with no cap. Its
        /// constructor took no provider at all, so there was no seam to put any of that through.
        /// RestApi's own docstring says this app makes an HttpClient in exactly one place.
        /// </summary>
        [Fact]
        public void The_required_mod_installer_asks_the_app_for_its_client()
        {
            var seam = typeof(RequiredModChecker)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(p => p.ParameterType)
                .ToList();

            Assert.Contains(typeof(IHttpClientProvider), seam);
        }

        [Fact]
        public void The_required_mod_installer_streams_under_a_cap_rather_than_buffering_a_body()
        {
            var source = AppSourceTree.Read("ValheimBakaLoader", "Tools", "RequiredModChecker.cs");

            // The two lines that were the defect, in code rather than in a comment about it.
            var code = string.Join(Environment.NewLine, source
                .Split(new[] { '\n' })
                .Where(line => !line.TrimStart().StartsWith("//")));

            Assert.DoesNotContain("new HttpClient {", code);
            Assert.DoesNotContain("GetByteArrayAsync", code);
            Assert.Contains("BoundedDownload.ToFileAsync", code);
            Assert.Contains("MaxBytes", code);
        }

        [Fact]
        public async Task The_required_mod_installer_goes_out_on_the_providers_client()
        {
            var provider = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.NotFound));

            var checker = new RequiredModChecker(
                GetService<IApplicationLogger>(), new AlwaysFindsIt(), provider);

            var plugins = Path.Combine(Root, "plugins");
            Directory.CreateDirectory(plugins);

            var ok = await checker.InstallModAsync(
                new RequiredMod { Author = "Someone", ModName = "SomeMod" }, plugins);

            Assert.False(ok);
            Assert.Single(provider.Handler.Requests);
            Assert.Equal("https://example.invalid/pack.zip", provider.Handler.Requests[0]);
        }

        /// <summary>A Thunderstore client that answers one package and touches no network.</summary>
        private sealed class AlwaysFindsIt : IThunderstoreClient
        {
            public Task<ThunderstorePackage> GetLatestAsync(string author, string modName) =>
                Task.FromResult(new ThunderstorePackage
                {
                    Namespace = author,
                    Name = modName,
                    Latest = new ThunderstorePackageVersion
                    {
                        VersionNumber = "1.0.0",
                        DownloadUrl = "https://example.invalid/pack.zip",
                    },
                });

            public Task<ThunderstorePackage> GetLiveAsync(string author, string modName) =>
                GetLatestAsync(author, modName);

            public Task<ThunderstoreLiveLookup> LookupLiveAsync(string author, string modName) =>
                Task.FromResult(new ThunderstoreLiveLookup { Answered = false });

            public Task<ThunderstoreIndexState> RefreshAsync() =>
                Task.FromResult<ThunderstoreIndexState>(null);

            public DateTime? IndexFetchedUtc => null;

            public string IndexSource => null;

            public ThunderstoreFailureMemo LastFailure => null;
        }

        // ------------------------------------------------------------- how far the list reaches

        /// <summary>
        /// The release list was one bare call, which GitHub answers with thirty and no hint
        /// that there are more. The pack lookup's contract is "the newest release at or below
        /// the version I am running", and that reaches DOWN: a rolling window of thirty would
        /// one day answer "no packs for your version" to a host whose packs are published and
        /// sitting on release thirty-one.
        /// </summary>
        [Fact]
        public async Task The_release_list_asks_for_a_full_page_and_follows_it()
        {
            var provider = new RecordingHttpClientProvider(request =>
            {
                var page = request.RequestUri?.Query.Contains("page=2") == true ? 2 : 1;
                var count = page == 1 ? GitHubClient.ReleasePageSize : 9;

                var rows = Enumerable.Range(0, count).Select(i =>
                    "{\"tag_name\":\"v0." + page + "." + i + "\",\"published_at\":\"2026-01-01T00:00:00Z\","
                    + "\"draft\":false,\"prerelease\":false,"
                    + "\"assets\":[{\"name\":\"a.zip\",\"browser_download_url\":\"https://x/a.zip\",\"size\":1}]}");

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[" + string.Join(",", rows) + "]", Encoding.UTF8, "application/json"),
                };
            });

            var client = new GitHubClient(
                new RestClientContext(GetService<Serilog.ILogger>(), provider));

            var releases = await client.GetReleasesAsync();

            Assert.Equal(GitHubClient.ReleasePageSize + 9, releases.Length);
            Assert.Equal(2, provider.Handler.Requests.Count);
            Assert.Contains("per_page=" + GitHubClient.ReleasePageSize, provider.Handler.Requests[0]);
            Assert.Contains("page=1", provider.Handler.Requests[0]);
            Assert.Contains("page=2", provider.Handler.Requests[1]);
        }

        [Fact]
        public async Task A_first_page_that_is_short_is_the_whole_list_and_costs_one_request()
        {
            var provider = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"tag_name\":\"v1.2.4\",\"published_at\":\"2026-01-01T00:00:00Z\",\"draft\":false,"
                        + "\"prerelease\":false,\"assets\":[{\"name\":\"a.zip\","
                        + "\"browser_download_url\":\"https://x/a.zip\",\"size\":1}]}]",
                        Encoding.UTF8, "application/json"),
                });

            var client = new GitHubClient(
                new RestClientContext(GetService<Serilog.ILogger>(), provider));

            Assert.Single(await client.GetReleasesAsync());
            Assert.Single(provider.Handler.Requests);
        }

        // ------------------------------------------------------- the small JSON calls' own clock

        /// <summary>
        /// Every small JSON call in the app rode HttpClient's accidental hundred seconds, chosen
        /// by nobody, and one of them is a launch step: a slow site froze the splash for a minute
        /// and forty with nothing on screen to say so. These bodies are small and buffered, so one
        /// clock over the whole exchange is the honest shape, and the point is that a person chose
        /// the number.
        /// </summary>
        [Fact]
        public void A_small_json_call_names_its_own_budget()
        {
            var client = new PlainClient(
                new RestClientContext(GetService<Serilog.ILogger>(), new MockHttpClientProvider()));
            var call = client.Ask("https://example.invalid/thing");

            Assert.True(call.Timeout > TimeSpan.Zero);
            Assert.True(call.Timeout < TimeSpan.FromSeconds(100),
                "the call still rides the hundred seconds nobody chose");

            call.WithTimeout(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromSeconds(5), call.Timeout);
        }

        /// <summary>The smallest real client there is, so the default budget is the shipped one.</summary>
        private sealed class PlainClient : RestClient
        {
            public PlainClient(IRestClientContext context) : base(context) { }

            public ApiCall Ask(string url) => Get(url);
        }

        // ------------------------------------------------------- what a failure is remembered by

        /// <summary>
        /// The connection state was keyed on the exception's MESSAGE, and a message is not an
        /// identity: the same address refusing the same connection words itself differently from
        /// one attempt to the next, and a socket error carries the endpoint in its text. Every
        /// variation read as a NEW state, so a server that had been down for an hour went on
        /// writing a Warning a host reads, over and over, for one thing being wrong once.
        /// </summary>
        [Fact]
        public void One_failure_is_one_state_however_its_message_is_worded()
        {
            var first = new SocketException((int)SocketError.ConnectionRefused);
            var second = new SocketException((int)SocketError.ConnectionRefused);

            Assert.Equal(RconClient.FailureState(first), RconClient.FailureState(second));
        }

        [Fact]
        public void A_different_failure_is_a_different_state()
        {
            var refused = new SocketException((int)SocketError.ConnectionRefused);
            var timedOut = new SocketException((int)SocketError.TimedOut);
            var other = new IOException("the stream ended");

            Assert.NotEqual(RconClient.FailureState(refused), RconClient.FailureState(timedOut));
            Assert.NotEqual(RconClient.FailureState(refused), RconClient.FailureState(other));
        }

        [Fact]
        public void A_socket_error_wrapped_in_something_else_is_still_read_by_its_code()
        {
            var wrapped = new IOException("could not connect",
                new SocketException((int)SocketError.ConnectionRefused));

            Assert.Contains(((int)SocketError.ConnectionRefused).ToString(), RconClient.FailureState(wrapped));
        }
    }
}
