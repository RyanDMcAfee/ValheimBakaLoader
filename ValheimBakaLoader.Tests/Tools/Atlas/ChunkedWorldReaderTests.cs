using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Atlas;
using Xunit;
using Xunit.Abstractions;

namespace ValheimBakaLoader.Tests.Tools.Atlas
{
    /// <summary>
    /// Valheim 1.0 chunked world saves. Two layers of evidence: the real
    /// _main.2.db2 / _main.2.chunks / 00_00__0_1.chunk written by a live 1.0
    /// dedicated server (checked into TestData), and a synthetic writer that
    /// mirrors ZNet.SaveWorldThread / ChunkSaveMapping.Save / ZDO.Save so the
    /// parts a fresh world has none of (portals, cartography tables, build
    /// sites, packed positions and rotations) get exercised too.
    /// </summary>
    [Collection("DiagnosticSink")]
    public class ChunkedWorldReaderTests : IDisposable
    {
        private readonly string _tempDir =
            Path.Combine(Path.GetTempPath(), "vbl-chunked-tests-" + Guid.NewGuid().ToString("N"));

        private readonly ITestOutputHelper _output;

        public ChunkedWorldReaderTests(ITestOutputHelper output)
        {
            _output = output;
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            ChunkedWorldReader.GenerationPicked = null;
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }

        // ------------------------------------------------------------------
        // Real save files from a live Valheim 1.0.7 dedicated server
        // ------------------------------------------------------------------

        private static string RealWorldDir([CallerFilePath] string thisFile = "")
        {
            return Path.Combine(Path.GetDirectoryName(thisFile), "TestData", "ChunkedWorld");
        }

        [Fact]
        public void RealWorld_ParsesEveryChunkedFile()
        {
            string dir = RealWorldDir();
            Assert.True(Directory.Exists(dir), $"missing test resources: {dir}");

            var info = WorldDbReader.TryReadAny(dir);

            Assert.NotNull(info);
            Assert.Equal(41, info.WorldVersion);

            // netTime 2040 s: the game's day is netTime / 1800.
            Assert.Equal(2040.0, info.NetTime, 3);
            Assert.Equal(1, info.DayNumber);

            // _main.2.chunks says one live chunk holding 82 objects.
            Assert.Equal(82, info.ZdoCount);

            // ZoneSystem block (gzip) inside _main.2.db2.
            Assert.Equal(81, info.GeneratedZoneCount);
            Assert.True(info.LocationsGenerated);
            Assert.Equal(12272, info.Locations.Count);
            Assert.Empty(info.GlobalKeys);
            Assert.Equal("", info.EventName);

            // Location prefabs are hashes in 1.0; the vanilla ones resolve back
            // to their names, which is what the map's POI pins key off.
            Assert.Contains(info.Locations, l => l.Prefab == "StartTemple");
            Assert.Contains(info.Locations, l => l.Prefab == "Eikthyrnir");
            Assert.Contains(info.Locations, l => l.Prefab == "Vendor_BlackForest");

            // Every location in a freshly generated vanilla world resolves,
            // so nothing should fall back to a bare decimal hash.
            Assert.DoesNotContain(info.Locations, l => IsBareHash(l.Prefab));

            // A brand new world has no portals, tables or player builds yet.
            Assert.Empty(info.Portals);
            Assert.Empty(info.MapTables);
            Assert.Empty(info.BuildClusters);
        }

        private static bool IsBareHash(string prefab)
        {
            return prefab.Length > 0
                && prefab.All(c => char.IsDigit(c) || c == '-');
        }

        [Fact]
        public void RealWorld_UncommittedGenerationIsIgnored()
        {
            // Copy the real generation 2, then drop a newer generation 3 that is
            // missing its .ok marker: mid-write or failed, so still generation 2.
            string world = Path.Combine(_tempDir, "BakaChunkTest");
            Directory.CreateDirectory(world);
            foreach (string file in Directory.GetFiles(RealWorldDir()))
            {
                File.Copy(file, Path.Combine(world, Path.GetFileName(file)));
            }
            File.Copy(Path.Combine(world, "_main.2.fwl2"), Path.Combine(world, "_main.3.fwl2"));
            File.Copy(Path.Combine(world, "_main.2.chunks"), Path.Combine(world, "_main.3.chunks"));
            File.WriteAllBytes(Path.Combine(world, "_main.3.db2"), new byte[] { 9, 9, 9, 9 }); // garbage on purpose

            var info = WorldDbReader.TryReadAny(world);

            Assert.NotNull(info);
            Assert.Equal(41, info.WorldVersion);
            Assert.Equal(1, info.DayNumber);

            // Commit generation 3 with a valid .ok and it wins - proving the .ok
            // marker (not just the highest number) is what selects a generation.
            File.Copy(Path.Combine(world, "_main.2.db2"), Path.Combine(world, "_main.3.db2"), overwrite: true);
            File.WriteAllBytes(Path.Combine(world, "_main.3.ok"), BitConverter.GetBytes(41));
            File.Delete(Path.Combine(world, "_main.2.ok"));
            Assert.NotNull(WorldDbReader.TryReadAny(world));
        }

        [Fact]
        public void RealWorld_MemberFileResolvesToItsFolder()
        {
            string dir = RealWorldDir();
            foreach (string member in new[] { "_main.2.db2", "_main.2.fwl2", "00_00__0_1.chunk" })
            {
                var info = WorldDbReader.TryReadAny(Path.Combine(dir, member));
                Assert.True(info != null, $"reading via {member} returned null");
                Assert.Equal(41, info.WorldVersion);
            }
        }

        // ------------------------------------------------------------------
        // Synthetic chunked world (mirrors the 1.0 writers)
        // ------------------------------------------------------------------

