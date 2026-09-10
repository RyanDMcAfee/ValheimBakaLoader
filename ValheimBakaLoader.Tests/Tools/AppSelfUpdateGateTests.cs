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

        private sealed class CountingGitHub : IGitHubClient
        {
            public int Calls { get; private set; }

            public Task<GitHubRelease> GetLatestReleaseAsync()
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
