using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The rule a package file is placed by, row for row against Gale's
    /// <c>SubdirInstaller::map_file</c> with the BepInEx table (<c>mod_loader.rs</c>: plugins,
    /// patchers, monomod with <c>.mm.dll</c>, core, all SeparateFlatten, config untracked, plugins
    /// the default). Each expected value was worked from the Rust, not from a description of it:
    /// the walk stops at the FIRST component that names a subdir, ignoring case; what came
    /// before it is dropped; what comes after it is kept; with no such component the file goes
    /// into plugins by its file name alone; and when the trigger is the last component (a
    /// <c>.mm.dll</c> file, or a file named like a subdir) the file goes in by its own name.
    /// <para>
    /// The real shapes come from the Beyond Compare report Truncated attached to issue 20:
    /// JamesJonesTV-Pets ships <c>plugins/Pets/RavenHeimPets.dll</c> beside
    /// <c>config/jamesjones.RavenHeimPets.cfg</c>, and the DeathMonger packages ship
    /// <c>BepInEx/plugins/X.dll</c>.
    /// </para>
    /// </summary>
    public class PackageLayoutTests
    {
        [Theory]
        // Truncated's shapes.
        [InlineData("BepInEx/plugins/DeathMongerCore.dll", "DeathMonger-DeathMongerCore", "plugins/DeathMonger-DeathMongerCore/DeathMongerCore.dll", PackageFileKind.Plugins)]
        [InlineData("plugins/Pets/RavenHeimPets.dll", "JamesJonesTV-Pets", "plugins/JamesJonesTV-Pets/Pets/RavenHeimPets.dll", PackageFileKind.Plugins)]
        [InlineData("config/jamesjones.RavenHeimPets.cfg", "JamesJonesTV-Pets", "config/jamesjones.RavenHeimPets.cfg", PackageFileKind.Config)]
        [InlineData("manifest.json", "JamesJonesTV-Pets", "plugins/JamesJonesTV-Pets/manifest.json", PackageFileKind.Plugins)]
        // The plugins trigger in every spelling and depth.
        [InlineData("plugins/X.dll", "A-B", "plugins/A-B/X.dll", PackageFileKind.Plugins)]
        [InlineData("Plugins/X.dll", "A-B", "plugins/A-B/X.dll", PackageFileKind.Plugins)]
        [InlineData("PLUGINS/Sub/Deep/X.dll", "A-B", "plugins/A-B/Sub/Deep/X.dll", PackageFileKind.Plugins)]
        [InlineData("Packer/BepInEx/plugins/X.dll", "A-B", "plugins/A-B/X.dll", PackageFileKind.Plugins)]
        [InlineData("plugins\\Windows\\X.dll", "A-B", "plugins/A-B/Windows/X.dll", PackageFileKind.Plugins)]
        // The walk stops at the FIRST trigger, so a config folder inside plugins stays there.
        [InlineData("plugins/config/x.cfg", "A-B", "plugins/A-B/config/x.cfg", PackageFileKind.Plugins)]
        // No trigger at all: plugins, by file name only (Gale's flatten).
        [InlineData("X.dll", "A-B", "plugins/A-B/X.dll", PackageFileKind.Plugins)]
        [InlineData("Pets/RavenHeimPets.dll", "JamesJonesTV-Pets", "plugins/JamesJonesTV-Pets/RavenHeimPets.dll", PackageFileKind.Plugins)]
        [InlineData("Assets/Translations/English/t.json", "A-B", "plugins/A-B/t.json", PackageFileKind.Plugins)]
        [InlineData("BepInEx/X.dll", "A-B", "plugins/A-B/X.dll", PackageFileKind.Plugins)]
        [InlineData("icon.png", "A-B", "plugins/A-B/icon.png", PackageFileKind.Plugins)]
        [InlineData("README.md", "A-B", "plugins/A-B/README.md", PackageFileKind.Plugins)]
        [InlineData("CHANGELOG.md", "A-B", "plugins/A-B/CHANGELOG.md", PackageFileKind.Plugins)]
        // patchers, monomod, core: their own folder under the package name.
        [InlineData("patchers/Early.dll", "A-B", "patchers/A-B/Early.dll", PackageFileKind.Patchers)]
        [InlineData("BepInEx/patchers/Sub/Early.dll", "A-B", "patchers/A-B/Sub/Early.dll", PackageFileKind.Patchers)]
        [InlineData("monomod/Assembly-CSharp.Fix.mm.dll", "A-B", "monomod/A-B/Assembly-CSharp.Fix.mm.dll", PackageFileKind.Monomod)]
        [InlineData("Stuff/Assembly-CSharp.Fix.mm.dll", "A-B", "monomod/A-B/Assembly-CSharp.Fix.mm.dll", PackageFileKind.Monomod)]
        [InlineData("core/Loader.dll", "A-B", "core/A-B/Loader.dll", PackageFileKind.Core)]
        // config: shared, no package folder, sub folders kept.
        [InlineData("CONFIG/a.cfg", "A-B", "config/a.cfg", PackageFileKind.Config)]
        [InlineData("BepInEx/config/Sub/a.cfg", "A-B", "config/Sub/a.cfg", PackageFileKind.Config)]
        // A file named like a trigger is placed by its own name in that subdir (Gale's defaulted case).
        [InlineData("Stuff/config", "A-B", "config/config", PackageFileKind.Config)]
        public void A_package_file_lands_where_Gale_puts_it(string entry, string package, string expected, PackageFileKind kind)
        {
            var (destination, actualKind) = PackageLayout.Map(entry, package);

            Assert.Equal(expected, destination);
            Assert.Equal(kind, actualKind);
        }

        [Theory]
        [InlineData("plugins/")]
        [InlineData("")]
        [InlineData("../evil.dll")]
        [InlineData("plugins/../../evil.dll")]
        public void Directory_entries_and_paths_that_walk_up_are_ignored(string entry)
        {
            var (destination, kind) = PackageLayout.Map(entry, "A-B");

            Assert.Null(destination);
            Assert.Equal(PackageFileKind.Ignored, kind);
        }

        [Theory]
        [InlineData("manifest.json", true)]
        [InlineData("Manifest.JSON", true)]
        [InlineData("icon.png", true)]
        [InlineData("README.md", true)]
        [InlineData("CHANGELOG.md", true)]
        [InlineData("Lib/manifest.json", false)]
        [InlineData("plugins/manifest.json", false)]
        [InlineData("X.dll", false)]
        public void Only_the_packages_own_root_files_count_as_its_metadata(string entry, bool expected)
        {
            Assert.Equal(expected, PackageLayout.IsRootMetadata(entry));
        }
    }
}
