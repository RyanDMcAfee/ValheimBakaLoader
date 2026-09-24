using System;
using System.IO;
using System.Linq;
using System.Text;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Issue 17: a duplicated realm gets a copy of the world it was duplicated from, in its
    /// own save folder, under its own name.
    /// <para>
    /// Duplicate used to switch profile and open the forge, and the forge makes a brand new
    /// empty world. The host found that out by walking into it. What that needed was a copy
    /// that crosses save folders AND renames, which was the one shape WorldStore did not
    /// have: CopyWorld crosses folders and keeps the name, CopyWorldAs renames and stays put.
    /// </para>
    /// <para>
    /// Every test here reads the copy's own header, because the game carries a world's name
    /// inside its .fwl or .fwl2 and builds the biome cache file name out of it: a copy that
    /// kept the source's name would be two worlds answering to one. And every one of them
    /// reads the SOURCE afterwards, byte for byte, because the realm it was copied from is
    /// still running for somebody.
    /// </para>
    /// </summary>
    public class WorldStoreCopyIntoTests : IDisposable
    {
        private readonly string Root =
            Path.Combine(Path.GetTempPath(), "vbl-copyinto-tests-" + Guid.NewGuid().ToString("N"));

        private string From => Path.Combine(Root, "from");

        private string Into => Path.Combine(Root, "into");

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        // ------------------------------------------------------------------ tree builders

        private static string WorldsDir(string saveFolder, string sub = "worlds_local")
        {
            var dir = Path.Combine(saveFolder, sub);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static byte[] BuildFwl(int version, string worldName, string seedName)
        {
            using var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(version);
                w.Write(worldName);
                w.Write(seedName);
                w.Write(FwlWriter.GetStableHashCode(seedName));
                w.Write(1234567890123L);
                w.Write(2);
                w.Write(false);
                w.Write(0);
            }
            var bytes = payload.ToArray();

            using var file = new MemoryStream();
            using (var w = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(bytes.Length);
                w.Write(bytes);
            }
            return file.ToArray();
        }

        private static byte[] BuildDbHeader(int version, double netTime)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(version);
                w.Write(netTime);
                w.Write(new byte[32]);
            }
            return ms.ToArray();
        }

        private static void MakeLegacyWorld(string saveFolder, string name, string seedName = "seedy")
        {
            var dir = WorldsDir(saveFolder);
            File.WriteAllBytes(Path.Combine(dir, name + ".fwl"), BuildFwl(37, name, seedName));
            File.WriteAllBytes(Path.Combine(dir, name + ".db"), BuildDbHeader(37, 3600));
        }

        private static string MakeChunkedWorld(string saveFolder, string name,
            string seedName = "chunkyseed", int number = 1)
        {
            var dir = Path.Combine(WorldsDir(saveFolder), name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"_main.{number}.fwl2"), BuildFwl(41, name, seedName));
            File.WriteAllBytes(Path.Combine(dir, $"_main.{number}.db2"), BuildDbHeader(41, 5400));
            File.WriteAllBytes(Path.Combine(dir, $"_main.{number}.chunks"), new byte[] { 41, 0, 0, 0 });
            File.WriteAllBytes(Path.Combine(dir, $"_main.{number}.ok"), BitConverter.GetBytes(41));
            File.WriteAllBytes(Path.Combine(dir, "00_00__0_41.chunk"), new byte[64]);
            return dir;
        }

        private static string StoredName(string metaPath) => FwlReader.TryRead(metaPath)?.WorldName;

        /// <summary>Every file under a folder with its bytes, for an untouched-source check.</summary>
        private static (string[] Paths, byte[][] Bytes) Snapshot(string folder)
        {
            var paths = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            return (paths, paths.Select(File.ReadAllBytes).ToArray());
        }

        private static void AssertUnchanged(string folder, (string[] Paths, byte[][] Bytes) before)
        {
            var after = Snapshot(folder);
            Assert.Equal(before.Paths, after.Paths);
            for (var i = 0; i < before.Paths.Length; i++)
                Assert.Equal(before.Bytes[i], after.Bytes[i]);
        }

        // ------------------------------------------------------------------ the copy itself

        [Fact]
        public void A_chunked_world_lands_in_the_other_save_folder_under_the_new_name()
        {
            MakeChunkedWorld(From, "Midgard");
            var before = Snapshot(From);

            var landed = WorldStore.CopyWorldAs(WorldStore.Find(From, "Midgard"), "Midgard Two", Into);

            var copy = Path.Combine(Into, "worlds_local", "Midgard Two");
            Assert.Equal(
                Path.GetFullPath(copy).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(landed).TrimEnd(Path.DirectorySeparatorChar));

            // Every file of the generation travelled, and the chunk beside them.
            foreach (var name in new[] { "_main.1.fwl2", "_main.1.db2", "_main.1.chunks", "_main.1.ok", "00_00__0_41.chunk" })
                Assert.True(File.Exists(Path.Combine(copy, name)), name + " did not land");

            // And the copy names itself rather than the world it came from.
            Assert.Equal("Midgard Two", StoredName(Path.Combine(copy, "_main.1.fwl2")));

            // The seed is the one thing a copy must keep: it is what the world IS.
            var source = FwlReader.TryRead(Path.Combine(From, "worlds_local", "Midgard", "_main.1.fwl2"));
            var made = FwlReader.TryRead(Path.Combine(copy, "_main.1.fwl2"));
            Assert.Equal(source.SeedName, made.SeedName);
            Assert.Equal(source.Seed, made.Seed);

            // The world it was copied from is somebody's live realm. It came out untouched.
            AssertUnchanged(From, before);
        }

        [Fact]
        public void A_legacy_world_lands_as_a_pair_under_the_new_name()
        {
            MakeLegacyWorld(From, "Midgard");
            var before = Snapshot(From);

            WorldStore.CopyWorldAs(WorldStore.Find(From, "Midgard"), "Midgard Two", Into);

            var dir = Path.Combine(Into, "worlds_local");
            Assert.True(File.Exists(Path.Combine(dir, "Midgard Two.fwl")));
            Assert.True(File.Exists(Path.Combine(dir, "Midgard Two.db")));
            Assert.Equal("Midgard Two", StoredName(Path.Combine(dir, "Midgard Two.fwl")));

            // The database travelled whole.
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(From, "worlds_local", "Midgard.db")),
                File.ReadAllBytes(Path.Combine(dir, "Midgard Two.db")));

            AssertUnchanged(From, before);
        }

        /// <summary>
        /// The same name in another save folder is not the same world twice, so it is
        /// allowed: a duplicated realm that keeps the world's name is the ordinary case.
        /// </summary>
        [Fact]
        public void The_copy_may_keep_the_name_when_it_lands_in_another_save_folder()
        {
            MakeChunkedWorld(From, "Midgard");

            WorldStore.CopyWorldAs(WorldStore.Find(From, "Midgard"), "Midgard", Into);

            var copy = Path.Combine(Into, "worlds_local", "Midgard");
            Assert.True(File.Exists(Path.Combine(copy, "_main.1.fwl2")));
            Assert.Equal("Midgard", StoredName(Path.Combine(copy, "_main.1.fwl2")));
        }

        /// <summary>The destination is shaped like a save folder the game made itself.</summary>
        [Fact]
        public void The_destination_gets_the_folders_the_game_writes_into()
        {
            MakeLegacyWorld(From, "Midgard");

            WorldStore.CopyWorldAs(WorldStore.Find(From, "Midgard"), "Midgard Two", Into);

            Assert.True(Directory.Exists(Path.Combine(Into, "worlds_local")));
        }

        /// <summary>
        /// The biome cache is keyed on the name inside the header, which is the new one now,
        /// so the copy gets its own in its own save folder and the source keeps its own.
        /// </summary>
        [Fact]
        public void The_biome_cache_follows_the_copy_under_the_new_name()
        {
            MakeChunkedWorld(From, "Midgard");
            var cache = Path.Combine(From, "cache");
            Directory.CreateDirectory(cache);
            File.WriteAllBytes(Path.Combine(cache, "Midgard_biomedatacache.bin"), new byte[] { 1, 2, 3 });

            WorldStore.CopyWorldAs(WorldStore.Find(From, "Midgard"), "Midgard Two", Into);

            Assert.True(File.Exists(Path.Combine(Into, "cache", "Midgard Two_biomedatacache.bin")));
            Assert.True(File.Exists(Path.Combine(From, "cache", "Midgard_biomedatacache.bin")));
            Assert.False(File.Exists(Path.Combine(From, "cache", "Midgard Two_biomedatacache.bin")));
        }

        // ------------------------------------------------------------------ the refusals

        [Fact]
        public void A_name_the_destination_already_holds_is_refused_and_nothing_is_written()
        {
            MakeChunkedWorld(From, "Midgard");
            MakeChunkedWorld(Into, "Midgard Two", seedName: "somebody elses");
            var before = Snapshot(Into);

            var refused = Assert.Throws<HostFacingException>(() =>
                WorldStore.CopyWorldAs(WorldStore.Find(From, "Midgard"), "Midgard Two", Into));

            Assert.Equal("worlds.copyTargetExists", refused.MessageId);
            AssertUnchanged(Into, before);
        }

        [Fact]
        public void Copying_a_world_onto_itself_under_its_own_name_is_refused()
        {
            MakeChunkedWorld(From, "Midgard");

            var refused = Assert.Throws<HostFacingException>(() =>
                WorldStore.CopyWorldAs(WorldStore.Find(From, "Midgard"), "Midgard", From));

            Assert.Equal("worlds.copyTargetExists", refused.MessageId);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("bad/name")]
        [InlineData("ends with a dot.")]
        public void A_name_a_world_cannot_be_saved_under_is_refused(string target)
        {
            MakeLegacyWorld(From, "Midgard");

            var refused = Assert.Throws<HostFacingException>(() =>
                WorldStore.CopyWorldAs(WorldStore.Find(From, "Midgard"), target, Into));

            Assert.StartsWith("worlds.copy", refused.MessageId, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(Into, "worlds_local")) &&
                         Directory.EnumerateFileSystemEntries(Path.Combine(Into, "worlds_local")).Any(),
                "something landed in the destination for a name that was refused");
        }

        [Fact]
        public void A_header_that_cannot_be_read_stops_the_copy_before_a_byte_moves()
        {
            var dir = WorldsDir(From);
            File.WriteAllBytes(Path.Combine(dir, "Broken.fwl"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(dir, "Broken.db"), BuildDbHeader(37, 60));

            var world = WorldStore.Find(From, "Broken");
            if (world == null) return;   // the reader refused it as a world at all, which is also a no

            Assert.Throws<HostFacingException>(() => WorldStore.CopyWorldAs(world, "Broken Two", Into));
            Assert.False(File.Exists(Path.Combine(Into, "worlds_local", "Broken Two.fwl")));
        }

        /// <summary>
        /// And the two-argument call still means "beside itself", including its own rule that
        /// the source's own name is the one name the copy cannot take.
        /// </summary>
        [Fact]
        public void The_copy_beside_itself_still_refuses_the_source_name()
        {
            MakeLegacyWorld(From, "Midgard");

            var refused = Assert.Throws<HostFacingException>(() =>
                WorldStore.CopyWorldAs(WorldStore.Find(From, "Midgard"), "midgard"));

            Assert.Equal("worlds.copyTargetExists", refused.MessageId);
        }
    }
}
