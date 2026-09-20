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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using Xunit;
using Xunit.Abstractions;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The packs a release is about to publish, pushed through the real installer.
    /// <para>
    /// Every other test here builds its own fixture, which proves the two halves agree about
    /// a pack the suite wrote. It cannot prove anything about the four zips somebody cut on
    /// a Tuesday: those were cut from real catalogs and real subset faces by a chain of
    /// scripts, and the one thing that had gone wrong with them was invisible to every check
    /// inside the zip. The families were named after their own files, so the pack installed,
    /// hashed, stored and served perfectly and the page asked for families nobody published.
    /// </para>
    /// <para>
    /// So this reads the artefacts themselves. Point <c>BAKA_PACK_DIST</c> at the folder
    /// holding lang-manifest.json and the zips, and each one goes through the same service
    /// the app uses, with the same test doubles every other pack test uses, into a temporary
    /// languages folder. With the variable unset there is nothing to read and the test says
    /// so rather than asserting something it did not look at.
    /// </para>
    /// </summary>
    public class LanguagePackRealArtefactTests : IDisposable
    {
        private const string DistVariable = "BAKA_PACK_DIST";
        private const string ManifestUrl = "https://objects.example.invalid/release/lang-manifest.json?token=abc";

        private readonly ITestOutputHelper Output;

        private readonly string Work =
            Path.Combine(Path.GetTempPath(), "bakaloader-realpack-" + Guid.NewGuid().ToString("N"));

        public LanguagePackRealArtefactTests(ITestOutputHelper output)
        {
            Output = output;
            Directory.CreateDirectory(Work);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Work)) Directory.Delete(Work, recursive: true); } catch (Exception) { }
        }

        [Fact]
        public async Task Every_pack_a_release_is_about_to_publish_installs_and_draws_in_its_own_faces()
        {
            var dist = Environment.GetEnvironmentVariable(DistVariable);
            if (string.IsNullOrWhiteSpace(dist) || !Directory.Exists(dist))
            {
                Output.WriteLine(
                    DistVariable + " is not set to a folder, so there are no release artefacts to read. " +
                    "Set it to the folder holding lang-manifest.json and the pack zips to run this.");
                return;
            }

            var manifestPath = Path.Combine(dist, LanguagePackService.ManifestAssetName);
            Assert.True(File.Exists(manifestPath), manifestPath + " is not there, so that folder is not a release");

            var manifestBody = File.ReadAllText(manifestPath);
            var manifest = JObject.Parse(manifestBody);
            var appVersion = (string)manifest["appVersion"];
            var languages = (JArray)manifest["languages"];

            Assert.False(string.IsNullOrWhiteSpace(appVersion), "the manifest does not name an app version");
            Assert.NotEmpty(languages);

            var catalogKeys = Ids(AppSourceTree.Read("ValheimBakaLoader", "WebUI", "i18n", "en.json"));
            var css = AppSourceTree.Web("app.css");

            Output.WriteLine("reading " + dist);
            Output.WriteLine(languages.Count + " packs for " + appVersion + ", against " + catalogKeys + " English ids");

            foreach (var language in languages)
            {
                var code = (string)language["code"];
                var asset = (string)language["asset"];
                var url = (string)language["url"];
                var zipPath = Path.Combine(dist, asset ?? "");

                Assert.True(File.Exists(zipPath), code + ": the manifest names " + asset + " and the folder has no such file");
                Assert.False(string.IsNullOrWhiteSpace(url), code + ": the manifest publishes no address for it");

                var bytes = File.ReadAllBytes(zipPath);
                var root = Path.Combine(Work, "languages-" + code);
                Directory.CreateDirectory(root);

                var service = Service(root, appVersion, manifestBody, url, bytes);
                var result = await service.DownloadAsync(code);

                Assert.True(result.Ok, code + ": the real pack did not install (" + result.ReasonId + ")");

                // The catalog holds the whole product, id for id. A pack short of the English
                // catalog is a pack that leaves lines in English without saying so.
                var strings = Path.Combine(result.Folder, "strings.json");
                Assert.True(File.Exists(strings), code + ": nothing was placed");
                Assert.Equal(catalogKeys, Ids(File.ReadAllText(strings)));

                var install = service.Installed(code);
                Assert.NotNull(install);
                Assert.NotEmpty(install.Fonts);

                var asked = Stacks(css, code);
                Assert.NotEmpty(asked);

                foreach (var font in install.Fonts)
                {
                    // Every face handed to the page has bytes behind it. The page writes one
                    // @font-face per row and an address that answers 404 looks exactly like a
                    // font that failed to load.
                    Assert.False(string.IsNullOrWhiteSpace(font.File), code + ": a face names no file");
                    var stored = Path.Combine(root, font.File.Replace('/', Path.DirectorySeparatorChar));
                    Assert.True(File.Exists(stored), code + ": " + font.Family + " names " + font.File + ", which is not in the store");

                    // And a family the stacks never name is a face nobody will ever draw.
                    Assert.False(string.IsNullOrWhiteSpace(font.Family), code + ": a face publishes no family");
                    Assert.True(
                        asked.Contains(font.Family),
                        code + ": the pack publishes '" + font.Family + "' and no stack that applies to " + code +
                        " asks for it. The stacks ask for: " + string.Join(", ", asked.OrderBy(f => f)));
                }

                // The CJK stacks put a Marks face in front of Inter so a full width ellipsis is
                // drawn in the script it belongs to. Without the range it would cover the whole
                // script and every Latin word in the product would change texture.
                if (code == "ja" || code == "zh-Hans" || code == "zh-Hant")
                {
                    var marks = install.Fonts.FirstOrDefault(
                        f => (f.Family ?? "").EndsWith(" Marks", StringComparison.Ordinal));

                    Assert.True(marks != null, code + ": the pack carries no Marks face, so the full width marks fall to Inter");
                    Assert.False(
                        string.IsNullOrWhiteSpace(marks.UnicodeRange),
                        code + ": the Marks face publishes no range, so it would cover the whole script");
                }

                Output.WriteLine(
                    "ok   " + code.PadRight(8) + bytes.Length.ToString("N0").PadLeft(10) + " bytes   " +
                    install.Fonts.Count + " faces   " +
                    string.Join(", ", install.Fonts.Select(f => f.Family)));
            }
        }

        /// <summary>How many ids a catalog holds.</summary>
        private static int Ids(string json)
        {
            var keys = JObject.Parse(json)["keys"] as JObject;
            return keys?.Count ?? 0;
        }

        /// <summary>
        /// Every family the stylesheet asks for under one language: the block for that
        /// language, plus the root stacks for the variables that block does not override, with
        /// var() followed so --serif-small reads as the stack it points at.
        /// </summary>
        private static HashSet<string> Stacks(string css, string code)
        {
            var names = new[] { "--serif", "--serif-small", "--sans", "--mono" };
            var text = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var selector in new[] { ":root", "html[data-lang=\"" + code + "\"]" })
            {
                foreach (System.Text.RegularExpressions.Match block in Regex.Matches(
                    text, @"(?:^|[};])\s*" + Regex.Escape(selector) + @"\s*\{([^{}]*)\}", RegexOptions.Singleline))
                {
                    foreach (var piece in block.Groups[1].Value.Split(';'))
                    {
                        var at = piece.IndexOf(':');
                        if (at <= 0) continue;

                        var name = piece.Substring(0, at).Trim();
                        if (names.Contains(name)) values[name] = piece.Substring(at + 1).Trim();
                    }
                }
            }

            var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                foreach (var family in Families(values, name, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                {
                    asked.Add(family);
                }
            }

            return asked;
        }

        private static IEnumerable<string> Families(Dictionary<string, string> values, string name, HashSet<string> seen)
        {
            if (!seen.Add(name) || !values.TryGetValue(name, out var stack)) return Array.Empty<string>();

            var reference = Regex.Match(stack.Trim(), @"^var\(\s*(--[A-Za-z0-9_-]+)\s*\)$");
            if (reference.Success) return Families(values, reference.Groups[1].Value, seen);

            return stack.Split(',')
                .Select(piece => piece.Trim().Trim('\'', '"').Trim())
                .Where(piece => piece.Length > 0);
        }

        /// <summary>
        /// The service the app uses, with the release it is about to publish in front of it:
        /// the manifest at an address only the release knows, and the real zip bytes at the
        /// address the manifest published.
        /// </summary>
        private static LanguagePackService Service(
            string root, string appVersion, string manifestBody, string packUrl, byte[] packBytes)
        {
            var tags = ValheimBakaLoader.Properties.Resources.UrlGithubApi + "/releases/tags/v" + appVersion;

            var release = JsonConvert.SerializeObject(new
            {
                tag_name = "v" + appVersion,
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
                if (url == tags) return Json(release);
                if (url == ManifestUrl) return Json(manifestBody);
                if (url == packUrl) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(packBytes) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            return new LanguagePackService(
                new GitHubClient(new RestClientContext(new LoggerConfiguration().CreateLogger(), provider)),
                provider,
                new MockUserPreferencesProvider(),
                Mock.Of<IAnalyticsService>(),
                Mock.Of<IApplicationLogger>())
            {
                RootFolder = root,
                AppVersion = appVersion,
            };
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
