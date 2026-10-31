using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Newtonsoft.Json;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// A package zip on this PC, read by the REAL reader and installed by the REAL installer into a
    /// temporary server tree (1.3.2, issue 32). Nothing reaches the network: the installer is built
    /// over a strict Thunderstore mock and an HTTP provider that fails the test if it is ever asked.
    /// <para>
    /// The adversarial scenarios of the spec that live below the bridge: 1 (spaces, accents, CJK, and
    /// non-ASCII entry names with the UTF-8 flag set and unset), 3 (a share that never answers costs
    /// a bounded wait, off the calling thread), 4 (manifest in a folder, a folder beside a root
    /// manifest, Mac leftovers), 5 (zip slip in five shapes, nothing written; a zip carrying its own
    /// source note), 6 (owner collisions, case only), 7 (an installed copy switched off stays off),
    /// 8 (locked, .crdownload, zero bytes, gone), 10 (the size cap before any copy, a declared bomb,
    /// an entry that lies about its length), 11 (update safety on every reader of the one flag),
    /// 12 (the Gale diff), 15 (patcher only, config kept).
    /// </para>
    /// </summary>
    public class LocalPackageReaderTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "vbl-localzip-" + Guid.NewGuid().ToString("N"));
        private readonly string Bep;
        private readonly string Plugins;
        private readonly string Downloads;
        private readonly string Temp;

        public LocalPackageReaderTests()
        {
            Bep = Path.Combine(Root, "server", "BepInEx");
            Plugins = Path.Combine(Bep, "plugins");
            Downloads = Path.Combine(Root, "downloads");
            Temp = Path.Combine(Root, "temp");
            Directory.CreateDirectory(Plugins);
            Directory.CreateDirectory(Downloads);
            Directory.CreateDirectory(Temp);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        // ------------------------------------------------------------------ plumbing

        internal static string Manifest(string name, string version, params string[] deps) =>
            JsonConvert.SerializeObject(new { name, version_number = version, website_url = "", description = "", dependencies = deps });

        internal static byte[] Zip(IEnumerable<(string Name, string Body)> entries, bool utf8Flag = true)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, body) in entries)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false));
                    writer.Write(body);
                }
            }
            var bytes = buffer.ToArray();
            if (!utf8Flag) ClearUtf8Flag(bytes);
            return bytes;
        }

        /// <summary>Clears general purpose bit 11 in every local and central header: names stay UTF-8 bytes, unflagged.</summary>
        private static void ClearUtf8Flag(byte[] zip)
        {
            for (var i = 0; i + 10 < zip.Length; i++)
            {
                if (zip[i] != 0x50 || zip[i + 1] != 0x4B) continue;
                if (zip[i + 2] == 0x03 && zip[i + 3] == 0x04) zip[i + 7] &= 0xF7;        // local header, flags at +6
                else if (zip[i + 2] == 0x01 && zip[i + 3] == 0x02) zip[i + 9] &= 0xF7;   // central header, flags at +8
            }
        }

        /// <summary>Overwrites the uncompressed size the central directory declares for the named entry.</summary>
        private static void DeclareSize(byte[] zip, string entryName, uint size)
        {
            var name = Encoding.UTF8.GetBytes(entryName);
            for (var i = 0; i + 46 < zip.Length; i++)
            {
                if (zip[i] != 0x50 || zip[i + 1] != 0x4B || zip[i + 2] != 0x01 || zip[i + 3] != 0x02) continue;
                var nameLength = zip[i + 28] | (zip[i + 29] << 8);
                if (nameLength != name.Length || !zip.AsSpan(i + 46, nameLength).SequenceEqual(name)) continue;
                BitConverter.GetBytes(size).CopyTo(zip, i + 24);
                return;
            }
            throw new InvalidOperationException("no central header for " + entryName);
        }

        private static (string, string)[] Package(string name = "Aurora", string version = "1.4.0", params (string, string)[] more) =>
            new[] { ("manifest.json", Manifest(name, version)), ("plugins/Aurora.dll", "the plugin " + version) }.Concat(more).ToArray();

        private string Write(string fileName, byte[] bytes, string folder = null)
        {
            var dir = folder == null ? Downloads : Path.Combine(Downloads, folder);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private string TempCopy() => Path.Combine(Temp, "copy-" + Guid.NewGuid().ToString("N") + ".zip");

        private List<InstalledMod> Installed() => new ModScanner(Mock.Of<IApplicationLogger>()).ScanPlugins(Plugins);

        private Task<LocalPackageReading> Read(string path, string copy = null, long cap = LocalPackageReader.DefaultMaxZipBytes) =>
            LocalPackageReader.InspectAsync(path, copy ?? TempCopy(), Installed(), cap);

        private static ModUpdateService Service()
        {
            var http = new Mock<IHttpClientProvider>(MockBehavior.Strict);
            return new ModUpdateService(new Mock<IThunderstoreClient>(MockBehavior.Strict).Object, http.Object, Mock.Of<IApplicationLogger>());
        }

        /// <summary>Reads and installs, the way the bridge's accepted call does.</summary>
        private async Task<ModInstallResult> Install(string path)
        {
            var copy = TempCopy();
            var reading = await Read(path, copy);
            Assert.True(reading.Refusal == null, reading.Refusal?.Reason + " " + reading.Refusal?.Sentence);
            var result = await Service().InstallFromLocalZipAsync(reading.Facts, copy, Plugins);
            Assert.False(File.Exists(copy), "the private copy outlived the install");
            return result;
        }

        private void InstallHand(string fullName, string version, string source = null)
        {
            var dir = Path.Combine(Plugins, fullName);
            Directory.CreateDirectory(dir);
            var name = fullName.Substring(fullName.IndexOf('-') + 1);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest(name, version));
            File.WriteAllText(Path.Combine(dir, name + ".dll"), "old " + version);
            if (source != null)
                ModSourceMarkerFile.Write(dir, new ModSourceMarker
                {
                    Schema = ModSourceMarkerFile.CurrentSchema, Writer = "BakaLoader test", Source = source,
                    Owner = fullName.Split('-')[0], Name = name, Version = version, InstalledUtc = DateTime.UtcNow,
                });
        }

        private static string Reason(LocalPackageReading r) => r.Refusal?.Reason;

        private string At(string relative) => Path.Combine(Bep, relative.Replace('/', Path.DirectorySeparatorChar));

        // ------------------------------------------------------------------ 1. non-ASCII and spaces

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task A_path_with_spaces_accents_and_CJK_installs_with_non_ASCII_entry_names(bool utf8Flag)
        {
            var zip = Zip(new[]
            {
                ("manifest.json", Manifest("Aurora", "1.4.0-beta.1")),
                ("plugins/Äurora 雪/Äurora 雪.dll", "the plugin"),
                ("README.md", "readme"),
            }, utf8Flag);
            var path = Write("Acme-Aurora-1.4.0-beta.1 テスト.zip", zip, folder: Path.Combine("Zoë Müller", "私のダウンロード"));

            var reading = await Read(path);
            Assert.Null(Reason(reading));
            Assert.Equal("Acme", reading.Facts.Owner);
            Assert.Equal("fileName", reading.Facts.OwnerFrom);
            Assert.Equal("Acme-Aurora-1.4.0-beta.1 テスト.zip", reading.Facts.FileName);

            var result = await Install(path);
            Assert.True(result.Installed, result.Error);
            Assert.True(File.Exists(At("plugins/Acme-Aurora/Äurora 雪/Äurora 雪.dll")), "the non-ASCII entry did not land under its own name");
            var row = Installed().Single(m => m.FullName == "Acme-Aurora");
            Assert.Equal("1.4.0-beta.1", row.InstalledVersion);
            Assert.Equal("local", row.InstalledSource);
        }

        // ------------------------------------------------------------------ 3. a share that never answers

        [Fact]
        public async Task A_file_that_never_answers_costs_a_bounded_wait_off_the_calling_thread()
        {
            var path = Write("Acme-Aurora-1.4.0.zip", Zip(Package()));
            var release = new ManualResetEventSlim(false);
            var openedOnPool = false;
            var copy = TempCopy();
            Stream Hung(string p)
            {
                openedOnPool = Thread.CurrentThread.IsThreadPoolThread;
                release.Wait(TimeSpan.FromSeconds(20));
                throw new IOException("the share went away");
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var reading = await LocalPackageReader.InspectAsync(path, copy, Installed(), LocalPackageReader.DefaultMaxZipBytes,
                TimeSpan.FromMilliseconds(300), default, Hung);
            clock.Stop();
            release.Set();

            Assert.Equal("local.unreadable", Reason(reading));
            Assert.Contains("no answer within", (string)reading.Refusal.Params["detail"]);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "the wait was not bounded: " + clock.Elapsed);
            Assert.True(openedOnPool, "the file was opened on the calling thread");
        }

        // ------------------------------------------------------------------ 4. where manifest.json sits

        [Fact]
        public async Task A_manifest_inside_a_folder_is_refused_and_named()
        {
            var path = Write("Acme-Aurora-1.4.0.zip", Zip(new[]
            {
                ("Aurora/manifest.json", Manifest("Aurora", "1.4.0")),
                ("Aurora/Aurora.dll", "the plugin"),
            }));

            var reading = await Read(path);
            Assert.Equal("local.manifestInFolder", Reason(reading));
            Assert.Equal("Aurora", reading.Refusal.Params["folder"]);
        }

        [Fact]
        public async Task A_folder_beside_a_root_manifest_installs_and_the_Mac_leftovers_are_left_out()
        {
            var path = Write("Acme-Aurora-1.4.0.zip", Zip(new[]
            {
                ("manifest.json", Manifest("Aurora", "1.4.0")),
                ("Aurora/Aurora.dll", "the plugin"),
                ("Aurora/manifest.json", Manifest("Other", "9.9.9")),
                ("__MACOSX/._Aurora.dll", "resource fork"),
                ("__MACOSX/Aurora/._Aurora.dll", "resource fork"),
                (".DS_Store", "finder"),
                ("Aurora/.DS_Store", "finder"),
            }));

            var result = await Install(path);
            Assert.True(result.Installed, result.Error);
            Assert.True(File.Exists(At("plugins/Acme-Aurora/Aurora.dll")));
            var all = Directory.GetFiles(At("plugins/Acme-Aurora"), "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
            Assert.DoesNotContain("._Aurora.dll", all);
            Assert.DoesNotContain(".DS_Store", all);
        }

        [Fact]
        public async Task Mac_leftovers_are_not_counted_as_the_dll()
        {
            var path = Write("Acme-Aurora-1.4.0.zip", Zip(new[]
            {
                ("manifest.json", Manifest("Aurora", "1.4.0")),
                ("__MACOSX/._Aurora.dll", "resource fork"),
            }));
            Assert.Equal("local.noDll", Reason(await Read(path)));
        }

        // ------------------------------------------------------------------ 5. zip slip and a stray note

        [Theory]
        [InlineData("../evil.dll")]
        [InlineData("/abs/evil.dll")]
        [InlineData("C:/evil.dll")]
        [InlineData(@"C:\evil.dll")]
        [InlineData("plugins/../../evil.dll")]
        [InlineData(@"..\..\evil.dll")]
        [InlineData("plugins/Aurora.dll:ads")]
        public async Task An_entry_that_leaves_the_folder_is_refused_before_anything_is_written(string entry)
        {
            var sibling = Path.Combine(Root, "sibling");
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "keep.txt"), "untouched");
            var path = Write("Acme-Aurora-1.4.0.zip", Zip(Package(more: (entry, "evil"))));
            var before = Directory.GetFiles(Root, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();
            var copy = TempCopy();

            var reading = await Read(path, copy);

            Assert.Equal("local.unsafeEntry", Reason(reading));
            Assert.Equal(entry, reading.Refusal.Params["entry"]);
            // Nothing beside the one private copy, and the copy is the caller's to delete.
            var after = Directory.GetFiles(Root, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();
            Assert.Equal(before.Append(copy).OrderBy(f => f), after);
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(sibling, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(Root, "evil.dll")));
        }

        [Fact]
        public void The_bounded_extractor_refuses_a_slip_entry_on_its_own_too()
        {
            var zip = Write("slip.zip", Zip(Package(more: ("../evil.dll", "evil"))));
            var into = Path.Combine(Temp, "x");
            Assert.Throws<InvalidDataException>(() => LocalPackageReader.ExtractTo(zip, into));
            Assert.False(File.Exists(Path.Combine(Temp, "evil.dll")));
        }

        [Theory]
        [InlineData("hexium")]
        [InlineData("local")]
        [InlineData("thunderstore")]
        public async Task A_zip_carrying_its_own_source_note_has_it_stripped_and_the_note_written_is_local(string claimed)
        {
            var stray = JsonConvert.SerializeObject(new ModSourceMarker
            {
                Schema = ModSourceMarkerFile.CurrentSchema, Writer = "BakaLoader 9.9.9", Source = claimed,
                Owner = "Acme", Name = "Aurora", Version = "1.4.0", DownloadUrl = "https://example.invalid/", InstalledUtc = DateTime.UtcNow,
            });
            var path = Write("Acme-Aurora-1.4.0.zip", Zip(Package(more: (ModSourceMarkerFile.FileName, stray))), folder: "secret share name");

            var result = await Install(path);
            Assert.True(result.Installed, result.Error);
            var note = ModSourceMarkerFile.Read(At("plugins/Acme-Aurora"));
            Assert.Equal("local", note.Source);
            Assert.Equal("local", ModSourceMarkerFile.ReadTrustedSource(At("plugins/Acme-Aurora"), "1.4.0"));
            // The file name only: the folder it came from never reaches the mods folder.
            Assert.Equal("Acme-Aurora-1.4.0.zip", note.DownloadUrl);
            Assert.DoesNotContain("secret share name", File.ReadAllText(Path.Combine(At("plugins/Acme-Aurora"), ModSourceMarkerFile.FileName)));
        }

        // ------------------------------------------------------------------ 6. who publishes it

        [Fact]
        public async Task An_installed_mod_of_the_same_name_under_another_owner_is_refused_rather_than_guessed()
        {
            InstallHand("Acme-Aurora", "1.3.0");
            var path = Write("Other-Aurora-1.4.0.zip", Zip(Package()));

            var reading = await Read(path);
            Assert.Equal("local.ownerConflict", Reason(reading));
            Assert.Equal("Acme", reading.Refusal.Params["installed"]);
            Assert.Equal("Other", reading.Refusal.Params["owner"]);
        }

        [Fact]
        public async Task Two_installed_mods_of_the_same_name_are_refused_unless_the_file_name_says_which()
        {
            InstallHand("Acme-Aurora", "1.3.0");
            InstallHand("Beta-Aurora", "1.3.0");

            var plain = await Read(Write("Aurora.zip", Zip(Package())));
            Assert.Equal("local.ownerAmbiguous", Reason(plain));

            var named = await Read(Write("Gamma-Aurora-1.4.0.zip", Zip(Package())));
            Assert.Equal("local.ownerAmbiguous", Reason(named));

            var chosen = await Read(Write("Beta-Aurora-1.4.0.zip", Zip(Package())));
            Assert.Null(Reason(chosen));
            Assert.Equal("Beta", chosen.Facts.Owner);
        }

        [Fact]
        public async Task A_case_only_difference_is_the_same_mod_in_the_installed_folders_spelling()
        {
            InstallHand("Acme-Aurora", "1.3.0");
            var path = Write("aurora-test-build.zip", Zip(new[] { ("manifest.json", Manifest("aurora", "1.4.0")), ("aurora.dll", "x") }));

            var reading = await Read(path);
            Assert.Null(Reason(reading));
            Assert.Equal("Acme", reading.Facts.Owner);
            Assert.Equal("Aurora", reading.Facts.Name);
            Assert.Equal("installed", reading.Facts.OwnerFrom);
        }

        [Fact]
        public async Task With_no_installed_copy_and_no_owner_in_the_file_name_the_zip_is_refused()
        {
            var reading = await Read(Write("Aurora test.zip", Zip(Package())));
            Assert.Equal("local.noOwner", Reason(reading));
            Assert.Equal("Aurora", reading.Refusal.Params["name"]);
        }

        [Theory]
        [InlineData("BepInExPack_Valheim", "Acme-BepInExPack_Valheim-5.4.2333.zip")]
        [InlineData("BakaLoaderKillAll", "Baka-BakaLoaderKillAll-1.0.0.zip")]
        public async Task The_loader_and_the_parts_BakaLoader_looks_after_are_refused(string name, string file)
        {
            var reading = await Read(Write(file, Zip(new[] { ("manifest.json", Manifest(name, "1.0.0")), ("x.dll", "x") })));
            Assert.Equal("local.reserved", Reason(reading));
        }

        // ------------------------------------------------------------------ the manifest

        [Theory]
        [InlineData("Aurora-Extra", "1.4.0")]
        [InlineData("Aurora", "")]
        [InlineData("", "1.4.0")]
        [InlineData("Aurora", "not a version")]
        public async Task A_manifest_without_a_usable_name_and_version_is_refused(string name, string version)
        {
            var reading = await Read(Write("Acme-Aurora-1.4.0.zip", Zip(new[] { ("manifest.json", Manifest(name, version)), ("a.dll", "x") })));
            Assert.Equal("local.manifestFields", Reason(reading));
        }

        [Fact]
        public async Task The_other_manifest_and_archive_refusals_each_say_their_own_reason()
        {
            Assert.Equal("local.noManifest", Reason(await Read(Write("Acme-Aurora-1.4.0.zip", Zip(new[] { ("a.dll", "x") })))));
            Assert.Equal("local.badManifest", Reason(await Read(Write("Acme-Aurora-1.4.1.zip", Zip(new[] { ("manifest.json", "{ not json"), ("a.dll", "x") })))));
            Assert.Equal("local.noDll", Reason(await Read(Write("Acme-Aurora-1.4.2.zip", Zip(new[] { ("manifest.json", Manifest("Aurora", "1.4.0")), ("readme.txt", "x") })))));
            Assert.Equal("local.notArchive", Reason(await Read(Write("Acme-Aurora-1.4.3.zip", Encoding.ASCII.GetBytes("this is not a zip at all")))));
            Assert.Equal("local.isFolder", Reason(await Read(Downloads)));
        }

        // ------------------------------------------------------------------ 8. locked, partial, gone

        [Fact]
        public async Task A_zip_another_program_holds_answers_locked()
        {
            var path = Write("Acme-Aurora-1.4.0.zip", Zip(Package()));
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal("local.locked", Reason(await Read(path)));
            }
            Assert.Null(Reason(await Read(path)));
        }

        [Fact]
        public async Task A_download_still_in_progress_a_zero_byte_file_and_a_deleted_one_each_say_so()
        {
            Assert.Equal("local.notZip", Reason(await Read(Write("Acme-Aurora-1.4.0.zip.crdownload", Zip(Package())))));
            Assert.Equal("local.empty", Reason(await Read(Write("Acme-Aurora-1.4.0.zip", Array.Empty<byte>()))));
            var gone = Write("Acme-Aurora-1.4.1.zip", Zip(Package()));
            File.Delete(gone);
            var reading = await Read(gone);
            Assert.Equal("local.missing", Reason(reading));
            Assert.Equal(gone, reading.Refusal.Params["path"]);
        }

        [Fact]
        public async Task A_read_only_file_installs()
        {
            var path = Write("Acme-Aurora-1.4.0.zip", Zip(Package()));
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try { Assert.True((await Install(path)).Installed); }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
        }

        // ------------------------------------------------------------------ 10. sizes

        [Fact]
        public async Task A_zip_over_the_cap_is_refused_before_any_copy_is_made()
        {
            var path = Write("Acme-Aurora-1.4.0.zip", Zip(Package(more: ("plugins/big.bin", new string('x', 4096)))));
            var copy = TempCopy();

            var reading = await Read(path, copy, cap: 1024);

            Assert.Equal("local.tooLarge", Reason(reading));
            Assert.False(File.Exists(copy), "a copy was made of a zip over the cap");
        }

        [Fact]
        public async Task A_small_zip_declaring_more_than_the_unpacked_cap_is_refused_by_the_entry_scan()
        {
            var zip = Zip(Package(more: ("plugins/bomb.bin", "tiny")));
            DeclareSize(zip, "plugins/bomb.bin", 0xF0000000u);
            var reading = await Read(Write("Acme-Aurora-1.4.0.zip", zip));
            Assert.Equal("local.unpackedTooLarge", Reason(reading));
        }

        [Fact]
        public async Task More_entries_than_the_cap_are_refused()
        {
            var many = Enumerable.Range(0, LocalPackageReader.MaxEntries + 1).Select(i => ("plugins/f" + i + ".txt", ""));
            var reading = await Read(Write("Acme-Aurora-1.4.0.zip", Zip(Package(more: many.ToArray()))));
            Assert.Equal("local.tooManyEntries", Reason(reading));
        }

        [Fact]
        public async Task An_entry_that_lies_about_its_length_stops_the_unpack_cleanly_and_places_nothing()
        {
            InstallHand("Acme-Aurora", "1.3.0");
            var before = File.ReadAllText(At("plugins/Acme-Aurora/Aurora.dll"));
            var zip = Zip(Package(more: ("plugins/data.bin", new string('A', 5000))));
            DeclareSize(zip, "plugins/data.bin", 10);
            var path = Write("Acme-Aurora-1.4.0.zip", zip);

            var copy = TempCopy();
            var reading = await Read(path, copy);
            Assert.Null(Reason(reading));
            var tempBefore = Directory.GetDirectories(Path.GetTempPath(), "bakaloader-*").ToHashSet();

            var result = await Service().InstallFromLocalZipAsync(reading.Facts, copy, Plugins);

            Assert.False(result.Installed);
            Assert.Equal(before, File.ReadAllText(At("plugins/Acme-Aurora/Aurora.dll")));
            Assert.False(File.Exists(At("plugins/Acme-Aurora/data.bin")));
            Assert.False(File.Exists(copy));
            // Other tests unpack into folders of the same shape at the same time, so only a new
            // one holding this zip's own payload counts as left behind by this install.
            Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "bakaloader-*")
                .Where(d => !tempBefore.Contains(d))
                .Where(d => { try { return Directory.GetFiles(d, "data.bin", SearchOption.AllDirectories).Length > 0; } catch { return false; } }));
        }

        // ------------------------------------------------------------------ 7. replacing, and switched off

        [Fact]
        public async Task An_installed_copy_that_was_switched_off_stays_off_and_the_hexium_note_is_replaced()
        {
            InstallHand("Acme-Aurora", "1.3.0", source: "hexium");
            var dir = At("plugins/Acme-Aurora");
            File.Move(Path.Combine(dir, "Aurora.dll"), Path.Combine(dir, "Aurora.dll.old"));
            Assert.True(ModSwitch.IsDisabled(dir, At("patchers/Acme-Aurora")));

            var result = await Install(Write("Acme-Aurora-1.4.0.zip", Zip(Package())));

            Assert.True(result.Installed, result.Error);
            Assert.True(result.Replaced);
            Assert.True(ModSwitch.IsDisabled(dir, At("patchers/Acme-Aurora")), "the host's off switch did not survive the install");
            Assert.Equal("local", ModSourceMarkerFile.ReadTrustedSource(dir, "1.4.0"));
        }

        [Fact]
        public async Task A_placement_that_fails_half_way_puts_the_old_copy_back()
        {
            InstallHand("Acme-Aurora", "1.3.0", source: "thunderstore");
            var copy = TempCopy();
            var reading = await Read(Write("Acme-Aurora-1.4.0.zip", Zip(Package(more: ("plugins/second.dll", "two")))), copy);
            var service = Service();
            var placed = 0;
            service.PlacingFile = _ => { if (++placed == 2) throw new IOException("disk said no"); };

            var result = await service.InstallFromLocalZipAsync(reading.Facts, copy, Plugins);

            Assert.False(result.Installed);
            Assert.Equal("old 1.3.0", File.ReadAllText(At("plugins/Acme-Aurora/Aurora.dll")));
            Assert.Equal("thunderstore", ModSourceMarkerFile.ReadTrustedSource(At("plugins/Acme-Aurora"), "1.3.0"));
        }

        // ------------------------------------------------------------------ 15. package shapes

        [Fact]
        public async Task A_patcher_only_zip_installs_as_one_row_carrying_its_patcher_part()
        {
            var result = await Install(Write("Acme-AuroraPatch-1.0.0.zip", Zip(new[]
            {
                ("manifest.json", Manifest("AuroraPatch", "1.0.0")),
                ("patchers/AuroraPatch.dll", "the patcher"),
            })));

            Assert.True(result.Installed, result.Error);
            Assert.True(File.Exists(At("patchers/Acme-AuroraPatch/AuroraPatch.dll")));
            Assert.False(File.Exists(At("plugins/Acme-AuroraPatch/AuroraPatch.dll")), "the patcher was placed as a plugin");
            // The same shape a link install of a patcher package gives: the manifest and the
            // source note sit in the plugins folder, so the row is the mod's one row with its
            // patcher part attached, and a removal takes both.
            var row = Installed().Single(m => m.FullName == "Acme-AuroraPatch");
            Assert.Equal(At("patchers/Acme-AuroraPatch"), row.PatcherDirectory);
            Assert.True(row.IsLocalInstalled);
        }

        [Fact]
        public async Task A_zip_with_config_files_keeps_the_hosts_existing_config()
        {
            Directory.CreateDirectory(At("config"));
            File.WriteAllText(At("config/acme.aurora.cfg"), "mine");

            var result = await Install(Write("Acme-Aurora-1.4.0.zip", Zip(Package(more: new[]
            {
                ("config/acme.aurora.cfg", "shipped"),
                ("config/acme.aurora.extra.cfg", "new one"),
            }))));

            Assert.True(result.Installed, result.Error);
            Assert.Equal("mine", File.ReadAllText(At("config/acme.aurora.cfg")));
            Assert.Equal("new one", File.ReadAllText(At("config/acme.aurora.extra.cfg")));
        }

        // ------------------------------------------------------------------ 11. update safety

        [Theory]
        [InlineData("1.4.0-beta.1", "1.4.0")]
        [InlineData("1.2.0", "1.3.0")]
        public async Task A_local_copy_is_never_offered_the_published_build_by_any_reader_of_the_one_flag(string local, string published)
        {
            Assert.True((await Install(Write("Acme-Aurora-" + local + ".zip", Zip(Package(version: local))))).Installed);
            InstallHand("Other-Ordinary", "1.0.0");

            var mods = Installed();
            foreach (var m in mods) m.LatestVersion = m.FullName == "Acme-Aurora" ? published : "9.9.9";
            var row = mods.Single(m => m.FullName == "Acme-Aurora");

            Assert.True(row.IsLocalInstalled);
            Assert.True(row.HoldsOffThunderstore);
            Assert.False(row.UpdateAvailable);
            Assert.False(row.ThunderstoreNewer, "a zip copy was offered the Hexium swap mark");
            // The waiting count and the unattended apply (BlendWindow's two hooks) and Update all
            // all select on this one expression.
            Assert.Equal(new[] { "Other-Ordinary" }, mods.Where(m => m.UpdateAvailable).Select(m => m.FullName));
            // The update half of mods.checkOne.
            Assert.False(ValheimBakaLoader.Forms.BlendWindow.CheckOneSaysUpdateWaiting(published, local, row.HoldsOffThunderstore));

            // mods.update hands the row straight to the service, which leaves it alone without
            // asking Thunderstore anything (the client is strict: a question would throw).
            var dll = File.ReadAllText(At("plugins/Acme-Aurora/Aurora.dll"));
            var skipped = await Service().UpdateModAsync(row);
            Assert.False(skipped.Updated);
            Assert.Null(skipped.Error);
            Assert.Equal(dll, File.ReadAllText(At("plugins/Acme-Aurora/Aurora.dll")));
        }

        [Fact]
        public async Task A_hand_edited_manifest_puts_the_row_back_on_the_ordinary_road()
        {
            Assert.True((await Install(Write("Acme-Aurora-1.4.0-beta.1.zip", Zip(Package(version: "1.4.0-beta.1"))))).Installed);
            File.WriteAllText(At("plugins/Acme-Aurora/manifest.json"), Manifest("Aurora", "1.3.9"));

            var row = Installed().Single(m => m.FullName == "Acme-Aurora");
            row.LatestVersion = "1.4.0";
            Assert.Null(row.InstalledSource);
            Assert.True(row.UpdateAvailable);
        }

        // ------------------------------------------------------------------ 12. the Gale diff

        [Fact]
        public async Task The_Gale_diff_reads_a_local_build_by_name_and_version_only()
        {
            Assert.True((await Install(Write("Acme-Aurora-1.4.0.zip", Zip(Package())))).Installed);
            Assert.True((await Install(Write("Acme-Borealis-2.0.0.zip", Zip(new[] { ("manifest.json", Manifest("Borealis", "2.0.0")), ("b.dll", "b") })))).Installed);
            Assert.True((await Install(Write("Acme-Cirrus-1.0.0.zip", Zip(new[] { ("manifest.json", Manifest("Cirrus", "1.0.0")), ("c.dll", "c") })))).Installed);

            var meta = new GaleProfileMeta
            {
                Mods = new[]
                {
                    new GaleProfileMod { Name = "Acme-Aurora", Enabled = true, Version = "1.4.0" },
                    new GaleProfileMod { Name = "Acme-Cirrus", Enabled = true, Version = "1.5.0" },
                },
            };
            var diff = GaleProfileDiff.Compare(meta, Installed(), Array.Empty<string>());

            Assert.Equal(GaleDiffKind.Same, diff.Rows.Single(r => r.Name == "Acme-Aurora").Kind);
            Assert.Equal(GaleDiffKind.NotInProfile, diff.Rows.Single(r => r.Name == "Acme-Borealis").Kind);
            Assert.Equal(GaleDiffKind.Update, diff.Rows.Single(r => r.Name == "Acme-Cirrus").Kind);
        }
    }
}
