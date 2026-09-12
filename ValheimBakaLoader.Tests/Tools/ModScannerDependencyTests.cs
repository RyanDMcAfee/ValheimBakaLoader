using System;
using System.IO;
using System.Linq;
using Moq;
using Newtonsoft.Json;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Feature C: a mod's manifest dependencies are surfaced so a removal can offer to take
    /// the mods that depend on it along too. Covers the "Namespace-Name-Version" parse, the
    /// scanner wiring, and the dependents resolution (direct, transitive, cycle-safe,
    /// target-excluded, ordered dependents-first).
    /// </summary>
    public class ModScannerDependencyTests
    {
        private static InstalledMod Mod(string author, string name, params string[] deps) => new()
        {
            Author = author,
            ModName = name,
            Dependencies = deps ?? Array.Empty<string>(),
        };

        // --------------------------------------------------------- dependency-string parse

        [Fact]
        public void A_core_dependency_string_parses_to_its_full_name()
        {
            var parsed = ModScanner.ParseDependencyFullNames(new[] { "denikson-BepInExPack_Valheim-5.4.2202" });

            Assert.Equal(new[] { "denikson-BepInExPack_Valheim" }, parsed);
        }

        [Fact]
        public void Blank_and_null_dependency_entries_are_skipped_and_the_result_is_never_null()
        {
            var parsed = ModScanner.ParseDependencyFullNames(new[] { "  ", null, "Owner-Mod-1.0.0" });

            Assert.Equal(new[] { "Owner-Mod" }, parsed);
        }

        [Fact]
        public void A_null_dependency_array_parses_to_an_empty_array()
        {
            Assert.Empty(ModScanner.ParseDependencyFullNames(null));
        }

        // --------------------------------------------------------- scanner wiring

        [Fact]
        public void ScanPlugins_reads_manifest_dependencies_into_the_mod()
        {
            var plugins = Path.Combine(Path.GetTempPath(), "vbl-deps-" + Guid.NewGuid().ToString("N"));
            var modDir = Path.Combine(plugins, "Owner-ModA");
            Directory.CreateDirectory(modDir);
            try
            {
                File.WriteAllText(Path.Combine(modDir, "manifest.json"), JsonConvert.SerializeObject(new
                {
                    name = "ModA",
                    version_number = "1.0.0",
                    dependencies = new[] { "denikson-BepInExPack_Valheim-5.4.2202" },
                }));

                var scanner = new ModScanner(new Mock<IApplicationLogger>().Object);
                var mods = scanner.ScanPlugins(plugins);

                var modA = Assert.Single(mods);
                Assert.Equal(new[] { "denikson-BepInExPack_Valheim" }, modA.Dependencies);
            }
            finally
            {
                Directory.Delete(plugins, recursive: true);
            }
        }

        [Fact]
        public void A_folder_with_no_manifest_carries_an_empty_dependency_array()
        {
            var plugins = Path.Combine(Path.GetTempPath(), "vbl-deps-" + Guid.NewGuid().ToString("N"));
            var modDir = Path.Combine(plugins, "Owner-LooseDll");
            Directory.CreateDirectory(modDir);
            try
            {
                File.WriteAllText(Path.Combine(modDir, "Owner.LooseDll.dll"), "not a real dll");

                var scanner = new ModScanner(new Mock<IApplicationLogger>().Object);
                var mods = scanner.ScanPlugins(plugins);

                var loose = Assert.Single(mods);
                Assert.NotNull(loose.Dependencies);
                Assert.Empty(loose.Dependencies);
            }
            finally
            {
                Directory.Delete(plugins, recursive: true);
            }
        }

        // --------------------------------------------------------- dependents resolution

        [Fact]
        public void A_direct_dependent_is_found()
        {
            var target = Mod("denikson", "BepInExPack_Valheim");
            var a = Mod("X", "ModA", "denikson-BepInExPack_Valheim");
            var independent = Mod("X", "ModC");

            var dependents = ModScanner.FindDependents(new[] { target, a, independent }, target.FullName);

            var only = Assert.Single(dependents);
            Assert.Equal("X-ModA", only.FullName);
        }

        [Fact]
        public void Nothing_depends_on_a_mod_returns_an_empty_list()
        {
            var a = Mod("X", "ModA", "denikson-BepInExPack_Valheim");
            var independent = Mod("X", "ModC");

            var dependents = ModScanner.FindDependents(new[] { a, independent }, independent.FullName);

            Assert.Empty(dependents);
        }

        [Fact]
        public void A_transitive_chain_is_ordered_dependents_first_and_excludes_the_target()
        {
            // b -> a -> target. Removing must go b, then a, then the target, so a chain is
            // never orphaned midway.
            var target = Mod("denikson", "BepInExPack_Valheim");
            var a = Mod("X", "ModA", "denikson-BepInExPack_Valheim");
            var b = Mod("X", "ModB", "X-ModA");

            var dependents = ModScanner.FindDependents(new[] { target, a, b }, target.FullName);

            Assert.Equal(new[] { "X-ModB", "X-ModA" }, dependents.Select(m => m.FullName).ToArray());
            Assert.DoesNotContain(dependents, m => m.FullName == target.FullName);
        }

        [Fact]
        public void A_dependency_cycle_is_walked_once_and_does_not_loop()
        {
            // a and b depend on each other and both reach the target. The visited set must keep
            // this finite and free of duplicates.
            var target = Mod("denikson", "BepInExPack_Valheim");
            var a = Mod("X", "ModA", "denikson-BepInExPack_Valheim", "X-ModB");
            var b = Mod("X", "ModB", "X-ModA");

            var dependents = ModScanner.FindDependents(new[] { target, a, b }, target.FullName);

            Assert.Equal(2, dependents.Count);
            Assert.Equal(2, dependents.Select(m => m.FullName).Distinct().Count());
            Assert.Contains(dependents, m => m.FullName == "X-ModA");
            Assert.Contains(dependents, m => m.FullName == "X-ModB");
        }

        [Fact]
        public void A_widely_depended_on_core_lists_every_dependent_once()
        {
            // Several mods depend on the same core; each appears exactly once.
            var core = Mod("denikson", "BepInExPack_Valheim");
            var a = Mod("X", "ModA", "denikson-BepInExPack_Valheim");
            var b = Mod("X", "ModB", "denikson-BepInExPack_Valheim");
            var c = Mod("X", "ModC", "denikson-BepInExPack_Valheim", "X-ModA");

            var dependents = ModScanner.FindDependents(new[] { core, a, b, c }, core.FullName);

            Assert.Equal(3, dependents.Count);
            Assert.Equal(3, dependents.Select(m => m.FullName).Distinct().Count());
            // C depends on A, so C must come before A.
            var names = dependents.Select(m => m.FullName).ToList();
            Assert.True(names.IndexOf("X-ModC") < names.IndexOf("X-ModA"));
        }
    }
}
