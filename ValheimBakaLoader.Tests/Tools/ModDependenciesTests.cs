using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
    /// What a mod needs that a realm does not give it, from 1.2.16.
    /// <para>
    /// The trigger: MySix_ForgeOfPotential was installed by link without MySix_Core, BepInEx
    /// logged "Could not load ... missing dependencies: com.mysix.core", and nothing in
    /// BakaLoader said so. These hold the resolver to its rules (the minimum version kept, the
    /// BepInEx pack, older and switched off told apart from missing, the chain walked once
    /// through a cycle), the client to reading the dependency lists the site publishes, and the
    /// install to handing them back for a latest link and a pinned one. Every answer comes from
    /// a mock or from <see cref="RecordingHttpHandler"/>; nothing reaches thunderstore.io, and
    /// every folder is a temporary one.
    /// </para>
    /// </summary>
    public class ModDependenciesTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "bakaloader-deps-" + Guid.NewGuid().ToString("N"));
        private readonly string Plugins;

        public ModDependenciesTests()
        {
            Plugins = Path.Combine(Root, "BepInEx", "plugins");
            Directory.CreateDirectory(Plugins);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }

        private static InstalledMod Mod(string fullName, string version, bool off = false, params string[] needs)
        {
            var hyphen = fullName.IndexOf('-');
            return new InstalledMod
            {
                Author = fullName.Substring(0, hyphen),
                ModName = fullName.Substring(hyphen + 1),
                InstalledVersion = version,
                Disabled = off,
                DeclaredDependencies = needs,
                Dependencies = ModScanner.ParseDependencyFullNames(needs),
            };
        }

        private static Func<string, Task<IEnumerable<string>>> Site(Dictionary<string, string[]> latest, List<string> asked = null) =>
            name =>
            {
                asked?.Add(name);
                return Task.FromResult<IEnumerable<string>>(latest.TryGetValue(name, out var list) ? list : null);
            };

        // ------------------------------------------------------------------ parsing

        [Theory]
        [InlineData("MySix-MySix_Core-1.0.2", "MySix-MySix_Core", "1.0.2")]
        [InlineData("  denikson-BepInExPack_Valheim-5.4.2350 ", "denikson-BepInExPack_Valheim", "5.4.2350")]
        [InlineData("ValheimModding-Jotunn", "ValheimModding-Jotunn", null)]
        public void An_entry_keeps_its_minimum_version(string entry, string fullName, string min)
        {
            Assert.True(ModDependencies.TryParse(entry, out var r));
            Assert.Equal(fullName, r.FullName);
            Assert.Equal(min, r.MinVersion);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("NoHyphen")]
        [InlineData("Owner-")]
        [InlineData("-1.0.0")]
        public void An_entry_that_names_no_package_is_refused(string entry)
        {
            Assert.False(ModDependencies.TryParse(entry, out _));
        }

        [Fact]
        public void The_same_package_twice_keeps_the_higher_minimum()
        {
            var list = ModDependencies.Parse(new[] { "A-Core-1.2.0", "a-core-1.10.0", "A-Core-1.3.0", "B-Lib-2.0.0" });
            Assert.Equal(new[] { "A-Core", "B-Lib" }, list.Select(r => r.FullName));
            Assert.Equal("1.10.0", list[0].MinVersion);
        }

        // ------------------------------------------------------------------ one level, the scan's rules

        [Fact]
        public void A_dependency_that_is_there_on_and_new_enough_is_not_reported()
        {
            var installed = new[] { Mod("MySix-MySix_Core", "1.0.5") };
            Assert.Empty(ModDependencies.Check(new[] { "MySix-MySix_Core-1.0.2" }, installed, true));
        }

        [Fact]
        public void A_missing_one_an_older_one_and_a_switched_off_one_are_each_told_apart()
        {
            var installed = new[]
            {
                Mod("ValheimModding-Jotunn", "2.19.1"),
                Mod("Azumatt-AzuCraftyBoxes", "1.8.4", off: true),
            };

            var needs = ModDependencies.Check(
                new[] { "MySix-MySix_Core-1.0.2", "ValheimModding-Jotunn-2.20.0", "Azumatt-AzuCraftyBoxes-1.8.0" },
                installed, true, "MySix-MySix_ForgeOfPotential");

            Assert.Equal(new[] { "missing", "older", "off" }, needs.Select(n => n.State));
            Assert.Equal("MySix_Core", needs[0].Name);
            Assert.Equal("1.0.2", needs[0].MinVersion);
            Assert.Null(needs[0].InstalledVersion);
            Assert.Equal("2.19.1", needs[1].InstalledVersion);
            Assert.Equal("2.20.0", needs[1].MinVersion);
            Assert.Equal("1.8.4", needs[2].InstalledVersion);
            Assert.All(needs, n => Assert.Equal("MySix-MySix_ForgeOfPotential", n.RequiredBy));
        }

        [Fact]
        public void A_match_ignores_case_and_a_copy_with_no_version_is_good_enough()
        {
            var installed = new[] { Mod("mysix-mysix_core", "unknown") };
            Assert.Empty(ModDependencies.Check(new[] { "MySix-MySix_Core-9.0.0" }, installed, true));
        }

        [Fact]
        public void The_BepInEx_pack_is_there_when_BepInEx_is_and_missing_when_it_is_not()
        {
            var declared = new[] { "denikson-BepInExPack_Valheim-5.4.2350" };
            Assert.Empty(ModDependencies.Check(declared, Array.Empty<InstalledMod>(), bepInExSatisfied: true));

            var without = ModDependencies.Check(declared, Array.Empty<InstalledMod>(), bepInExSatisfied: false);
            Assert.Equal("denikson-BepInExPack_Valheim", Assert.Single(without).FullName);
            Assert.Equal("missing", without[0].State);
        }

        [Fact]
        public void The_scan_check_goes_one_level_and_asks_nobody()
        {
            // A dependency that is there and itself needs something missing is that
            // dependency's row, not this one's.
            var installed = new[] { Mod("A-Core", "1.0.0", false, "B-Deep-1.0.0") };
            Assert.Empty(ModDependencies.Check(new[] { "A-Core-1.0.0" }, installed, true));
        }

        [Fact]
        public void A_mod_that_lists_itself_does_not_need_itself()
        {
            var installed = new[] { Mod("A-Self", "1.0.0") };
            Assert.Empty(ModDependencies.Check(new[] { "A-Self-2.0.0" }, installed, true, "A-Self"));
        }

        // ------------------------------------------------------------------ the whole chain

        [Fact]
        public async Task The_chain_is_followed_through_the_site_for_what_is_not_on_disk()
        {
            var asked = new List<string>();
            var needs = await ModDependencies.ResolveAsync("MySix-MySix_ForgeOfPotential",
                new[] { "denikson-BepInExPack_Valheim-5.4.2350", "MySix-MySix_Core-1.0.2" },
                Array.Empty<InstalledMod>(), true,
                Site(new Dictionary<string, string[]>
                {
                    ["MySix-MySix_Core"] = new[] { "denikson-BepInExPack_Valheim-5.4.2350", "ValheimModding-Jotunn-2.20.0" },
                    ["ValheimModding-Jotunn"] = new[] { "denikson-BepInExPack_Valheim-5.4.2202" },
                }, asked));

            Assert.Equal(new[] { "MySix-MySix_Core", "ValheimModding-Jotunn" }, needs.Select(n => n.FullName));
            Assert.Equal("MySix-MySix_Core", needs[1].RequiredBy);
            // The pack is never asked about: it is not a mod, and BepInEx is there.
            Assert.Equal(new[] { "MySix-MySix_Core", "ValheimModding-Jotunn" }, asked);
        }

        [Fact]
        public async Task A_dependency_on_disk_is_followed_through_its_own_manifest_not_the_site()
        {
            var asked = new List<string>();
            var installed = new[] { Mod("MySix-MySix_Core", "1.0.2", false, "ValheimModding-Jotunn-2.20.0") };

            var needs = await ModDependencies.ResolveAsync("MySix-MySix_ForgeOfPotential",
                new[] { "MySix-MySix_Core-1.0.2" }, installed, true,
                Site(new Dictionary<string, string[]>(), asked));

            Assert.Equal("ValheimModding-Jotunn", Assert.Single(needs).FullName);
            Assert.Equal(new[] { "ValheimModding-Jotunn" }, asked);
        }

        [Fact]
        public async Task A_cycle_is_walked_once_and_ends()
        {
            var asked = new List<string>();
            var needs = await ModDependencies.ResolveAsync("A-Root", new[] { "B-One-1.0.0" }, Array.Empty<InstalledMod>(), true,
                Site(new Dictionary<string, string[]>
                {
                    ["B-One"] = new[] { "C-Two-1.0.0", "A-Root-1.0.0" },
                    ["C-Two"] = new[] { "B-One-1.0.0" },
                }, asked));

            Assert.Equal(new[] { "B-One", "C-Two" }, needs.Select(n => n.FullName));
            Assert.Equal(new[] { "B-One", "C-Two" }, asked);
        }

        [Fact]
        public async Task A_cycle_through_mods_on_disk_ends_too()
        {
            var installed = new[]
            {
                Mod("B-One", "1.0.0", false, "C-Two-1.0.0"),
                Mod("C-Two", "1.0.0", false, "B-One-1.0.0", "D-Missing-1.0.0"),
            };

            var needs = await ModDependencies.ResolveAsync("A-Root", new[] { "B-One-1.0.0" }, installed, true, null);

            Assert.Equal("D-Missing", Assert.Single(needs).FullName);
            Assert.Equal("C-Two", needs[0].RequiredBy);
        }

        [Fact]
        public async Task A_deeper_mod_asking_for_a_newer_build_makes_an_installed_one_older()
        {
            var installed = new[]
            {
                Mod("B-Lib", "1.1.0", false, "C-Core-1.5.0"),
                Mod("C-Core", "1.2.0"),
            };

            var needs = await ModDependencies.ResolveAsync("A-Root", new[] { "B-Lib-1.0.0", "C-Core-1.0.0" }, installed, true, null);

            var core = Assert.Single(needs);
            Assert.Equal("C-Core", core.FullName);
            Assert.Equal("older", core.State);
            Assert.Equal("1.5.0", core.MinVersion);
        }

        [Fact]
        public async Task A_fetch_that_fails_ends_its_branch_and_never_the_walk()
        {
            var needs = await ModDependencies.ResolveAsync("A-Root", new[] { "B-One-1.0.0", "C-Two-1.0.0" }, Array.Empty<InstalledMod>(), true,
                name => name == "B-One" ? throw new HttpRequestException("down") : Task.FromResult<IEnumerable<string>>(new[] { "D-Three-1.0.0" }));

            Assert.Equal(new[] { "B-One", "C-Two", "D-Three" }, needs.Select(n => n.FullName));
        }

        [Fact]
        public async Task A_listing_that_goes_on_forever_is_cut_at_the_budget()
        {
            var asked = new List<string>();
            var needs = await ModDependencies.ResolveAsync("A-Root", new[] { "X-M0-1.0.0" }, Array.Empty<InstalledMod>(), true,
                name =>
                {
                    asked.Add(name);
                    var n = int.Parse(name.Substring(3));
                    return Task.FromResult<IEnumerable<string>>(new[] { "X-M" + (n + 1) + "-1.0.0" });
                }, fetchBudget: 5);

            Assert.Equal(5, asked.Count);
            Assert.Equal(6, needs.Count);
        }

        [Fact]
        public async Task Nothing_declared_is_nothing_needed()
        {
            Assert.Empty(await ModDependencies.ResolveAsync("A-Root", null, null, false, null));
            Assert.Empty(ModDependencies.Check(null, null, false));
        }

        // ------------------------------------------------------------------ the scanner keeps the versions

        [Fact]
        public void The_scan_reads_the_manifest_list_with_its_versions_and_a_hand_copy_has_none()
        {
            var withManifest = Path.Combine(Plugins, "MySix-MySix_ForgeOfPotential");
            Directory.CreateDirectory(withManifest);
            File.WriteAllText(Path.Combine(withManifest, "manifest.json"),
                "{\"name\":\"MySix_ForgeOfPotential\",\"version_number\":\"1.0.0\",\"dependencies\":[\"MySix-MySix_Core-1.0.2\",\" \"]}");
            var hand = Path.Combine(Plugins, "Someone-HandCopy");
            Directory.CreateDirectory(hand);
            File.WriteAllText(Path.Combine(hand, "HandCopy.dll"), "MZ");

            var mods = new ModScanner(Mock.Of<IApplicationLogger>()).ScanPlugins(Plugins);

            Assert.Equal(new[] { "MySix-MySix_Core-1.0.2" }, mods.Single(m => m.ModName == "MySix_ForgeOfPotential").DeclaredDependencies);
            Assert.Empty(mods.Single(m => m.ModName == "HandCopy").DeclaredDependencies);
        }

        // ------------------------------------------------------------------ the client reads what the site lists

        private static (ThunderstoreClient Client, RecordingHttpHandler Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            var provider = new RecordingHttpClientProvider(responder);
            var context = new RestClientContext(new Serilog.LoggerConfiguration().CreateLogger(), provider);
            return (new ThunderstoreClient(context), provider.Handler);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        [Fact]
        public async Task The_package_page_hands_its_latest_dependencies_over()
        {
            // The shape the live package page answered with on 2026-10-20, trimmed.
            var (client, handler) = Client(_ => Json(
                "{\"namespace\":\"MySix\",\"name\":\"MySix_ForgeOfPotential\",\"is_deprecated\":false,\"latest\":{"
                + "\"namespace\":\"MySix\",\"name\":\"MySix_ForgeOfPotential\",\"version_number\":\"1.0.0\","
                + "\"full_name\":\"MySix-MySix_ForgeOfPotential-1.0.0\",\"dependencies\":[\"denikson-BepInExPack_Valheim-5.4.2350\",\"MySix-MySix_Core-1.0.2\"],"
                + "\"download_url\":\"https://thunderstore.io/package/download/MySix/MySix_ForgeOfPotential/1.0.0/\",\"is_active\":true}}"));

            var lookup = await client.LookupLiveAsync("MySix", "MySix_ForgeOfPotential");

            Assert.Equal(new[] { "denikson-BepInExPack_Valheim-5.4.2350", "MySix-MySix_Core-1.0.2" }, lookup.Package.Latest.Dependencies);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task A_pinned_version_is_asked_of_its_own_page_once()
        {
            var (client, handler) = Client(request =>
                request.RequestUri!.ToString() == "https://thunderstore.io/api/experimental/package/MySix/MySix_ForgeOfPotential/1.0.0/"
                    ? Json("{\"namespace\":\"MySix\",\"name\":\"MySix_ForgeOfPotential\",\"version_number\":\"1.0.0\","
                        + "\"dependencies\":[\"MySix-MySix_Core-1.0.2\"],\"is_active\":true}")
                    : new HttpResponseMessage(HttpStatusCode.NotFound));

            var version = await client.LookupVersionAsync("MySix", "MySix_ForgeOfPotential", "1.0.0");

            Assert.Equal("1.0.0", version.VersionNumber);
            Assert.Equal(new[] { "MySix-MySix_Core-1.0.2" }, version.Dependencies);
            Assert.Equal(new[] { "https://thunderstore.io/api/experimental/package/MySix/MySix_ForgeOfPotential/1.0.0/" }, handler.Requests);
        }

        [Fact]
        public async Task A_pinned_version_the_site_does_not_have_is_null_and_never_a_throw()
        {
            var (missing, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            Assert.Null(await missing.LookupVersionAsync("MySix", "MySix_ForgeOfPotential", "9.9.9"));

            var (broken, _) = Client(_ => Json("not json"));
            Assert.Null(await broken.LookupVersionAsync("MySix", "MySix_ForgeOfPotential", "1.0.0"));

            var (down, handler) = Client(_ => throw new HttpRequestException("no route"));
            Assert.Null(await down.LookupVersionAsync("MySix", "MySix_ForgeOfPotential", "1.0.0"));

            var (blank, blankHandler) = Client(_ => Json("{}"));
            Assert.Null(await blank.LookupVersionAsync("MySix", "MySix_ForgeOfPotential", " "));
            Assert.Empty(blankHandler.Requests);
        }

        [Fact]
        public void A_listing_entry_carries_its_dependencies_through_the_shared_model()
        {
            var version = Newtonsoft.Json.JsonConvert.DeserializeObject<ThunderstorePackageVersion>(
                "{\"version_number\":\"1.0.0\",\"dependencies\":[\"A-B-1.0.0\"]}");
            Assert.Equal(new[] { "A-B-1.0.0" }, version.Dependencies);
            Assert.Null(Newtonsoft.Json.JsonConvert.DeserializeObject<ThunderstorePackageVersion>("{\"version_number\":\"1.0.0\"}").Dependencies);
        }

        // ------------------------------------------------------------------ the install hands them back

        private const string LatestUrl = "https://thunderstore.io/package/download/MySix/MySix_ForgeOfPotential/1.0.0/";

        private static byte[] PackageZip()
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var manifest = new StreamWriter(archive.CreateEntry("manifest.json").Open());
                manifest.Write("{\"name\":\"MySix_ForgeOfPotential\",\"version_number\":\"1.0.0\"}");
            }
            return buffer.ToArray();
        }

        private static RecordingHttpClientProvider Downloads() =>
            new(request => request.RequestUri?.ToString() == LatestUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PackageZip()) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        private static ThunderstoreLiveLookup Live(params string[] needs) => new()
        {
            Answered = true,
            Package = new ThunderstorePackage
            {
                Namespace = "MySix",
                Name = "MySix_ForgeOfPotential",
                Latest = new ThunderstorePackageVersion
                {
                    VersionNumber = "1.0.0",
                    DownloadUrl = LatestUrl,
                    FullName = "MySix-MySix_ForgeOfPotential-1.0.0",
                    Dependencies = needs.ToList(),
                },
            },
        };

        [Fact]
        public async Task A_latest_install_hands_back_what_the_page_listed_and_asks_no_more()
        {
            var site = new Mock<IThunderstoreClient>();
            site.Setup(c => c.LookupLiveAsync("MySix", "MySix_ForgeOfPotential"))
                .ReturnsAsync(Live("denikson-BepInExPack_Valheim-5.4.2350", " MySix-MySix_Core-1.0.2 ", ""));
            var service = new ModUpdateService(site.Object, Downloads(), Mock.Of<IApplicationLogger>());

            var result = await service.InstallFromThunderstoreAsync(
                new ThunderstoreModReference { Owner = "MySix", Name = "MySix_ForgeOfPotential" }, Plugins);

            Assert.True(result.Installed);
            Assert.Equal(new[] { "denikson-BepInExPack_Valheim-5.4.2350", "MySix-MySix_Core-1.0.2" }, result.Dependencies);
            site.Verify(c => c.LookupVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task A_pinned_install_asks_its_version_page_after_the_files_are_in()
        {
            var site = new Mock<IThunderstoreClient>(MockBehavior.Strict);
            site.Setup(c => c.LookupVersionAsync("MySix", "MySix_ForgeOfPotential", "1.0.0"))
                .ReturnsAsync(new ThunderstorePackageVersion { VersionNumber = "1.0.0", Dependencies = new List<string> { "MySix-MySix_Core-1.0.2" } });
            var service = new ModUpdateService(site.Object, Downloads(), Mock.Of<IApplicationLogger>());

            var result = await service.InstallFromThunderstoreAsync(
                new ThunderstoreModReference { Owner = "MySix", Name = "MySix_ForgeOfPotential", Version = "1.0.0" }, Plugins);

            Assert.True(result.Installed);
            Assert.Equal(new[] { "MySix-MySix_Core-1.0.2" }, result.Dependencies);
            site.Verify(c => c.LookupVersionAsync("MySix", "MySix_ForgeOfPotential", "1.0.0"), Times.Once);
        }

        [Fact]
        public async Task A_pinned_install_whose_version_page_fails_still_installs_with_nothing_listed()
        {
            var site = new Mock<IThunderstoreClient>();
            site.Setup(c => c.LookupVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new HttpRequestException("down"));
            var service = new ModUpdateService(site.Object, Downloads(), Mock.Of<IApplicationLogger>());

            var result = await service.InstallFromThunderstoreAsync(
                new ThunderstoreModReference { Owner = "MySix", Name = "MySix_ForgeOfPotential", Version = "1.0.0" }, Plugins);

            Assert.True(result.Installed);
            Assert.NotNull(result.Dependencies);
            Assert.Empty(result.Dependencies);
        }

        [Fact]
        public async Task A_failed_install_asks_nothing_about_dependencies()
        {
            var site = new Mock<IThunderstoreClient>();
            var service = new ModUpdateService(site.Object, Downloads(), Mock.Of<IApplicationLogger>());

            var result = await service.InstallFromThunderstoreAsync(
                new ThunderstoreModReference { Owner = "MySix", Name = "MySix_ForgeOfPotential", Version = "1.0.0" },
                Path.Combine(Root, "no-such-folder"));

            Assert.False(result.Installed);
            Assert.Empty(result.Dependencies);
            site.Verify(c => c.LookupVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
    }
}
