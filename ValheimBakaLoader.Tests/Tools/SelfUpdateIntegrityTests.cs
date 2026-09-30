using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The one download in this app that writes over the app itself, and until 1.2.6 the only
    /// one held to nothing at all.
    /// <para>
    /// GitHub publishes the asset's size on the same object the address is read from, and
    /// GitHubReleaseAsset.Size was parsed and never read by anything. A zip that arrived short
    /// went into ZipContainsExe, which reads the central directory only, and the watchdog then
    /// unpacked it with Expand-Archive, which writes a truncated DLL and returns nothing to say
    /// so. The relaunched app raised BadImageFormatException and never came back, and the host
    /// had been told "BakaLoader is already on the newest release it can see".
    /// </para>
    /// </summary>
    public class SelfUpdateIntegrityTests : BaseTest, IDisposable
    {
        private readonly string Temp = Path.Combine(
            Path.GetTempPath(), "bakaloader-selfupd-" + Guid.NewGuid().ToString("N"));

        public SelfUpdateIntegrityTests() => Directory.CreateDirectory(Temp);

        public void Dispose()
        {
            try { Directory.Delete(Temp, recursive: true); } catch { /* best effort */ }
        }

        private string WriteFile(string name, byte[] bytes)
        {
            var path = Path.Combine(Temp, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        // ------------------------------------------------------------------ the size check

        [Fact]
        public void A_download_that_arrived_short_is_named_as_damaged()
        {
            var asset = new GitHubReleaseAsset { Name = "app.zip", Size = 317 };
            var path = WriteFile("short.zip", new byte[158]);

            var said = AppUpdateService.DescribeDamage(asset, path, written: 158);

            Assert.NotNull(said);
            Assert.Contains("158", said);
            Assert.Contains("317", said);
        }

        [Fact]
        public void A_download_whose_byte_count_matches_the_published_size_passes()
        {
            var asset = new GitHubReleaseAsset { Name = "app.zip", Size = 317 };
            var path = WriteFile("whole.zip", new byte[317]);

            Assert.Null(AppUpdateService.DescribeDamage(asset, path, written: 317));
        }

        /// <summary>
        /// A release published before GitHub answered a size is not refused for it: the zip's own
        /// readability is still the gate it always was. A guard that blocks a legitimate input is
        /// not a fix.
        /// </summary>
        [Fact]
        public void A_release_that_published_no_size_and_no_digest_is_not_refused()
        {
            var asset = new GitHubReleaseAsset { Name = "app.zip", Size = 0 };
            var path = WriteFile("nosize.zip", new byte[64]);

            Assert.Null(AppUpdateService.DescribeDamage(asset, path, written: 64));
        }

        // ------------------------------------------------------------------ the digest check

        [Fact]
        public void A_download_the_right_length_with_the_wrong_bytes_is_caught_by_the_digest()
        {
            var published = new byte[512];
            new Random(11).NextBytes(published);

            var arrived = published.ToArray();
            arrived[100] ^= 0xFF;   // same length, different content

            var asset = new GitHubReleaseAsset
            {
                Name = "app.zip",
                Size = published.LongLength,
                Digest = "sha256:" + Sha256Hex(published),
            };

            var path = WriteFile("rewritten.zip", arrived);
            var said = AppUpdateService.DescribeDamage(asset, path, written: arrived.LongLength);

            Assert.NotNull(said);
            Assert.Contains("digest", said);
        }

        [Fact]
        public void A_download_that_matches_the_digest_passes()
        {
            var published = new byte[512];
            new Random(12).NextBytes(published);

            var asset = new GitHubReleaseAsset
            {
                Name = "app.zip",
                Size = published.LongLength,
                Digest = "sha256:" + Sha256Hex(published),
            };

            var path = WriteFile("matching.zip", published);
            Assert.Null(AppUpdateService.DescribeDamage(asset, path, written: published.LongLength));
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("md5:abc", false)]
        [InlineData("sha256:tooshort", false)]
        [InlineData("sha256:0123456789012345678901234567890123456789012345678901234567890123", true)]
        public void Only_a_sha256_digest_of_the_right_length_is_read(string digest, bool read)
        {
            Assert.Equal(read, AppUpdateService.ReadSha256Digest(digest) != null);
        }

        // ------------------------------------------------------------------ what the host is told

        /// <summary>
        /// A damaged download has its OWN answer. It used to fall into NoRelease, and NoRelease
        /// is worded "BakaLoader is already on the newest release it can see" while the pill on
        /// the same screen names the newer version. That is a false statement about the host's
        /// machine, and the page now has a sentence for the real one.
        /// </summary>
        [Fact]
        public void A_damaged_download_is_not_reported_as_already_current()
        {
            Assert.Equal("damaged", BlendWindow.SelfUpdateReason(StageOutcome.DownloadDamaged));
            Assert.Equal("notAvailable", BlendWindow.SelfUpdateReason(StageOutcome.AlreadyCurrent));
            Assert.Equal("notAvailable", BlendWindow.SelfUpdateReason(StageOutcome.NoRelease));
            Assert.Equal("offline", BlendWindow.SelfUpdateReason(StageOutcome.NetworkError));
        }

        [Fact]
        public void The_page_has_a_sentence_for_the_damaged_answer()
        {
            var app = AppSourceTree.Web("app.js");
            Assert.Contains("reason===\"damaged\"", app);
            Assert.Contains("appupd.refusal.damaged", app);
            Assert.Contains("appupd.refusal.damaged", AppSourceTree.Web("i18n/en.json"));
        }

        // ------------------------------------------------------------------ the watchdog

        /// <summary>
        /// robocopy says what it did with its exit code, and anything from 8 up means files it
        /// could not write. PowerShell's own error handling never sees a native exit code, so
        /// the script sailed past a failed copy: half one version and half the other was left on
        /// disk and the app was relaunched on it, with exit 0 and nothing logged anywhere.
        /// </summary>
        [Fact]
        public void The_watchdog_reads_robocopys_exit_code_and_puts_the_old_files_back()
        {
            var script = BuildScript();

            Assert.Contains("$copyCode = $LASTEXITCODE", script);
            Assert.Contains("if ($copyCode -ge 8)", script);

            // The rollback copy is taken BEFORE the new files land, because the only way to undo
            // a half-finished copy is to still have the old files.
            var rollbackTaken = script.IndexOf("robocopy $installDir $rollbackDir", StringComparison.Ordinal);
            var newFilesWritten = script.IndexOf("robocopy $srcDir $installDir", StringComparison.Ordinal);
            Assert.True(rollbackTaken >= 0 && newFilesWritten > rollbackTaken,
                "the rollback copy is taken after the new files were written, which undoes nothing");

            // And the exe is not copied and nothing is relaunched on a mixed install: the guard
            // sits above both.
            var guard = script.IndexOf("if ($copyCode -ge 8)", StringComparison.Ordinal);
            var exeCopied = script.IndexOf("Copy-Item -Path $exeFile.FullName", StringComparison.Ordinal);
            Assert.True(guard < exeCopied, "the exe is copied before the exit code is read");
        }

        [Fact]
        public void The_watchdog_writes_a_note_the_app_reads_at_the_next_launch()
        {
            var script = BuildScript();

            Assert.Contains("$reportPath", script);
            Assert.Contains("Set-Content -LiteralPath $reportPath", script);

            // Outside the work folder, because step six deletes the work folder.
            AppUpdateService.ReportPathOverride = null;
            var report = AppUpdateService.UpdateReportPath();
            Assert.DoesNotContain("BakaLoaderUpdate", report);

            // And the launch road reads it.
            var splash = AppSourceTree.Read("ValheimBakaLoader", "Forms", "SplashForm.cs");
            Assert.Contains("ReadAndClearUpdateReport", splash);
        }

        /// <summary>
        /// Pointed at a temp file rather than at the real one. A copy of the app may well be
        /// running on this machine and reads that path at launch; a test must not put words in
        /// front of a host who never had a failed update.
        /// </summary>
        [Fact]
        public void The_note_is_read_once_and_taken_away()
        {
            var path = Path.Combine(Temp, "update-report.txt");
            AppUpdateService.ReportPathOverride = path;

            try
            {
                Assert.Equal(path, AppUpdateService.UpdateReportPath());
                File.WriteAllText(path, "  the update could not be written  ");

                Assert.Equal("the update could not be written", AppUpdateService.ReadAndClearUpdateReport());
                Assert.Null(AppUpdateService.ReadAndClearUpdateReport());
                Assert.False(File.Exists(path), "the note was read and left behind to be read again");
            }
            finally
            {
                AppUpdateService.ReportPathOverride = null;
            }
        }

        private static string BuildScript()
        {
            var method = typeof(AppUpdateService).GetMethod(
                "BuildWatchdogScript", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            return (string)method.Invoke(null, new object[]
            {
                1234, @"C:\work\update.zip", @"C:\work\extracted", @"C:\install",
                @"C:\install\ValheimBakaLoader.exe", @"C:\work",
            });
        }
    }
}
