using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Which asset of a release the self-updater downloads. 1.2.0 shipped four language packs
    /// beside the app, GitHub lists assets by name, and "the first .zip" was lang-ja, so every
    /// host on 1.2.0 was told its update contained no app.
    /// <para>
    /// 1.2.6 adds a second product to the same release, BakaLoaderUncheat, which is a plugin
    /// for a player's own game client. It is a zip and it sorts before everything, so the rule
    /// that answers these tests is now the app's own NAME SHAPE rather than "anything that is
    /// not a language pack": ValheimBakaLoader- at the front, -win-x64.zip at the back. A
    /// release under any version string still matches; nothing else this project publishes can.
    /// </para>
    /// </summary>
    public class AppUpdateAssetChoiceTests
    {
        private static GitHubReleaseAsset Asset(string name) => new GitHubReleaseAsset
        {
            Name = name,
            BrowserDownloadUrl = "https://github.com/x/y/releases/download/v1.2.1/" + name,
        };

        private static GitHubRelease Release(string tag, params string[] names) => new GitHubRelease
        {
            TagName = tag,
            Assets = names.Select(Asset).ToArray(),
        };

        // The order GitHub really answered for v1.2.1: by name, language packs first.
        private static readonly string[] AsPublished =
        {
            "lang-ja-1.2.1.zip", "lang-manifest.json", "lang-ru-1.2.1.zip",
            "lang-zh-hans-1.2.1.zip", "lang-zh-hant-1.2.1.zip", "ValheimBakaLoader-1.2.1-win-x64.zip",
        };

        [Fact]
        public void The_app_zip_is_chosen_when_language_packs_sort_before_it()
        {
            var chosen = AppUpdateService.ChooseAppAsset(Release("v1.2.1", AsPublished));
            Assert.Equal("ValheimBakaLoader-1.2.1-win-x64.zip", chosen.Name);
        }

        /// <summary>
        /// 1.2.6 puts a SECOND product on the release: BakaLoaderUncheat, a plugin for a
        /// player's own game client, which has nothing to do with the app and must never be
        /// downloaded as one. It sorts before everything else by name, so it stands exactly
        /// where lang-ja stood on 1.2.0, and that one shipped.
        /// </summary>
        [Fact]
        public void The_companion_plugin_is_never_mistaken_for_the_app()
        {
            var names = new[]
            {
                "BakaLoaderUncheat-1.0.0.zip",
                "lang-ja-1.2.6.zip", "lang-manifest.json", "lang-ru-1.2.6.zip",
                "lang-zh-hans-1.2.6.zip", "lang-zh-hant-1.2.6.zip",
                "ValheimBakaLoader-1.2.6-win-x64.zip",
            };

            var chosen = AppUpdateService.ChooseAppAsset(Release("v1.2.6", names));
            Assert.Equal("ValheimBakaLoader-1.2.6-win-x64.zip", chosen.Name);
        }

        [Fact]
        public void The_app_zip_is_chosen_when_a_copy_that_sorts_first_is_also_there()
        {
            var names = new[] { "BakaLoader-1.2.1-win-x64.zip" }.Concat(AsPublished).ToArray();
            var chosen = AppUpdateService.ChooseAppAsset(Release("v1.2.1", names));
            Assert.Equal("ValheimBakaLoader-1.2.1-win-x64.zip", chosen.Name);
        }

        /// <summary>
        /// The exact name is missing and the app zip is under a fourth version part, which is
        /// a tag this project really has cut. The companion sorts first and the packs sort
        /// before the app, so every one of them is in front of the right answer.
        /// <para>
        /// This is the one that was wrong until 1.2.6: the fallback was "the first zip that is
        /// not a language pack", so the answer here was BakaLoaderUncheat-1.0.0.zip, a plugin
        /// for a player's game client, downloaded as an app update. It is the app's own name
        /// shape now.
        /// </para>
        /// </summary>
        [Fact]
        public void A_four_part_tag_still_finds_the_app_and_not_the_companion()
        {
            var names = new[]
            {
                "BakaLoaderUncheat-1.0.0.zip",
                "lang-ja-1.2.6.zip", "lang-manifest.json", "lang-ru-1.2.6.zip",
                "ValheimBakaLoader-1.2.6.1-win-x64.zip",
            };

            // The tag has four parts, so the composed name is ...-1.2.6.0-... and misses.
            var chosen = AppUpdateService.ChooseAppAsset(Release("v1.2.6.0", names));
            Assert.Equal("ValheimBakaLoader-1.2.6.1-win-x64.zip", chosen.Name);
        }

        /// <summary>
        /// And when nothing on the release is the app, the answer is nothing, which is the
        /// refusal the service already words: it logs that the release carries no
        /// ValheimBakaLoader-&lt;version&gt;-win-x64.zip and leaves the installed version alone.
        /// A zip that is neither a pack nor the app is not a substitute for one.
        /// </summary>
        [Fact]
        public void A_release_with_no_app_zip_on_it_has_nothing_to_install()
        {
            Assert.Null(AppUpdateService.ChooseAppAsset(
                Release("v1.2.6", "BakaLoaderUncheat-1.0.0.zip", "lang-ja-1.2.6.zip",
                        "lang-manifest.json", "SomethingElse-1.2.6.zip")));
        }

        [Fact]
        public void A_release_with_only_language_packs_has_no_app_to_install()
        {
            Assert.Null(AppUpdateService.ChooseAppAsset(
                Release("v1.2.1", "lang-ja-1.2.1.zip", "lang-manifest.json")));
        }

        [Fact]
        public void An_old_style_release_with_one_zip_still_works()
        {
            var chosen = AppUpdateService.ChooseAppAsset(Release("v1.1.0", "ValheimBakaLoader-1.1.0-win-x64.zip"));
            Assert.Equal("ValheimBakaLoader-1.1.0-win-x64.zip", chosen.Name);
        }

        [Fact]
        public void Nothing_to_choose_from_is_null_not_a_crash()
        {
            Assert.Null(AppUpdateService.ChooseAppAsset(null));
            Assert.Null(AppUpdateService.ChooseAppAsset(new GitHubRelease { TagName = "v1.2.1" }));
        }
    }
}
