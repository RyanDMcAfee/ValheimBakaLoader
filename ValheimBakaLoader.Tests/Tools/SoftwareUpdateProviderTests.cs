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

        /// <summary>
        /// The check now repeats every few hours for as long as the app is open, which on a
        /// server box is weeks. Announcing the same release on every one of those would put the
        /// standing row the host dismissed back on their screen four times a day, and dismissing
        /// it is an answer about that version. Only a version they have not been told about yet
        /// gets to ask again.
        /// </summary>
        [Fact]
        public async Task The_same_release_is_only_announced_once_however_often_it_is_found()
        {
            var announced = 0;
            var provider = Provider(new GitHubRelease
            {
                TagName = "99.0.0",
                HtmlUrl = "https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/tag/99.0.0",
            });
            provider.UpdateAvailable += (s, found) => announced++;

            await provider.CheckForUpdatesAsync(isManualCheck: true);
            await provider.CheckForUpdatesAsync(isManualCheck: true);
            await provider.CheckForUpdatesAsync(isManualCheck: true);

            Assert.Equal(1, announced);

            // Quiet is not the same as forgotten: every surface that asks still gets the answer.
            Assert.NotNull(provider.LatestAvailable);
            Assert.Equal("99.0.0", provider.LatestAvailable.Version);
        }

        /// <summary>
        /// And a genuinely newer release does ask again. The row stays dismissed until there is
        /// something new to say, not for the rest of the process.
        /// </summary>
        [Fact]
        public async Task A_newer_release_than_the_one_already_announced_is_announced_too()
        {
            var seen = new System.Collections.Generic.List<string>();
            var github = new SwappableGitHub(new GitHubRelease { TagName = "99.0.0" });
            var provider = new SoftwareUpdateProvider(github, MockUserPreferencesProvider, GetService<IApplicationLogger>());
            provider.UpdateAvailable += (s, found) => seen.Add(found.Version);

            await provider.CheckForUpdatesAsync(isManualCheck: true);
            await provider.CheckForUpdatesAsync(isManualCheck: true);

            github.Release = new GitHubRelease { TagName = "99.1.0" };
            await provider.CheckForUpdatesAsync(isManualCheck: true);
            await provider.CheckForUpdatesAsync(isManualCheck: true);

            Assert.Equal(new[] { "99.0.0", "99.1.0" }, seen);
        }

        /// <summary>
        /// A release that goes away and comes back is announced again. A re-upload leaves the
        /// release with no files on it for a minute, and the check reads that as nothing newer:
        /// the standing row is cleared at that moment, so a run that kept on remembering the
        /// version would clear the row and then never put it back for the rest of the session.
        /// </summary>
        [Fact]
        public async Task A_release_that_disappears_and_comes_back_is_announced_again()
        {
            var seen = new System.Collections.Generic.List<string>();
            var github = new SwappableGitHub(new GitHubRelease { TagName = "99.0.0" });
            var provider = new SoftwareUpdateProvider(github, MockUserPreferencesProvider, GetService<IApplicationLogger>());
            provider.UpdateAvailable += (s, found) => seen.Add(found.Version);

            await provider.CheckForUpdatesAsync(isManualCheck: true);
            Assert.Equal(new[] { "99.0.0" }, seen);

            // The release is pulled, or briefly has no files on it: a successful check that finds
            // nothing newer. The standing row goes with it.
            github.Release = null;
            await provider.CheckForUpdatesAsync(isManualCheck: true);
            Assert.Null(provider.LatestAvailable);

            github.Release = new GitHubRelease { TagName = "99.0.0" };
            await provider.CheckForUpdatesAsync(isManualCheck: true);

            Assert.Equal(new[] { "99.0.0", "99.0.0" }, seen);
            Assert.NotNull(provider.LatestAvailable);
        }

        /// <summary>
        /// And an outage is not a release going away. A check that never reached GitHub knows
        /// nothing, so it leaves both the found release and the announcement alone: a host whose
        /// connection drops for an hour must not be told about the same version all over again.
        /// </summary>
        [Fact]
        public async Task A_check_that_failed_does_not_make_the_next_one_repeat_itself()
        {
            var seen = new System.Collections.Generic.List<string>();
            var github = new FlakyGitHub(new GitHubRelease { TagName = "99.0.0" });
            var provider = new SoftwareUpdateProvider(github, MockUserPreferencesProvider, GetService<IApplicationLogger>());
            provider.UpdateAvailable += (s, found) => seen.Add(found.Version);

            await provider.CheckForUpdatesAsync(isManualCheck: true);

            github.Throws = true;
            await provider.CheckForUpdatesAsync(isManualCheck: true);
            Assert.NotNull(provider.LatestAvailable);   // the check knew nothing, so nothing changed

            github.Throws = false;
            await provider.CheckForUpdatesAsync(isManualCheck: true);

            Assert.Equal(new[] { "99.0.0" }, seen);
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

            public Task<GitHubRelease[]> GetReleasesAsync() =>
                Task.FromResult(Release == null ? System.Array.Empty<GitHubRelease>() : new[] { Release });

            public Task<GitHubRelease> GetReleaseByTagAsync(string tag) =>
                Task.FromResult(Release?.TagName == tag ? Release : null);
        }

        /// <summary>The same stub again, with a connection that comes and goes.</summary>
        private sealed class FlakyGitHub : IGitHubClient
        {
            private readonly GitHubRelease Release;

            public FlakyGitHub(GitHubRelease release) => Release = release;

            public bool Throws { get; set; }

            public Task<GitHubRelease> GetLatestReleaseAsync()
                => Throws
                    ? throw new System.Net.Http.HttpRequestException("no such host is known")
                    : Task.FromResult(Release);

            public Task<GitHubRelease[]> GetReleasesAsync()
                => Throws
                    ? throw new System.Net.Http.HttpRequestException("no such host is known")
                    : Task.FromResult(Release == null ? System.Array.Empty<GitHubRelease>() : new[] { Release });

            public Task<GitHubRelease> GetReleaseByTagAsync(string tag)
                => Throws
                    ? throw new System.Net.Http.HttpRequestException("no such host is known")
                    : Task.FromResult(Release?.TagName == tag ? Release : null);
        }

        /// <summary>The same stub, with the release swapped between checks the way GitHub does.</summary>
        private sealed class SwappableGitHub : IGitHubClient
        {
            public SwappableGitHub(GitHubRelease release) => Release = release;

            public GitHubRelease Release { get; set; }

            public Task<GitHubRelease> GetLatestReleaseAsync() => Task.FromResult(Release);

            public Task<GitHubRelease[]> GetReleasesAsync() =>
                Task.FromResult(Release == null ? System.Array.Empty<GitHubRelease>() : new[] { Release });

            public Task<GitHubRelease> GetReleaseByTagAsync(string tag) =>
                Task.FromResult(Release?.TagName == tag ? Release : null);
        }
    }
}
