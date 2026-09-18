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
    /// What the BepInEx installer actually writes, and what it refuses to.
    /// <para>
    /// Every archive here is built in memory to the shape of the real pack (a metadata tier
    /// over one folder whose CONTENTS go to the install root), every download is served by
    /// <see cref="RecordingHttpHandler"/> so nothing reaches thunderstore.io, and every
    /// folder lives under a temporary path. No real server install is ever touched.
    /// </para>
    /// </summary>
    public class BepInExServiceTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "bakaloader-bepinex-" + Guid.NewGuid().ToString("N"));
        private readonly string BaseDir;
        private readonly string BaseExe;

        public BepInExServiceTests()
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
        /// A junction-safe delete, because one of these tests makes real junctions and a
        /// recursive delete that follows one would reach outside the temp folder.
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

        // ------------------------------------------------------------------ the archive

        /// <summary>
        /// The pack, to the shape of the real one: four loose loader files, a core folder, a
        /// shipped config, the Linux and macOS libs, the two shell scripts, and a metadata
        /// tier above all of it that no installer ever copies.
        /// </summary>
        private static byte[] Pack(string version = "5.4.2350", params string[] extraInsidePack)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                const string root = "BepInExPack_Valheim/";

                Write(zip, root + "winhttp.dll", "the doorstop proxy " + version);
                Write(zip, root + "doorstop_config.ini", "[General]\nenabled = true\n");
                Write(zip, root + ".doorstop_version", "4.4.0");
                Write(zip, root + "changelog.txt",
                    "40 commits since v5.4.23.5\n\nChangelog (excluding merges):\n"
                    + "* (ef506e0) [AzumattDev] Bump Thunderstore version to " + version + "\n");

                Write(zip, root + "BepInEx/core/BepInEx.dll", "core " + version);
                Write(zip, root + "BepInEx/core/BepInEx.Preloader.dll", "preloader " + version);
                Write(zip, root + "BepInEx/core/0Harmony.dll", "harmony " + version);
                Write(zip, root + "BepInEx/config/BepInEx.cfg", "[Logging]\nshipped default\n");

                // Linux and macOS only, and the two shell scripts: inert on Windows and never
                // part of the allow list.
                Write(zip, root + "doorstop_libs/libdoorstop_x64.so", "elf");
                Write(zip, root + "doorstop_libs/libdoorstop_x64.dylib", "macho");
                Write(zip, root + "start_server_bepinex.sh", "#!/bin/sh\n");
                Write(zip, root + "start_game_bepinex.sh", "#!/bin/sh\n");

                foreach (var extra in extraInsidePack) Write(zip, root + extra, "surprise");

                // The metadata tier.
                Write(zip, "manifest.json",
                    "{\"name\":\"BepInExPack_Valheim\",\"version_number\":\"" + version + "\"}");
                Write(zip, "README.md", "readme");
                Write(zip, "CHANGELOG.md", "changelog");
                Write(zip, "icon.png", "png");
            }
            return buffer.ToArray();
        }

        /// <summary>An archive that is a plain mod, which is the thing that must be refused.</summary>
        private static byte[] AModArchive()
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, "SomeMod/SomeMod.dll", "a plugin");
                Write(zip, "manifest.json", "{\"name\":\"SomeMod\",\"version_number\":\"1.0.0\"}");
            }
            return buffer.ToArray();
        }

        private static void Write(ZipArchive zip, string path, string content)
        {
            using var stream = zip.CreateEntry(path).Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }

        // ------------------------------------------------------------------ the service

        private static ThunderstorePackage Listing(string version, long? size = null, string sha = null) => new()
        {
            Namespace = BepInExService.DefaultPackageOwner,
            Name = BepInExService.DefaultPackageName,
            Latest = new ThunderstorePackageVersion
            {
                VersionNumber = version,
                FileSize = size,
                Sha256 = sha,
            },
        };

        private (BepInExService Service, RecordingHttpHandler Handler) Build(
            string version = "5.4.2350", byte[] archive = null, long? size = null, string sha = null)
        {
            var bytes = archive ?? Pack(version);
            var provider = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

            var thunderstore = new Mock<IThunderstoreClient>();
            thunderstore
                .Setup(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ThunderstoreLiveLookup { Answered = true, Package = Listing(version, size, sha) });
            thunderstore
                .Setup(c => c.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(Listing(version, size, sha));

            var isolation = new InstallIsolationService(Mock.Of<IApplicationLogger>());
            return (new BepInExService(thunderstore.Object, provider, isolation, Mock.Of<IApplicationLogger>()),
                provider.Handler);
        }

        private (T Service, RecordingHttpHandler Handler) BuildWith<T>(
            Func<IThunderstoreClient, RecordingHttpClientProvider, IInstallIsolationService, IApplicationLogger, T> make,
            string version = "5.4.2350", byte[] archive = null)
            where T : BepInExService
        {
            var bytes = archive ?? Pack(version);
            var provider = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

            var thunderstore = new Mock<IThunderstoreClient>();
            thunderstore
                .Setup(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ThunderstoreLiveLookup { Answered = true, Package = Listing(version) });
            thunderstore
                .Setup(c => c.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(Listing(version));

            var isolation = new InstallIsolationService(Mock.Of<IApplicationLogger>());
            return (make(thunderstore.Object, provider, isolation, Mock.Of<IApplicationLogger>()), provider.Handler);
        }

        private static IEnumerable<BepInExProfileInstall> Nobody() => Array.Empty<BepInExProfileInstall>();

        // ------------------------------------------------------------------ the allow list

        /// <summary>
        /// The four loose files, the whole core and the shipped config, and NOTHING else. The
        /// archive here carries an extra file the pack does not ship, which is exactly what a
        /// repackaged or tampered pack would look like, and it must not reach the folder the
        /// server runs from.
        /// </summary>
        [Fact]
        public async Task An_install_writes_the_allow_list_and_nothing_else()
        {
            var (service, _) = Build(archive: Pack("5.4.2350", "extra.dll", "nested/deeper.dll"));

            var result = await service.InstallAsync(BaseExe, null, Nobody());

            Assert.True(result.Installed);
            Assert.False(result.Replaced);
            Assert.Equal("5.4.2350", result.Version);

            Assert.True(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
            Assert.True(File.Exists(Path.Combine(BaseDir, "doorstop_config.ini")));
            Assert.True(File.Exists(Path.Combine(BaseDir, ".doorstop_version")));
            Assert.True(File.Exists(Path.Combine(BaseDir, "changelog.txt")));
            Assert.True(File.Exists(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll")));
            Assert.True(File.Exists(Path.Combine(BaseDir, "BepInEx", "config", "BepInEx.cfg")));

            // The extras the archive carried
            Assert.False(File.Exists(Path.Combine(BaseDir, "extra.dll")));
            Assert.False(Directory.Exists(Path.Combine(BaseDir, "nested")));
            // The archive's own metadata tier
            Assert.False(File.Exists(Path.Combine(BaseDir, "manifest.json")));
            Assert.False(File.Exists(Path.Combine(BaseDir, "README.md")));
            Assert.False(File.Exists(Path.Combine(BaseDir, "icon.png")));
            // Linux and macOS baggage
            Assert.False(Directory.Exists(Path.Combine(BaseDir, "doorstop_libs")));
            Assert.False(File.Exists(Path.Combine(BaseDir, "start_server_bepinex.sh")));
            Assert.False(File.Exists(Path.Combine(BaseDir, "start_game_bepinex.sh")));
        }

        /// <summary>
        /// The core is replaced WHOLE, and whole means the subfolders too. BepInEx 5's core is
        /// flat today, so nothing is lost by the pack that ships now; what this stops is a
        /// pack that grows a tier (a net472 folder, a runtimes folder) installing a core
        /// missing those assemblies while the note beside it claims a complete write.
        /// </summary>
        [Fact]
        public async Task The_whole_core_is_written_subfolders_and_all()
        {
            var (service, _) = Build(archive: Pack("5.4.2350", "BepInEx/core/net472/Mono.Cecil.dll"));

            var result = await service.InstallAsync(BaseExe, null, Nobody());
            Assert.True(result.Installed);

            var nested = Path.Combine(BaseDir, "BepInEx", "core", "net472", "Mono.Cecil.dll");
            Assert.True(File.Exists(nested), "the core's subfolder never reached the install");

            // and the note records it, by the path the install reads it at
            var marker = BepInExMarkerFile.Read(BaseDir);
            Assert.NotNull(marker);
            Assert.Contains(marker.Files, f => f.Path == "BepInEx/core/net472/Mono.Cecil.dll");
        }

        /// <summary>
        /// A sequence that answers differently each time it is walked, which is what the
        /// bridge hands in: an iterator over the live session registry. The list a test would
        /// normally pass cannot show this, because its second answer is its first one.
        /// </summary>
        private sealed class ChangingProfiles : IEnumerable<BepInExProfileInstall>
        {
            private readonly Queue<BepInExProfileInstall[]> Answers;

            public ChangingProfiles(params BepInExProfileInstall[][] answers)
                => Answers = new Queue<BepInExProfileInstall[]>(answers);

            public int Asked { get; private set; }

            public IEnumerator<BepInExProfileInstall> GetEnumerator()
            {
                Asked++;
                var answer = Answers.Count > 1 ? Answers.Dequeue() : Answers.Peek();
                return ((IEnumerable<BepInExProfileInstall>)answer).GetEnumerator();
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// The refusal is asked TWICE, and the second ask is what counts: it happens with the
        /// archive already on disk and not one byte written. A fifty megabyte fetch on a slow
        /// line is minutes, and a realm started inside that window would otherwise have its
        /// BepInEx/core backed up, cleared and rewritten underneath it.
        /// </summary>
        [Fact]
        public async Task A_server_started_during_the_download_still_stops_the_write()
        {
            var running = new BepInExProfileInstall
            {
                ProfileName = "Final Sunset",
                ServerExePath = BaseExe,
                Running = true,
            };
            var profiles = new ChangingProfiles(Array.Empty<BepInExProfileInstall>(), new[] { running });

            var (service, _) = Build();

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.InstallAsync(BaseExe, null, profiles));

            Assert.Equal("bepinex.serversRunning", refused.MessageId);
            Assert.Equal("Final Sunset", refused.Params["names"]);
            Assert.True(profiles.Asked >= 2, "the writer only asked once, before the download");

            // and nothing was written: the first ask let it through, the second stopped it
            Assert.False(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
            Assert.False(Directory.Exists(Path.Combine(BaseDir, "BepInEx", "core")));
            Assert.Null(BepInExMarkerFile.Read(BaseDir));
        }

        /// <summary>The plugins folder is never created, cleared or touched by an install.</summary>
        [Fact]
        public async Task An_install_never_touches_the_mods()
        {
            var plugins = Path.Combine(BaseDir, "BepInEx", "plugins", "Azumatt-AzuAntiItemLag");
            Directory.CreateDirectory(plugins);
            File.WriteAllText(Path.Combine(plugins, "AzuAntiItemLag.dll"), "a mod the host installed");

            var (service, _) = Build();
            await service.InstallAsync(BaseExe, null, Nobody());

            Assert.Equal("a mod the host installed",
                File.ReadAllText(Path.Combine(plugins, "AzuAntiItemLag.dll")));
        }

        // ------------------------------------------------------------------ the shape refusal

        /// <summary>
        /// A link that answers with something that is not a loader is refused before a single
        /// file is written. Unpacking an arbitrary archive over the folder a server runs from
        /// is a far larger blast radius than dropping a folder under plugins, so the shape is
        /// the gate.
        /// </summary>
        [Fact]
        public async Task An_archive_that_is_not_a_loader_is_refused_and_writes_nothing()
        {
            var (service, _) = Build(archive: AModArchive());

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.InstallAsync(BaseExe, "https://thunderstore.io/package/download/x/y/1.0.0/", Nobody()));

            Assert.Equal("bepinex.notALoader", refused.MessageId);
            Assert.False(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
            Assert.False(Directory.Exists(Path.Combine(BaseDir, "BepInEx")));
            Assert.False(File.Exists(Path.Combine(BaseDir, BepInExMarkerFile.FileName)));
        }

        [Fact]
        public async Task A_link_that_is_not_an_address_is_refused()
        {
            var (service, handler) = Build();

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.InstallAsync(BaseExe, "not a url at all", Nobody()));

            Assert.Equal("bepinex.notALoader", refused.MessageId);
            Assert.Empty(handler.Requests);
        }

        // ------------------------------------------------------------------ the config rule

        /// <summary>
        /// Gale's rule, not r2modman's: a config the host edited survives the update. r2modman
        /// copies the whole BepInEx folder with an overwriting copy and silently replaces it
        /// with the pack's shipped default on every single install.
        /// </summary>
        [Fact]
        public async Task An_edited_config_is_left_exactly_as_it_was()
        {
            var config = Path.Combine(BaseDir, "BepInEx", "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "BepInEx.cfg"), "[Logging]\nthe host edited this\n");

            var (service, _) = Build();
            await service.InstallAsync(BaseExe, null, Nobody());

            Assert.Equal("[Logging]\nthe host edited this\n",
                File.ReadAllText(Path.Combine(config, "BepInEx.cfg")));
        }

        [Fact]
        public async Task A_missing_config_is_written_from_the_pack()
        {
            var (service, _) = Build();
            await service.InstallAsync(BaseExe, null, Nobody());

            Assert.Equal("[Logging]\nshipped default\n",
                File.ReadAllText(Path.Combine(BaseDir, "BepInEx", "config", "BepInEx.cfg")));
        }

        // ------------------------------------------------------------------ the core

        /// <summary>
        /// The core is REPLACED, not merged: a file the new pack does not ship would still be
        /// loaded if it were left there.
        /// </summary>
        [Fact]
        public async Task The_core_is_replaced_whole_and_a_stale_assembly_does_not_survive()
        {
            var core = Path.Combine(BaseDir, "BepInEx", "core");
            Directory.CreateDirectory(core);
            File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "an older core");
            File.WriteAllText(Path.Combine(core, "SomethingOld.dll"), "no longer shipped");

            var (service, _) = Build();
            var result = await service.InstallAsync(BaseExe, null, Nobody());

            Assert.True(result.Replaced);
            Assert.Equal("core 5.4.2350", File.ReadAllText(Path.Combine(core, "BepInEx.dll")));
            Assert.False(File.Exists(Path.Combine(core, "SomethingOld.dll")));
        }

        /// <summary>
        /// A core that only half copied is an install that starts neither version, so the one
        /// that was there goes straight back. Driven for real through the copy seam rather
        /// than reasoned about.
        /// </summary>
        [Fact]
        public async Task A_core_that_fails_half_way_through_is_put_back_the_way_it_was()
        {
            var core = Path.Combine(BaseDir, "BepInEx", "core");
            Directory.CreateDirectory(core);
            File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "the working core");
            File.WriteAllText(Path.Combine(core, "BepInEx.Preloader.dll"), "the working preloader");

            var (service, _) = BuildWith((thunderstore, http, isolation, logger) =>
                new HalfWayService(thunderstore, http, isolation, logger) { FailAfter = 1 });

            await Assert.ThrowsAnyAsync<Exception>(() => service.InstallAsync(BaseExe, null, Nobody()));

            Assert.Equal("the working core", File.ReadAllText(Path.Combine(core, "BepInEx.dll")));
            Assert.Equal("the working preloader", File.ReadAllText(Path.Combine(core, "BepInEx.Preloader.dll")));
            // And no note claiming an install that did not happen.
            Assert.False(File.Exists(Path.Combine(BaseDir, BepInExMarkerFile.FileName)));
        }

        /// <summary>
        /// The same failure on a FIRST install, where there is no previous core to put back.
        /// Half a core is worse than none: the loader finds BepInEx.dll, looks for a preloader
        /// that is not there and starts nothing, with nothing on disk saying why. The folder is
        /// left exactly as it was found instead.
        /// </summary>
        [Fact]
        public async Task A_first_install_that_fails_half_way_through_leaves_no_half_core()
        {
            Assert.False(Directory.Exists(Path.Combine(BaseDir, "BepInEx", "core")));

            var (service, _) = BuildWith((thunderstore, http, isolation, logger) =>
                new HalfWayService(thunderstore, http, isolation, logger) { FailAfter = 1 });

            await Assert.ThrowsAnyAsync<Exception>(() => service.InstallAsync(BaseExe, null, Nobody()));

            var core = Path.Combine(BaseDir, "BepInEx", "core");
            Assert.False(File.Exists(Path.Combine(core, "BepInEx.dll")));
            Assert.Empty(Directory.Exists(core) ? Directory.GetFileSystemEntries(core) : Array.Empty<string>());

            // and the status the row reads says what is true: nothing is installed
            var status = service.Status(BaseExe);
            Assert.False(status.Installed);
            Assert.False(status.MaintainedByBakaLoader);
        }

        /// <summary>A service whose core copy gives up after a given number of files.</summary>
        private sealed class HalfWayService : BepInExService
        {
            private int Copied;

            public HalfWayService(IThunderstoreClient thunderstore, ValheimBakaLoader.Tools.Http.IHttpClientProvider http,
                IInstallIsolationService isolation, IApplicationLogger logger)
                : base(thunderstore, http, isolation, logger) { }

            public int FailAfter { get; init; }

            protected override void CopyCoreFile(string source, string destination)
            {
                if (Copied++ >= FailAfter) throw new IOException("the disk went away half way through");
                base.CopyCoreFile(source, destination);
            }
        }

        // ------------------------------------------------------------------ the note

        /// <summary>
        /// Nothing a correct install leaves on disk records the pack version: the archive's
        /// manifest.json is a metadata-tier file every installer refuses to copy. So the note
        /// is the only place the version lives, and every install writes one.
        /// </summary>
        [Fact]
        public async Task Every_install_writes_a_note_naming_the_pack_the_version_and_the_files()
        {
            var (service, _) = Build();
            await service.InstallAsync(BaseExe, null, Nobody());

            var marker = BepInExMarkerFile.Read(BaseDir);

            Assert.NotNull(marker);
            Assert.Equal(BepInExService.DefaultPackage, marker.Package);
            Assert.Equal("5.4.2350", marker.Version);
            Assert.StartsWith("BakaLoader", marker.Writer);
            Assert.Equal("https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/5.4.2350/",
                marker.Source);
            Assert.NotEqual(default, marker.InstalledUtc);

            Assert.Contains(marker.Files, f => f.Path == "winhttp.dll");
            Assert.Contains(marker.Files, f => f.Path == "BepInEx/core/BepInEx.dll");
            Assert.Contains(marker.Files, f => f.Path == "BepInEx/config/BepInEx.cfg");
            Assert.All(marker.Files, f => Assert.Equal(64, f.Sha256.Length));

            // And the hash in the note is the hash of what is actually on disk.
            var winhttp = marker.Files.First(f => f.Path == "winhttp.dll");
            Assert.Equal(BepInExService.Sha256Of(Path.Combine(BaseDir, "winhttp.dll")), winhttp.Sha256);
        }

        [Fact]
        public async Task The_status_reads_the_note_back_as_the_pack_version()
        {
            var (service, _) = Build();
            await service.InstallAsync(BaseExe, null, Nobody());

            var status = service.Status(BaseExe);

            Assert.True(status.Installed);
            Assert.True(status.MaintainedByBakaLoader);
            Assert.Equal("5.4.2350", status.PackVersion);
            Assert.Null(status.CoreFileVersion);
            Assert.Equal(BepInExService.DefaultPackage, status.Package);
        }

        /// <summary>
        /// An install somebody else made has no note, so the only version there is is the
        /// framework assembly's own. It is NOT the pack version and never was: pack 5.4.2350
        /// ships BepInEx 5.4.23.5, which is why the row has to word the two differently.
        /// </summary>
        [Fact]
        public void Without_a_note_the_version_falls_back_to_the_core_assembly()
        {
            var core = Path.Combine(BaseDir, "BepInEx", "core");
            Directory.CreateDirectory(core);
            // A real, versioned assembly: the app's own, which is the only way to prove the
            // fallback reads a file version rather than returning null for everything.
            File.Copy(typeof(BepInExService).Assembly.Location, Path.Combine(core, "BepInEx.dll"));

            var service = new BepInExService(null, null, null, Mock.Of<IApplicationLogger>());
            var status = service.Status(BaseExe);

            Assert.True(status.Installed);
            Assert.False(status.MaintainedByBakaLoader);
            Assert.Null(status.PackVersion);
            Assert.False(string.IsNullOrWhiteSpace(status.CoreFileVersion));
        }

        [Fact]
        public void Nothing_installed_says_so()
        {
            var service = new BepInExService(null, null, null, Mock.Of<IApplicationLogger>());
            var status = service.Status(BaseExe);

            Assert.False(status.Installed);
            Assert.False(status.MaintainedByBakaLoader);
            Assert.Null(status.PackVersion);
            Assert.Null(status.CoreFileVersion);
        }

        /// <summary>The status names every profile on this install and which of them are up.</summary>
        [Fact]
        public void The_status_names_the_profiles_that_share_this_install()
        {
            var service = new BepInExService(null, null, null, Mock.Of<IApplicationLogger>());
            var isolatedExe = Path.Combine(Path.GetDirectoryName(BaseDir), ".bakaloader-instances",
                "Quiet", "valheim_server.exe");

            var status = service.Status(BaseExe, null, new[]
            {
                new BepInExProfileInstall { ProfileName = "Final Sunset", ServerExePath = BaseExe, Running = true },
                new BepInExProfileInstall { ProfileName = "Quiet", ServerExePath = isolatedExe, Running = false },
                new BepInExProfileInstall
                {
                    ProfileName = "Elsewhere",
                    ServerExePath = Path.Combine(Root, "other", "valheim_server.exe"),
                    Running = true,
                },
            });

            Assert.Equal(new[] { "Final Sunset", "Quiet" }, status.SharingProfiles);
            Assert.Equal(new[] { "Final Sunset" }, status.RunningProfiles);
        }

        // ------------------------------------------------------------------ the running refusal

        /// <summary>
        /// The hard refusal, asked before a byte is fetched. A running server has the loader
        /// files mapped, and on an install with isolated profiles those files are hard links
        /// and junctions every one of them writes through.
        /// </summary>
        [Fact]
        public async Task A_running_server_on_this_install_stops_the_write_before_anything_is_fetched()
        {
            var (service, handler) = Build();

            var refused = await Assert.ThrowsAsync<HostFacingException>(() => service.InstallAsync(
                BaseExe, null, new[]
                {
                    new BepInExProfileInstall { ProfileName = "Final Sunset", ServerExePath = BaseExe, Running = true },
                }));

            Assert.Equal("bepinex.serversRunning", refused.MessageId);
            Assert.Equal("Final Sunset", refused.Params["names"]);
            Assert.Empty(handler.Requests);
            Assert.False(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
        }

        /// <summary>An isolated profile that is up refuses the write into its BASE install.</summary>
        [Fact]
        public async Task An_isolated_profile_that_is_up_stops_the_write_into_its_base()
        {
            var isolatedExe = Path.Combine(Path.GetDirectoryName(BaseDir), ".bakaloader-instances",
                "Quiet", "valheim_server.exe");
            var (service, handler) = Build();

            var refused = await Assert.ThrowsAsync<HostFacingException>(() => service.InstallAsync(
                BaseExe, null, new[]
                {
                    new BepInExProfileInstall { ProfileName = "Quiet", ServerExePath = isolatedExe, Running = true },
                }));

            Assert.Equal("bepinex.serversRunning", refused.MessageId);
            Assert.Empty(handler.Requests);
        }

        // ------------------------------------------------------------------ what came down the wire

        /// <summary>
        /// The size the listing named is checked against the bytes that arrived. Thunderstore
        /// does not publish a hash today, so this is the only check there is, and a mismatch
        /// writes nothing rather than unpacking whatever it was.
        /// </summary>
        [Fact]
        public async Task A_download_that_does_not_weigh_what_the_listing_said_writes_nothing()
        {
            var (service, _) = Build(size: 999999);

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.InstallAsync(BaseExe, null, Nobody()));

            Assert.Equal("bepinex.integrity", refused.MessageId);
            Assert.False(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
        }

        [Fact]
        public async Task A_download_that_does_not_hash_to_what_the_listing_said_writes_nothing()
        {
            var (service, _) = Build(sha: new string('a', 64));

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.InstallAsync(BaseExe, null, Nobody()));

            Assert.Equal("bepinex.integrity", refused.MessageId);
        }

        [Fact]
        public async Task A_download_that_matches_the_listing_goes_in()
        {
            var bytes = Pack("5.4.2350");
            var provider = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

            var thunderstore = new Mock<IThunderstoreClient>();
            thunderstore
                .Setup(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ThunderstoreLiveLookup
                {
                    Answered = true,
                    Package = Listing("5.4.2350", bytes.LongLength, Sha256OfBytes(bytes)),
                });

            var service = new BepInExService(thunderstore.Object, provider,
                new InstallIsolationService(Mock.Of<IApplicationLogger>()), Mock.Of<IApplicationLogger>());

            var result = await service.InstallAsync(BaseExe, null, Nobody());

            Assert.True(result.Installed);
        }

        private static string Sha256OfBytes(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
        }

        /// <summary>
        /// The cap counts bytes as they arrive, so a response that declares no length at all
        /// is still stopped. The real pack is under a megabyte; the cap exists for a link that
        /// answers with something else entirely.
        /// </summary>
        [Fact]
        public async Task A_download_past_the_cap_is_stopped()
        {
            var bytes = Pack();
            var provider = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

            var thunderstore = new Mock<IThunderstoreClient>();
            thunderstore
                .Setup(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ThunderstoreLiveLookup { Answered = true, Package = Listing("5.4.2350") });

            var service = new BepInExService(thunderstore.Object, provider,
                new InstallIsolationService(Mock.Of<IApplicationLogger>()), Mock.Of<IApplicationLogger>())
            {
                MaxDownloadBytes = 10,
            };

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.InstallAsync(BaseExe, null, Nobody()));

            Assert.Equal("bepinex.tooLarge", refused.MessageId);
            Assert.False(File.Exists(Path.Combine(BaseDir, "winhttp.dll")));
        }

        /// <summary>A site that will not answer is a refusal with a name, not a stack trace.</summary>
        [Fact]
        public async Task A_site_that_does_not_answer_is_named_as_such()
        {
            var thunderstore = new Mock<IThunderstoreClient>();
            thunderstore
                .Setup(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ThunderstoreLiveLookup { Answered = false, Package = null });
            thunderstore
                .Setup(c => c.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((ThunderstorePackage)null);

            var service = new BepInExService(thunderstore.Object, new RecordingHttpClientProvider(),
                new InstallIsolationService(Mock.Of<IApplicationLogger>()), Mock.Of<IApplicationLogger>());

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.InstallAsync(BaseExe, null, Nobody()));

            Assert.Equal("bepinex.offline", refused.MessageId);
        }

        [Fact]
        public async Task An_install_without_a_server_path_is_refused()
        {
            var (service, _) = Build();

            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.InstallAsync(null, null, Nobody()));

            Assert.Equal("bepinex.noServerPath", refused.MessageId);
        }

        // ------------------------------------------------------------------ update

        /// <summary>An update fetches the package the note names, not whatever the default is.</summary>
        [Fact]
        public async Task An_update_moves_the_note_forward_and_says_what_it_replaced()
        {
            var (first, _) = Build("5.4.2333");
            await first.InstallAsync(BaseExe, null, Nobody());

            var (second, handler) = Build("5.4.2350");
            var result = await second.UpdateAsync(BaseExe, Nobody());

            Assert.True(result.Installed);
            Assert.True(result.Replaced);
            Assert.Equal("5.4.2333", result.PreviousVersion);
            Assert.Equal("5.4.2350", result.Version);
            Assert.Equal("5.4.2350", BepInExMarkerFile.Read(BaseDir).Version);
            Assert.Contains(handler.Requests, r => r.Contains("/BepInExPack_Valheim/5.4.2350/"));
        }

        // ------------------------------------------------------------------ the wrong location

        [Fact]
        public async Task The_misplaced_pack_folder_is_removed_and_nothing_else_is()
        {
            var plugins = Path.Combine(BaseDir, "BepInEx", "plugins");
            Directory.CreateDirectory(Path.Combine(plugins, BepInExService.DefaultPackage, "BepInEx", "core"));
            Directory.CreateDirectory(Path.Combine(plugins, "Azumatt-AzuAntiItemLag"));
            File.WriteAllText(Path.Combine(plugins, "Azumatt-AzuAntiItemLag", "mod.dll"), "a real mod");

            var service = new BepInExService(null, null, null, Mock.Of<IApplicationLogger>());
            Assert.True(await service.RemoveWrongLocationAsync(plugins, Nobody(), BaseExe));

            Assert.False(Directory.Exists(Path.Combine(plugins, BepInExService.DefaultPackage)));
            Assert.True(File.Exists(Path.Combine(plugins, "Azumatt-AzuAntiItemLag", "mod.dll")));
        }

        [Fact]
        public async Task Removing_a_folder_that_is_not_there_is_refused_rather_than_silently_fine()
        {
            var plugins = Path.Combine(BaseDir, "BepInEx", "plugins");
            Directory.CreateDirectory(plugins);

            var service = new BepInExService(null, null, null, Mock.Of<IApplicationLogger>());
            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.RemoveWrongLocationAsync(plugins, Nobody(), BaseExe));

            Assert.Equal("bepinex.noWrongFolder", refused.MessageId);
        }

        [Fact]
        public async Task A_running_server_stops_the_misplaced_folder_being_removed_too()
        {
            var plugins = Path.Combine(BaseDir, "BepInEx", "plugins");
            Directory.CreateDirectory(Path.Combine(plugins, BepInExService.DefaultPackage));

            var service = new BepInExService(null, null, null, Mock.Of<IApplicationLogger>());
            var refused = await Assert.ThrowsAsync<HostFacingException>(
                () => service.RemoveWrongLocationAsync(plugins, new[]
                {
                    new BepInExProfileInstall { ProfileName = "Final Sunset", ServerExePath = BaseExe, Running = true },
                }, BaseExe));

            Assert.Equal("bepinex.serversRunning", refused.MessageId);
            Assert.True(Directory.Exists(Path.Combine(plugins, BepInExService.DefaultPackage)));
        }

        // ------------------------------------------------------------------ isolation

        /// <summary>
        /// A profile provisioned before BepInEx existed has no BepInEx folder at all, because
        /// the isolation copier only walks the folders the base install already carries. After
        /// BepInEx lands in the base it gets the junctions and the loader files, or it would
        /// run vanilla forever with nothing saying so.
        /// <para>
        /// Real junctions, in a temporary folder. A volume or a policy that refuses to make
        /// one skips the test rather than failing it.
        /// </para>
        /// </summary>
        [Fact]
        public async Task An_isolated_profile_provisioned_before_bepinex_is_given_the_sharing_it_lacked()
        {
            var instances = Path.Combine(Path.GetDirectoryName(BaseDir), ".bakaloader-instances");
            var isolated = Path.Combine(instances, "Quiet");
            Directory.CreateDirectory(isolated);
            File.WriteAllText(Path.Combine(isolated, "valheim_server.exe"), "the isolated copy");

            if (!JunctionsWork()) return;   // the volume or the policy refuses; nothing to prove here

            var (service, _) = Build();
            var result = await service.InstallAsync(BaseExe, null, Nobody());

            Assert.Equal(1, result.IsolatedInstallsLinked);

            var core = new DirectoryInfo(Path.Combine(isolated, "BepInEx", "core"));
            Assert.True(core.Exists);
            Assert.True(core.Attributes.HasFlag(FileAttributes.ReparsePoint));
            // Through the junction, the isolated profile reads the base's core.
            Assert.Equal("core 5.4.2350", File.ReadAllText(Path.Combine(core.FullName, "BepInEx.dll")));

            // The loader file beside the executable, which is the whole mechanism on Windows.
            Assert.True(File.Exists(Path.Combine(isolated, "winhttp.dll")));
            // And its own plugins and config, which are per server and never shared.
            Assert.True(Directory.Exists(Path.Combine(isolated, "BepInEx", "plugins")));
            Assert.False(new DirectoryInfo(Path.Combine(isolated, "BepInEx", "plugins"))
                .Attributes.HasFlag(FileAttributes.ReparsePoint));
        }

        /// <summary>
        /// An isolated install that already has a real core folder of its own is left exactly
        /// as it is. Replacing a real folder with a junction would delete whatever was in it,
        /// and an install that diverged did so for a reason.
        /// </summary>
        [Fact]
        public void An_isolated_install_that_already_has_a_core_is_never_replaced()
        {
            var instances = Path.Combine(Path.GetDirectoryName(BaseDir), ".bakaloader-instances");
            var isolated = Path.Combine(instances, "Diverged");
            var ownCore = Path.Combine(isolated, "BepInEx", "core");
            Directory.CreateDirectory(ownCore);
            File.WriteAllText(Path.Combine(isolated, "valheim_server.exe"), "the isolated copy");
            File.WriteAllText(Path.Combine(ownCore, "BepInEx.dll"), "a core of its own");

            Directory.CreateDirectory(Path.Combine(BaseDir, "BepInEx", "core"));
            File.WriteAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll"), "the base core");

            var isolation = new InstallIsolationService(Mock.Of<IApplicationLogger>());
            isolation.EnsureSharedBepInEx(BaseExe, isolated);

            Assert.Equal("a core of its own", File.ReadAllText(Path.Combine(ownCore, "BepInEx.dll")));
            Assert.False(new DirectoryInfo(ownCore).Attributes.HasFlag(FileAttributes.ReparsePoint));
        }

        /// <summary>Only installs derived from this base are listed, never a stranger's folder.</summary>
        [Fact]
        public void Only_installs_carrying_this_base_exe_are_listed()
        {
            var instances = Path.Combine(Path.GetDirectoryName(BaseDir), ".bakaloader-instances");
            var mine = Path.Combine(instances, "Quiet");
            var stranger = Path.Combine(instances, "NotAnInstall");
            Directory.CreateDirectory(mine);
            Directory.CreateDirectory(stranger);
            File.WriteAllText(Path.Combine(mine, "valheim_server.exe"), "x");
            File.WriteAllText(Path.Combine(stranger, "readme.txt"), "x");

            var isolation = new InstallIsolationService(Mock.Of<IApplicationLogger>());
            var found = isolation.ManagedInstallDirectories(BaseExe).ToList();

            Assert.Contains(mine, found);
            Assert.DoesNotContain(stranger, found);
        }

        /// <summary>
        /// An install provisioned from this base carries a note saying so, and the note is the
        /// ONLY thing that can tell one base's instances from another's inside the shared
        /// fallback root: every Valheim dedicated server on the machine is called
        /// valheim_server.exe, so the file name proves nothing there.
        /// </summary>
        [Fact]
        public void A_provisioned_install_records_the_base_it_came_from()
        {
            if (!JunctionsWork()) return;   // this machine or policy will not make junctions

            Directory.CreateDirectory(Path.Combine(BaseDir, "valheim_server_Data"));
            File.WriteAllText(Path.Combine(BaseDir, "valheim_server_Data", "resources.assets"), "game data");

            var isolation = new InstallIsolationService(Mock.Of<IApplicationLogger>());
            var made = isolation.ProvisionInstall(BaseExe, "Quiet", seedPluginsFromBase: false);

            Assert.Equal(Path.GetFullPath(BaseExe),
                InstallIsolationService.RecordedBaseExe(made.InstallDirectory));
        }

        /// <summary>
        /// BakaLoader's note about the BASE install's BepInEx never travels into an instance.
        /// It is a hard link when it does, so the instance answers "yes, this install is
        /// looked after, at pack X" on behalf of a folder it is not, and a scan or a status
        /// read from inside the instance reads the base's answer without knowing it.
        /// </summary>
        [Fact]
        public void The_bepinex_note_is_never_shared_into_an_isolated_install()
        {
            var instances = Path.Combine(Path.GetDirectoryName(BaseDir), ".bakaloader-instances");
            var isolated = Path.Combine(instances, "Quiet");
            Directory.CreateDirectory(isolated);
            File.WriteAllText(Path.Combine(isolated, "valheim_server.exe"), "the isolated copy");

            Directory.CreateDirectory(Path.Combine(BaseDir, "BepInEx", "core"));
            File.WriteAllText(Path.Combine(BaseDir, "BepInEx", "core", "BepInEx.dll"), "the base core");
            File.WriteAllText(Path.Combine(BaseDir, "winhttp.dll"), "the doorstop proxy");
            BepInExMarkerFile.Write(BaseDir, new BepInExMarker
            {
                Schema = BepInExMarkerFile.CurrentSchema,
                Writer = "BakaLoader 1.2.0",
                Package = BepInExService.DefaultPackage,
                Version = "5.4.2350",
            });

            var isolation = new InstallIsolationService(Mock.Of<IApplicationLogger>());
            isolation.EnsureSharedBepInEx(BaseExe, isolated);

            // the loader file did travel, because that IS the mechanism on Windows
            Assert.True(File.Exists(Path.Combine(isolated, "winhttp.dll")));
            // the note did not
            Assert.False(File.Exists(Path.Combine(isolated, BepInExMarkerFile.FileName)));
            Assert.Null(BepInExMarkerFile.Read(isolated));
        }

        /// <summary>
        /// The other half of the sharing, and the one that is easiest to break: the loose
        /// loader files beside the executable are HARD LINKS, not junctions, so the write has
        /// to go THROUGH the file that is already there. Delete it and copy a new one and the
        /// link is gone: the base gets the new loader and every isolated profile is left
        /// pointing at a file nothing updates again.
        /// <para>
        /// Real hard links in a temporary folder. A volume that refuses one skips the test
        /// rather than failing it.
        /// </para>
        /// </summary>
        [Fact]
        public async Task A_hard_linked_loader_file_in_an_isolated_install_follows_the_base()
        {
            var instances = Path.Combine(Path.GetDirectoryName(BaseDir), ".bakaloader-instances");
            var isolated = Path.Combine(instances, "Quiet");
            Directory.CreateDirectory(isolated);
            File.WriteAllText(Path.Combine(isolated, "valheim_server.exe"), "the isolated copy");

            // The loader the base carries today, and the isolated install's hard link to it.
            var baseWinhttp = Path.Combine(BaseDir, "winhttp.dll");
            var isolatedWinhttp = Path.Combine(isolated, "winhttp.dll");
            File.WriteAllText(baseWinhttp, "the doorstop proxy 5.4.2333");
            if (!CreateHardLink(isolatedWinhttp, baseWinhttp, IntPtr.Zero)) return;   // the volume refuses

            Assert.Equal("the doorstop proxy 5.4.2333", File.ReadAllText(isolatedWinhttp));

            var (service, _) = Build("5.4.2350");
            await service.InstallAsync(BaseExe, null, Nobody());

            // Written in place, so the isolated install reads the new loader through its link.
            Assert.Equal("the doorstop proxy 5.4.2350", File.ReadAllText(baseWinhttp));
            Assert.Equal("the doorstop proxy 5.4.2350", File.ReadAllText(isolatedWinhttp));
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        /// <summary>Whether this volume and this policy will make a junction at all.</summary>
        private bool JunctionsWork()
        {
            var target = Path.Combine(Root, "junction-probe-target");
            var link = Path.Combine(Root, "junction-probe-link");
            try
            {
                Directory.CreateDirectory(target);
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
                var ok = proc.HasExited && proc.ExitCode == 0 && Directory.Exists(link)
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
