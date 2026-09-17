using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// they never run at the same time. One collection was never enough on its own: a server
    /// start files its news from a background task, and a class outside this collection that
    /// starts a server under the same realm name walks over these records while they are being
    /// read. Every test here therefore opens a record book of its own, which nothing outside
    /// this test can reach.
    /// </summary>
    [Collection(CompanionPluginStatusTests.CollectionName)]
    public class CompanionPluginStatusTests : IDisposable
    {
        public const string CollectionName = "companion plugin status";

        private readonly IDisposable OwnRecords;

        public CompanionPluginStatusTests()
        {
            OwnRecords = CompanionPluginStatus.BeginOwnRecords();
            CompanionPluginStatus.Clear();
        }

        public void Dispose()
        {
            CompanionPluginStatus.Clear();
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// The book opened in the constructor has to be the one the test method reads, or every
        /// test here is back on the shared record and back to reading another class's writes.
        /// This is the gate on that: it fails if the scope ever stops reaching the test body.
        /// </summary>
        [Fact]
        public void Every_test_here_reads_a_record_book_of_its_own()
            => Assert.True(CompanionPluginStatus.UsingOwnRecords);

        /// <summary>
        /// The gate on the book itself. The record is process-wide while the writers are not:
        /// a server start files its news from a background task, so a start elsewhere in the
        /// run that happened to name the same realm ran the same install pass and its opening
        /// clear took this test's record with it. A book of one's own has to be beyond the
        /// reach of a flow that never opened it, and this drives exactly that shape: another
        /// flow clearing the realm and writing its own news, with nothing of ours disturbed.
        /// </summary>
        [Fact]
        public void A_book_of_its_own_is_beyond_the_reach_of_another_flow()
        {
            CompanionPluginStatus.ReportFailure("Item indexer", "ours");

            // Started with the flow suppressed, so the task does NOT inherit this test's book
            // and lands on the shared one, which is where every other class in the run writes.
            using (ExecutionContext.SuppressFlow())
            {
                Task.Run(() =>
                {
                    Assert.False(CompanionPluginStatus.UsingOwnRecords);
                    CompanionPluginStatus.ClearProfile(null);
                    CompanionPluginStatus.Clear();
                    CompanionPluginStatus.ReportFailure("Max Players", "theirs");
                }).GetAwaiter().GetResult();
            }

            var failure = Assert.Single(CompanionPluginStatus.Failures);
            Assert.Equal("Item indexer", failure.Plugin);
            Assert.Equal("ours", failure.Message);
        }

        /// <summary>
        /// Every way a test file can reach this record. Naming the record outright is the
        /// obvious one. The other one is the reason this gate had to be widened: starting a
        /// ValheimServer runs the companion plugin install pass, and that pass clears the record
        /// for the realm it is starting without ever mentioning it by name. UpdateGateTests
        /// starts servers and mentions nothing, so the old spelling of this gate waved it
        /// through while its starts were walking over the records these tests were reading.
        /// </summary>
        private static readonly string[] WaysToReachTheRecord =
        {
            // Names the record itself.
            "CompanionPluginStatus.",

            // Or builds a server, which is the same thing once it is started: the install pass
            // inside Start() clears the realm's records and files its own.
            "GetService<ValheimServer>",
            "new ValheimServer(",
            "GetRequiredService<ValheimServer>",

            // Or leans on a helper that starts one for it.
            "SessionRunning(",
            "RunningServer(",
        };

        /// <summary>
        /// The rule generalised, so the next class to read this record cannot quietly reopen the
        /// hole. Sharing a collection is not enough on its own and never was: it serialises the
        /// classes that carry the attribute and nothing else, while a server start filing its
        /// news from a background task belongs to whichever test set it going, whenever that
        /// task happens to run. So the rule is about the book, not the collection: every test
        /// class that touches this record keeps one of its own.
        /// <para>
        /// And "touches" means reaches, not mentions. A class that starts a server touches the
        /// record whether it knows the record exists or not, which is exactly how the class that
        /// caused the last flake got past the first version of this gate.
        /// </para>
        /// </summary>
        [Fact]
        public void Every_test_class_that_touches_the_record_keeps_a_book_of_its_own()
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()));
            var offenders = new System.Collections.Generic.List<string>();

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;

                var text = File.ReadAllText(file);
                if (!WaysToReachTheRecord.Any(way => text.Contains(way, StringComparison.Ordinal))) continue;
                if (text.Contains("BeginOwnRecords()", StringComparison.Ordinal)) continue;

                offenders.Add(Path.GetFileName(file));
            }

            Assert.True(offenders.Count == 0,
                "these test files read the companion plugin record, or start a server whose "
                + "install pass writes to it, without a book of their own, so another class's "
                + "server start can walk over what they wrote: " + string.Join(", ", offenders));
        }

        /// <summary>
        /// The gate has to fail on the shape it is there to catch, or it is only a comment. This
        /// is the exact text UpdateGateTests had when the flake happened: a server built and
        /// started, the record never named, and no book opened.
        /// </summary>
        [Fact]
        public void The_gate_catches_a_class_that_only_starts_a_server()
        {
            const string beforeTheFix =
                "public class Something : BaseTest { void Go() { var s = GetService<ValheimServer>(); s.Start(o); } }";
            const string afterTheFix =
                "public class Something : BaseTest { Something() { Own = CompanionPluginStatus.BeginOwnRecords(); } "
                + "void Go() { var s = GetService<ValheimServer>(); s.Start(o); } }";

            Assert.Contains(WaysToReachTheRecord, way => beforeTheFix.Contains(way, StringComparison.Ordinal));
            Assert.DoesNotContain("BeginOwnRecords()", beforeTheFix, StringComparison.Ordinal);

            Assert.Contains("BeginOwnRecords()", afterTheFix, StringComparison.Ordinal);
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string here = "") => here;

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