        private static int Hash(string s) => FwlWriter.GetStableHashCode(s);

        private sealed class ZdoSpec
        {
            public int Prefab;
            public float X, Y, Z;
            public bool SmallPosition;
            public bool Rotation;
            public bool ShortRotation = true;   // the 2 byte yaw-only form
            public bool Connection;
            public List<(int Key, long Value)> Longs = new();
            public List<(int Key, string Value)> Strings = new();
            public List<(int Key, byte[] Value)> ByteArrays = new();
            public List<(int Key, int Value)> Ints = new();
        }

        private static void WriteNumItems(BinaryWriter bw, int n)
        {
            if (n < 128)
            {
                bw.Write((byte)n);
            }
            else
            {
                bw.Write((byte)((n >> 8) | 0x80));
                bw.Write((byte)n);
            }
        }

        /// <summary>ZDO.Save for world version 40+: no sector field.</summary>
        private static void WriteZdo(BinaryWriter bw, ZdoSpec z)
        {
            ushort flags = 0x100; // Persistent
            if (z.Rotation) flags |= 0x1000;
            if (z.SmallPosition) flags |= 0x2000;
            if (z.Connection) flags |= 0x0001;
            if (z.Ints.Count > 0) flags |= 0x0010;
            if (z.Longs.Count > 0) flags |= 0x0020;
            if (z.Strings.Count > 0) flags |= 0x0040;
            if (z.ByteArrays.Count > 0) flags |= 0x0080;

            bw.Write(flags);
            if (z.SmallPosition)
            {
                bw.Write((short)z.X);
                bw.Write((short)z.Z);
            }
            else
            {
                bw.Write(z.X);
                bw.Write(z.Y);
                bw.Write(z.Z);
            }
            bw.Write(z.Prefab);

            if (z.Rotation)
            {
                if (z.ShortRotation)
                {
                    bw.Write((ushort)(0x8000 | 120)); // yaw 60 degrees
                }
                else
                {
                    uint packed = 100u | (200u << 10) | (300u << 20);
                    bw.Write((ushort)(packed >> 16));
                    bw.Write((ushort)(packed & 0xFFFF));
                }
            }
            if ((flags & 0xFF) == 0) return;

            if (z.Connection)
            {
                bw.Write((byte)1);
                bw.Write(123456789);
            }
            if (z.Ints.Count > 0)
            {
                WriteNumItems(bw, z.Ints.Count);
                foreach (var (k, v) in z.Ints) { bw.Write(k); bw.Write(v); }
            }
            if (z.Longs.Count > 0)
            {
                WriteNumItems(bw, z.Longs.Count);
                foreach (var (k, v) in z.Longs) { bw.Write(k); bw.Write(v); }
            }
            if (z.Strings.Count > 0)
            {
                WriteNumItems(bw, z.Strings.Count);
                foreach (var (k, v) in z.Strings) { bw.Write(k); bw.Write(v); }
            }
            if (z.ByteArrays.Count > 0)
            {
                WriteNumItems(bw, z.ByteArrays.Count);
                foreach (var (k, v) in z.ByteArrays)
                {
                    bw.Write(k);
                    bw.Write(v.Length);
                    bw.Write(v);
                }
            }
        }

        private sealed class ChunkSpec
        {
            public ushort Chunk;
            public byte Size;
            public uint Version = 1;
            public List<ZdoSpec> Zdos = new();

            public string FileName =>
                (Chunk >> 8).ToString("x2") + "_" + (Chunk & 0xFF).ToString("x2")
                + "__" + Size + "_" + Version + ".chunk";
        }

