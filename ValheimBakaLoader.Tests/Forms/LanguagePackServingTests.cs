using System;
using System.IO;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// Serving a language pack to the page, and the one boundary that has to hold.
    /// <para>
    /// An @font-face src cannot read a local file, so a pack's fonts need a URL, and every
    /// font fetch is a CORS request. Serving them from the page's own origin sidesteps the
    /// CORS question entirely and puts this side in charge of what may be read: a request
    /// under https://app.baka/lang/ is answered out of the languages folder and nowhere
    /// else. That last clause is the whole of the security surface, so it lives in a plain
    /// function with no I/O in it and these tests drive it directly.
    /// </para>
    /// </summary>
    public class LanguagePackServingTests
    {
        private const string Root = @"C:\Users\Someone\AppData\LocalLow\BakaLoader\ValheimBakaLoader\languages";

        private static string Map(string path) => BlendWindow.MapLanguageResourcePath(Root, path);

        // ------------------------------------------------------------------ A. what is served

        [Theory]
        [InlineData("/lang/ja/1.2.0/fonts/NotoSansJP-jp.woff2", @"ja\1.2.0\fonts\NotoSansJP-jp.woff2")]
        [InlineData("/lang/ru/1.2.0/fonts/Forum-cyrillic.woff2", @"ru\1.2.0\fonts\Forum-cyrillic.woff2")]
        [InlineData("/lang/zh-Hans/1.2.0/strings.json", @"zh-Hans\1.2.0\strings.json")]
        [InlineData("/lang/zh-Hant/1.2.0/pack.css", @"zh-Hant\1.2.0\pack.css")]
        [InlineData("/lang/ja/1.2.0/fonts/OFL.txt", @"ja\1.2.0\fonts\OFL.txt")]
        public void A_pack_file_resolves_under_the_languages_folder(string requested, string expectedTail)
        {
            Assert.Equal(Path.Combine(Root, expectedTail), Map(requested));
        }

        /// <summary>A cache buster on the end is not part of the file name.</summary>
        [Fact]
        public void A_query_or_a_fragment_is_not_part_of_the_name()
        {
            Assert.Equal(Path.Combine(Root, @"ja\1.2.0\pack.css"), Map("/lang/ja/1.2.0/pack.css?v=1.2.0"));
            Assert.Equal(Path.Combine(Root, @"ja\1.2.0\pack.css"), Map("/lang/ja/1.2.0/pack.css#top"));
        }

        /// <summary>Percent escapes decode to ordinary names. A space in a file name is a file name.</summary>
        [Fact]
        public void An_escaped_ordinary_name_decodes_to_that_name()
        {
            Assert.Equal(Path.Combine(Root, @"ja\1.2.0\Noto Sans.woff2"), Map("/lang/ja/1.2.0/Noto%20Sans.woff2"));
        }

        // ------------------------------------------------------------------ B. what is refused

        [Theory]
        // Plain traversal, in both slash flavours.
        [InlineData("/lang/../userprefs.json")]
        [InlineData("/lang/ja/../../../userprefs.json")]
        [InlineData("/lang/..\\..\\userprefs.json")]
        [InlineData("/lang/ja/1.2.0/../../../../secrets.txt")]
        // Encoded dots: decoded after the prefix check and canonicalised after that, which
        // is the whole reason the order in the function is what it is.
        [InlineData("/lang/%2e%2e/userprefs.json")]
        [InlineData("/lang/%2E%2E/%2E%2E/userprefs.json")]
        [InlineData("/lang/ja/..%2f..%2fuserprefs.json")]
        [InlineData("/lang/ja/%2e%2e%5c%2e%2e%5cuserprefs.json")]
        // An absolute path, a drive letter, an alternate data stream.
        [InlineData("/lang/C:/Windows/win.ini")]
        [InlineData("/lang/C:\\Windows\\win.ini")]
        [InlineData("/lang/ja/pack.css:$DATA")]
        // Nothing named at all.
        [InlineData("/lang/")]
        [InlineData("/lang")]
        [InlineData("/lang///")]
        // A different prefix entirely: index.html is not served through this handler.
        [InlineData("/index.html")]
        [InlineData("/langs/ja/1.2.0/pack.css")]
        [InlineData("")]
        [InlineData(null)]
        public void Anything_that_is_not_inside_the_languages_folder_is_refused(string requested)
        {
            Assert.Null(Map(requested));
        }

        /// <summary>
        /// A sibling folder whose name merely begins the same way is outside. This is the
        /// off-by-one a plain StartsWith on the folder name gets wrong.
        /// </summary>
        [Fact]
        public void A_folder_beside_the_languages_folder_is_not_inside_it()
        {
            Assert.Null(BlendWindow.MapLanguageResourcePath(@"C:\data\languages", "/lang/../languages-backup/x.json"));
        }

        /// <summary>A double encoding decodes once, to a literal name, which names no file.</summary>
        [Fact]
        public void A_double_encoded_traversal_becomes_a_literal_name_rather_than_a_traversal()
        {
            var mapped = Map("/lang/%252e%252e/userprefs.json");

            Assert.Equal(Path.Combine(Root, "%2e%2e", "userprefs.json"), mapped);
        }

        /// <summary>
        /// A doubled slash is an empty URL segment, not a UNC share. It has to land inside
        /// the folder like any other name, where it simply is not a file, rather than being
        /// handed to the platform as a network path.
        /// </summary>
        [Theory]
        [InlineData("/lang//server/share/secret.txt")]
        [InlineData("/lang/%2F%2Fserver%2Fshare%2Fsecret.txt")]
        public void An_empty_leading_segment_never_becomes_a_network_path(string requested)
        {
            var mapped = Map(requested);

            Assert.Equal(Path.Combine(Root, @"server\share\secret.txt"), mapped);
        }

        [Fact]
        public void No_root_means_nothing_is_served()
        {
            Assert.Null(BlendWindow.MapLanguageResourcePath(null, "/lang/ja/1.2.0/pack.css"));
            Assert.Null(BlendWindow.MapLanguageResourcePath("   ", "/lang/ja/1.2.0/pack.css"));
        }

        // ------------------------------------------------------------------ C. content types

        [Theory]
        [InlineData("x.woff2", "font/woff2")]
        [InlineData("x.WOFF2", "font/woff2")]
        [InlineData("x.woff", "font/woff")]
        [InlineData("strings.json", "application/json; charset=utf-8")]
        [InlineData("pack.css", "text/css; charset=utf-8")]
        [InlineData("OFL.txt", "text/plain; charset=utf-8")]
        [InlineData("x.bin", "application/octet-stream")]
        [InlineData("x", "application/octet-stream")]
        [InlineData("", "application/octet-stream")]
        public void A_pack_file_is_served_as_what_it_is(string name, string expected)
        {
            Assert.Equal(expected, BlendWindow.LanguageContentType(name));
        }

        // ------------------------------------------------------------------ D. the wiring

        private static string BlendWindowSource() => AppSourceTree.Files()["BlendWindow.cs"];

        /// <summary>
        /// A filter added after Navigate only applies to requests made after it, and the
        /// page asks for its fonts on the first paint. So the registration has to be before
        /// the navigation, not merely somewhere in startup.
        /// </summary>
        [Fact]
        public void The_filter_is_registered_before_the_page_is_navigated_to()
        {
            var source = BlendWindowSource();

            var filter = source.IndexOf("AddWebResourceRequestedFilter(", StringComparison.Ordinal);
            var handler = source.IndexOf("core.WebResourceRequested += OnLanguageResourceRequested;", StringComparison.Ordinal);
            var navigate = source.IndexOf("core.Navigate($\"https://{VirtualHost}/index.html\");", StringComparison.Ordinal);

            Assert.True(filter > 0, "no request filter is registered for the language packs");
            Assert.True(handler > 0, "nothing answers a language pack request");
            Assert.True(navigate > filter, "the filter has to be registered before the page loads");
            Assert.True(navigate > handler, "the handler has to be attached before the page loads");
        }

        [Fact]
        public void The_packs_are_served_on_the_pages_own_origin()
        {
            Assert.Contains("LanguageUrlPrefix = \"https://\" + VirtualHost + LanguagePathPrefix;", BlendWindowSource());
            Assert.Contains("LanguagePathPrefix = \"/lang/\";", BlendWindowSource());
        }

        /// <summary>
        /// The folder sits beside userprefs, not in the install folder: the install folder
        /// may be read-only, and a manual re-extract carries nothing across.
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
