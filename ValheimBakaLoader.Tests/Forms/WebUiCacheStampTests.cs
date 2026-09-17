using System;
using System.IO;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// "The interface you see is the interface you installed."
    /// <para>
    /// The halls are ordinary web files behind a virtual host and the embedded browser keeps a
    /// disk cache that outlives an update. Nothing about the two addresses changes when a new
    /// build lands, so the window could come up showing the interface the update replaced, with
    /// the version line the only hint that anything was wrong. Two things stop that: every
    /// include carries the version it shipped with, and the first launch of a version that has
    /// not run here before throws away what the browser held.
    /// </para>
    /// <para>
    /// These are gates on the source, because what they guard against is somebody later writing
    /// a plain include back into the page or dropping the clear out of the startup path.
    /// </para>
    /// </summary>
    public class WebUiCacheStampTests
    {
        private static string Html() => AppSourceTree.Web("index.html");

        private static string BlendWindow() => AppSourceTree.Files()["BlendWindow.cs"];

        /// <summary>The version in the csproj, which is what the running build reports.</summary>
        private static string ShippedVersion()
        {
            var csproj = File.ReadAllText(Path.Combine(
                AppSourceTree.RepoRoot(), "ValheimBakaLoader", "ValheimBakaLoader.csproj"));

            var match = Regex.Match(csproj, @"<Version>([^<]+)</Version>");
            Assert.True(match.Success, "the app csproj has no Version");
            return match.Groups[1].Value.Trim();
        }

        // ------------------------------------------------------------------ A. the two includes

        [Fact]
        public void The_stylesheet_is_included_through_the_stamp_and_never_plainly()
        {
            var html = Html();

            Assert.Contains("BAKA_ASSET(\"app.css\")", html);
            Assert.DoesNotContain("href=\"app.css\"", html);
        }

        [Fact]
        public void The_script_is_included_through_the_stamp_and_never_plainly()
        {
            var html = Html();

            Assert.Contains("BAKA_ASSET(\"app.js\")", html);
            Assert.DoesNotContain("src=\"app.js\"", html);
        }

        /// <summary>
        /// The stamp is the version and nothing else, so two builds never share an address and
        /// the same build never invents a new one on every launch.
        /// </summary>
        [Fact]
        public void The_stamp_is_the_version_carried_as_a_query()
        {
            var html = Html();

            Assert.Contains("return name + \"?v=\" + encodeURIComponent(v)", html);
            Assert.DoesNotContain("Date.now()", html);
        }

        /// <summary>
        /// The page takes the version from the host when there is one. The bootstrap has to run
        /// before anything else on the page, so the includes it writes are the only ones.
        /// </summary>
        [Fact]
        public void The_version_comes_from_the_host_when_there_is_one()
        {
            var html = Html();

            Assert.Contains("window.BAKA_VERSION ||", html);
            Assert.True(
                html.IndexOf("window.BAKA_VERSION ||", StringComparison.Ordinal) < html.IndexOf("</head>", StringComparison.Ordinal),
                "the stamp bootstrap has to run inside the head, before anything it stamps");
        }

        /// <summary>
        /// With no host in front of it the page falls back to a constant, which is how the mock
        /// preview goes on working off a plain file server. The constant is the version this
        /// build ships, so the preview and the app agree about what they are showing.
        /// </summary>
        [Fact]
        public void With_no_host_the_page_falls_back_to_the_version_it_shipped_with()
        {
            var version = ShippedVersion();

            Assert.Contains("window.BAKA_VERSION || \"" + version + "\"", Html());
        }

        /// <summary>The version the sidebar and the status bar show is the version that shipped.</summary>
        [Fact]
        public void The_version_the_halls_show_is_the_version_that_shipped()
        {
            var version = ShippedVersion();
            var html = Html();

            Assert.Contains("id=\"sideVer\">v" + version + "<", html);
            Assert.Contains("id=\"sbVersion\">v" + version + "<", html);
        }

        /// <summary>
        /// A stamped address the host declines must not cost the host their interface: both
        /// includes keep the plain address as a fallback.
        /// </summary>
        [Fact]
        public void A_stamped_address_that_will_not_load_falls_back_to_the_plain_one()
        {
            var html = Html();

            // Three now: the stylesheet, the lookup and the app. Every stamped address
            // keeps a plain one behind it, because a cache stamp must never be the reason
            // the interface does not come up at all.
            Assert.Equal(3, CountOf(html, "onerror=\"BAKA_ASSET_PLAIN(this)\""));
            Assert.Contains("String(el.tagName === \"LINK\" ? el.href : el.src).split(\"?\")[0]", html);
        }

        // --------------------------------------------------------------- B. the host's own half

        [Fact]
        public void The_host_hands_the_page_its_version_before_the_page_runs()
        {
            var source = BlendWindow();

            Assert.Contains("AddScriptToExecuteOnDocumentCreatedAsync", source);
            Assert.Contains("window.BAKA_VERSION=", source);
        }

        [Fact]
        public void The_host_clears_the_browser_cache_on_a_version_it_has_not_run_before()
        {
            var source = BlendWindow();

            Assert.Contains("WebUiCacheStamp.ShouldClear", source);
            Assert.Contains("ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache)", source);
            Assert.Contains("WebUiCacheStamp.RememberVersion", source);
        }

        /// <summary>
        /// Both of them run while the core is up and before the page is asked for, or the first
        /// load of a new build is the one load that misses them.
        /// </summary>
        [Fact]
        public void Both_halves_run_before_the_page_is_navigated_to()
        {
            var source = BlendWindow();

            var announce = source.IndexOf("await AnnounceVersionToPageAsync(core);", StringComparison.Ordinal);
            var clear = source.IndexOf("await ClearCacheOnceForThisVersionAsync(core);", StringComparison.Ordinal);
            var navigate = source.IndexOf("core.Navigate($\"https://{VirtualHost}/index.html\");", StringComparison.Ordinal);

            Assert.True(announce > 0, "the version is never handed to the page");
            Assert.True(clear > 0, "the cache is never cleared");
            Assert.True(navigate > announce, "the version has to be set before the page is loaded");
            Assert.True(navigate > clear, "the cache has to be cleared before the page is loaded");
        }

        /// <summary>
        /// The marker lives beside userprefs.json, so a reinstall over the top keeps it. Asked
        /// of the preferences path itself rather than of a second copy of it typed out here: a
        /// literal in the test is only ever a record of where the folder used to be, and it
        /// would go on passing after somebody moved the real one.
        /// </summary>
        [Fact]
        public void The_marker_sits_beside_the_preferences_file()
        {
            var prefs = Path.GetDirectoryName(
                Environment.ExpandEnvironmentVariables(Resources.UserPrefsFilePathV2));

            Assert.Equal(prefs, Path.GetDirectoryName(WebUiCacheStamp.ExpandedMarkerPath()));
            Assert.Equal("webui-cache-version.txt", Path.GetFileName(WebUiCacheStamp.ExpandedMarkerPath()));
        }

        // --------------------------------------------------------- C. the once per version rule

        [Fact]
        public void A_version_that_has_never_run_here_clears()
            => Assert.True(WebUiCacheStamp.ShouldClear(null, "1.1.1"));

        [Fact]
        public void An_empty_marker_reads_the_same_as_no_marker()
        {
            Assert.True(WebUiCacheStamp.ShouldClear("", "1.1.1"));
            Assert.True(WebUiCacheStamp.ShouldClear("   ", "1.1.1"));
        }

        [Fact]
        public void An_update_clears()
            => Assert.True(WebUiCacheStamp.ShouldClear("1.1.0", "1.1.1"));

        /// <summary>A rollback is still a change of build, and still wants the old cache gone.</summary>
        [Fact]
        public void A_rollback_clears_too()
            => Assert.True(WebUiCacheStamp.ShouldClear("1.1.1", "1.1.0"));

        /// <summary>
        /// The same version launching again keeps what it has. Clearing every launch would throw
        /// the cache away for nothing, which is the whole point of not doing it.
        /// </summary>
        [Fact]
        public void The_same_version_launching_again_keeps_its_cache()
        {
            Assert.False(WebUiCacheStamp.ShouldClear("1.1.1", "1.1.1"));
            Assert.False(WebUiCacheStamp.ShouldClear(" 1.1.1 ", "1.1.1"));
        }

        /// <summary>
        /// A build that cannot say which version it is clears nothing: there would be no marker
        /// to write afterwards, so it would clear on every launch forever.
        /// </summary>
        [Fact]
        public void A_build_that_cannot_name_itself_clears_nothing()
        {
            Assert.False(WebUiCacheStamp.ShouldClear(null, null));
            Assert.False(WebUiCacheStamp.ShouldClear("1.1.0", ""));
            Assert.False(WebUiCacheStamp.ShouldClear("1.1.0", "   "));
        }

        /// <summary>The marker written by one launch is what the next launch reads back.</summary>
        [Fact]
        public void What_one_launch_writes_down_the_next_one_reads()
        {
            var folder = Path.Combine(Path.GetTempPath(), "baka-cachestamp-" + Guid.NewGuid().ToString("N"));
            var marker = Path.Combine(folder, "webui-cache-version.txt");

            try
            {
                Assert.Null(WebUiCacheStamp.ReadLastVersionFrom(marker));

                WebUiCacheStamp.RememberVersionIn(marker, "1.1.1");

                Assert.Equal("1.1.1", WebUiCacheStamp.ReadLastVersionFrom(marker));
                Assert.False(WebUiCacheStamp.ShouldClear(WebUiCacheStamp.ReadLastVersionFrom(marker), "1.1.1"));
                Assert.True(WebUiCacheStamp.ShouldClear(WebUiCacheStamp.ReadLastVersionFrom(marker), "1.1.2"));
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
            }
        }

        private static int CountOf(string haystack, string needle)
        {
            var n = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