        /// <summary>
        /// Writes a complete committed generation: _main.N.fwl2 / .db2 /
        /// .chunks / .ok plus one file per chunk.
        /// </summary>
        private string WriteWorld(
            string worldName,
            int generation,
            double netTime,
            List<ChunkSpec> chunks,
            List<string> globalKeys = null,
            List<(string Prefab, float X, float Y, float Z, bool Placed)> locations = null,
            List<(int Hash, float X, float Y, float Z, bool Placed)> hashLocations = null,
            int zoneCount = 3,
            string eventName = "",
            int worldVersion = 41,
            bool writeOkMarker = true,
            IEnumerable<string> extraChunkFiles = null)
        {
            string dir = Path.Combine(_tempDir, worldName);
            Directory.CreateDirectory(dir);
            string stem = Path.Combine(dir, "_main." + generation);

            // --- _main.N.fwl2 (only its existence matters to this reader) ---
            using (var ms = new MemoryStream())
            {
                using (var pk = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    pk.Write(worldVersion);
                    pk.Write(worldName);
                    pk.Write("seedname12");
                    pk.Write(Hash("seedname12"));
                    pk.Write(1234567890123L);
                    pk.Write(2);        // world gen version
                    pk.Write(true);     // needs db
                    pk.Write(0);        // start keys
                    pk.Write(0);        // player history
                }
                byte[] payload = ms.ToArray();
                using var fs = new FileStream(stem + ".fwl2", FileMode.Create, FileAccess.Write);
                using var bw = new BinaryWriter(fs);
                bw.Write(payload.Length);
                bw.Write(payload);
            }

            // --- _main.N.db2 ---
            byte[] zoneBlock;
            using (var raw = new MemoryStream())
            {
                using (var bw = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
                {
                    bw.Write(zoneCount);
                    for (int i = 0; i < zoneCount; i++)
                    {
                        bw.Write((short)i);       // Vector2s, two int16
                        bw.Write((short)(-i));
                    }
                    bw.Write(32);                 // location version
                    globalKeys ??= new List<string>();
                    bw.Write(globalKeys.Count);
                    foreach (string key in globalKeys) bw.Write(key);
                    bw.Write(true);               // locations generated

                    var all = new List<(int Hash, float X, float Y, float Z, bool Placed)>();
                    foreach (var (prefab, x, y, z, placed) in locations ?? new List<(string, float, float, float, bool)>())
                    {
                        all.Add((Hash(prefab), x, y, z, placed));
                    }
                    all.AddRange(hashLocations ?? new List<(int, float, float, float, bool)>());
                    bw.Write(all.Count);
                    foreach (var (hash, x, y, z, placed) in all)
                    {
                        bw.Write(hash);
                        bw.Write(x); bw.Write(y); bw.Write(z);
                        bw.Write(placed);
                    }
                }
                zoneBlock = Gzip(raw.ToArray());
            }
            using (var fs = new FileStream(stem + ".db2", FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                bw.Write(worldVersion);
                bw.Write(netTime);
                bw.Write(zoneBlock.Length);
                bw.Write(zoneBlock);
                bw.Write(42.5f);            // RandEventSystem timer
                bw.Write(eventName);
                bw.Write(100f);             // event time
                bw.Write(11f); bw.Write(0f); bw.Write(-22f);
                byte[] persistent = { 1, 2, 3 };   // brotli blob, deliberately ignored
                bw.Write(persistent.Length);
                bw.Write(persistent);
            }

            // --- chunk files ---
            foreach (ChunkSpec chunk in chunks)
            {
                using var fs = new FileStream(Path.Combine(dir, chunk.FileName), FileMode.Create, FileAccess.Write);
                using var bw = new BinaryWriter(fs, Encoding.UTF8);
                bw.Write((short)worldVersion);
                bw.Write(chunk.Zdos.Count);
                foreach (ZdoSpec z in chunk.Zdos) WriteZdo(bw, z);
            }
            foreach (string stale in extraChunkFiles ?? Enumerable.Empty<string>())
            {
                // A stale chunk left over from an older save: not in the index,
                // and deliberately unparseable so loading it would be obvious.
                File.WriteAllBytes(Path.Combine(dir, stale), new byte[] { 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE });
            }

            // --- _main.N.chunks ---
            using (var fs = new FileStream(stem + ".chunks", FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                bw.Write((ushort)worldVersion);
                bw.Write(chunks.Sum(c => c.Zdos.Count));
                bw.Write(chunks.Count);
                foreach (ChunkSpec chunk in chunks)
                {
                    bw.Write(chunk.Chunk);
                    bw.Write(chunk.Size);
                    bw.Write(chunk.Version);
                    bw.Write(chunk.Zdos.Count);
                }
            }

            // --- _main.N.ok, written last, the commit marker ---
            if (writeOkMarker)
            {
                File.WriteAllBytes(stem + ".ok", BitConverter.GetBytes(worldVersion));
            }
            return dir;
        }

        private static byte[] Gzip(byte[] raw)
        {
            using var dst = new MemoryStream();
            using (var gz = new GZipStream(dst, CompressionMode.Compress, leaveOpen: true))
            {
                gz.Write(raw, 0, raw.Length);
            }
            return dst.ToArray();
        }

        private static byte[] GzipSharedMap(int textureSize, params int[] exploredPixels)
        {
            using var raw = new MemoryStream();
            using (var bw = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
            {
                int count = textureSize * textureSize;
                bw.Write(3);         // shared map version
                bw.Write(count);
                for (int i = 0; i < count; i++) bw.Write(exploredPixels.Contains(i));
                bw.Write(0);         // no pins
            }
            return Gzip(raw.ToArray());
        }

        [Fact]
        public void Synthetic_PortalsMapTablesAndBuildsAreExtracted()
        {
            // Portals live in their own chunk (1, 0) and are excluded from the
            // regular chunks, so the portal chunk's contents are portals by
            // definition, whatever prefab they carry.
            var portalChunk = new ChunkSpec
            {
                Chunk = 1,
                Size = 0,
                Zdos =
                {
                    new ZdoSpec
                    {
                        Prefab = Hash("portal_wood"), X = 120f, Y = 32f, Z = -80f,
                        Rotation = true, Connection = true,
                        Strings = { (Hash("tag"), "home") },
                    },
                    new ZdoSpec
                    {
                        // A portal prefab this reader has never heard of still
                        // counts, because the chunk it sits in says so.
                        Prefab = Hash("portal_someMod"), X = -400f, Y = 12f, Z = 900f,
                        Rotation = true, ShortRotation = false,
                        Strings = { (Hash("tag"), "outpost") },
                    },
                },
            };

            // Version 2 of this chunk: only dirty chunks are rewritten, so the
            // superseded version 1 file stays on disk and must be ignored.
            var mainChunk = new ChunkSpec { Chunk = 0x0203, Size = 0, Version = 2 };
            mainChunk.Zdos.Add(new ZdoSpec
            {
                Prefab = Hash("piece_cartographytable"), X = 5f, Y = 30f, Z = 6f,
                Longs = { (Hash("creator"), 42L) },
                ByteArrays = { (Hash("data"), GzipSharedMap(4, 0, 5, 15)) },
            });
            // A packed-position ZDO with no payload at all: the record ends
            // right after the prefab.
            mainChunk.Zdos.Add(new ZdoSpec
            {
                Prefab = Hash("Boar"), X = 500f, Z = -700f, SmallPosition = true,
            });
            // A 200-int ZDO exercises the two-byte NumItems path; the stamped
            // pieces after it prove the stream stayed aligned.
            var big = new ZdoSpec { Prefab = Hash("Player_tombstone"), X = 1f, Y = 2f, Z = 3f };
            for (int i = 0; i < 200; i++) big.Ints.Add((i + 1, i));
            mainChunk.Zdos.Add(big);
            for (int i = 0; i < 10; i++)
            {
                mainChunk.Zdos.Add(new ZdoSpec
                {
                    Prefab = Hash("wood_wall"), X = 1000f + i * 2f, Y = 35f, Z = 2000f + i,
                    Rotation = true,
                    Longs = { (Hash("creator"), 1234567L) },
                });
            }

            string dir = WriteWorld(
                "Synthetic", 7, 1800.0 * 123 + 900,
                new List<ChunkSpec> { mainChunk, portalChunk },
                globalKeys: new List<string> { "defeated_eikthyr", "NoHeavySnow" },
                locations: new List<(string, float, float, float, bool)>
                {
                    ("StartTemple", 10f, 35f, -20f, true),
                    ("Eikthyrnir", 300f, 40f, 400f, false),
                },
                hashLocations: new List<(int, float, float, float, bool)>
                {
                    (unchecked((int)0xDEADBEEF), 1f, 2f, 3f, true),
                },
                eventName: "army_theelder",
                extraChunkFiles: new[] { "02_03__0_1.chunk" });

            var info = WorldDbReader.TryReadAny(dir);

            Assert.NotNull(info);
            Assert.Equal(41, info.WorldVersion);
            Assert.Equal(123, info.DayNumber);
            Assert.Equal(3, info.GeneratedZoneCount);
            Assert.True(info.LocationsGenerated);
            Assert.Equal(new[] { "defeated_eikthyr", "NoHeavySnow" }, info.GlobalKeys);
            Assert.Equal(mainChunk.Zdos.Count + portalChunk.Zdos.Count, info.ZdoCount);

            Assert.Equal(2, info.Portals.Count);
            Assert.Contains(info.Portals, p => p.Tag == "home" && p.X == 120f && p.Z == -80f);
            Assert.Contains(info.Portals, p => p.Tag == "outpost" && p.X == -400f && p.Z == 900f);

            var table = Assert.Single(info.MapTables);
            var shared = SharedMapData.TryDecode(table.Data);
            Assert.NotNull(shared);
            Assert.Equal(4, shared.TextureSize);
            Assert.True(shared.Explored[5]);
            Assert.False(shared.Explored[1]);

            var cluster = Assert.Single(info.BuildClusters);
            Assert.Equal(10, cluster.PieceCount);

            // Known location hashes resolve to names, unknown ones keep the hash.
            Assert.Contains(info.Locations, l => l.Prefab == "StartTemple" && l.Placed);
            Assert.Contains(info.Locations, l => l.Prefab == "Eikthyrnir" && !l.Placed);
            Assert.Contains(info.Locations, l => l.Prefab == unchecked((int)0xDEADBEEF).ToString());

            Assert.Equal("army_theelder", info.EventName);
            Assert.Equal(11f, info.EventPosX);
            Assert.Equal(-22f, info.EventPosZ);
        }

        [Fact]
        public void Synthetic_PackedPositionAndRotationKeepTheStreamAligned()
        {
            // Every combination of the packed position and the two packed
            // rotation widths, with a tagged portal last: if any of the record
            // sizes were wrong the tag would not come back.
            var chunk = new ChunkSpec { Chunk = 1, Size = 0 };
            chunk.Zdos.Add(new ZdoSpec { Prefab = Hash("a"), X = 10f, Z = 20f, SmallPosition = true });
            chunk.Zdos.Add(new ZdoSpec { Prefab = Hash("b"), X = 10f, Z = 20f, SmallPosition = true, Rotation = true });
            chunk.Zdos.Add(new ZdoSpec { Prefab = Hash("c"), X = 10f, Z = 20f, SmallPosition = true, Rotation = true, ShortRotation = false });
            chunk.Zdos.Add(new ZdoSpec { Prefab = Hash("d"), X = 1f, Y = 2f, Z = 3f, Rotation = true, ShortRotation = false });
            chunk.Zdos.Add(new ZdoSpec
            {
                Prefab = Hash("portal_stone"), X = 9f, Y = 8f, Z = 7f,
                Rotation = true,
                Strings = { (Hash("tag"), "last") },
            });

            string dir = WriteWorld("Packed", 1, 3600, new List<ChunkSpec> { chunk });
            var info = WorldDbReader.TryReadAny(dir);

            Assert.NotNull(info);
            Assert.Equal(5, info.Portals.Count);
            Assert.Equal("last", info.Portals[4].Tag);
            // The packed position is whole meters with y forced to zero.
            Assert.Equal(10f, info.Portals[0].X);
            Assert.Equal(0f, info.Portals[0].Y);
            Assert.Equal(20f, info.Portals[0].Z);
        }

        [Fact]
        public void Synthetic_MissingChunkFileDoesNotFailTheWholeRead()
        {
            var present = new ChunkSpec { Chunk = 1, Size = 0 };
            present.Zdos.Add(new ZdoSpec
            {
                Prefab = Hash("portal_wood"), X = 1f, Y = 2f, Z = 3f,
                Strings = { (Hash("tag"), "survivor") },
            });

            string dir = WriteWorld("Gappy", 4, 5400, new List<ChunkSpec> { present });

            // Add a second chunk to the index whose file was never written.
            string chunksPath = Path.Combine(dir, "_main.4.chunks");
            using (var fs = new FileStream(chunksPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)41);
                bw.Write(5);
                bw.Write(2);
                bw.Write((ushort)1); bw.Write((byte)0); bw.Write(1u); bw.Write(1);
                bw.Write((ushort)0x0505); bw.Write((byte)0); bw.Write(9u); bw.Write(4);
            }

            var info = WorldDbReader.TryReadAny(dir);

            Assert.NotNull(info);
            Assert.Equal("survivor", Assert.Single(info.Portals).Tag);
            Assert.Equal(5, info.ZdoCount); // the index still reports both chunks

            // The world is only part of a world, and it has to say so: without this the map
            // draws an empty list of portals and build sites as though that were the truth.
            Assert.Equal(2, info.ChunksTotal);
            Assert.Equal(1, info.ChunksSkipped);
        }

        [Fact]
        public void Synthetic_AWholeWorldReportsNothingSkipped()
        {
            var chunk = new ChunkSpec { Chunk = 1, Size = 0 };
            chunk.Zdos.Add(new ZdoSpec { Prefab = Hash("Beech1"), X = 1f, Y = 2f, Z = 3f });

            var info = WorldDbReader.TryReadAny(WriteWorld("Whole", 2, 1800, new List<ChunkSpec> { chunk }));

            Assert.NotNull(info);
            Assert.Equal(1, info.ChunksTotal);
            Assert.Equal(0, info.ChunksSkipped);
        }

        [Fact]
        public void Synthetic_NoCommitMarkerMeansNoWorld()
        {
            string dir = WriteWorld("Uncommitted", 1, 1800, new List<ChunkSpec>(), writeOkMarker: false);
            var reasons = CaptureDiagnostics(() => Assert.Null(WorldDbReader.TryReadAny(dir)));
            Assert.Contains(reasons, r => r.Contains(".ok"));
        }

        [Fact]
        public void Synthetic_UnsupportedChunkedVersionIsRejected()
        {
            string dir = WriteWorld("FromTheFuture", 1, 1800, new List<ChunkSpec>(), worldVersion: 99);
            var reasons = CaptureDiagnostics(() => Assert.Null(WorldDbReader.TryReadAny(dir)));
            Assert.Contains(reasons, r => r.Contains("world version 99 is"));
        }

        // ------------------------------------------------------------------
        // Legacy .db reader must refuse chunked versions instead of guessing
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(40)]
        [InlineData(41)]
        public void LegacyReader_RejectsChunkedWorldVersions(int version)
        {
            // A version 40+ header on a .db: the ZDO record lost its sector
            // field, so the old walk would silently misparse. It must refuse.
            string path = Path.Combine(_tempDir, "v" + version + ".db");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(version);
                bw.Write(1800.0);
                bw.Write(1L);
                bw.Write((uint)1);
                bw.Write(0);              // zdo count
                bw.Write(0);              // zones
                bw.Write(0); bw.Write(0);
                bw.Write(0);
                bw.Write(true);
                bw.Write(0);
                bw.Write(0f); bw.Write(""); bw.Write(0f);
                bw.Write(0f); bw.Write(0f); bw.Write(0f);
            }

            var reasons = CaptureDiagnostics(() =>
            {
                Assert.Null(WorldDbReader.TryRead(path));
                Assert.Null(WorldDbReader.TryReadAny(path));
            });
            Assert.Contains(reasons, r => r.Contains("chunked save"));
        }

        [Fact]
        public void LegacyReader_StillReadsAPreChunkedDb()
        {
            // World version 37 through the new entry point: unchanged behavior.
            string path = Path.Combine(_tempDir, "old.db");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                bw.Write(37);
                bw.Write(1800.0 * 5);
                bw.Write(1L);
                bw.Write((uint)1);
                bw.Write(1);                        // one ZDO
                bw.Write((ushort)(0x100 | 0x0040)); // persistent + strings
                bw.Write((short)0); bw.Write((short)0);   // sector, gone in 1.0
                bw.Write(1f); bw.Write(2f); bw.Write(3f);
                bw.Write(Hash("portal_wood"));
                bw.Write((byte)1);
                bw.Write(Hash("tag"));
                bw.Write("legacy");
                bw.Write(0);                        // zones
                bw.Write(0);                        // pgw version
                bw.Write(26);                       // location version
                bw.Write(0);                        // global keys
                bw.Write(true);
                bw.Write(1);                        // one location, by NAME
                bw.Write("StartTemple");
                bw.Write(1f); bw.Write(2f); bw.Write(3f);
                bw.Write(true);
                bw.Write(0f); bw.Write(""); bw.Write(0f);
                bw.Write(0f); bw.Write(0f); bw.Write(0f);
            }

            var info = WorldDbReader.TryReadAny(path);

            Assert.NotNull(info);
            Assert.Equal(37, info.WorldVersion);
            Assert.Equal(5, info.DayNumber);
            Assert.Equal("legacy", Assert.Single(info.Portals).Tag);
            Assert.Equal("StartTemple", Assert.Single(info.Locations).Prefab);
        }

