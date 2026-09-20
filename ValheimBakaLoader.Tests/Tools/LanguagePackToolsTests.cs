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

        /// <summary>
        /// A folder of faces described the way the font builder describes them: an object
        /// about the whole folder with a "fonts" LIST inside it, one entry per face rather
        /// than one per file, and two of the entries over the same file.
        /// <para>
        /// That last part is the shape the real CJK packs need. The body face is published a
        /// second time under "&lt;family&gt; Marks" with a range of exactly the few full width
        /// marks whose code points live in the Latin blocks, so the stack can put that face in
        /// front of Inter without shipping the bytes twice.
        /// </para>
        /// </summary>
        private (string Strings, string Fonts, byte[] Body, byte[] Display) Listed(
            string code, string folderName, bool describe = true, object[] entries = null)
        {
            var source = Path.Combine(Work, folderName);
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
                    ["app.stop"] = new { translation = "" },
                },
            }, Formatting.Indented), new UTF8Encoding(false));

            var body = Face(6000, seed: 11);
            var display = Face(4000, seed: 12);
            File.WriteAllBytes(Path.Combine(fonts, "Body.woff2"), body);
            File.WriteAllBytes(Path.Combine(fonts, "Display.woff2"), display);
            File.WriteAllText(Path.Combine(fonts, "OFL.txt"), "SIL Open Font License");

            if (describe)
            {
                File.WriteAllText(Path.Combine(fonts, "fonts.json"), JsonConvert.SerializeObject(new
                {
                    code,
                    builtWith = new { catalog = "strings.json" },
                    fonts = entries ?? new object[]
                    {
                        new
                        {
                            file = "fonts/Body.woff2",
                            family = "Inter",
                            weight = "400 600",
                            style = "normal",
                            unicodeRange = "U+0301,U+0400-045F",
                            sha256 = Sha(body),
                            role = "body",
                        },
                        new
                        {
                            file = "fonts/Body.woff2",
                            family = "JetBrains Mono",
                            weight = "400 700",
                            style = "normal",
                            unicodeRange = "U+0301,U+0400-045F",
                            sha256 = Sha(body),
                            role = "mono",
                        },
                        new
                        {
                            file = "fonts/Display.woff2",
                            family = "Baka Roman",
                            weight = "400",
                            style = "normal",
                            unicodeRange = "U+0301,U+0400-045F",
                            sha256 = Sha(display),
                            role = "display",
                        },
                    },
                }, Formatting.Indented), new UTF8Encoding(false));
            }

            return (strings, fonts, body, display);
        }

        /// <summary>Filler that opens the way a woff2 opens, which is all the service reads of it.</summary>
        private static byte[] Face(int bytes, int seed)
        {
            var face = new byte[bytes];
            new Random(seed).NextBytes(face);
            Array.Copy(Encoding.ASCII.GetBytes("wOF2"), face, 4);
            return face;
        }

        /// <summary>
        /// A stylesheet shaped like the product's: four variables, a language block that
        /// overrides two of them, and app.css declaring only the Latin faces itself.
        /// </summary>
        private string Stylesheet()
        {
            var path = Path.Combine(Work, "app.css");
            File.WriteAllText(path, string.Join(Environment.NewLine, new[]
            {
                "@font-face{font-family:'Cinzel';src:url('fonts/Cinzel-latin.woff2') format('woff2');unicode-range:U+0000-00FF;}",
                "@font-face{font-family:'Inter';src:url('fonts/Inter-latin.woff2') format('woff2');unicode-range:U+0000-00FF;}",
                "@font-face{font-family:'JetBrains Mono';src:url('fonts/JetBrainsMono-latin.woff2') format('woff2');unicode-range:U+0000-00FF;}",
                "/* 'Forum' in a comment is not a stack, and must not be read as one. */",
                ":root{",
                "  --serif:'Cinzel',Georgia,'Times New Roman',serif;",
                "  --serif-small:var(--serif);",
                "  --mono:'JetBrains Mono',Consolas,'Cascadia Mono',monospace;",
                "  --sans:'Inter',system-ui,'Segoe UI',sans-serif;",
                "}",
                "html[data-lang=\"ru\"]{",
                "  --serif:'Cinzel','Baka Roman',Georgia,'Times New Roman',serif;",
                "  --serif-small:var(--serif);",
                "}",
                "html[data-lang=\"ru\"] .navitem .lbl{font-size:10px}",
            }), new UTF8Encoding(false));

            return path;
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

        // ------------------------------------------------------------------ what the folder says

        /// <summary>
        /// The builder writes a LIST of faces, and a face is an entry rather than a file. Read
        /// as an object keyed by file name, which is what the older shape is, every lookup
        /// misses: each face comes out named after its own file, at a plain 400, with no range,
        /// and the page then asks for families nothing publishes. Four packs were cut that way
        /// and every one of them downloaded, stored and served bytes that were never drawn.
        /// </summary>
        [Fact]
        public void A_face_is_an_entry_rather_than_a_file_and_two_entries_may_share_one()
        {
            var fixture = Listed("ru", "listed-ru");
            var outDir = Path.Combine(Work, "out-listed");
            var entryOut = Path.Combine(Work, "entry-listed.json");

            var built = RepoScript.Run(
                RepoScript.Python(), Script(), "build-pack",
                "--code", "ru",
                "--strings", fixture.Strings,
                "--fonts", fixture.Fonts,
                "--app-version", AppVersion,
                "--out", outDir,
                "--base-url", BaseUrl,
                "--entry-out", entryOut);

            Assert.True(built.Ok, built.ToString());

            var entry = JObject.Parse(File.ReadAllText(entryOut));
            var faces = (JArray)entry["fonts"];

            // Three entries in the order the list gave them, with the families and the ranges
            // the list published rather than the file names.
            Assert.Equal(3, faces.Count);
            Assert.Equal(new[] { "Inter", "JetBrains Mono", "Baka Roman" },
                faces.Select(f => (string)f["family"]).ToArray());
            Assert.Equal("400 600", (string)faces[0]["weight"]);
            Assert.Equal("U+0301,U+0400-045F", (string)faces[0]["unicodeRange"]);

            // Two of them are the same file, and the digest is the file's either way.
            Assert.Equal("fonts/Body.woff2", (string)faces[0]["file"]);
            Assert.Equal("fonts/Body.woff2", (string)faces[1]["file"]);
            Assert.Equal(Sha(fixture.Body), (string)faces[0]["sha256"]);
            Assert.Equal(Sha(fixture.Body), (string)faces[1]["sha256"]);

            // The bytes go in once. A zip holding the same name twice is a zip whose readers
            // disagree about which of the two they got.
            var zip = Path.Combine(outDir, (string)entry["asset"]);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
            {
                Assert.Single(archive.Entries.Where(e => e.FullName == "fonts/Body.woff2"));
                Assert.Single(archive.Entries.Where(e => e.FullName == "fonts/Display.woff2"));
            }

            var verified = RepoScript.Run(RepoScript.Python(), Script(), "verify-pack", "--zip", zip);
            Assert.True(verified.Ok, verified.ToString());

            // And what the release publishes says the same thing the pack does.
            var manifestPath = Path.Combine(Work, "lang-manifest-listed.json");
            var manifest = RepoScript.Run(
                RepoScript.Python(), Script(), "build-manifest",
                "--entry", entryOut,
                "--app-version", AppVersion,
                "--base-url", BaseUrl,
                "--out", manifestPath);

            Assert.True(manifest.Ok, manifest.ToString());
            var published = (JArray)JObject.Parse(File.ReadAllText(manifestPath))["languages"][0]["fonts"];
            Assert.Equal(faces.ToString(Formatting.None), published.ToString(Formatting.None));
        }

        /// <summary>
        /// An entry for a face that is not beside it, which is a rename nobody carried through.
        /// </summary>
        [Fact]
        public void The_script_will_not_cut_a_pack_whose_list_names_a_face_that_is_not_there()
        {
            var fixture = Listed("ru", "listed-missing", entries: new object[]
            {
                new { file = "fonts/Body.woff2", family = "Inter" },
                new { file = "fonts/Display.woff2", family = "Baka Roman" },
                new { file = "fonts/Gone.woff2", family = "Missing" },
            });

            var built = RepoScript.Run(
                RepoScript.Python(), Script(), "build-pack",
                "--code", "ru",
                "--strings", fixture.Strings,
                "--fonts", fixture.Fonts,
                "--app-version", AppVersion,
                "--out", Path.Combine(Work, "out-missing"));

            Assert.False(built.Ok, "a list naming a face that is not there was accepted: " + built);
            Assert.Contains("Gone.woff2", built.Output);
        }

        /// <summary>
        /// A face beside the list that the list does not name. The bytes were built, subset and
        /// paid for, and a pack cut over them would ship without them and say nothing.
        /// </summary>
        [Fact]
        public void The_script_will_not_cut_a_pack_that_leaves_a_face_behind()
        {
            var fixture = Listed("ru", "listed-left", entries: new object[]
            {
                new { file = "fonts/Body.woff2", family = "Inter" },
            });

            var built = RepoScript.Run(
                RepoScript.Python(), Script(), "build-pack",
                "--code", "ru",
                "--strings", fixture.Strings,
                "--fonts", fixture.Fonts,
                "--app-version", AppVersion,
                "--out", Path.Combine(Work, "out-left"));

            Assert.False(built.Ok, "a face nobody named was packed in silence: " + built);
            Assert.Contains("Display.woff2", built.Output);
        }

        /// <summary>A stated digest is a claim about the file, so it is held against the file.</summary>
        [Fact]
        public void The_script_will_not_cut_a_pack_whose_list_publishes_the_wrong_digest()
        {
            var fixture = Listed("ru", "listed-digest", entries: new object[]
            {
                new { file = "fonts/Body.woff2", family = "Inter", sha256 = new string('0', 64) },
                new { file = "fonts/Display.woff2", family = "Baka Roman" },
            });

            var built = RepoScript.Run(
                RepoScript.Python(), Script(), "build-pack",
                "--code", "ru",
                "--strings", fixture.Strings,
                "--fonts", fixture.Fonts,
                "--app-version", AppVersion,
                "--out", Path.Combine(Work, "out-digest"));

            Assert.False(built.Ok, "a digest that disagrees with the file was accepted: " + built);
            Assert.Contains("Body.woff2", built.Output);
        }

        /// <summary>The older shape, an object keyed by file name, still describes its faces.</summary>
        [Fact]
        public void A_folder_described_the_old_way_by_file_name_still_reads()
        {
            var fixture = Listed("ru", "keyed-ru", describe: false);
            File.WriteAllText(Path.Combine(fixture.Fonts, "fonts.json"), JsonConvert.SerializeObject(
                new Dictionary<string, object>
                {
                    ["Body.woff2"] = new { family = "Inter" },
                }));

            var entryOut = Path.Combine(Work, "entry-keyed.json");
            var built = RepoScript.Run(
                RepoScript.Python(), Script(), "build-pack",
                "--code", "ru",
                "--strings", fixture.Strings,
                "--fonts", fixture.Fonts,
                "--app-version", AppVersion,
                "--out", Path.Combine(Work, "out-keyed"),
                "--entry-out", entryOut);

            Assert.True(built.Ok, built.ToString());
            var faces = (JArray)JObject.Parse(File.ReadAllText(entryOut))["fonts"];

            // One face per file, the described one named and the other one falling back to its
            // own file name, which is what that shape can say and all it can say.
            Assert.Equal(2, faces.Count);
            Assert.Equal(new[] { "Inter", "Display" }, faces.Select(f => (string)f["family"]).ToArray());
        }

        // ------------------------------------------------------------------ the stylesheet's side

        /// <summary>
        /// A pack can install cleanly, hash correctly, be served correctly and draw nothing at
        /// all: the page only ever asks for the families the stylesheet names. Nothing inside
        /// the zip can see that, so verify-pack is handed the stylesheet and reads both.
        /// </summary>
        [Fact]
        public void Verify_holds_the_families_the_pack_publishes_against_the_stacks_that_apply()
        {
            var css = Stylesheet();

            var good = Listed("ru", "css-good");
            var goodOut = Path.Combine(Work, "out-css-good");
            Assert.True(RepoScript.Run(
                RepoScript.Python(), Script(), "build-pack",
                "--code", "ru", "--strings", good.Strings, "--fonts", good.Fonts,
                "--app-version", AppVersion, "--out", goodOut).Ok);

            var happy = RepoScript.Run(
                RepoScript.Python(), Script(), "verify-pack",
                "--zip", Path.Combine(goodOut, "lang-ru-1.2.0.zip"), "--css", css);

            Assert.True(happy.Ok, happy.ToString());
            Assert.Contains("TOTAL 0", happy.Output);

            // The same faces with no list beside them, which is the pack that was cut for
            // release: every family named after its own file, and every stack asking for a
            // name nobody publishes.
            var drifted = Listed("ru", "css-drifted", describe: false);
            var driftedOut = Path.Combine(Work, "out-css-drifted");
            Assert.True(RepoScript.Run(
                RepoScript.Python(), Script(), "build-pack",
                "--code", "ru", "--strings", drifted.Strings, "--fonts", drifted.Fonts,
                "--app-version", AppVersion, "--out", driftedOut).Ok);

            var unhappy = RepoScript.Run(
                RepoScript.Python(), Script(), "verify-pack",
                "--zip", Path.Combine(driftedOut, "lang-ru-1.2.0.zip"), "--css", css);

            Assert.False(unhappy.Ok, "a pack whose families no stack asks for was called clean: " + unhappy);

            // Both directions are reported: what the pack publishes and nobody asks for, and
            // what a stack asks for and nobody publishes.
            Assert.Contains("the pack publishes 'Body' and no stack that applies to ru asks for it", unhappy.Output);
            Assert.Contains("the --serif stack for ru names 'Baka Roman'", unhappy.Output);
            Assert.Contains("the --sans stack for ru carries no face over Cyrillic", unhappy.Output);

            // And the system faces the stacks fall back to on purpose are not reported.
            Assert.DoesNotContain("Georgia", unhappy.Output);
            Assert.DoesNotContain("Segoe UI", unhappy.Output);

            // A family named only in a comment is not a stack.
            Assert.DoesNotContain("Forum", unhappy.Output);
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
