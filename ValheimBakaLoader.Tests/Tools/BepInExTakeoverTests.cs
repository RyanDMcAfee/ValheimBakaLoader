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
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Taking over a BepInEx somebody else put there, which is what nearly every host has.
    /// <para>
    /// Everything here runs against a temporary folder that is laid out the way a real hand
    /// made install is: the four loose loader files, a core, a config the host edited, mods and
    /// patchers. Every download is served from memory by <see cref="RecordingHttpHandler"/>, so
    /// nothing reaches thunderstore.io, and the one test that does is gated on an environment
    /// variable and says out loud when it did nothing. No real server install is ever touched.
    /// </para>
    /// </summary>
    public class BepInExTakeoverTests : IDisposable
    {
        private readonly string Root =
            Path.Combine(Path.GetTempPath(), "bakaloader-bxtakeover-" + Guid.NewGuid().ToString("N"));

        private readonly string BaseDir;
        private readonly string BaseExe;

        public BepInExTakeoverTests()
        {
            // <root>/common/Valheim dedicated server/valheim_server.exe, so the instances root
            // the isolation service uses (<root>/common/.bakaloader-instances) is a real sibling.
            BaseDir = Path.Combine(Root, "common", "Valheim dedicated server");
            Directory.CreateDirectory(BaseDir);
            BaseExe = Path.Combine(BaseDir, "valheim_server.exe");
            File.WriteAllText(BaseExe, "not really an executable");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) DeleteTree(Root); } catch { }
        }

        /// <summary>
        /// A junction-safe delete: one of these tests makes real junctions, and a recursive
        /// delete that followed one would reach outside the temp folder.
        /// </summary>
        private static void DeleteTree(string dir)
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists) return;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { Directory.Delete(dir); return; }

            foreach (var sub in info.GetDirectories())
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(sub.FullName);
                else DeleteTree(sub.FullName);
            }
            foreach (var file in info.GetFiles())
            {
                try { file.Attributes = FileAttributes.Normal; } catch { }
                file.Delete();
            }
            Directory.Delete(dir);
        }

        // ------------------------------------------------------------------ the pack fixture

        /// <summary>What one recorded pack carries. Everything has the real pack's shape.</summary>
        private sealed class PackSpec
        {
            /// <summary>The Thunderstore pack version, which is not the BepInEx version.</summary>
            public string Version = "5.4.2350";

            /// <summary>A real assembly to ship as BepInEx.dll, when a version must be readable.</summary>
            public string CoreAssembly;

            /// <summary>What the pack's own .doorstop_version says, or null to ship none.</summary>
            public string DoorstopVersion = "4.4.0";

            /// <summary>The section header the pack's doorstop_config.ini uses.</summary>
            public string DoorstopSection = BepInExDoorstop.ModernSection;

            /// <summary>Changes the bytes of the text core files without changing the version.</summary>
            public string CoreFlavour = "";

            /// <summary>
            /// Changes the bytes of ONE core file and nothing else, which is what an archive
            /// that is not quite the archive the note recorded looks like.
            /// </summary>
            public string HarmonyFlavour = "";

            /// <summary>False to build a pack with one core assembly simply not in it.</summary>
            public bool ShipHarmony = true;

            /// <summary>
            /// One more assembly under the pack's core that the real pack never shipped, named
            /// here or null for none. The core is copied whole, so a file that is only in the
            /// archive still lands in the folder the loader resolves assemblies from.
            /// </summary>
            public string ExtraCoreFile;
        }

        private static byte[] Pack(PackSpec spec = null)
        {
            spec ??= new PackSpec();

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                const string root = "BepInExPack_Valheim/";

                Write(zip, root + "winhttp.dll", "the doorstop proxy " + spec.Version);
                Write(zip, root + "doorstop_config.ini", DoorstopText(spec.DoorstopSection));
                if (spec.DoorstopVersion != null)
                    Write(zip, root + ".doorstop_version", spec.DoorstopVersion);
                Write(zip, root + "changelog.txt",
                    "40 commits since v5.4.23.5\n\nChangelog (excluding merges):\n"
                    + "* (ef506e0) [AzumattDev] Bump Thunderstore version to " + spec.Version + "\n");

                if (spec.CoreAssembly != null)
                    WriteBytes(zip, root + "BepInEx/core/BepInEx.dll", File.ReadAllBytes(spec.CoreAssembly));
                else
                    Write(zip, root + "BepInEx/core/BepInEx.dll", "core " + spec.Version + spec.CoreFlavour);

                Write(zip, root + "BepInEx/core/BepInEx.Preloader.dll",
                    "preloader " + spec.Version + spec.CoreFlavour);
                if (spec.ShipHarmony)
                    Write(zip, root + "BepInEx/core/0Harmony.dll",
                        "harmony " + spec.Version + spec.CoreFlavour + spec.HarmonyFlavour);
                if (spec.ExtraCoreFile != null)
                    Write(zip, root + "BepInEx/core/" + spec.ExtraCoreFile,
                        "something the pack the note recorded never shipped");
                Write(zip, root + "BepInEx/config/BepInEx.cfg", "[Logging]\nshipped default\n");

                // Linux and macOS only, and the two shell scripts: inert on Windows and never
                // part of the allow list.
                Write(zip, root + "doorstop_libs/libdoorstop_x64.so", "elf");
                Write(zip, root + "start_server_bepinex.sh", "#!/bin/sh\n");

                Write(zip, "manifest.json",
                    "{\"name\":\"BepInExPack_Valheim\",\"version_number\":\"" + spec.Version + "\"}");
                Write(zip, "icon.png", "png");
            }
            return buffer.ToArray();
        }

        private static string DoorstopText(string section) => section == BepInExDoorstop.LegacySection
            ? "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n"
            : "[General]\nenabled = true\ntarget_assembly = BepInEx\\core\\BepInEx.Preloader.dll\n";

        private static void Write(ZipArchive zip, string path, string content)
            => WriteBytes(zip, path, Encoding.UTF8.GetBytes(content));

        private static void WriteBytes(ZipArchive zip, string path, byte[] bytes)
        {
            using var stream = zip.CreateEntry(path).Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Two real assemblies whose file versions are readable, DIFFERENT, and both 5.x, lower
        /// one first.
        /// <para>
        /// A version resource cannot be faked into a text file and the downgrade guard reads
        /// exactly that, so the fixture borrows assemblies that are already on disk beside the
        /// tests. Both have to be 5.x or the fixture would be testing the foreign-core rule
        /// instead: a core that is not a BepInEx 5 is left alone before the versions are ever
        /// compared, and the test would pass for the wrong reason.
        /// </para>
        /// </summary>
        private static (string Lower, string Higher) TwoVersionedFiveAssemblies()
        {
            var readable = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
                .Select(p => (Path: p, Version: BepInExService.FileVersionOf(p)))
                .Where(p => !string.IsNullOrWhiteSpace(p.Version))
                .Where(p => !BepInExService.IsForeignCoreVersion(p.Version))
                .GroupBy(p => p.Version, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.Version, Comparer<string>.Create(SemVer.Compare))
                .ToList();

            Assert.True(readable.Count >= 2,
                "there are not two 5.x assemblies beside the tests to build the version fixture from");

            return (readable[0].Path, readable[^1].Path);
        }

        /// <summary>A real assembly whose version reads as a framework this pack is not.</summary>
        private static string AForeignAssembly()
        {
            var path = typeof(Newtonsoft.Json.JsonConvert).Assembly.Location;
            Assert.True(BepInExService.IsForeignCoreVersion(BepInExService.FileVersionOf(path)),
                "the assembly picked for the foreign-core fixture reads as a BepInEx 5");
            return path;
        }

        // ------------------------------------------------------------------ the install fixture

        /// <summary>
        /// The install a host already has: four loose loader files, a core nobody recorded, a
        /// config they edited, their mods and their patchers. No BakaLoader note anywhere.
        /// </summary>
        private void ExistingInstall(string coreAssembly = null, string doorstopSection = null,
            string doorstopVersion = "4.4.0", string coreFlavour = " (the one that was here)")
        {
            File.WriteAllText(Path.Combine(BaseDir, "winhttp.dll"), "the doorstop proxy 5.4.2100");
            File.WriteAllText(Path.Combine(BaseDir, "doorstop_config.ini"),
                DoorstopText(doorstopSection ?? BepInExDoorstop.ModernSection));
            if (doorstopVersion != null)
                File.WriteAllText(Path.Combine(BaseDir, ".doorstop_version"), doorstopVersion);
            File.WriteAllText(Path.Combine(BaseDir, "changelog.txt"), "an older changelog\n");

            var core = Path.Combine(BaseDir, "BepInEx", "core");
            Directory.CreateDirectory(core);
            if (coreAssembly != null) File.Copy(coreAssembly, Path.Combine(core, "BepInEx.dll"));
            else File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "core" + coreFlavour);
            File.WriteAllText(Path.Combine(core, "BepInEx.Preloader.dll"), "preloader" + coreFlavour);
            File.WriteAllText(Path.Combine(core, "0Harmony.dll"), "harmony" + coreFlavour);

            var config = Path.Combine(BaseDir, "BepInEx", "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "BepInEx.cfg"),
                "[Logging]\nthe host edited this\nHideManagerGameObject = true\n");
            File.WriteAllText(Path.Combine(config, "some.mod.cfg"), "a mod's own settings\n");

            var plugins = Path.Combine(BaseDir, "BepInEx", "plugins", "Azumatt-AzuAntiItemLag");
            Directory.CreateDirectory(plugins);
            File.WriteAllText(Path.Combine(plugins, "AzuAntiItemLag.dll"), "a mod the host installed");

            var patchers = Path.Combine(BaseDir, "BepInEx", "patchers");
            Directory.CreateDirectory(patchers);
            File.WriteAllText(Path.Combine(patchers, "SomePatcher.dll"), "a patcher the host installed");
        }

        // ------------------------------------------------------------------ the service

        /// <summary>
        /// The real service with two seams pinned: the process table it would read, and the one
        /// core copy that can be made to fall over half way.
        /// </summary>
        private sealed class TestService : BepInExService
        {
            private int Copied;

            public TestService(IThunderstoreClient thunderstore,
                ValheimBakaLoader.Tools.Http.IHttpClientProvider http,
                IInstallIsolationService isolation, IApplicationLogger logger)
                : base(thunderstore, http, isolation, logger) { }

            /// <summary>The valheim_server processes this test says are up. Empty by default.</summary>
            public List<string> RunningImages { get; } = new();

            /// <summary>After this many core files, the copy gives up. Below zero: never.</summary>
            public int FailCoreCopyAfter { get; init; } = -1;

            protected override IReadOnlyList<string> RunningServerImagePaths() => RunningImages;

            protected override void CopyCoreFile(string source, string destination)
            {
                if (FailCoreCopyAfter >= 0 && Copied++ >= FailCoreCopyAfter)
                    throw new IOException("the disk went away half way through");
                base.CopyCoreFile(source, destination);
            }
        }

        /// <summary>
        /// What the site says about the pack. The SIZE is not decoration: an unattended write
        /// is refused when neither Thunderstore answer says what the archive weighs, because
        /// there is then nothing to check the download against. Every window in this file
        /// therefore hands in the real length of the fixture's own bytes, which is what the
        /// download will weigh, so these tests exercise the check rather than route round it.
        /// The date is left unset on purpose: an unknown publish date is not held back by the
        /// soak, and the tests that are about the soak say their own date.
        /// </summary>
        private static ThunderstorePackage Listing(
            string version, long? size = null, DateTime? created = null, bool? deprecated = null,
            bool? active = null) => new()
        {
            Namespace = BepInExService.DefaultPackageOwner,
            Name = BepInExService.DefaultPackageName,
            IsDeprecated = deprecated,
            Latest = new ThunderstorePackageVersion
            {
                VersionNumber = version,
                FileSize = size,
                DateCreated = created,
                // Left unsaid by default, which is how a listing that carries no such field
                // reads and what every other test in this file is about.
                IsActiveRaw = active,
            },
        };

        /// <param name="served">
        /// The archive the site really hands back, when that is a different pack from the one
        /// the LISTING describes. A repair asks for the version its own note names, and
        /// Thunderstore answers that address with that version's archive however far the
        /// listing has moved on; without this the fixture served the newest pack's bytes under
        /// the old version's address, which is not a thing the site does.
        /// </param>
        /// <param name="whileFetching">
        /// Runs as the archive is handed back, which is the only way to say "and THIS happened
        /// during the download". A fifty megabyte fetch on a slow line is minutes, so anything
        /// the write read before it started is worth being able to change underneath it.
        /// </param>
        private (TestService Service, RecordingHttpHandler Handler) Build(
            PackSpec spec = null, int failCoreCopyAfter = -1,
            long? liveSize = -1, long? indexSize = -1,
            DateTime? created = null, bool? deprecated = null, bool pulled = false,
            PackSpec served = null, Action whileFetching = null)
        {
            spec ??= new PackSpec();
            var bytes = Pack(served ?? spec);

            // Minus one means "the real length", which is what a listing that knows its own
            // package says. A test that wants an endpoint to leave the size out passes null.
            var live = liveSize == -1 ? bytes.LongLength : liveSize;
            var index = indexSize == -1 ? bytes.LongLength : indexSize;

            var provider = new RecordingHttpClientProvider(_ =>
            {
                whileFetching?.Invoke();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            });

            var thunderstore = new Mock<IThunderstoreClient>();
            thunderstore
                .Setup(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ThunderstoreLiveLookup
                {
                    Answered = true,
                    Package = Listing(spec.Version, live, created, deprecated, pulled ? false : null),
                });
            thunderstore
                .Setup(c => c.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(Listing(spec.Version, index, created, deprecated, pulled ? false : null));

            var service = new TestService(thunderstore.Object, provider,
                new InstallIsolationService(Mock.Of<IApplicationLogger>()), Mock.Of<IApplicationLogger>())
            {
                FailCoreCopyAfter = failCoreCopyAfter,
            };

            return (service, provider.Handler);
        }

        /// <summary>A service whose listing never answers.</summary>
        private TestService OfflineService()
        {
            var thunderstore = new Mock<IThunderstoreClient>();
            thunderstore
                .Setup(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ThunderstoreLiveLookup { Answered = false, Package = null });
            thunderstore
                .Setup(c => c.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((ThunderstorePackage)null);

            return new TestService(thunderstore.Object, new RecordingHttpClientProvider(),
                new InstallIsolationService(Mock.Of<IApplicationLogger>()), Mock.Of<IApplicationLogger>());
        }

        private static IEnumerable<BepInExProfileInstall> Nobody() => Array.Empty<BepInExProfileInstall>();

        /// <summary>
        /// A manual write the host has already been shown what is on disk for, and said go
        /// ahead to.
        /// <para>
        /// Every install in this file is one BakaLoader did not make, and a press over one of
        /// those is refused by the SERVICE until that confirm has happened: a dialog the page
        /// shows is a manner rather than a clamp, because the call is there to be made. So a
        /// test about anything else says the yes here rather than tripping that refusal on the
        /// way to what it is actually testing, and the tests that ARE about the clamp press
        /// with no options at all.
        /// </para>
        /// </summary>
        private static BepInExWriteOptions Pressed => new() { OverOutside = true };

        private Dictionary<string, string> Snapshot()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(BaseDir, "*", SearchOption.AllDirectories))
                map[Path.GetRelativePath(BaseDir, file)] = BepInExService.Sha256Of(file) ?? "(unreadable)";
            return map;
        }

        private static IReadOnlyList<string> ChangedBetween(
            Dictionary<string, string> before, Dictionary<string, string> after)
            => after.Where(kv => !before.TryGetValue(kv.Key, out var hash) || hash != kv.Value)
                .Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        private string BackupRoot => Path.Combine(BaseDir, "BepInEx", BepInExService.BackupDirName);

        private BepInExBackup NewestBackup() => BepInExBackups.Newest(BaseDir);

        // ------------------------------------------------------------------ adopting

        /// <summary>
        /// The whole point of the feature: an install a host already had comes under care, the
        /// loader files are replaced, and nothing they own is touched. The mods, the patchers,
        /// every mod config and the BepInEx.cfg they edited come out the other side byte for
        /// byte, and what was replaced is in the backup.
        /// </summary>
        [Fact]
        public async Task An_existing_install_is_adopted_and_only_the_loader_files_change()
        {
            ExistingInstall();
            var before = Snapshot();

            var (service, _) = Build();
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.Installed);
            Assert.True(result.Replaced);
            Assert.True(result.Adopted);
            Assert.False(result.Skipped);

            var changed = ChangedBetween(before, Snapshot());

            // The host's own files are not on that list.
            Assert.DoesNotContain(Path.Combine("BepInEx", "config", "BepInEx.cfg"), changed);
            Assert.DoesNotContain(Path.Combine("BepInEx", "config", "some.mod.cfg"), changed);
            Assert.DoesNotContain(Path.Combine("BepInEx", "plugins", "Azumatt-AzuAntiItemLag",
                "AzuAntiItemLag.dll"), changed);
            Assert.DoesNotContain(Path.Combine("BepInEx", "patchers", "SomePatcher.dll"), changed);
            Assert.DoesNotContain("doorstop_config.ini", changed);

            // The loader files are.
            Assert.Contains("winhttp.dll", changed);
            Assert.Contains(Path.Combine("BepInEx", "core", "BepInEx.dll"), changed);
            Assert.Equal("core 5.4.2350",
                File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")));

            // And what they replaced is kept.
            var backup = NewestBackup();
            Assert.NotNull(backup);
            Assert.Equal(result.BackupStamp, backup.Stamp);
            Assert.Equal("core (the one that was here)",
                File.ReadAllText(Path.Combine(backup.CoreFolder, "BepInEx.dll")));
            Assert.Equal("the doorstop proxy 5.4.2100",
                File.ReadAllText(Path.Combine(backup.RootFolder, "winhttp.dll")));
        }

        /// <summary>The same window twice: the second one fetches nothing and writes nothing.</summary>
        [Fact]
        public async Task The_window_running_twice_changes_nothing_the_second_time()
        {
            ExistingInstall();

            var (first, _) = Build();
            await first.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            var between = Snapshot();
            var backupsThen = Directory.GetDirectories(BackupRoot).Length;

            var (second, handler) = Build();
            var result = await second.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.AlreadyCurrent);
            Assert.True(result.NothingChanged);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(between, Snapshot()));
            Assert.Equal(backupsThen, Directory.GetDirectories(BackupRoot).Length);
        }

        /// <summary>
        /// The commonest case of all: the host is already on the current pack, they just never
        /// had a note saying so. Nothing is fetched twice, nothing is backed up and not one byte
        /// of the install changes. Only the note is written.
        /// </summary>
        [Fact]
        public async Task An_install_that_already_holds_the_pack_gets_the_note_and_nothing_else()
        {
            ExistingInstall();

            // Put the pack in once so the disk holds exactly what it ships...
            var (setup, _) = Build();
            await setup.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            // ...then take the note away, which is the state a hand-made install is in.
            File.Delete(Path.Combine(BaseDir, BepInExMarkerFile.FileName));
            var before = Snapshot();

            // With no note there is no version to match on, so the answer can only come from
            // the files themselves. This is exactly the state a hand-made install is in.
            var (service, handler) = Build();
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.NothingChanged);
            Assert.True(result.Adopted);
            Assert.False(result.Replaced);
            Assert.NotEmpty(handler.Requests);       // it did fetch, and then chose not to write
            Assert.Null(result.BackupStamp);

            var after = Snapshot();
            Assert.Equal(new[] { BepInExMarkerFile.FileName }, ChangedBetween(before, after));

            var marker = BepInExMarkerFile.Read(BaseDir);
            Assert.Equal("5.4.2350", marker.Version);
            Assert.Contains(marker.Files, f => f.Path == "BepInEx/core/BepInEx.dll");
            Assert.Equal(BepInExService.Sha256Of(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")),
                marker.Files.First(f => f.Path == "BepInEx/core/BepInEx.dll").Sha256);
        }

        /// <summary>
        /// The same framework version with different bytes is a core somebody patched or pinned
        /// on purpose, and the window does not undo that quietly.
        /// </summary>
        [Fact]
        public async Task A_core_of_the_same_version_with_different_bytes_is_left_alone_by_the_window()
        {
            var (lower, _) = TwoVersionedFiveAssemblies();
            ExistingInstall(coreAssembly: lower);
            var before = Snapshot();

            // The same assembly in the pack, so the versions match, but the other core files
            // around it differ: a core that is not the stock one.
            var (service, _) = Build(new PackSpec { CoreAssembly = lower, CoreFlavour = " stock" });
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.Skipped);
            Assert.Equal(BepInExSkipReason.Foreign, result.SkipReason);
            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        // ------------------------------------------------------------------ files that will not move

        /// <summary>
        /// A core assembly held the way a LOADED one is, open with <c>FileShare.Read</c>. It
        /// copies perfectly well and refuses to be deleted, so a writer that finds out by trying
        /// gets eight files into clearing the core before the ninth stops it. The pre-flight
        /// asks for read AND write with no sharing at all, before anything has moved.
        /// </summary>
        [Fact]
        public async Task A_core_file_held_the_way_a_loaded_assembly_is_stops_the_write_and_names_it()
        {
            ExistingInstall();
            var before = Snapshot();
            var held = Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.Preloader.dll");

            var (service, _) = Build();

            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var refused = await Assert.ThrowsAsync<HostFacingException>(
                    () => service.UpdateAsync(BaseExe, Nobody(), options: Pressed));

                Assert.Equal("bepinex.locked", refused.MessageId);
                Assert.Equal("BepInEx.Preloader.dll", refused.Params["file"]);
            }

            // Byte for byte, and no note claiming an install that did not happen.
            Assert.Empty(ChangedBetween(before, Snapshot()));
            Assert.Null(BepInExMarkerFile.Read(BaseDir));
            Assert.False(Directory.Exists(BackupRoot));
            Assert.Null(BepInExWriteSentinel.StaleStamp(BaseDir));
        }

        /// <summary>The same with no sharing at all, which is the other way a file is held.</summary>
        [Fact]
        public async Task A_core_file_held_with_no_sharing_stops_the_write_and_names_it()
        {
            ExistingInstall();
            var before = Snapshot();
            var held = Path.Combine(BaseDir, "BepInEx", "core", "0Harmony.dll");

            var (service, _) = Build();

            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var refused = await Assert.ThrowsAsync<HostFacingException>(
                    () => service.UpdateAsync(BaseExe, Nobody(), options: Pressed));

                Assert.Equal("bepinex.locked", refused.MessageId);
                Assert.Equal("0Harmony.dll", refused.Params["file"]);
            }

            Assert.Empty(ChangedBetween(before, Snapshot()));
            Assert.Null(BepInExMarkerFile.Read(BaseDir));
        }

        /// <summary>A loose loader file that will not move stops the write just the same.</summary>
        [Fact]
        public async Task A_loader_file_beside_the_server_that_will_not_move_stops_the_write()
        {
            ExistingInstall();
            var before = Snapshot();

            var (service, _) = Build();

            using (new FileStream(Path.Combine(BaseDir, "winhttp.dll"),
                       FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var refused = await Assert.ThrowsAsync<HostFacingException>(
                    () => service.UpdateAsync(BaseExe, Nobody(), options: Pressed));

                Assert.Equal("bepinex.locked", refused.MessageId);
                Assert.Equal("winhttp.dll", refused.Params["file"]);
            }

            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        /// <summary>
        /// A core copy that gives up half way never reaches the install at all now: the new
        /// core is built in a folder beside it and only swapped in when it is whole.
        /// </summary>
        [Fact]
        public async Task A_core_copy_that_gives_up_half_way_leaves_the_install_exactly_as_it_was()
        {
            ExistingInstall();
            var before = Snapshot();

            var (service, _) = Build(failCoreCopyAfter: 1);

            await Assert.ThrowsAnyAsync<Exception>(() => service.UpdateAsync(BaseExe, Nobody(), options: Pressed));

            Assert.Empty(ChangedBetween(before, Snapshot()));
            Assert.Null(BepInExMarkerFile.Read(BaseDir));
            Assert.Null(BepInExWriteSentinel.StaleStamp(BaseDir));

            // and no empty backup folder left behind to be counted as one
            Assert.Null(NewestBackup());
        }

        // ------------------------------------------------------------------ never backwards

        /// <summary>
        /// An install ahead of the pack is not moved back by a window that nobody is watching.
        /// The reason is recorded so the row can say what happened.
        /// </summary>
        [Fact]
        public async Task An_install_newer_than_the_pack_is_left_alone_by_the_window()
        {
            var (lower, higher) = TwoVersionedFiveAssemblies();
            ExistingInstall(coreAssembly: higher);
            var before = Snapshot();

            var (service, _) = Build(new PackSpec { CoreAssembly = lower });
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.Skipped);
            Assert.Equal(BepInExSkipReason.Newer, result.SkipReason);
            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        /// <summary>
        /// The manual button refuses too, and names both versions, until the host has been shown
        /// them and said yes.
        /// </summary>
        [Fact]
        public async Task The_manual_write_refuses_to_go_backwards_until_the_host_says_yes()
        {
            var (lower, higher) = TwoVersionedFiveAssemblies();
            var installedVersion = BepInExService.FileVersionOf(higher);
            var packVersion = BepInExService.FileVersionOf(lower);

            ExistingInstall(coreAssembly: higher);

            var (service, _) = Build(new PackSpec { CoreAssembly = lower });

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.UpdateAsync(BaseExe, Nobody(), options: Pressed));

            Assert.Equal("bepinex.newer", refused.MessageId);
            Assert.Equal(installedVersion, refused.Params["installed"]);
            Assert.Equal(packVersion, refused.Params["pack"]);
            Assert.Null(BepInExMarkerFile.Read(BaseDir));

            // And with the yes in hand it goes ahead. Two separate answers, because they are
            // two separate questions: one is "this install is not mine to write to" and the
            // other is "this would put you on an older loader than the one you have".
            var (again, _) = Build(new PackSpec { CoreAssembly = lower });
            var result = await again.UpdateAsync(BaseExe, Nobody(),
                options: new BepInExWriteOptions { OverOutside = true, AllowDowngrade = true });

            Assert.True(result.Installed);
            Assert.Equal(packVersion,
                BepInExService.FileVersionOf(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")));
        }

        // ------------------------------------------------------------------ somebody else's install

        /// <summary>
        /// A host coming from r2modman, Gale or Thunderstore Mod Manager has their doorstop
        /// config pointed at that tool's own profile folder. Writing the pack's copy over it
        /// swaps their whole mod set for an empty one, so the window leaves the install alone.
        /// </summary>
        [Fact]
        public async Task An_install_another_manager_drives_is_read_and_left_alone()
        {
            ExistingInstall();
            var ini = Path.Combine(BaseDir, "doorstop_config.ini");
            File.WriteAllText(ini,
                "[General]\nenabled = true\n"
                + "target_assembly = C:\\Users\\Someone\\AppData\\Roaming\\com.kesomannen.gale"
                + "\\valheim\\profiles\\Default\\BepInEx\\core\\BepInEx.Preloader.dll\n");

            var before = Snapshot();
            var (service, handler) = Build();

            var status = service.Status(BaseExe);
            Assert.True(status.DrivenElsewhere);
            Assert.Contains("com.kesomannen.gale", status.DoorstopTarget);

            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.Skipped);
            Assert.Equal(BepInExSkipReason.DrivenElsewhere, result.SkipReason);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        /// <summary>A core that is not a 5.x is a different framework, and is left alone.</summary>
        [Fact]
        public async Task A_core_that_is_not_a_five_is_left_alone_by_the_window()
        {
            ExistingInstall(coreAssembly: AForeignAssembly());
            var before = Snapshot();

            var (service, handler) = Build();
            Assert.True(service.Status(BaseExe).ForeignCore);

            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.Skipped);
            Assert.Equal(BepInExSkipReason.Foreign, result.SkipReason);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        // ------------------------------------------------------------------ the host's own files

        /// <summary>
        /// A config the host edited and a doorstop config they tuned both survive the write, and
        /// what WAS replaced is in the backup where they can get it.
        /// </summary>
        [Fact]
        public async Task A_customised_config_and_doorstop_are_both_kept_and_the_rest_is_backed_up()
        {
            ExistingInstall();
            var ini = Path.Combine(BaseDir, "doorstop_config.ini");
            File.WriteAllText(ini, File.ReadAllText(ini) + "redirect_output_log = true\ndebug_enabled = true\n");

            var cfg = Path.Combine(BaseDir, "BepInEx", "config", "BepInEx.cfg");
            var cfgHash = BepInExService.Sha256Of(cfg);
            var iniHash = BepInExService.Sha256Of(ini);

            var (service, _) = Build();
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.False(result.DoorstopReplaced);
            Assert.Equal(cfgHash, BepInExService.Sha256Of(cfg));
            Assert.Equal(iniHash, BepInExService.Sha256Of(ini));

            var backup = NewestBackup();
            Assert.Equal("the doorstop proxy 5.4.2100",
                File.ReadAllText(Path.Combine(backup.RootFolder, "winhttp.dll")));
            Assert.Equal("an older changelog\n",
                File.ReadAllText(Path.Combine(backup.RootFolder, "changelog.txt")));
            // The one that was kept is not in there, because it was not replaced.
            Assert.False(File.Exists(Path.Combine(backup.RootFolder, "doorstop_config.ini")));
        }

        /// <summary>
        /// The one case where the host's file has to give way: the pack ships a Doorstop the old
        /// config cannot drive. Their file goes into the backup and the result says so.
        /// </summary>
        [Fact]
        public async Task A_loader_generation_change_replaces_the_config_and_keeps_the_old_one()
        {
            ExistingInstall(doorstopSection: BepInExDoorstop.LegacySection, doorstopVersion: "3.7.0");
            var ini = Path.Combine(BaseDir, "doorstop_config.ini");
            var hostText = File.ReadAllText(ini);

            var (service, _) = Build(new PackSpec { DoorstopVersion = "4.4.0" });
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.DoorstopReplaced);
            Assert.Equal(BepInExDoorstop.ModernSection,
                BepInExDoorstop.SectionInText(File.ReadAllText(ini)));

            var backup = NewestBackup();
            Assert.Equal(hostText, File.ReadAllText(Path.Combine(backup.RootFolder, "doorstop_config.ini")));
        }

        /// <summary>
        /// A loader file the new pack does not ship, still sitting here from the old one. It
        /// goes into the backup so what is left beside the server is one coherent set.
        /// </summary>
        [Fact]
        public async Task A_loader_file_the_new_pack_does_not_ship_is_moved_into_the_backup()
        {
            ExistingInstall(doorstopVersion: "4.4.0");
            Assert.True(File.Exists(Path.Combine(BaseDir, ".doorstop_version")));

            var (service, _) = Build(new PackSpec { DoorstopVersion = null });
            await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.False(File.Exists(Path.Combine(BaseDir, ".doorstop_version")));
            Assert.Equal("4.4.0",
                File.ReadAllText(Path.Combine(NewestBackup().RootFolder, ".doorstop_version")));

            // and the host's doorstop config is NEVER moved away by that rule
            Assert.True(File.Exists(Path.Combine(BaseDir, "doorstop_config.ini")));
        }

        // ------------------------------------------------------------------ what counts as installed

        /// <summary>
        /// On Windows the game never looks at the core without the loose file beside the
        /// executable, so an install missing it loads nothing however complete the core is.
        /// Antivirus taking that one file is the ordinary way this happens.
        /// </summary>
        [Fact]
        public async Task An_install_without_the_loose_loader_file_is_not_installed()
        {
            ExistingInstall();
            var (service, _) = Build();
            await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(service.Status(BaseExe).Installed);

            File.Delete(Path.Combine(BaseDir, "winhttp.dll"));
            var status = service.Status(BaseExe);

            Assert.False(status.Installed);
            Assert.False(status.LoaderFilePresent);
            Assert.True(status.MaintainedByBakaLoader);
            Assert.Contains("winhttp.dll", status.MissingFiles);
            Assert.False(status.Drifted);
        }

        /// <summary>
        /// A core with files in it and no BepInEx.dll among them: a BepInEx 6, a renamed core,
        /// or one an antivirus took the assembly out of. Nothing is installed or cleared there
        /// automatically, and the manual write is refused until the host has been asked.
        /// </summary>
        [Fact]
        public async Task A_core_BakaLoader_does_not_recognise_is_never_written_over_unasked()
        {
            var core = Path.Combine(BaseDir, "BepInEx", "core");
            Directory.CreateDirectory(core);
            File.WriteAllText(Path.Combine(core, "BepInEx.Core.dll"), "a six, or something renamed");
            File.WriteAllText(Path.Combine(BaseDir, "winhttp.dll"), "the doorstop proxy");
            var before = Snapshot();

            var (service, handler) = Build();
            var status = service.Status(BaseExe);

            Assert.True(status.Unrecognised);
            Assert.True(status.Damaged);
            Assert.False(status.Installed);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.InstallAsync(BaseExe, null, Nobody()));
            Assert.Equal("bepinex.unrecognisedCore", refused.MessageId);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));

            // The window says the same thing without throwing, because nobody is there to read it.
            var skipped = await service.InstallAsync(BaseExe, null, Nobody(),
                options: BepInExWriteOptions.Window);
            Assert.True(skipped.Skipped);
            Assert.Equal(BepInExSkipReason.Unrecognised, skipped.SkipReason);
            Assert.Empty(ChangedBetween(before, Snapshot()));

            // And with the host's yes in hand it goes in.
            var (over, _) = Build();
            var result = await over.InstallAsync(BaseExe, null, Nobody(),
                options: new BepInExWriteOptions { OverUnrecognised = true, OverOutside = true });

            Assert.True(result.Installed);
            Assert.Equal("core 5.4.2350", File.ReadAllText(Path.Combine(core, "BepInEx.dll")));
            // and what was there is in the backup rather than gone
            Assert.Equal("a six, or something renamed",
                File.ReadAllText(Path.Combine(NewestBackup().CoreFolder, "BepInEx.Core.dll")));
        }

        // ------------------------------------------------------------------ somebody else wrote here

        /// <summary>
        /// Another tool wrote over the files the note recorded. BakaLoader gives up ownership
        /// rather than taking the install straight back off them, and it STAYS given up: two
        /// managers each adopting what the other wrote is a loop nobody can see happening.
        /// </summary>
        [Fact]
        public async Task Files_another_tool_wrote_over_drop_ownership_and_keep_it_dropped()
        {
            ExistingInstall();
            var (service, _) = Build();
            await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);
            Assert.True(service.Status(BaseExe).MaintainedByBakaLoader);

            // Another manager writes its own core over the top.
            File.WriteAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll"),
                "a core some other tool put here");

            var status = service.Status(BaseExe);
            Assert.True(status.Drifted);
            Assert.False(status.MaintainedByBakaLoader);
            Assert.Null(status.PackVersion);
            Assert.True(File.Exists(Path.Combine(BaseDir, BepInExMarkerFile.StaleFileName)));
            Assert.False(File.Exists(Path.Combine(BaseDir, BepInExMarkerFile.FileName)));

            // Read again: still given up, and this is the half the row depends on.
            Assert.True(service.Status(BaseExe).Drifted);

            var before = Snapshot();
            var (window, handler) = Build(new PackSpec { Version = "5.4.2360" });
            var result = await window.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.Skipped);
            Assert.Equal(BepInExSkipReason.Drift, result.SkipReason);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));

            // The host pressing the button is still allowed to take it back, and then the
            // install is BakaLoader's again with nothing left saying otherwise.
            var (manual, _) = Build(new PackSpec { Version = "5.4.2360" });
            Assert.True((await manual.UpdateAsync(BaseExe, Nobody(), options: Pressed)).Installed);
            Assert.False(File.Exists(Path.Combine(BaseDir, BepInExMarkerFile.StaleFileName)));
            Assert.False(service.Status(BaseExe).Drifted);
        }

        /// <summary>A config the host edited is not somebody else taking the install over.</summary>
        [Fact]
        public async Task Editing_a_config_is_not_drift()
        {
            ExistingInstall();
            var (service, _) = Build();
            await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            File.WriteAllText(Path.Combine(BaseDir, "BepInEx", "config", "BepInEx.cfg"),
                "[Logging]\nthe host changed their mind\n");
            File.WriteAllText(Path.Combine(BaseDir, "doorstop_config.ini"),
                DoorstopText(BepInExDoorstop.ModernSection) + "debug_enabled = true\n");

            var status = service.Status(BaseExe);
            Assert.False(status.Drifted);
            Assert.True(status.MaintainedByBakaLoader);
        }

        // ------------------------------------------------------------------ a server nobody started here

        /// <summary>
        /// A server the host launched themselves, from the Steam library or a shortcut, holds
        /// the very files a write replaces and appears in no profile list BakaLoader keeps.
        /// </summary>
        [Fact]
        public async Task A_live_server_process_on_this_install_stops_the_write_before_anything_is_fetched()
        {
            ExistingInstall();
            var before = Snapshot();

            var (service, handler) = Build();
            service.RunningImages.Add(BaseExe);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.UpdateAsync(BaseExe, Nobody(), options: Pressed));

            Assert.Equal("bepinex.serversRunning", refused.MessageId);
            Assert.Equal("Valheim dedicated server", refused.Params["names"]);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        /// <summary>A server running somewhere else has nothing to do with this install.</summary>
        [Fact]
        public async Task A_live_server_process_on_another_install_stops_nothing()
        {
            ExistingInstall();

            var (service, _) = Build();
            service.RunningImages.Add(Path.Combine(Root, "elsewhere", "valheim_server.exe"));

            Assert.True((await service.UpdateAsync(BaseExe, Nobody(), options: Pressed)).Installed);
        }

        /// <summary>
        /// A first install replaced nothing, so it has nothing to back up. An empty folder is
        /// not a backup and must not be counted as one, or the row would offer to put back a
        /// copy of nothing.
        /// </summary>
        [Fact]
        public async Task A_first_install_leaves_no_backup_to_offer()
        {
            var (service, _) = Build();
            var result = await service.InstallAsync(BaseExe, null, Nobody());

            Assert.True(result.Installed);
            Assert.False(result.Replaced);
            Assert.Null(result.BackupStamp);
            Assert.Null(service.Status(BaseExe).NewestBackup);
            Assert.Null(NewestBackup());
        }

        // ------------------------------------------------------------------ a core that is a link

        /// <summary>
        /// An isolated profile's core IS a link to the base install's. Writing through it would
        /// rewrite the base from a folder that only borrows it, so the write is refused and the
        /// reason says where it belongs.
        /// </summary>
        [Fact]
        public async Task A_core_that_is_a_link_to_another_folder_is_refused()
        {
            if (!JunctionsWork()) return;   // this volume or this policy will not make junctions

            var realCore = Path.Combine(Root, "the-real-core");
            Directory.CreateDirectory(realCore);
            File.WriteAllText(Path.Combine(realCore, "BepInEx.dll"), "the base install's core");

            Directory.CreateDirectory(Path.Combine(BaseDir, "BepInEx"));
            File.WriteAllText(Path.Combine(BaseDir, "winhttp.dll"), "a hard link, really");
            Junction(Path.Combine(BaseDir, "BepInEx", "core"), realCore);

            var (service, handler) = Build();
            Assert.True(service.Status(BaseExe).CoreIsJunction);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.UpdateAsync(BaseExe, Nobody(), options: Pressed));

            Assert.Equal("bepinex.coreIsJunction", refused.MessageId);
            Assert.Empty(handler.Requests);
            Assert.Equal("the base install's core", File.ReadAllText(Path.Combine(realCore, "BepInEx.dll")));
        }

        /// <summary>
        /// The swap moves the core folder out and a new one in, and an isolated profile's
        /// junction points at the PATH, so it reads the new core without being touched. This is
        /// the half of the swap that would break silently: a profile still reading a folder that
        /// had been renamed into the backup would run the old loader forever.
        /// </summary>
        [Fact]
        public async Task The_swap_leaves_an_isolated_profiles_junction_reading_the_new_core()
        {
            if (!JunctionsWork()) return;

            var instances = Path.Combine(Path.GetDirectoryName(BaseDir), ".bakaloader-instances");
            var isolated = Path.Combine(instances, "Quiet");
            Directory.CreateDirectory(isolated);
            File.WriteAllText(Path.Combine(isolated, "valheim_server.exe"), "the isolated copy");

            ExistingInstall();

            var (first, _) = Build(new PackSpec { Version = "5.4.2350" });
            Assert.Equal(1, (await first.UpdateAsync(BaseExe, Nobody(), options: Pressed)).IsolatedInstallsLinked);

            var isolatedCore = Path.Combine(isolated, "BepInEx", "core");
            Assert.True(new DirectoryInfo(isolatedCore).Attributes.HasFlag(FileAttributes.ReparsePoint));
            Assert.Equal("core 5.4.2350", File.ReadAllText(Path.Combine(isolatedCore, "BepInEx.dll")));

            // A second write, which is where the swap happens over a core that is already there.
            var (second, _) = Build(new PackSpec { Version = "5.4.2360" });
            await second.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            Assert.True(new DirectoryInfo(isolatedCore).Attributes.HasFlag(FileAttributes.ReparsePoint));
            Assert.Equal("core 5.4.2360", File.ReadAllText(Path.Combine(isolatedCore, "BepInEx.dll")));
        }

        // ------------------------------------------------------------------ putting one back

        /// <summary>The backup is a real copy of the files, and it really goes back.</summary>
        [Fact]
        public async Task The_restore_puts_the_newest_backup_back()
        {
            ExistingInstall();
            var wasCore = File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll"));
            var wasLoader = File.ReadAllText(Path.Combine(BaseDir, "winhttp.dll"));

            var (service, _) = Build();
            await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);
            Assert.NotEqual(wasCore, File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")));

            var stamp = service.Status(BaseExe).NewestBackup;
            Assert.NotNull(stamp);

            var result = await service.RestoreAsync(BaseExe, Nobody());

            Assert.True(result.Restored);
            Assert.True(result.Installed);
            Assert.Equal(wasCore, File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")));
            Assert.Equal(wasLoader, File.ReadAllText(Path.Combine(BaseDir, "winhttp.dll")));

            // The mods are still the host's, and the note describes what is really there now.
            Assert.Equal("a mod the host installed", File.ReadAllText(Path.Combine(
                BaseDir, "BepInEx", "plugins", "Azumatt-AzuAntiItemLag", "AzuAntiItemLag.dll")));
            Assert.False(service.Status(BaseExe).Drifted);
        }

        [Fact]
        public async Task A_restore_with_no_backup_says_so_rather_than_doing_nothing_quietly()
        {
            ExistingInstall();
            var (service, _) = Build();

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.RestoreAsync(BaseExe, Nobody()));

            Assert.Equal("bepinex.noBackup", refused.MessageId);
        }

        /// <summary>
        /// Backups written by 1.1.x sat one level deeper under a folder called core, with no
        /// root half at all. A host upgrading has those on disk, and the one thing worse than an
        /// old layout is a restore that cannot see the backup it needs.
        /// </summary>
        [Fact]
        public async Task A_backup_written_in_the_old_layout_is_still_read_and_still_put_back()
        {
            ExistingInstall();

            var old = Path.Combine(BackupRoot, "core", "20260101-120000");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "BepInEx.dll"), "the core 1.1.2 replaced");
            File.WriteAllText(Path.Combine(old, "BepInEx.Preloader.dll"), "the preloader 1.1.2 replaced");

            var (service, _) = Build();
            Assert.Equal("20260101-120000", service.Status(BaseExe).NewestBackup);

            await service.RestoreAsync(BaseExe, Nobody(), "20260101-120000");

            Assert.Equal("the core 1.1.2 replaced",
                File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")));
        }

        /// <summary>Three are kept, and the three that are kept are the newest three.</summary>
        [Fact]
        public void Only_the_newest_three_backups_are_kept_whichever_layout_wrote_them()
        {
            foreach (var stamp in new[] { "20260101-120000", "20260102-120000", "20260103-120000" })
            {
                var folder = Path.Combine(BackupRoot, stamp, "core");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "BepInEx.dll"), stamp);
            }

            var legacy = Path.Combine(BackupRoot, "core", "20251231-120000");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "BepInEx.dll"), "the oldest of all");

            Assert.Equal(4, BepInExBackups.All(BaseDir).Count);
            Assert.Equal("20260103-120000", BepInExBackups.Newest(BaseDir).Stamp);

            BepInExBackups.Prune(BaseDir, "20260103-120000", BepInExService.KeptCoreBackups);

            Assert.Equal(
                new[] { "20260103-120000", "20260102-120000", "20260101-120000" },
                BepInExBackups.All(BaseDir).Select(b => b.Stamp).ToArray());
            Assert.False(Directory.Exists(legacy));
        }

        // ------------------------------------------------------------------ a write that stopped

        /// <summary>
        /// The swap has one moment where the install has no core at all: one rename wide. A
        /// machine losing power inside it leaves a server that starts with no mods and nothing
        /// on disk saying why, so the write leaves a mark naming the backup and the next read
        /// finishes the job the other way round.
        /// </summary>
        [Fact]
        public async Task A_write_that_stopped_between_the_two_renames_is_finished_the_other_way()
        {
            ExistingInstall();
            var wasCore = File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll"));

            var (service, _) = Build();
            await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            var stamp = service.Status(BaseExe).NewestBackup;

            // What the machine would have been left holding: the old core in the backup, the new
            // one not yet renamed in, and the mark naming which backup it was.
            DeleteTree(Path.Combine(BaseDir, "BepInEx", "core"));
            File.WriteAllText(BepInExWriteSentinel.PathOf(BaseDir), stamp);

            var status = service.Status(BaseExe);
            Assert.True(status.InterruptedWrite);
            Assert.False(status.Installed);

            var healed = service.HealInterruptedWrite(BaseExe, Nobody());

            Assert.NotNull(healed);
            Assert.True(healed.Healed);
            Assert.Equal(wasCore, File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")));
            Assert.False(service.Status(BaseExe).InterruptedWrite);
        }

        /// <summary>
        /// A mark over a core that is whole is a write that got past the renames and stopped
        /// somewhere harmless. Putting an older core back over a good one would BE the damage,
        /// so the mark simply comes off.
        /// </summary>
        [Fact]
        public async Task A_write_mark_over_a_whole_core_is_taken_off_and_nothing_is_moved()
        {
            ExistingInstall();
            var (service, _) = Build();
            await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            var before = Snapshot();
            File.WriteAllText(BepInExWriteSentinel.PathOf(BaseDir), service.Status(BaseExe).NewestBackup);

            Assert.Null(service.HealInterruptedWrite(BaseExe, Nobody()));
            Assert.False(service.Status(BaseExe).InterruptedWrite);
            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        /// <summary>
        /// A write that is RUNNING holds its mark open, which is what tells it apart from one
        /// that stopped. A status read a second into a write must not decide the write failed.
        /// </summary>
        [Fact]
        public void A_mark_a_write_is_holding_is_not_a_write_that_stopped()
        {
            Directory.CreateDirectory(Path.Combine(BaseDir, "BepInEx"));

            using (BepInExWriteSentinel.Hold(BaseDir, "20260920-120000"))
            {
                Assert.True(File.Exists(BepInExWriteSentinel.PathOf(BaseDir)));
                Assert.Null(BepInExWriteSentinel.StaleStamp(BaseDir));
            }

            // And once it is let go there is nothing left at all.
            Assert.False(File.Exists(BepInExWriteSentinel.PathOf(BaseDir)));
            Assert.Null(BepInExWriteSentinel.StaleStamp(BaseDir));
        }

        // ------------------------------------------------------------------ no network

        /// <summary>A site that will not answer writes nothing and says which it was.</summary>
        [Fact]
        public async Task The_site_not_answering_leaves_an_existing_install_exactly_as_it_was()
        {
            ExistingInstall();
            var before = Snapshot();

            var service = OfflineService();

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window));

            Assert.Equal("bepinex.offline", refused.MessageId);
            Assert.Empty(ChangedBetween(before, Snapshot()));
            Assert.Null(BepInExMarkerFile.Read(BaseDir));
        }

        // ------------------------------------------------------------------ the real thing

        /// <summary>
        /// The one test that talks to Thunderstore, and only when it is asked to. Set
        /// BAKA_BEPINEX_LIVE=1 to run it; without that it prints what it did not do and ends,
        /// because a suite that quietly skips a network test is a suite that looks greener than
        /// it is.
        /// </summary>
        [Fact]
        public async Task A_real_pack_from_thunderstore_adopts_a_copy_of_a_realistic_install()
        {
            if (Environment.GetEnvironmentVariable("BAKA_BEPINEX_LIVE") != "1")
            {
                Console.WriteLine(
                    "BAKA_BEPINEX_LIVE is not 1, so this test did nothing: no request was made to "
                    + "thunderstore.io and no pack was fetched. Set BAKA_BEPINEX_LIVE=1 to run it.");
                return;
            }

            ExistingInstall();
            var before = Snapshot();

            var http = new ValheimBakaLoader.Tools.Http.HttpClientProvider();
            var serilog = new Serilog.LoggerConfiguration().CreateLogger();
            var thunderstore = new ThunderstoreClient(
                new ValheimBakaLoader.Tools.Http.RestClientContext(serilog, http));

            var isolation = new Mock<IInstallIsolationService>();
            isolation.Setup(i => i.ManagedInstallDirectories(It.IsAny<string>()))
                .Returns(Array.Empty<string>());

            var service = new BepInExService(thunderstore, http, isolation.Object,
                Mock.Of<IApplicationLogger>());

            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.Installed);
            Assert.False(string.IsNullOrWhiteSpace(result.Version));
            Console.WriteLine("The live pack " + result.Version + " went into the copy at " + BaseDir + ".");

            // The host's own files came through it, which is the whole claim.
            var after = Snapshot();
            Assert.Equal(before[Path.Combine("BepInEx", "config", "BepInEx.cfg")],
                after[Path.Combine("BepInEx", "config", "BepInEx.cfg")]);
            Assert.Equal(before[Path.Combine("BepInEx", "plugins", "Azumatt-AzuAntiItemLag",
                    "AzuAntiItemLag.dll")],
                after[Path.Combine("BepInEx", "plugins", "Azumatt-AzuAntiItemLag", "AzuAntiItemLag.dll")]);
            Assert.Equal(before["doorstop_config.ini"], after["doorstop_config.ini"]);
        }

        // -------------------------------------------------------- the confirms are clamps

        /// <summary>
        /// Every destructive write over an install BakaLoader did not make is refused by the
        /// SERVICE until the call carries the answer to the question the host was meant to be
        /// asked. The page's confirm is a manner; this is the clamp.
        /// <para>
        /// The table walks each refusal from both ends: without its flag it throws its own id
        /// and writes nothing, and with it the write goes through. Each flag lifts only its
        /// own refusal, so a page that asked the wrong question does not get past the right
        /// one.
        /// </para>
        /// </summary>
        [Theory]
        // a hand made install, nothing else wrong with it
        [InlineData("outside", "bepinex.outsideUnconfirmed")]
        // another mod manager's doorstop config points into its own profile
        [InlineData("manager", "bepinex.drivenElsewhere")]
        // a core that is not a BepInEx 5 at all
        [InlineData("foreign", "bepinex.foreignCore")]
        public async Task A_press_over_somebody_elses_install_is_refused_until_the_host_has_said_yes(
            string shape, string refusalId)
        {
            ExistingInstall(
                coreAssembly: shape == "foreign" ? AForeignAssembly() : null,
                doorstopSection: null);

            if (shape == "manager")
                File.WriteAllText(Path.Combine(BaseDir, "doorstop_config.ini"),
                    "[General]\nenabled = true\ntarget_assembly = "
                    + Path.Combine(Root, "gale", "profiles", "Default", "BepInEx", "core",
                        "BepInEx.Preloader.dll") + "\n");

            var before = Snapshot();
            var (service, handler) = Build();

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.UpdateAsync(BaseExe, Nobody()));

            Assert.Equal(refusalId, refused.MessageId);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));

            // The right flag, and only the right flag, lets it through.
            var (wrongYes, _) = Build();
            var stillRefused = await Assert.ThrowsAsync<HostFacingException>(
                () => wrongYes.UpdateAsync(BaseExe, Nobody(),
                    options: new BepInExWriteOptions { OverUnrecognised = true }));
            Assert.Equal(refusalId, stillRefused.MessageId);

            var (asked, _) = Build();
            var result = await asked.UpdateAsync(BaseExe, Nobody(), options: new BepInExWriteOptions
            {
                OverOutside = true,
                OverDrivenElsewhere = true,
                OverForeign = true,
            });

            Assert.True(result.Installed);
            Assert.False(result.Skipped);
        }

        /// <summary>
        /// A window can never carry one of those answers. There is nobody at the keyboard on
        /// that path to have given one, so an options object that claims otherwise is taken at
        /// the value the path has rather than the one it was handed.
        /// </summary>
        [Fact]
        public async Task A_window_can_never_carry_an_answer_the_host_did_not_give()
        {
            Assert.False(BepInExWriteOptions.Window.CarriesAnAnswer);
            Assert.False(BepInExWriteOptions.Manual.CarriesAnAnswer);

            ExistingInstall(coreAssembly: AForeignAssembly());
            var before = Snapshot();

            var (service, handler) = Build();
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: new BepInExWriteOptions
            {
                Unattended = true,
                OverOutside = true,
                OverDrivenElsewhere = true,
                OverForeign = true,
                OverUnrecognised = true,
                AllowDowngrade = true,
            });

            Assert.True(result.Skipped);
            Assert.Equal(BepInExSkipReason.Foreign, result.SkipReason);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        /// <summary>
        /// A drifted install is one with no trusted note, so the outside clamp is what catches
        /// a press over it. Taking an install back off another tool is a decision, and it is
        /// the host's.
        /// </summary>
        [Fact]
        public async Task A_press_over_a_drifted_install_is_the_outside_clamp()
        {
            ExistingInstall();

            var (setup, _) = Build();
            await setup.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            // Somebody else writes over a file the note recorded.
            File.WriteAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll"),
                "another tool's core");

            var (service, _) = Build(new PackSpec { Version = "5.4.2360" });
            Assert.True(service.Status(BaseExe).Drifted);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.UpdateAsync(BaseExe, Nobody()));

            Assert.Equal("bepinex.outsideUnconfirmed", refused.MessageId);
        }

        /// <summary>
        /// Nothing installed at all is not somebody else's install. There is no loader to
        /// write over, so the first install does not ask a question about one.
        /// </summary>
        [Fact]
        public async Task A_first_install_is_not_asked_about_an_install_that_is_not_there()
        {
            var (service, _) = Build();
            var result = await service.InstallAsync(BaseExe, null, Nobody());

            Assert.True(result.Installed);
            Assert.False(result.Replaced);
        }

        // --------------------------------------------------- the drift check and a live write

        /// <summary>
        /// Ownership is never dropped over BakaLoader's own half-done work. A status read
        /// while a write is in flight sees a core that is mid-swap, which looks exactly like
        /// another tool having written here, and dropping ownership is a one-way door: the
        /// stale note keeps the install Outside on every later read.
        /// </summary>
        [Fact]
        public async Task A_status_read_during_a_write_never_calls_it_drift()
        {
            ExistingInstall();

            var (setup, _) = Build();
            await setup.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);
            Assert.True(setup.Status(BaseExe).MaintainedByBakaLoader);

            // A write in flight: the mark is held open, and the core no longer holds what the
            // note recorded because it is half way through being replaced.
            using (BepInExWriteSentinel.Hold(BaseDir, "20260920-030000"))
            {
                File.WriteAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll"),
                    "half of the new core");

                var during = setup.Status(BaseExe);
                Assert.False(during.Drifted);
                Assert.True(during.MaintainedByBakaLoader);
                Assert.False(File.Exists(Path.Combine(BaseDir, BepInExMarkerFile.StaleFileName)));
            }

            // The mark is gone with the write, and the same files now really are somebody
            // else's, so the very next read says so. The guard is about the moment, not about
            // the comparison.
            var after = setup.Status(BaseExe);
            Assert.True(after.Drifted);
        }

        /// <summary>
        /// The same guard for a write that STOPPED: the mark is still there, nobody is holding
        /// it, and the files are as half done as they were. The heal is what puts that right,
        /// and until it runs the note is not read back against them.
        /// </summary>
        [Fact]
        public async Task A_write_that_stopped_is_not_called_drift_either()
        {
            ExistingInstall();

            var (setup, _) = Build();
            await setup.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            BepInExWriteSentinel.Hold(BaseDir, "20260920-030000").Dispose();
            File.WriteAllText(Path.Combine(BaseDir, "BepInEx", BepInExWriteSentinel.FileName),
                "20260920-030000");
            File.WriteAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll"),
                "half of the new core");

            var status = setup.Status(BaseExe);

            Assert.True(status.InterruptedWrite);
            Assert.False(status.Drifted);
            Assert.True(status.MaintainedByBakaLoader);
        }

        // ------------------------------------------------------------- which pack goes in

        /// <summary>
        /// A pre-release, a deprecated package and a pack listed an hour ago are each left for
        /// the host to decide on. The proof is that nothing was FETCHED: the refusal happens
        /// before the download, so a window on a metered line does not pay for an archive it
        /// was never going to unpack.
        /// </summary>
        [Theory]
        [InlineData("5.4.2400-rc.1", false, false, BepInExSkipReason.PreRelease)]
        [InlineData("5.4.2350", true, false, BepInExSkipReason.Deprecated)]
        [InlineData("5.4.2350", false, true, BepInExSkipReason.Soak)]
        public async Task A_pack_a_window_may_not_write_is_never_even_fetched(
            string version, bool deprecated, bool brandNew, string reason)
        {
            ExistingInstall();
            var before = Snapshot();

            var (service, handler) = Build(
                new PackSpec { Version = version },
                created: brandNew ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddDays(-30),
                deprecated: deprecated ? true : null);

            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.Skipped);
            Assert.Equal(reason, result.SkipReason);
            Assert.Equal(version, result.Version);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));
            Assert.Null(BepInExMarkerFile.Read(BaseDir));
        }

        /// <summary>
        /// The soak carries WHEN, so the row can say how long the wait has left rather than
        /// only that there is one. Nothing else carries it: a pre-release never becomes
        /// eligible by waiting.
        /// </summary>
        [Fact]
        public async Task A_pack_that_is_still_soaking_says_when_it_becomes_eligible()
        {
            ExistingInstall();
            var listed = DateTime.UtcNow.AddHours(-2);

            var (service, _) = Build(created: listed);
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.Equal(BepInExSkipReason.Soak, result.SkipReason);
            Assert.NotNull(result.EligibleUtc);
            Assert.Equal(listed.AddHours(72), result.EligibleUtc.Value, TimeSpan.FromSeconds(2));

            var (again, _) = Build(new PackSpec { Version = "5.4.2400-rc.1" }, created: listed);
            Assert.Null((await again.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window))
                .EligibleUtc);
        }

        /// <summary>
        /// The manual button is not held back by any of them. A host who has read the version
        /// and pressed the button has made the decision the window is not allowed to make.
        /// </summary>
        [Fact]
        public async Task The_button_is_not_held_back_by_the_pack_policy()
        {
            ExistingInstall();

            var (service, _) = Build(
                new PackSpec { Version = "5.4.2400-rc.1" },
                created: DateTime.UtcNow.AddMinutes(-5), deprecated: true);

            var result = await service.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            Assert.True(result.Installed);
            Assert.False(result.Skipped);
            Assert.Equal("core 5.4.2400-rc.1",
                File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")));
        }

        /// <summary>
        /// The package page answers with no size, so the community index is asked for the size
        /// of that very version and the download is checked against it. This is the whole of
        /// the integrity story: without it nothing was ever compared with anything.
        /// </summary>
        [Fact]
        public async Task The_size_comes_from_the_community_index_when_the_page_leaves_it_out()
        {
            ExistingInstall();

            var (service, handler) = Build(liveSize: null);
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.False(result.Skipped);
            Assert.True(result.Installed);
            Assert.Single(handler.Requests);
        }

        /// <summary>
        /// An index that is answering about a DIFFERENT version has a size that belongs to
        /// another archive, and checking the download against it would fail every time. It is
        /// not taken, so the window is refused rather than misled.
        /// </summary>
        [Fact]
        public async Task A_size_for_another_version_is_not_taken()
        {
            ExistingInstall();
            var before = Snapshot();

            var bytes = Pack(new PackSpec { Version = "5.4.2350" });
            var thunderstore = new Mock<IThunderstoreClient>();
            thunderstore
                .Setup(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ThunderstoreLiveLookup
                {
                    Answered = true,
                    Package = Listing("5.4.2350"),
                });
            thunderstore
                .Setup(c => c.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(Listing("5.4.2100", bytes.LongLength));

            var provider = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            var service = new TestService(thunderstore.Object, provider,
                new InstallIsolationService(Mock.Of<IApplicationLogger>()), Mock.Of<IApplicationLogger>());

            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.Skipped);
            Assert.Equal(BepInExSkipReason.Unverified, result.SkipReason);
            Assert.Empty(provider.Handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        /// <summary>
        /// With a size from neither answer a window writes nothing, and the button still does.
        /// A host who pressed it gets the pack, and the log says what was not checked.
        /// </summary>
        [Fact]
        public async Task With_no_size_anywhere_the_window_refuses_and_the_button_does_not()
        {
            ExistingInstall();
            var before = Snapshot();

            var (window, handler) = Build(liveSize: null, indexSize: null);
            var refused = await window.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(refused.Skipped);
            Assert.Equal(BepInExSkipReason.Unverified, refused.SkipReason);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));

            var (pressed, _) = Build(liveSize: null, indexSize: null);
            var written = await pressed.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            Assert.True(written.Installed);
            Assert.False(written.Skipped);
        }

        /// <summary>
        /// A download that does not weigh what the listing said is not unpacked. The size now
        /// really comes from somewhere on the ordinary path, so this check really runs.
        /// </summary>
        [Fact]
        public async Task A_download_that_is_not_the_size_the_listing_named_is_refused()
        {
            ExistingInstall();
            var before = Snapshot();

            var (service, _) = Build(liveSize: 999999L, indexSize: 999999L);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.UpdateAsync(BaseExe, Nobody(), options: Pressed));

            Assert.Equal("bepinex.integrity", refused.MessageId);
            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        // --------------------------------------------------------------- telling the host

        /// <summary>
        /// The commonest write of all is the first adoption, and an install BakaLoader did not
        /// make has no pack version anywhere on disk. Without the fallback every sentence built
        /// from this field said the loader had been replaced by nothing.
        /// </summary>
        [Fact]
        public async Task What_was_here_before_falls_back_to_the_core_file_version()
        {
            var (lower, higher) = TwoVersionedFiveAssemblies();
            ExistingInstall(coreAssembly: lower);

            var (service, _) = Build(new PackSpec { CoreAssembly = higher });
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.Adopted);
            Assert.Null(result.PreviousPackVersion);
            Assert.Equal(BepInExService.FileVersionOf(lower), result.PreviousCoreVersion);
            Assert.Equal(BepInExService.FileVersionOf(lower), result.PreviousVersion);

            // And once there IS a note, the pack version is what it means again.
            var (second, _) = Build(new PackSpec { Version = "5.4.2360", CoreAssembly = higher });
            var next = await second.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.Equal("5.4.2350", next.PreviousPackVersion);
            Assert.Equal("5.4.2350", next.PreviousVersion);
        }

        /// <summary>
        /// A write that is refused still says what is on disk and what was on offer, because
        /// the sentence the row draws names both numbers.
        /// </summary>
        [Fact]
        public async Task A_refusal_carries_both_numbers_for_the_row()
        {
            var (lower, higher) = TwoVersionedFiveAssemblies();
            ExistingInstall(coreAssembly: higher);

            var (service, _) = Build(new PackSpec { CoreAssembly = lower });
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.Equal(BepInExSkipReason.Newer, result.SkipReason);
            Assert.Equal(BepInExService.FileVersionOf(higher), result.PreviousCoreVersion);
            Assert.Equal(BepInExService.FileVersionOf(lower), result.Version);
        }

        /// <summary>
        /// The file system saying no in the middle of a rename is still a refusal the page has
        /// words for. A raw .NET sentence in a toast tells a host nothing they can act on.
        /// </summary>
        [Fact]
        public async Task A_write_that_falls_over_still_refuses_with_an_id()
        {
            ExistingInstall();

            var (service, _) = Build(failCoreCopyAfter: 1);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.UpdateAsync(BaseExe, Nobody(), options: Pressed));

            Assert.Equal("bepinex.writeFailed", refused.MessageId);
            Assert.Contains("detail", refused.Params.Keys);

            // and the core it could not replace is still the one that was here
            Assert.Equal("core (the one that was here)",
                File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")));
        }

        // -------------------------------------------- profiles that hold copies, not links

        /// <summary>
        /// A profile provisioned onto another volume follows the base's core through a
        /// junction but holds its own COPY of the loose loader files. After a write it would
        /// otherwise run the new core under the old winhttp.dll, which is the two halves of one
        /// loader disagreeing and a server that comes up with no mods and nothing in the log.
        /// </summary>
        [Fact]
        public async Task A_profile_that_holds_copies_gets_its_loader_files_refreshed()
        {
            if (!JunctionsWork()) return;

            var isolated = IsolatedProfile("Quiet");
            ExistingInstall();

            // The first write links the profile up, the same as any first install does.
            var (first, _) = Build(new PackSpec { Version = "5.4.2350" });
            await first.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            // Now make it look like a profile on another volume: the core is still the base's
            // through the junction, but the loader file beside the executable is a copy of its
            // own, and it is stale.
            BreakTheLink(Path.Combine(isolated, "winhttp.dll"), "a copy from the old pack");

            var (second, _) = Build(new PackSpec { Version = "5.4.2360" });
            var result = await second.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            Assert.Equal(1, result.ProfileLoaderFilesRefreshed);
            Assert.Equal(File.ReadAllText(Path.Combine(BaseDir, "winhttp.dll")),
                File.ReadAllText(Path.Combine(isolated, "winhttp.dll")));

            // The core is still the base's, untouched by this pass.
            Assert.True(new DirectoryInfo(Path.Combine(isolated, "BepInEx", "core"))
                .Attributes.HasFlag(FileAttributes.ReparsePoint));
        }

        /// <summary>
        /// A profile with a core of its own is not part of this install's loader at all.
        /// Refreshing the file the game loads while the core it points at stays where it was
        /// would be the very mismatch the pass above exists to stop.
        /// </summary>
        [Fact]
        public async Task A_profile_with_its_own_real_core_is_left_entirely_alone()
        {
            var isolated = IsolatedProfile("Loud");
            var ownCore = Path.Combine(isolated, "BepInEx", "core");
            Directory.CreateDirectory(ownCore);
            File.WriteAllText(Path.Combine(ownCore, "BepInEx.dll"), "this profile's own core");
            File.WriteAllText(Path.Combine(isolated, "winhttp.dll"), "this profile's own loader file");

            ExistingInstall();

            var (service, _) = Build();
            var result = await service.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            Assert.Equal(0, result.ProfileLoaderFilesRefreshed);
            Assert.Equal("this profile's own loader file",
                File.ReadAllText(Path.Combine(isolated, "winhttp.dll")));
            Assert.Equal("this profile's own core",
                File.ReadAllText(Path.Combine(ownCore, "BepInEx.dll")));
        }

        /// <summary>
        /// The host's doorstop_config.ini is theirs in a profile exactly as it is in the base,
        /// so it only moves on the write that had to replace the base's too.
        /// </summary>
        [Fact]
        public async Task A_profiles_doorstop_config_follows_the_rule_the_base_one_does()
        {
            if (!JunctionsWork()) return;

            var isolated = IsolatedProfile("Quiet");
            ExistingInstall();

            var (first, _) = Build(new PackSpec { Version = "5.4.2350" });
            await first.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            BreakTheLink(Path.Combine(isolated, "doorstop_config.ini"),
                "[General]\nenabled = true\nredirect_output_log = true\n");

            // An ordinary write, where the base's own doorstop config is kept.
            var (second, _) = Build(new PackSpec { Version = "5.4.2360" });
            var kept = await second.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            Assert.False(kept.DoorstopReplaced);
            Assert.Contains("redirect_output_log",
                File.ReadAllText(Path.Combine(isolated, "doorstop_config.ini")));

            // A loader generation change, where it cannot be kept anywhere.
            var (third, _) = Build(new PackSpec
            {
                Version = "5.4.2370",
                DoorstopVersion = "3.0.0",
                DoorstopSection = BepInExDoorstop.LegacySection,
            });
            var replaced = await third.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            Assert.True(replaced.DoorstopReplaced);
            Assert.Equal(File.ReadAllText(Path.Combine(BaseDir, "doorstop_config.ini")),
                File.ReadAllText(Path.Combine(isolated, "doorstop_config.ini")));
        }

        // ------------------------------------------------------------------ the antivirus repair

        /// <summary>
        /// SECTION J, the sentence the wiki calls the single most common way a working install
        /// stops loading: an antivirus takes winhttp.dll, and the next window puts it back.
        /// <para>
        /// This is the WRITE rather than the rule. The rule was tested from the day it was
        /// written and answered Apply; the writer then refused every one of these on the
        /// "same core version, different bytes, so somebody patched it" guard, because a file
        /// that is GONE reads as "not the same bytes" exactly like one that was edited, and on
        /// a repair the version on disk is always the pack's own. So the window declined, the
        /// row was told a false sentence about BakaLoader's own install, and the file stayed
        /// gone for ever.
        /// </para>
        /// </summary>
        [Fact]
        public async Task The_window_puts_a_missing_loader_file_back()
        {
            // The pack ships a real versioned core, so that after the adoption the version on
            // disk and the pack's read the same, which is what every repair looks like and what
            // the guard this used to die on keyed off.
            var (lower, _) = TwoVersionedFiveAssemblies();
            ExistingInstall();
            var (service, _) = Build(new PackSpec { CoreAssembly = lower });
            await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            var loader = Path.Combine(BaseDir, "winhttp.dll");
            File.Delete(loader);

            var gone = service.Status(BaseExe);
            Assert.False(gone.Installed);
            Assert.Contains("winhttp.dll", gone.MissingFiles);

            // The window is asked the same way the restart hook asks it, and the SERVICE is
            // what has to answer: the rule already said Apply here before this fix.
            Assert.Equal(BepInExUnattendedAction.Apply, BepInExUnattended.Decide(
                consentEffective: true, installed: gone.Installed, installedVersion: gone.PackVersion,
                latestVersion: "5.4.2350", otherServersRunning: false,
                filesMissing: gone.MissingFiles.Count > 0));

            var (repair, _) = Build(new PackSpec { CoreAssembly = lower });
            var result = await repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.False(result.Skipped);
            Assert.True(File.Exists(loader));
            Assert.True(repair.Status(BaseExe).Installed);
            Assert.Empty(repair.Status(BaseExe).MissingFiles);
        }

        /// <summary>
        /// The same for a CORE file, which is the case the version guard was written for and
        /// the one it got wrong: 0Harmony.dll gone leaves BepInEx.dll where it is, so the
        /// install still reads as installed and its core version still equals the pack's.
        /// </summary>
        [Fact]
        public async Task The_window_puts_a_missing_core_file_back()
        {
            // A REAL versioned assembly in the PACK, because the guard this used to die on only
            // fires when both core versions read and match, and after an adoption they always
            // do: the core on disk is the one BakaLoader wrote out of this very pack. The
            // install it starts from carries the fixture's plain text core, whose version reads
            // as nothing, so the adoption itself is not the thing being tested here.
            var (lower, _) = TwoVersionedFiveAssemblies();
            ExistingInstall();
            var (service, _) = Build(new PackSpec { CoreAssembly = lower });
            await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);
            Assert.True(service.Status(BaseExe).MaintainedByBakaLoader);

            var harmony = Path.Combine(BaseDir, "BepInEx", "core", "0Harmony.dll");
            File.Delete(harmony);

            var gone = service.Status(BaseExe);
            Assert.True(gone.Installed);
            Assert.False(gone.Drifted);
            Assert.Equal(gone.CoreVersion, BepInExService.FileVersionOf(lower));
            Assert.Contains(gone.MissingFiles, f => f.EndsWith("0Harmony.dll", StringComparison.OrdinalIgnoreCase));

            var (repair, _) = Build(new PackSpec { CoreAssembly = lower });
            var result = await repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.False(result.Skipped);
            Assert.True(File.Exists(harmony));
            Assert.Empty(repair.Status(BaseExe).MissingFiles);
        }

        /// <summary>
        /// And the guard that fix loosened still holds where it was written to hold: a core
        /// somebody PATCHED, same version and different bytes with nothing missing, is left
        /// exactly as it is by a window.
        /// </summary>
        [Fact]
        public async Task A_patched_core_with_nothing_missing_is_still_left_alone()
        {
            var (lower, _) = TwoVersionedFiveAssemblies();
            ExistingInstall(coreAssembly: lower);
            var before = Snapshot();

            var (service, _) = Build(new PackSpec { CoreAssembly = lower, CoreFlavour = " stock" });

            // The clause the repair fix added, asked the other way round: nothing is missing
            // here, so the guard still answers.
            Assert.Empty(service.Status(BaseExe).MissingFiles);

            var skipped = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(skipped.Skipped);
            Assert.Equal(BepInExSkipReason.Foreign, skipped.SkipReason);
            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        // -------------------------------------------- the repair is checked against the note

        /// <summary>
        /// An install whose note names a pack, with one loader file taken off it. Every test
        /// below starts here: it is the antivirus case, and it is the one write that fetches a
        /// version the listing may no longer say anything at all about.
        /// </summary>
        private void AnInstallMissingItsLoaderFile(string version = "5.4.2350")
        {
            ExistingInstall();
            var (service, _) = Build(new PackSpec { Version = version });
            service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window).GetAwaiter().GetResult();

            Assert.True(service.Status(BaseExe).MaintainedByBakaLoader);
            File.Delete(Path.Combine(BaseDir, "winhttp.dll"));
            Assert.Contains("winhttp.dll", service.Status(BaseExe).MissingFiles);
        }

        /// <summary>
        /// Nothing was added or changed AND nothing went. The one-way comparison used on its
        /// own reads a deleted file as no difference at all, which is the half that matters
        /// most on a path whose whole promise is that it put nothing back.
        /// </summary>
        private static void AssertUnchanged(
            Dictionary<string, string> before, Dictionary<string, string> after)
        {
            Assert.Empty(ChangedBetween(before, after));
            Assert.Empty(ChangedBetween(after, before));
            Assert.Equal(before.Count, after.Count);
        }

        /// <summary>
        /// Takes the digest off one entry of the note and leaves the entry itself where it is,
        /// which is what BakaLoader's own writer leaves behind when a file would not read as it
        /// was being recorded.
        /// </summary>
        private void BlankTheNotesDigestFor(string relativePath)
        {
            var note = BepInExMarkerFile.Read(BaseDir);
            var entry = note.Files.Single(
                f => string.Equals(f.Path, relativePath, StringComparison.OrdinalIgnoreCase));

            Assert.False(string.IsNullOrWhiteSpace(entry.Sha256));
            entry.Sha256 = null;
            BepInExMarkerFile.Write(BaseDir, note);
        }

        /// <summary>
        /// The repair's own check, asked the way it is meant to answer: the archive really is
        /// the one the note recorded, so it goes in, and it goes in with NO size anywhere.
        /// <para>
        /// That second half is the whole point of the check. A repair fetches the version the
        /// note names rather than the newest one, and once Thunderstore has moved on nothing on
        /// the listing describes that version: the size and the digest are dropped, and what is
        /// left standing behind the archive is the note.
        /// </para>
        /// </summary>
        [Fact]
        public async Task A_repair_whose_pack_matches_the_note_goes_in_with_no_size_to_check()
        {
            AnInstallMissingItsLoaderFile();

            var (repair, handler) = Build(new PackSpec { Version = "5.4.2350" },
                liveSize: null, indexSize: null);
            var result = await repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.False(result.Skipped);
            Assert.True(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
            Assert.True(repair.Status(BaseExe).Installed);
            Assert.Empty(repair.Status(BaseExe).MissingFiles);
            Assert.NotEmpty(handler.Requests);
        }

        /// <summary>
        /// ONE file in the archive holding something else, and the repair puts nothing back.
        /// <para>
        /// This is the hole the check was written for. The version the note names is one
        /// Thunderstore has moved on from, so the community index answers with no size for it,
        /// the digest goes with the size, and what came down was unpacked over an install on
        /// nobody's word. The note has held a SHA256 for every one of those files since the day
        /// BakaLoader wrote them, and one that does not match means this is not that pack.
        /// </para>
        /// </summary>
        [Fact]
        public async Task A_repair_whose_pack_holds_one_different_file_puts_nothing_back()
        {
            AnInstallMissingItsLoaderFile();
            var before = Snapshot();

            var (repair, _) = Build(
                new PackSpec { Version = "5.4.2350", HarmonyFlavour = " (not the build that went in here)" },
                liveSize: null, indexSize: null);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Manual));

            Assert.Equal("bepinex.repairMismatch", refused.MessageId);
            Assert.Equal("5.4.2350", refused.Params["version"]);

            // byte for byte, and the file that went is still gone: a repair that refuses has
            // to leave the install exactly as broken as it found it rather than half mended
            AssertUnchanged(before, Snapshot());
            Assert.False(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
            Assert.Equal("5.4.2350", BepInExMarkerFile.Read(BaseDir).Version);
        }

        /// <summary>
        /// The same refusal for a file the archive does not carry AT ALL. A pack missing a core
        /// assembly the note lists is not the pack that was written here either, and unpacking
        /// it would leave an install whose note claims files that are not on disk.
        /// </summary>
        [Fact]
        public async Task A_repair_whose_pack_is_missing_a_listed_core_file_puts_nothing_back()
        {
            AnInstallMissingItsLoaderFile();
            var before = Snapshot();

            var (repair, _) = Build(new PackSpec { Version = "5.4.2350", ShipHarmony = false },
                liveSize: null, indexSize: null);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Manual));

            Assert.Equal("bepinex.repairMismatch", refused.MessageId);
            AssertUnchanged(before, Snapshot());
            Assert.False(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
        }

        /// <summary>
        /// And the rule is a REPAIR's rule. An ordinary update writes a pack that differs from
        /// the note on purpose, because a new pack is meant to differ: holding one to the old
        /// note would refuse every update there is.
        /// <para>
        /// One install, walked through both: the archive that is refused as a repair, the mend
        /// that puts the install back together, and then a NEW pack whose core files differ
        /// from every digest the note holds, written without a murmur.
        /// </para>
        /// </summary>
        [Fact]
        public async Task An_ordinary_update_is_not_held_to_the_note_the_way_a_repair_is()
        {
            AnInstallMissingItsLoaderFile();

            var (repair, _) = Build(
                new PackSpec { Version = "5.4.2350", HarmonyFlavour = " (not the build that went in here)" },
                liveSize: null, indexSize: null);
            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Manual));
            Assert.Equal("bepinex.repairMismatch", refused.MessageId);

            // The install is mended with the pack it was written from, so nothing is missing
            // any more and the next write is an ordinary update rather than a repair.
            var (mend, _) = Build(new PackSpec { Version = "5.4.2350" }, liveSize: null, indexSize: null);
            Assert.False((await mend.UpdateAsync(BaseExe, Nobody(),
                options: BepInExWriteOptions.Window)).Skipped);
            Assert.Empty(mend.Status(BaseExe).MissingFiles);

            var (update, _) = Build(new PackSpec { Version = "5.4.2400" });
            var written = await update.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.False(written.Skipped);
            Assert.True(written.Installed);
            Assert.Equal("5.4.2400", written.Version);
            Assert.Equal("harmony 5.4.2400",
                File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "0Harmony.dll")));
        }

        /// <summary>
        /// The window path. Nobody is at the keyboard, so there is no toast to throw into: the
        /// answer is a refusal the result carries, with the reason the row words it from, and
        /// the install is left exactly as it was.
        /// </summary>
        [Fact]
        public async Task An_unattended_repair_of_a_pack_that_does_not_match_is_recorded_as_a_refusal()
        {
            AnInstallMissingItsLoaderFile();
            var before = Snapshot();

            var (repair, _) = Build(
                new PackSpec { Version = "5.4.2350", HarmonyFlavour = " (not the build that went in here)" },
                liveSize: null, indexSize: null);

            var result = await repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(result.Skipped);
            Assert.Equal(BepInExSkipReason.RepairMismatch, result.SkipReason);
            Assert.Equal("5.4.2350", result.Version);

            AssertUnchanged(before, Snapshot());
            Assert.False(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
            Assert.True(repair.Status(BaseExe).MaintainedByBakaLoader);
        }

        /// <summary>
        /// An archive that changes nothing the note named and simply carries ONE MORE assembly
        /// under the core, and the repair still puts nothing back.
        /// <para>
        /// Reading the note's list and asking the archive for each of those files answers the
        /// wrong question on its own. The core is copied WHOLE, so a file that is only in the
        /// archive lands in the folder the loader resolves assemblies from, and the note is
        /// rewritten off that folder afterwards: the addition would then be recorded with its
        /// own digest and every reading after that would call the install untouched. Adding a
        /// file has to be as refused as changing one, or the check is a check nobody has to
        /// get past.
        /// </para>
        /// </summary>
        [Fact]
        public async Task A_repair_whose_pack_carries_a_core_file_the_note_never_named_puts_nothing_back()
        {
            AnInstallMissingItsLoaderFile();
            var before = Snapshot();

            var (repair, _) = Build(
                new PackSpec { Version = "5.4.2350", ExtraCoreFile = "Uninvited.dll" },
                liveSize: null, indexSize: null);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Manual));

            Assert.Equal("bepinex.repairMismatch", refused.MessageId);
            AssertUnchanged(before, Snapshot());
            Assert.False(File.Exists(Path.Combine(BaseDir, "BepInEx", "core", "Uninvited.dll")));
            Assert.False(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));

            // and the note was not rewritten around it either, which is what would have made
            // the addition permanent and invisible
            Assert.DoesNotContain(BepInExMarkerFile.Read(BaseDir).Files,
                f => f.Path.EndsWith("Uninvited.dll", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The other way a note gets written, and a repair on one of those still goes through.
        /// <para>
        /// Asking the archive whether it carries anything the note never named is only safe
        /// because a note's loader files are always exactly one pack's: the core is moved out
        /// whole and the pack's moved in, so the two sets cannot drift apart. The ADOPTION that
        /// writes its note from the disk instead is the case worth proving rather than assuming,
        /// and it reaches that note only after the pack and the disk have been compared both
        /// ways and found identical. So the note it leaves names that pack's files and no
        /// others, and a repair off it is an ordinary repair.
        /// </para>
        /// </summary>
        [Fact]
        public async Task A_repair_goes_through_on_a_note_the_adoption_wrote_from_the_disk()
        {
            ExistingInstall();

            // The pack goes in, then the note is taken away, which is the state a hand-made
            // install is in. The next write finds the disk already holding the pack and adopts
            // it by note alone, writing that note from what is on disk.
            var (setup, _) = Build();
            await setup.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);
            File.Delete(Path.Combine(BaseDir, BepInExMarkerFile.FileName));

            var (adopt, _) = Build();
            Assert.True((await adopt.UpdateAsync(BaseExe, Nobody(),
                options: BepInExWriteOptions.Window)).Adopted);

            File.Delete(Path.Combine(BaseDir, "winhttp.dll"));
            Assert.Contains("winhttp.dll", adopt.Status(BaseExe).MissingFiles);

            var (repair, _) = Build(new PackSpec { Version = "5.4.2350" },
                liveSize: null, indexSize: null);
            var result = await repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.False(result.Skipped);
            Assert.True(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
            Assert.Empty(repair.Status(BaseExe).MissingFiles);
        }

        /// <summary>
        /// A note entry with no digest is not a file that has been vouched for, so an archive
        /// is not let past on one.
        /// <para>
        /// BakaLoader writes those itself: the note is built by hashing each file just after
        /// the core is swapped in, and a file that will not read at that moment is recorded
        /// with no digest at all. A file held open for a second as it is being written is
        /// exactly the antivirus case this whole path exists for, so the undigested entry it
        /// leaves is permanent. Skipping the entry would mean the repair writes whatever the
        /// archive holds for that one file, unchecked, forever after.
        /// </para>
        /// </summary>
        [Fact]
        public async Task A_repair_is_refused_over_a_file_the_note_recorded_with_no_digest()
        {
            AnInstallMissingItsLoaderFile();
            BlankTheNotesDigestFor("BepInEx/core/0Harmony.dll");
            var before = Snapshot();

            var (repair, _) = Build(
                new PackSpec { Version = "5.4.2350", HarmonyFlavour = " (a different build entirely)" },
                liveSize: null, indexSize: null);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Manual));

            Assert.Equal("bepinex.repairMismatch", refused.MessageId);
            AssertUnchanged(before, Snapshot());
            Assert.Equal("harmony 5.4.2350",
                File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "0Harmony.dll")));
        }

        /// <summary>
        /// The note losing its digests WHILE the archive comes down, and the repair refusing
        /// rather than sailing through unchecked.
        /// <para>
        /// The check re-reads the note, because the download took minutes and the note is an
        /// ordinary file on an ordinary disk. A note that has nothing left to hold the archive
        /// to is not the same thing as an archive that passed: the write was called a repair on
        /// the strength of a note that DID vouch for files, so the one input that matters going
        /// missing has to close the path rather than open it.
        /// </para>
        /// </summary>
        [Fact]
        public async Task A_repair_whose_note_loses_its_digests_mid_download_puts_nothing_back()
        {
            AnInstallMissingItsLoaderFile();
            var before = Snapshot();

            var (repair, _) = Build(new PackSpec { Version = "5.4.2350" },
                liveSize: null, indexSize: null,
                whileFetching: () =>
                {
                    var note = BepInExMarkerFile.Read(BaseDir);
                    foreach (var entry in note.Files) entry.Sha256 = null;
                    BepInExMarkerFile.Write(BaseDir, note);
                });

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Manual));

            Assert.Equal("bepinex.repairMismatch", refused.MessageId);
            Assert.False(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));

            // The note is the one file that DID move, and the test is what moved it, so it is
            // the only difference allowed in EITHER direction. Nothing was added beside it and
            // nothing went.
            var after = Snapshot();
            Assert.Equal(new[] { BepInExMarkerFile.FileName }, ChangedBetween(before, after));
            Assert.Equal(new[] { BepInExMarkerFile.FileName }, ChangedBetween(after, before));
            Assert.Equal(before.Count, after.Count);
        }

        // ------------------------------------------------------------------ nothing is reported wrong

        /// <summary>
        /// SECTION H. The loader is in by the time the note is written, so a note that will not
        /// write is not a write that did not happen: it used to come out of here as a raw .NET
        /// sentence with no id for the page to word, and the very next status read called
        /// BakaLoader's own fresh install somebody else's work and set the note aside, which is
        /// a one way door.
        /// </summary>
        [Fact]
        public async Task A_note_that_will_not_write_is_still_a_refusal_the_page_has_words_for()
        {
            ExistingInstall();
            var (service, _) = Build();

            var note = Path.Combine(BaseDir, BepInExMarkerFile.FileName);
            File.WriteAllText(note, "{}");

            using (new FileStream(note, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var refused = await Assert.ThrowsAsync<HostFacingException>(
                    () => service.UpdateAsync(BaseExe, Nobody(), options: Pressed));

                Assert.Equal("bepinex.writeFailed", refused.MessageId);
            }

            // The loader really did go in, which is the half the old sentence denied.
            Assert.Equal("core 5.4.2350",
                File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")));
        }

        /// <summary>
        /// SECTION E. A root file the new pack does NOT ship is MOVED into the backup, and a
        /// move is as much a write to that file as a copy over it. It used to sit outside the
        /// pre-flight, so a held .doorstop_version threw AFTER the core, winhttp.dll and the
        /// doorstop config had all been replaced, under a sentence saying the install had been
        /// left exactly as it was.
        /// </summary>
        [Fact]
        public async Task A_root_file_the_pack_does_not_ship_is_proved_before_anything_moves()
        {
            ExistingInstall(doorstopVersion: "4.4.0");
            var before = Snapshot();
            var stale = Path.Combine(BaseDir, ".doorstop_version");

            var (service, _) = Build(new PackSpec { DoorstopVersion = null });

            using (new FileStream(stale, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var refused = await Assert.ThrowsAsync<HostFacingException>(
                    () => service.UpdateAsync(BaseExe, Nobody(), options: Pressed));

                Assert.Equal("bepinex.locked", refused.MessageId);
                Assert.Equal(".doorstop_version", refused.Params["file"]);
            }

            Assert.Empty(ChangedBetween(before, Snapshot()));
            Assert.Null(BepInExMarkerFile.Read(BaseDir));
            Assert.False(Directory.Exists(BackupRoot));
        }

        // ------------------------------------------------------------------ what is here at all

        /// <summary>
        /// SECTION O. The outside clamp asks whether the core FILE is here, not whether
        /// anything can read a version out of it, and this is the shape where the difference
        /// showed: an install BakaLoader did not make whose winhttp.dll an antivirus has taken.
        /// Installed is false (half of it is gone), Damaged is false (the core assembly is
        /// right there), and the fixture's core is plain text so CoreVersion reads null. On the
        /// old question every one of those said "there is nothing here", and a manual install
        /// wrote a fresh pack straight over the host's own core with no confirm at all.
        /// </summary>
        [Fact]
        public async Task An_outside_core_whose_loader_file_is_gone_is_still_an_install_to_ask_about()
        {
            ExistingInstall();
            File.Delete(Path.Combine(BaseDir, "winhttp.dll"));

            var before = Snapshot();
            var (service, handler) = Build();
            var status = service.Status(BaseExe);

            Assert.False(status.Installed);
            Assert.False(status.LoaderFilePresent);
            Assert.False(status.Damaged);
            Assert.Null(status.CoreVersion);
            Assert.True(status.CoreFilePresent);
            Assert.False(status.MaintainedByBakaLoader);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.InstallAsync(BaseExe, null, Nobody()));

            Assert.Equal("bepinex.outsideUnconfirmed", refused.MessageId);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));

            // And with the host's yes it goes in, which is what makes the row's button work.
            var (over, _) = Build();
            var result = await over.InstallAsync(BaseExe, null, Nobody(), options: Pressed);
            Assert.True(result.Installed);
            Assert.True(over.Status(BaseExe).Installed);
        }

        // ------------------------------------------------------------------ the backup that is kept

        /// <summary>
        /// The keep-three rule drops the OLDEST first, and the oldest is the one backup that
        /// ever holds a loader BakaLoader did not write: after four ordinary writes the host's
        /// own original core was gone for good, and that one they cannot fetch again off
        /// Thunderstore. It is marked on the way in and the pruning steps over it.
        /// </summary>
        [Fact]
        public async Task The_backup_holding_the_hosts_own_loader_is_never_pruned()
        {
            ExistingInstall(coreFlavour: " (the host's very own)");
            var theirs = BepInExService.Sha256Of(
                Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll"));

            var (adopt, _) = Build(new PackSpec { Version = "5.4.2350" });
            var first = await adopt.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            var adoption = BepInExBackups.Adoption(BaseDir);
            Assert.NotNull(adoption);
            Assert.Equal(first.BackupStamp, adoption.Stamp);
            Assert.True(adoption.Adoption);

            // Four more writes, which is one more than the keep-three rule holds.
            foreach (var version in new[] { "5.4.2360", "5.4.2370", "5.4.2380", "5.4.2390" })
            {
                // The stamps are one second apart at worst, and the folder names are to the
                // second: a write inside the same second gets a "-2" name, which sorts after
                // the plain one and would make this test about string ordering rather than
                // about pruning.
                await Task.Delay(1100);
                var (next, _) = Build(new PackSpec { Version = version });
                await next.UpdateAsync(BaseExe, Nobody(), options: Pressed);
            }

            var kept = BepInExBackups.All(BaseDir).ToList();

            // Three ordinary ones plus the adoption one, which is still there and still holds
            // the very bytes the host had.
            Assert.Equal(4, kept.Count);
            Assert.Contains(kept, b => b.Stamp == adoption.Stamp);
            Assert.Equal(theirs, BepInExService.Sha256Of(
                Path.Combine(adoption.Stamp == null ? "" : BepInExBackups.Adoption(BaseDir).CoreFolder,
                    "BepInEx.dll")));

            // and it is on the status, by name, so the restore can offer it as itself
            Assert.Equal(adoption.Stamp, adopt.Status(BaseExe).AdoptionBackup);
        }

        /// <summary>
        /// A first install replaced nothing, so there is nothing of the host's to keep and
        /// nothing to mark. A write over BakaLoader's own install is not an adoption either.
        /// </summary>
        [Fact]
        public async Task Only_the_write_that_took_an_install_over_leaves_an_adoption_backup()
        {
            var (fresh, _) = Build();
            var first = await fresh.InstallAsync(BaseExe, null, Nobody());

            Assert.Null(first.BackupStamp);
            Assert.Null(BepInExBackups.Adoption(BaseDir));
            Assert.Null(fresh.Status(BaseExe).AdoptionBackup);

            await Task.Delay(1100);
            var (second, _) = Build(new PackSpec { Version = "5.4.2360" });
            await second.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.Null(BepInExBackups.Adoption(BaseDir));
        }

        /// <summary>
        /// Only the FIRST adoption is kept for good. A drift takes the note away, and the write
        /// that takes the install back is an adoption too: protecting every one of those would
        /// let a pair of tools writing over each other fill the folder for ever, and the one
        /// the host means by "my own BepInEx" is the one from before BakaLoader ever wrote here.
        /// </summary>
        [Fact]
        public async Task Only_the_first_adoption_is_the_one_kept_for_good()
        {
            ExistingInstall(coreFlavour: " (the host's very own)");
            var theirs = BepInExService.Sha256Of(
                Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll"));

            var (adopt, _) = Build(new PackSpec { Version = "5.4.2350" });
            await adopt.UpdateAsync(BaseExe, Nobody(), options: Pressed);
            var own = BepInExBackups.Adoption(BaseDir);
            Assert.NotNull(own);

            // Another tool writes here, BakaLoader lets go, and a press takes it back. That
            // second write is an adoption by every test the writer can make.
            await Task.Delay(1100);
            File.WriteAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll"),
                "a core some other tool put here");
            Assert.True(adopt.Status(BaseExe).Drifted);

            var (back, _) = Build(new PackSpec { Version = "5.4.2360" });
            await back.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            // Still the first one, and still the host's own bytes.
            Assert.Equal(own.Stamp, BepInExBackups.Adoption(BaseDir).Stamp);
            Assert.Equal(theirs, BepInExService.Sha256Of(
                Path.Combine(BepInExBackups.Adoption(BaseDir).CoreFolder, "BepInEx.dll")));

            // and the second one is an ordinary backup, so it rolls over with the rest
            foreach (var version in new[] { "5.4.2370", "5.4.2380", "5.4.2390" })
            {
                await Task.Delay(1100);
                var (next, _) = Build(new PackSpec { Version = version });
                await next.UpdateAsync(BaseExe, Nobody(), options: Pressed);
            }

            var kept = BepInExBackups.All(BaseDir).ToList();
            Assert.Equal(4, kept.Count);
            Assert.Contains(kept, b => b.Stamp == own.Stamp);
        }

        // ------------------------------------------------------------------ a repair is not an update

        /// <summary>
        /// PLANNER CORRECTION 2. The files that went were written out of the pack the note
        /// names, so that is the pack they come back from. Fetching the newest one instead
        /// would move a host to a loader nobody chose on the strength of an antivirus taking a
        /// file, and the three day wait in front of a new pack would then refuse the repair
        /// outright, leaving the commonest breakage there is unfixed for three days.
        /// </summary>
        [Fact]
        public async Task A_repair_puts_the_pack_the_note_names_back_and_the_soak_does_not_hold_it()
        {
            ExistingInstall();
            var (service, _) = Build(new PackSpec { Version = "5.4.2350" });
            await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            File.Delete(Path.Combine(BaseDir, "winhttp.dll"));

            // The site has moved on, and the pack it offers came out an hour ago: a window
            // would not take THAT pack, and it is not the one the repair wants anyway. The
            // address the repair asks for names 5.4.2350, so that is the archive that comes
            // back: the listing having moved on does not change what the old address serves.
            var (repair, handler) = Build(new PackSpec { Version = "5.4.2400" },
                created: DateTime.UtcNow.AddHours(-1),
                served: new PackSpec { Version = "5.4.2350" });
            var result = await repair.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.False(result.Skipped);
            Assert.Equal("5.4.2350", result.Version);
            Assert.True(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
            Assert.Equal("5.4.2350", BepInExMarkerFile.Read(BaseDir).Version);

            // the address it actually asked for names that version and not the newer one
            Assert.Contains(handler.Requests, r => r.Contains("5.4.2350"));
            Assert.DoesNotContain(handler.Requests, r => r.Contains("5.4.2400"));
        }

        /// <summary>
        /// And an ordinary update is still held by every one of those rules: only a repair
        /// walks past them, and only because the pack it writes is the one already installed.
        /// </summary>
        [Fact]
        public async Task An_update_with_nothing_missing_is_still_held_by_the_soak()
        {
            ExistingInstall();
            var (service, _) = Build(new PackSpec { Version = "5.4.2350" });
            await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            var (window, handler) = Build(new PackSpec { Version = "5.4.2400" },
                created: DateTime.UtcNow.AddHours(-1));
            var skipped = await window.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(skipped.Skipped);
            Assert.Equal(BepInExSkipReason.Soak, skipped.SkipReason);
            Assert.Empty(handler.Requests);
        }

        /// <summary>
        /// PLANNER CORRECTION 3. A note that could not be written must not turn into drift on
        /// the next read. The loader on disk is BakaLoader's own fresh write; the note beside
        /// it still describes the files it replaced, and comparing the two would call
        /// BakaLoader's own work somebody else's and set the note aside, which is a one way
        /// door: the stale file keeps the install Outside on every later read as well.
        /// </summary>
        [Fact]
        public async Task A_note_that_could_not_be_written_does_not_become_drift()
        {
            ExistingInstall();
            var (first, _) = Build(new PackSpec { Version = "5.4.2350" });
            await first.UpdateAsync(BaseExe, Nobody(), options: Pressed);
            Assert.True(first.Status(BaseExe).MaintainedByBakaLoader);

            var note = Path.Combine(BaseDir, BepInExMarkerFile.FileName);
            var (second, _) = Build(new PackSpec { Version = "5.4.2360" });

            using (new FileStream(note, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var refused = await Assert.ThrowsAsync<HostFacingException>(
                    () => second.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window));
                Assert.Equal("bepinex.writeFailed", refused.MessageId);
            }

            // Still held: the mark stands in front of the comparison, so nothing calls
            // BakaLoader's own fresh write somebody else's work.
            using (new FileStream(note, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var held = second.Status(BaseExe);
                Assert.False(held.Drifted);
                Assert.False(File.Exists(Path.Combine(BaseDir, BepInExMarkerFile.StaleFileName)));
                Assert.True(BepInExWriteSentinel.Present(BaseDir));

                // and the heal cannot settle it either while the file is held, so it leaves
                // the mark rather than opening the door
                Assert.Null(second.HealInterruptedWrite(BaseExe, Nobody()));
                Assert.True(BepInExWriteSentinel.Present(BaseDir));
                Assert.False(second.Status(BaseExe).Drifted);
            }

            // Whatever was holding it lets go, and the next heal settles the whole thing.
            Assert.Null(second.HealInterruptedWrite(BaseExe, Nobody()));
            Assert.False(BepInExWriteSentinel.Present(BaseDir));

            var status = second.Status(BaseExe);

            Assert.False(status.Drifted);
            Assert.False(File.Exists(Path.Combine(BaseDir, BepInExMarkerFile.StaleFileName)));
            Assert.False(status.MaintainedByBakaLoader);
            // the core really is the new one, which is the half the old sentence denied
            Assert.Equal("core 5.4.2360",
                File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")));
            // and one press puts it right, which a drifted install could never offer
            var (third, _) = Build(new PackSpec { Version = "5.4.2360" });
            var fixedUp = await third.UpdateAsync(BaseExe, Nobody(), options: Pressed);
            Assert.True(fixedUp.Installed);
            Assert.True(third.Status(BaseExe).MaintainedByBakaLoader);
        }

        /// <summary>
        /// And a mark over an install whose note DOES still describe it, which is the ordinary
        /// shape (a machine that died after both renames), is cleared with nothing touched.
        /// </summary>
        [Fact]
        public async Task A_mark_over_a_note_that_still_matches_is_just_cleared()
        {
            ExistingInstall();
            var (service, _) = Build();
            await service.UpdateAsync(BaseExe, Nobody(), options: Pressed);

            var before = Snapshot();
            using (BepInExWriteSentinel.Hold(BaseDir, "20260920-011500")) { }
            File.WriteAllText(BepInExWriteSentinel.PathOf(BaseDir), "20260920-011500");

            Assert.Null(service.HealInterruptedWrite(BaseExe, Nobody()));

            Assert.False(BepInExWriteSentinel.Present(BaseDir));
            Assert.True(service.Status(BaseExe).MaintainedByBakaLoader);
            Assert.Empty(ChangedBetween(before, Snapshot()));
        }

        /// <summary>
        /// PLANNER CORRECTION 4b. A version Thunderstore has taken down (<c>is_active</c> false)
        /// is never written by a window, and the button is not held back by it.
        /// </summary>
        [Fact]
        public async Task A_version_the_site_has_taken_down_is_never_written_unattended()
        {
            ExistingInstall();
            var before = Snapshot();

            var (service, handler) = Build(pulled: true);
            var skipped = await service.UpdateAsync(BaseExe, Nobody(), options: BepInExWriteOptions.Window);

            Assert.True(skipped.Skipped);
            Assert.Equal(BepInExSkipReason.Pulled, skipped.SkipReason);
            Assert.Equal("5.4.2350", skipped.Version);
            Assert.Empty(handler.Requests);
            Assert.Empty(ChangedBetween(before, Snapshot()));

            var (pressed, _) = Build(pulled: true);
            var written = await pressed.UpdateAsync(BaseExe, Nobody(), options: Pressed);
            Assert.True(written.Installed);
            Assert.False(written.Skipped);
        }

        /// <summary>
        /// Turns one of a profile's loader files into a real copy of its own.
        /// <para>
        /// Provisioning HARD LINKS these files when the profile is on the same volume as the
        /// base, which every test here is, so the two paths are one file on disk and writing
        /// through either changes both. That is the case the refresh pass has nothing to do
        /// for, and writing to the profile's path to set up a stale copy would quietly change
        /// the base's file instead. Deleting first drops this name from the link and leaves
        /// the base's file where it is, which is exactly what a profile on another volume,
        /// holding copies rather than links, looks like.
        /// </para>
        /// </summary>
        private static void BreakTheLink(string path, string content)
        {
            if (File.Exists(path)) File.Delete(path);
            File.WriteAllText(path, content);
        }

        /// <summary>An isolated profile folder of this family, with its own executable.</summary>
        private string IsolatedProfile(string name)
        {
            var instances = Path.Combine(Path.GetDirectoryName(BaseDir), ".bakaloader-instances");
            var isolated = Path.Combine(instances, name);
            Directory.CreateDirectory(isolated);
            File.WriteAllText(Path.Combine(isolated, "valheim_server.exe"), "the isolated copy");
            return isolated;
        }

        // ------------------------------------------------------------------ junctions

        private static void Junction(string link, string target)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc.WaitForExit(20000);
        }

        /// <summary>Whether this volume and this policy will make a junction at all.</summary>
        private bool JunctionsWork()
        {
            var target = Path.Combine(Root, "junction-probe-target");
            var link = Path.Combine(Root, "junction-probe-link");
            try
            {
                Directory.CreateDirectory(target);
                Junction(link, target);
                var ok = Directory.Exists(link)
                         && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (ok) Directory.Delete(link);
                return ok;
            }
            catch
            {
                return false;
            }
        }
    }
}
