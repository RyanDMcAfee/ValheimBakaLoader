using System;
using System.IO;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// Serving a language pack to the page: the route it travels, and where it may travel from.
    /// <para>
    /// An @font-face src cannot read a local file, so a pack's fonts need a URL. Until 1.2.1 they
    /// had one on the page's OWN origin, answered by hand out of the languages folder, and that
    /// design cannot work: a host registered with SetVirtualHostNameToFolderMapping is claimed
    /// whole by the folder mapper and never raises WebResourceRequested, so the filter on
    /// https://app.baka/lang/* was attached and never called once. The install worked and the
    /// read-back was dead, which is why every language answered "could not be downloaded".
    /// </para>
    /// <para>
    /// The packs have a host of their own now, mapped onto the languages folder, which puts them
    /// on a second origin: every pack fetch is cross-origin and a font fetch is always a CORS
    /// request, so Allow is the only access kind that answers both the catalog and the faces.
    /// These tests pin the route. What the mapper will and will not serve out of that folder is
    /// the engine's own boundary rather than a function of ours, and it is exercised where it
    /// can actually be observed: LanguageRouteRealEngineTests drives a real WebView2.
    /// </para>
    /// </summary>
    public class LanguagePackServingTests
    {
        private static string BlendWindowSource() => AppSourceTree.Files()["BlendWindow.cs"];

        // ------------------------------------------------------------------ A. the route

        /// <summary>
        /// The packs are served from a host of their OWN, not from the page's origin. The whole
        /// of the 1.2.0 failure is in this one line: a path under a folder-mapped host can never
        /// be answered by hand, so a pack address on app.baka is an address nothing serves.
        /// </summary>
        [Fact]
        public void The_packs_are_served_from_their_own_virtual_host()
        {
            var source = BlendWindowSource();

            Assert.Contains("LangVirtualHost = \"lang.baka\";", source, StringComparison.Ordinal);
            Assert.Contains(
                "LanguageUrlPrefix = \"https://\" + LangVirtualHost + \"/\";", source, StringComparison.Ordinal);

            // And nothing builds a pack address on the page's own host any more.
            Assert.DoesNotContain("VirtualHost + LanguagePathPrefix", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// Allow, and not either of the other two kinds. Deny and DenyCors were both measured in
        /// the real engine and both fail the catalog AND the fonts, because the page and the
        /// packs are on different origins from here on.
        /// </summary>
        [Fact]
        public void The_languages_folder_is_mapped_with_the_kind_that_answers_a_cross_origin_fetch()
        {
            Assert.Contains(
                "LangVirtualHost, LanguagesDir, CoreWebView2HostResourceAccessKind.Allow);",
                BlendWindowSource(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Order, twice over. The folder has to be resolved (and created) before it is mapped,
        /// because mapping a folder that is not there throws and that throw closes the window on
        /// a host who has never downloaded a pack; and the mapping has to be in place before the
        /// page is navigated to, because the page asks for its catalog on the first paint.
        /// </summary>
        [Fact]
        public void The_mapping_sits_after_the_folder_is_resolved_and_before_the_page_loads()
        {
            var source = BlendWindowSource();

            var resolve = source.IndexOf("LanguagesDir = GetLanguagesDir();", StringComparison.Ordinal);
            var mapping = source.IndexOf(
                "LangVirtualHost, LanguagesDir, CoreWebView2HostResourceAccessKind.Allow);", StringComparison.Ordinal);
            var navigate = source.IndexOf(
                "core.Navigate($\"https://{VirtualHost}/index.html\");", StringComparison.Ordinal);

            Assert.True(resolve > 0, "the languages folder is never resolved");
            Assert.True(mapping > 0, "the languages folder is never mapped");
            Assert.True(mapping > resolve, "the folder has to exist before it is mapped");
            Assert.True(navigate > mapping, "the mapping has to be in place before the page loads");
        }

        /// <summary>
        /// A mapping that throws must not close the window. GetLanguagesDir creates the folder,
        /// so this should never fire, but "should never fire" is what the whole of 1.2.0's
        /// language route was: the window coming up is worth more than the language route.
        /// </summary>
        [Fact]
        public void A_languages_folder_that_cannot_be_served_still_lets_the_window_come_up()
        {
            var source = BlendWindowSource();
            var mapping = source.IndexOf(
                "LangVirtualHost, LanguagesDir, CoreWebView2HostResourceAccessKind.Allow);", StringComparison.Ordinal);
            Assert.True(mapping > 0);

            // The try opens before the folder is resolved and the catch is between the mapping
            // and the navigation, so both halves are inside it.
            var tryStart = source.LastIndexOf("try", mapping, StringComparison.Ordinal);
            var catchStart = source.IndexOf("catch (Exception ex)", mapping, StringComparison.Ordinal);
            var resolve = source.IndexOf("LanguagesDir = GetLanguagesDir();", StringComparison.Ordinal);

            Assert.True(tryStart > 0 && tryStart < resolve, "resolving the folder is not guarded");
            Assert.True(catchStart > mapping, "mapping the folder is not guarded");
            Assert.Contains(
                "installed language packs will not load", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE GATE THIS FILE EXISTS FOR. A folder-mapped host never raises
        /// WebResourceRequested, so a handler on one is dead code that reads like a feature.
        /// Nothing in the window may register one again.
        /// </summary>
        [Fact]
        public void Nothing_answers_a_request_by_hand_on_a_folder_mapped_host()
        {
            var source = BlendWindowSource();

            Assert.DoesNotContain("AddWebResourceRequestedFilter", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WebResourceRequested +=", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CreateWebResourceResponse", source, StringComparison.Ordinal);
            Assert.DoesNotContain("OnLanguageResourceRequested", source, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ B. the addresses

        /// <summary>
        /// The two addresses the page really builds, taken off the constant the bridge builds
        /// them from. Both have to come out on the mapped host with no leftover path segment:
        /// a stray /lang/ would name a folder inside the languages folder that is not there.
        /// </summary>
        [Fact]
        public void The_two_addresses_the_page_builds_come_out_on_the_mapped_host()
        {
            const string code = "ja";
            const string version = "1.2.1";
            const string sha = "8f14e45fceea167a5a36dedd4bea2543c9c1ff0f0c6d1e6d97cb5d0bf1234567";

            var strings = BlendWindow.LanguageUrlPrefix + code + "/" + version + "/strings.json";
            var font = BlendWindow.LanguageUrlPrefix + "_fonts/" + sha + ".woff2";

            Assert.Equal("https://lang.baka/ja/1.2.1/strings.json", strings);
            Assert.Equal("https://lang.baka/_fonts/" + sha + ".woff2", font);
            Assert.DoesNotContain("/lang/", strings, StringComparison.Ordinal);
            Assert.DoesNotContain("/lang/", font, StringComparison.Ordinal);
        }

        /// <summary>
        /// And the bridge builds them from that constant rather than from a literal of its own,
        /// which is what makes the assertion above cover the real addresses.
        /// </summary>
        [Fact]
        public void The_bridge_builds_both_addresses_off_the_one_constant()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];

            Assert.Contains(
                "LanguageUrlPrefix + code + \"/\" + version + \"/strings.json\"", bridge, StringComparison.Ordinal);
            Assert.Contains(
                "url = LanguageUrlPrefix + font.File.Replace('\\\\', '/').TrimStart('/')",
                bridge, StringComparison.Ordinal);
            Assert.DoesNotContain("https://app.baka/lang/", bridge, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ C. where it reads from

        /// <summary>
        /// The folder sits beside userprefs, not in the install folder: the install folder
        /// may be read-only, and a manual re-extract carries nothing across.
        /// <para>
        /// It is load-bearing in a second way now. The page can read everything under the folder
        /// that is mapped, so what is beside it rather than inside it is what stays out of
        /// reach: userprefs.json, the logs and the caches are all siblings.
        /// </para>
        /// </summary>
        [Fact]
        public void The_languages_folder_sits_beside_the_preferences_file()
        {
            var prefs = Path.GetDirectoryName(
                Environment.ExpandEnvironmentVariables(ValheimBakaLoader.Properties.Resources.UserPrefsFilePathV2));
            var languages = Environment.ExpandEnvironmentVariables(
                ValheimBakaLoader.Properties.Resources.LanguagesFolderPath);

            Assert.Equal(prefs, Path.GetDirectoryName(languages));
            Assert.Equal("languages", Path.GetFileName(languages));
        }
    }
}
