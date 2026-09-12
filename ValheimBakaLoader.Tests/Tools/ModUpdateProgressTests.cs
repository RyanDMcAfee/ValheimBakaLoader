using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Feature A: the bulk update reports one "updating" step before each mod and one
    /// "done"/"failed" after it, so the UI can show a determinate bar and a per-mod status.
    /// The returned results list must be exactly what it was before, so the completion path
    /// is unchanged.
    /// </summary>
    public class ModUpdateProgressTests
    {
        private sealed class RecordingProgress : IProgress<ModUpdateProgress>
        {
            public List<ModUpdateProgress> Reports { get; } = new();
            public void Report(ModUpdateProgress value) => Reports.Add(value);
        }

        private static ModUpdateService NewService(IThunderstoreClient thunderstore) => new(
            thunderstore,
            new MockHttpClientProvider(),
            new Mock<IApplicationLogger>().Object);

        [Fact]
        public async Task Each_mod_gets_an_updating_then_a_done_or_failed_report_with_a_running_index()
        {
            // A: empty author -> fails fast at validation (no network, no files) -> "failed".
            var a = new InstalledMod { Author = "", ModName = "Broken", InstalledVersion = "1.0.0" };

            // B: already current -> skipped -> "done". Needs a real folder and a package whose
            // latest version equals the installed one, so nothing is downloaded.
            var bDir = Path.Combine(Path.GetTempPath(), "vbl-upd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(bDir);
            try
            {
                var b = new InstalledMod
                {
                    Author = "Owner",
                    ModName = "Current",
                    InstalledVersion = "2.0.0",
                    PluginDirectory = bDir,
                };

                var thunderstore = new Mock<IThunderstoreClient>();
                thunderstore.Setup(t => t.GetLatestAsync("Owner", "Current"))
                    .ReturnsAsync(new ThunderstorePackage
                    {
                        Namespace = "Owner",
                        Name = "Current",
                        Latest = new ThunderstorePackageVersion
                        {
                            VersionNumber = "2.0.0",
                            DownloadUrl = "https://example/current.zip",
                        },
                    });

                var service = NewService(thunderstore.Object);
                var progress = new RecordingProgress();

                var results = await service.UpdateModsAsync(new[] { a, b }, progress);

                // Two mods -> four reports in order.
                Assert.Equal(4, progress.Reports.Count);

                Assert.Equal("updating", progress.Reports[0].Phase);
                Assert.Equal(1, progress.Reports[0].Index);
                Assert.Equal(2, progress.Reports[0].Total);
                Assert.Equal("-Broken", progress.Reports[0].Mod);

                Assert.Equal("failed", progress.Reports[1].Phase);
                Assert.Equal(1, progress.Reports[1].Index);
                Assert.False(string.IsNullOrEmpty(progress.Reports[1].Error));

                Assert.Equal("updating", progress.Reports[2].Phase);
                Assert.Equal(2, progress.Reports[2].Index);
                Assert.Equal(2, progress.Reports[2].Total);
                Assert.Equal("Owner-Current", progress.Reports[2].Mod);

                Assert.Equal("done", progress.Reports[3].Phase);
                Assert.Equal(2, progress.Reports[3].Index);
                Assert.Null(progress.Reports[3].Error);

                // The results list is unchanged from the no-progress behavior.
                Assert.Equal(2, results.Count);
                Assert.False(string.IsNullOrEmpty(results[0].Error)); // A failed
                Assert.False(results[1].Updated);                     // B skipped
                Assert.Null(results[1].Error);
            }
            finally
            {
                Directory.Delete(bDir, recursive: true);
            }
        }

        [Fact]
        public async Task The_old_call_without_a_reporter_still_returns_a_result_per_mod()
        {
            var a = new InstalledMod { Author = "", ModName = "One", InstalledVersion = "1.0.0" };
            var b = new InstalledMod { Author = "", ModName = "Two", InstalledVersion = "1.0.0" };

            var service = NewService(new Mock<IThunderstoreClient>().Object);

            var results = await service.UpdateModsAsync(new[] { a, b });

            Assert.Equal(2, results.Count);
        }

        [Fact]
        public async Task A_null_mod_list_reports_nothing_and_returns_empty()
        {
            var service = NewService(new Mock<IThunderstoreClient>().Object);
            var progress = new RecordingProgress();

            var results = await service.UpdateModsAsync(null, progress);

            Assert.Empty(results);
            Assert.Empty(progress.Reports);
        }
    }
}
