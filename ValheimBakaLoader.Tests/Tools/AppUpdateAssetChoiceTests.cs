using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Which asset of a release the self-updater downloads. 1.2.0 shipped four language packs
    /// beside the app, GitHub lists assets by name, and "the first .zip" was lang-ja, so every
    /// host on 1.2.0 was told its update contained no app.
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

        [Fact]
        public void The_app_zip_is_chosen_when_a_copy_that_sorts_first_is_also_there()
        {
            var names = new[] { "BakaLoader-1.2.1-win-x64.zip" }.Concat(AsPublished).ToArray();
            var chosen = AppUpdateService.ChooseAppAsset(Release("v1.2.1", names));
            Assert.Equal("ValheimBakaLoader-1.2.1-win-x64.zip", chosen.Name);
        }

        [Fact]
        public void A_language_pack_is_never_the_answer_even_when_the_app_is_named_differently()
        {
            var chosen = AppUpdateService.ChooseAppAsset(
                Release("v1.2.1", "lang-ja-1.2.1.zip", "lang-ru-1.2.1.zip", "SomethingElse-1.2.1.zip"));
            Assert.Equal("SomethingElse-1.2.1.zip", chosen.Name);
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
