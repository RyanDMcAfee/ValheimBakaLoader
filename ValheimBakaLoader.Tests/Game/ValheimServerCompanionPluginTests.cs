using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// A companion plugin that will not install must never block a server start, so every
    /// installer failure is swallowed. It used to be swallowed so completely that the only
    /// trace was a line in the application log, and the feature it powers simply went missing
    /// with no explanation. These drive a real start with a broken installer and prove the
    /// failure comes back out where the host is already looking.
    /// </summary>
    [Collection(ValheimBakaLoader.Tests.Tools.CompanionPluginStatusTests.CollectionName)]
    public class ValheimServerCompanionPluginTests : BaseTest, IDisposable
    {
        private readonly string SandboxDir;

        public ValheimServerCompanionPluginTests()
        {
            CompanionPluginStatus.Clear();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-plugin-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            CompanionPluginStatus.Clear();
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void An_installer_that_throws_is_recorded_and_said_in_the_server_log()
        {
            // One of the three installers that does not report for itself.
            var broken = new Mock<ISpawnHelperInstaller>();
            broken
                .Setup(i => i.EnsureInstalled(It.IsAny<string>()))
                .Throws(new IOException("the plugins folder is read only {oops}"));

            ServiceCollection.Replace(ServiceDescriptor.Singleton(broken.Object));
            using var provider = ServiceCollection.BuildServiceProvider();
            var server = provider.GetRequiredService<ValheimServer>();

            var written = new List<string>();
            var options = Options();
            options.LogMessageHandler = line => { lock (written) written.Add(line); };

            try
            {
                server.Start(options);

                // The record the interface reads.
                var failure = Assert.Single(CompanionPluginStatus.Failures);
                Assert.Equal("spawn helper", failure.Plugin);
                Assert.Contains("the plugins folder is read only", failure.Message);

                // And the line in the log stream the web UI shows. It only exists once the
                // logger does, which is after the install pass, so this is the hop that was
                // missing rather than a restatement of the record above.
                string said;
                lock (written) said = string.Join("\n", written);

                Assert.Contains("The spawn helper plugin could not be installed", said);

                // The message went in as an argument, not as the template: the braces in it
                // would otherwise be read as Serilog property names and vanish.
                Assert.Contains("{oops}", said);
            }
            finally
            {
                try { server.Dispose(); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void Server_status_carries_the_failures_so_the_interface_never_has_to_read_a_log()
        {
            CompanionPluginStatus.ReportFailure("Item indexer", "Access to the path is denied.");

            var carried = BlendWindow.BuildPluginFailureDtos();

            var dto = JObject.FromObject(Assert.Single(carried));
            Assert.Equal("Item indexer", dto.Value<string>("plugin"));
            Assert.Equal("Access to the path is denied.", dto.Value<string>("message"));
            Assert.Contains("could not be installed", dto.Value<string>("text"));
        }

        /// <summary>
        /// The record behind server.status is process-wide, so with two realms up the list used
        /// to be handed whole to both of them and one realm's bar named the other realm's
        /// plugin. Each session now gets its own plus anything recorded with no realm in hand.
        /// </summary>
        [Fact]
        public void Server_status_carries_only_this_realms_failures()
        {
            using (CompanionPluginStatus.BeginProfile("Alpha"))
                CompanionPluginStatus.ReportFailure("Item indexer", "alpha could not install it");
            using (CompanionPluginStatus.BeginProfile("Beta"))
                CompanionPluginStatus.ReportFailure("Max Players", "beta could not install it");
            CompanionPluginStatus.ReportFailure("Commander", "no realm in hand");

            // What every realm's bar used to be handed: all three, two of them not its own.
            Assert.Equal(3, BlendWindow.BuildPluginFailureDtos().Count);

            var alpha = BlendWindow.BuildPluginFailureDtos("Alpha")
                .Select(JObject.FromObject)
                .ToList();

            Assert.Equal(
                new[] { "Commander", "Item indexer" },
                alpha.Select(d => d.Value<string>("plugin")).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.DoesNotContain(alpha, d => d.Value<string>("plugin") == "Max Players");

            // Each row says which realm it belongs to, and null for the one that belongs to none.
            Assert.Equal("Alpha", alpha.Single(d => d.Value<string>("plugin") == "Item indexer").Value<string>("profile"));
            Assert.Null(alpha.Single(d => d.Value<string>("plugin") == "Commander").Value<string>("profile"));
        }

        /// <summary>
        /// One realm starting again clears what that realm recorded last time and leaves the
        /// other realm's record alone. The clear runs at the top of the install pass, so this
        /// drives a real start rather than calling the record directly.
        /// </summary>
        [Fact]
        public void A_fresh_start_forgets_only_that_realms_earlier_failures()
        {
            using (CompanionPluginStatus.BeginProfile("Alpha"))
                CompanionPluginStatus.ReportFailure("spawn helper", "alpha failed last time");
            using (CompanionPluginStatus.BeginProfile("Beta"))
                CompanionPluginStatus.ReportFailure("spawn helper", "beta is still broken");

            using var provider = ServiceCollection.BuildServiceProvider();
            var server = provider.GetRequiredService<ValheimServer>();
            server.ServerKey = "Alpha";  // what the bridge stamps on a session's server

            try
            {
                server.Start(Options());

                Assert.Empty(CompanionPluginStatus.FailuresFor("Alpha"));

                var left = Assert.Single(CompanionPluginStatus.Failures);
                Assert.Equal("Beta", left.Profile);
                Assert.Equal("beta is still broken", left.Message);
            }
            finally
            {
                try { server.Dispose(); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// The failure a start writes carries the realm that started, so nothing else has to
        /// guess which realm the news belongs to.
        /// </summary>
        [Fact]
        public void A_failure_from_a_start_is_stamped_with_that_realm()
        {
            var broken = new Mock<ISpawnHelperInstaller>();
            broken
                .Setup(i => i.EnsureInstalled(It.IsAny<string>()))
                .Throws(new IOException("the plugins folder is read only"));

            ServiceCollection.Replace(ServiceDescriptor.Singleton(broken.Object));
            using var provider = ServiceCollection.BuildServiceProvider();
            var server = provider.GetRequiredService<ValheimServer>();
            server.ServerKey = "Alpha";

            try
            {
                server.Start(Options());

                var failure = Assert.Single(CompanionPluginStatus.Failures);
                Assert.Equal("Alpha", failure.Profile);

                // And the other realm never sees it.
                Assert.Empty(BlendWindow.BuildPluginFailureDtos("Beta"));

                // The scope does not outlive the install pass.
                Assert.Null(CompanionPluginStatus.CurrentProfile);
            }
            finally
            {
                try { server.Dispose(); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void A_clean_pass_carries_nothing()
        {
            Assert.Empty(BlendWindow.BuildPluginFailureDtos());
        }

        private ValheimServerOptions Options() => new()
        {
            Name = "Plugin Test Server",
            WorldName = "Plugin Test World",
            Password = "hunter2",
            Port = 2456,
            SaveInterval = 30,
            Backups = 1,
            BackupShort = 60,
            BackupLong = 120,
            LogToFile = false,
            ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
            SaveDataFolderPath = Path.Combine(SandboxDir, "saves"),
        };
    }
}
