using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
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
    /// The packaging script and the installer, held against each other.
    /// <para>
    /// scripts/i18n/pack_tools.py cuts the real packs at release time, and this builds a pack
    /// with the same script and installs it through the same service the app uses. The two
    /// halves then agree by construction rather than by two people reading the same spec: a
    /// change to either that the other cannot live with fails here rather than on release day.
    /// </para>
    /// <para>
    /// The script is run rather than reimplemented, for the reason the catalog gate is: a C#
    /// copy of it would be a second opinion, not a gate. A machine with no python fails this
    /// with the list of names that were tried, the same as every other script gate in the
    /// suite, because a gate that quietly stands down is the false green they all exist to
    /// avoid.
    /// </para>
    /// </summary>
    public class LanguagePackToolsTests : IDisposable
    {
        private const string AppVersion = "1.2.0";
        private const string BaseUrl = "https://objects.example.invalid/packs/7f3c";

        private static readonly string TagsUrl =
            ValheimBakaLoader.Properties.Resources.UrlGithubApi + "/releases/tags/v" + AppVersion;

        private const string ManifestUrl = "https://objects.example.invalid/packs/7f3c/lang-manifest.json?token=abc";

        private readonly string Work =
            Path.Combine(Path.GetTempPath(), "bakaloader-packtools-" + Guid.NewGuid().ToString("N"));

        private readonly string Root;

        public LanguagePackToolsTests()
        {
            Root = Path.Combine(Work, "languages");
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Work)) Directory.Delete(Work, recursive: true); } catch (Exception) { }
        }

        private static string Script() => RepoScript.At("scripts", "i18n", "pack_tools.py");

        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        /// <summary>A catalog and a face on disk, which is what the release step is handed.</summary>
        private (string Strings, string Fonts, byte[] Face) Fixture(string code)
        {
            var source = Path.Combine(Work, "source-" + code);
            var fonts = Path.Combine(source, "fonts");
            Directory.CreateDirectory(fonts);

            var strings = Path.Combine(source, "strings.json");
            File.WriteAllText(strings, JsonConvert.SerializeObject(new
            {
                _meta = new { language = code, appVersion = "0.0.0", catalog = 7 },
                keys = new Dictionary<string, object>
                {
                    ["app.title"] = new { translation = "BakaLoader" },
                    ["app.start"] = new { translation = "Start" },
                    // A key nobody has translated yet, so the counts in the entry mean something.
                    ["app.stop"] = new { translation = "" },
                },
            }, Formatting.Indented), new UTF8Encoding(false));

            // A face the service will actually take: the first four bytes of a woff2 file
            // say what it is, and the service moves a file into the shared font store only
            // when its name and its bytes both say font. A real pack cut by pack_tools
            // carries real faces, so the fixture has to as well or the two disagree here and
            // nowhere else.
            var face = new byte[6000];
            new Random(7).NextBytes(face);
            Array.Copy(Encoding.ASCII.GetBytes("wOF2"), face, 4);
            File.WriteAllBytes(Path.Combine(fonts, "Fixture.woff2"), face);
            File.WriteAllText(Path.Combine(fonts, "OFL.txt"), "SIL Open Font License");

            return (strings, fonts, face);
        }

        private JObject BuildPack(string code, string outDir)
        {
            var fixture = Fixture(code);
            var entryOut = Path.Combine(Work, "entry-" + code + ".json");

            var built = RepoScript.Run(
                RepoScript.Python(), Script(), "build-pack",
                "--code", code,
                "--strings", fixture.Strings,
                "--fonts", fixture.Fonts,
                "--app-version", AppVersion,
                "--out", outDir,
                "--status", "machine",
                "--base-url", BaseUrl,
                "--entry-out", entryOut);

            Assert.True(built.Ok, built.ToString());
            return JObject.Parse(File.ReadAllText(entryOut));
        }

        // ------------------------------------------------------------------ the script on its own

        /// <summary>
        /// The installer fetches a pack over https and refuses anything else, so a base url
        /// typed without the s would cut a whole manifest of packs that are dead on arrival,
        /// and the first anybody would hear of it is a host picking a language and being told
        /// no. The script refuses it at cutting time instead, which is the half of the pair
        /// that can still be fixed cheaply.
        /// </summary>
        [Fact]
        public void The_script_will_not_cut_a_pack_published_at_an_address_the_installer_refuses()
        {
            var fixture = Fixture("ru");
            var outDir = Path.Combine(Work, "out-plain");

            var built = RepoScript.Run(
                RepoScript.Python(), Script(), "build-pack",
                "--code", "ru",
                "--strings", fixture.Strings,
                "--fonts", fixture.Fonts,
                "--app-version", AppVersion,
                "--out", outDir,
                "--base-url", "http://objects.example.invalid/packs/7f3c");

            Assert.False(built.Ok, "a plain http base url was accepted: " + built);
            Assert.Contains("https", built.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_pack_the_script_built_reads_back_the_way_the_installer_reads_it()
        {
            var outDir = Path.Combine(Work, "out");
            var entry = BuildPack("ru", outDir);

            var zip = Path.Combine(outDir, (string)entry["asset"]);
            Assert.Equal("lang-ru-1.2.0.zip", Path.GetFileName(zip));
            Assert.True(File.Exists(zip));

            // The entry describes the file that was written, both the weight and the digest.
            var bytes = File.ReadAllBytes(zip);
            Assert.Equal(bytes.Length, (long)entry["bytes"]);
            Assert.Equal(Sha(bytes), (string)entry["sha256"]);
            Assert.Equal(BaseUrl + "/lang-ru-1.2.0.zip", (string)entry["url"]);

            // Three keys, one of them not translated yet, which is what the menu's note counts.
            Assert.Equal(3, (int)entry["keys"]);
            Assert.Equal(2, (int)entry["translated"]);
            Assert.Equal(7, (int)entry["catalog"]);
            Assert.Equal("machine", (string)entry["status"]);
            Assert.Equal(AppVersion, (string)entry["appVersion"]);

            var verified = RepoScript.Run(RepoScript.Python(), Script(), "verify-pack", "--zip", zip);
            Assert.True(verified.Ok, verified.ToString());
            Assert.Contains("TOTAL 0", verified.Output);
        }

        [Fact]
        public void The_script_notices_a_pack_that_was_tampered_with_afterwards()
        {
            var outDir = Path.Combine(Work, "out");
            var entry = BuildPack("ru", outDir);
            var zip = Path.Combine(outDir, (string)entry["asset"]);

            // Swap the face for different bytes without touching the digest the pack published.
            var rewritten = Path.Combine(Work, "tampered.zip");
            using (var source = System.IO.Compression.ZipFile.OpenRead(zip))
            using (var target = System.IO.Compression.ZipFile.Open(rewritten, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var item in source.Entries)
                {
                    using var writing = target.CreateEntry(item.FullName).Open();
                    if (item.FullName.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase))
                    {
                        var other = new byte[6000];
                        new Random(9).NextBytes(other);
                        writing.Write(other, 0, other.Length);
                        continue;
                    }

                    using var reading = item.Open();
                    reading.CopyTo(writing);
                }
            }

            var verified = RepoScript.Run(RepoScript.Python(), Script(), "verify-pack", "--zip", rewritten);

            Assert.False(verified.Ok, verified.ToString());
            Assert.Contains("does not match the digest", verified.Output);
        }

        // ------------------------------------------------------------------ the two halves together

        [Fact]
        public async Task A_pack_the_release_script_cut_installs_through_the_service()
        {
            var outDir = Path.Combine(Work, "out");
            var entry = BuildPack("ru", outDir);

            var manifestPath = Path.Combine(Work, "lang-manifest.json");
            var manifest = RepoScript.Run(
                RepoScript.Python(), Script(), "build-manifest",
                "--entry", Path.Combine(Work, "entry-ru.json"),
                "--app-version", AppVersion,
                "--base-url", BaseUrl,
                "--out", manifestPath);

            Assert.True(manifest.Ok, manifest.ToString());

            var manifestBody = File.ReadAllText(manifestPath);
            var packUrl = (string)entry["url"];
            var packBytes = File.ReadAllBytes(Path.Combine(outDir, (string)entry["asset"]));

            var release = JsonConvert.SerializeObject(new
            {
                tag_name = "v" + AppVersion,
                published_at = "2026-09-17T00:00:00Z",
                draft = false,
                prerelease = false,
                assets = new[]
                {
                    new
                    {
                        name = LanguagePackService.ManifestAssetName,
                        browser_download_url = ManifestUrl,
                        size = manifestBody.Length,
                    },
                },
            });

            var provider = new RecordingHttpClientProvider(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url == TagsUrl) return Json(release);
                if (url == ManifestUrl) return Json(manifestBody);
                if (url == packUrl) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(packBytes) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var service = new LanguagePackService(
                new GitHubClient(new RestClientContext(new LoggerConfiguration().CreateLogger(), provider)),
                provider,
                new MockUserPreferencesProvider(),
                Mock.Of<IAnalyticsService>(),
                Mock.Of<IApplicationLogger>())
            {
                RootFolder = Root,
                AppVersion = AppVersion,
            };

            var result = await service.DownloadAsync("ru");

            Assert.True(result.Ok, result.ReasonId);
            Assert.Equal(Path.Combine(Root, "ru", AppVersion), result.Folder);

            // The catalog the script packed is the one on disk, and it says which language it is.
            var catalog = JObject.Parse(File.ReadAllText(Path.Combine(result.Folder, "strings.json")));
            Assert.Equal("ru", (string)catalog["_meta"]["lang"]);
            Assert.Equal("ru", (string)catalog["_meta"]["language"]);
            Assert.Equal(AppVersion, (string)catalog["_meta"]["appVersion"]);

            // The face the script packed went into the shared store under its own digest, and
            // the installed pack points at it there rather than at a folder of its own.
            var installed = service.Installed("ru");
            Assert.NotNull(installed);
            Assert.Equal(7, installed.Catalog);
            Assert.Equal(3, installed.Keys);
            Assert.Equal(2, installed.Translated);

            var font = Assert.Single(installed.Fonts);
            Assert.StartsWith("_fonts/", font.File);
            Assert.True(File.Exists(Path.Combine(Root, font.File.Replace('/', Path.DirectorySeparatorChar))));
            Assert.False(Directory.Exists(Path.Combine(result.Folder, "fonts")));

            var pack = JObject.Parse(File.ReadAllText(Path.Combine(result.Folder, "pack.json")));
            Assert.Single(pack["licenses"]);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
