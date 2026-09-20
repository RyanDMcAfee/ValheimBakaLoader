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
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The five things a review of the pack service said it would take to trust it with a
    /// folder on somebody's server box, each one a way a manifest or a pack could still get
    /// something past it: a version the filesystem would spell differently, a face that is not
    /// a face, a cancel answered true that stopped nothing, and a boot sweep that took a latch
    /// it might not give back.
    /// <para>
    /// Everything here is answered out of memory through the recording handler and written
    /// into a temporary folder. Nothing reaches GitHub and nothing goes near a real install.
    /// </para>
    /// </summary>
    public class LanguagePackHardeningTests : IDisposable
    {
        private const string AppVersion = "1.2.0";

        private static readonly string TagsUrl =
            ValheimBakaLoader.Properties.Resources.UrlGithubApi + "/releases/tags/v" + AppVersion;

        private const string ManifestUrl = "https://objects.example.invalid/held/7f3c/lang-manifest.json?token=abc";
        private const string RuPackUrl = "https://objects.example.invalid/held/7f3c/ru-pack-bytes?token=def";

        private readonly string Root =
            Path.Combine(Path.GetTempPath(), "bakaloader-langhard-" + Guid.NewGuid().ToString("N"));

        private readonly MockUserPreferencesProvider Prefs = new();

        public LanguagePackHardeningTests() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ fixtures

        private static ILogger Quiet() => new LoggerConfiguration().CreateLogger();

        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static byte[] Filler(int bytes, int seed)
        {
            var buffer = new byte[bytes];
            new Random(seed).NextBytes(buffer);
            return buffer;
        }

        /// <summary>Bytes that open the way a web font opens.</summary>
        private static byte[] Face(int bytes, int seed)
        {
            var all = Filler(bytes, seed);
            Array.Copy(Encoding.ASCII.GetBytes("wOF2"), all, 4);
            return all;
        }

        private static string CatalogJson(string code, string version, int catalog) =>
            JsonConvert.SerializeObject(new
            {
                _meta = new { lang = code, language = code, appVersion = version, catalog },
                keys = new Dictionary<string, object>
                {
                    ["app.title"] = new { lore = "BakaLoader" },
                },
            });

        private static void Write(ZipArchive zip, string path, byte[] bytes)
        {
            using var stream = zip.CreateEntry(path).Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// A pack whose pack.json lists exactly the faces the test wants it to list, carrying
        /// exactly the files the test wants carried. A face with null bytes is named and not
        /// carried.
        /// </summary>
        private static byte[] PackListingFaces(
            string code,
            string version,
            int catalog,
            params (string Named, byte[] Bytes, string EntryName)[] faces)
        {
            var pack = JsonConvert.SerializeObject(new
            {
                schema = 1,
                code,
                appVersion = version,
                catalog,
                keys = 1,
                translated = 1,
                status = "machine",
                fonts = faces.Select(f => new
                {
                    file = f.Named,
                    family = "Face",
                    weight = "400",
                    style = "normal",
                    sha256 = f.Bytes == null ? "" : Sha(f.Bytes),
                }),
            });

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, "pack.json", Encoding.UTF8.GetBytes(pack));
                Write(zip, "strings.json", Encoding.UTF8.GetBytes(CatalogJson(code, version, catalog)));

                foreach (var (_, bytes, entryName) in faces)
                {
                    if (entryName != null && bytes != null) Write(zip, entryName, bytes);
                }
            }

            return buffer.ToArray();
        }

        private static byte[] PlainPack(string code, string version, int catalog)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, "pack.json", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                {
                    schema = 1,
                    code,
                    appVersion = version,
                    catalog,
                    keys = 1,
                    translated = 1,
                    status = "machine",
                })));
                Write(zip, "strings.json", Encoding.UTF8.GetBytes(CatalogJson(code, version, catalog)));
            }

            return buffer.ToArray();
        }

        private static object Entry(string code, string url, byte[] zip, int catalog, string appVersion = null) => new
        {
            code,
            asset = $"lang-{code}-{AppVersion}.zip",
            url,
            bytes = zip.Length,
            sha256 = Sha(zip),
            catalog,
            nativeName = code,
            englishName = code,
            keys = 1,
            translated = 1,
            status = "machine",
            minAppVersion = "1.0.0",
            appVersion = appVersion ?? AppVersion,
        };

        private static string ManifestJson(params object[] entries) =>
            JsonConvert.SerializeObject(new
            {
                schema = 1,
                appVersion = AppVersion,
                generatedUtc = "2026-09-18T00:00:00Z",
                catalog = 7,
                languages = entries,
            });

        private static string ReleaseJson() =>
            JsonConvert.SerializeObject(new
            {
                tag_name = "v" + AppVersion,
                html_url = "https://example.invalid/releases/tag/v" + AppVersion,
                published_at = "2026-09-18T00:00:00Z",
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

        private (LanguagePackService Service, RecordingHttpHandler Handler) Build(string manifest, byte[] pack)
        {
            var provider = new RecordingHttpClientProvider(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url == TagsUrl) return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(ReleaseJson(), Encoding.UTF8, "application/json") };
                if (url == ManifestUrl) return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(manifest, Encoding.UTF8, "application/json") };
                if (url == RuPackUrl) return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(pack) };

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var service = new LanguagePackService(
                new GitHubClient(new RestClientContext(Quiet(), provider)),
                provider,
                Prefs,
                Mock.Of<IAnalyticsService>(),
                Mock.Of<IApplicationLogger>())
            {
                RootFolder = Root,
                AppVersion = AppVersion,
            };

            return (service, provider.Handler);
        }

        private void AssertStagingIsEmpty()
        {
            var staging = Path.Combine(Root, ".staging");
            if (!Directory.Exists(staging)) return;
            Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
        }

        // ------------------------------------------------------------------ 1. the version, spelled

        /// <summary>
        /// Windows drops a trailing dot off a name without saying so, so a manifest that
        /// publishes 1.2.0. would install into the folder called 1.2.0 while every answer the
        /// service gives still says 1.2.0. That is a pack that installs and can never be
        /// found again, and a pack that quietly took the plain version's folder away from it.
        /// The manifest is the untrusted side of this exchange, so the version is refused
        /// rather than corrected: a name it did not publish is not a name to make up for it.
        /// </summary>
        [Theory]
        [InlineData("1.2.0.")]
        [InlineData("1.2.0..")]
        public async Task A_version_the_filesystem_would_spell_differently_installs_nothing(string version)
        {
            var zip = PlainPack("ru", version, 7);
            var (service, handler) = Build(ManifestJson(Entry("ru", RuPackUrl, zip, 7, appVersion: version)), zip);

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.Integrity, result.ReasonId);

            // Refused before a byte of the pack was asked for, which is the whole point of
            // holding the version to being a version up at the top of the pipeline.
            Assert.DoesNotContain(RuPackUrl, handler.Requests);
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", "1.2.0")));
            Assert.Null(service.InstalledAny("ru"));
            AssertStagingIsEmpty();
        }

        /// <summary>
        /// And the version that is merely unusual is still a version. Four parts is what a
        /// .NET assembly version has, so a pack cut against one has to install, and it has to
        /// install into exactly the folder the service says it did.
        /// </summary>
        [Fact]
        public async Task A_version_of_four_parts_installs_into_the_folder_the_service_reports()
        {
            const string version = "1.2.0.1";

            var zip = PlainPack("ru", version, 7);
            var (service, _) = Build(ManifestJson(Entry("ru", RuPackUrl, zip, 7, appVersion: version)), zip);

            var result = await service.DownloadAsync("ru");

            Assert.True(result.Ok, result.ReasonId);
            Assert.Equal(version, result.AppVersion);
            Assert.Equal(Path.Combine(Root, "ru", version), result.Folder);
            Assert.True(File.Exists(Path.Combine(result.Folder, "strings.json")));

            // The folder on disk is spelled the way the answer is spelled, and reading it back
            // by that spelling finds it.
            Assert.Contains(version, Directory.EnumerateDirectories(Path.Combine(Root, "ru")).Select(Path.GetFileName));
            Assert.Equal(version, service.InstalledAny("ru")?.Version);
        }

        // ------------------------------------------------------------------ 2. a face has to be a face

        /// <summary>
        /// The fonts list is read AFTER the pack has been approved, and every path in it is a
        /// file the pack is asking to have moved out of itself. A pack that lists its own
        /// strings.json as a face would have that file carried off into the shared store under
        /// a digest for a name, and the folder placed afterwards would hold no words at all:
        /// an install that reports ok and leaves a language that cannot be read.
        /// </summary>
        [Fact]
        public async Task A_pack_that_lists_its_own_strings_as_a_face_is_refused_and_keeps_them()
        {
            var strings = Encoding.UTF8.GetBytes(CatalogJson("ru", AppVersion, 7));

            var zip = PackListingFaces("ru", AppVersion, 7, ("strings.json", strings, null));
            var (service, _) = Build(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), zip);

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.Contents, result.ReasonId);

            // Nothing placed, and nothing of the pack's left in the shared store.
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
            Assert.False(Directory.Exists(Path.Combine(Root, "_fonts")) &&
                         Directory.EnumerateFiles(Path.Combine(Root, "_fonts")).Any());
            AssertStagingIsEmpty();
        }

        /// <summary>
        /// The same thing with the name put right, which is why the name alone is not the
        /// check: a pack chooses what to call a file. The bytes are what the file is, and a
        /// font opens with four bytes that say so.
        /// </summary>
        [Fact]
        public async Task A_face_whose_bytes_are_not_a_font_is_refused()
        {
            var notAFont = Encoding.UTF8.GetBytes("{\"_meta\":{\"lang\":\"ru\"},\"keys\":{}}");

            var zip = PackListingFaces("ru", AppVersion, 7, ("fonts/Face.woff2", notAFont, "fonts/Face.woff2"));
            var (service, _) = Build(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), zip);

            var result = await service.DownloadAsync("ru");

            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.Contents, result.ReasonId);
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
            AssertStagingIsEmpty();
        }

        /// <summary>
        /// And the control, so the pair cannot both pass on a service that stopped installing
        /// faces at all: a real face, carried under a font's name, still lands in the store.
        /// </summary>
        [Fact]
        public async Task A_real_face_still_installs()
        {
            var face = Face(2_048, seed: 5);

            var zip = PackListingFaces("ru", AppVersion, 7, ("fonts/Face.woff2", face, "fonts/Face.woff2"));
            var (service, _) = Build(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), zip);

            var result = await service.DownloadAsync("ru");

            Assert.True(result.Ok, result.ReasonId);
            Assert.True(File.Exists(Path.Combine(Root, "_fonts", Sha(face) + ".woff2")));

            var pack = JObject.Parse(File.ReadAllText(Path.Combine(result.Folder, "pack.json")));
            Assert.Equal("_fonts/" + Sha(face) + ".woff2", (string)pack["fonts"][0]["file"]);
        }

        // ------------------------------------------------------------------ 3. the cancel that lost the race

        /// <summary>
        /// The window this closes: a host presses Cancel in the instant between the service's
        /// last look at the token and the latch that stops offering the cancel. Cancel answered
        /// true, and the pack went into place anyway, which is the one answer a cancel button
        /// must never give. The look and the latch are one decision under the gate now, so the
        /// press either wins outright or is told false.
        /// </summary>
        [Fact]
        public async Task A_cancel_in_the_last_instant_is_honoured_rather_than_merely_answered()
        {
            var held = Path.Combine(Root, "ru", "1.1.0");
            Directory.CreateDirectory(held);
            File.WriteAllText(Path.Combine(held, "pack.json"), JsonConvert.SerializeObject(new
            {
                schema = 1,
                code = "ru",
                appVersion = "1.1.0",
                catalog = 6,
                keys = 1,
                translated = 1,
                status = "machine",
            }));
            File.WriteAllText(Path.Combine(held, "strings.json"), CatalogJson("ru", "1.1.0", 6));

            var zip = PlainPack("ru", AppVersion, 7);
            var (service, _) = Build(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), zip);

            bool? answered = null;
            service.BeforeTakingTheDoor = code => answered = service.Cancel(code);

            var result = await service.DownloadAsync("ru");

            // The press landed while the cancel was still honest, so it was taken...
            Assert.True(answered);

            // ...and taking it meant something: nothing was placed, and the pack that was
            // already here is untouched.
            Assert.True(result.Cancelled);
            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.Cancelled, result.ReasonId);
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
            Assert.Equal("1.1.0", service.InstalledAny("ru")?.Version);
            AssertStagingIsEmpty();
        }

        /// <summary>
        /// The same decision on the OTHER road into place. When the manifest says the catalogue
        /// has not moved, the pack for the new version is a copy of the one already on disk and
        /// no bytes are fetched at all, and that road used to note a cancel and copy anyway: the
        /// host was told the pack was theirs after pressing the button that says stop. It makes
        /// the same one decision the fetched road makes now, so the press either wins outright
        /// or is told false, and this drives it rather than reading it.
        /// </summary>
        [Fact]
        public async Task A_cancel_before_the_unchanged_catalogue_is_copied_stops_the_copy()
        {
            // Held for the version before, at the catalogue the manifest names. Nothing is
            // published to fetch for this run: the copy is the whole of the install.
            var held = Path.Combine(Root, "ru", "1.1.0");
            Directory.CreateDirectory(held);
            File.WriteAllText(Path.Combine(held, "pack.json"), JsonConvert.SerializeObject(new
            {
                schema = 1,
                code = "ru",
                appVersion = "1.1.0",
                catalog = 7,
                keys = 1,
                translated = 1,
                status = "machine",
            }));
            File.WriteAllText(Path.Combine(held, "strings.json"), CatalogJson("ru", "1.1.0", 7));

            var zip = PlainPack("ru", AppVersion, 7);
            var (service, handler) = Build(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), zip);

            bool? answered = null;
            service.BeforeTakingTheDoor = code => answered = service.Cancel(code);

            var result = await service.DownloadAsync("ru");

            // The press landed while the cancel was still honest.
            Assert.True(answered);

            // And taking it meant something. Before the fix this read Ok with the copy in
            // place, which is the one answer a cancel must never produce.
            Assert.True(result.Cancelled);
            Assert.False(result.Ok);
            Assert.Equal(LanguagePackReasons.Cancelled, result.ReasonId);

            // Nothing was placed for the running version, the held pack is where it was, and
            // this really was the copy road: no pack was ever asked for.
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));
            Assert.Equal("1.1.0", service.InstalledAny("ru")?.Version);
            Assert.DoesNotContain(RuPackUrl, handler.Requests);
            AssertStagingIsEmpty();
        }

        /// <summary>
        /// The other side of the same decision, one instant later: once the door is taken the
        /// answer is false, and the pack finishes. A cancel that is refused has to be refused
        /// out loud, because the row on the page reads that answer.
        /// </summary>
        [Fact]
        public async Task A_cancel_one_instant_later_is_refused_and_the_pack_lands()
        {
            var zip = PlainPack("ru", AppVersion, 7);
            var (service, _) = Build(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), zip);

            bool? answered = null;
            service.BeforePlacing = code => answered = service.Cancel(code);

            var result = await service.DownloadAsync("ru");

            Assert.False(answered);
            Assert.True(result.Ok, result.ReasonId);
            Assert.False(result.Cancelled);
            Assert.True(File.Exists(Path.Combine(Root, "ru", AppVersion, "strings.json")));
        }

        // ------------------------------------------------------------------ 4. the sweep's latch

        /// <summary>
        /// The sweep stands the fetches aside while it runs, so the latch it takes has to come
        /// back on every road out of the method, including the short one where there is no
        /// folder to sweep at all. A sweep that kept it would turn away every download for as
        /// long as the app stayed open, and on a server box that is weeks.
        /// </summary>
        [Fact]
        public async Task The_sweep_gives_the_latch_back_even_with_nothing_to_sweep()
        {
            var zip = PlainPack("ru", AppVersion, 7);
            var (service, _) = Build(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), zip);

            // The road that returns early: the whole languages folder is not there yet.
            Directory.Delete(Root, recursive: true);

            Assert.Equal(0, service.PruneOnBoot());
            Assert.False(service.IsBusy);

            // Twice, because a latch that stuck the first time would turn the second into the
            // "a pack is being fetched" road and answer zero for the wrong reason.
            Assert.Equal(0, service.PruneOnBoot());
            Assert.False(service.IsBusy);

            // And a download after the sweep is not turned away as busy.
            var result = await service.DownloadAsync("ru");
            Assert.True(result.Ok, result.ReasonId);
        }

        /// <summary>
        /// The other half of the latch, and the half nothing was asking about: a fetch that
        /// arrives WHILE the sweep runs has to be turned away.
        /// <para>
        /// The test above passes on the shape this was fixed from, and so does the one in
        /// LanguagePackServiceTests that sweeps during a download: both of them only ever ask
        /// the sweep to stand aside, and a sweep that merely CONSULTED IsBusy did stand aside,
        /// correctly, every time. What it did not do was take anything. So a download starting
        /// one line later found Current null and nobody sweeping, said yes, and went to work in
        /// the very staging folder the sweep was emptying. That is the race, it is invisible
        /// from outside the service, and the only way to stand in it is from inside the sweep.
        /// </para>
        /// <para>
        /// Mutate PruneOnBoot back to asking IsBusy without setting Sweeping and this test goes
        /// red on the Busy assertion, with the pack landing while the sweep runs; every other
        /// test in both files stays green.
        /// </para>
        /// </summary>
        [Fact]
        public async Task A_fetch_that_arrives_while_the_sweep_runs_is_turned_away()
        {
            // Something for the sweep to actually do, so it is not the empty road above.
            var stale = Path.Combine(Root, "ru", "1.0.0");
            Directory.CreateDirectory(stale);
            File.WriteAllText(Path.Combine(stale, "pack.json"), JsonConvert.SerializeObject(new
            {
                schema = 1,
                code = "ru",
                appVersion = "1.0.0",
                catalog = 3,
                keys = 1,
                translated = 1,
                status = "machine",
            }));
            File.WriteAllText(Path.Combine(stale, "strings.json"), CatalogJson("ru", "1.0.0", 3));

            var zip = PlainPack("ru", AppVersion, 7);
            var (service, handler) = Build(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), zip);

            LanguagePackResult during = null;
            service.DuringSweep = () =>
            {
                // On its own thread with no context of its own, so the pre-fix shape fails by
                // going green on the wrong answer rather than by hanging here.
                var arriving = Task.Run(() => service.DownloadAsync("ru"));
                during = arriving.Wait(TimeSpan.FromSeconds(30)) ? arriving.Result : null;
            };

            service.PruneOnBoot();

            Assert.NotNull(during);
            Assert.False(during.Ok);
            Assert.False(during.Cancelled);
            Assert.Equal(LanguagePackReasons.Busy, during.ReasonId);

            // Turned away means turned away: not one byte was asked for, and nothing was put
            // on disk for the running version while the sweep held the folders.
            Assert.DoesNotContain(RuPackUrl, handler.Requests);
            Assert.False(Directory.Exists(Path.Combine(Root, "ru", AppVersion)));

            // The latch came back, so the fetch that arrives after the sweep is served.
            Assert.False(service.IsBusy);
            service.DuringSweep = null;

            var after = await service.DownloadAsync("ru");
            Assert.True(after.Ok, after.ReasonId);
        }

        // ------------------------------------------------------------------ 5. what is on disk after an update

        /// <summary>
        /// SPEC section 9 item 13, on the service's side of the bridge. The app updated, the
        /// pack on disk was cut for the version before it, and the host's language is not
        /// English: the older pack is what answers, it says out loud that it does not match
        /// this build, the quiet fetch is decided as started, and the preference is not
        /// touched by any of it.
        /// </summary>
        [Fact]
        public void A_pack_from_the_version_before_this_one_still_answers_and_says_so()
        {
            var older = Path.Combine(Root, "ru", "1.1.9");
            Directory.CreateDirectory(older);
            File.WriteAllText(Path.Combine(older, "pack.json"), JsonConvert.SerializeObject(new
            {
                schema = 1,
                code = "ru",
                appVersion = "1.1.9",
                catalog = 6,
                keys = 900,
                translated = 880,
                status = "machine",
            }));
            File.WriteAllText(Path.Combine(older, "strings.json"), CatalogJson("ru", "1.1.9", 6));

            Prefs.LoadPreferences().Language = "ru";
            Prefs.LoadPreferences().CheckForUpdates = true;

            var zip = PlainPack("ru", AppVersion, 7);
            var (service, handler) = Build(ManifestJson(Entry("ru", RuPackUrl, zip, 7)), zip);

            var install = service.InstalledAny("ru");

            Assert.NotNull(install);
            Assert.Equal("1.1.9", install.Version);
            Assert.False(install.MatchesApp);
            Assert.Null(service.Installed("ru"));

            Assert.Equal(
                LanguageQuietFetch.Started,
                service.QuietFetchDecision(Prefs.LoadPreferences().CheckForUpdates, "ru", AppVersion));

            // Deciding asks nobody anything, and the preference is still the host's.
            Assert.Empty(handler.Requests);
            Assert.Equal("ru", Prefs.LoadPreferences().Language);
        }
    }
}
