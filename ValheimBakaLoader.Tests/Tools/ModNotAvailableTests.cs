using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// A pinned build Thunderstore no longer has (SPEC-1212 M). Four of a real profile's mods
    /// were deprecated with no versions left, so each pinned download answered 404 and the host
    /// got the raw status sentence four times. A 404 on a pinned download now asks the package
    /// page once; deprecated, or the version not among the ones it lists, is the typed reason
    /// "notAvailable" with its own sentence. Anything else keeps the wording it always had.
    /// <para>
    /// Every answer comes from <see cref="RecordingHttpHandler"/>; nothing reaches thunderstore.io,
    /// and the Thunderstore client is a strict mock, so a pinned install is proved to look nothing
    /// up through it.
    /// </para>
    /// </summary>
    public class ModNotAvailableTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "bakaloader-gone-" + Guid.NewGuid().ToString("N"));
        private readonly string Plugins;

        private const string Download = "https://thunderstore.io/package/download/Azumatt/AzuCraftyBoxes/1.8.4/";
        private const string PackagePage = "https://thunderstore.io/api/experimental/package/Azumatt/AzuCraftyBoxes/";

        public ModNotAvailableTests()
        {
            Plugins = Path.Combine(Root, "BepInEx", "plugins");
            Directory.CreateDirectory(Plugins);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        /// <summary>The package page's shape, cut from the real answer for this package on 2026-10-12.</summary>
        private static string Page(bool deprecated, string latest, string versions = null) =>
            "{\"namespace\":\"Azumatt\",\"name\":\"AzuCraftyBoxes\",\"full_name\":\"Azumatt-AzuCraftyBoxes\",\"is_deprecated\":"
            + (deprecated ? "true" : "false")
            + ",\"latest\":{\"version_number\":\"" + latest + "\",\"download_url\":\"https://thunderstore.io/package/download/Azumatt/AzuCraftyBoxes/" + latest + "/\"}"
            + (versions == null ? "" : ",\"versions\":" + versions)
            + ",\"community_listings\":[{\"community\":\"valheim\"}]}";

        private (ModUpdateService, RecordingHttpClientProvider) Service(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            var provider = new RecordingHttpClientProvider(responder);
            var thunderstore = new Mock<IThunderstoreClient>(MockBehavior.Strict);
            return (new ModUpdateService(thunderstore.Object, provider, Mock.Of<IApplicationLogger>()), provider);
        }

        private static ThunderstoreModReference Pinned() => new() { Owner = "Azumatt", Name = "AzuCraftyBoxes", Version = "1.8.4" };

        private const string Sentence =
            "Azumatt-AzuCraftyBoxes 1.8.4 is no longer on Thunderstore (the package is deprecated or that version was removed); "
            + "Gale on the players' machines may still carry it";

        [Fact]
        public async Task A_404_on_a_deprecated_package_is_not_available()
        {
            var (service, provider) = Service(r => r.RequestUri!.ToString() switch
            {
                Download => new HttpResponseMessage(HttpStatusCode.NotFound),
                PackagePage => Json(Page(deprecated: true, latest: "1.8.19")),
                _ => new HttpResponseMessage(HttpStatusCode.NotImplemented),
            });

            var result = await service.InstallFromThunderstoreAsync(Pinned(), Plugins);

            Assert.False(result.Installed);
            Assert.Equal(ModInstallResult.NotAvailableReason, result.Reason);
            Assert.Equal("notAvailable", result.Reason);
            Assert.Equal(Sentence, result.Error);
            Assert.Equal("1.8.4", result.Version);
            // The page is asked once, after the download, and nothing else is.
            Assert.Equal(new[] { Download, PackagePage }, provider.Handler.Requests);
            Assert.False(Directory.Exists(Path.Combine(Plugins, "Azumatt-AzuCraftyBoxes")));
        }

        [Fact]
        public async Task A_404_on_a_version_the_package_no_longer_lists_is_not_available()
        {
            var (service, provider) = Service(r => r.RequestUri!.ToString() switch
            {
                Download => new HttpResponseMessage(HttpStatusCode.NotFound),
                PackagePage => Json(Page(deprecated: false, latest: "1.8.19",
                    versions: "[{\"version_number\":\"1.8.19\"},{\"version_number\":\"1.8.5\"}]")),
                _ => new HttpResponseMessage(HttpStatusCode.NotImplemented),
            });

            var result = await service.InstallFromThunderstoreAsync(Pinned(), Plugins);

            Assert.Equal("notAvailable", result.Reason);
            Assert.Equal(Sentence, result.Error);
            Assert.Equal(1, provider.Handler.Requests.Count(u => u == PackagePage));
        }

        [Fact]
        public async Task A_404_where_the_page_names_only_a_different_latest_is_not_available()
        {
            // The live page carries "latest" and no list of versions. A pinned build whose
            // download answered 404 and that is not the latest is not there.
            var (service, _) = Service(r => r.RequestUri!.ToString() switch
            {
                Download => new HttpResponseMessage(HttpStatusCode.NotFound),
                PackagePage => Json(Page(deprecated: false, latest: "1.8.19")),
                _ => new HttpResponseMessage(HttpStatusCode.NotImplemented),
            });

            var result = await service.InstallFromThunderstoreAsync(Pinned(), Plugins);

            Assert.Equal("notAvailable", result.Reason);
        }

        [Fact]
        public async Task A_404_on_a_version_the_package_still_lists_keeps_the_old_wording()
        {
            var (service, _) = Service(r => r.RequestUri!.ToString() switch
            {
                Download => new HttpResponseMessage(HttpStatusCode.NotFound),
                PackagePage => Json(Page(deprecated: false, latest: "1.8.4")),
                _ => new HttpResponseMessage(HttpStatusCode.NotImplemented),
            });

            var result = await service.InstallFromThunderstoreAsync(Pinned(), Plugins);

            Assert.Null(result.Reason);
            Assert.Contains("404", result.Error);
            Assert.EndsWith("(check that this version exists on the mod's Versions page)", result.Error);
        }

        [Fact]
        public async Task A_404_whose_package_page_does_not_answer_keeps_the_old_wording()
        {
            var (service, _) = Service(r => r.RequestUri!.ToString() switch
            {
                Download => new HttpResponseMessage(HttpStatusCode.NotFound),
                PackagePage => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => new HttpResponseMessage(HttpStatusCode.NotImplemented),
            });

            var result = await service.InstallFromThunderstoreAsync(Pinned(), Plugins);

            Assert.Null(result.Reason);
            Assert.Contains("404", result.Error);
        }

        [Fact]
        public async Task A_plain_network_failure_keeps_its_wording_and_asks_the_page_nothing()
        {
            var failure = new HttpRequestException("No such host is known. (thunderstore.io:443)");
            var (service, provider) = Service(r => r.RequestUri!.ToString() == Download
                ? throw failure
                : new HttpResponseMessage(HttpStatusCode.NotImplemented));

            var result = await service.InstallFromThunderstoreAsync(Pinned(), Plugins);

            Assert.False(result.Installed);
            Assert.Null(result.Reason);
            Assert.Equal(ModUpdateService.InstallFailureSentence("download", failure, "1.8.4"), result.Error);
            Assert.DoesNotContain(PackagePage, provider.Handler.Requests);
        }

        [Fact]
        public void The_catalog_sentence_matches_the_host_sides_words()
        {
            var en = Newtonsoft.Json.Linq.JObject.Parse(AppSourceTree.Web("i18n/en.json"));
            var lore = en["keys"]!["mods.add.not_available.toast"]!.Value<string>("lore");
            Assert.Equal(Sentence + ".", lore.Replace("{mod}", "Azumatt-AzuCraftyBoxes").Replace("{version}", "1.8.4"));
        }
    }
}
