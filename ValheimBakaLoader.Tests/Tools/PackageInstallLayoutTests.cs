using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// A package going in through the REAL install, update and Hexium paths, laid out the way a
    /// mod manager lays it out (issues 19 and 20). Before 1.2.15 the whole zip went 1:1 into
    /// <c>plugins/Author-Name/</c>, so a patcher package did nothing and a package's config
    /// never reached <c>BepInEx/config</c>.
    /// <para>
    /// Every download is answered from memory by <see cref="RecordingHttpClientProvider"/> and
    /// every Thunderstore question by a mock, so nothing reaches the network; every file lands
    /// under a temporary folder.
    /// </para>
    /// </summary>
    public class PackageInstallLayoutTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "bakaloader-layout-" + Guid.NewGuid().ToString("N"));
        private readonly string Bep;
        private readonly string Plugins;

        public PackageInstallLayoutTests()
        {
            Bep = Path.Combine(Root, "server", "BepInEx");
            Plugins = Path.Combine(Bep, "plugins");
            Directory.CreateDirectory(Plugins);
        }

        public void Dispose()
        {
            // Junctions first, as links, so a recursive delete never walks into the base tree twice.
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).ToList())
                    if (BepInExService.IsReparsePoint(dir)) Directory.Delete(dir);
            }
            catch { /* best effort */ }

            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        private static byte[] Zip(IDictionary<string, string> entries)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, body) in entries)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(body);
                }
            }
            return buffer.ToArray();
        }

        private static string Manifest(string name, string version) =>
            "{\"name\":\"" + name + "\",\"version_number\":\"" + version + "\"}";

        private static ModUpdateService Service(byte[] zip, Mock<IThunderstoreClient> thunderstore = null)
        {
            var provider = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
            return new ModUpdateService((thunderstore ?? new Mock<IThunderstoreClient>(MockBehavior.Strict)).Object,
                provider, Mock.Of<IApplicationLogger>());
        }

        private static ThunderstoreModReference Pinned(string owner, string name, string version) =>
            new() { Owner = owner, Name = name, Version = version };

        private InstalledMod Scan(string fullName) =>
            new ModScanner(Mock.Of<IApplicationLogger>()).ScanPlugins(Plugins).Single(m => m.FullName == fullName);

        private string At(string relative) => Path.Combine(Bep, relative.Replace('/', Path.DirectorySeparatorChar));

        // ---- acceptance 1: Truncated's shapes ----

        [Fact]
        public async Task Truncateds_Pets_package_lands_the_way_Gale_lays_it_out()
        {
            var zip = Zip(new Dictionary<string, string>
            {
                ["manifest.json"] = Manifest("Pets", "1.0.4"),
                ["icon.png"] = "png",
                ["README.md"] = "readme",
                ["plugins/Pets/RavenHeimPets.dll"] = "the plugin",
                ["config/jamesjones.RavenHeimPets.cfg"] = "[General]\nauthor = shipped",
            });

            var result = await Service(zip).InstallFromThunderstoreAsync(Pinned("JamesJonesTV", "Pets", "1.0.4"), Plugins);

            Assert.True(result.Installed, result.Error);
            Assert.True(File.Exists(At("plugins/JamesJonesTV-Pets/Pets/RavenHeimPets.dll")));
            Assert.True(File.Exists(At("plugins/JamesJonesTV-Pets/manifest.json")));
            Assert.True(File.Exists(At("plugins/JamesJonesTV-Pets/icon.png")));
            Assert.True(File.Exists(At("plugins/JamesJonesTV-Pets/README.md")));
            Assert.Equal("[General]\nauthor = shipped", File.ReadAllText(At("config/jamesjones.RavenHeimPets.cfg")));

            // The old whole-zip shape is gone.
            Assert.False(Directory.Exists(At("plugins/JamesJonesTV-Pets/plugins")));
            Assert.False(Directory.Exists(At("plugins/JamesJonesTV-Pets/config")));

            var row = Scan("JamesJonesTV-Pets");
            Assert.Equal("1.0.4", row.InstalledVersion);
            Assert.Equal("managed", row.Layout);
            Assert.Equal("thunderstore", row.InstalledSource);
            Assert.False(row.IsHexiumInstalled);
            Assert.True(row.UpdateAvailable == false);
            Assert.NotNull(row.InstalledUtc);
            Assert.InRange(row.InstalledUtc.Value, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
            Assert.False(row.Disabled);
        }

        [Fact]
        public async Task A_DeathMonger_package_with_BepInEx_plugins_inside_lands_in_its_own_plugins_folder()
        {
            var zip = Zip(new Dictionary<string, string>
            {
                ["manifest.json"] = Manifest("DeathMongerCore", "2.1.0"),
                ["BepInEx/plugins/DeathMongerCore.dll"] = "core plugin",
            });

            var result = await Service(zip).InstallFromThunderstoreAsync(Pinned("DeathMonger", "DeathMongerCore", "2.1.0"), Plugins);

            Assert.True(result.Installed, result.Error);
            Assert.True(File.Exists(At("plugins/DeathMonger-DeathMongerCore/DeathMongerCore.dll")));
            Assert.False(Directory.Exists(At("plugins/DeathMonger-DeathMongerCore/BepInEx")));
        }

        // ---- every destination, and a second run over a config the host edited ----

        private static byte[] FullPackage(string version, bool withSecondConfig = false)
        {
            var entries = new Dictionary<string, string>
            {
                ["manifest.json"] = Manifest("Full", version),
                ["plugins/Full.dll"] = "plugin " + version,
                ["plugins/Assets/data.json"] = "{}",
                ["BepInEx/plugins/Nested.dll"] = "nested " + version,
                ["patchers/FullPatcher.dll"] = "patcher " + version,
                ["monomod/Assembly-CSharp.Full.mm.dll"] = "monomod " + version,
                ["config/Some.Full.cfg"] = "shipped " + version,
                ["Lib/manifest.json"] = Manifest("SomeBundledLib", "9.9.9"),
            };
            if (withSecondConfig) entries["config/Some.Full.Extra.cfg"] = "extra " + version;
            return Zip(entries);
        }

        [Fact]
        public async Task Every_part_of_a_package_reaches_its_own_folder()
        {
            var result = await Service(FullPackage("1.0.0")).InstallFromThunderstoreAsync(Pinned("Some", "Full", "1.0.0"), Plugins);

            Assert.True(result.Installed, result.Error);
            Assert.Equal("plugin 1.0.0", File.ReadAllText(At("plugins/Some-Full/Full.dll")));
            Assert.True(File.Exists(At("plugins/Some-Full/Assets/data.json")));
            Assert.Equal("nested 1.0.0", File.ReadAllText(At("plugins/Some-Full/Nested.dll")));
            Assert.Equal("patcher 1.0.0", File.ReadAllText(At("patchers/Some-Full/FullPatcher.dll")));
            Assert.Equal("monomod 1.0.0", File.ReadAllText(At("monomod/Some-Full/Assembly-CSharp.Full.mm.dll")));
            Assert.Equal("shipped 1.0.0", File.ReadAllText(At("config/Some.Full.cfg")));

            // A bundled library's manifest flattens onto the same name and never wins over the
            // package's own: the row has to read the package's version.
            Assert.Contains("\"1.0.0\"", File.ReadAllText(At("plugins/Some-Full/manifest.json")));
            var row = Scan("Some-Full");
            Assert.Equal("1.0.0", row.InstalledVersion);
            Assert.Equal(At("patchers/Some-Full"), row.PatcherDirectory);
        }

        [Fact]
        public async Task A_second_install_keeps_the_hosts_edit_and_writes_only_new_config_files()
        {
            await Service(FullPackage("1.0.0")).InstallFromThunderstoreAsync(Pinned("Some", "Full", "1.0.0"), Plugins);
            File.WriteAllText(At("config/Some.Full.cfg"), "the host's own settings");

            var result = await Service(FullPackage("1.1.0", withSecondConfig: true))
                .InstallFromThunderstoreAsync(Pinned("Some", "Full", "1.1.0"), Plugins);

            Assert.True(result.Installed, result.Error);
            Assert.True(result.Replaced);
            Assert.Equal("the host's own settings", File.ReadAllText(At("config/Some.Full.cfg")));
            Assert.Equal("extra 1.1.0", File.ReadAllText(At("config/Some.Full.Extra.cfg")));
            Assert.Equal("plugin 1.1.0", File.ReadAllText(At("plugins/Some-Full/Full.dll")));
            Assert.Equal("patcher 1.1.0", File.ReadAllText(At("patchers/Some-Full/FullPatcher.dll")));
            Assert.Equal("1.1.0", Scan("Some-Full").InstalledVersion);
        }

        // ---- rollback ----

        private static Dictionary<string, string> Snapshot(string dir) =>
            !Directory.Exists(dir)
                ? new Dictionary<string, string>()
                : Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(ModSourceMarkerFile.FileName, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllText);

        [Fact]
        public async Task A_placement_that_throws_half_way_puts_every_folder_back_and_takes_only_the_new_config()
        {
            await Service(FullPackage("1.0.0")).InstallFromThunderstoreAsync(Pinned("Some", "Full", "1.0.0"), Plugins);
            File.WriteAllText(At("config/Some.Full.cfg"), "the host's own settings");

            var pluginsBefore = Snapshot(At("plugins/Some-Full"));
            var patchersBefore = Snapshot(At("patchers/Some-Full"));
            var monomodBefore = Snapshot(At("monomod/Some-Full"));

            var service = Service(FullPackage("1.1.0", withSecondConfig: true));
            var written = 0;
            service.PlacingFile = destination =>
            {
                // Let the new config file and a few others land, then fail.
                if (++written == 6) throw new IOException("the disk filled up half way");
            };

            var result = await service.InstallFromThunderstoreAsync(Pinned("Some", "Full", "1.1.0"), Plugins);

            Assert.False(result.Installed);
            Assert.Equal(pluginsBefore, Snapshot(At("plugins/Some-Full")));
            Assert.Equal(patchersBefore, Snapshot(At("patchers/Some-Full")));
            Assert.Equal(monomodBefore, Snapshot(At("monomod/Some-Full")));
            Assert.Equal("the host's own settings", File.ReadAllText(At("config/Some.Full.cfg")));
            Assert.False(File.Exists(At("config/Some.Full.Extra.cfg")), "a config file the failed install wrote new was left behind");
            Assert.Equal("1.0.0", Scan("Some-Full").InstalledVersion);
        }

        [Fact]
        public async Task A_brand_new_install_that_fails_leaves_no_folder_and_no_new_config()
        {
            var service = Service(FullPackage("1.0.0"));
            var written = 0;
            service.PlacingFile = _ => { if (++written == 5) throw new IOException("stopped"); };

            var result = await service.InstallFromThunderstoreAsync(Pinned("Some", "Full", "1.0.0"), Plugins);

            Assert.False(result.Installed);
            Assert.False(Directory.Exists(At("plugins/Some-Full")));
            Assert.False(Directory.Exists(At("patchers/Some-Full")));
            Assert.False(Directory.Exists(At("monomod/Some-Full")));
            Assert.False(File.Exists(At("config/Some.Full.cfg")));
        }

        // ---- an old nested install is replaced by the managed one ----

        [Fact]
        public async Task A_pinned_reinstall_replaces_an_old_nested_layout()
        {
            var old = At("plugins/Some-Full");
            Directory.CreateDirectory(Path.Combine(old, "plugins"));
            Directory.CreateDirectory(Path.Combine(old, "config"));
            File.WriteAllText(Path.Combine(old, "manifest.json"), Manifest("Full", "1.0.0"));
            File.WriteAllText(Path.Combine(old, "plugins", "Full.dll"), "old nested");
            File.WriteAllText(Path.Combine(old, "config", "Some.Full.cfg"), "buried, never read");
            Assert.Equal("nested", Scan("Some-Full").Layout);

            var result = await Service(FullPackage("1.0.0")).InstallFromThunderstoreAsync(Pinned("Some", "Full", "1.0.0"), Plugins);

            Assert.True(result.Installed, result.Error);
            Assert.False(Directory.Exists(Path.Combine(old, "plugins")));
            Assert.False(Directory.Exists(Path.Combine(old, "config")));
            Assert.Equal("plugin 1.0.0", File.ReadAllText(Path.Combine(old, "Full.dll")));
            Assert.Equal("managed", Scan("Some-Full").Layout);

            // The old copy is kept, outside plugins, where BepInEx never loads it.
            var backups = Path.Combine(Bep, ".bakaloader-mod-backups", "Some-Full");
            Assert.True(Directory.EnumerateFiles(backups, "Full.dll", SearchOption.AllDirectories).Any());
        }

        // ---- what counts as nested: a plugin file a level too deep, never a data folder ----

        [Theory]
        [InlineData("config/Some.Full.cfg", "managed")]
        [InlineData("config/deeper/list.yaml", "managed")]
        [InlineData("plugins/translations/English.json", "managed")]
        [InlineData("BepInEx/config/Some.Full.cfg", "managed")]
        [InlineData("patchers/readme.txt", "managed")]
        [InlineData("Full.dll", "managed")]
        [InlineData("Assets/Full.dll", "managed")]
        [InlineData("plugins/Full.dll", "nested")]
        [InlineData("Plugins/Full.dll", "nested")]
        [InlineData("plugins/Pets/Full.dll", "nested")]
        [InlineData("patchers/Full.Patcher.dll.old", "nested")]
        [InlineData("BepInEx/plugins/Full.dll", "nested")]
        [InlineData("BepInEx/patchers/deep/Full.Patcher.DLL", "nested")]
        public void Layout_counts_a_package_nested_only_for_a_plugin_file_under_a_nested_loader_folder(string file, string expected)
        {
            var folder = At("plugins/Some-Full");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), Manifest("Full", "1.0.0"));
            var path = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");

            Assert.Equal(expected, ModScanner.LayoutOf(folder));
            Assert.Equal(expected, Scan("Some-Full").Layout);
        }

        [Fact]
        public void Layout_of_an_empty_nested_folder_is_managed()
        {
            var folder = At("plugins/Some-Full");
            Directory.CreateDirectory(Path.Combine(folder, "plugins"));
            Directory.CreateDirectory(Path.Combine(folder, "BepInEx", "config"));
            File.WriteAllText(Path.Combine(folder, "manifest.json"), Manifest("Full", "1.0.0"));

            Assert.Equal("managed", ModScanner.LayoutOf(folder));
        }

        // ---- the update path keeps a switched off mod switched off ----

        [Fact]
        public async Task An_update_of_a_switched_off_mod_leaves_it_switched_off()
        {
            await Service(FullPackage("1.0.0")).InstallFromThunderstoreAsync(Pinned("Some", "Full", "1.0.0"), Plugins);
            Assert.True(ModSwitch.Disable(Scan("Some-Full"), Mock.Of<IApplicationLogger>()).Ok);
            Assert.True(Scan("Some-Full").Disabled);

            var latest = new ThunderstorePackage
            {
                Namespace = "Some",
                Name = "Full",
                Latest = new ThunderstorePackageVersion
                {
                    VersionNumber = "1.1.0",
                    DownloadUrl = "https://thunderstore.io/package/download/Some/Full/1.1.0/",
                },
            };
            var site = new Mock<IThunderstoreClient>();
            site.Setup(c => c.LookupLiveAsync("Some", "Full"))
                .ReturnsAsync(new ThunderstoreLiveLookup { Answered = true, Package = latest });
            site.Setup(c => c.GetLatestAsync("Some", "Full")).ReturnsAsync(latest);

            var result = await Service(FullPackage("1.1.0"), site).UpdateModAsync(Scan("Some-Full"));

            Assert.True(result.Updated, result.Error);
            var row = Scan("Some-Full");
            Assert.Equal("1.1.0", row.InstalledVersion);
            Assert.True(row.Disabled, "the update switched the mod back on");
            Assert.Equal("plugin 1.1.0", File.ReadAllText(At("plugins/Some-Full/Full.dll.old")));
            Assert.Equal("patcher 1.1.0", File.ReadAllText(At("patchers/Some-Full/FullPatcher.dll.old")));
            Assert.False(File.Exists(At("plugins/Some-Full/Full.dll")));
            Assert.Equal("thunderstore", row.InstalledSource);
        }

        // ---- Hexium goes through the same placement ----

        [Fact]
        public async Task A_Hexium_install_is_laid_out_the_same_way_and_keeps_its_own_note()
        {
            var zip = FullPackage("1.2.3");
            var plan = new HexiumInstallPlan
            {
                Owner = "Some",
                Name = "Full",
                Version = "1.2.3",
                DownloadUrl = "https://cdn.hexium.gg/upload/1255/1.2.3.zip",
                FileSize = zip.Length,
            };

            var result = await Service(zip).InstallFromHexiumAsync(plan, Plugins);

            Assert.True(result.Installed, result.Error);
            Assert.True(File.Exists(At("patchers/Some-Full/FullPatcher.dll")));
            Assert.True(File.Exists(At("config/Some.Full.cfg")));
            var row = Scan("Some-Full");
            Assert.Equal("hexium", row.InstalledSource);
            Assert.True(row.IsHexiumInstalled);
        }

        // ---- acceptance 2: a patcher package in an isolated realm never reaches the base ----

        [Fact]
        public async Task A_patcher_package_in_an_isolated_realm_lands_in_that_realm_and_not_in_the_base()
        {
            var basePatchers = Path.Combine(Root, "base", "BepInEx", "patchers");
            Directory.CreateDirectory(basePatchers);
            File.WriteAllText(Path.Combine(basePatchers, "BaseOwn.dll"), "the base install's patcher");

            // The realm the way every isolated realm was provisioned before 1.2.15.
            InstallIsolationService.CreateJunctionOrThrow(At("patchers"), basePatchers);
            Assert.True(BepInExService.IsReparsePoint(At("patchers")));

            var zip = Zip(new Dictionary<string, string>
            {
                ["manifest.json"] = Manifest("EarlyFix", "1.0.0"),
                ["patchers/EarlyFix.dll"] = "a patcher",
            });

            var result = await Service(zip).InstallFromThunderstoreAsync(Pinned("Some", "EarlyFix", "1.0.0"), Plugins);

            Assert.True(result.Installed, result.Error);
            Assert.False(BepInExService.IsReparsePoint(At("patchers")), "the realm still writes through the junction");
            Assert.True(File.Exists(At("patchers/Some-EarlyFix/EarlyFix.dll")));
            Assert.True(File.Exists(At("patchers/BaseOwn.dll")), "the realm lost what the base's patchers showed it");

            Assert.False(Directory.Exists(Path.Combine(basePatchers, "Some-EarlyFix")), "the patcher was written into the base install");
            Assert.Equal(new[] { "BaseOwn.dll" }, Directory.EnumerateFileSystemEntries(basePatchers).Select(Path.GetFileName));
        }

        [Fact]
        public async Task A_package_that_writes_into_a_shared_core_is_refused_before_anything_moves()
        {
            var baseCore = Path.Combine(Root, "base", "BepInEx", "core");
            Directory.CreateDirectory(baseCore);
            File.WriteAllText(Path.Combine(baseCore, "BepInEx.dll"), "loader");
            InstallIsolationService.CreateJunctionOrThrow(At("core"), baseCore);

            var zip = Zip(new Dictionary<string, string>
            {
                ["manifest.json"] = Manifest("CoreThing", "1.0.0"),
                ["core/CoreThing.dll"] = "would load in every realm",
            });

            var result = await Service(zip).InstallFromThunderstoreAsync(Pinned("Some", "CoreThing", "1.0.0"), Plugins);

            Assert.False(result.Installed);
            Assert.Contains("BepInEx/core", result.Error);
            Assert.False(Directory.Exists(At("plugins/Some-CoreThing")));
            Assert.Equal(new[] { "BepInEx.dll" }, Directory.EnumerateFileSystemEntries(baseCore).Select(Path.GetFileName));
        }

        // ---- acceptance 4: removal takes the package's folders and never the config ----

        [Fact]
        public async Task A_removal_takes_every_package_folder_and_leaves_config_alone()
        {
            await Service(FullPackage("1.0.0")).InstallFromThunderstoreAsync(Pinned("Some", "Full", "1.0.0"), Plugins);
            Directory.CreateDirectory(At("core/Some-Full"));
            File.WriteAllText(At("core/Some-Full/CoreBit.dll"), "core bit");

            var removal = new ModRemovalService(Mock.Of<IApplicationLogger>()).RemoveMod(Scan("Some-Full"), includeConfig: false);

            Assert.True(removal.Removed, removal.Error);
            Assert.False(Directory.Exists(At("plugins/Some-Full")));
            Assert.False(Directory.Exists(At("patchers/Some-Full")));
            Assert.False(Directory.Exists(At("monomod/Some-Full")));
            Assert.False(Directory.Exists(At("core/Some-Full")));
            Assert.Equal("shipped 1.0.0", File.ReadAllText(At("config/Some.Full.cfg")));
            Assert.Empty(removal.DeletedConfigFiles);
        }
    }
}
