using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The rule that a remote address is read and never built, kept as a gate on the source
    /// rather than on one call.
    /// <para>
    /// This is the Hexium lesson generalised. The mod installer uses the address the index
    /// gave it and refuses anything else, because an address the app assembles out of a code
    /// and a version is a silent 404 the moment one side spells a version "v1.2.0" and the
    /// other spells it "1.2.0". A host reads that 404 as "that language does not exist".
    /// So: addresses come from a release asset list or from a manifest, and nothing under
    /// Tools may interpolate or concatenate one.
    /// </para>
    /// </summary>
    public class LanguagePackSourceGateTests
    {
        private static IReadOnlyDictionary<string, string> ToolsSource()
        {
            var root = Path.Combine(AppSourceTree.RepoRoot(), "ValheimBakaLoader", "Tools");
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                map[Path.GetRelativePath(root, file).Replace('\\', '/')] = AppSourceTree.Lf(File.ReadAllText(file));
            }

            return map;
        }

        /// <summary>
        /// Three shapes, because there are three ways to build a string: an interpolated
        /// literal, a literal on the left of a concatenation, and a literal on the right.
        /// </summary>
        private static IEnumerable<string> Composers(string needle) => new[]
        {
            "\\$@?\"[^\"\\n]*" + needle,
            "\"[^\"\\n]*" + needle + "[^\"\\n]*\"\\s*\\+",
            "\\+\\s*@?\"[^\"\\n]*" + needle,
        };

        private static List<string> Offenders(string needle)
        {
            var found = new List<string>();

            foreach (var (name, source) in ToolsSource())
            {
                foreach (var pattern in Composers(needle))
                {
                    foreach (Match match in Regex.Matches(source, pattern))
                    {
                        var line = source.Take(match.Index).Count(c => c == '\n') + 1;
                        found.Add($"{name}:{line} {match.Value.Trim()}");
                    }
                }
            }

            return found;
        }

        [Fact]
        public void Nothing_under_tools_builds_a_release_download_address()
        {
            var offenders = Offenders("releases/download");

            Assert.True(
                offenders.Count == 0,
                "a download address has to come from a release asset list or a manifest, never from " +
                "a string that was put together. Found: " + string.Join("; ", offenders));
        }

        [Fact]
        public void Nothing_under_tools_builds_a_language_pack_file_name()
        {
            var offenders = Offenders("lang-");

            Assert.True(
                offenders.Count == 0,
                "a pack file name is looked up in the release's asset list by name, never composed " +
                "from a code and a version. Found: " + string.Join("; ", offenders));
        }

        /// <summary>
        /// The one name the app does spell out is the manifest's, because that is what it
        /// looks an asset up BY. It is a plain constant with nothing interpolated into it.
        /// </summary>
        [Fact]
        public void The_manifest_is_found_by_its_published_name()
        {
            var source = ToolsSource()["LanguagePackService.cs"];

            Assert.Contains("public const string ManifestAssetName = \"lang-manifest.json\";", source);
            Assert.Contains("release.Asset(ManifestAssetName)", source);
            Assert.Equal("lang-manifest.json", ValheimBakaLoader.Tools.LanguagePackService.ManifestAssetName);
        }

        /// <summary>The address the bytes come from is the one the manifest entry carries.</summary>
        [Fact]
        public void The_pack_is_fetched_at_the_address_the_manifest_carries()
        {
            var source = ToolsSource()["LanguagePackService.cs"];

            Assert.Contains("entry.Url", source);
            Assert.DoesNotContain("Resources.UrlGithubApi", source);
        }

        /// <summary>
        /// Progress that can reorder is a defect in this codebase, and the reasoning is written
        /// out beside SynchronousProgress in the bridge: Progress&lt;T&gt; posts asynchronously,
        /// so a bar can see a "done" before its "updating". The service never makes one; it
        /// takes the one the caller hands it.
        /// </summary>
        [Fact]
        public void The_service_never_makes_a_progress_that_can_reorder()
        {
            foreach (var (name, source) in ToolsSource())
            {
                Assert.False(
                    source.Contains("new Progress<", StringComparison.Ordinal),
                    name + " makes a Progress<T>, which can reorder its reports. Use SynchronousProgress.");
            }
        }

        /// <summary>
        /// One service for the whole app, registered once.
        /// <para>
        /// The latch that keeps a quiet post-update fetch from colliding with a host who has
        /// just picked a language lives in the service, and so does the event every open
        /// window follows. A window is transient and there is one per profile, so registering
        /// the service the same way would hand each window its own latch and its own silence:
        /// two downloads at once, and a language that changes in one window only. It compiles,
        /// every other test passes, and nothing at all says so.
        /// </para>
        /// </summary>
        [Fact]
        public void The_service_is_registered_once_for_the_whole_app()
        {
            var program = AppSourceTree.Files()["Program.cs"];

            Assert.Contains("services.AddSingleton<ILanguagePackService, LanguagePackService>();", program);
            Assert.DoesNotContain("AddTransient<ILanguagePackService", program);
            Assert.DoesNotContain("AddScoped<ILanguagePackService", program);
        }

        /// <summary>
        /// Where the packs live is one constant in one place. A second spelling of that path
        /// is how a folder the page serves and a folder the service writes drift apart.
        /// </summary>
        [Fact]
        public void The_languages_folder_is_never_spelled_out_a_second_time()
        {
            var source = ToolsSource()["LanguagePackService.cs"];

            Assert.Contains("Properties.Resources.LanguagesFolderPath", source);
            Assert.DoesNotContain("LocalLow", source);
        }
    }
}