        // ------------------------------------------------------------------
        // TryReadAny dispatch
        // ------------------------------------------------------------------

        [Fact]
        public void TryReadAny_RejectsNonsensePaths()
        {
            Assert.Null(WorldDbReader.TryReadAny(null));
            Assert.Null(WorldDbReader.TryReadAny("   "));
            Assert.Null(WorldDbReader.TryReadAny(Path.Combine(_tempDir, "nope.db")));
            Assert.Null(WorldDbReader.TryReadAny(Path.Combine(_tempDir, "nope")));

            // An empty folder is a folder, just not a world.
            string empty = Path.Combine(_tempDir, "EmptyFolder");
            Directory.CreateDirectory(empty);
            Assert.Null(WorldDbReader.TryReadAny(empty));

            // A file with an extension the reader does not handle.
            string stray = Path.Combine(_tempDir, "notes.txt");
            File.WriteAllText(stray, "hello");
            Assert.Null(WorldDbReader.TryReadAny(stray));
        }

        [Fact]
        public void TryReadAny_FwlResolvesToItsDbSibling()
        {
            string db = Path.Combine(_tempDir, "Sibling.db");
            using (var fs = new FileStream(db, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                bw.Write(37);
                bw.Write(1800.0 * 2);
                bw.Write(1L); bw.Write((uint)1); bw.Write(0);
                bw.Write(0); bw.Write(0); bw.Write(26); bw.Write(0);
                bw.Write(true); bw.Write(0);
                bw.Write(0f); bw.Write(""); bw.Write(0f);
                bw.Write(0f); bw.Write(0f); bw.Write(0f);
            }
            File.WriteAllBytes(Path.Combine(_tempDir, "Sibling.fwl"), new byte[] { 0 });

            var info = WorldDbReader.TryReadAny(Path.Combine(_tempDir, "Sibling.fwl"));
            Assert.NotNull(info);
            Assert.Equal(2, info.DayNumber);
        }

        // ------------------------------------------------------------------
        // Location name table
        // ------------------------------------------------------------------

        [Fact]
        public void LocationNames_ResolveTheMapPois()
        {
            foreach (string poi in new[]
            {
                "StartTemple", "Eikthyrnir", "GDKing", "Bonemass", "Dragonqueen", "GoblinKing",
                "Mistlands_DvergrBossEntrance1", "FaderLocation", "Vendor_BlackForest",
                "Hildir_camp", "BogWitch_Camp",
            })
            {
                Assert.Equal(poi, WorldLocationNames.Resolve(Hash(poi)));
            }
            Assert.Equal("12345", WorldLocationNames.Resolve(12345));
            Assert.True(WorldLocationNames.Count >= 178);
        }


        // ------------------------------------------------------------------
        // Hostile or damaged .db2 headers
        // ------------------------------------------------------------------

        /// <summary>
        /// Rewrites a world's .db2 with a chosen zone-block length field and
        /// whatever bytes should follow it. Everything before the length is a
        /// valid header so the read gets that far.
        /// </summary>
        private string WriteWorldWithZoneBlockLength(string worldName, int declaredLength, byte[] payload)
        {
            var chunk = new ChunkSpec { Chunk = 1, Size = 0 };
            chunk.Zdos.Add(new ZdoSpec { Prefab = Hash("Beech1"), X = 1f, Y = 2f, Z = 3f });
            string dir = WriteWorld(worldName, 2, 1800, new List<ChunkSpec> { chunk });

            string db2 = Path.Combine(dir, "_main.2.db2");
            using (var fs = new FileStream(db2, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                bw.Write(41);          // world version
                bw.Write(1800.0);      // net time
                bw.Write(declaredLength);
                bw.Write(payload);
            }
            return dir;
        }

        [Fact]
        public void Db2_ZoneBlockLongerThanWhatIsLeftInTheFileIsRejectedBeforeItIsAllocated()
        {
            // One int32 off the disk used to decide the size of a byte array on
            // its own. A corrupt world claiming 200 MB made the reader allocate
            // 200 MB and only then notice the file was 40 bytes long.
            string dir = WriteWorldWithZoneBlockLength("ShortBlock", 200 * 1024 * 1024, new byte[] { 1, 2, 3, 4 });

            var notes = new List<string>();
            long before = GC.GetAllocatedBytesForCurrentThread();
            WorldDbInfo info = ChunkedWorldReader.TryRead(dir, notes.Add);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Null(info);
            Assert.Contains(notes, n => n.Contains("only 4 are left in the file"));
            Assert.True(allocated < 4 * 1024 * 1024,
                $"the rejected read allocated {allocated} bytes, so the length field is still being trusted");
        }

        [Fact]
        public void Db2_ZoneBlockOverTheCeilingIsRejected()
        {
            // 300 MB sits under the old 512 MB bound and over the 256 MB one.
            string dir = WriteWorldWithZoneBlockLength("HugeBlock", 300 * 1024 * 1024, new byte[] { 1, 2, 3, 4 });

            var notes = new List<string>();
            long before = GC.GetAllocatedBytesForCurrentThread();
            WorldDbInfo info = ChunkedWorldReader.TryRead(dir, notes.Add);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Null(info);
            Assert.Contains(notes, n => n.Contains("is out of range") && n.Contains("256 MB"));
            Assert.True(allocated < 4 * 1024 * 1024, $"the rejected read allocated {allocated} bytes");
        }

        [Fact]
        public void Db2_ZoneBlockThatExpandsPastTheCeilingIsCorruptNotValid()
        {
            // A gzip member ends with its uncompressed length. This one is a
            // real, small block with that trailer rewritten to claim 2 GB, which
            // is what a decompression bomb announces about itself. The cap on the
            // compressed size says nothing about this: a few hundred bytes in,
            // gigabytes out.
            byte[] block = Gzip(new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 });
            byte[] lying = (byte[])block.Clone();
            BitConverter.GetBytes(2u * 1024u * 1024u * 1024u).CopyTo(lying, lying.Length - 4);

            string dir = WriteWorldWithZoneBlockLength("Bomb", lying.Length, lying);

            var notes = new List<string>();
            long before = GC.GetAllocatedBytesForCurrentThread();
            WorldDbInfo info = ChunkedWorldReader.TryRead(dir, notes.Add);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Null(info);
            Assert.Contains(notes, n => n.Contains("expands to") && n.Contains("1024 MB ceiling"));
            Assert.True(allocated < 4 * 1024 * 1024,
                $"the rejected read allocated {allocated} bytes, so it decompressed the block first");
        }

        [Fact]
        public void Db2_AZoneBlockBiggerThanOneCopyBufferStillReads()
        {
            // The counted copy replaced CopyTo, so a block that takes more than
            // one pass through the buffer has to still come out whole. The real
            // 1.0 world in TestData decompresses to a couple of hundred
            // kilobytes, which is several buffers worth.
            var info = WorldDbReader.TryReadAny(RealWorldDir());

            Assert.NotNull(info);
            Assert.Equal(12272, info.Locations.Count);
        }

        // ------------------------------------------------------------------
        // Reading again: only when a save landed underneath the read
        // ------------------------------------------------------------------

        [Fact]
        public void ShouldReadAgain_OnlyWhenTheGenerationRotated()
        {
            // A newer committed generation is the game's own proof that a save
            // landed, because it writes N+1 .ok before deleting N.
            Assert.True(ChunkedWorldReader.ShouldReadAgain(generationRead: 4, committedNow: 5, attempt: 0));

            // Same generation still newest: the file is damaged, not rotating.
            Assert.False(ChunkedWorldReader.ShouldReadAgain(generationRead: 4, committedNow: 4, attempt: 0));

            // Nothing committed at all, or something older: also not a rotation.
            Assert.False(ChunkedWorldReader.ShouldReadAgain(generationRead: 4, committedNow: 3, attempt: 0));
            Assert.False(ChunkedWorldReader.ShouldReadAgain(generationRead: 4, committedNow: -1, attempt: 0));

            // One re-read and no more, whatever the folder does after that.
            Assert.False(ChunkedWorldReader.ShouldReadAgain(generationRead: 4, committedNow: 5, attempt: 1));
        }

        [Fact]
        public void MissingChunk_IsCalledDamageAndTheReadIsNotRepeated()
        {
            var present = new ChunkSpec { Chunk = 1, Size = 0 };
            present.Zdos.Add(new ZdoSpec { Prefab = Hash("portal_wood"), X = 1f, Y = 2f, Z = 3f });
            string dir = WriteWorld("StillBroken", 4, 5400, new List<ChunkSpec> { present });

            // Point the index at a second chunk whose file was never written.
            using (var fs = new FileStream(Path.Combine(dir, "_main.4.chunks"), FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)41);
                bw.Write(5);
                bw.Write(2);
                bw.Write((ushort)1); bw.Write((byte)0); bw.Write(1u); bw.Write(1);
                bw.Write((ushort)0x0505); bw.Write((byte)0); bw.Write(9u); bw.Write(4);
            }

            // Warm the code path so the timing printed below is the read and
            // not the first-call JIT.
            Assert.NotNull(ChunkedWorldReader.TryRead(dir, null));

            // Every generation the reader picks up, so "it did not read the
            // folder twice" is something this test watches happen instead of
            // something it infers from a stopwatch.
            var picked = new List<int>();
            ChunkedWorldReader.GenerationPicked = (folder, generation) => picked.Add(generation);

            var notes = new List<string>();
            var clock = Stopwatch.StartNew();
            WorldDbInfo info;
            try
            {
                info = ChunkedWorldReader.TryRead(dir, notes.Add);
            }
            finally
            {
                ChunkedWorldReader.GenerationPicked = null;
            }
            clock.Stop();

            // The rest of the world still comes back, and the map is told a piece
            // of it is missing.
            Assert.NotNull(info);
            Assert.Equal(2, info.ChunksTotal);
            Assert.Equal(1, info.ChunksSkipped);
            Assert.Contains(notes, n => n.Contains("damage and not a save landing"));
            Assert.Contains(notes, n => n.Contains("could not be read") && n.Contains("skipping"));

            // Generation 4 is still the newest committed one, so there is nothing
            // to wait for and nothing to read again. The old reader slept 250 ms
            // on this exact path before reaching the same answer; the timing
            // below is printed for information and nothing hangs on it, because
            // a busy machine can make any wall-clock number say anything.
            _output.WriteLine($"damaged-chunk read took {clock.Elapsed.TotalMilliseconds:F1} ms");
            Assert.Equal(new[] { 4 }, picked);
        }

