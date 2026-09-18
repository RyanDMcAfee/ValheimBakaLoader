using System;
using System.IO;
using System.Linq;
using Moq;
using Newtonsoft.Json;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Patcher-type mods install under BepInEx/patchers, a sibling of plugins. The scanner has to
    /// surface them so they can be seen and removed: merged into their plugin row when a mod ships
    /// both parts, added as an IsPatcher row when a mod is patcher-only, and never listing the
    /// framework or auto-generated patchers.
    /// </summary>
    public class ModScannerPatcherTests
    {
        private static ModScanner NewScanner() => new(new Mock<IApplicationLogger>().Object);

        /// <summary>Builds a temp BepInEx tree and returns its plugins directory path.</summary>
        private static string NewBepInEx(out string patchersDir)
        {
            var bep = Path.Combine(Path.GetTempPath(), "vbl-patch-" + Guid.NewGuid().ToString("N"), "BepInEx");
            var plugins = Path.Combine(bep, "plugins");
            patchersDir = Path.Combine(bep, "patchers");
            Directory.CreateDirectory(plugins);
            Directory.CreateDirectory(patchersDir);
            return plugins;
        }

        private static void WriteManifest(string modDir, string name, string version)
        {
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(modDir, "manifest.json"), JsonConvert.SerializeObject(new
            {
                name,
                version_number = version,
                dependencies = Array.Empty<string>(),
            }));
        }

        [Fact]
        public void A_mod_with_both_a_plugin_and_a_patcher_is_one_entry_with_the_patcher_attached()
        {
            var plugins = NewBepInEx(out var patchers);
            var bepRoot = Directory.GetParent(plugins).Parent.FullName;
            try
            {
                var pluginDir = Path.Combine(plugins, "Owner-ModA");
                var patcherDir = Path.Combine(patchers, "Owner-ModA");
                WriteManifest(pluginDir, "ModA", "1.0.0");
                Directory.CreateDirectory(patcherDir);
                File.WriteAllText(Path.Combine(patcherDir, "Owner.ModA.Patcher.dll"), "x");

                var mods = NewScanner().ScanPlugins(plugins);

                var only = Assert.Single(mods);
                Assert.Equal("Owner-ModA", only.FullName);
                Assert.Equal(pluginDir, only.PluginDirectory);
                Assert.Equal(patcherDir, only.PatcherDirectory);
                Assert.False(only.IsPatcher); // it has a plugin part, so it is not a patcher-only row
            }
            finally
            {
                Directory.Delete(bepRoot, recursive: true);
            }
        }

        [Fact]
        public void A_patcher_only_mod_is_surfaced_with_IsPatcher_true_and_no_plugin_dir()
        {
            var plugins = NewBepInEx(out var patchers);
            var bepRoot = Directory.GetParent(plugins).Parent.FullName;
            try
            {
                var patcherDir = Path.Combine(patchers, "Smoothbrain-StartupAccelerator");
                WriteManifest(patcherDir, "StartupAccelerator", "1.2.3");

                var mods = NewScanner().ScanPlugins(plugins);

                var only = Assert.Single(mods);
                Assert.Equal("Smoothbrain-StartupAccelerator", only.FullName);
                Assert.True(only.IsPatcher);
                Assert.Equal(patcherDir, only.PatcherDirectory);
                Assert.True(string.IsNullOrEmpty(only.PluginDirectory));
                Assert.Equal("1.2.3", only.InstalledVersion);
            }
            finally
            {
                Directory.Delete(bepRoot, recursive: true);
            }
        }

        [Fact]
        public void The_framework_and_generated_patchers_are_never_listed()
        {
            var plugins = NewBepInEx(out var patchers);
            var bepRoot = Directory.GetParent(plugins).Parent.FullName;
            try
            {
                Directory.CreateDirectory(Path.Combine(patchers, "denikson-BepInExPack_Valheim"));
                Directory.CreateDirectory(Path.Combine(patchers, "ValheimModding-HookGenPatcher"));

                var mods = NewScanner().ScanPlugins(plugins);

                Assert.Empty(mods);
            }
            finally
            {
                Directory.Delete(bepRoot, recursive: true);
            }
        }

        [Fact]
        public void A_missing_patchers_directory_is_a_no_op_and_plugins_still_scan()
        {
            var plugins = NewBepInEx(out var patchers);
            var bepRoot = Directory.GetParent(plugins).Parent.FullName;
            try
            {
                Directory.Delete(patchers, recursive: true); // no patchers dir at all
                WriteManifest(Path.Combine(plugins, "Owner-ModB"), "ModB", "2.0.0");

                var mods = NewScanner().ScanPlugins(plugins);

                var only = Assert.Single(mods);
                Assert.Equal("Owner-ModB", only.FullName);
                Assert.False(only.IsPatcher);
                Assert.True(string.IsNullOrEmpty(only.PatcherDirectory));
            }
            finally
            {
                Directory.Delete(bepRoot, recursive: true);
            }
        }

        [Fact]
        public void IsProtectedPatcher_matches_the_framework_and_generated_patchers_case_insensitively()
        {
            Assert.True(ModScanner.IsProtectedPatcher("denikson-BepInExPack_Valheim"));
            Assert.True(ModScanner.IsProtectedPatcher("valheimmodding-hookgenpatcher"));
            Assert.False(ModScanner.IsProtectedPatcher("Smoothbrain-StartupAccelerator"));
            Assert.False(ModScanner.IsProtectedPatcher(null));
        }

        /// <summary>
        /// HookGenPatcher is an ordinary Thunderstore listing and one of the most commonly named
        /// Valheim dependencies. Adding it through the app unpacks it into
        /// plugins/ValheimModding-HookGenPatcher, and a folder a host installed there keeps its
        /// row, its version and its Remove button. Only the patchers copy is held back, because
        /// that folder is shared across every isolated instance by a junction.
        /// </summary>
        [Fact]
        public void HookGenPatcher_installed_into_plugins_still_gets_a_row()
        {
            var plugins = NewBepInEx(out var patchers);
            var bepRoot = Directory.GetParent(plugins).Parent.FullName;
            try
            {
                WriteManifest(Path.Combine(plugins, "Azumatt-AzuAntiItemLag"), "AzuAntiItemLag", "1.0.0");
                WriteManifest(Path.Combine(plugins, "ValheimModding-HookGenPatcher"), "HookGenPatcher", "0.0.5");

                var mods = NewScanner().ScanPlugins(plugins);

                var hookGen = Assert.Single(mods.Where(m => m.FullName == "ValheimModding-HookGenPatcher"));
                Assert.Equal("0.0.5", hookGen.InstalledVersion);
                Assert.Equal(Path.Combine(plugins, "ValheimModding-HookGenPatcher"), hookGen.PluginDirectory);
                Assert.False(hookGen.IsPatcher);
                Assert.Contains(mods, m => m.FullName == "Azumatt-AzuAntiItemLag");
                Assert.Equal(2, mods.Count);
            }
            finally
            {
                Directory.Delete(bepRoot, recursive: true);
            }
        }

        /// <summary>
        /// The pack unpacked into plugins is the framework in the wrong place: the BepInEx row
        /// above the table owns that, so the mod table stays quiet about it. It is the only name
        /// the plugins scan holds back on framework grounds.
        /// </summary>
        [Fact]
        public void The_BepInEx_pack_in_the_plugins_folder_is_the_only_framework_name_the_plugins_scan_skips()
        {
            var plugins = NewBepInEx(out var patchers);
            var bepRoot = Directory.GetParent(plugins).Parent.FullName;
            try
            {
                WriteManifest(Path.Combine(plugins, "denikson-BepInExPack_Valheim"), "BepInExPack_Valheim", "5.4.2350");
                WriteManifest(Path.Combine(plugins, "ValheimModding-HookGenPatcher"), "HookGenPatcher", "0.0.5");

                var mods = NewScanner().ScanPlugins(plugins);

                Assert.DoesNotContain(mods, m => m.FullName == "denikson-BepInExPack_Valheim");
                Assert.Contains(mods, m => m.FullName == "ValheimModding-HookGenPatcher");

                Assert.True(ModScanner.IsPluginsFrameworkFolder("denikson-bepinexpack_valheim"));
                Assert.False(ModScanner.IsPluginsFrameworkFolder("ValheimModding-HookGenPatcher"));
                Assert.False(ModScanner.IsPluginsFrameworkFolder(null));
            }
            finally
            {
                Directory.Delete(bepRoot, recursive: true);
            }
        }
    }
}
