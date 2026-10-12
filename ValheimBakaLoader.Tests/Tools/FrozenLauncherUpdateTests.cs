using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// What an update does with the launcher now that the launcher never changes, and which
    /// release counts as one that is out.
    /// <para>
    /// Both halves come from 2026-10-04. Defender's local model quarantined a freshly written
    /// 1.2.7 exe, so the launcher is frozen and an update that would write the same bytes back
    /// does not write them at all. And v1.2.7 was set to pre-release that day so no host would
    /// self-update into the flagged hash, which only works if the whole app agrees that a
    /// pre-release is not out: the self-updater must not offer it, the pack lookup must not read
    /// its manifest, and a host sitting on 1.2.7 must still read itself as current rather than
    /// as two releases behind a 1.2.6 that is now the newest stable one.
    /// </para>
    /// </summary>
    public class FrozenLauncherUpdateTests : BaseTest
    {
        // --------------------------------------------------- the launcher the update writes

        /// <summary>
        /// The one write that ended the 1.2.7 incident. Staging leaves the frozen launcher out of
        /// the update altogether (ManagedUpdaterTests holds that), and the updater itself, handed a
        /// launcher with the same bytes as the one installed, leaves the file alone: no new bytes,
        /// no new write time, nothing for a local model to judge. Run in process here, starting
        /// nothing afterwards.
        /// </summary>
        [Fact]
        public void The_updater_does_not_write_a_launcher_that_is_already_these_bytes()
        {
            var root = LauncherMissingTests.TempRoot();
            try
            {
                var plan = Plan(root, out var install, out var pending);
                var exe = Path.Combine(install, LauncherRoute.LauncherName);
                File.WriteAllText(exe, "frozen launcher");
                var written = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(exe, written);
                File.WriteAllText(Path.Combine(install, LauncherRoute.LibraryName), "old library");
                File.WriteAllText(Path.Combine(pending, LauncherRoute.LauncherName), "frozen launcher");
                File.WriteAllText(Path.Combine(pending, LauncherRoute.LibraryName), "new library");

                var result = ManagedUpdater.Apply(plan, null);

                Assert.Equal(UpdateResult.Updated, result.Outcome);
                Assert.Equal(UpdateResult.LauncherKept, result.LauncherDownload);
                Assert.Equal("new library", File.ReadAllText(Path.Combine(install, LauncherRoute.LibraryName)));
                Assert.Equal("frozen launcher", File.ReadAllText(exe));
                Assert.Equal(written, File.GetLastWriteTimeUtc(exe));
            }
            finally
            {
                LauncherMissingTests.TryDelete(root);
            }
        }

        /// <summary>
        /// A launcher that genuinely changed is still written. Leaving a launcher alone is only for
        /// the same bytes, or for one the update cannot read; a guard that turned every difference
        /// into "nothing to do" would hold a host on an old launcher forever the day a new one is
        /// genuinely needed.
        /// </summary>
        [Fact]
        public void A_launcher_that_genuinely_changed_is_written()
        {
            var root = LauncherMissingTests.TempRoot();
            try
            {
                var plan = Plan(root, out var install, out var pending);
                var exe = Path.Combine(install, LauncherRoute.LauncherName);
                File.WriteAllText(exe, "old launcher");
                File.WriteAllText(Path.Combine(install, LauncherRoute.LibraryName), "old library");
                File.WriteAllText(Path.Combine(pending, LauncherRoute.LauncherName), "new launcher");
                File.WriteAllText(Path.Combine(pending, LauncherRoute.LibraryName), "new library");

                var result = ManagedUpdater.Apply(plan, null);

                Assert.Equal(UpdateResult.Updated, result.Outcome);
                Assert.Equal(UpdateResult.LauncherWritten, result.LauncherDownload);
                Assert.Equal("new launcher", File.ReadAllText(exe));
                Assert.Equal("new library", File.ReadAllText(Path.Combine(install, LauncherRoute.LibraryName)));
            }
            finally
            {
                LauncherMissingTests.TryDelete(root);
            }
        }

        /// <summary>
        /// A launcher that cannot be written into the install, with one already there, is not a
        /// reason to undo the update: the one that was there goes back where it was and the rest
        /// stays in, because the app is the library beside it. The write is made to fail by holding
        /// the installed launcher open without delete sharing, so it can be copied aside and not
        /// replaced.
        /// </summary>
        [Fact]
        public void A_launcher_that_cannot_be_written_keeps_the_old_one_and_the_update()
        {
            var root = LauncherMissingTests.TempRoot();
            try
            {
                var plan = Plan(root, out var install, out var pending);
                var exe = Path.Combine(install, LauncherRoute.LauncherName);
                File.WriteAllText(exe, "old launcher");
                File.WriteAllText(Path.Combine(install, LauncherRoute.LibraryName), "old library");
                File.WriteAllText(Path.Combine(pending, LauncherRoute.LauncherName), "new launcher");
                File.WriteAllText(Path.Combine(pending, LauncherRoute.LibraryName), "new library");

                UpdateResult result;
                using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    result = ManagedUpdater.Apply(plan, null);
                }

                Assert.Equal(UpdateResult.Updated, result.Outcome);
                Assert.Equal(UpdateResult.LauncherUnwritable, result.LauncherDownload);
                Assert.Equal("old launcher", File.ReadAllText(exe));
                Assert.Equal("new library", File.ReadAllText(Path.Combine(install, LauncherRoute.LibraryName)));

                var lines = ManagedUpdater.SagaLines(result, "1.2.13");
                Assert.Contains(lines, l => l.Warning && l.Line.Contains("could not be written"));
            }
            finally
            {
                LauncherMissingTests.TryDelete(root);
            }
        }

        /// <summary>
        /// And it says so where the host can read it. A good update is an ordinary line in the
        /// Saga, not a warning. The note the 1.2.12 watchdog leaves is still read for the one start
        /// after the update into 1.2.13, which that watchdog swaps: its routine note is marked and
        /// logged as an ordinary line, and a failure note is untouched and still a warning.
        /// </summary>
        [Fact]
        public void A_good_update_is_said_out_loud_and_is_not_a_warning()
        {
            var good = ManagedUpdater.SagaLines(
                new UpdateResult { Outcome = UpdateResult.Updated, Launcher = UpdateResult.LauncherFrozen, Start = UpdateResult.StartLauncher },
                "1.2.13");
            Assert.Single(good);
            Assert.False(good[0].Warning);

            var note = AppUpdateService.RoutineNoteMark + "The launcher was already these exact bytes.";
            Assert.True(AppUpdateService.IsRoutineNote(note));
            Assert.Equal("The launcher was already these exact bytes.", AppUpdateService.NoteText(note));

            const string bad = "The update could not be written: robocopy answered 8.";
            Assert.False(AppUpdateService.IsRoutineNote(bad));
            Assert.Equal(bad, AppUpdateService.NoteText(bad));

            // And the launch road reads both, telling the good from the bad.
            var splash = AppSourceTree.Read("ValheimBakaLoader", "Forms", "SplashForm.cs");
            Assert.Contains("ManagedUpdater.ReadAndClearResult(ManagedUpdater.WorkFolder())", splash);
            Assert.Contains("ManagedUpdater.SagaLines(result,", splash);
            Assert.Contains("IsRoutineNote(note)", splash);
            Assert.Contains("Logger.Information(\"{note}\", said)", splash);
        }

        private static ManagedUpdater.Plan Plan(string root, out string install, out string pending)
        {
            install = Path.Combine(root, "install folder");
            pending = ManagedUpdater.PendingFolder(install);
            Directory.CreateDirectory(pending);
            return new ManagedUpdater.Plan
            {
                Pid = 0,
                Pending = pending,
                Install = install,
                Work = Path.Combine(root, "work"),
                Wait = TimeSpan.Zero,
                Start = false,
            };
        }

        // ------------------------------------------------------- which release counts as out

        /// <summary>
        /// A newer pre-release sitting above a stable release is not what the self-updater
        /// offers. v1.2.7 was flipped to pre-release the day it was flagged, and a host on 1.2.6
        /// must stay on 1.2.6 rather than be walked into the quarantined hash.
        /// </summary>
        [Fact]
        public async Task A_newer_prerelease_is_not_the_release_the_self_updater_offers()
        {
            var client = Client(Releases(
                Row("v1.2.7", "2026-10-03T00:00:00Z", prerelease: true),
                Row("v1.2.8", "2026-10-04T00:00:00Z", draft: true),
                Row("v1.2.6", "2026-09-30T00:00:00Z")));

            var latest = await client.GetLatestReleaseAsync();

            Assert.NotNull(latest);
            Assert.Equal("v1.2.6", latest.TagName);
        }

        /// <summary>
        /// And the same for the list the language pack lookup reads. Its contract is "the newest
        /// release at or below the version I am running", which reaches down through this list,
        /// so a pre-release left in it would have packs fetched off a release that is not out.
        /// </summary>
        [Fact]
        public async Task A_newer_prerelease_is_not_in_the_list_the_pack_lookup_reads()
        {
            var client = Client(Releases(
                Row("v1.2.7", "2026-10-03T00:00:00Z", prerelease: true, asset: LanguagePackService.ManifestAssetName),
                Row("v1.2.8", "2026-10-04T00:00:00Z", draft: true, asset: LanguagePackService.ManifestAssetName),
                Row("v1.2.6", "2026-09-30T00:00:00Z", asset: LanguagePackService.ManifestAssetName)));

            var releases = await client.GetReleasesAsync();

            Assert.Single(releases);
            Assert.Equal("v1.2.6", releases[0].TagName);
            Assert.NotNull(releases[0].Asset(LanguagePackService.ManifestAssetName));
            Assert.DoesNotContain("v1.2.7", releases.Select(r => r.TagName));
        }

        /// <summary>
        /// And the one place a pre-release is still read, on purpose: the host's OWN version,
        /// asked for by name. v1.2.7 became a pre-release under the hosts who were already
        /// running it, and the packs with their sentences in are the 1.2.7 packs on that very
        /// release. Filtering here would send every one of them down to the 1.2.6 packs and put
        /// the newer lines back into English. The tag lookup is asked for one version by name,
        /// never for "what is newest", so it answers with what is there.
        /// </summary>
        [Fact]
        public async Task The_packs_for_a_version_that_became_a_prerelease_are_still_read_by_tag()
        {
            var client = Client(Row(
                "v1.2.7", "2026-10-03T00:00:00Z", prerelease: true,
                asset: LanguagePackService.ManifestAssetName));

            var release = await client.GetReleaseByTagAsync("v1.2.7");

            Assert.NotNull(release);
            Assert.True(release.Prerelease, "the fixture is not a pre-release, so this proves nothing");
            Assert.NotNull(release.Asset(LanguagePackService.ManifestAssetName));

            // And that is the first thing the pack lookup asks, before it ever reaches the list.
            var service = AppSourceTree.Files()["LanguagePackService.cs"];
            Assert.Contains("GetReleaseByTagAsync(\"v\" + AppVersion)", service);
        }

        // ------------------------------------------------ what a 1.2.7 install makes of itself

        /// <summary>
        /// The host whose 1.2.7 became a pre-release under them. The newest stable release is
        /// now 1.2.6, which is OLDER than what they are running, and that is not a reason to
        /// write 1.2.6 over them or to call them behind: they are current until 1.2.8 is out.
        /// </summary>
        [Fact]
        public async Task A_host_on_a_version_that_became_a_prerelease_reads_itself_as_current()
        {
            var service = Service("1.2.7", Release("v1.2.6"));

            Assert.Equal(StageOutcome.AlreadyCurrent, await service.TryStageUpdateAsync());
        }

        /// <summary>
        /// And is offered the next stable release the day it is published. The outcome here is
        /// not Staged because this stub release carries no app zip and nothing is downloaded in
        /// a test; what matters is that it got PAST the version gate, which AlreadyCurrent would
        /// mean it had not.
        /// </summary>
        [Fact]
        public async Task The_same_host_is_offered_the_next_stable_release()
        {
            var service = Service("1.2.7", Release("v1.2.8"));

            var outcome = await service.TryStageUpdateAsync();

            Assert.NotEqual(StageOutcome.AlreadyCurrent, outcome);
            Assert.Equal(StageOutcome.NoRelease, outcome);
        }

        /// <summary>
        /// The comparison's own arithmetic, both ways, with neither side being the build this
        /// test is running as. A version string is parsed rather than compared as text, because
        /// "1.10.0" sorts before "1.9.0" as text and after it as a version.
        /// </summary>
        [Theory]
        [InlineData("1.2.6", "1.2.7", -1)]
        [InlineData("1.2.7", "1.2.7", 0)]
        [InlineData("1.2.8", "1.2.7", 1)]
        [InlineData("v1.2.8", "1.2.7", 1)]
        [InlineData("1.10.0", "1.9.0", 1)]
        public void The_installed_side_of_the_comparison_is_a_version_not_a_string(
            string release, string installed, int expected)
        {
            Assert.Equal(expected, AssemblyHelper.CompareVersion(release, installed));
        }

        /// <summary>
        /// With nothing injected, the installed side is the library's own version, which is
        /// where it has always come from and the only place it may come from now that the exe is
        /// frozen at 1.2.6.
        /// </summary>
        [Fact]
        public async Task The_installed_version_defaults_to_the_library_and_not_to_the_launcher()
        {
            var running = AssemblyHelper.GetApplicationVersion();
            var service = Service(null, Release("v" + running));

            Assert.Equal(StageOutcome.AlreadyCurrent, await service.TryStageUpdateAsync());
            Assert.NotEqual("1.2.6", running);
        }

        /// <summary>
        /// And the seam stays a seam. InstalledVersion exists so the suite can put a host on a
        /// version this build is not; nothing shipped sets it, and it is on a service the
        /// container hands out, so a public setter is a knob on a live service that only a test
        /// has ever wanted. It is internal, reached through the assembly's
        /// InternalsVisibleTo("ValheimBakaLoader.Tests"), and this is what stops it quietly
        /// widening back out.
        /// </summary>
        [Fact]
        public void The_installed_version_seam_is_not_public()
        {
            var property = typeof(AppUpdateService).GetProperty(
                "InstalledVersion",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.NotNull(property);
            Assert.False(property.GetMethod?.IsPublic ?? false,
                "AppUpdateService.InstalledVersion is readable from outside the assembly. It is a "
                + "test seam on a service the container hands out: keep it internal and reach it "
                + "through InternalsVisibleTo.");
            Assert.False(property.SetMethod?.IsPublic ?? false,
                "AppUpdateService.InstalledVersion is settable from outside the assembly, which "
                + "makes a test seam into a knob on a live service. Keep it internal.");

            // And the friend declaration it is reached through is really there.
            Assert.Contains(
                "[assembly: InternalsVisibleTo(\"ValheimBakaLoader.Tests\")]",
                AppSourceTree.Files()["AtlasInternals.cs"]);
        }

        // ---------------------------------------------------------------------------- fixtures

        private AppUpdateService Service(string installedVersion, GitHubRelease release) =>
            new(new OneRelease(release), MockHttpClientProvider,
                MockUserPreferencesProvider, GetService<IApplicationLogger>())
            {
                InstalledVersion = installedVersion,
            };

        private static GitHubRelease Release(string tag) => new()
        {
            TagName = tag,
            PublishedAt = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc),
            Assets = Array.Empty<GitHubReleaseAsset>(),
        };

        private GitHubClient Client(string body) =>
            new(new RestClientContext(GetService<Serilog.ILogger>(),
                new RecordingHttpClientProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                })));

        private static string Releases(params string[] rows) => "[" + string.Join(",", rows) + "]";

        private static string Row(
            string tag, string published, bool prerelease = false, bool draft = false,
            string asset = "ValheimBakaLoader-win-x64.zip") =>
            "{\"tag_name\":\"" + tag + "\",\"published_at\":\"" + published + "\","
            + "\"draft\":" + (draft ? "true" : "false") + ","
            + "\"prerelease\":" + (prerelease ? "true" : "false") + ","
            + "\"assets\":[{\"name\":\"" + asset + "\","
            + "\"browser_download_url\":\"https://example.invalid/" + asset + "\",\"size\":1}]}";

        /// <summary>One release, whatever is asked for.</summary>
        private sealed class OneRelease : IGitHubClient
        {
            private readonly GitHubRelease Held;

            public OneRelease(GitHubRelease held) => Held = held;

            public Task<GitHubRelease> GetLatestReleaseAsync() => Task.FromResult(Held);

            public Task<GitHubRelease[]> GetReleasesAsync() =>
                Task.FromResult(Held == null ? Array.Empty<GitHubRelease>() : new[] { Held });

            public Task<GitHubRelease> GetReleaseByTagAsync(string tag) =>
                Task.FromResult(Held?.TagName == tag ? Held : null);
        }
    }
}
