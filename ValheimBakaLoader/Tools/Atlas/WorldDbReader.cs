using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace ValheimBakaLoader.Tools.Atlas
{
    /// <summary>
    /// Parsed summary of a Valheim world .db save (read-only, mod-free).
    /// Byte layout verified against the dedicated server assembly (world
    /// version 37; ZNet.LoadWorld / ZDOMan.Load / ZDO.Load / ZoneSystem.Load /
    /// RandEventSystem.Load). Layout is unchanged since version 33; versions
    /// 31-32 differ only in the typed-block count byte; pre-31 files degrade
    /// gracefully (header + zone/location data only, ZDO records skipped).
    /// </summary>
    public sealed class WorldDbInfo
    {
        public int WorldVersion;

        /// <summary>World clock in seconds. One in-game day = 1800 s.</summary>
        public double NetTime;

        /// <summary>Completed in-game days (netTime / 1800).</summary>
        public int DayNumber => (int)(NetTime / 1800.0);

        public int ZdoCount;
        public int GeneratedZoneCount;
        public bool LocationsGenerated;

        public readonly List<string> GlobalKeys = new List<string>();

        /// <summary>Placed points of interest (boss altars, spawn temple, dungeons, ...).</summary>
        public readonly List<DbLocation> Locations = new List<DbLocation>();

        /// <summary>Player-built portals with their connection tags.</summary>
        public readonly List<DbPortal> Portals = new List<DbPortal>();

        /// <summary>Cartography tables and their raw (gzip-compressed) shared-map payloads.</summary>
        public readonly List<DbMapTable> MapTables = new List<DbMapTable>();

        /// <summary>Player build sites (clusters of creator-stamped pieces).</summary>
        public readonly List<DbBuildCluster> BuildClusters = new List<DbBuildCluster>();

        /// <summary>Active random event name ("" when none).</summary>
        public string EventName = "";
        public float EventPosX;
        public float EventPosZ;
    }

    public sealed class DbLocation
    {
        public string Prefab;
        public float X, Y, Z;
        public bool Placed;
    }

    public sealed class DbPortal
    {
        public string Tag = "";
        public float X, Y, Z;
    }

    public sealed class DbMapTable
    {
        public float X, Y, Z;

        /// <summary>Raw ZDO "data" byte array (gzip). Decode with <see cref="SharedMapData.TryDecode"/>.</summary>
        public byte[] Data;
    }

    public sealed class DbBuildCluster
    {
        public float CenterX, CenterZ;
        public int PieceCount;
        public float RadiusMeters;
    }

    /// <summary>
    /// Decoded cartography-table shared map (MapTable.GetMapData →
    /// Utils.Compress(gzip) over a ZPackage: int version(=3), int count,
    /// count×bool explored, [v≥2] int pinCount × pins).
    /// This is the combined explored area written by everyone who has used
    /// the table - the only mod-free server-side source for fog of war.
    /// </summary>
    public sealed class SharedMapData
    {
        public int Version;

        /// <summary>Explored bitmap is TextureSize × TextureSize (vanilla 2048).</summary>
        public int TextureSize;

        /// <summary>Row-major explored flags; index = py * TextureSize + px. World→pixel: px = round(wx / PixelSize + TextureSize/2).</summary>
        public bool[] Explored;

        /// <summary>Meters per minimap pixel (vanilla prefab value).</summary>
        public const float VanillaPixelSize = 12f;

        public readonly List<SharedMapPin> Pins = new List<SharedMapPin>();

        public static SharedMapData TryDecode(byte[] compressed)
        {
            if (compressed == null || compressed.Length < 4) return null;
            try
            {
                byte[] raw = Decompress(compressed);
                using (var ms = new MemoryStream(raw))
                using (var br = new BinaryReader(ms))
                {
                    var map = new SharedMapData();
                    map.Version = br.ReadInt32();
                    if (map.Version < 1 || map.Version > 100) return null;

                    int count = br.ReadInt32();
                    if (count <= 0 || count > 64 * 1024 * 1024) return null;
                    int size = (int)Math.Round(Math.Sqrt(count));
                    if (size * size != count) return null;

                    byte[] bits = br.ReadBytes(count);
                    if (bits.Length != count) return null;
                    map.TextureSize = size;
                    map.Explored = new bool[count];
                    for (int i = 0; i < count; i++)
                    {
                        map.Explored[i] = bits[i] != 0;
                    }

                    if (map.Version >= 2)
                    {
                        int pins = br.ReadInt32();
                        if (pins < 0 || pins > 100000) return null;
                        for (int i = 0; i < pins; i++)
                        {
                            var pin = new SharedMapPin();
                            pin.OwnerId = br.ReadInt64();
                            pin.Name = br.ReadString();
                            pin.X = br.ReadSingle();
                            pin.Y = br.ReadSingle();
                            pin.Z = br.ReadSingle();
                            pin.Type = br.ReadInt32();
                            pin.Checked = br.ReadBoolean();
                            if (map.Version >= 3)
                            {
                                pin.PlatformUserId = br.ReadString();
                            }
                            map.Pins.Add(pin);
                        }
                    }

                    return map;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>OR another table's explored bitmap into this one (sizes must match).</summary>
        public bool MergeFrom(SharedMapData other)
        {
            if (other?.Explored == null || Explored == null || other.TextureSize != TextureSize)
            {
                return false;
            }
            for (int i = 0; i < Explored.Length; i++)
            {
                Explored[i] |= other.Explored[i];
            }
            return true;
        }

        private static byte[] Decompress(byte[] input)
        {
            using (var src = new MemoryStream(input))
            using (var gz = new GZipStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                gz.CopyTo(dst);
                return dst.ToArray();
            }
        }
    }

    public sealed class SharedMapPin
    {
        public long OwnerId;
        public string Name = "";
        public float X, Y, Z;
        public int Type;
        public bool Checked;
        public string PlatformUserId = "";
    }

    /// <summary>
    /// Read-only parser for Valheim world .db saves. Opens with
    /// FileShare.ReadWrite (the server may be mid-save) and retries once on
    /// the atomic .db.new→.db swap window. Never writes anything.
    /// </summary>
    public static class WorldDbReader
    {
        // ZDO key/prefab hashes (game's StableHashCode, same as FwlWriter's).
        private static readonly int HashTag = FwlWriter.GetStableHashCode("tag");
        private static readonly int HashCreator = FwlWriter.GetStableHashCode("creator");
        private static readonly int HashData = FwlWriter.GetStableHashCode("data");
        private static readonly int HashCartographyTable = FwlWriter.GetStableHashCode("piece_cartographytable");

        private static readonly HashSet<int> PortalPrefabHashes = new HashSet<int>
        {
            FwlWriter.GetStableHashCode("portal_wood"),
            FwlWriter.GetStableHashCode("portal_stone"),
            FwlWriter.GetStableHashCode("portal"),
        };

        /// <summary>Build-cluster grid cell size in meters.</summary>
        private const float ClusterCellSize = 32f;

        /// <summary>Minimum creator-stamped pieces for a cluster to count as a build site.</summary>
        private const int ClusterMinPieces = 8;

        /// <summary>Locates the .db beside a world's .fwl and parses it. Null when absent or unreadable.</summary>
        public static WorldDbInfo TryReadWorld(string saveDataFolder, string worldName)
        {
            var fwl = FwlReader.FindWorldFwl(saveDataFolder, worldName);
            if (fwl == null) return null;
            return TryRead(Path.ChangeExtension(fwl, ".db"));
        }

        /// <summary>Parses a world .db. Returns null on any failure (missing, mid-swap, corrupt, unsupported).</summary>
        public static WorldDbInfo TryRead(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath)) return null;

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var br = new BinaryReader(fs))
                    {
                        return Read(br);
                    }
                }
                catch (FileNotFoundException) when (attempt == 0)
                {
                    // Atomic save swap: .db briefly absent while .db.new is moved in.
                    Thread.Sleep(250);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        private static WorldDbInfo Read(BinaryReader br)
        {
            var info = new WorldDbInfo();

            info.WorldVersion = br.ReadInt32();
            if (info.WorldVersion < 9 || info.WorldVersion > 200) return null;

            if (info.WorldVersion >= 4)
            {
                info.NetTime = br.ReadDouble();
                if (double.IsNaN(info.NetTime) || info.NetTime < 0 || info.NetTime > 1e12) return null;
            }

            // ---- ZDOMan section ----
            br.ReadInt64(); // session id (ignored on load)
            br.ReadUInt32(); // next uid (recomputed on load for v>=31)
            info.ZdoCount = br.ReadInt32();
            if (info.ZdoCount < 0 || info.ZdoCount > 50_000_000) return null;

            var buildPieces = new List<(float X, float Z)>();

            if (info.WorldVersion >= 31)
            {
                for (int i = 0; i < info.ZdoCount; i++)
                {
                    ReadModernZdo(br, info.WorldVersion, info, buildPieces);
                }
            }
            else
            {
                // Legacy format: length-prefixed records we can hop over, then a dead-ZDO list.
                for (int i = 0; i < info.ZdoCount; i++)
                {
                    br.BaseStream.Seek(12, SeekOrigin.Current); // ZDOID (int64 user + uint32 id)
                    int len = br.ReadInt32();
                    if (len < 0 || len > 10_000_000) return null;
                    br.BaseStream.Seek(len, SeekOrigin.Current);
                }
                int dead = br.ReadInt32();
                if (dead < 0 || dead > 50_000_000) return null;
                br.BaseStream.Seek((long)dead * 20, SeekOrigin.Current);
            }

            // ---- ZoneSystem section ----
            if (info.WorldVersion >= 12)
            {
                info.GeneratedZoneCount = br.ReadInt32();
                if (info.GeneratedZoneCount < 0 || info.GeneratedZoneCount > 10_000_000) return null;
                br.BaseStream.Seek((long)info.GeneratedZoneCount * 8, SeekOrigin.Current);

                if (info.WorldVersion >= 13)
                {
                    br.ReadInt32(); // pgw version (written as 0, discarded on load)
                    if (info.WorldVersion >= 21)
                    {
                        br.ReadInt32(); // location version
                    }
                    if (info.WorldVersion >= 14)
                    {
                        int keys = br.ReadInt32();
                        if (keys < 0 || keys > 100_000) return null;
                        for (int i = 0; i < keys; i++)
                        {
                            info.GlobalKeys.Add(br.ReadString());
                        }
                    }
                    if (info.WorldVersion >= 18)
                    {
                        if (info.WorldVersion >= 20)
                        {
                            info.LocationsGenerated = br.ReadBoolean();
                        }
                        int locations = br.ReadInt32();
                        if (locations < 0 || locations > 10_000_000) return null;
                        for (int i = 0; i < locations; i++)
                        {
                            var loc = new DbLocation();
                            loc.Prefab = br.ReadString();
                            loc.X = br.ReadSingle();
                            loc.Y = br.ReadSingle();
                            loc.Z = br.ReadSingle();
                            if (info.WorldVersion >= 19)
                            {
                                loc.Placed = br.ReadBoolean();
                            }
                            info.Locations.Add(loc);
                        }
                    }
                }
            }

            // ---- RandEventSystem section ----
            if (info.WorldVersion >= 15)
            {
                br.ReadSingle(); // event timer
                if (info.WorldVersion >= 25)
                {
                    info.EventName = br.ReadString();
                    br.ReadSingle(); // event time
                    info.EventPosX = br.ReadSingle();
                    br.ReadSingle(); // event pos y
                    info.EventPosZ = br.ReadSingle();
                }
            }

            ClusterBuilds(buildPieces, info.BuildClusters);
            return info;
        }

        /// <summary>
        /// Walks one v≥31 ZDO record, extracting portals (prefab + "tag"
        /// string), creator-stamped build pieces ("creator" long ≠ 0) and
        /// cartography-table payloads ("data" byte array).
        /// </summary>
        private static void ReadModernZdo(BinaryReader br, int version, WorldDbInfo info, List<(float X, float Z)> buildPieces)
        {
            ushort flags = br.ReadUInt16();
            br.BaseStream.Seek(4, SeekOrigin.Current); // sector (2 × int16)
            float posX = br.ReadSingle();
            float posY = br.ReadSingle();
            float posZ = br.ReadSingle();
            int prefab = br.ReadInt32();

            if ((flags & 0x1000) != 0)
            {
                br.BaseStream.Seek(12, SeekOrigin.Current); // euler rotation (3 × float)
            }
            if ((flags & 0xFF) == 0)
            {
                return;
            }

            bool isPortal = PortalPrefabHashes.Contains(prefab);
            bool isMapTable = prefab == HashCartographyTable;

            if ((flags & 0x0001) != 0)
            {
                br.ReadByte();  // connection type
                br.ReadInt32(); // connection target hash
            }

            if ((flags & 0x0002) != 0) // floats
            {
                int n = ReadNumItems(br, version);
                br.BaseStream.Seek((long)n * 8, SeekOrigin.Current);
            }
            if ((flags & 0x0004) != 0) // Vector3s
            {
                int n = ReadNumItems(br, version);
                br.BaseStream.Seek((long)n * 16, SeekOrigin.Current);
            }
            if ((flags & 0x0008) != 0) // quaternions
            {
                int n = ReadNumItems(br, version);
                br.BaseStream.Seek((long)n * 20, SeekOrigin.Current);
            }
            if ((flags & 0x0010) != 0) // ints
            {
                int n = ReadNumItems(br, version);
                br.BaseStream.Seek((long)n * 8, SeekOrigin.Current);
            }

            bool isBuildPiece = false;
            if ((flags & 0x0020) != 0) // longs
            {
                int n = ReadNumItems(br, version);
                for (int i = 0; i < n; i++)
                {
                    int key = br.ReadInt32();
                    long value = br.ReadInt64();
                    if (key == HashCreator && value != 0)
                    {
                        isBuildPiece = true;
                    }
                }
            }

            string portalTag = null;
            if ((flags & 0x0040) != 0) // strings
            {
                int n = ReadNumItems(br, version);
                for (int i = 0; i < n; i++)
                {
                    int key = br.ReadInt32();
                    if (isPortal && key == HashTag)
                    {
                        portalTag = br.ReadString();
                    }
                    else
                    {
                        SkipString(br);
                    }
                }
            }

            byte[] mapData = null;
            if ((flags & 0x0080) != 0) // byte arrays
            {
                int n = ReadNumItems(br, version);
                for (int i = 0; i < n; i++)
                {
                    int key = br.ReadInt32();
                    int len = br.ReadInt32();
                    if (len < 0 || len > 128 * 1024 * 1024)
                    {
                        throw new InvalidDataException("byte array length out of range");
                    }
                    if (isMapTable && key == HashData)
                    {
                        mapData = br.ReadBytes(len);
                    }
                    else
                    {
                        br.BaseStream.Seek(len, SeekOrigin.Current);
                    }
                }
            }

            if (isPortal)
            {
                info.Portals.Add(new DbPortal { Tag = portalTag ?? "", X = posX, Y = posY, Z = posZ });
            }
            else if (isMapTable)
            {
                info.MapTables.Add(new DbMapTable { X = posX, Y = posY, Z = posZ, Data = mapData });
            }
            else if (isBuildPiece)
            {
                buildPieces.Add((posX, posZ));
            }
        }

        /// <summary>ZPackage.ReadNumItems (v≥33) / raw byte (v31-32).</summary>
        private static int ReadNumItems(BinaryReader br, int version)
        {
            if (version < 33)
            {
                return br.ReadByte();
            }
            int b0 = br.ReadByte();
            if ((b0 & 0x80) != 0)
            {
                return ((b0 & 0x7F) << 8) | br.ReadByte();
            }
            return b0;
        }

        /// <summary>Skips a BinaryWriter string (7-bit varint length + UTF-8 bytes).</summary>
        private static void SkipString(BinaryReader br)
        {
            int length = 0;
            int shift = 0;
            byte b;
            do
            {
                if (shift > 28) throw new InvalidDataException("string length varint too long");
                b = br.ReadByte();
                length |= (b & 0x7F) << shift;
                shift += 7;
            }
            while ((b & 0x80) != 0);
            if (length < 0 || length > 64 * 1024 * 1024) throw new InvalidDataException("string length out of range");
            br.BaseStream.Seek(length, SeekOrigin.Current);
        }

        /// <summary>
        /// Groups creator-stamped pieces into build sites: 32 m grid cells,
        /// 8-neighbor flood fill, clusters of ≥8 pieces reported with a
        /// piece-weighted center and covering radius.
        /// </summary>
        private static void ClusterBuilds(List<(float X, float Z)> pieces, List<DbBuildCluster> output)
        {
            if (pieces.Count == 0) return;

            var cells = new Dictionary<(int cx, int cz), List<int>>();
            for (int i = 0; i < pieces.Count; i++)
            {
                var key = ((int)Math.Floor(pieces[i].X / ClusterCellSize), (int)Math.Floor(pieces[i].Z / ClusterCellSize));
                if (!cells.TryGetValue(key, out var list))
                {
                    cells[key] = list = new List<int>();
                }
                list.Add(i);
            }

            var visited = new HashSet<(int, int)>();
            var stack = new Stack<(int cx, int cz)>();

            foreach (var seed in cells.Keys)
            {
                if (visited.Contains(seed)) continue;

                var members = new List<int>();
                stack.Push(seed);
                visited.Add(seed);
                while (stack.Count > 0)
                {
                    var (cx, cz) = stack.Pop();
                    members.AddRange(cells[(cx, cz)]);
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            var next = (cx + dx, cz + dz);
                            if (cells.ContainsKey(next) && visited.Add(next))
                            {
                                stack.Push(next);
                            }
                        }
                    }
                }

                if (members.Count < ClusterMinPieces) continue;

                double sumX = 0, sumZ = 0;
                foreach (int i in members)
                {
                    sumX += pieces[i].X;
                    sumZ += pieces[i].Z;
                }
                float centerX = (float)(sumX / members.Count);
                float centerZ = (float)(sumZ / members.Count);

                double maxDistSq = 0;
                foreach (int i in members)
                {
                    double dx = pieces[i].X - centerX;
                    double dz = pieces[i].Z - centerZ;
                    double d = dx * dx + dz * dz;
                    if (d > maxDistSq) maxDistSq = d;
                }

                output.Add(new DbBuildCluster
                {
                    CenterX = centerX,
                    CenterZ = centerZ,
                    PieceCount = members.Count,
                    RadiusMeters = (float)Math.Sqrt(maxDistSq),
                });
            }

            output.Sort((a, b) => b.PieceCount.CompareTo(a.PieceCount));
        }
    }
}