        [Fact]
        public void RotatedGeneration_AlreadyCommittedBeforeTheRead_IsReadWhole()
        {
            // The other side of the same rule, end to end: generation 4 has lost
            // its chunk file AND generation 5 is committed, which is what a
            // reader sees a moment after a save lands. The world that comes back
            // has to be generation 5, whole, with no complaint about damage.
            var four = new ChunkSpec { Chunk = 1, Size = 0 };
            four.Zdos.Add(new ZdoSpec { Prefab = Hash("portal_wood"), X = 1f, Y = 2f, Z = 3f });
            string dir = WriteWorld("Rotated", 4, 5400, new List<ChunkSpec> { four });
            File.Delete(Path.Combine(dir, four.FileName));

            var five = new ChunkSpec { Chunk = 2, Size = 0, Version = 7 };
            five.Zdos.Add(new ZdoSpec
            {
                Prefab = Hash("portal_wood"), X = 40f, Y = 5f, Z = 60f,
                Strings = { (Hash("tag"), "after the save") },
            });
            WriteWorld("Rotated", 5, 7200, new List<ChunkSpec> { five });

            var notes = new List<string>();
            WorldDbInfo info = ChunkedWorldReader.TryRead(dir, notes.Add);

            Assert.NotNull(info);
            Assert.Equal("after the save", Assert.Single(info.Portals).Tag);
            Assert.Equal(0, info.ChunksSkipped);
            Assert.DoesNotContain(notes, n => n.Contains("damage"));
        }

