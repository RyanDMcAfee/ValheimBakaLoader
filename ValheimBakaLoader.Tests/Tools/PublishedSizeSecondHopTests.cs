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
    /// Where the size a mod download is held to actually comes from.
    /// <para>
    /// The check that refuses a package which did not arrive whole reads the size the site
    /// published about that build. Thunderstore's package page, which is the address the
    /// installer asks FIRST and which answers on every ordinary install and update, publishes
    /// no size and no digest at all: only version_number, download_url, date_created and a
    /// handful of display fields. So the check had nothing to compare with on the one path
    /// every host walks, and a damaged archive was still backed up over, cleared and written.
    /// The community listing carries a file_size per version, and the BepInEx installer has
    /// made that second hop since it was written. These tests drive the real service through
    /// the REAL shapes of both answers and ask whether a short download is refused.
    /// </para>
    /// <para>
    /// Nothing here reaches thunderstore.io: every answer comes from a mock client and a
    /// recording transport, and every file lands under a temporary folder.
    /// </para>
    /// </summary>
    public class PublishedSizeSecondHopTests : IDisposable
    {
        private readonly string Root = Path.Combine(
            Path.GetTempPath(), "bakaloader-secondhop-" + Guid.NewGuid().ToString("N"));

        private readonly string Plugins;

        public PublishedSizeSecondHopTests()
        {
            Plugins = Path.Combine(Root, "BepInEx", "plugins");
            Directory.CreateDirectory(Plugins);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        private const string Owner = "Vapok";
        private const string Name = "XPortalNetworks";
        private const string Latest = "2.0.5";
        private const string LatestUrl = "https://thunderstore.io/package/download/Vapok/XPortalNetworks/2.0.5/";

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

        /// <summary>
        /// The package page's answer, exactly the shape the live endpoint returns: the version
        /// and the address, and nothing at all about what the archive weighs. Checked against
        /// https://thunderstore.io/api/experimental/package/denikson/BepInExPack_Valheim/ ,
        /// whose "latest" carries date_created, dependencies, description, download_url,
        /// downloads, full_name, icon, is_active, name, namespace, version_number and
        /// website_url. No file_size. No sha256.
        /// </summary>
        private static ThunderstorePackage FromThePage(string version = Latest) => new()
        {
            Namespace = Owner,
            Name = Name,
            Latest = new ThunderstorePackageVersion
            {
                VersionNumber = version,
                DownloadUrl = "https://thunderstore.io/package/download/" + Owner + "/" + Name + "/" + version + "/",
                FullName = Owner + "-" + Name + "-" + version,
                DateCreated = new DateTime(2026, 4, 2, 11, 0, 0, DateTimeKind.Utc),
            },
        };

        /// <summary>
        /// The community listing's answer, which is the only one of the two that says what a
        /// version weighs. The live index carries file_size per version (702924 bytes for the
        /// BepInEx pack's 5.4.2351 the day this was written).
        /// </summary>
        private static ThunderstorePackage FromTheListing(string version, long? size) => new()
        {
            Namespace = Owner,
            Name = Name,
            Latest = new ThunderstorePackageVersion
            {
                VersionNumber = version,
                DownloadUrl = "https://thunderstore.io/package/download/" + Owner + "/" + Name + "/" + version + "/",
                FullName = Owner + "-" + Name + "-" + version,
                FileSize = size,
            },
        };

        private static Mock<IThunderstoreClient> Site(ThunderstorePackage page, ThunderstorePackage listing)
        {
            var client = new Mock<IThunderstoreClient>();
            client.Setup(c => c.LookupLiveAsync(Owner, Name))
                .ReturnsAsync(new ThunderstoreLiveLookup { Answered = true, Package = page });
            client.Setup(c => c.GetLiveAsync(Owner, Name)).ReturnsAsync(page);
            client.Setup(c => c.GetLatestAsync(Owner, Name)).ReturnsAsync(listing);
            return client;
        }

        private static RecordingHttpClientProvider Serving(byte[] body) =>
            new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

        private static ModUpdateService Service(IThunderstoreClient site, RecordingHttpClientProvider http) =>
            new(site, http, Mock.Of<IApplicationLogger>());

        private string InstallMod(string version)
        {
            var dir = Path.Combine(Plugins, Owner + "-" + Name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"name\":\"" + Name + "\",\"version_number\":\"" + version + "\"}");
            File.WriteAllText(Path.Combine(dir, "XPortalNetworks.dll"), "the working copy " + version);
            return dir;
        }

        // ------------------------------------------------------ the install every host walks

        /// <summary>
        /// The ordinary install: the page answered, so the page is what names the version, and
        /// the page publishes no size. A short archive was written over the mod folder anyway,
        /// because the only thing holding it to anything read a field the page never carries.
        /// </summary>
        [Fact]
        public async Task A_short_package_is_refused_on_the_path_the_page_answers()
        {
            var body = PackageZip(Latest);
            var site = Site(FromThePage(), FromTheListing(Latest, body.LongLength + 137));
            var http = Serving(body);

            var result = await Service(site.Object, http)
                .InstallFromThunderstoreAsync(new ThunderstoreModReference { Owner = Owner, Name = Name }, Plugins);

            Assert.False(result.Installed);
            Assert.Contains("did not arrive whole", result.Error);
            Assert.Contains("Nothing was replaced", result.Error);

            // And the folder was never created, let alone written into.
            Assert.False(Directory.Exists(Path.Combine(Plugins, Owner + "-" + Name)));
        }

        /// <summary>
        /// The same on the update path, where the cost is a working mod: the folder is backed
        /// up, cleared and refilled, so an archive nothing vouched for leaves the host with a
        /// plugin BepInEx cannot load and no record anywhere of why.
        /// </summary>
        [Fact]
        public async Task A_short_package_is_refused_before_a_working_mod_is_cleared()
        {
            var dir = InstallMod("2.0.2");
            var mod = new ModScanner(Mock.Of<IApplicationLogger>()).ScanPlugins(Plugins).Single();

            var body = PackageZip(Latest);
            var site = Site(FromThePage(), FromTheListing(Latest, body.LongLength + 4096));

            var result = await Service(site.Object, Serving(body)).UpdateModAsync(mod);

            Assert.False(result.Updated);
            Assert.Contains("did not arrive whole", result.Error);

            // The working copy is still the working copy.
            Assert.Equal("the working copy 2.0.2", File.ReadAllText(Path.Combine(dir, "XPortalNetworks.dll")));
            Assert.Contains("2.0.2", File.ReadAllText(Path.Combine(dir, "manifest.json")));
        }

        /// <summary>The whole archive still installs: the check is a size, not a refusal.</summary>
        [Fact]
        public async Task A_package_that_arrived_whole_installs()
        {
            var body = PackageZip(Latest);
            var site = Site(FromThePage(), FromTheListing(Latest, body.LongLength));

            var result = await Service(site.Object, Serving(body))
                .InstallFromThunderstoreAsync(new ThunderstoreModReference { Owner = Owner, Name = Name }, Plugins);

            Assert.True(result.Installed);
            Assert.Equal(Latest, result.Version);
        }

        // ------------------------------------------------ and the size belongs to one archive

        /// <summary>
        /// The listing is a snapshot the site rebuilds on a timer, so it can name an older
        /// version than the page does. A size belongs to one archive, so a size read off a
        /// DIFFERENT build must not be applied: it would turn every install during that window
        /// into a failed integrity check. Same rule the BepInEx installer already follows.
        /// </summary>
        [Fact]
        public async Task A_size_from_a_different_build_is_not_applied()
        {
            var body = PackageZip(Latest);

            // The listing is two releases behind and its archive weighs something else entirely.
            var site = Site(FromThePage(), FromTheListing("2.0.3", body.LongLength + 999));

            var result = await Service(site.Object, Serving(body))
                .InstallFromThunderstoreAsync(new ThunderstoreModReference { Owner = Owner, Name = Name }, Plugins);

            Assert.True(result.Installed);
            Assert.Equal(Latest, result.Version);
        }

        /// <summary>
        /// Neither answer said what it weighs, which is a machine on an old listing and a page
        /// that never carries one. That is not a reason to refuse the install: a guard that
        /// turns away every host is worse than the defect it was written for.
        /// </summary>
        [Fact]
        public async Task A_build_neither_answer_weighed_is_still_installed()
        {
            var body = PackageZip(Latest);
            var site = Site(FromThePage(), FromTheListing(Latest, null));

            var result = await Service(site.Object, Serving(body))
                .InstallFromThunderstoreAsync(new ThunderstoreModReference { Owner = Owner, Name = Name }, Plugins);

            Assert.True(result.Installed);
            Assert.Equal(Latest, result.Version);
        }

        /// <summary>
        /// A listing that could not be read at all leaves the install unheld rather than
        /// stopped. The size is a check on the bytes, not a second thing that has to answer
        /// before a host may add a mod.
        /// </summary>
        [Fact]
        public async Task A_listing_that_did_not_answer_does_not_stop_the_install()
        {
            var body = PackageZip(Latest);
            var client = new Mock<IThunderstoreClient>();
            client.Setup(c => c.LookupLiveAsync(Owner, Name))
                .ReturnsAsync(new ThunderstoreLiveLookup { Answered = true, Package = FromThePage() });
            client.Setup(c => c.GetLatestAsync(Owner, Name))
                .ThrowsAsync(new HttpRequestException("the listing did not answer"));

            var result = await Service(client.Object, Serving(body))
                .InstallFromThunderstoreAsync(new ThunderstoreModReference { Owner = Owner, Name = Name }, Plugins);

            Assert.True(result.Installed);
            Assert.Equal(Latest, result.Version);
        }

        /// <summary>
        /// The version still comes from the page. The second hop is for the size only, so a
        /// listing that is behind must not drag the install back to an older build, which is
        /// the bug the page-first order was written to fix in the first place.
        /// </summary>
        [Fact]
        public async Task The_page_still_decides_which_version_is_installed()
        {
            var body = PackageZip(Latest);
            var site = Site(FromThePage(), FromTheListing("2.0.3", body.LongLength));
            var http = Serving(body);

            var result = await Service(site.Object, http)
                .InstallFromThunderstoreAsync(new ThunderstoreModReference { Owner = Owner, Name = Name }, Plugins);

            Assert.True(result.Installed);
            Assert.Equal(Latest, result.Version);
            Assert.Equal(new[] { LatestUrl }, http.Handler.Requests.ToArray());
        }

        /// <summary>
        /// A link that names its own version is fetched with nothing asked of anybody, and that
        /// stays true: the second hop must not turn a pinned link into two lookups.
        /// </summary>
        [Fact]
        public async Task A_pinned_link_still_asks_nobody_anything()
        {
            var body = PackageZip("2.0.3");
            var client = new Mock<IThunderstoreClient>(MockBehavior.Strict);

            var result = await Service(client.Object, Serving(body)).InstallFromThunderstoreAsync(
                new ThunderstoreModReference { Owner = Owner, Name = Name, Version = "2.0.3" }, Plugins);

            Assert.True(result.Installed);
            Assert.Equal("2.0.3", result.Version);
        }
    }
}
