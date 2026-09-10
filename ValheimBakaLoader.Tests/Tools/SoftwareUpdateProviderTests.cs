using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The launch-time "is there a newer release?" check used to find one and only write a log
    /// line, while the WebUI sat waiting on an app.updateAvailable event nothing ever raised.
    /// It now says what it found, and keeps it: the check finishes before any window is on
    /// screen, so a window has to be able to ask afterwards rather than catch the event live.
    /// </summary>
    public class SoftwareUpdateProviderTests : BaseTest
    {
        [Fact]
        public async Task A_newer_release_is_announced_and_kept()
        {
            AppUpdateAvailability announced = null;
            var provider = Provider(new GitHubRelease
            {
                TagName = "99.0.0",
                HtmlUrl = "https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/tag/99.0.0",
            });
            provider.UpdateAvailable += (s, found) => announced = found;

            await provider.CheckForUpdatesAsync(isManualCheck: true);

            Assert.NotNull(announced);
            Assert.Equal("99.0.0", announced.Version);
            Assert.Equal("https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/tag/99.0.0", announced.NotesUrl);

            // The window that opens after the check still has to be able to find this.
            Assert.NotNull(provider.LatestAvailable);
            Assert.Equal("99.0.0", provider.LatestAvailable.Version);
        }

        [Fact]
        public async Task An_older_release_announces_nothing()
        {
            var raised = false;
            var provider = Provider(new GitHubRelease { TagName = "0.0.1" });
            provider.UpdateAvailable += (s, found) => raised = true;

            await provider.CheckForUpdatesAsync(isManualCheck: true);

            Assert.False(raised);
            Assert.Null(provider.LatestAvailable);
        }

        [Fact]
        public async Task A_repo_with_no_releases_announces_nothing()
        {
            var raised = false;
            var provider = Provider(null);
            provider.UpdateAvailable += (s, found) => raised = true;

            await provider.CheckForUpdatesAsync(isManualCheck: true);

            Assert.False(raised);
            Assert.Null(provider.LatestAvailable);
        }

        [Fact]
        public void The_services_the_bridge_resolves_are_registered()
        {
            // The bridge asks the container for both of these while a window is being built,
            // so a missing registration is a dead app, not a failed feature.
            Assert.NotNull(GetService<ISoftwareUpdateProvider>());
            Assert.NotNull(GetService<IServerUpdateService>());
        }

        private SoftwareUpdateProvider Provider(GitHubRelease release)
            => new(new StubGitHub(release), MockUserPreferencesProvider, GetService<IApplicationLogger>());

        private sealed class StubGitHub : IGitHubClient
        {
            private readonly GitHubRelease Release;

            public StubGitHub(GitHubRelease release) => Release = release;

            public Task<GitHubRelease> GetLatestReleaseAsync() => Task.FromResult(Release);
        }
    }
}