        [Fact]
        public void SaveLandingMidRead_MakesTheReaderTakeTheNewGenerationInstead()
        {
            // The case the re-read branch exists for, driven for real. The test
            // above starts with both generations already on disk, so the reader
            // picks the newer one straight away and never reaches the branch.
            // Here generation 5 lands after the reader has committed to reading
            // generation 4, in the order the game writes it: 5 whole and marked
            // committed first, then 4 chunk file deleted underneath the read.
            var four = new ChunkSpec { Chunk = 1, Size = 0 };
            four.Zdos.Add(new ZdoSpec
            {
                Prefab = Hash("portal_wood"), X = 1f, Y = 2f, Z = 3f,
                Strings = { (Hash("tag"), "before the save") },
            });
            string dir = WriteWorld("MidRead", 4, 5400, new List<ChunkSpec> { four });

            var five = new ChunkSpec { Chunk = 2, Size = 0, Version = 7 };
            five.Zdos.Add(new ZdoSpec
            {
                Prefab = Hash("portal_wood"), X = 40f, Y = 5f, Z = 60f,
                Strings = { (Hash("tag"), "after the save") },
            });

            var picked = new List<int>();
            ChunkedWorldReader.GenerationPicked = (folder, generation) =>
            {
                picked.Add(generation);
                if (generation != 4) return;
                WriteWorld("MidRead", 5, 7200, new List<ChunkSpec> { five });
                File.Delete(Path.Combine(dir, four.FileName));
            };

            var notes = new List<string>();
            WorldDbInfo info;
            try
            {
                info = ChunkedWorldReader.TryRead(dir, notes.Add);
            }
            finally
            {
                ChunkedWorldReader.GenerationPicked = null;
            }

            // It really did read twice, and the second read was the save that
            // had just landed.
            Assert.Equal(new[] { 4, 5 }, picked);

            // What comes back is generation 5, whole, with no complaint filed
            // about the generation it walked away from.
            Assert.NotNull(info);
            Assert.Equal("after the save", Assert.Single(info.Portals).Tag);
            Assert.Equal(0, info.ChunksSkipped);
            Assert.Equal(7200.0, info.NetTime, 3);
            Assert.DoesNotContain(notes, n => n.Contains("damage"));
            Assert.DoesNotContain(notes, n => n.Contains("could not be read"));
        }
        // ------------------------------------------------------------------
        // Reading a lot of chunk files
        // ------------------------------------------------------------------

