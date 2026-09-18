using System;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The BepInEx rules that decide something before anything is written: which install a
    /// server really belongs to, who is blocked from writing, what an archive has to look
    /// like, whether the loader actually ran, and whether the unattended window should move
    /// it. Every one of them is pure, so every one of them gets a table.
    /// </summary>
    public class BepInExRulesTests
    {
        // ------------------------------------------------------------------ which install

        /// <summary>
        /// The install root is where the exe sits and nothing cleverer. An isolated install
        /// is NOT hoisted here on purpose: a path alone cannot say which of an instances
        /// root's siblings the base is, so the caller resolves that from the profile and
        /// hands it in.
        /// </summary>
        [Fact]
        public void The_install_root_is_the_folder_the_exe_sits_in()
        {
            var isolated = Path.Combine(@"D:\", "steamlibrary", "common", ".bakaloader-instances",
                "Final Sunset", "valheim_server.exe");

            Assert.Equal(
                Path.GetFullPath(Path.Combine(@"D:\", "steamlibrary", "common", ".bakaloader-instances",
                    "Final Sunset")),
                BepInExService.InstallRootOf(isolated));

            var plain = Path.Combine(@"D:\", "steamlibrary", "common", "Valheim dedicated server",
                "valheim_server.exe");

            Assert.Equal(
                Path.GetFullPath(Path.Combine(@"D:\", "steamlibrary", "common", "Valheim dedicated server")),
                BepInExService.InstallRootOf(plain));
        }

        /// <summary>
        /// The key that says which installs load one BepInEx: the instances root a base
        /// install would provision into, and the one an isolated install already lives in.
        /// They are the same string, which is what makes the sharing rule work at all.
        /// </summary>
        [Fact]
        public void A_base_and_the_instances_made_from_it_answer_with_one_key()
        {
            var basePath = Path.Combine(@"D:\", "steamlibrary", "common", "Valheim dedicated server",
                "valheim_server.exe");
            var isolated = Path.Combine(@"D:\", "steamlibrary", "common", ".bakaloader-instances",
                "Final Sunset", "valheim_server.exe");
            var expected = Path.Combine(@"D:\", "steamlibrary", "common", ".bakaloader-instances");

            Assert.Equal(expected, BepInExService.InstallGroupKey(basePath));
            Assert.Equal(expected, BepInExService.InstallGroupKey(isolated));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_path_that_is_not_a_path_has_no_root_and_no_key(string exePath)
        {
            Assert.Null(BepInExService.InstallRootOf(exePath));
            Assert.Null(BepInExService.InstallGroupKey(exePath));
        }

        /// <summary>
        /// Two isolated profiles and the base install itself are all one BepInEx. This is the
        /// fact the whole feature turns on, so it is asserted rather than assumed.
        /// </summary>
        [Fact]
        public void Two_isolated_profiles_and_their_base_all_share_one_bepinex()
        {
            var basePath = Path.Combine(@"D:\", "common", "server", "valheim_server.exe");
            var one = Path.Combine(@"D:\", "common", ".bakaloader-instances", "one", "valheim_server.exe");
            var two = Path.Combine(@"D:\", "common", ".bakaloader-instances", "two", "valheim_server.exe");

            Assert.True(BepInExService.SharesBase(basePath, one));
            Assert.True(BepInExService.SharesBase(basePath, two));
            Assert.True(BepInExService.SharesBase(one, two));
        }

        [Fact]
        public void A_second_install_elsewhere_shares_nothing()
        {
            Assert.False(BepInExService.SharesBase(
                Path.Combine(@"D:\", "common", "server", "valheim_server.exe"),
                Path.Combine(@"E:\", "another", "valheim_server.exe")));
        }

        /// <summary>
        /// The hole this rule was rewritten to close, and the one a path-only key could not
        /// see. A base install whose own parent is not writable (a server under Program Files
        /// with no administrator rights is the ordinary case) has its isolated profiles
        /// provisioned into ONE shared root under LocalApplicationData. Nothing in that path
        /// names the base, so a key derived from "the folder beside me" said the instance and
        /// its base shared nothing at all, while the linking code went on junctioning that
        /// instance's BepInEx/core straight at that very base. The refusal then never fired:
        /// BakaLoader would back up, CLEAR and rewrite the core underneath a live world.
        /// </summary>
        [Fact]
        public void An_instance_in_the_shared_fallback_root_still_belongs_to_the_base()
        {
            var basePath = Path.Combine(@"C:\", "Program Files (x86)", "Steam", "steamapps", "common",
                "Valheim dedicated server", "valheim_server.exe");
            var quiet = Path.Combine(InstallIsolationService.FallbackInstancesRoot, "Quiet", "valheim_server.exe");

            Assert.True(BepInExService.SharesBase(basePath, quiet));
            Assert.True(BepInExService.SharesBase(quiet, basePath));

            var blocked = BepInExService.ProfilesBlockingWrite(basePath, new[] { Install("Quiet", quiet, true) });
            Assert.Equal(new[] { "Quiet" }, blocked);
            Assert.False(BepInExService.IsSafeToWrite(basePath, new[] { Install("Quiet", quiet, true) }));
        }

        /// <summary>
        /// And the direction that must NOT follow from it. Both of these would provision into
        /// the same fallback root if their own parents were unwritable, which does not make
        /// them one install: a rule that said so would refuse every BepInEx write on the
        /// machine whenever any server anywhere happened to be up.
        /// </summary>
        [Fact]
        public void The_shared_fallback_root_does_not_join_two_base_installs()
        {
            var one = Path.Combine(@"C:\", "Program Files (x86)", "Steam", "steamapps", "common",
                "Valheim dedicated server", "valheim_server.exe");
            var two = Path.Combine(@"C:\", "Games", "Another Valheim server", "valheim_server.exe");

            Assert.False(BepInExService.SharesBase(one, two));
            Assert.False(BepInExService.SharesBase(two, one));
            Assert.True(BepInExService.IsSafeToWrite(one, new[] { Install("Elsewhere", two, true) }));
        }

        /// <summary>
        /// A base install names both roots it could provision into, most specific first; an
        /// instance names the one it already sits in and nothing else. The first key is what
        /// <see cref="BepInExService.InstallGroupKey"/> still answers, so everything that read
        /// it reads the same string it always did.
        /// </summary>
        [Fact]
        public void A_base_names_both_roots_it_could_provision_into_and_an_instance_names_one()
        {
            var basePath = Path.Combine(@"D:\", "common", "server", "valheim_server.exe");
            var keys = BepInExService.InstallGroupKeys(basePath);

            Assert.Equal(2, keys.Count);
            Assert.Equal(Path.Combine(@"D:\", "common", ".bakaloader-instances"), keys[0]);
            Assert.True(InstallIsolationService.IsFallbackRoot(keys[1]));
            Assert.Equal(keys[0], BepInExService.InstallGroupKey(basePath));

            var instance = Path.Combine(@"D:\", "common", ".bakaloader-instances", "q", "valheim_server.exe");
            Assert.Equal(new[] { Path.Combine(@"D:\", "common", ".bakaloader-instances") },
                BepInExService.InstallGroupKeys(instance));

            Assert.Empty(BepInExService.InstallGroupKeys(null));
        }

        // ------------------------------------------------------------------ who blocks a write

        private static BepInExProfileInstall Install(string name, string exe, bool running) => new()
        {
            ProfileName = name,
            ServerExePath = exe,
            Running = running,
        };

        /// <summary>
        /// The refusal names the realms that are up, because "stop your servers" with no list
        /// is advice a host with four profiles cannot act on.
        /// </summary>
        [Fact]
        public void A_running_server_on_the_same_install_blocks_the_write_and_is_named()
        {
            var basePath = Path.Combine(@"D:\", "common", "server", "valheim_server.exe");
            var blocked = BepInExService.ProfilesBlockingWrite(basePath, new[]
            {
                Install("Final Sunset", Path.Combine(@"D:\", "common", "server", "valheim_server.exe"), running: true),
                Install("Quiet", Path.Combine(@"D:\", "common", ".bakaloader-instances", "q", "valheim_server.exe"), false),
            });

            Assert.Equal(new[] { "Final Sunset" }, blocked);
            Assert.False(BepInExService.IsSafeToWrite(basePath, new[]
            {
                Install("Final Sunset", Path.Combine(@"D:\", "common", "server", "valheim_server.exe"), running: true),
            }));
        }

        /// <summary>A running server on a DIFFERENT install has nothing to do with this one.</summary>
        [Fact]
        public void A_running_server_on_another_install_does_not_block_anything()
        {
            var basePath = Path.Combine(@"D:\", "common", "server", "valheim_server.exe");

            Assert.True(BepInExService.IsSafeToWrite(basePath, new[]
            {
                Install("Elsewhere", Path.Combine(@"E:\", "other", "valheim_server.exe"), running: true),
            }));
        }

        /// <summary>An isolated profile that is up blocks the base, because it writes through it.</summary>
        [Fact]
        public void An_isolated_profile_that_is_up_blocks_the_base_install()
        {
            var basePath = Path.Combine(@"D:\", "common", "server", "valheim_server.exe");
            var blocked = BepInExService.ProfilesBlockingWrite(basePath, new[]
            {
                Install("Quiet", Path.Combine(@"D:\", "common", ".bakaloader-instances", "q", "valheim_server.exe"), true),
            });

            Assert.Equal(new[] { "Quiet" }, blocked);
        }

        [Fact]
        public void Nothing_running_is_a_safe_write()
        {
            Assert.True(BepInExService.IsSafeToWrite(Path.Combine(@"D:\", "common", "server", "valheim_server.exe"), null));
            Assert.Empty(BepInExService.ProfilesBlockingWrite(Path.Combine(@"D:\", "s", "x.exe"),
                Array.Empty<BepInExProfileInstall>()));
        }

        // ------------------------------------------------------------------ the archive shape

        private static string TwoTier(string root, bool withWinhttp = true, bool withCore = true)
        {
            var pack = Path.Combine(root, "BepInExPack_Valheim");
            Directory.CreateDirectory(Path.Combine(pack, "BepInEx", "core"));
            if (withWinhttp) File.WriteAllText(Path.Combine(pack, "winhttp.dll"), "loader");
            if (withCore) File.WriteAllText(Path.Combine(pack, "BepInEx", "core", "BepInEx.dll"), "core");
            return pack;
        }

        [Fact]
        public void The_pack_shape_is_one_top_folder_with_winhttp_and_a_core_assembly()
        {
            var root = TempDir();
            try
            {
                var pack = TwoTier(root);
                File.WriteAllText(Path.Combine(root, "manifest.json"), "{}");
                File.WriteAllText(Path.Combine(root, "icon.png"), "x");

                Assert.Equal(pack, BepInExService.LoaderRootOf(root));
            }
            finally { Nuke(root); }
        }

        [Fact]
        public void Two_top_level_folders_is_not_a_pack()
        {
            var root = TempDir();
            try
            {
                TwoTier(root);
                Directory.CreateDirectory(Path.Combine(root, "SomethingElse"));

                Assert.Null(BepInExService.LoaderRootOf(root));
            }
            finally { Nuke(root); }
        }

        [Fact]
        public void A_plain_mod_archive_is_not_a_pack()
        {
            var root = TempDir();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "SomeMod"));
                File.WriteAllText(Path.Combine(root, "SomeMod", "SomeMod.dll"), "x");

                Assert.Null(BepInExService.LoaderRootOf(root));
            }
            finally { Nuke(root); }
        }

        [Fact]
        public void A_folder_with_winhttp_but_no_core_assembly_is_not_a_pack()
        {
            var root = TempDir();
            try
            {
                TwoTier(root, withCore: false);
                Assert.Null(BepInExService.LoaderRootOf(root));
            }
            finally { Nuke(root); }
        }

        [Fact]
        public void A_folder_with_a_core_assembly_but_no_loader_dll_is_not_a_pack()
        {
            var root = TempDir();
            try
            {
                TwoTier(root, withWinhttp: false);
                Assert.Null(BepInExService.LoaderRootOf(root));
            }
            finally { Nuke(root); }
        }

        [Fact]
        public void Nothing_at_all_is_not_a_pack()
        {
            Assert.Null(BepInExService.LoaderRootOf(null));
            Assert.Null(BepInExService.LoaderRootOf(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid())));
        }

        // ------------------------------------------------------------------ the address

        /// <summary>
        /// The address is BUILT from an identity, never taken from anything a page or a
        /// listing hands over, so a pinned package cannot be talked into fetching from
        /// somewhere else.
        /// </summary>
        [Fact]
        public void The_download_address_is_built_from_the_package_identity()
        {
            Assert.Equal(
                "https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/5.4.2350/",
                BepInExService.ConstructedDownloadUrl(
                    BepInExService.DefaultPackageOwner, BepInExService.DefaultPackageName, "5.4.2350"));
        }

        // ------------------------------------------------------------------ the wrong location

        [Fact]
        public void A_pack_folder_under_plugins_is_found_and_named()
        {
            var root = TempDir();
            try
            {
                var plugins = Path.Combine(root, "BepInEx", "plugins");
                Directory.CreateDirectory(Path.Combine(plugins, "denikson-BepInExPack_Valheim"));

                Assert.Equal(Path.Combine(plugins, "denikson-BepInExPack_Valheim"),
                    BepInExService.WrongLocationFolderIn(plugins));
            }
            finally { Nuke(root); }
        }

        [Fact]
        public void A_plugins_folder_with_ordinary_mods_has_no_wrong_location()
        {
            var root = TempDir();
            try
            {
                var plugins = Path.Combine(root, "BepInEx", "plugins");
                Directory.CreateDirectory(Path.Combine(plugins, "Azumatt-AzuAntiItemLag"));

                Assert.Null(BepInExService.WrongLocationFolderIn(plugins));
                Assert.Null(BepInExService.WrongLocationFolderIn(null));
            }
            finally { Nuke(root); }
        }

        // ------------------------------------------------------------------ did it load

        private static readonly DateTime Started = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// The whole table. There is no launch flag and no answer coming back on Windows, so
        /// the log's timestamp against the process start is the only evidence there is.
        /// </summary>
        [Theory]
        // not installed: the question does not apply, however long it has been
        [InlineData(false, null, 600, BepInExLoadVerdict.NotInstalled)]
        // the log is this start's: written after the process began
        [InlineData(true, 5, 600, BepInExLoadVerdict.Loaded)]
        // written on the very same tick, which a one second filesystem stamp will do
        [InlineData(true, 0, 600, BepInExLoadVerdict.Loaded)]
        // no log yet and the grace has not passed
        [InlineData(true, null, 30, BepInExLoadVerdict.TooEarly)]
        [InlineData(true, null, 89, BepInExLoadVerdict.TooEarly)]
        // the grace passed with no log at all
        [InlineData(true, null, 90, BepInExLoadVerdict.NotLoaded)]
        [InlineData(true, null, 600, BepInExLoadVerdict.NotLoaded)]
        // a log from the PREVIOUS start is not this start's log
        [InlineData(true, -60, 600, BepInExLoadVerdict.NotLoaded)]
        [InlineData(true, -60, 30, BepInExLoadVerdict.TooEarly)]
        public void The_load_check_reads_the_log_against_the_start(
            bool installed, int? logOffsetSeconds, int secondsSinceStart, BepInExLoadVerdict expected)
        {
            DateTime? log = logOffsetSeconds.HasValue
                ? Started.AddSeconds(logOffsetSeconds.Value)
                : null;

            Assert.Equal(expected, BepInExLoadCheck.Verdict(
                installed, log, Started, Started.AddSeconds(secondsSinceStart)));
        }

        /// <summary>The row only ever goes up for the one verdict worth saying out loud.</summary>
        [Fact]
        public void Only_the_one_verdict_raises_the_row()
        {
            Assert.True(BepInExLoadCheck.DidNotLoad(true, null, Started, Started.AddSeconds(600)));
            Assert.False(BepInExLoadCheck.DidNotLoad(true, null, Started, Started.AddSeconds(10)));
            Assert.False(BepInExLoadCheck.DidNotLoad(false, null, Started, Started.AddSeconds(600)));
            Assert.False(BepInExLoadCheck.DidNotLoad(true, Started.AddSeconds(2), Started, Started.AddSeconds(600)));
        }

        /// <summary>The grace is a parameter, and a test walks both sides of the one it is given.</summary>
        [Fact]
        public void The_grace_is_the_one_it_was_given()
        {
            Assert.Equal(BepInExLoadVerdict.TooEarly,
                BepInExLoadCheck.Verdict(true, null, Started, Started.AddSeconds(9), graceSeconds: 10));
            Assert.Equal(BepInExLoadVerdict.NotLoaded,
                BepInExLoadCheck.Verdict(true, null, Started, Started.AddSeconds(10), graceSeconds: 10));
        }

        // ------------------------------------------------------------------ the unattended window

        [Theory]
        // the switch is off: BakaLoader never writes BepInEx on its own
        [InlineData(false, true, "5.4.2333", "5.4.2350", false, BepInExUnattendedAction.Skip)]
        // nothing installed: that belongs to the start path, not to this window
        [InlineData(true, false, null, "5.4.2350", false, BepInExUnattendedAction.Skip)]
        // the site did not answer, so there is no newer version to be sure about
        [InlineData(true, true, "5.4.2333", null, false, BepInExUnattendedAction.Skip)]
        // already current
        [InlineData(true, true, "5.4.2350", "5.4.2350", false, BepInExUnattendedAction.Skip)]
        // ahead of the listing, which a hand install can be
        [InlineData(true, true, "5.4.2351", "5.4.2350", false, BepInExUnattendedAction.Skip)]
        // a newer pack and nothing else up: write it
        [InlineData(true, true, "5.4.2333", "5.4.2350", false, BepInExUnattendedAction.Apply)]
        // no note at all: first adoption of an install somebody else made
        [InlineData(true, true, null, "5.4.2350", false, BepInExUnattendedAction.Apply)]
        // a newer pack and another realm on this install is up: wait
        [InlineData(true, true, "5.4.2333", "5.4.2350", true, BepInExUnattendedAction.Defer)]
        [InlineData(true, true, null, "5.4.2350", true, BepInExUnattendedAction.Defer)]
        // already current with another up: still nothing to do, and no row raised
        [InlineData(true, true, "5.4.2350", "5.4.2350", true, BepInExUnattendedAction.Skip)]
        public void The_unattended_window_knows_when_to_write_and_when_to_wait(
            bool maintained, bool installed, string installedVersion, string latest,
            bool othersRunning, BepInExUnattendedAction expected)
        {
            Assert.Equal(expected, BepInExUnattended.Decide(
                maintained, installed, installedVersion, latest, othersRunning));
        }

        // ------------------------------------------------------------------ the marker

        [Fact]
        public void A_note_written_by_anybody_else_is_no_note()
        {
            var root = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(root, BepInExMarkerFile.FileName),
                    "{\"schema\":1,\"writer\":\"SomeOtherManager\",\"package\":\"denikson-BepInExPack_Valheim\",\"version\":\"5.4.2350\"}");

                Assert.Null(BepInExMarkerFile.Read(root));
            }
            finally { Nuke(root); }
        }

        [Fact]
        public void A_note_from_a_shape_this_build_does_not_know_is_no_note()
        {
            var root = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(root, BepInExMarkerFile.FileName),
                    "{\"schema\":99,\"writer\":\"BakaLoader 1.2.0\",\"package\":\"p\",\"version\":\"1\"}");

                Assert.Null(BepInExMarkerFile.Read(root));
            }
            finally { Nuke(root); }
        }

        [Fact]
        public void A_note_that_will_not_parse_is_no_note()
        {
            var root = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(root, BepInExMarkerFile.FileName), "not json at all");
                Assert.Null(BepInExMarkerFile.Read(root));
                Assert.Null(BepInExMarkerFile.Read(null));
            }
            finally { Nuke(root); }
        }

        [Fact]
        public void A_note_this_build_wrote_round_trips()
        {
            var root = TempDir();
            try
            {
                BepInExMarkerFile.Write(root, new BepInExMarker
                {
                    Schema = BepInExMarkerFile.CurrentSchema,
                    Writer = "BakaLoader 1.2.0",
                    Package = BepInExService.DefaultPackage,
                    Version = "5.4.2350",
                    InstalledUtc = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc),
                    Source = "https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/5.4.2350/",
                    Files = { new BepInExMarkerFileEntry { Path = "winhttp.dll", Sha256 = "abc" } },
                });

                var read = BepInExMarkerFile.Read(root);

                Assert.NotNull(read);
                Assert.Equal("5.4.2350", read.Version);
                Assert.Equal(BepInExService.DefaultPackage, read.Package);
                Assert.Equal("winhttp.dll", Assert.Single(read.Files).Path);
            }
            finally { Nuke(root); }
        }

        // ------------------------------------------------------------------ the allow list itself

        /// <summary>
        /// The four loose files and nothing else. Written down as a test because the whole
        /// safety of unpacking an archive off the internet into the folder a server runs from
        /// rests on this being an allow list and staying one.
        /// </summary>
        [Fact]
        public void The_root_allow_list_is_exactly_the_four_loader_files()
        {
            Assert.Equal(
                new[] { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" },
                BepInExService.RootFileAllowList);
        }

        [Fact]
        public void The_download_cap_is_fifty_megabytes()
        {
            var service = new BepInExService(null, null, null, null);
            Assert.Equal(50L * 1024 * 1024, service.MaxDownloadBytes);
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "bakaloader-bepinex-rules-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Nuke(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
