using System;
using System.IO;
using System.Linq;
using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// A companion plugin that will not install used to leave nothing behind but a line in the
    /// application log, which no operator reads, so the feature it powers simply went missing.
    /// These cover the record that carries such a failure out to the server log and the interface.
    /// The record is process-wide, so every test class that touches it shares one collection and
    /// they never run at the same time.
    /// </summary>
    [Collection(CompanionPluginStatusTests.CollectionName)]
    public class CompanionPluginStatusTests : IDisposable
    {
        public const string CollectionName = "companion plugin status";

        public CompanionPluginStatusTests() => CompanionPluginStatus.Clear();

        public void Dispose()
        {
            CompanionPluginStatus.Clear();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void ReportFailure_KeepsThePluginAndTheReason()
        {
            CompanionPluginStatus.ReportFailure("Item indexer", "Access to the path is denied.");

            var failure = Assert.Single(CompanionPluginStatus.Failures);
            Assert.Equal("Item indexer", failure.Plugin);
            Assert.Equal("Access to the path is denied.", failure.Message);
            Assert.True(CompanionPluginStatus.HasFailures);
            Assert.Contains("Item indexer", failure.Describe());
            Assert.Contains("Access to the path is denied.", failure.Describe());
        }

        [Fact]
        public void ReportFailure_ReplacesAnEarlierFailureForTheSamePlugin()
        {
            CompanionPluginStatus.ReportFailure("Item indexer", "first");
            CompanionPluginStatus.ReportFailure("Item indexer", "second");

            var failure = Assert.Single(CompanionPluginStatus.Failures);
            Assert.Equal("second", failure.Message);
        }

        [Fact]
        public void ReportSuccess_ClearsOnlyThatPlugin()
        {
            CompanionPluginStatus.ReportFailure("Item indexer", "no");
            CompanionPluginStatus.ReportFailure("Max Players", "also no");

            CompanionPluginStatus.ReportSuccess("Item indexer");

            var failure = Assert.Single(CompanionPluginStatus.Failures);
            Assert.Equal("Max Players", failure.Plugin);
        }

        /// <summary>
        /// The record is process-wide but two realms can be up at once, so what one realm's
        /// start recorded must never be handed to the other. Anything recorded with no realm in
        /// hand belongs to none of them in particular and is still shown to all of them.
        /// </summary>
        [Fact]
        public void FailuresFor_GivesOneRealmItsOwnPlusTheUnscopedOnes()
        {
            using (CompanionPluginStatus.BeginProfile("Alpha"))
                CompanionPluginStatus.ReportFailure("Item indexer", "alpha could not install it");
            using (CompanionPluginStatus.BeginProfile("Beta"))
                CompanionPluginStatus.ReportFailure("Max Players", "beta could not install it");
            CompanionPluginStatus.ReportFailure("Commander", "no realm in hand");

            var alpha = CompanionPluginStatus.FailuresFor("Alpha").Select(f => f.Plugin).OrderBy(n => n).ToArray();
            var beta = CompanionPluginStatus.FailuresFor("Beta").Select(f => f.Plugin).OrderBy(n => n).ToArray();

            Assert.Equal(new[] { "Commander", "Item indexer" }, alpha);
            Assert.Equal(new[] { "Commander", "Max Players" }, beta);

            // Asking with no realm in hand is the old whole-list read, which is what the
            // single-server path still wants.
            Assert.Equal(3, CompanionPluginStatus.Failures.Count);
        }

        /// <summary>
        /// Same plugin, both realms: two records, not one overwriting the other.
        /// </summary>
        [Fact]
        public void ReportFailure_KeepsARecordPerRealmForTheSamePlugin()
        {
            using (CompanionPluginStatus.BeginProfile("Alpha"))
                CompanionPluginStatus.ReportFailure("Item indexer", "alpha reason");
            using (CompanionPluginStatus.BeginProfile("Beta"))
                CompanionPluginStatus.ReportFailure("Item indexer", "beta reason");

            Assert.Equal(2, CompanionPluginStatus.Failures.Count);
            Assert.Equal("alpha reason", Assert.Single(CompanionPluginStatus.FailuresFor("Alpha")).Message);
            Assert.Equal("beta reason", Assert.Single(CompanionPluginStatus.FailuresFor("Beta")).Message);
        }

        [Fact]
        public void ReportSuccess_ClearsOnlyTheRealmInScope()
        {
            using (CompanionPluginStatus.BeginProfile("Alpha"))
                CompanionPluginStatus.ReportFailure("Item indexer", "alpha reason");
            using (CompanionPluginStatus.BeginProfile("Beta"))
                CompanionPluginStatus.ReportFailure("Item indexer", "beta reason");

            using (CompanionPluginStatus.BeginProfile("Alpha"))
                CompanionPluginStatus.ReportSuccess("Item indexer");

            var left = Assert.Single(CompanionPluginStatus.Failures);
            Assert.Equal("Beta", left.Profile);
            Assert.Equal("beta reason", left.Message);
        }

        /// <summary>
        /// A realm starting again forgets what it recorded last time, so a plugin that has since
        /// been fixed stops being reported. The other realm's news has to survive that.
        /// </summary>
        [Fact]
        public void ClearProfile_ForgetsOneRealmAndLeavesTheOther()
        {
            using (CompanionPluginStatus.BeginProfile("Alpha"))
                CompanionPluginStatus.ReportFailure("spawn helper", "alpha reason");
            using (CompanionPluginStatus.BeginProfile("Beta"))
                CompanionPluginStatus.ReportFailure("spawn helper", "beta reason");

            CompanionPluginStatus.ClearProfile("Alpha");

            var left = Assert.Single(CompanionPluginStatus.Failures);
            Assert.Equal("Beta", left.Profile);
            Assert.Empty(CompanionPluginStatus.FailuresFor("Alpha"));
        }

        [Fact]
        public void BeginProfile_PutsBackTheScopeItFound()
        {
            using (CompanionPluginStatus.BeginProfile("Alpha"))
            {
                using (CompanionPluginStatus.BeginProfile("Beta"))
                    Assert.Equal("Beta", CompanionPluginStatus.CurrentProfile);

                Assert.Equal("Alpha", CompanionPluginStatus.CurrentProfile);
            }

            Assert.Null(CompanionPluginStatus.CurrentProfile);
        }

        [Fact]
        public void ReportFailure_WithNoDetailStillSaysSomethingReadable()
        {
            CompanionPluginStatus.ReportFailure("Kill-all", "   ");

            var failure = Assert.Single(CompanionPluginStatus.Failures);
            Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        }

        /// <summary>
        /// The indexer installer swallows its own failures on purpose, so a missing plugin can
        /// never block a server start. Before this it swallowed the news along with them.
        /// </summary>
        [Fact]
        public void ItemIndexerInstaller_ReportsAFailureTheOperatorCanBeShown()
        {
            var root = Path.Combine(Path.GetTempPath(), "baka-indexer-" + Guid.NewGuid().ToString("N"));
            var pluginsDir = Path.Combine(root, "BepInEx", "plugins");
            Directory.CreateDirectory(pluginsDir);

            // A FILE where the installer needs a folder: CreateDirectory then throws, exactly the
            // shape of a real permissions or lock failure, without needing either.
            File.WriteAllText(Path.Combine(pluginsDir, "BakaLoaderItemIndexer"), "in the way");

            try
            {
                var bundled = Path.Combine(AppContext.BaseDirectory, "Resources", "ItemIndexer",
                    "BakaLoaderItemIndexer.dll");
                if (!File.Exists(bundled)) return; // nothing to install from, so nothing to prove

                new ItemIndexerInstaller(new Mock<IApplicationLogger>().Object).EnsureInstalled(pluginsDir);

                var failure = Assert.Single(CompanionPluginStatus.Failures);
                Assert.Equal(ItemIndexerInstaller.CompanionPluginName, failure.Plugin);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