        private static List<ChunkSpec> ManyChunks(int count)
        {
            var chunks = new List<ChunkSpec>(count);
            for (int i = 0; i < count; i++)
            {
                // Chunk 1 size 0 is the portal chunk, so it is skipped here and
                // every portal below is found by its prefab instead.
                var chunk = new ChunkSpec { Chunk = (ushort)(i + 2), Size = 0, Version = (uint)(i + 1) };
                chunk.Zdos.Add(new ZdoSpec
                {
                    Prefab = Hash("portal_wood"), X = i, Y = 0f, Z = -i,
                    Strings = { (Hash("tag"), "portal " + i) },
                });
                chunks.Add(chunk);
            }
            return chunks;
        }

        [Fact]
        public void ManyChunks_ComeBackWholeAndInTheSameOrderEveryTime()
        {
            const int count = 400;
            string dir = WriteWorld("BigWorld", 3, 3600, ManyChunks(count));

            var clock = Stopwatch.StartNew();
            WorldDbInfo first = ChunkedWorldReader.TryRead(dir, null);
            clock.Stop();

            Assert.NotNull(first);
            Assert.Equal(count, first.ChunksTotal);
            Assert.Equal(0, first.ChunksSkipped);
            Assert.Equal(count, first.Portals.Count);

            // The chunk files are read a few at a time now, so the one thing that
            // could quietly rot is the order they are merged in. It has to be the
            // order of the index, every time, or the same world draws differently
            // on two runs.
            for (int i = 0; i < count; i++)
            {
                Assert.Equal("portal " + i, first.Portals[i].Tag);
            }

            for (int run = 0; run < 3; run++)
            {
                WorldDbInfo again = ChunkedWorldReader.TryRead(dir, null);
                Assert.NotNull(again);
                Assert.Equal(
                    string.Join(",", first.Portals.ConvertAll(p => p.Tag)),
                    string.Join(",", again.Portals.ConvertAll(p => p.Tag)));
            }

            _output.WriteLine($"{count} chunk files read in {clock.Elapsed.TotalMilliseconds:F1} ms");
        }

