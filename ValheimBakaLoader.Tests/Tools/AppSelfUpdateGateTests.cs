using System;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The self-update reads BOTH switches on the Upkeep card. Auto-update on its own used to be
    /// enough, so a host who turned update checking off still had BakaLoader reach out to GitHub
    /// on every launch and on every restart, and replace itself with what it found: the one path
    /// that still went out after they said not to. Both callers of the self-update go through
    /// this one service, which is why the rule sits here rather than in each of them.
    /// </summary>
    public class AppSelfUpdateGateTests : BaseTest
    {
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]   // may look, but is not allowed to install
        [InlineData(false, true, false)]   // the case that used to go ahead anyway
        [InlineData(false, false, false)]
        public void A_self_update_needs_update_checking_and_auto_update_both_on(
            bool checkForUpdates, bool autoUpdateBakaLoader, bool allowed)
        {
            Assert.Equal(allowed, AppUpdateService.MaySelfUpdate(checkForUpdates, autoUpdateBakaLoader));
        }

        /// <summary>
        /// The one exception, and the shape of it. Auto-update answers "may an unattended run
        /// install what it finds", so a host standing in front of the update dialog has already
        /// answered that themselves and must not be refused by it. Update checking answers "may
        /// BakaLoader reach out at all", which is not a question any button gets to overrule.
        /// </summary>
        [Theory]
        [InlineData(true, false, true, true)]    // the click that used to be refused
        [InlineData(true, true, true, true)]
        [InlineData(false, true, true, false)]   // checking off still wins, button or no button
        [InlineData(false, false, true, false)]
        [InlineData(true, false, false, false)]  // unattended still needs both
        [InlineData(true, true, false, true)]
        public void A_host_who_asked_for_the_update_only_needs_update_checking_on(
            bool checkForUpdates, bool autoUpdateBakaLoader, bool userInitiated, bool allowed)
        {
            Assert.Equal(allowed,
                AppUpdateService.MaySelfUpdate(checkForUpdates, autoUpdateBakaLoader, userInitiated));
        }

        [Fact]
        public async Task An_asked_for_update_goes_and_looks_with_auto_update_off()
        {
            var github = new CountingGitHub();
            var service = Service(github, checkForUpdates: true, autoUpdateBakaLoader: false);

            // Nothing is staged (the stub has no release), but the gate let it through to ask,
            // which is the whole of what the dialog's button needs.
            Assert.False(await service.CheckAndStageUpdateAsync(userInitiated: true));
            Assert.Equal(1, github.Calls);
        }

        [Fact]
        public async Task An_asked_for_update_still_does_not_ask_GitHub_with_checking_off()
        {
            var github = new CountingGitHub();
            var service = Service(github, checkForUpdates: false, autoUpdateBakaLoader: true);

            Assert.False(await service.CheckAndStageUpdateAsync(userInitiated: true));
            Assert.Equal(0, github.Calls);
        }

        [Fact]
        public async Task With_update_checking_off_it_does_not_even_ask_GitHub()
        {
            var github = new CountingGitHub();
            var service = Service(github, checkForUpdates: false, autoUpdateBakaLoader: true);

            Assert.False(await service.CheckAndStageUpdateAsync());
            Assert.Equal(0, github.Calls);
        }

        [Fact]
        public async Task With_auto_update_off_it_does_not_even_ask_GitHub()
        {
            var github = new CountingGitHub();
            var service = Service(github, checkForUpdates: true, autoUpdateBakaLoader: false);

            Assert.False(await service.CheckAndStageUpdateAsync());
            Assert.Equal(0, github.Calls);
        }

        [Fact]
        public async Task With_both_on_it_goes_and_looks()
        {
            var github = new CountingGitHub();
            var service = Service(github, checkForUpdates: true, autoUpdateBakaLoader: true);

            // The stub answers with no release at all, so nothing is staged. What this proves is
            // that the gate let it through to ask in the first place.
            Assert.False(await service.CheckAndStageUpdateAsync());
            Assert.Equal(1, github.Calls);
        }

        // ------------------------------------------------------- what came of asking, not just if

        /// <summary>
        /// The switches, the answer GitHub gave, and a machine that could not reach it at all are
        /// three different endings, and the host who asked for the update reads a different
        /// sentence for each. They all used to be a plain false, so an offline app told the host
        /// there was nothing newer, which it had no way of knowing.
        /// </summary>
        [Fact]
        public async Task A_check_that_never_got_through_is_not_the_same_as_nothing_newer()
        {
            var service = Service(new ThrowingGitHub(), checkForUpdates: true, autoUpdateBakaLoader: true);

            Assert.Equal(StageOutcome.NetworkError, await service.TryStageUpdateAsync(userInitiated: true));

            // And the unattended callers still read the same plain answer they always did.
            Assert.False(await service.CheckAndStageUpdateAsync());
        }

        [Fact]
        public async Task A_repo_with_nothing_on_it_says_so_rather_than_blaming_the_connection()
        {
            var service = Service(new CountingGitHub(), checkForUpdates: true, autoUpdateBakaLoader: true);

            Assert.Equal(StageOutcome.NoRelease, await service.TryStageUpdateAsync(userInitiated: true));
            Assert.False(await service.CheckAndStageUpdateAsync());
        }

        [Fact]
        public async Task A_release_older_than_this_build_is_said_as_already_current()
        {
            var service = Service(new StubGitHub(new GitHubRelease { TagName = "0.0.1" }),
                checkForUpdates: true, autoUpdateBakaLoader: true);

            Assert.Equal(StageOutcome.AlreadyCurrent, await service.TryStageUpdateAsync(userInitiated: true));
        }

        [Fact]
        public async Task The_switches_are_their_own_ending_and_nothing_is_asked_of_GitHub()
        {
            var github = new CountingGitHub();
            var service = Service(github, checkForUpdates: false, autoUpdateBakaLoader: true);

            Assert.Equal(StageOutcome.SwitchedOff, await service.TryStageUpdateAsync(userInitiated: true));
            Assert.Equal(0, github.Calls);
        }

        // ------------------------------------------------------------------ the watchdog itself

        /// <summary>
        /// The last line of defence, and the one that does not depend on the app behaving. The
        /// watchdog waits two minutes for BakaLoader to exit and then carries on regardless, so
        /// anything that keeps the process alive used to end with the new files written over a
        /// running install and the locked exe left behind: half one version, half the other, and
        /// no relaunch. It now looks once more before it writes, and does nothing if the app is
        /// still there.
        /// </summary>
        [Fact]
        public void The_watchdog_refuses_to_write_while_the_app_is_still_running()
        {
            var script = WatchdogScript();

            Assert.Contains("if (Get-Process -Id $appPid -ErrorAction SilentlyContinue) { exit 1 }", script);

            // And it looks after the wait and before anything is written, which is the only
            // place the look is worth anything.
            var waitAt = script.IndexOf("Wait-Process", StringComparison.Ordinal);
            var lookAt = script.IndexOf("if (Get-Process -Id $appPid", StringComparison.Ordinal);
            var extractAt = script.IndexOf("Expand-Archive", StringComparison.Ordinal);
            var copyAt = script.IndexOf("robocopy", StringComparison.Ordinal);

            Assert.True(lookAt > waitAt, "the watchdog looks before it has finished waiting");
            Assert.True(extractAt > lookAt, "the watchdog unpacks the release before it looks");
            Assert.True(copyAt > lookAt, "the watchdog writes over the install before it looks");
        }

        /// <summary>
        /// The script the watchdog runs, built the way the service builds it. Private because
        /// nothing else has any business writing one, so the test reaches it rather than the
        /// service growing a seam for a test's sake.
        /// </summary>
        private static string WatchdogScript()
        {
            var build = typeof(AppUpdateService).GetMethod(
                "BuildWatchdogScript",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            Assert.NotNull(build);

            return (string)build.Invoke(null, new object[]
            {
                4242,
                @"C:\Temp\BakaLoaderUpdate\update.zip",
                @"C:\Temp\BakaLoaderUpdate\extracted",
                @"C:\Program Files\BakaLoader",
                @"C:\Program Files\BakaLoader\ValheimBakaLoader.exe",
                @"C:\Temp\BakaLoaderUpdate",
            });
        }

        [Fact]
        public void The_self_update_service_still_comes_out_of_the_container()
        {
            // It reads the preferences now, so the container has to be able to hand it a
            // provider. A constructor the container cannot satisfy is a dead app at launch,
            // not a failed feature.
            Assert.NotNull(GetService<IAppUpdateService>());
        }

        private AppUpdateService Service(IGitHubClient github, bool checkForUpdates, bool autoUpdateBakaLoader)
            => new(github, MockHttpClientProvider,
                new StubPrefs(checkForUpdates, autoUpdateBakaLoader),
                GetService<IApplicationLogger>());

        /// <summary>One release, whatever the check asks for.</summary>
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

        /// <summary>A machine with no way out: the client throws rather than answering.</summary>
        private sealed class ThrowingGitHub : IGitHubClient
        {
            public Task<GitHubRelease> GetLatestReleaseAsync()
                => throw new System.Net.Http.HttpRequestException("no such host is known");

            public Task<GitHubRelease[]> GetReleasesAsync()
                => throw new System.Net.Http.HttpRequestException("no such host is known");

            public Task<GitHubRelease> GetReleaseByTagAsync(string tag)
                => throw new System.Net.Http.HttpRequestException("no such host is known");
        }

        private sealed class CountingGitHub : IGitHubClient
        {
            public int Calls { get; private set; }

            public Task<GitHubRelease> GetLatestReleaseAsync()
            {
                Calls++;
                return Task.FromResult<GitHubRelease>(null);
            }

            public Task<GitHubRelease[]> GetReleasesAsync()
            {
                Calls++;
                return Task.FromResult<GitHubRelease[]>(null);
            }

            public Task<GitHubRelease> GetReleaseByTagAsync(string tag)
            {
                Calls++;
                return Task.FromResult<GitHubRelease>(null);
            }
        }

        private sealed class StubPrefs : IUserPreferencesProvider
        {
            private readonly UserPreferences Current;

            public StubPrefs(bool checkForUpdates, bool autoUpdateBakaLoader)
            {
                Current = UserPreferences.GetDefault();
                Current.CheckForUpdates = checkForUpdates;
                Current.AutoUpdateBakaLoader = autoUpdateBakaLoader;
            }

            public event EventHandler<UserPreferences> PreferencesSaved;

            public UserPreferences LoadPreferences() => Current;

            public void SavePreferences(UserPreferences preferences)
                => PreferencesSaved?.Invoke(this, preferences);
        }
    }
}
