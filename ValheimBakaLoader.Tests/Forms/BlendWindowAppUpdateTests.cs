using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// What the app promises about its own new version: every surface reads one answer, and the
    /// one button that acts on it never gets there by taking a server down. Updating BakaLoader
    /// means closing BakaLoader, and the Valheim server is this app's own child process, so
    /// "update now" while a world is up would stop that world as a side effect of a decision the
    /// host thought they were making about the app.
    /// </summary>
    public class BlendWindowAppUpdateTests : BaseTest
    {
        // ---------------------------------------------------------------- the refusal

        [Fact]
        public void A_running_server_is_never_something_the_update_takes_down()
        {
            Assert.Equal("serverBusy",
                BlendWindow.SelfUpdateRefusal(checkEnabled: true, anyServerRunning: true, anyServerUpdateRunning: false));
        }

        /// <summary>
        /// The case the session registry cannot see: every profile is stopped, but steamcmd is
        /// still writing valheim_server.exe and the files beside it for one of them. Closing now
        /// abandons that rewrite half done, and a half written install cannot be undone.
        /// </summary>
        [Fact]
        public void A_stopped_profile_with_a_steam_update_running_is_still_busy()
        {
            Assert.Equal("serverBusy",
                BlendWindow.SelfUpdateRefusal(checkEnabled: true, anyServerRunning: false, anyServerUpdateRunning: true));
        }

        [Fact]
        public void Update_checking_off_is_refused_before_anything_reaches_out()
        {
            Assert.Equal("checkingOff",
                BlendWindow.SelfUpdateRefusal(checkEnabled: false, anyServerRunning: false, anyServerUpdateRunning: false));
        }

        /// <summary>
        /// The switch has the first word: a host who turned checking off must not be told the
        /// server is the problem, because turning the server off would not help them.
        /// </summary>
        [Fact]
        public void With_checking_off_the_reason_given_is_the_switch_and_not_the_server()
        {
            Assert.Equal("checkingOff",
                BlendWindow.SelfUpdateRefusal(checkEnabled: false, anyServerRunning: true, anyServerUpdateRunning: true));
        }

        [Fact]
        public void With_nothing_running_and_checking_on_it_may_go_ahead()
        {
            Assert.Null(
                BlendWindow.SelfUpdateRefusal(checkEnabled: true, anyServerRunning: false, anyServerUpdateRunning: false));
        }

        /// <summary>
        /// The button goes straight out to GitHub, past the six-hour floor the quiet checks run
        /// on. That is right for a host who just clicked it, and a held-down mouse would otherwise
        /// be a request per click.
        /// </summary>
        [Fact]
        public void A_second_click_inside_the_minute_is_a_cooldown()
        {
            var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(BlendWindow.SelfUpdateOnCooldown(now.AddSeconds(-1), now));
            Assert.True(BlendWindow.SelfUpdateOnCooldown(now.AddSeconds(-59), now));
            Assert.False(BlendWindow.SelfUpdateOnCooldown(now.AddSeconds(-60), now));
            Assert.False(BlendWindow.SelfUpdateOnCooldown(DateTime.MinValue, now));

            Assert.Equal("cooldown", BlendWindow.SelfUpdateRefusal(
                checkEnabled: true, anyServerRunning: false, anyServerUpdateRunning: false,
                onCooldown: true));
        }

        /// <summary>
        /// The cooldown is the last word, never the first: a host told "a server is running" has
        /// something to do about it, and swapping that sentence for "try again in a minute" would
        /// send them back to the same refusal sixty seconds later none the wiser.
        /// </summary>
        [Fact]
        public void A_running_server_is_said_before_a_cooldown_is()
        {
            Assert.Equal("serverBusy", BlendWindow.SelfUpdateRefusal(
                checkEnabled: true, anyServerRunning: true, anyServerUpdateRunning: false,
                onCooldown: true));
        }

        /// <summary>
        /// The cooldown is only spent on a request that was really made. A refusal that never
        /// reached GitHub must not start the clock, or a host who stops their server is told to
        /// wait another minute for a check that never happened.
        /// </summary>
        [Fact]
        public void The_cooldown_clock_starts_after_the_refusal_gate_and_not_before_it()
        {
            var rpc = BridgeSource();
            var handler = rpc[rpc.IndexOf("RegisterRpc(\"app.selfUpdateNow\"", StringComparison.Ordinal)..];

            var refusalAt = handler.IndexOf("SelfUpdateRefusal", StringComparison.Ordinal);
            var returnAt = handler.IndexOf("return new { ok = false, reason = refusal };", StringComparison.Ordinal);
            var stampAt = handler.IndexOf("LastSelfUpdateAttemptUtc = DateTime.UtcNow", StringComparison.Ordinal);
            var stageAt = handler.IndexOf("TryStageUpdateAsync", StringComparison.Ordinal);

            Assert.True(refusalAt >= 0 && returnAt > refusalAt, "the refusal gate is gone from app.selfUpdateNow");
            Assert.True(stampAt > returnAt, "the cooldown is stamped before the refusal gate turns the host away");
            Assert.True(stageAt > stampAt, "the cooldown is stamped after the request it is meant to throttle");
        }

        // ---------------------------------------------------------------- the release notes link

        /// <summary>
        /// The notes button opens the release the check actually found, so the host reads the
        /// notes for the version they were just offered rather than whatever is newest today.
        /// </summary>
        [Fact]
        public void The_notes_address_is_the_release_the_check_found()
        {
            Assert.Equal(
                "https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/tag/1.0.8",
                BlendWindow.AppReleaseNotesUrl("https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/tag/1.0.8"));
        }

        /// <summary>
        /// And nothing else. The page never sends an address, and the one the check hands over
        /// only gets opened while it really is a release page on this repository: a check pointed
        /// somewhere else one day must not turn into the app opening somewhere else.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("https://example.com/releases/tag/1.0.8")]
        [InlineData("https://github.com/SomebodyElse/ValheimBakaLoader/releases/tag/1.0.8")]
        [InlineData("javascript:alert(1)")]
        [InlineData("file:///C:/Windows/System32/cmd.exe")]
        public void Anything_that_is_not_a_release_page_here_falls_back_to_the_releases_page(string url)
        {
            Assert.Equal(
                "https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/latest",
                BlendWindow.AppReleaseNotesUrl(url));
        }

        // ---------------------------------------------------------------- the status payload

        /// <summary>
        /// The payload the page reads is built from the very record the push event carries, so a
        /// surface that asks and a surface that listened cannot end up saying different things.
        /// </summary>
        [Fact]
        public async Task The_status_payload_reports_the_release_the_provider_actually_found()
        {
            var provider = new SoftwareUpdateProvider(
                new StubGitHub(new GitHubRelease
                {
                    TagName = "99.0.0",
                    HtmlUrl = "https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/tag/99.0.0",
                }),
                MockUserPreferencesProvider,
                GetService<IApplicationLogger>());

            await provider.CheckForUpdatesAsync(isManualCheck: true);

            var dto = JObject.FromObject(BlendWindow.BuildAppUpdateStatusDto(
                provider.LatestAvailable,
                installedVersion: "1.0.7",
                autoUpdateOnRestart: false,
                checkEnabled: true,
                anyServerRunning: true));

            Assert.Equal("1.0.7", dto.Value<string>("installedVersion"));
            Assert.Equal("99.0.0", dto.Value<string>("latestVersion"));
            Assert.True(dto.Value<bool>("updateAvailable"));
            Assert.Equal(
                "https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/tag/99.0.0",
                dto.Value<string>("releaseUrl"));
            Assert.False(dto.Value<bool>("autoUpdateOnRestart"));
            Assert.True(dto.Value<bool>("checkEnabled"));
            Assert.True(dto.Value<bool>("anyServerRunning"));
        }

        /// <summary>
        /// A release the check found somewhere other than this repository's releases is not a
        /// release page to send anyone to, so the payload says so with a null rather than quietly
        /// handing back the releases list under the name of a specific release.
        /// </summary>
        [Fact]
        public void A_release_address_that_is_not_one_of_ours_is_sent_as_nothing_at_all()
        {
            var dto = JObject.FromObject(BlendWindow.BuildAppUpdateStatusDto(
                new AppUpdateAvailability
                {
                    Version = "99.0.0",
                    NotesUrl = "https://example.com/releases/tag/99.0.0",
                },
                installedVersion: "1.0.7",
                autoUpdateOnRestart: false,
                checkEnabled: true,
                anyServerRunning: false));

            Assert.True(dto.Value<bool>("updateAvailable"));
            Assert.Null(dto.Value<string>("releaseUrl"));

            // The button behind it is still offered; it opens the releases page instead.
            Assert.Null(BlendWindow.SpecificAppReleaseUrl("https://example.com/releases/tag/99.0.0"));
            Assert.Equal(
                "https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/latest",
                BlendWindow.AppReleaseNotesUrl("https://example.com/releases/tag/99.0.0"));
        }

        /// <summary>
        /// Nothing found, and no state the page has to read around: the settings file the app
        /// could not read used to be a fourth answer here, and the provider it reads never
        /// produces one, so the page no longer carries a sentence for a thing that cannot happen.
        /// </summary>
        [Fact]
        public void The_page_no_longer_has_a_state_for_settings_that_cannot_be_unreadable()
        {
            var page = File.ReadAllText(WebUiPath("app.js"));

            Assert.DoesNotContain("prefsUnreadable", page);
            Assert.DoesNotContain("prefsReadable", page);
        }

        /// <summary>
        /// Up to date is the quiet answer, and every key still has to be there: the page reads
        /// them all on every refresh, and a missing one reads as undefined rather than as false.
        /// </summary>
        [Fact]
        public async Task With_nothing_newer_the_payload_says_so_without_dropping_a_key()
        {
            var provider = new SoftwareUpdateProvider(
                new StubGitHub(new GitHubRelease { TagName = "0.0.1" }),
                MockUserPreferencesProvider,
                GetService<IApplicationLogger>());

            await provider.CheckForUpdatesAsync(isManualCheck: true);
            Assert.Null(provider.LatestAvailable);

            var dto = JObject.FromObject(BlendWindow.BuildAppUpdateStatusDto(
                provider.LatestAvailable,
                installedVersion: "1.0.7",
                autoUpdateOnRestart: true,
                checkEnabled: true,
                anyServerRunning: false));

            Assert.False(dto.Value<bool>("updateAvailable"));
            Assert.Null(dto.Value<string>("latestVersion"));

            // Nothing was found, so there is no release page of its own to send anybody to.
            Assert.Null(dto.Value<string>("releaseUrl"));

            foreach (var key in new[]
            {
                "installedVersion", "latestVersion", "updateAvailable", "releaseUrl",
                "autoUpdateOnRestart", "checkEnabled", "anyServerRunning",
            })
            {
                Assert.True(dto.ContainsKey(key), "app.updateStatus dropped the key " + key);
            }
        }

        // ------------------------------------------------- the reason the host is actually given

        /// <summary>
        /// What the check ran into, turned into the sentence the host reads. The service used to
        /// answer with a plain false for every ending it had, so a machine with no internet and a
        /// machine already on the newest release were told the same thing: "nothing newer came
        /// back". One of those two the app had no way of knowing.
        /// </summary>
        [Theory]
        [InlineData(StageOutcome.NetworkError, "offline")]
        [InlineData(StageOutcome.SwitchedOff, "checkingOff")]
        [InlineData(StageOutcome.AlreadyCurrent, "notAvailable")]
        [InlineData(StageOutcome.NoRelease, "notAvailable")]
        public void Each_ending_of_the_check_gets_the_sentence_that_is_true_for_it(
            StageOutcome outcome, string reason)
        {
            Assert.Equal(reason, BlendWindow.SelfUpdateReason(outcome));
        }

        [Fact]
        public void A_staged_update_is_not_a_refusal_at_all()
        {
            Assert.Null(BlendWindow.SelfUpdateReason(StageOutcome.Staged));
        }

        /// <summary>
        /// The whole point of the outcome, stated on its own: an offline app must never claim to
        /// know what is on GitHub.
        /// </summary>
        [Fact]
        public void An_offline_app_is_never_told_it_is_already_up_to_date()
        {
            Assert.NotEqual(
                BlendWindow.SelfUpdateReason(StageOutcome.AlreadyCurrent),
                BlendWindow.SelfUpdateReason(StageOutcome.NetworkError));

            // And the page still has the sentence that goes with it. The words moved into the
            // catalog, so this now proves BOTH halves rather than only the literal: the offline
            // branch asks for its own id, and that id carries the sentence. Asserting on the
            // source text alone would have gone quiet the moment the wording moved.
            var page = File.ReadAllText(WebUiPath("app.js"));
            Assert.Contains("if(reason===\"offline\")", page);
            Assert.Contains("return T(\"appupd.refusal.offline\");", page);

            var offline = JObject.Parse(AppSourceTree.Web("i18n/en.json"))
                ["keys"]?["appupd.refusal.offline"]?["lore"]?.ToString();
            Assert.NotNull(offline);
            Assert.Contains("BakaLoader could not reach GitHub.", offline);
        }

        // ---------------------------------------------------------------- the never-stop gate

        /// <summary>
        /// The dialog is the only screen that offers to do something about a waiting release, and
        /// while a server is up the only thing it may offer is "next time BakaLoader closes". A
        /// convenience button that stopped the server first would be the app deciding, on the
        /// host's behalf, that their world coming down is an acceptable price for an update.
        /// <para>
        /// The gate is on the source rather than on a value, because the thing being guarded
        /// against is a line somebody adds later, not a value this run produces.
        /// </para>
        /// </summary>
        [Fact]
        public void The_update_dialog_never_stops_or_restarts_a_server()
        {
            var modal = AppUpdateModalSource();

            foreach (var forbidden in new[]
            {
                "server.stop", "server.restart", "server.start", "server.command",
                "lifecycleToggle", "stopServer", "restartServer", "kill",
                "ShutDownAllServers",
                // The dialog gained a second RPC (the release notes), so the list gains the
                // shapes a later button could reach for while it is being added to.
                "server.stopAll", "servers.stop", "app.quit", "app.close", "server.kill",
            })
            {
                Assert.False(modal.Contains(forbidden),
                    "appUpdateModal must never reach for " + forbidden);
            }

            // And it does offer the three things it is allowed to offer: the update itself, the
            // switch in Upkeep that installs it later, and the notes for the release it found.
            Assert.Contains("app.selfUpdateNow", modal);
            Assert.Contains("AutoUpdateBakaLoader:true", modal);
            Assert.Contains("shell.openAppRelease", modal);

            // The notes button reads the found release when the app handed one over, and the
            // releases page when it did not. Both of those open a page; neither touches a server.
            Assert.Contains("u.releaseUrl", modal);
            Assert.Contains("target:\"releases\"", modal);
        }

        /// <summary>
        /// The refusal reasons the app can send and the sentences the page has for them are two
        /// lists that have to match, or a host hits a dead "undefined" where the explanation was,
        /// or worse, a sentence about the wrong thing entirely.
        /// </summary>
        [Fact]
        public void Every_refusal_the_app_can_send_has_a_sentence_on_the_page()
        {
            var page = File.ReadAllText(WebUiPath("app.js"));
            Assert.Contains("appUpdRefusal", page);

            // Every reason app.selfUpdateNow can answer with, read off the bridge itself rather
            // than off a list somebody has to remember to update.
            foreach (var reason in RefusalReasonsTheAppCanSend())
            {
                Assert.True(page.Contains("\"" + reason + "\""),
                    "app.js has no sentence for the refusal reason " + reason);
            }
        }

        /// <summary>
        /// Every reason string the self-update path can put on the wire, taken from the bridge
        /// source: the two refusal overloads plus the reasons the RPC answers with directly.
        /// </summary>
        private static string[] RefusalReasonsTheAppCanSend()
        {
            var source = BridgeSource();

            // Two tight slices rather than the whole file: a "return \"steamLibrary\";" from some
            // other corner of the bridge is not a refusal reason, and folding one in here would
            // fail this test for a sentence the page was never supposed to have.
            var scan =
                Slice(source, "bool anyServerUpdateRunning, bool onCooldown)", "return null;")
                + Slice(source, "public static string SelfUpdateReason(", "default: return \"notAvailable\";")
                + Slice(source, "RegisterRpc(\"app.selfUpdateNow\"", "return new { ok = true };");

            var found = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(scan, @"(?:return|reason =) ""(\w+)"""))
            {
                found.Add(m.Groups[1].Value);
            }

            // The five the self-update can answer with today. Named so a sixth added without a
            // sentence trips this rather than passing quietly.
            foreach (var expected in new[]
            {
                "serverBusy", "checkingOff", "cooldown", "notAvailable", "offline",
            })
            {
                Assert.True(found.Contains(expected), "the bridge no longer sends the reason " + expected);
            }

            return found.ToArray();
        }

        /// <summary>The source between one marker and the first end marker after it.</summary>
        private static string Slice(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, "BlendWindow.Bridge.cs no longer contains " + from);

            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            Assert.True(end > start, "BlendWindow.Bridge.cs no longer contains " + to + " after " + from);

            return source[start..(end + to.Length)];
        }

        /// <summary>The bridge's own source, read from the tree rather than from a build.</summary>
        private static string BridgeSource([CallerFilePath] string thisFile = "")
        {
            // <repo>/ValheimBakaLoader.Tests/Forms/BlendWindowAppUpdateTests.cs
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile), "..", ".."));
            return File.ReadAllText(Path.Combine(repo, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"));
        }

        /// <summary>
        /// The body of appUpdateModal, from its declaration to the declaration that follows it.
        /// Reading the whole file would let a stop call anywhere else in the page satisfy the
        /// gate, which is the opposite of what it is for.
        /// </summary>
        private static string AppUpdateModalSource()
        {
            var page = File.ReadAllText(WebUiPath("app.js"));

            var start = page.IndexOf("async function appUpdateModal()");
            Assert.True(start >= 0, "appUpdateModal is gone from app.js");

            // Every top-level declaration in this file starts at column 0, so the next one is
            // the end of this function.
            var end = page.IndexOf("\n$(\"#hAppUpdPill\")", start);
            Assert.True(end > start, "could not find the end of appUpdateModal");

            return page[start..end];
        }

        private static string WebUiPath(string file, [CallerFilePath] string thisFile = "")
        {
            // <repo>/ValheimBakaLoader.Tests/Forms/BlendWindowAppUpdateTests.cs
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile), "..", ".."));
            return Path.Combine(repo, "ValheimBakaLoader", "WebUI", file);
        }

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
    }
}
