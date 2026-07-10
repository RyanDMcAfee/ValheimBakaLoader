using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Atlas;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools.Atlas
{
    /// <summary>
    /// Byte-exact synthetic .db round-trips for WorldDbReader plus the
    /// cartography-table SharedMapData decoder. The writer below mirrors the
    /// game's save layout (world version 37) verified against the dedicated
    /// server assembly.
    /// </summary>
    public class WorldDbReaderTests : IDisposable
    {
        private readonly string _tempDir =
            Path.Combine(Path.GetTempPath(), "vbl-db-tests-" + Guid.NewGuid().ToString("N"));

        public WorldDbReaderTests()
        {
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }

        // ------------------------------------------------------------------
        // Synthetic .db writer (mirrors ZNet.SaveWorld for v37)
        // ------------------------------------------------------------------

        private static int Hash(string s) => FwlWriter.GetStableHashCode(s);

        private sealed class ZdoSpec
        {
            public int Prefab;
            public float X, Y, Z;
            public bool Rotation;
            public bool Connection;
            public List<(int Key, long Value)> Longs = new();
            public List<(int Key, string Value)> Strings = new();
            public List<(int Key, byte[] Value)> ByteArrays = new();
            public List<(int Key, int Value)> Ints = new();
            public List<(int Key, float Value)> Floats = new();
        }

        private static void WriteNumItems(BinaryWriter bw, int n)
        {
            // ZPackage.WriteNumItems (v>=33)
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

        private static void WriteZdo(BinaryWriter bw, ZdoSpec z)
        {
            ushort flags = 0x100; // persistent
            if (z.Rotation) flags |= 0x1000;
            if (z.Connection) flags |= 0x0001;
            if (z.Floats.Count > 0) flags |= 0x0002;
            if (z.Ints.Count > 0) flags |= 0x0010;
            if (z.Longs.Count > 0) flags |= 0x0020;
            if (z.Strings.Count > 0) flags |= 0x0040;
            if (z.ByteArrays.Count > 0) flags |= 0x0080;

            bw.Write(flags);
            bw.Write((short)(z.X / 64f)); // sector x
            bw.Write((short)(z.Z / 64f)); // sector y
            bw.Write(z.X);
            bw.Write(z.Y);
            bw.Write(z.Z);
            bw.Write(z.Prefab);

            if (z.Rotation)
            {
                bw.Write(10f); bw.Write(20f); bw.Write(30f);
            }
            if ((flags & 0xFF) == 0) return;

            if (z.Connection)
            {
                bw.Write((byte)1);       // connection type
                bw.Write(123456789);     // target hash
            }
            if (z.Floats.Count > 0)
            {
                WriteNumItems(bw, z.Floats.Count);
                foreach (var (k, v) in z.Floats) { bw.Write(k); bw.Write(v); }
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

        private static byte[] BuildDb(
            int version,
            double netTime,
            List<ZdoSpec> zdos,
            List<string> globalKeys = null,
            List<(string Prefab, float X, float Y, float Z, bool Placed)> locations = null,
            string eventName = "")
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(version);
            bw.Write(netTime);

            // ZDOMan
            bw.Write(9999L);        // session id
            bw.Write((uint)77);     // next uid
            bw.Write(zdos.Count);
            foreach (var z in zdos) WriteZdo(bw, z);

            // ZoneSystem
            bw.Write(2);            // generated zones
            bw.Write(0); bw.Write(0);
            bw.Write(1); bw.Write(-1);
            bw.Write(0);            // pgw version
            bw.Write(26);           // location version
            globalKeys ??= new List<string>();
            bw.Write(globalKeys.Count);
            foreach (var k in globalKeys) bw.Write(k);
            bw.Write(true);         // locations generated
            locations ??= new List<(string, float, float, float, bool)>();
            bw.Write(locations.Count);
            foreach (var (prefab, x, y, z, placed) in locations)
            {
                bw.Write(prefab);
                bw.Write(x); bw.Write(y); bw.Write(z);
                bw.Write(placed);
            }

            // RandEventSystem
            bw.Write(42.5f);        // event timer
            bw.Write(eventName);
            bw.Write(100f);         // event time
            bw.Write(11f); bw.Write(0f); bw.Write(-22f); // event pos

            bw.Flush();
            return ms.ToArray();
        }

        private WorldDbInfo ParseBytes(byte[] db)
        {
            string path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".db");
            File.WriteAllBytes(path, db);
            return WorldDbReader.TryRead(path);
        }

        private static byte[] GzipSharedMap(int version, int textureSize, bool[] explored, List<SharedMapPin> pins = null)
        {
            using var raw = new MemoryStream();
            using (var bw = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(version);
                bw.Write(textureSize * textureSize);
                for (int i = 0; i < textureSize * textureSize; i++)
                {
                    bw.Write(explored != null && explored[i]);
                }
                if (version >= 2)
                {
                    pins ??= new List<SharedMapPin>();
                    bw.Write(pins.Count);
                    foreach (var p in pins)
                    {
                        bw.Write(p.OwnerId);
                        bw.Write(p.Name);
                        bw.Write(p.X); bw.Write(p.Y); bw.Write(p.Z);
                        bw.Write(p.Type);
                        bw.Write(p.Checked);
                        if (version >= 3) bw.Write(p.PlatformUserId);
                    }
                }
            }
            using var dst = new MemoryStream();
            using (var gz = new GZipStream(dst, CompressionMode.Compress, leaveOpen: true))
            {
                raw.Position = 0;
                raw.CopyTo(gz);
            }
            return dst.ToArray();
        }

        // ------------------------------------------------------------------
        // Round-trip: header, portals, map tables, clusters, zones, event
        // ------------------------------------------------------------------

        [Fact]
        public void FullV37RoundTrip()
        {
            var zdos = new List<ZdoSpec>
            {
                // A tagged wooden portal (rotation + connection like the real thing).
                new ZdoSpec
                {
                    Prefab = Hash("portal_wood"), X = 120f, Y = 32f, Z = -80f,
                    Rotation = true, Connection = true,
                    Strings = { (Hash("tag"), "home") },
                },
                // A cartography table with a real gzip shared-map payload.
                new ZdoSpec
                {
                    Prefab = Hash("piece_cartographytable"), X = 5f, Y = 30f, Z = 6f,
                    Longs = { (Hash("creator"), 42L) },
                    ByteArrays = { (Hash("data"), GzipSharedMap(3, 4, new bool[16])) },
                },
                // A plain no-data ZDO (record ends right after prefab).
                new ZdoSpec { Prefab = Hash("Boar"), X = 500f, Y = 40f, Z = 500f },
            };

            // Ten creator-stamped pieces bunched together -> one build cluster.
            for (int i = 0; i < 10; i++)
            {
                zdos.Add(new ZdoSpec
                {
                    Prefab = Hash("wood_wall"), X = 1000f + i * 2f, Y = 35f, Z = 2000f + i,
                    Rotation = true,
                    Longs = { (Hash("creator"), 1234567L) },
                });
            }

            var db = BuildDb(37, 1800.0 * 123 + 900, zdos,
                globalKeys: new List<string> { "defeated_eikthyr", "defeated_gdking" },
                locations: new List<(string, float, float, float, bool)>
                {
                    ("StartTemple", 10f, 35f, -20f, true),
                    ("Eikthyrnir", 300f, 40f, 400f, false),
                },
                eventName: "army_theelder");

            var info = ParseBytes(db);

            Assert.NotNull(info);
            Assert.Equal(37, info.WorldVersion);
            Assert.Equal(123, info.DayNumber);
            Assert.Equal(zdos.Count, info.ZdoCount);
            Assert.Equal(2, info.GeneratedZoneCount);
            Assert.True(info.LocationsGenerated);
            Assert.Equal(new[] { "defeated_eikthyr", "defeated_gdking" }, info.GlobalKeys);

            // Portal with its tag.
            var portal = Assert.Single(info.Portals);
            Assert.Equal("home", portal.Tag);
            Assert.Equal(120f, portal.X);
            Assert.Equal(-80f, portal.Z);

            // Cartography table captured (and NOT double-counted as a build piece).
            var table = Assert.Single(info.MapTables);
            Assert.NotNull(table.Data);
            Assert.NotNull(SharedMapData.TryDecode(table.Data));

            // One cluster of exactly the 10 stamped pieces.
            var cluster = Assert.Single(info.BuildClusters);
            Assert.Equal(10, cluster.PieceCount);
            Assert.InRange(cluster.CenterX, 1000f, 1020f);
            Assert.InRange(cluster.CenterZ, 2000f, 2010f);
            Assert.InRange(cluster.RadiusMeters, 1f, 32f);

            // Locations + event.
            Assert.Contains(info.Locations, l => l.Prefab == "StartTemple" && l.Placed);
            Assert.Contains(info.Locations, l => l.Prefab == "Eikthyrnir" && !l.Placed);
            Assert.Equal("army_theelder", info.EventName);
            Assert.Equal(11f, info.EventPosX);
            Assert.Equal(-22f, info.EventPosZ);
        }

        [Fact]
        public void FewPiecesDoNotFormACluster()
        {
            var zdos = new List<ZdoSpec>();
            for (int i = 0; i < 7; i++) // below the 8-piece threshold
            {
                zdos.Add(new ZdoSpec
                {
                    Prefab = Hash("wood_wall"), X = i * 2f, Y = 35f, Z = 0f,
                    Longs = { (Hash("creator"), 99L) },
                });
            }
            var info = ParseBytes(BuildDb(37, 3600, zdos));
            Assert.NotNull(info);
            Assert.Empty(info.BuildClusters);
        }

        [Fact]
        public void NumItemsAbove127UsesTwoByteEncoding()
        {
            // A ZDO carrying 200 ints exercises the 2-byte NumItems path;
            // a portal AFTER it proves the stream stayed aligned.
            var big = new ZdoSpec { Prefab = Hash("Player_tombstone"), X = 1f, Y = 2f, Z = 3f };
            for (int i = 0; i < 200; i++) big.Ints.Add((i + 1, i));

            var zdos = new List<ZdoSpec>
            {
                big,
                new ZdoSpec
                {
                    Prefab = Hash("portal_stone"), X = 9f, Y = 8f, Z = 7f,
                    Strings = { (Hash("tag"), "after-big") },
                },
            };

            var info = ParseBytes(BuildDb(37, 1800, zdos));
            Assert.NotNull(info);
            var portal = Assert.Single(info.Portals);
            Assert.Equal("after-big", portal.Tag);
        }

        [Fact]
        public void Version32UsesSingleByteCounts()
        {
            // v31/32: typed-block counts are a single raw byte.
            byte[] db;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(32);           // version
                bw.Write(1800.0);       // net time
                bw.Write(1L);           // session
                bw.Write((uint)2);      // next uid
                bw.Write(1);            // zdo count

                // Portal ZDO, v32 style: raw-byte string count.
                bw.Write((ushort)(0x100 | 0x0040));
                bw.Write((short)0); bw.Write((short)0);
                bw.Write(1f); bw.Write(2f); bw.Write(3f);
                bw.Write(Hash("portal_wood"));
                bw.Write((byte)1);      // ONE string, raw byte
                bw.Write(Hash("tag"));
                bw.Write("v32");

                // ZoneSystem (v32 gates: zones, pgw, locVer, keys, locGen, locations)
                bw.Write(0);            // zones
                bw.Write(0);            // pgw
                bw.Write(26);           // location version
                bw.Write(0);            // global keys
                bw.Write(true);         // locations generated
                bw.Write(0);            // locations

                // RandEventSystem
                bw.Write(0f);
                bw.Write("");
                bw.Write(0f);
                bw.Write(0f); bw.Write(0f); bw.Write(0f);
                bw.Flush();
                db = ms.ToArray();
            }

            var info = ParseBytes(db);
            Assert.NotNull(info);
            var portal = Assert.Single(info.Portals);
            Assert.Equal("v32", portal.Tag);
        }

        [Fact]
        public void LegacyPre31DegradesGracefully()
        {
            byte[] db;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(29);           // version < 31
                bw.Write(5400.0);       // net time (day 3)
                bw.Write(1L);           // session
                bw.Write((uint)5);      // next uid
                bw.Write(2);            // zdo count

                // Legacy records: ZDOID (12 bytes) + length-prefixed blob.
                for (int i = 0; i < 2; i++)
                {
                    bw.Write((long)(100 + i)); bw.Write((uint)i);
                    byte[] blob = { 1, 2, 3, 4, 5 };
                    bw.Write(blob.Length);
                    bw.Write(blob);
                }
                bw.Write(1);            // dead zdo count
                bw.Write(7L); bw.Write((uint)8); bw.Write(9L);

                // ZoneSystem (v29: zones, pgw, locVer, keys, locGen, locations)
                bw.Write(1);
                bw.Write(3); bw.Write(-3);
                bw.Write(0);            // pgw
                bw.Write(26);           // location version
                bw.Write(1);            // global keys
                bw.Write("defeated_bonemass");
                bw.Write(true);
                bw.Write(1);
                bw.Write("StartTemple");
                bw.Write(1f); bw.Write(2f); bw.Write(3f);
                bw.Write(true);

                // RandEventSystem
                bw.Write(0f);
                bw.Write("");
                bw.Write(0f);
                bw.Write(0f); bw.Write(0f); bw.Write(0f);
                bw.Flush();
                db = ms.ToArray();
            }

            var info = ParseBytes(db);
            Assert.NotNull(info);
            Assert.Equal(29, info.WorldVersion);
            Assert.Equal(3, info.DayNumber);
            Assert.Empty(info.Portals);     // ZDO details unavailable pre-31
            Assert.Single(info.GlobalKeys);
            Assert.Single(info.Locations);
        }

        // ------------------------------------------------------------------
        // Failure modes
        // ------------------------------------------------------------------

        [Fact]
        public void MissingFileReturnsNull()
        {
            Assert.Null(WorldDbReader.TryRead(Path.Combine(_tempDir, "nope.db")));
        }

        [Fact]
        public void CorruptOrTruncatedReturnsNull()
        {
            Assert.Null(ParseBytes(new byte[] { 1, 2, 3 }));

            // Plausible header, garbage body.
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(37);
            bw.Write(1800.0);
            bw.Write(1L);
            bw.Write((uint)1);
            bw.Write(1000); // claims 1000 zdos, then EOF
            bw.Flush();
            Assert.Null(ParseBytes(ms.ToArray()));
        }

        [Fact]
        public void InsaneVersionReturnsNull()
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(9999);
            bw.Write(new byte[64]);
            bw.Flush();
            Assert.Null(ParseBytes(ms.ToArray()));
        }

        // ------------------------------------------------------------------
        // SharedMapData
        // ------------------------------------------------------------------

        [Fact]
        public void SharedMap_DecodesExploredAndPins()
        {
            var explored = new bool[16];
            explored[0] = explored[5] = explored[15] = true;
            var pins = new List<SharedMapPin>
            {
                new SharedMapPin { OwnerId = 42, Name = "Trader", X = 1f, Y = 2f, Z = 3f, Type = 4, Checked = true, PlatformUserId = "Steam_123" },
            };

            var map = SharedMapData.TryDecode(GzipSharedMap(3, 4, explored, pins));

            Assert.NotNull(map);
            Assert.Equal(3, map.Version);
            Assert.Equal(4, map.TextureSize);
            Assert.True(map.Explored[0]);
            Assert.True(map.Explored[5]);
            Assert.True(map.Explored[15]);
            Assert.False(map.Explored[1]);
            var pin = Assert.Single(map.Pins);
            Assert.Equal("Trader", pin.Name);
            Assert.Equal(42, pin.OwnerId);
            Assert.True(pin.Checked);
            Assert.Equal("Steam_123", pin.PlatformUserId);
        }

        [Fact]
        public void SharedMap_MergeOrsBitmaps()
        {
            var a = SharedMapData.TryDecode(GzipSharedMap(3, 4, MakeExplored(16, 0, 1)));
            var b = SharedMapData.TryDecode(GzipSharedMap(3, 4, MakeExplored(16, 1, 2)));

            Assert.True(a.MergeFrom(b));
            Assert.True(a.Explored[0]);
            Assert.True(a.Explored[1]);
            Assert.True(a.Explored[2]);
            Assert.False(a.Explored[3]);

            // Mismatched sizes refuse to merge.
            var c = SharedMapData.TryDecode(GzipSharedMap(3, 2, new bool[4]));
            Assert.False(a.MergeFrom(c));
        }

        [Fact]
        public void SharedMap_GarbageReturnsNull()
        {
            Assert.Null(SharedMapData.TryDecode(null));
            Assert.Null(SharedMapData.TryDecode(new byte[] { 1, 2, 3, 4, 5 }));
            // Valid gzip, non-square explored count.
            byte[] bad;
            using (var raw = new MemoryStream())
            using (var bw = new BinaryWriter(raw))
            {
                bw.Write(3);
                bw.Write(15); // not a perfect square
                bw.Write(new byte[15]);
                bw.Flush();
                using var dst = new MemoryStream();
                using (var gz = new GZipStream(dst, CompressionMode.Compress, leaveOpen: true))
                {
                    raw.Position = 0;
                    raw.CopyTo(gz);
                }
                bad = dst.ToArray();
            }
            Assert.Null(SharedMapData.TryDecode(bad));
        }

        private static bool[] MakeExplored(int count, params int[] setBits)
        {
            var arr = new bool[count];
            foreach (int i in setBits) arr[i] = true;
            return arr;
        }

        // ------------------------------------------------------------------
        // Live world (skips when the file isn't on this machine)
        // ------------------------------------------------------------------

        [Fact]
        public void LiveWorld_ParsesWhenPresent()
        {
            string db = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "IronGate", "Valheim", "worlds_local", "Final Sunset.db");
            if (!File.Exists(db)) return; // machine without the live world

            var info = WorldDbReader.TryRead(db);
            Assert.NotNull(info);
            Assert.InRange(info.WorldVersion, 31, 60);
            Assert.True(info.DayNumber > 0, "expected a lived-in world");
            Assert.True(info.ZdoCount > 10_000, $"zdo count: {info.ZdoCount}");
            Assert.Contains(info.Locations, l => l.Prefab == "StartTemple");
            Assert.True(info.GeneratedZoneCount > 0);
        }
    }
}