        [Fact]
        public void RealWorld_ReadsRepeatedlyAtASaneSpeed()
        {
            // The measurement F8 asks for, on the real 1.0 save in TestData. It
            // is one chunk file next to a 143 KB .db2, so this is mostly the
            // header and zone-block path; the many-file gain is what the
            // synthetic world above covers.
            string dir = RealWorldDir();
            Assert.NotNull(WorldDbReader.TryReadAny(dir));   // warm up

            const int runs = 20;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < runs; i++)
            {
                Assert.NotNull(WorldDbReader.TryReadAny(dir));
            }
            clock.Stop();

            _output.WriteLine($"real 1.0 world read {runs} times in {clock.Elapsed.TotalMilliseconds:F1} ms "
                + $"({clock.Elapsed.TotalMilliseconds / runs:F2} ms per read)");
            Assert.True(clock.Elapsed.TotalSeconds < 20,
                $"{runs} reads of the real world took {clock.Elapsed.TotalSeconds:F1} s");
        }

        // ------------------------------------------------------------------

        private static List<string> CaptureDiagnostics(Action action)
        {
            var captured = new List<string>();
            Action<string> previous = WorldDbReader.DiagnosticSink;
            WorldDbReader.DiagnosticSink = message => captured.Add(message);
            try
            {
                action();
            }
            finally
            {
                WorldDbReader.DiagnosticSink = previous;
            }
            return captured;
        }
    }
}
