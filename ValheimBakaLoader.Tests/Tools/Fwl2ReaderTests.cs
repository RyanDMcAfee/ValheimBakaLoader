using System;
using System.IO;
using System.Text;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Reading the Valheim 1.0 "_main.{N}.fwl2" world metadata. The fields up to
    /// worldGenVersion sit in the same order as in the pre-1.0 .fwl, so one reader serves both;
    /// the point of these tests is to hold that claim against the bytes the game really wrote.
    /// </summary>
    public class Fwl2ReaderTests : IDisposable
    {
        private readonly string SaveFolder =
            Path.Combine(Path.GetTempPath(), "vbl-fwl2-tests-" + Guid.NewGuid().ToString("N"));

        /// <summary>
        /// "_main.1.fwl2" lifted straight out of a world a live Valheim 1.0 dedicated server
        /// wrote (worldVersion 41), copied into the test resources unchanged.
        /// </summary>
        private static string RealFwl2Path =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "BakaLegacy_main.1.fwl2");

        public void Dispose()
        {
            try { Directory.Delete(SaveFolder, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void TryRead_ParsesARealValheim10Fwl2()
        {
            Assert.True(File.Exists(RealFwl2Path), $"Missing test resource: {RealFwl2Path}");

            var info = FwlReader.TryRead(RealFwl2Path);

            Assert.NotNull(info);
            Assert.Equal("BakaLegacy", info.WorldName);
            Assert.Equal("SeedSeed99", info.SeedName);
            // The numeric seed is its own field on the wire, so it is asserted as the literal
            // the capture holds rather than re-derived from the seed string.
            Assert.Equal(123456789, info.Seed);
            Assert.Equal(41, info.WorldVersion);       // Version.World.DeepNorth
            Assert.Equal(2, info.WorldGenVersion);
            Assert.NotEqual(0, info.Uid);
            Assert.Equal(WorldFormat.Chunked, info.Format);
        }

        [Fact]
        public void TryReadWorld_FindsTheCommittedGenerationOfAChunkedWorld()
        {
            var worldDir = Path.Combine(SaveFolder, "worlds_local", "BakaLegacy");
            Directory.CreateDirectory(worldDir);

            // Generation 1, fully committed.
            File.Copy(RealFwl2Path, Path.Combine(worldDir, "_main.1.fwl2"));
            File.WriteAllBytes(Path.Combine(worldDir, "_main.1.db2"), DbHeader(41, 1800));
            File.WriteAllBytes(Path.Combine(worldDir, "_main.1.chunks"), new byte[10]);
            File.WriteAllBytes(Path.Combine(worldDir, "_main.1.ok"), BitConverter.GetBytes(41));

            var info = FwlReader.TryReadWorld(SaveFolder, "BakaLegacy");
            Assert.NotNull(info);
            Assert.Equal("BakaLegacy", info.WorldName);
            Assert.Equal(41, info.WorldVersion);

            Assert.EndsWith("_main.1.fwl2", FwlReader.FindWorldMeta(SaveFolder, "BakaLegacy"));

            // FindWorldFwl stays deliberately legacy-only: its callers pair the .fwl with a
            // sibling ".db" by extension, which only holds for the pre-1.0 layout.
            Assert.Null(FwlReader.FindWorldFwl(SaveFolder, "BakaLegacy"));
        }

        [Fact]
        public void TryReadWorld_StillReadsAnUncommittedGenerationsSeed()
        {
            // A world created but never saved has only its "_main.0.fwl2". The seed is already
            // decided at that point, so the World hall must still be able to show it.
            var worldDir = Path.Combine(SaveFolder, "worlds_local", "BakaLegacy");
            Directory.CreateDirectory(worldDir);
            File.Copy(RealFwl2Path, Path.Combine(worldDir, "_main.0.fwl2"));

            var info = FwlReader.TryReadWorld(SaveFolder, "BakaLegacy");
            Assert.NotNull(info);
            Assert.Equal("SeedSeed99", info.SeedName);
        }

        [Fact]
        public void TryRead_ParsesASyntheticFwl2()
        {
            Directory.CreateDirectory(SaveFolder);
            var path = Path.Combine(SaveFolder, "_main.7.fwl2");
            File.WriteAllBytes(path, BuildFwl2("Synthetic", "abc123", 4242, 99887766554433L));

            var info = FwlReader.TryRead(path);
            Assert.NotNull(info);
            Assert.Equal("Synthetic", info.WorldName);
            Assert.Equal("abc123", info.SeedName);
            Assert.Equal(4242, info.Seed);
            Assert.Equal(99887766554433L, info.Uid);
            Assert.Equal(41, info.WorldVersion);
            Assert.Equal(2, info.WorldGenVersion);
            Assert.Equal(WorldFormat.Chunked, info.Format);
        }

        [Fact]
        public void TryRead_ReturnsNullForGarbageWithAFwl2Name()
        {
            Directory.CreateDirectory(SaveFolder);
            var path = Path.Combine(SaveFolder, "_main.1.fwl2");
            File.WriteAllBytes(path, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 1 });
            Assert.Null(FwlReader.TryRead(path));
        }

        [Fact]
        public void WriteNewWorld_RefusesWhenAChunkedWorldDirectoryAlreadyExists()
        {
            // The seed invariant has to see a 1.0 world for what it is: a DIRECTORY. Without
            // that, choosing a seed would drop a conflicting .fwl beside a live world.
            var worldDir = Path.Combine(SaveFolder, "worlds_local", "BakaLegacy");
            Directory.CreateDirectory(worldDir);
            File.Copy(RealFwl2Path, Path.Combine(worldDir, "_main.1.fwl2"));

            Assert.Throws<InvalidOperationException>(
                () => FwlWriter.WriteNewWorld(SaveFolder, "BakaLegacy", "someseed"));
            Assert.False(File.Exists(Path.Combine(SaveFolder, "worlds_local", "BakaLegacy.fwl")));
        }

        [Fact]
        public void WriteNewWorld_StillWritesTheLegacyLayoutForABrandNewWorld()
        {
            // Valheim 1.0 accepts the v37 .fwl and converts it on the first save, so BakaLoader
            // keeps writing that and never hand-writes a .fwl2.
            var written = FwlWriter.WriteNewWorld(SaveFolder, "Fresh", "myseed");
            Assert.Equal(37, written.WorldVersion);
            Assert.True(File.Exists(Path.Combine(SaveFolder, "worlds_local", "Fresh.fwl")));

            var stored = WorldStore.Find(SaveFolder, "Fresh");
            Assert.NotNull(stored);
            Assert.Equal(WorldFormat.Legacy, stored.Format);
        }

        private static byte[] DbHeader(int version, double netTime)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(version);
            w.Write(netTime);
            w.Write(new byte[16]);
            w.Flush();
            return ms.ToArray();
        }

        private static byte[] BuildFwl2(string worldName, string seedName, int seed, long uid)
        {
            using var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(41);             // worldVersion
                w.Write(worldName);
                w.Write(seedName);
                w.Write(seed);
                w.Write(uid);
                w.Write(2);              // worldGenVersion
                w.Write(true);           // needsDB
                w.Write(0);              // global key count
                w.Write(0);              // player history count
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
    }
}
