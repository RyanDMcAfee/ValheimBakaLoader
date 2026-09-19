using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Fetching a language pack: where the bytes come from, how many there may be, what has
    /// to check out before anything on disk moves, and what is left behind when any of it
    /// goes wrong.
    /// <para>
    /// Every download here is answered by <see cref="RecordingHttpHandler"/> from a zip built
    /// in memory, and every folder is a temporary one. Nothing reaches GitHub and nothing is
    /// written anywhere near a real install. The recorded request list is the proof for the
    /// two switch tests: an empty list is a stronger statement than any flag the code could set.
    /// </para>
    /// </summary>
    public class LanguagePackServiceTests : IDisposable
    {
        private const string AppVersion = "1.2.0";

        private static readonly string TagsUrl =
            ValheimBakaLoader.Properties.Resources.UrlGithubApi + "/releases/tags/v" + AppVersion;

        private static readonly string ReleasesUrl =
            ValheimBakaLoader.Properties.Resources.UrlGithubApi + "/releases";

        // Deliberately not a shape anything could compose. A manifest may publish its packs
        // anywhere GitHub redirects them to, and the app's only job is to use the address it
        // was given.
        private const string ManifestUrl = "https://objects.example.invalid/held/7f3c/lang-manifest.json?token=abc";
        private const string RuPackUrl = "https://objects.example.invalid/held/7f3c/ru-pack-bytes?token=def";
        private const string JaPackUrl = "https://objects.example.invalid/held/91aa/ja-pack-bytes?token=ghi";

        private readonly string Root =
            Path.Combine(Path.GetTempPath(), "bakaloader-langpack-" + Guid.NewGuid().ToString("N"));

        private readonly MockUserPreferencesProvider Prefs = new();

        public LanguagePackServiceTests() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ building the answers

        private static ILogger Quiet() => new LoggerConfiguration().CreateLogger();

        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static byte[] Filler(int bytes, int seed)
        {
            // Random rather than repeated, so the zip does not compress down to nothing and
            // the stream is long enough to be read in several passes.
            var buffer = new byte[bytes];
            new Random(seed).NextBytes(buffer);
            return buffer;
        }

        private static string CatalogJson(string code, string version, int catalog) =>
            JsonConvert.SerializeObject(new
            {
                _meta = new { lang = code, language = code, appVersion = version, catalog },
                keys = new Dictionary<string, object>
                {
                    ["app.title"] = new { lore = "BakaLoader" },
                    ["app.start"] = new { lore = "Start" },
                },
            });

        /// <summary>A pack zip, exactly the shape pack_tools cuts.</summary>
        private static byte[] PackZip(
            string code,
            string version,
            int catalog,
            (string Name, byte[] Bytes)[] fonts = null,
            string declaredLanguage = null,
            bool withStrings = true)
        {
            fonts ??= Array.Empty<(string, byte[])>();

            var faces = fonts.Select(f => new
            {
                file = "fonts/" + f.Name,
                family = Path.GetFileNameWithoutExtension(f.Name),
                weight = "400",
                style = "normal",
                sha256 = Sha(f.Bytes),
            }).ToArray();

            var pack = JsonConvert.SerializeObject(new
            {
                schema = 1,
                code,
                appVersion = version,
                asset = $"lang-{code.ToLowerInvariant()}-{version}.zip",
                catalog,
                nativeName = code,
                englishName = code,
                keys = 2,
                translated = 2,
                status = "machine",
                minAppVersion = "1.0.0",
                fonts = faces,
            });

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, "pack.json", Encoding.UTF8.GetBytes(pack));
                if (withStrings)
                {
                    Write(zip, "strings.json",
                        Encoding.UTF8.GetBytes(CatalogJson(declaredLanguage ?? code, version, catalog)));
                }

                foreach (var (name, bytes) in fonts) Write(zip, "fonts/" + name, bytes);
                if (fonts.Length > 0) Write(zip, "fonts/OFL.txt", Encoding.UTF8.GetBytes("SIL Open Font License"));
            }

            return buffer.ToArray();
        }

        private static void Write(ZipArchive zip, string path, byte[] bytes)
        {
            using var stream = zip.CreateEntry(path).Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        private static object Entry(
            string code, string url, byte[] zip, int catalog, long? bytes = null, string sha = null,
            string minAppVersion = null) => new
            {
                code,
                asset = $"lang-{code.ToLowerInvariant()}-{AppVersion}.zip",
                url,
                bytes = bytes ?? zip.Length,
                sha256 = sha ?? Sha(zip),
                catalog,
                nativeName = code,
                englishName = code,
                keys = 2,
                translated = 2,
                status = "machine",
                minAppVersion = minAppVersion ?? "1.0.0",
                appVersion = AppVersion,
            };

        private static string ManifestJson(params object[] entries) =>
            JsonConvert.SerializeObject(new
            {
                schema = 1,
                appVersion = AppVersion,
                generatedUtc = "2026-09-17T00:00:00Z",
                catalog = 7,
                languages = entries,
            });

        private static string ReleaseJson() =>
            JsonConvert.SerializeObject(new
            {
                tag_name = "v" + AppVersion,
                html_url = "https://example.invalid/releases/tag/v" + AppVersion,
                published_at = "2026-09-17T00:00:00Z",
                draft = false,
                prerelease = false,
                assets = new[]
                {
                    new
                    {
                        name = LanguagePackService.ManifestAssetName,
                        browser_download_url = ManifestUrl,
                        size = 512,
                    },
                },
            });

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Bytes(byte[] bytes) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

        private static HttpResponseMessage Missing() => new(HttpStatusCode.NotFound);

        /// <summary>The ordinary setup: one release, one manifest, one pack behind it.</summary>
        private Func<HttpRequestMessage, HttpResponseMessage> Serving(string manifest, params (string Url, byte[] Bytes)[] packs)
        {
            return request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url == TagsUrl) return Json(ReleaseJson());
                if (url == ManifestUrl) return Json(manifest);

                foreach (var (packUrl, bytes) in packs)
                {
                    if (url == packUrl) return Bytes(bytes);
                }

                return Missing();
            };
        }

        private (LanguagePackService Service, RecordingHttpHandler Handler) Build(
            Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            var provider = new RecordingHttpClientProvider(responder);
            var github = new GitHubClient(new RestClientContext(Quiet(), provider));

            var service = new LanguagePackService(
                github, provider, Prefs, Mock.Of<IAnalyticsService>(), Mock.Of<IApplicationLogger>())
            {
                RootFolder = Root,
                AppVersion = AppVersion,
            };

            return (service, provider.Handler);
        }

        /// <summary>A pack already on disk, with a file in it that proves whether it was replaced.</summary>
        private string PlaceInstalled(string code, string version, int catalog, params string[] fontFiles)
        {
            var dir = Path.Combine(Root, code, version);
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "pack.json"), JsonConvert.SerializeObject(new
            {
                schema = 1,
                code,
                appVersion = version,
                catalog,
                nativeName = code,
                englishName = code,
                keys = 2,
                translated = 2,
                status = "machine",
                fonts = fontFiles.Select(f => new { file = f, family = "Held", weight = "400", style = "normal", sha256 = "" }),
            }));

            File.WriteAllText(Path.Combine(dir, "strings.json"), CatalogJson(code, version, catalog));
            File.WriteAllText(Path.Combine(dir, "keepme.txt"), "the copy that was already here");
            return dir;
        }

        private string Staging => Path.Combine(Root, ".staging");

        private void AssertStagingIsEmpty()
        {
            if (!Directory.Exists(Staging)) return;
            Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
        }

        // ------------------------------------------------------------------ 1. the address

        /// <summary>
        /// The app reads the address off the manifest and asks for exactly that. Nothing in
        /// the recorded list may look like an address the app could have built from a code
        /// and a version, because that is the 404 that reads as "that language does not exist".
        /// </summary>
        [Fact]
        public async Task The_address_asked_for_is_the_one_the_manifest_published()
        {
            var zip = PackZip("ru", AppVersion, 7);
            var (service, handler) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var result = await service.DownloadAsync("ru");

            Assert.True(result.Ok, result.ReasonId);
            Assert.Contains(ManifestUrl, handler.Requests);
            Assert.Contains(RuPackUrl, handler.Requests);
            Assert.DoesNotContain(handler.Requests, r => r.Contains("releases/download", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.Requests, r => r.Contains("lang-ru-1.2.0.zip", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(Path.Combine(Root, "ru", AppVersion), result.Folder);
            Assert.True(File.Exists(Path.Combine(result.Folder, "strings.json")));
            AssertStagingIsEmpty();
        }

        // ------------------------------------------------------------------ 2 and 3. integrity

        [Fact]
        public async Task A_pack_that_does_not_match_its_published_checksum_replaces_nothing()
        {
            var held = PlaceInstalled("ru", "1.1.0", catalog: 6);
            var zip = PackZip("ru", AppVersion, 7);

            var manifest = ManifestJson(Entry("ru", RuPackUrl, zip, 7, sha: new string('a', 64)));
            var (service, _) = Build(Serving(manifest, (RuPackUrl, zip)));

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.Integrity, result.ReasonId);
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
            Assert.True(File.Exists(Path.Combine(held, "keepme.txt")));
            AssertStagingIsEmpty();
        }

        [Fact]
        public async Task A_pack_that_does_not_weigh_what_the_manifest_published_replaces_nothing()
        {
            var held = PlaceInstalled("ru", "1.1.0", catalog: 6);
            var zip = PackZip("ru", AppVersion, 7);

            var manifest = ManifestJson(Entry("ru", RuPackUrl, zip, 7, bytes: zip.Length + 500));
            var (service, _) = Build(Serving(manifest, (RuPackUrl, zip)));

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.Integrity, result.ReasonId);
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
            Assert.True(File.Exists(Path.Combine(held, "keepme.txt")));
            AssertStagingIsEmpty();
        }

        // ------------------------------------------------------------------ 4. the cap

        /// <summary>
        /// A stream nobody can measure ahead of time, which is what a chunked response looks
        /// like. It is how the cap that counts bytes as they arrive gets exercised rather than
        /// the one that reads a declared length.
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

        [Fact]
        public async Task A_pack_that_keeps_coming_is_stopped_at_the_cap_and_leaves_nothing_behind()
        {
            var held = PlaceInstalled("ru", "1.1.0", catalog: 6);
            var zip = PackZip("ru", AppVersion, 7, new[] { ("Filler.woff2", Filler(200_000, seed: 3)) });

            // The manifest declares a small pack and the stream keeps going, with no length in
            // the headers. The only thing that can stop this is the count as the bytes arrive.
            var manifest = ManifestJson(Entry("ru", RuPackUrl, zip, 7, bytes: 8));
            var (service, _) = Build(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url == TagsUrl) return Json(ReleaseJson());
                if (url == ManifestUrl) return Json(manifest);
                if (url == RuPackUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new UnmeasurableStream(zip)),
                    };
                }

                return Missing();
            });

            service.MaxLanguagePackBytes = 16;

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.TooLarge, result.ReasonId);
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
            Assert.True(File.Exists(Path.Combine(held, "keepme.txt")));
            AssertStagingIsEmpty();
        }

        [Fact]
        public async Task A_published_size_over_the_cap_is_refused_without_asking_for_a_byte()
        {
            var zip = PackZip("ru", AppVersion, 7);
            var manifest = ManifestJson(Entry("ru", RuPackUrl, zip, 7, bytes: 900L * 1024 * 1024));
            var (service, handler) = Build(Serving(manifest, (RuPackUrl, zip)));

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.TooLarge, result.ReasonId);
            Assert.DoesNotContain(RuPackUrl, handler.Requests);
        }

        // ------------------------------------------------------------------ 5. progress

        [Fact]
        public async Task Progress_only_ever_moves_forward_and_reaches_a_hundred_before_done()
        {
            var zip = PackZip("ru", AppVersion, 7, new[] { ("Filler.woff2", Filler(300_000, seed: 11)) });
            var (service, _) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var seen = new List<LanguagePackProgress>();
            var result = await service.DownloadAsync("ru", new Inline<LanguagePackProgress>(seen.Add));

            Assert.True(result.Ok, result.ReasonId);

            var downloading = seen.Where(p => p.Phase == LanguagePackPhases.Downloading).ToList();
            Assert.True(downloading.Count >= 3, "a 300 KB pack should report more than once");

            long bytes = 0;
            var percent = -1;
            foreach (var push in seen)
            {
                Assert.True(push.BytesDone >= bytes || push.BytesDone == 0, "bytes went backwards");
                if (push.BytesDone > 0) bytes = push.BytesDone;

                if (push.Percent < 0) continue;
                Assert.True(push.Percent >= percent, "the percentage went backwards");
                percent = push.Percent;
            }

            Assert.Equal(LanguagePackPhases.Done, seen[^1].Phase);
            Assert.Equal(100, seen[^1].Percent);
            Assert.Equal(100, seen[^2].Percent);
            Assert.Equal(
                new[]
                {
                    LanguagePackPhases.Resolving, LanguagePackPhases.Downloading,
                    LanguagePackPhases.Verifying, LanguagePackPhases.Installing, LanguagePackPhases.Done,
                },
                seen.Select(p => p.Phase).Distinct().ToArray());
        }

        /// <summary>
        /// Runs its callback on the reporting thread, so the order the suite reads is the order
        /// the service made them in. The bridge's own SynchronousProgress exists for the same
        /// reason: Progress&lt;T&gt; posts asynchronously and can reorder.
        /// </summary>
        private sealed class Inline<T> : IProgress<T>
        {
            private readonly Action<T> Handler;

            public Inline(Action<T> handler) => Handler = handler;

            public void Report(T value) => Handler(value);
        }

        // ------------------------------------------------------------------ 6 and 7. cancel

        [Fact]
        public async Task A_cancel_while_the_bytes_are_coming_leaves_the_pack_that_was_there()
        {
            var held = PlaceInstalled("ru", "1.1.0", catalog: 6);
            var zip = PackZip("ru", AppVersion, 7, new[] { ("Filler.woff2", Filler(400_000, seed: 5)) });
            var (service, _) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var cancelTook = (bool?)null;
            var progress = new Inline<LanguagePackProgress>(push =>
            {
                if (cancelTook == null && push.Phase == LanguagePackPhases.Downloading && push.BytesDone > 0)
                    cancelTook = service.Cancel("ru");
            });

            var result = await service.DownloadAsync("ru", progress);

            Assert.True(cancelTook, "the cancel was refused while the bytes were still coming");
            Assert.False(result.Ok);
            Assert.True(result.Cancelled);
            Assert.Equal(LanguagePackReasons.Cancelled, result.ReasonId);

            Assert.False(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
            Assert.True(File.Exists(Path.Combine(held, "keepme.txt")));
            AssertStagingIsEmpty();
        }

        [Fact]
        public async Task A_cancel_once_the_pack_is_going_into_place_is_refused_and_says_so()
        {
            var zip = PackZip("ru", AppVersion, 7);
            var (service, _) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var answered = (bool?)null;
            service.BeforePlacing = _ => answered = service.Cancel("ru");

            var result = await service.DownloadAsync("ru");

            Assert.False(answered, "a cancel during the move must answer false rather than stop it");
            Assert.True(result.Ok, result.ReasonId);
            Assert.True(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
        }

        /// <summary>
        /// A cancel names the language it is cancelling. A code that is not the one running,
        /// or one the app does not know at all, must not stop somebody else's download.
        /// </summary>
        [Fact]
        public async Task A_cancel_for_another_language_does_not_stop_the_one_that_is_running()
        {
            var zip = PackZip("ru", AppVersion, 7);
            var (service, _) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var answers = new List<bool>();
            service.BeforePlacing = _ =>
            {
                answers.Add(service.Cancel("ja"));         // a different language
                answers.Add(service.Cancel("klingon"));    // not a language at all
            };

            var result = await service.DownloadAsync("ru");

            Assert.Equal(new[] { false, false }, answers);
            Assert.True(result.Ok, result.ReasonId);

            // And with nothing running at all, a cancel is simply false.
            Assert.False(service.Cancel("ru"));
        }

        /// <summary>A connection that opened and then went quiet. The stall window ends it.</summary>
        private sealed class SilentStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// A download that stopped receiving data and a download the host stopped are the same
        /// ending in the code and two different sentences to a person, so they carry different
        /// ids and the cancelled flag tells them apart.
        /// </summary>
        [Fact]
        public async Task A_download_that_stops_receiving_data_is_not_reported_as_a_cancel()
        {
            var zip = PackZip("ru", AppVersion, 7);
            var (service, _) = Build(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url == TagsUrl) return Json(ReleaseJson());
                if (url == ManifestUrl) return Json(ManifestJson(Entry("ru", RuPackUrl, zip, 7)));
                if (url == RuPackUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()) };
                }

                return Missing();
            });

            service.StallTimeout = TimeSpan.FromMilliseconds(250);

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.False(result.Cancelled);
            Assert.Equal(LanguagePackReasons.Stalled, result.ReasonId);
            AssertStagingIsEmpty();
        }

        // ------------------------------------------------------------------ 8 and 9. the switch

        [Fact]
        public async Task The_quiet_fetch_asks_nobody_anything_when_update_checking_is_off()
        {
            Prefs.LoadPreferences().CheckForUpdates = false;

            var zip = PackZip("ru", AppVersion, 7);
            var (service, handler) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var said = await service.EnsureCurrentQuietlyAsync(
                prefsCheckForUpdates: false, savedLanguage: "ru", runningVersion: AppVersion);

            Assert.Equal(LanguageQuietFetch.SkippedChecksOff, said);
            Assert.Empty(handler.Requests);
            Assert.False(Directory.Exists(Path.Combine(Root, "ru")));
        }

        [Fact]
        public async Task A_download_the_host_asked_for_goes_ahead_with_the_switch_off()
        {
            Prefs.LoadPreferences().CheckForUpdates = false;

            var zip = PackZip("ru", AppVersion, 7);
            var (service, handler) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var result = await service.DownloadAsync("ru", userInitiated: true);

            Assert.True(result.Ok, result.ReasonId);
            Assert.Contains(RuPackUrl, handler.Requests);
        }

        [Fact]
        public async Task The_quiet_fetch_does_nothing_for_english_or_for_a_pack_that_is_already_here()
        {
            var zip = PackZip("ru", AppVersion, 7);
            var (service, handler) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            Assert.Equal(LanguageQuietFetch.None, await service.EnsureCurrentQuietlyAsync(true, "en", AppVersion));
            Assert.Empty(handler.Requests);

            PlaceInstalled("ru", AppVersion, catalog: 7);
            Assert.Equal(LanguageQuietFetch.None, await service.EnsureCurrentQuietlyAsync(true, "ru", AppVersion));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task The_quiet_fetch_runs_after_an_update_and_leaves_the_older_pack_where_it_is()
        {
            var held = PlaceInstalled("ru", "1.1.9", catalog: 6);
            var zip = PackZip("ru", AppVersion, 7);
            var (service, handler) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var said = await service.EnsureCurrentQuietlyAsync(true, "ru", AppVersion);

            Assert.Equal(LanguageQuietFetch.Started, said);
            Assert.Contains(RuPackUrl, handler.Requests);
            Assert.True(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
            // The pack the host was using is never deleted to make room for the new one.
            Assert.True(File.Exists(Path.Combine(held, "keepme.txt")));
        }

        // ------------------------------------------------------------------ 10. the unchanged catalogue

        [Fact]
        public async Task An_unchanged_catalogue_is_satisfied_by_the_pack_already_on_disk()
        {
            PlaceInstalled("ru", "1.1.0", catalog: 7);
            var zip = PackZip("ru", AppVersion, 7);
            var (service, handler) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var result = await service.DownloadAsync("ru");

            Assert.True(result.Ok, result.ReasonId);
            Assert.DoesNotContain(RuPackUrl, handler.Requests);

            var landed = Path.Combine(Root, "ru", AppVersion);
            Assert.Equal(landed, result.Folder);
            Assert.True(File.Exists(Path.Combine(landed, "strings.json")));

            // The copy says it is the pack for this version, so reading it back agrees with
            // the folder it is in.
            var install = service.Installed("ru");
            Assert.NotNull(install);
            Assert.Equal(AppVersion, install.Version);
            Assert.True(install.MatchesApp);
            AssertStagingIsEmpty();
        }

        // ------------------------------------------------------------------ 15. the font store

        [Fact]
        public async Task A_face_two_packs_share_is_stored_once_and_named_by_its_digest()
        {
            var shared = Filler(9_000, seed: 21);
            var onlyJapanese = Filler(4_000, seed: 22);

            var ru = PackZip("ru", AppVersion, 7, new[] { ("Shared.woff2", shared) });
            var ja = PackZip("ja", AppVersion, 7, new[] { ("Shared.woff2", shared), ("NotoSansJP.woff2", onlyJapanese) });

            var manifest = ManifestJson(Entry("ru", RuPackUrl, ru, 7), Entry("ja", JaPackUrl, ja, 7));
            var (service, _) = Build(Serving(manifest, (RuPackUrl, ru), (JaPackUrl, ja)));

            Assert.True((await service.DownloadAsync("ru")).Ok);
            Assert.True((await service.DownloadAsync("ja")).Ok);

            var store = Path.Combine(Root, "_fonts");
            var sharedDigest = Sha(shared);

            Assert.True(File.Exists(Path.Combine(store, sharedDigest + ".woff2")));
            Assert.True(File.Exists(Path.Combine(store, Sha(onlyJapanese) + ".woff2")));

            // One copy of the shared face, whichever pack brought it.
            Assert.Single(Directory.EnumerateFiles(store, "*.woff2")
                .Where(f => Path.GetFileNameWithoutExtension(f) == sharedDigest));

            foreach (var code in new[] { "ru", "ja" })
            {
                var folder = Path.Combine(Root, code, AppVersion);
                Assert.False(Directory.Exists(Path.Combine(folder, "fonts")));

                var pack = JObject.Parse(File.ReadAllText(Path.Combine(folder, "pack.json")));
                var files = pack["fonts"].Select(f => (string)f["file"]).ToList();
                Assert.Contains("_fonts/" + sharedDigest + ".woff2", files);
                Assert.All(files, f => Assert.StartsWith("_fonts/", f));

                // The licence travelled with the fonts.
                var licences = pack["licenses"].Select(l => (string)l).ToList();
                Assert.All(licences, l => Assert.True(File.Exists(Path.Combine(Root, l.Replace('/', Path.DirectorySeparatorChar)))));
            }
        }

        [Fact]
        public async Task A_face_that_does_not_match_the_digest_the_pack_published_is_refused()
        {
            var font = Filler(5_000, seed: 31);
            var zip = TamperedFontPack("ru", AppVersion, 7, font);

            var manifest = ManifestJson(Entry("ru", RuPackUrl, zip, 7));
            var (service, _) = Build(Serving(manifest, (RuPackUrl, zip)));

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.Integrity, result.ReasonId);
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
        }

        /// <summary>A pack whose own pack.json names a digest the font in it does not have.</summary>
        private static byte[] TamperedFontPack(string code, string version, int catalog, byte[] font)
        {
            var pack = JsonConvert.SerializeObject(new
            {
                schema = 1,
                code,
                appVersion = version,
                catalog,
                keys = 2,
                translated = 2,
                status = "machine",
                fonts = new[]
                {
                    new { file = "fonts/Face.woff2", family = "Face", weight = "400", style = "normal", sha256 = new string('b', 64) },
                },
            });

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, "pack.json", Encoding.UTF8.GetBytes(pack));
                Write(zip, "strings.json", Encoding.UTF8.GetBytes(CatalogJson(code, version, catalog)));
                Write(zip, "fonts/Face.woff2", font);
                Write(zip, "fonts/OFL.txt", Encoding.UTF8.GetBytes("SIL Open Font License"));
            }

            return buffer.ToArray();
        }

        // ------------------------------------------------------------------ 16. the boot sweep

        [Fact]
        public void The_sweep_keeps_the_newest_two_versions_and_the_one_the_app_is_running()
        {
            var kept = Sha(Filler(64, seed: 41));
            PlaceInstalled("ru", "1.3.0", catalog: 9, "_fonts/" + kept + ".woff2");
            PlaceInstalled("ru", "1.2.5", catalog: 8);
            PlaceInstalled("ru", AppVersion, catalog: 7);
            PlaceInstalled("ru", "1.0.0", catalog: 5);

            Directory.CreateDirectory(Path.Combine(Root, "_fonts"));
            File.WriteAllBytes(Path.Combine(Root, "_fonts", kept + ".woff2"), Filler(64, seed: 41));
            File.WriteAllBytes(Path.Combine(Root, "_fonts", "orphan.woff2"), Filler(64, seed: 42));

            Directory.CreateDirectory(Path.Combine(Root, ".staging", "ru-1.2.0-leftover"));
            File.WriteAllText(Path.Combine(Root, ".staging", "ru-1.2.0-leftover", "pack.zip"), "half a download");

            var (service, handler) = Build(_ => Missing());

            var removed = service.PruneOnBoot();

            Assert.True(removed > 0);
            Assert.Empty(handler.Requests);

            Assert.True(Directory.Exists(Path.Combine(Root, "ru", "1.3.0")));
            Assert.True(Directory.Exists(Path.Combine(Root, "ru", "1.2.5")));
            // Not in the newest two, kept because it is the one the app is running.
            Assert.True(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", "1.0.0")));

            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(Root, ".staging")));
            Assert.True(File.Exists(Path.Combine(Root, "_fonts", kept + ".woff2")));
            Assert.False(File.Exists(Path.Combine(Root, "_fonts", "orphan.woff2")));
        }

        // ------------------------------------------------------------------ refusals and the menu

        [Theory]
        [InlineData("de")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("../ru")]
        [InlineData("xx")]
        public async Task A_code_the_app_does_not_know_is_refused_before_anything_is_asked(string code)
        {
            var (service, handler) = Build(_ => Missing());

            var result = await service.DownloadAsync(code);

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.UnknownCode, result.ReasonId);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task English_ships_inside_the_app_and_is_never_downloaded()
        {
            var (service, handler) = Build(_ => Missing());

            var result = await service.DownloadAsync("en");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.BuiltIn, result.ReasonId);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task A_pack_that_needs_a_newer_app_is_refused_before_a_byte_is_asked_for()
        {
            var zip = PackZip("ru", AppVersion, 7);
            var manifest = ManifestJson(Entry("ru", RuPackUrl, zip, 7, minAppVersion: "1.3.0"));
            var (service, handler) = Build(Serving(manifest, (RuPackUrl, zip)));

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.TooOld, result.ReasonId);
            Assert.DoesNotContain(RuPackUrl, handler.Requests);
        }

        [Fact]
        public async Task A_manifest_with_no_pack_for_this_language_says_so()
        {
            var zip = PackZip("ja", AppVersion, 7);
            var manifest = ManifestJson(Entry("ja", JaPackUrl, zip, 7));
            var (service, _) = Build(Serving(manifest, (JaPackUrl, zip)));

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.NoPack, result.ReasonId);
        }

        [Fact]
        public async Task A_release_page_that_cannot_be_reached_is_said_plainly()
        {
            var (service, _) = Build(_ => Missing());

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.Offline, result.ReasonId);
        }

        [Fact]
        public async Task A_pack_holding_the_wrong_language_is_not_installed()
        {
            var zip = PackZip("ru", AppVersion, 7, declaredLanguage: "ja");
            var (service, _) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.Contents, result.ReasonId);
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
        }

        [Fact]
        public async Task A_pack_with_no_catalog_in_it_is_not_installed()
        {
            var zip = PackZip("ru", AppVersion, 7, withStrings: false);
            var (service, _) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.Contents, result.ReasonId);
        }

        /// <summary>
        /// One download at a time across the whole app, and the service owns that latch rather
        /// than the bridge, because the quiet fetch runs with no page involved.
        /// </summary>
        [Fact]
        public async Task A_second_download_while_one_is_running_is_turned_away()
        {
            var zip = PackZip("ru", AppVersion, 7);
            var (service, _) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            LanguagePackResult second = null;
            service.BeforePlacing = _ => second = service.DownloadAsync("ja").GetAwaiter().GetResult();

            var first = await service.DownloadAsync("ru");

            Assert.True(first.Ok, first.ReasonId);
            Assert.NotNull(second);
            Assert.False(second.Ok);
            Assert.Equal(LanguagePackReasons.Busy, second.ReasonId);
            Assert.False(service.IsBusy);
        }

        [Fact]
        public async Task The_menu_is_answered_in_full_with_no_connection_at_all()
        {
            PlaceInstalled("ru", "1.1.0", catalog: 6);
            var (service, _) = Build(_ => Missing());

            var listing = await service.ListAsync();

            Assert.Equal(AppVersion, listing.AppVersion);
            Assert.Equal(
                new[] { "en", "ru", "ja", "zh-Hans", "zh-Hant" },
                listing.Languages.Select(l => l.Code).ToArray());

            Assert.False(listing.Manifest.Ok);
            Assert.Equal(LanguagePackReasons.Offline, listing.Manifest.ErrorId);

            var english = listing.Languages[0];
            Assert.True(english.BuiltIn);
            Assert.True(english.Installed);
            Assert.True(english.MatchesApp);

            var russian = listing.Languages[1];
            Assert.True(russian.Installed);
            Assert.Equal("1.1.0", russian.InstalledVersion);
            Assert.False(russian.MatchesApp);
            Assert.Equal("\u0420\u0443\u0441\u0441\u043a\u0438\u0439", russian.NativeName);

            var japanese = listing.Languages[2];
            Assert.False(japanese.Installed);
            Assert.False(japanese.Available);
        }

        [Fact]
        public async Task The_menu_carries_the_published_size_of_a_pack_that_is_not_here_yet()
        {
            var zip = PackZip("ja", AppVersion, 7);
            var manifest = ManifestJson(Entry("ja", JaPackUrl, zip, 7));
            var (service, _) = Build(Serving(manifest, (JaPackUrl, zip)));

            var listing = await service.ListAsync();

            Assert.True(listing.Manifest.Ok);
            Assert.False(listing.Manifest.FromCache);
            Assert.Equal("v" + AppVersion, listing.Manifest.ReleaseTag);

            var japanese = listing.Languages.Single(l => l.Code == "ja");
            Assert.True(japanese.Available);
            Assert.False(japanese.Installed);
            Assert.Equal(zip.Length, japanese.Bytes);
            Assert.Equal(7, japanese.Catalog);
        }

        /// <summary>
        /// The manifest is small and changes on release day, so a held copy answers the second
        /// question inside the window rather than costing a rate limited call.
        /// </summary>
        [Fact]
        public async Task A_held_manifest_answers_the_next_question_without_asking_again()
        {
            var zip = PackZip("ja", AppVersion, 7);
            var (service, handler) = Build(Serving(ManifestJson(Entry("ja", JaPackUrl, zip, 7)), (JaPackUrl, zip)));

            await service.ListAsync();
            var asked = handler.Requests.Count;

            var second = await service.ListAsync();

            Assert.Equal(asked, handler.Requests.Count);
            Assert.True(second.Manifest.Ok);
            Assert.True(second.Manifest.FromCache);
        }

        [Fact]
        public async Task A_held_manifest_older_than_the_window_is_asked_about_again()
        {
            var zip = PackZip("ja", AppVersion, 7);
            var (service, handler) = Build(Serving(ManifestJson(Entry("ja", JaPackUrl, zip, 7)), (JaPackUrl, zip)));

            var now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            service.UtcNow = () => now;

            await service.ListAsync();
            var asked = handler.Requests.Count;

            now = now.AddHours(7);
            await service.ListAsync();

            Assert.True(handler.Requests.Count > asked);
        }

        /// <summary>
        /// The held copy keeps the tag the release answered with, and the next question
        /// outside the window carries that tag rather than asking for the whole file again.
        /// A manifest changes on release day and on no other day, unauthenticated calls are
        /// counted per address, and this app sits on a server box for weeks, so the ordinary
        /// answer to that question is "nothing has changed" and it should cost nothing.
        /// </summary>
        [Fact]
        public async Task A_held_manifest_is_asked_about_with_the_tag_it_came_with()
        {
            const string Tag = "\"7f3c-manifest\"";

            var zip = PackZip("ja", AppVersion, 7);
            var manifest = ManifestJson(Entry("ja", JaPackUrl, zip, 7));

            var conditional = new List<string>();
            var (service, handler) = Build(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url == TagsUrl) return Json(ReleaseJson());
                if (url == ManifestUrl)
                {
                    var asked = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
                    if (asked != null)
                    {
                        conditional.Add(asked);
                        return new HttpResponseMessage(HttpStatusCode.NotModified);
                    }

                    var answer = Json(manifest);
                    answer.Headers.ETag = new EntityTagHeaderValue(Tag);
                    return answer;
                }

                return Missing();
            });

            var now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            service.UtcNow = () => now;

            var first = await service.ListAsync();

            Assert.True(first.Manifest.Ok);
            Assert.False(first.Manifest.FromCache);
            // Held under its own key, rather than happening to appear somewhere in the file.
            var held = JObject.Parse(File.ReadAllText(Path.Combine(Root, "manifest-cache.json")));
            Assert.Equal(Tag, (string)held["etag"]);

            // Past the window, so the release page is asked again, and this is the question
            // it is asked with.
            now = now.AddHours(7);
            var second = await service.ListAsync();

            Assert.Equal(new[] { Tag }, conditional);

            // "Nothing has changed" is an answer rather than a failure: the menu still carries
            // everything the manifest said the first time, and no pack was fetched to get it.
            Assert.True(second.Manifest.Ok);
            Assert.True(second.Manifest.FromCache);
            Assert.Equal(now, second.Manifest.CheckedUtc);

            var japanese = second.Languages.Single(l => l.Code == "ja");
            Assert.True(japanese.Available);
            Assert.Equal(zip.Length, japanese.Bytes);
            Assert.DoesNotContain(JaPackUrl, handler.Requests);
        }

        [Fact]
        public void What_is_on_disk_is_read_from_the_pack_itself()
        {
            PlaceInstalled("ru", "1.1.0", catalog: 6);
            PlaceInstalled("ru", AppVersion, catalog: 7);
            var (service, _) = Build(_ => Missing());

            var current = service.Installed("ru");
            Assert.NotNull(current);
            Assert.Equal(AppVersion, current.Version);
            Assert.Equal(7, current.Catalog);
            Assert.True(current.MatchesApp);

            var newest = service.InstalledAny("ru");
            Assert.Equal(AppVersion, newest.Version);

            Assert.Null(service.Installed("ja"));
            Assert.Null(service.InstalledAny("ja"));
            Assert.Null(service.Installed("nonsense"));
        }

        [Fact]
        public async Task A_reinstall_moves_the_old_folder_aside_and_leaves_nothing_behind()
        {
            var held = PlaceInstalled("ru", AppVersion, catalog: 6);
            var zip = PackZip("ru", AppVersion, 7);
            var (service, _) = Build(Serving(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), (RuPackUrl, zip)));

            var result = await service.DownloadAsync("ru");

            Assert.True(result.Ok, result.ReasonId);
            // The folder is the new pack now, not the old one with the new files dropped in.
            Assert.False(File.Exists(Path.Combine(held, "keepme.txt")));
            Assert.Equal(7, service.Installed("ru").Catalog);
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Root, "ru"))
                .Where(d => d.Contains(".old-", StringComparison.Ordinal)));
        }

        [Fact]
        public void The_language_changed_event_carries_the_code_and_the_version()
        {
            var (service, _) = Build(_ => Missing());

            LanguageChangedEventArgs seen = null;
            service.LanguageChanged += (_, args) => seen = args;

            service.NotifyLanguageChanged("ru", "1.2.0");

            Assert.NotNull(seen);
            Assert.Equal("ru", seen.Code);
            Assert.Equal("1.2.0", seen.Version);
        }

        [Fact]
        public async Task A_release_without_a_manifest_falls_back_to_the_newest_one_that_has_it()
        {
            var zip = PackZip("ru", "1.1.9", 7);

            // Nothing was cut for the running version, so the pack that is found is older than
            // the app. That is not a failure; the menu says which version it came from.
            var manifest = JsonConvert.SerializeObject(new
            {
                schema = 1,
                appVersion = "1.1.9",
                generatedUtc = "2026-08-01T00:00:00Z",
                catalog = 7,
                languages = new object[]
                {
                    new
                    {
                        code = "ru",
                        asset = "lang-ru-1.1.9.zip",
                        url = RuPackUrl,
                        bytes = zip.Length,
                        sha256 = Sha(zip),
                        catalog = 7,
                        nativeName = "ru",
                        englishName = "ru",
                        keys = 2,
                        translated = 2,
                        status = "machine",
                        minAppVersion = "1.0.0",
                        appVersion = "1.1.9",
                    },
                },
            });

            var releases = JsonConvert.SerializeObject(new object[]
            {
                new
                {
                    tag_name = "v1.3.0",
                    published_at = "2026-10-01T00:00:00Z",
                    draft = false,
                    prerelease = false,
                    assets = new[] { new { name = "ValheimBakaLoader-1.3.0-win-x64.zip", browser_download_url = "https://example.invalid/app.zip", size = 1 } },
                },
                new
                {
                    tag_name = "v1.1.9",
                    published_at = "2026-08-01T00:00:00Z",
                    draft = false,
                    prerelease = false,
                    assets = new[] { new { name = LanguagePackService.ManifestAssetName, browser_download_url = ManifestUrl, size = 512 } },
                },
            });

            var (service, handler) = Build(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url == TagsUrl) return Missing();          // nothing published under this tag
                if (url == ReleasesUrl) return Json(releases);
                if (url == ManifestUrl) return Json(manifest);
                if (url == RuPackUrl) return Bytes(zip);
                return Missing();
            });

            var result = await service.DownloadAsync("ru");

            Assert.True(result.Ok, result.ReasonId);
            Assert.Equal("1.1.9", result.AppVersion);
            Assert.Equal(Path.Combine(Root, "ru", "1.1.9"), result.Folder);

            var install = service.InstalledAny("ru");
            Assert.Equal("1.1.9", install.Version);
            Assert.False(install.MatchesApp);
            Assert.Contains(ReleasesUrl, handler.Requests);
        }
    }
}
