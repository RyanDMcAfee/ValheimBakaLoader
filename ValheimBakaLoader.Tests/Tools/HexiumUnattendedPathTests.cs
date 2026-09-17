using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The promise that matters most: a mod the host deliberately took from the second
    /// site is never quietly replaced by the Thunderstore build, least of all while
    /// nobody is at the keyboard.
    /// <para>
    /// This drives the real chain rather than a stand-in for it. A temporary plugins
    /// folder is scanned by the real <see cref="ModScanner"/>, which reads the real note;
    /// the count the empty-server hook takes is the same
    /// <see cref="InstalledMod.UpdateAvailable"/> expression the hook uses; and the real
    /// <see cref="ModUpdateService"/> is then handed the whole list to prove it refuses
    /// the row even when it is asked to take it. The source gates at the end hold the
    /// bridge to the same predicate, because what they guard against is a line somebody
    /// adds later.
    /// </para>
    /// <para>
    /// Everything runs under a temporary folder. No real BepInEx tree is touched, and the
    /// transport throws if anything tries to reach the network.
    /// </para>
    /// </summary>
    public class HexiumUnattendedPathTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "bakaloader-unattended-" + Guid.NewGuid().ToString("N"));
        private readonly string Plugins;

        public HexiumUnattendedPathTests()
        {
            Plugins = Path.Combine(Root, "BepInEx", "plugins");
            Directory.CreateDirectory(Plugins);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }

        private string InstallMod(string folderName, string version, bool fromHexium)
        {
            var dir = Path.Combine(Plugins, folderName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"name\":\"" + folderName + "\",\"version_number\":\"" + version + "\"}");
            File.WriteAllText(Path.Combine(dir, "plugin.dll"), "not really a dll");

            if (fromHexium)
            {
                ModSourceMarkerFile.Write(dir, new ModSourceMarker
                {
                    Schema = ModSourceMarkerFile.CurrentSchema,
                    Writer = "BakaLoader 1.1.0",
                    Source = ModSourceMarkerFile.HexiumSource,
                    Owner = folderName.Split('-')[0],
                    Name = folderName.Split('-')[1],
                    Version = version,
                    DownloadUrl = "https://cdn.hexium.gg/upload/1/" + version + ".zip",
                    InstalledUtc = DateTime.UtcNow,
                });
            }

            return dir;
        }

        private static ModScanner Scanner() => new(Mock.Of<IApplicationLogger>());

        /// <summary>
        /// A service whose transport throws, so any attempt to actually fetch something
        /// fails loudly rather than quietly succeeding.
        /// </summary>
        private static ModUpdateService ServiceThatMustNotDownload(IThunderstoreClient thunderstore)
        {
            var provider = new Mock<IHttpClientProvider>();
            provider.Setup(p => p.CreateClient()).Throws(new InvalidOperationException("nothing here may download"));
            return new ModUpdateService(thunderstore, provider.Object, Mock.Of<IApplicationLogger>());
        }

        private static IThunderstoreClient ThunderstoreOffering(string version, string downloadUrl = "https://thunderstore.io/x.zip")
        {
            var client = new Mock<IThunderstoreClient>();
            client.Setup(c => c.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ThunderstorePackage
                {
                    Namespace = "Owner",
                    Name = "Mod",
                    Latest = new ThunderstorePackageVersion { VersionNumber = version, DownloadUrl = downloadUrl },
                });
            return client.Object;
        }

        // --- The scan reads the note, and the count the hook takes leaves the row out ---

        [Fact]
        public void The_scanner_reads_the_note_and_the_waiting_count_leaves_that_row_out()
        {
            InstallMod("shudnal-ExtraSlots", "1.0.20", fromHexium: true);
            InstallMod("JereKuusela-WorldEditCommands", "1.65.0", fromHexium: false);

            var mods = Scanner().ScanPlugins(Plugins);
            foreach (var mod in mods) mod.LatestVersion = "9.9.9";   // Thunderstore is far ahead of both

            var hexium = mods.Single(m => m.FullName == "shudnal-ExtraSlots");
            var ordinary = mods.Single(m => m.FullName == "JereKuusela-WorldEditCommands");

            Assert.Equal("hexium", hexium.InstalledSource);
            Assert.True(hexium.IsHexiumInstalled);
            Assert.Null(ordinary.InstalledSource);

            // This is character for character the expression BlendWindow's
            // GetPendingModUpdateCount hook counts with.
            var pending = mods.Count(m => m.UpdateAvailable);

            Assert.Equal(1, pending);
            Assert.False(hexium.UpdateAvailable);
            Assert.True(ordinary.UpdateAvailable);
        }

        [Fact]
        public void The_apply_hook_picks_up_the_same_rows_the_count_did()
        {
            InstallMod("shudnal-ExtraSlots", "1.0.20", fromHexium: true);
            InstallMod("JereKuusela-WorldEditCommands", "1.65.0", fromHexium: false);

            var mods = Scanner().ScanPlugins(Plugins);
            foreach (var mod in mods) mod.LatestVersion = "9.9.9";

            // And this is the expression ApplyModUpdates selects with.
            var updatable = mods.Where(m => m.UpdateAvailable).ToList();

            Assert.Single(updatable);
            Assert.Equal("JereKuusela-WorldEditCommands", updatable[0].FullName);
        }

        // --- And the service refuses even when it is handed the row anyway ---

        [Fact]
        public async Task The_update_service_refuses_a_hexium_row_handed_to_it_directly()
        {
            var dir = InstallMod("shudnal-ExtraSlots", "1.0.20", fromHexium: true);
            var before = Directory.GetFiles(dir).Length;

            var mods = Scanner().ScanPlugins(Plugins);
            var mod = mods.Single();
            mod.LatestVersion = "9.9.9";

            var service = ServiceThatMustNotDownload(ThunderstoreOffering("9.9.9"));

            // Every mod, not just the ones the hook selected, so the refusal has to come
            // from the service and not from the caller's filter.
            var results = await service.UpdateModsAsync(mods);

            Assert.Single(results);
            Assert.False(results[0].Updated);
            Assert.Null(results[0].Error);                       // skipped, not failed
            Assert.Equal("1.0.20", results[0].ToVersion);
            Assert.Equal("1.0.20", mod.InstalledVersion);        // and nothing was rewritten
            Assert.Equal(before, Directory.GetFiles(dir).Length);
            Assert.Equal("hexium", ModSourceMarkerFile.ReadTrustedSource(dir, "1.0.20"));
        }

        [Fact]
        public async Task An_ordinary_row_still_goes_through_the_update_path_as_it_always_did()
        {
            InstallMod("JereKuusela-WorldEditCommands", "1.65.0", fromHexium: false);

            var mods = Scanner().ScanPlugins(Plugins);
            mods[0].LatestVersion = "1.66.0";

            var service = ServiceThatMustNotDownload(ThunderstoreOffering("1.66.0"));
            var results = await service.UpdateModsAsync(mods);

            // It is refused by the transport, not by any Hexium rule: the point is that
            // it got as far as trying, which the Hexium row never does.
            Assert.Single(results);
            Assert.False(results[0].Updated);
            Assert.NotNull(results[0].Error);
            Assert.Contains("nothing here may download", results[0].Error);
        }

        [Fact]
        public async Task A_note_that_stopped_matching_the_manifest_puts_the_row_back_in_the_ordinary_path()
        {
            // Somebody replaced the folder by hand after BakaLoader wrote the note, so the
            // note no longer describes these files and is no longer believed.
            var dir = InstallMod("shudnal-ExtraSlots", "1.0.20", fromHexium: true);
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"name\":\"ExtraSlots\",\"version_number\":\"1.0.21\"}");

            var mods = Scanner().ScanPlugins(Plugins);
            mods[0].LatestVersion = "1.0.22";

            Assert.Null(mods[0].InstalledSource);
            Assert.True(mods[0].UpdateAvailable);

            var service = ServiceThatMustNotDownload(ThunderstoreOffering("1.0.22"));
            var results = await service.UpdateModsAsync(mods);
            Assert.NotNull(results[0].Error);   // it tried, which is the whole point
        }

        // --- The bridge is held to the same predicate ---

        [Fact]
        public void The_empty_server_hooks_still_read_the_one_flag_and_nothing_else()
        {
            var source = BridgeSource();
            var hooks = Between(source, "private void WireModUpdateHooks", "private void WireRelaunchSettings");

            Assert.Contains("m.UpdateAvailable", hooks);
            Assert.Contains("GetPendingModUpdateCount", hooks);
            Assert.Contains("ApplyModUpdates", hooks);

            // The unattended path must never learn about the second site. Anything here
            // that names it would be a way for an unattended restart to reach it.
            foreach (var forbidden in new[] { "Hexium", "hexium" })
            {
                Assert.False(hooks.Contains(forbidden, StringComparison.Ordinal),
                    "the unattended mod-update hooks must never name " + forbidden);
            }
        }

        [Fact]
        public void The_bulk_update_still_selects_on_the_one_flag()
        {
            var source = BridgeSource();

            // "Update all" and the empty-server apply share the expression, so a row the
            // count leaves out is a row the run leaves out.
            Assert.Contains("mods.Where(m => m.UpdateAvailable)", source);
            Assert.Contains("mods?.Where(m => m.UpdateAvailable)", source);
            Assert.Contains("mods?.Count(m => m.UpdateAvailable)", source);
        }

        [Fact]
        public void The_scan_reads_the_switch_and_hands_it_to_the_step_that_acts_on_it()
        {
            // What the switch MEANS is proved by driving the step itself, in
            // HexiumSourceSwitchTests. This only holds the bridge to reading the switch and
            // passing it on, and to treating an unreadable preferences file as off.
            var method = Between(BridgeSource(), "private async Task AddHexiumVersionsAsync", "private void RecordModManifest");

            Assert.Contains("UseHexiumSource", method);
            Assert.Contains("HexiumScan.ApplyAsync(mods, enabled)", method);
            Assert.Contains("catch { return; }", method);
        }

        [Fact]
        public void Only_the_hexium_reader_and_its_link_rules_hold_an_address_for_the_site()
        {
            // An address for the site (the host followed by a path) lives in exactly two
            // files, and neither reaches out until it is called. That is what makes "the
            // switch off means no connection at all" a property of the tree rather than a
            // promise: everywhere else can only name the site in a sentence to the host.
            var allowed = new[] { "HexiumClient.cs", "HexiumUrlParser.cs" };

            var offenders = AppSourceTree.Files()
                .Where(f => f.Value.Contains("hexium.gg/", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Key)
                .Where(f => !allowed.Contains(f))
                .ToList();

            Assert.True(offenders.Count == 0,
                "these files hold an address on hexium.gg and should not: " + string.Join(", ", offenders));
        }

        private static string Between(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            if (start < 0) return "";
            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            return end < 0 ? source.Substring(start) : source.Substring(start, end - start);
        }

        private static string BridgeSource() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];
    }
}
