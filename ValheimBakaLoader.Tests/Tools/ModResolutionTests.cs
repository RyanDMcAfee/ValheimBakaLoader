using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Which answer decides what gets installed.
    /// <para>
    /// A host deleted a mod and pasted its link back in, and BakaLoader reinstalled the
    /// version it had been insisting on rather than the one the page showed, because a
    /// link with no version on it was resolved out of the package list BakaLoader was
    /// holding. The list is a snapshot Thunderstore rebuilds on a timer, so it can be
    /// hours behind. These prove the order is now the other way round: the package's own
    /// address first, the held list only when that did not answer, and a link that names
    /// a version resolved through neither.
    /// </para>
    /// <para>
    /// Everything runs under a temporary folder and every answer comes from
    /// <see cref="RecordingHttpHandler"/>. Nothing here reaches thunderstore.io.
    /// </para>
    /// </summary>
    public class ModResolutionTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "bakaloader-resolve-" + Guid.NewGuid().ToString("N"));
        private readonly string Plugins;

        public ModResolutionTests()
        {
            Plugins = Path.Combine(Root, "BepInEx", "plugins");
            Directory.CreateDirectory(Plugins);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }

        private const string LiveUrl = "https://thunderstore.io/package/download/Vapok/XPortalNetworks/2.0.5/";
        private const string ListUrl = "https://thunderstore.io/package/download/Vapok/XPortalNetworks/2.0.3/";

        /// <summary>A package zip with one plausible file in it, so an install really lands.</summary>
        private static byte[] PackageZip(string version)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var manifest = new StreamWriter(archive.CreateEntry("manifest.json").Open());
                manifest.Write("{\"name\":\"XPortalNetworks\",\"version_number\":\"" + version + "\"}");
            }

            return buffer.ToArray();
        }

        private static HttpResponseMessage Zip(string version) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(PackageZip(version)) };

        /// <summary>A transport that hands back a package zip for any download address.</summary>
        private static RecordingHttpClientProvider Downloads() =>
            new(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url == LiveUrl) return Zip("2.0.5");
                if (url == ListUrl) return Zip("2.0.3");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        private static ThunderstorePackage Package(string version, string downloadUrl) => new()
        {
            Namespace = "Vapok",
            Name = "XPortalNetworks",
            Latest = new ThunderstorePackageVersion
            {
                VersionNumber = version,
                DownloadUrl = downloadUrl,
                FullName = "Vapok-XPortalNetworks-" + version,
            },
        };

        /// <summary>
        /// A client whose held list is two releases behind and whose live answer is the
        /// current one, which is the exact shape the host ran into.
        /// </summary>
        private static Mock<IThunderstoreClient> StaleListFreshLive()
        {
            var client = new Mock<IThunderstoreClient>();
            client.Setup(c => c.LookupLiveAsync("Vapok", "XPortalNetworks"))
                .ReturnsAsync(Answered(Package("2.0.5", LiveUrl)));
            client.Setup(c => c.GetLiveAsync("Vapok", "XPortalNetworks"))
                .ReturnsAsync(Package("2.0.5", LiveUrl));
            client.Setup(c => c.GetLatestAsync("Vapok", "XPortalNetworks"))
                .ReturnsAsync(Package("2.0.3", ListUrl));
            return client;
        }

        /// <summary>The site spoke: here is the package, or here is a plain no.</summary>
        private static ThunderstoreLiveLookup Answered(ThunderstorePackage package = null) =>
            new() { Answered = true, Package = package };

        /// <summary>The site did not speak, which says nothing about the package.</summary>
        private static ThunderstoreLiveLookup Silent() => new() { Answered = false };

        private static ModUpdateService Service(IThunderstoreClient thunderstore, RecordingHttpClientProvider provider) =>
            new(thunderstore, provider, Mock.Of<IApplicationLogger>());

        private static ThunderstoreModReference Link(string version = null) =>
            new() { Owner = "Vapok", Name = "XPortalNetworks", Version = version };

        private string InstallMod(string folderName, string version, bool fromHexium = false)
        {
            var dir = Path.Combine(Plugins, folderName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"name\":\"" + folderName + "\",\"version_number\":\"" + version + "\"}");

            if (fromHexium)
            {
                ModSourceMarkerFile.Write(dir, new ModSourceMarker
                {
                    Schema = ModSourceMarkerFile.CurrentSchema,
                    Writer = "BakaLoader 1.1.2",
                    Source = ModSourceMarkerFile.HexiumSource,
                    Owner = folderName.Split('-')[0],
                    Name = folderName.Split('-')[1],
                    Version = version,
                    DownloadUrl = "https://cdn.hexium.gg/upload/1/" + version + ".zip",
                    InstalledUtc = DateTime.UtcNow,
                });
            }

            return dir;
        }

        // --- Adding from a link ---

        [Fact]
        public async Task A_link_with_no_version_takes_the_live_answer_over_the_held_list()
        {
            var client = StaleListFreshLive();
            var provider = Downloads();

            var result = await Service(client.Object, provider).InstallFromThunderstoreAsync(Link(), Plugins);

            Assert.True(result.Installed);
            Assert.Equal("2.0.5", result.Version);

            // The address came back with the answer and was used exactly as it came.
            Assert.Equal(new[] { LiveUrl }, provider.Handler.Requests.ToArray());

            // The held list was never even consulted, because the live answer stood.
            client.Verify(c => c.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task When_the_live_answer_does_not_come_the_held_list_still_installs()
        {
            var client = StaleListFreshLive();
            client.Setup(c => c.LookupLiveAsync("Vapok", "XPortalNetworks")).ReturnsAsync(Silent());
            var provider = Downloads();

            var result = await Service(client.Object, provider).InstallFromThunderstoreAsync(Link(), Plugins);

            // Something old beats nothing at all, and the fallback is why refusing an odd
            // chunk or a slow address is safe everywhere else.
            Assert.True(result.Installed);
            Assert.Equal("2.0.3", result.Version);
            Assert.Equal(new[] { ListUrl }, provider.Handler.Requests.ToArray());
        }

        [Fact]
        public async Task A_link_that_names_a_version_is_fetched_without_asking_anybody()
        {
            // Strict: any lookup at all on this client is a failed test. A link with the
            // version on it is the workaround that already worked for the host, and it
            // has to keep working without depending on any list being right.
            var client = new Mock<IThunderstoreClient>(MockBehavior.Strict);
            var provider = Downloads();

            var result = await Service(client.Object, provider).InstallFromThunderstoreAsync(Link("2.0.3"), Plugins);

            Assert.True(result.Installed);
            Assert.Equal("2.0.3", result.Version);
            Assert.Equal(new[] { ListUrl }, provider.Handler.Requests.ToArray());
        }

        [Fact]
        public async Task A_package_neither_address_knows_installs_nothing_and_says_so()
        {
            var client = new Mock<IThunderstoreClient>();
            // The site spoke and has no such package, which is the only answer that means
            // the mod is not there.
            client.Setup(c => c.LookupLiveAsync("Vapok", "XPortalNetworks")).ReturnsAsync(Answered());
            var provider = Downloads();

            var result = await Service(client.Object, provider).InstallFromThunderstoreAsync(Link(), Plugins);

            Assert.False(result.Installed);
            Assert.Contains("Could not find", result.Error);
            Assert.Contains("Vapok/XPortalNetworks", result.Error);
            Assert.Empty(provider.Handler.Requests);
        }

        /// <summary>
        /// A site that would not speak is not a mod that does not exist. A host adding a
        /// brand new mod during a bad minute on the internet used to be told their mod was
        /// not on Thunderstore, which sends them looking for the wrong thing entirely.
        /// </summary>
        [Fact]
        public async Task A_site_that_did_not_answer_is_not_reported_as_a_mod_that_does_not_exist()
        {
            var client = new Mock<IThunderstoreClient>();
            client.Setup(c => c.LookupLiveAsync("Vapok", "XPortalNetworks")).ReturnsAsync(Silent());
            var provider = Downloads();

            var result = await Service(client.Object, provider).InstallFromThunderstoreAsync(Link(), Plugins);

            Assert.False(result.Installed);
            Assert.Contains("did not answer", result.Error);
            Assert.DoesNotContain("Could not find", result.Error);
            Assert.Contains("Vapok/XPortalNetworks", result.Error);
            Assert.Empty(provider.Handler.Requests);
        }

        /// <summary>The same distinction on the update path, which had lost it too.</summary>
        [Fact]
        public async Task An_update_during_an_outage_says_the_site_did_not_answer()
        {
            InstallMod("Vapok-XPortalNetworks", "2.0.2");
            var mod = new ModScanner(Mock.Of<IApplicationLogger>()).ScanPlugins(Plugins).Single();

            var client = new Mock<IThunderstoreClient>();
            client.Setup(c => c.LookupLiveAsync("Vapok", "XPortalNetworks")).ReturnsAsync(Silent());
            var provider = Downloads();

            var result = await Service(client.Object, provider).UpdateModAsync(mod);

            Assert.False(result.Updated);
            Assert.Contains("did not answer", result.Error);
            Assert.DoesNotContain("Could not find", result.Error);
            Assert.Empty(provider.Handler.Requests);
        }

        // --- Updating one installed mod ---

        [Fact]
        public async Task An_update_takes_the_live_answer_when_the_held_list_is_behind()
        {
            var dir = InstallMod("Vapok-XPortalNetworks", "2.0.2");
            var mod = new ModScanner(Mock.Of<IApplicationLogger>()).ScanPlugins(Plugins).Single();
            Assert.Equal(dir, mod.PluginDirectory);

            var client = StaleListFreshLive();
            var provider = Downloads();

            var result = await Service(client.Object, provider).UpdateModAsync(mod);

            Assert.True(result.Updated);
            Assert.Equal("2.0.2", result.FromVersion);
            Assert.Equal("2.0.5", result.ToVersion);
            Assert.Equal(new[] { LiveUrl }, provider.Handler.Requests.ToArray());
        }

        [Fact]
        public async Task A_copy_taken_from_the_other_site_is_still_left_alone()
        {
            InstallMod("Vapok-XPortalNetworks", "2.0.2", fromHexium: true);
            var mod = new ModScanner(Mock.Of<IApplicationLogger>()).ScanPlugins(Plugins).Single();

            var client = StaleListFreshLive();
            var provider = Downloads();

            var result = await Service(client.Object, provider).UpdateModAsync(mod);

            // Asking the live address changed nothing about this promise: the files the
            // host chose stay where they are, and nothing was fetched.
            Assert.False(result.Updated);
            Assert.Null(result.Error);
            Assert.Empty(provider.Handler.Requests);
            client.Verify(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            client.Verify(c => c.GetLiveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task An_update_that_is_already_current_fetches_nothing()
        {
            InstallMod("Vapok-XPortalNetworks", "2.0.5");
            var mod = new ModScanner(Mock.Of<IApplicationLogger>()).ScanPlugins(Plugins).Single();

            var client = StaleListFreshLive();
            var provider = Downloads();

            var result = await Service(client.Object, provider).UpdateModAsync(mod);

            Assert.False(result.Updated);
            Assert.Null(result.Error);
            Assert.Empty(provider.Handler.Requests);
        }
    }
}
