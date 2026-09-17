using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// What happens once the host has said yes: where the bytes may come from, how many
    /// of them there may be, and what is left on disk when any of that goes wrong.
    /// <para>
    /// Every download here is served by <see cref="RecordingHttpHandler"/> from a zip
    /// built in memory. Nothing reaches the network, and every folder lives under a
    /// temporary path, never a real BepInEx tree.
    /// </para>
    /// </summary>
    public class HexiumInstallGuardTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "bakaloader-hexinstall-" + Guid.NewGuid().ToString("N"));
        private readonly string Plugins;

        public HexiumInstallGuardTests()
        {
            Plugins = Path.Combine(Root, "BepInEx", "plugins");
            Directory.CreateDirectory(Plugins);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }

        // --- Building the things a download answers with ---

        private static byte[] Package(string version, bool withASourceNote = false)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, "manifest.json", "{\"name\":\"LockSmith\",\"version_number\":\"" + version + "\"}");
                Write(zip, "LockSmith.dll", "not really a dll");
                if (withASourceNote)
                {
                    // A package claiming a history it does not have.
                    Write(zip, ModSourceMarkerFile.FileName,
                        "{\"schema\":1,\"writer\":\"BakaLoader 1.1.0\",\"source\":\"hexium\",\"version\":\"" + version + "\"}");
                    Write(zip, "nested/" + ModSourceMarkerFile.FileName, "{\"schema\":1}");
                }
            }
            return buffer.ToArray();
        }

        private static void Write(ZipArchive zip, string path, string content)
        {
            using var stream = zip.CreateEntry(path).Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static HttpResponseMessage Zip(byte[] bytes) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

        /// <summary>
        /// A stream nobody can measure ahead of time, which is what a chunked response
        /// looks like. It is how the cap that counts bytes as they arrive gets exercised
        /// rather than the one that reads a declared length.
        /// </summary>
        private sealed class UnmeasurableStream : Stream
        {
            private readonly MemoryStream Inner;

            public UnmeasurableStream(byte[] bytes) => Inner = new MemoryStream(bytes);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) Inner.Dispose();
                base.Dispose(disposing);
            }
        }

        private static HttpResponseMessage RedirectTo(string url)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(url);
            return response;
        }

        private (ModUpdateService Service, RecordingHttpHandler Handler) Build(
            Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            var provider = new RecordingHttpClientProvider(responder);
            var service = new ModUpdateService(
                Mock.Of<IThunderstoreClient>(), provider, Mock.Of<IApplicationLogger>());
            return (service, provider.Handler);
        }

        private static HexiumInstallPlan Plan(string url = "https://cdn.hexium.gg/upload/1255/0.3.6.zip",
            long? size = null, string version = "0.3.6") => new()
        {
            Owner = "DrakeMods",
            Name = "LockSmith",
            Version = version,
            DownloadUrl = url,
            FileSize = size,
            Dependencies = new[] { "denikson-BepInExPack_Valheim-5.4.2350" },
        };

        private string ExistingInstall(string version)
        {
            var dir = Path.Combine(Plugins, "DrakeMods-LockSmith");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"name\":\"LockSmith\",\"version_number\":\"" + version + "\"}");
            File.WriteAllText(Path.Combine(dir, "keepme.txt"), "the copy that was already here");
            return dir;
        }

        // --- The happy path, so the guards below mean something ---

        [Fact]
        public async Task An_accepted_install_places_the_folder_and_leaves_a_note()
        {
            var bytes = Package("0.3.6");
            var (service, handler) = Build(_ => Zip(bytes));

            var result = await service.InstallFromHexiumAsync(Plan(size: bytes.Length), Plugins);

            Assert.True(result.Installed, result.Error);
            Assert.False(result.Replaced);
            Assert.Equal("hexium", result.Source);
            Assert.Equal("0.3.6", result.Version);
            Assert.Equal(new[] { "denikson-BepInExPack_Valheim-5.4.2350" }, result.Dependencies);
            Assert.Single(handler.Requests);
            Assert.Equal("cdn.hexium.gg", handler.Hosts[0]);

            var folder = Path.Combine(Plugins, "DrakeMods-LockSmith");
            Assert.True(File.Exists(Path.Combine(folder, "LockSmith.dll")));
            Assert.Equal("hexium", ModSourceMarkerFile.ReadTrustedSource(folder, "0.3.6"));
        }

        [Fact]
        public async Task Replacing_an_existing_folder_backs_the_old_one_up_first()
        {
            var dir = ExistingInstall("0.3.5");
            var bytes = Package("0.3.6");
            var (service, _) = Build(_ => Zip(bytes));

            var result = await service.InstallFromHexiumAsync(Plan(size: bytes.Length), Plugins);

            Assert.True(result.Installed, result.Error);
            Assert.True(result.Replaced);
            Assert.False(File.Exists(Path.Combine(dir, "keepme.txt")));  // the folder was replaced

            var backups = Path.Combine(Root, "BepInEx", ".bakaloader-mod-backups", "DrakeMods-LockSmith");
            Assert.True(Directory.Exists(backups));
            Assert.Contains(Directory.EnumerateFiles(backups, "keepme.txt", SearchOption.AllDirectories), _ => true);
        }

        [Fact]
        public async Task A_note_that_came_inside_the_package_is_thrown_away_and_ours_is_written()
        {
            var bytes = Package("0.3.6", withASourceNote: true);
            var (service, _) = Build(_ => Zip(bytes));

            var result = await service.InstallFromHexiumAsync(Plan(size: bytes.Length), Plugins);
            Assert.True(result.Installed, result.Error);

            var folder = Path.Combine(Plugins, "DrakeMods-LockSmith");
            // The one in the nested folder came from the archive and is gone.
            Assert.False(File.Exists(Path.Combine(folder, "nested", ModSourceMarkerFile.FileName)));

            // The one at the top is ours, written after the archive's was removed, and it
            // carries the address this build actually fetched.
            var note = ModSourceMarkerFile.Read(folder);
            Assert.Equal("https://cdn.hexium.gg/upload/1255/0.3.6.zip", note.DownloadUrl);
            Assert.StartsWith("BakaLoader", note.Writer);
        }

        // --- Where the bytes may come from ---

        [Theory]
        [InlineData("https://cdn.nothexium.gg/upload/1/2.zip")]
        [InlineData("https://hexium.gg.example.com/upload/1/2.zip")]
        [InlineData("https://thunderstore.io/package/download/a/b/1.0.0/")]
        [InlineData("http://cdn.hexium.gg/upload/1/2.zip")]
        [InlineData("file:///C:/temp/x.zip")]
        [InlineData("")]
        public async Task An_address_off_the_site_is_never_fetched_at_all(string url)
        {
            var (service, handler) = Build(_ => Zip(Package("0.3.6")));

            var result = await service.InstallFromHexiumAsync(Plan(url), Plugins);

            Assert.False(result.Installed);
            Assert.Contains("hexium.gg", result.Error);
            Assert.Empty(handler.Requests);   // refused before a single request went out
            Assert.False(Directory.Exists(Path.Combine(Plugins, "DrakeMods-LockSmith")));
        }

        [Fact]
        public async Task A_redirect_off_the_site_stops_the_download_and_leaves_the_folder_alone()
        {
            var dir = ExistingInstall("0.3.5");
            var (service, handler) = Build(request =>
                request.RequestUri.Host == "cdn.hexium.gg"
                    ? RedirectTo("https://evil.example.com/payload.zip")
                    : Zip(Package("9.9.9")));

            var result = await service.InstallFromHexiumAsync(Plan(), Plugins);

            Assert.False(result.Installed);
            Assert.Contains("hexium.gg", result.Error);
            // The hop was noticed before it was followed, so the other host was never asked.
            Assert.Single(handler.Requests);
            Assert.DoesNotContain(handler.Hosts, h => h == "evil.example.com");
            // And the copy that was already installed is untouched.
            Assert.True(File.Exists(Path.Combine(dir, "keepme.txt")));
        }

        [Fact]
        public async Task A_redirect_that_stays_on_the_site_is_followed()
        {
            var bytes = Package("0.3.6");
            var (service, handler) = Build(request =>
                request.RequestUri.Host == "cdn.hexium.gg" && request.RequestUri.AbsolutePath.EndsWith(".zip")
                    ? RedirectTo("https://files.hexium.gg/real/0.3.6.zip")
                    : Zip(bytes));

            var result = await service.InstallFromHexiumAsync(Plan(), Plugins);

            Assert.True(result.Installed, result.Error);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("files.hexium.gg", handler.Hosts[1]);
        }

        [Fact]
        public async Task A_redirect_that_never_settles_gives_up()
        {
            var (service, handler) = Build(_ => RedirectTo("https://cdn.hexium.gg/upload/1255/again.zip"));

            var result = await service.InstallFromHexiumAsync(Plan(), Plugins);

            Assert.False(result.Installed);
            Assert.Contains("redirected too many times", result.Error);
            Assert.True(handler.Requests.Count <= 6);
        }

        // --- How many bytes there may be ---

        [Fact]
        public async Task A_download_that_keeps_coming_is_stopped_at_the_cap_and_replaces_nothing()
        {
            var dir = ExistingInstall("0.3.5");
            var bytes = Package("0.3.6");

            // No length in the headers, so the only thing that can stop this is the count
            // of bytes as they arrive. This is the case a declared size cannot catch.
            var (service, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UnmeasurableStream(bytes)),
            });
            service.MaxHexiumDownloadBytes = 16;   // smaller than any real package

            var result = await service.InstallFromHexiumAsync(Plan(), Plugins);

            Assert.False(result.Installed);
            Assert.Contains("passed the size", result.Error);
            Assert.True(File.Exists(Path.Combine(dir, "keepme.txt")));
            Assert.Null(ModSourceMarkerFile.ReadTrustedSource(dir, "0.3.5"));
        }

        [Fact]
        public async Task A_declared_length_over_the_cap_is_refused_without_reading_the_body()
        {
            var bytes = Package("0.3.6");
            var (service, _) = Build(_ => Zip(bytes));
            service.MaxHexiumDownloadBytes = 16;

            var result = await service.InstallFromHexiumAsync(Plan(), Plugins);

            Assert.False(result.Installed);
            Assert.Contains("larger than", result.Error);
            Assert.False(Directory.Exists(Path.Combine(Plugins, "DrakeMods-LockSmith")));
        }

        [Fact]
        public async Task A_size_the_headers_declare_over_the_cap_is_refused_before_the_body_is_read()
        {
            var bytes = Package("0.3.6");
            var (service, _) = Build(_ =>
            {
                var response = Zip(bytes);
                response.Content.Headers.ContentLength = 700L * 1024 * 1024;
                return response;
            });

            var result = await service.InstallFromHexiumAsync(Plan(), Plugins);

            Assert.False(result.Installed);
            Assert.Contains("larger than", result.Error);
            Assert.False(Directory.Exists(Path.Combine(Plugins, "DrakeMods-LockSmith")));
        }

        [Fact]
        public async Task A_download_that_does_not_weigh_what_the_index_said_replaces_nothing()
        {
            var dir = ExistingInstall("0.3.5");
            var bytes = Package("0.3.6");
            var (service, _) = Build(_ => Zip(bytes));

            // The index named a size, and what arrived is not it.
            var result = await service.InstallFromHexiumAsync(Plan(size: bytes.Length + 500), Plugins);

            Assert.False(result.Installed);
            Assert.Contains("size Hexium listed", result.Error);
            Assert.True(File.Exists(Path.Combine(dir, "keepme.txt")));
            Assert.False(File.Exists(Path.Combine(dir, ModSourceMarkerFile.FileName)));
        }

        [Fact]
        public async Task A_size_the_index_did_not_name_is_not_held_against_the_download()
        {
            var bytes = Package("0.3.6");
            var (service, _) = Build(_ => Zip(bytes));

            var result = await service.InstallFromHexiumAsync(Plan(size: null), Plugins);

            Assert.True(result.Installed, result.Error);
        }

        // --- Everything else that can go wrong leaves the install as it was ---

        [Fact]
        public async Task A_download_that_fails_outright_leaves_the_folder_alone()
        {
            var dir = ExistingInstall("0.3.5");
            var (service, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

            var result = await service.InstallFromHexiumAsync(Plan(), Plugins);

            Assert.False(result.Installed);
            Assert.True(File.Exists(Path.Combine(dir, "keepme.txt")));
        }

        [Fact]
        public async Task An_empty_package_is_refused_and_a_new_folder_is_not_left_behind()
        {
            using var buffer = new MemoryStream();
            using (new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) { }
            var empty = buffer.ToArray();

            var (service, _) = Build(_ => Zip(empty));

            var result = await service.InstallFromHexiumAsync(Plan(), Plugins);

            Assert.False(result.Installed);
            Assert.False(Directory.Exists(Path.Combine(Plugins, "DrakeMods-LockSmith")));
        }

        [Theory]
        [InlineData(null, "LockSmith", "0.3.6")]
        [InlineData("DrakeMods", null, "0.3.6")]
        [InlineData("DrakeMods", "LockSmith", null)]
        public async Task A_plan_missing_a_piece_of_the_identity_is_refused(string owner, string name, string version)
        {
            var (service, handler) = Build(_ => Zip(Package("0.3.6")));

            var result = await service.InstallFromHexiumAsync(new HexiumInstallPlan
            {
                Owner = owner,
                Name = name,
                Version = version,
                DownloadUrl = "https://cdn.hexium.gg/upload/1255/0.3.6.zip",
            }, Plugins);

            Assert.False(result.Installed);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task A_plugins_folder_that_is_not_there_is_refused()
        {
            var (service, handler) = Build(_ => Zip(Package("0.3.6")));

            var result = await service.InstallFromHexiumAsync(Plan(), Path.Combine(Root, "nowhere"));

            Assert.False(result.Installed);
            Assert.Contains("plugins folder", result.Error);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task The_user_agent_names_BakaLoader_on_the_download_too()
        {
            string agent = null;
            var bytes = Package("0.3.6");
            var (service, _) = Build(request =>
            {
                agent = request.Headers.UserAgent.ToString();
                return Zip(bytes);
            });

            await service.InstallFromHexiumAsync(Plan(), Plugins);

            Assert.StartsWith("BakaLoader/", agent);
        }
    }
}
