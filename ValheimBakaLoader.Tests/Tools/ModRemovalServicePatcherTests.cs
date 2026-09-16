using System;
using System.IO;
using Moq;
using Newtonsoft.Json;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// A mod removal has to take the mod's patcher folder as well as its plugin folder, or a
    /// patcher-type mod keeps loading its DLL on every boot. Covers the plugin-plus-patcher,
    /// patcher-only, and plugin-only shapes, and proves both parts are backed up before deletion.
    /// </summary>
    public class ModRemovalServicePatcherTests
    {
        private static ModRemovalService NewService() => new(new Mock<IApplicationLogger>().Object);

        /// <summary>Builds a temp BepInEx tree and hands back its plugins and patchers paths.</summary>
        private static string NewBepInEx(out string plugins, out string patchers)
        {
            var bep = Path.Combine(Path.GetTempPath(), "vbl-rm-" + Guid.NewGuid().ToString("N"), "BepInEx");
            plugins = Path.Combine(bep, "plugins");
            patchers = Path.Combine(bep, "patchers");
            Directory.CreateDirectory(plugins);
            Directory.CreateDirectory(patchers);
            return bep;
        }

        private static string MakeFolder(string parent, string folderName, string fileName)
        {
            var dir = Path.Combine(parent, folderName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), "x");
            return dir;
        }

        [Fact]
        public void Removing_a_plugin_plus_patcher_mod_takes_both_folders_and_backs_both_up()
        {
            var bep = NewBepInEx(out var plugins, out var patchers);
            var root = Directory.GetParent(bep).FullName;
            try
            {
                var pluginDir = MakeFolder(plugins, "Owner-ModA", "Owner.ModA.dll");
                var patcherDir = MakeFolder(patchers, "Owner-ModA", "Owner.ModA.Patcher.dll");
                var mod = new InstalledMod
                {
                    Author = "Owner",
                    ModName = "ModA",
                    PluginDirectory = pluginDir,
                    PatcherDirectory = patcherDir,
                };

                var result = NewService().RemoveMod(mod, includeConfig: false);

                Assert.True(result.Removed, result.Error);
                Assert.False(Directory.Exists(pluginDir));
                Assert.False(Directory.Exists(patcherDir));
                // The named subfolders go, the shared patchers directory itself stays.
                Assert.True(Directory.Exists(patchers));
                // Both parts are recoverable under the one backup root.
                Assert.True(Directory.Exists(Path.Combine(result.BackupDirectory, "plugin", "Owner-ModA")));
                Assert.True(Directory.Exists(Path.Combine(result.BackupDirectory, "patcher", "Owner-ModA")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Removing_a_patcher_only_mod_takes_the_patcher_folder()
        {
            var bep = NewBepInEx(out var plugins, out var patchers);
            var root = Directory.GetParent(bep).FullName;
            try
            {
                var patcherDir = MakeFolder(patchers, "Smoothbrain-StartupAccelerator", "Smoothbrain.StartupAccelerator.dll");
                var mod = new InstalledMod
                {
                    Author = "Smoothbrain",
                    ModName = "StartupAccelerator",
                    PatcherDirectory = patcherDir,
                    IsPatcher = true,
                    // PluginDirectory intentionally null: patcher-only mod.
                };

                var result = NewService().RemoveMod(mod, includeConfig: false);

                Assert.True(result.Removed, result.Error);
                Assert.False(Directory.Exists(patcherDir));
                Assert.True(Directory.Exists(patchers)); // junction/dir itself untouched
                Assert.True(Directory.Exists(Path.Combine(result.BackupDirectory, "patcher", "Smoothbrain-StartupAccelerator")));
                // No plugin part, so no plugin backup subfolder is created.
                Assert.False(Directory.Exists(Path.Combine(result.BackupDirectory, "plugin")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Removing_a_plugin_only_mod_is_unchanged_and_touches_no_patcher()
        {
            var bep = NewBepInEx(out var plugins, out var patchers);
            var root = Directory.GetParent(bep).FullName;
            try
            {
                var pluginDir = Path.Combine(plugins, "Owner-ModB");
                Directory.CreateDirectory(pluginDir);
                File.WriteAllText(Path.Combine(pluginDir, "manifest.json"), JsonConvert.SerializeObject(new
                {
                    name = "ModB",
                    version_number = "2.0.0",
                }));
                // An unrelated patcher folder must NOT be touched by this removal.
                var otherPatcher = MakeFolder(patchers, "Someone-Else", "Someone.Else.dll");

                var mod = new InstalledMod
                {
                    Author = "Owner",
                    ModName = "ModB",
                    PluginDirectory = pluginDir,
                    // PatcherDirectory null; there is no patchers/Owner-ModB folder.
                };

                var result = NewService().RemoveMod(mod, includeConfig: false);

                Assert.True(result.Removed, result.Error);
                Assert.False(Directory.Exists(pluginDir));
                Assert.True(Directory.Exists(Path.Combine(result.BackupDirectory, "plugin", "Owner-ModB")));
                // No matching patcher: no patcher backup, and the unrelated patcher survives.
                Assert.False(Directory.Exists(Path.Combine(result.BackupDirectory, "patcher")));
                Assert.True(Directory.Exists(otherPatcher));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void A_mod_with_neither_folder_present_fails_cleanly()
        {
            var mod = new InstalledMod
            {
                Author = "Ghost",
                ModName = "Gone",
                PluginDirectory = Path.Combine(Path.GetTempPath(), "vbl-missing-" + Guid.NewGuid().ToString("N")),
            };

            var result = NewService().RemoveMod(mod, includeConfig: false);

            Assert.False(result.Removed);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
    }
}
