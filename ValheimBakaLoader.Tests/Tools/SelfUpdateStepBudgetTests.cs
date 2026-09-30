using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Moq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The launch step that goes and fetches a new BakaLoader, and the clock it is given.
    /// <para>
    /// Every launch step now runs under a per-step clock so no step can hold the window shut,
    /// which is right. The default is 45 seconds, and the self-update step is a GitHub release
    /// call plus a four megabyte zip whose OWN budget allows a 30 second header wait and ten
    /// minutes of body. So on any link slower than about 90 KB a second the step ran over, the
    /// window opened with nothing staged, and the abandoned task carried on and armed the
    /// watchdog anyway: it was never told to stop. The watchdog then waited two minutes for a
    /// PID that was not going anywhere and gave up, so the update never installed, every launch
    /// downloaded the whole zip again, and each one left a stray hidden PowerShell process
    /// behind. Worse, the step's own docstring promises the staging happens before any window is
    /// shown, which is what makes closing the app safe: a profile may have auto started by then
    /// and the Job Object would take a live server down with it.
    /// </para>
    /// </summary>
    public class SelfUpdateStepBudgetTests : BaseTest
    {
        private static AppUpdateService Service(
            IGitHubClient gitHub = null, Func<HttpRequestMessage, HttpResponseMessage> answer = null) =>
            new(gitHub ?? Mock.Of<IGitHubClient>(),
                new RecordingHttpClientProvider(answer ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
                new MockUserPreferencesProvider(),
                Mock.Of<IApplicationLogger>());

        /// <summary>
        /// The step may not be given less time than the work inside it is allowed to take. A
        /// clock shorter than that does not bound anything: it just guarantees the step is
        /// abandoned while it is still going, every time, on the machines that need it most.
        /// </summary>
        [Fact]
        public void The_step_is_never_given_less_time_than_the_download_it_wraps()
        {
            var service = Service();

            Assert.True(service.StageBudget >= service.Budget.TotalTimeout,
                "staging is allowed longer than the budget that is meant to cover it");

            var step = SplashForm.SelfUpdateStepBudget(service.StageBudget);

            Assert.True(step >= service.StageBudget,
                "the self-update step is given " + (int)step.TotalSeconds
                + " seconds for work that is allowed " + (int)service.StageBudget.TotalSeconds);

            // And it is not the plain per-step clock, which is the whole of the defect.
            Assert.True(step > LaunchBudget.PerStep);
        }

        /// <summary>
        /// A step with no budget of its own falls back to the 45 second one. The self-update
        /// step must not: the launch road has to hand it the longer clock by name.
        /// </summary>
        [Fact]
        public void The_launch_road_hands_the_self_update_step_its_own_clock()
        {
            var splash = AppSourceTree.Read("ValheimBakaLoader", "Forms", "SplashForm.cs");

            var at = splash.IndexOf(
                "AddLaunchStep(\"Self-update check\", CheckForAppSelfUpdateAsync,", StringComparison.Ordinal);
            Assert.True(at > 0, "the self-update step is no longer queued here");

            // The budget is the next argument, on the line under it.
            Assert.Contains("SelfUpdateStepBudget(AppUpdateService.StageBudget)",
                splash.Substring(at, Math.Min(200, splash.Length - at)), StringComparison.Ordinal);
        }

        // ------------------------------------------ and staging refuses itself once a window is up

        /// <summary>
        /// The promise in the step's own docstring, which is what makes closing the app to let
        /// the watchdog write safe: the staging happens before any window is shown. A step that
        /// ran over falsifies that on its own, so the service is handed the answer and refuses.
        /// Nothing is fetched and nothing is armed.
        /// </summary>
        [Fact]
        public async Task Staging_refuses_itself_once_the_answer_says_the_moment_has_gone()
        {
            var release = new GitHubRelease
            {
                TagName = "v99.0.0",
                Assets = new[]
                {
                    // The name a release really publishes. It used to be "ValheimBakaLoader.zip",
                    // a shape this project has never cut, and it only got this far because the
                    // asset fallback was "any zip that is not a language pack". Since 1.2.6 the
                    // fallback is the app's own name shape, so a made up name is refused before
                    // the step budget is reached and this test would be asserting the wrong
                    // refusal. What it is about is the MOMENT having gone, so the release it is
                    // handed has to be one the updater would otherwise take.
                    new GitHubReleaseAsset
                    {
                        Name = "ValheimBakaLoader-99.0.0-win-x64.zip",
                        BrowserDownloadUrl = "https://example.invalid/ValheimBakaLoader-99.0.0-win-x64.zip",
                        Size = 4_060_551,
                    },
                },
            };

            var gitHub = new Mock<IGitHubClient>();
            gitHub.Setup(c => c.GetLatestReleaseAsync()).ReturnsAsync(release);

            var asked = 0;
            var http = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8]) });

            var prefs = new MockUserPreferencesProvider();
            prefs.LoadPreferences().CheckForUpdates = true;
            prefs.LoadPreferences().AutoUpdateBakaLoader = true;

            var service = new AppUpdateService(
                gitHub.Object, http, prefs, Mock.Of<IApplicationLogger>());

            var outcome = await service.TryStageUpdateAsync(stillWanted: () =>
            {
                asked++;
                return false;
            });

            Assert.Equal(StageOutcome.Held, outcome);
            Assert.Equal(1, asked);

            // Nothing was fetched, so nothing could have been unpacked over anything.
            Assert.Empty(http.Handler.Requests);
        }

        /// <summary>
        /// A caller with no answer to give is never turned away: the host standing in front of
        /// the update dialog has nothing that could have changed under them. This is the guard
        /// that must not block a legitimate input.
        /// </summary>
        [Fact]
        public async Task A_caller_that_hands_in_no_answer_is_not_held()
        {
            var gitHub = new Mock<IGitHubClient>();
            gitHub.Setup(c => c.GetLatestReleaseAsync()).ReturnsAsync((GitHubRelease)null);

            var prefs = new MockUserPreferencesProvider();
            prefs.LoadPreferences().CheckForUpdates = true;
            prefs.LoadPreferences().AutoUpdateBakaLoader = true;

            var outcome = await new AppUpdateService(
                gitHub.Object,
                new RecordingHttpClientProvider(_ => new HttpResponseMessage(HttpStatusCode.NotFound)),
                prefs,
                Mock.Of<IApplicationLogger>()).TryStageUpdateAsync();

            Assert.NotEqual(StageOutcome.Held, outcome);
        }

        /// <summary>
        /// The download is the part that takes minutes, so the answer is read again after it and
        /// before the watchdog is armed. Read off the source, because arming the watchdog for
        /// real would start a detached process that writes over an install directory.
        /// </summary>
        [Fact]
        public void The_answer_is_read_again_in_the_moment_before_the_watchdog_is_armed()
        {
            var source = AppSourceTree.Read("ValheimBakaLoader", "Tools", "AppUpdateService.cs");

            var armed = source.IndexOf("LaunchWatchdog(workDir, zipPath, installDir);", StringComparison.Ordinal);
            Assert.True(armed > 0, "the watchdog is no longer armed here");

            var asked = source.LastIndexOf("if (!Wanted())", armed, StringComparison.Ordinal);
            Assert.True(asked > 0, "nothing is asked before the watchdog is armed");

            // And it really is the last thing before it, not something left further up.
            Assert.DoesNotContain("LaunchWatchdog(", source.Substring(0, asked));
        }

        /// <summary>The launch is the caller that hands the answer in.</summary>
        [Fact]
        public void The_launch_step_is_the_caller_that_answers()
        {
            var splash = AppSourceTree.Read("ValheimBakaLoader", "Forms", "SplashForm.cs");

            Assert.Contains("CheckAndStageUpdateAsync(stillWanted: () => !WindowsOpened)",
                splash, StringComparison.Ordinal);

            // And the flag is set before the staged flag is read, because a step that ran over
            // is still running while those two lines execute.
            var set = splash.IndexOf("WindowsOpened = true;", StringComparison.Ordinal);
            var read = splash.IndexOf("if (AppUpdateStaged)", StringComparison.Ordinal);
            Assert.True(set > 0 && read > set, "the windows flag is set after the staged flag is read");
        }
    }
}
