using System;
using System.IO;
using System.Reflection;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// What a brand new isolated realm is born holding.
    /// <para>
    /// The loose-file loop at the end of ProvisionBepInEx copied EVERY file sitting in the
    /// seed realm's BepInEx root, ungated. Two of those describe the install they sit in
    /// rather than the loader: items.json is written by the indexer plugin and names every
    /// prefab THAT install's mod set provides, and LogOutput.log is the loader's record of
    /// what it loaded there. A realm provisioned with no mods at all therefore came up with
    /// a catalog of mods it does not have: the spawn picker offered them, the spawn validator
    /// accepted them because they were in the catalog, and the running server had no such
    /// prefab. The subdirectory loop had the same shape of problem with the loader's backup
    /// folder, which is tens of megabytes of bookkeeping about the BASE install that nothing
    /// in an instance will ever read, restore or prune.
    /// </para>
    /// </summary>
    public class RealmProvisionParityTests : BaseTest, IDisposable
    {
        private readonly string Root = Path.Combine(
            Path.GetTempPath(), "bakaloader-provision-" + Guid.NewGuid().ToString("N"));

        public RealmProvisionParityTests() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>
        /// A BepInEx root the way a real one looks, minus core and patchers: those are shared
        /// by junction, and a junction needs a privilege a test run does not have.
        /// </summary>
        private string SeedBepInEx()
        {
            var bep = Path.Combine(Root, "base", "BepInEx");
            Directory.CreateDirectory(Path.Combine(bep, "plugins", "Someone-SomeMod"));
            Directory.CreateDirectory(Path.Combine(bep, "config"));
            Directory.CreateDirectory(Path.Combine(bep, BepInExService.BackupDirName, "20260920-011500"));

            File.WriteAllText(Path.Combine(bep, "plugins", "Someone-SomeMod", "SomeMod.dll"), "a mod");
            File.WriteAllText(Path.Combine(bep, "config", "Someone.SomeMod.cfg"), "[General]");
            File.WriteAllText(
                Path.Combine(bep, BepInExService.BackupDirName, "20260920-011500", "winhttp.dll"), "old loader");

            // The loose files at the root: the two that describe this install, and one that
            // does not and must still travel.
            File.WriteAllText(Path.Combine(bep, "items.json"), "[{\"prefab\":\"SuperModSword\"}]");
            File.WriteAllText(Path.Combine(bep, "LogOutput.log"), "the other realm's session");
            File.WriteAllText(Path.Combine(bep, "BepInEx.cfg"), "[Logging]");

            return bep;
        }

        private static void Provision(string srcBep, string dstBep, bool seedPlugins, IApplicationLogger logger)
        {
            var service = new InstallIsolationService(logger);
            var method = typeof(InstallIsolationService).GetMethod(
                "ProvisionBepInEx", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            method.Invoke(service, new object[] { srcBep, dstBep, seedPlugins, true, null });
        }

        [Fact]
        public void A_realm_with_no_mods_is_born_with_no_item_catalog()
        {
            var src = SeedBepInEx();
            var dst = Path.Combine(Root, "instance", "BepInEx");

            Provision(src, dst, seedPlugins: false, GetService<IApplicationLogger>());

            Assert.False(Directory.Exists(Path.Combine(dst, "plugins", "Someone-SomeMod")),
                "the mods were copied although the host said not to");
            Assert.False(File.Exists(Path.Combine(dst, "items.json")),
                "the new realm was born holding the other realm's item catalog");
        }

        /// <summary>
        /// Even a realm seeded WITH the mods starts with no catalog: the indexer writes the one
        /// that matches what actually loaded, at that realm's first start, and a copy of another
        /// install's answer is not that.
        /// </summary>
        [Fact]
        public void A_realm_seeded_with_the_mods_still_writes_its_own_catalog()
        {
            var src = SeedBepInEx();
            var dst = Path.Combine(Root, "seeded", "BepInEx");

            Provision(src, dst, seedPlugins: true, GetService<IApplicationLogger>());

            Assert.True(File.Exists(Path.Combine(dst, "plugins", "Someone-SomeMod", "SomeMod.dll")));
            Assert.False(File.Exists(Path.Combine(dst, "items.json")));
        }

        [Fact]
        public void The_other_realms_loader_log_does_not_travel()
        {
            var src = SeedBepInEx();
            var dst = Path.Combine(Root, "nolog", "BepInEx");

            Provision(src, dst, seedPlugins: true, GetService<IApplicationLogger>());

            Assert.False(File.Exists(Path.Combine(dst, "LogOutput.log")),
                "the new realm's loader log opens as a copy of another realm's session");
        }

        /// <summary>
        /// The loose files that are NOT about the install still travel, because per-server edits
        /// to them must not bleed. A skip list that took the whole loop with it would be a
        /// different defect.
        /// </summary>
        [Fact]
        public void The_loose_files_that_are_not_about_the_install_still_travel()
        {
            var src = SeedBepInEx();
            var dst = Path.Combine(Root, "loose", "BepInEx");

            Provision(src, dst, seedPlugins: true, GetService<IApplicationLogger>());

            Assert.True(File.Exists(Path.Combine(dst, "BepInEx.cfg")));
            Assert.True(File.Exists(Path.Combine(dst, "config", "Someone.SomeMod.cfg")));
        }

        [Fact]
        public void The_base_installs_loader_backups_are_not_duplicated_into_every_realm()
        {
            var src = SeedBepInEx();
            var dst = Path.Combine(Root, "nobackups", "BepInEx");

            Provision(src, dst, seedPlugins: true, GetService<IApplicationLogger>());

            Assert.False(Directory.Exists(Path.Combine(dst, BepInExService.BackupDirName)),
                "every isolated realm still gets a full copy of the base install's loader backups");
        }
    }
}
