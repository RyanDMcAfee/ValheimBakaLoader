using Moq;
using System;
using System.IO;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The migration off the third-party MaxPlayerCount mod runs while the server may still be
    /// holding the old plugin open, so it happens in two passes. These cover what the second
    /// pass must not throw away, and what a real failure has to say out loud.
    /// </summary>
    [Collection(CompanionPluginStatusTests.CollectionName)]
    public class MaxPlayersInstallerTests : IDisposable
    {
        private readonly string Root;
        private readonly string PluginsDir;
        private readonly string ConfigDir;
        private readonly MaxPlayersInstaller Installer;

        public MaxPlayersInstallerTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "baka-maxplayers-" + Guid.NewGuid().ToString("N"));
            PluginsDir = Path.Combine(Root, "BepInEx", "plugins");
            ConfigDir = Path.Combine(Root, "BepInEx", "config");
            Directory.CreateDirectory(PluginsDir);
            Directory.CreateDirectory(ConfigDir);

            Installer = new MaxPlayersInstaller(new Mock<IApplicationLogger>().Object);
            CompanionPluginStatus.Clear();
        }

        public void Dispose()
        {
            CompanionPluginStatus.Clear();
            try { Directory.Delete(Root, recursive: true); } catch { }
            GC.SuppressFinalize(this);
        }

        private string LegacyDir => Path.Combine(PluginsDir, MaxPlayersInstaller.LegacyFolderName);

        private string OurCfg => Path.Combine(ConfigDir, MaxPlayersInstaller.ConfigFileName);

        private string LegacyCfg => Path.Combine(ConfigDir, MaxPlayersInstaller.LegacyConfigFileName);

        private void InstallLegacyMod()
        {
            Directory.CreateDirectory(LegacyDir);
            File.WriteAllText(Path.Combine(LegacyDir, "MaxPlayerCount.dll"), "not a real assembly");
        }

        /// <summary>Writes a cfg and stamps its write time, so "which was saved last" is decided.</summary>
        private static void WriteCfg(string path, string key, int value, DateTime writtenUtc)
        {
            File.WriteAllText(path, "[General]\n" + key + " = " + value + "\n");
            File.SetLastWriteTimeUtc(path, writtenUtc);
        }

        [Fact]
        public void EnsureCurrent_AdoptsTheLegacyCountWhenOursIsUnset()
        {
            InstallLegacyMod();
            WriteCfg(LegacyCfg, "MaxPlayerCount", 24, DateTime.UtcNow);

            Installer.EnsureCurrent(PluginsDir);

            Assert.Equal(24, Installer.ReadConfiguredCount(ConfigDir));
        }

        [Fact]
        public void EnsureCurrent_AdoptsTheLegacyDefaultWhenNeitherCfgHasACount()
        {
            InstallLegacyMod();

            Installer.EnsureCurrent(PluginsDir);

            // Azumatt's mod defaults to 20 when its cfg is missing, so that is what was in force.
            Assert.Equal(20, Installer.ReadConfiguredCount(ConfigDir));
        }

        /// <summary>
        /// The deferred-migration data loss. Pass one adopted the legacy value into our cfg and
        /// then could not delete the locked folder, so the operator's next save reached only the
        /// legacy cfg. Pass two used to skip the adopt step (our key was no longer unset) and
        /// delete the legacy cfg, discarding the count the operator actually chose.
        /// </summary>
        [Fact]
        public void EnsureCurrent_KeepsTheCountSavedWhileTheMigrationWasWaiting()
        {
            InstallLegacyMod();

            var earlier = DateTime.UtcNow.AddMinutes(-10);
            WriteCfg(OurCfg, "MaxPlayers", 20, earlier);              // adopted by the failed pass
            WriteCfg(LegacyCfg, "MaxPlayerCount", 64, earlier.AddMinutes(5)); // what the operator saved

            Installer.EnsureCurrent(PluginsDir);

            Assert.Equal(64, Installer.ReadConfiguredCount(ConfigDir));
        }

        [Fact]
        public void EnsureCurrent_DoesNotOverwriteANewerCountOfOurOwn()
        {
            InstallLegacyMod();

            var earlier = DateTime.UtcNow.AddMinutes(-10);
            WriteCfg(LegacyCfg, "MaxPlayerCount", 20, earlier);
            WriteCfg(OurCfg, "MaxPlayers", 64, earlier.AddMinutes(5));

            Installer.EnsureCurrent(PluginsDir);

            Assert.Equal(64, Installer.ReadConfiguredCount(ConfigDir));
        }

        /// <summary>
        /// A locked legacy folder is a normal wait, not a failure: the operator must not be told
        /// a plugin broke every time they change the count while the server is up.
        /// </summary>
        [Fact]
        public void EnsureCurrent_TreatsALockedLegacyFolderAsAWaitAndNotAFailure()
        {
            InstallLegacyMod();
            WriteCfg(LegacyCfg, "MaxPlayerCount", 24, DateTime.UtcNow);

            var locked = Path.Combine(LegacyDir, "MaxPlayerCount.dll");
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Installer.EnsureCurrent(PluginsDir);
            }

            Assert.True(Directory.Exists(LegacyDir), "the locked folder must survive the pass");
            Assert.Equal(24, Installer.ReadConfiguredCount(ConfigDir));
            Assert.Empty(CompanionPluginStatus.Failures);
        }

        /// <summary>
        /// The migration used to delete the working mod and its cfg and only then try to copy the
        /// replacement in. A copy that could not happen left the server at the vanilla cap of 10
        /// with the operator's chosen count in a cfg no installed plugin reads. The order is now
        /// the other way round, so a failed copy costs nothing.
        /// </summary>
        [Fact]
        public void EnsureCurrent_KeepsTheLegacyModWhenTheReplacementCannotBeInstalled()
        {
            InstallLegacyMod();
            WriteCfg(LegacyCfg, "MaxPlayerCount", 40, DateTime.UtcNow);

            // A plain file sitting where the plugin's folder has to go, so the copy cannot
            // happen. Same shape of failure as a missing bundled DLL or a plugins folder that
            // will not take a write, and the only one a test can stage on any machine.
            File.WriteAllText(Path.Combine(PluginsDir, MaxPlayersInstaller.PluginFolderName), "in the way");

            Installer.EnsureCurrent(PluginsDir);

            Assert.True(Directory.Exists(LegacyDir),
                "the working mod was removed before its replacement was in place");
            Assert.True(File.Exists(LegacyCfg), "the legacy mod's own cfg went with it");
            Assert.False(Installer.IsInstalled(PluginsDir));

            // And the operator is told, rather than being left to find out from a full server.
            Assert.Single(CompanionPluginStatus.Failures);
            Assert.Equal(MaxPlayersInstaller.CompanionPluginName,
                CompanionPluginStatus.Failures[0].Plugin);
        }

        /// <summary>
        /// A pass with nothing to install checked nothing, so it has no news. Reporting success
        /// from it wiped the record of a real failure on the very next start.
        /// </summary>
        [Fact]
        public void EnsureCurrent_LeavesAnEarlierFailureStandingWhenThereIsNothingToInstall()
        {
            CompanionPluginStatus.ReportFailure(MaxPlayersInstaller.CompanionPluginName,
                "the bundled plugin could not be copied");

            // No legacy mod, and the bundled plugin is not installed either: the ordinary state
            // of a server that never raised the cap.
            Installer.EnsureCurrent(PluginsDir);

            Assert.Single(CompanionPluginStatus.Failures);
            Assert.Equal(MaxPlayersInstaller.CompanionPluginName,
                CompanionPluginStatus.Failures[0].Plugin);
        }
    }
}
