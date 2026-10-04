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
        /// The one write that ended the 1.2.7 incident. The watchdog hashes the launcher that
        /// arrived against the one already installed and leaves the file alone when they are the
        /// same, which with a frozen launcher is every update from 1.2.8 on: no new bytes, no
        /// new write time, nothing for a local model to judge.
        /// </summary>
        [Fact]
        public void The_watchdog_does_not_write_a_launcher_that_is_already_these_bytes()
        {
            var script = WatchdogScript();

            Assert.Contains("Get-FileHash -LiteralPath $exePath -Algorithm SHA256", script);
            Assert.Contains("Get-FileHash -LiteralPath $exeFile.FullName -Algorithm SHA256", script);
            Assert.Contains("$sameHost = $here -eq $there", script);

            // The copy is the else arm, so a matching hash reaches no write at all.
            var compared = script.IndexOf("$sameHost = $here -eq $there", StringComparison.Ordinal);
            var guarded = script.IndexOf("if ($sameHost) {", StringComparison.Ordinal);
            var copied = script.IndexOf("Copy-Item -Path $exeFile.FullName", StringComparison.Ordinal);

            Assert.True(compared < guarded, "the hashes are compared after the decision is taken");
            Assert.True(guarded < copied, "the launcher is copied before the hashes are looked at");
            Assert.Contains("} else {", script.Substring(guarded, copied - guarded));
        }

        /// <summary>
        /// A hash that cannot be read falls back to copying, not to skipping. A guard that turns
        /// an unreadable file into "nothing to do" would hold a host on an old launcher forever
        /// the day a new one is genuinely needed.
        /// </summary>
        [Fact]
        public void A_launcher_whose_hash_cannot_be_read_is_still_copied()
        {
            var script = WatchdogScript();

            var declared = script.IndexOf("$sameHost = $false", StringComparison.Ordinal);
            var compared = script.IndexOf("$sameHost = $here -eq $there", StringComparison.Ordinal);

            Assert.True(declared >= 0 && declared < compared,
                "the same-launcher answer is not false until something proves otherwise");
            Assert.Contains("catch { $sameHost = $false }", script);
        }

        /// <summary>
        /// And it says so where the host can read it. Every note the watchdog left used to be a
        /// failure, so the launch logged all of them as warnings; this one is an account of an
        /// update that worked, and it is marked as such and logged as an ordinary line.
        /// </summary>
        [Fact]
        public void The_skipped_launcher_is_said_out_loud_and_is_not_a_warning()
        {
            var script = WatchdogScript();

            Assert.Contains("Write-Note '" + AppUpdateService.RoutineNoteMark, script);
            Assert.Contains("The launcher was already these exact bytes", script);

            var note = AppUpdateService.RoutineNoteMark + "The launcher was already these exact bytes.";
            Assert.True(AppUpdateService.IsRoutineNote(note));
            Assert.Equal("The launcher was already these exact bytes.", AppUpdateService.NoteText(note));

            // A failure note is untouched and still a warning.
            const string bad = "The update could not be written: robocopy answered 8.";
            Assert.False(AppUpdateService.IsRoutineNote(bad));
            Assert.Equal(bad, AppUpdateService.NoteText(bad));

            // And the launch road tells the two apart rather than shouting at every host who
            // took an update that went perfectly.
            var splash = AppSourceTree.Read("ValheimBakaLoader", "Forms", "SplashForm.cs");
            Assert.Contains("IsRoutineNote(note)", splash);
            Assert.Contains("Logger.Information(\"{note}\", said)", splash);
        }

        /// <summary>
        /// And the script PowerShell is handed actually parses. Nothing in the suite has ever
        /// run the watchdog, because what it does is replace the app's own files, so every test
        /// about it reads the text instead. That leaves one hole the size of the whole feature: a
        /// brace or a quote out of place in the generated script is a self-update that cannot
        /// work for anybody, and no gate above would notice. So the real artefact goes through
        /// the real consumer, PowerShell's own parser, with nothing executed.
        /// </summary>
        [Fact]
        public void The_script_the_watchdog_runs_parses_as_PowerShell()
        {
            var path = Path.Combine(
                Path.GetTempPath(), "bakaloader-watchdog-" + Guid.NewGuid().ToString("N") + ".ps1");

            File.WriteAllText(path, WatchdogScript());
            try
            {
                var said = RepoScript.Run(
                    "powershell", "-NoProfile", "-NonInteractive", "-Command",
                    "$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile("
                    + "'" + path + "', [ref]$null, [ref]$e); "
                    + "if ($e -and $e.Count) { $e | ForEach-Object { $_.Message }; exit 1 }; "
                    + "Write-Output 'parsed'");

                Assert.True(said.Ok, "the watchdog script does not parse: " + said);
                Assert.Contains("parsed", said.Output);
            }
            finally
            {
                try { File.Delete(path); } catch { /* best effort */ }
            }
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

        /// <summary>The script the watchdog runs, built the way the service builds it.</summary>
        private static string WatchdogScript()
        {
            var build = typeof(AppUpdateService).GetMethod(
                "BuildWatchdogScript", BindingFlags.NonPublic | BindingFlags.Static);

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
