using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading;

namespace ValheimBakaLoader.Tools.Atlas
{
    /// <summary>
    /// Read-only parser for a Valheim 1.0 chunked world save (world version
    /// 40 and up), where a world is a DIRECTORY instead of a single .db file.
    ///
    /// A committed generation N is the set
    /// <c>_main.N.fwl2</c> + <c>_main.N.db2</c> + <c>_main.N.chunks</c> +
    /// <c>_main.N.ok</c>. The game writes the .ok marker LAST and only then
    /// deletes generation N-1 (ZNet.SaveWorldThread, decomp_new :80502-80531),
    /// so a generation without its .ok is either mid-write or a failed save and
    /// must be ignored. Reader picks the highest committed N.
    ///
    /// File layouts, all verified against real 1.0 saves with zero trailing
    /// bytes:
    ///   _main.N.db2    int32 version, double netTime, int32 gzipLength,
    ///                  GZIP(ZoneSystem block), RandEventSystem,
    ///                  int32 length + brotli(PersistentEventSystem JSON).
    ///   ZoneSystem     int32 zoneCount, Vector2s[] (2 x int16 each),
    ///                  int32 locationVersion, int32 keyCount + strings,
    ///                  bool locationsGenerated, int32 locationCount and then
    ///                  {int32 prefabHash, 3 x float, bool placed} records.
    ///                  The prefab is a hash now, and the placeholder int the
    ///                  old format wrote is gone (decomp_new :112978).
    ///   _main.N.chunks uint16 version, int32 totalZDOs, int32 chunkCount and
    ///                  then {uint16 chunk, byte size, uint32 version,
    ///                  int32 zdoCount} records. This index is the ONLY
    ///                  authority for which .chunk files are live: only dirty
    ///                  chunks get rewritten, so stale files with older version
    ///                  numbers can sit next to the live ones.
    ///   .chunk         int16 version, int32 zdoCount, then ZDO records in the
    ///                  version 40 layout (no sector field, optional small
    ///                  position, packed rotation).
    ///
    /// Portals are written to their own chunk, ChunkIndex(1, 0) => file
    /// <c>00_01__0_&lt;version&gt;.chunk</c>, and are excluded from the regular
    /// chunks (decomp_new :77466, :112421), so everything in that one file is a
    /// portal whatever its prefab is called.
    /// </summary>
    internal static class ChunkedWorldReader
    {
        /// <summary>ZoneSystem.ChunkPortal is ChunkIndex(1, 0) (decomp_new :112421).</summary>
        private const int PortalChunkIndex = 1;
        private const int PortalChunkSize = 0;

        private static readonly Regex GenerationPattern =
            new Regex(@"^_main\.(\d+)\.fwl2$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses the highest committed generation of a chunked world folder.
        /// Returns null (with a reason handed to <paramref name="report"/>) when
        /// the folder holds no committed generation or the save is unreadable.
        /// </summary>
        internal static WorldDbInfo TryRead(string worldDirectory, Action<string> report)
        {
            if (string.IsNullOrWhiteSpace(worldDirectory)) return null;

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!Directory.Exists(worldDirectory))
                    {
                        report?.Invoke($"chunked world folder not found: {worldDirectory}");
                        return null;
                    }

                    int generation = FindCommittedGeneration(worldDirectory, report);
                    if (generation < 0) return null;

                    return ReadGeneration(worldDirectory, generation, report, canRescan: attempt == 0);
                }
                catch (IOException) when (attempt == 0)
                {
                    // The commit of the next generation deletes this one's files.
                    // Re-scan once and read whichever generation is committed now.
                    Thread.Sleep(250);
                }
                catch (Exception ex)
                {
                    report?.Invoke($"chunked world read failed ({ex.GetType().Name}): {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>Highest N whose .fwl2/.db2/.chunks/.ok all exist, or -1.</summary>
        private static int FindCommittedGeneration(string worldDirectory, Action<string> report)
        {
            var generations = new List<int>();
            foreach (string path in Directory.GetFiles(worldDirectory, "_main.*.fwl2"))
            {
                Match match = GenerationPattern.Match(Path.GetFileName(path));
                if (!match.Success) continue;
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                {
                    generations.Add(n);
                }
            }

            generations.Sort();
            for (int i = generations.Count - 1; i >= 0; i--)
            {
                int n = generations[i];
                if (File.Exists(GenerationPath(worldDirectory, n, ".db2"))
                    && File.Exists(GenerationPath(worldDirectory, n, ".chunks"))
                    && File.Exists(GenerationPath(worldDirectory, n, ".ok")))
                {
                    return n;
                }
            }

            report?.Invoke(generations.Count == 0
                ? $"no _main.N.fwl2 in {worldDirectory} - not a chunked world folder"
                : $"no committed generation in {worldDirectory} (newest save is missing its .ok marker)");
            return -1;
        }

        private static string GenerationPath(string worldDirectory, int generation, string extension)
        {
            return Path.Combine(
                worldDirectory,
                "_main." + generation.ToString(CultureInfo.InvariantCulture) + extension);
        }

        /// <param name="canRescan">
        /// True while the caller still has a re-scan in hand. A chunk file that is missing or
        /// busy then means "a save just landed", so the failure travels up and the newly
        /// committed generation is read instead. False on the last pass, where the same failure
        /// is a genuinely unreadable chunk and is counted rather than thrown.
        /// </param>
        private static WorldDbInfo ReadGeneration(string worldDirectory, int generation, Action<string> report, bool canRescan)
        {
            var info = new WorldDbInfo();
            if (!ReadDb2(GenerationPath(worldDirectory, generation, ".db2"), info, report))
            {
                return null;
            }

            var chunks = ReadChunkIndex(GenerationPath(worldDirectory, generation, ".chunks"), report);
            if (chunks == null)
            {
                return null;
            }

            var buildPieces = new List<(float X, float Z)>();
            int zdoCount = 0;
            info.ChunksTotal = chunks.Count;

            foreach (ChunkRecord chunk in chunks)
            {
                zdoCount += chunk.ZdoCount;
                string chunkPath = Path.Combine(worldDirectory, chunk.FileName);

                try
                {
                    if (!File.Exists(chunkPath))
                    {
                        throw new FileNotFoundException("chunk file is not there", chunkPath);
                    }

                    ReadChunkFile(chunkPath, chunk.IsPortalChunk, info, buildPieces);
                }
                catch (IOException) when (canRescan)
                {
                    // A chunk file that is missing or busy is what a save landing mid-read looks
                    // like: committing the next generation deletes this one's files. Letting it
                    // out is what makes the caller re-scan and read the generation that is
                    // committed NOW, rather than quietly returning most of a world.
                    throw;
                }
                catch (Exception ex)
                {
                    // Past the re-scan this is a real unreadable chunk. The rest of the world is
                    // still worth drawing, as long as the map says a piece of it is missing.
                    info.ChunksSkipped++;
                    report?.Invoke($"chunk {chunk.FileName} could not be read ({ex.GetType().Name}: {ex.Message}), skipping");
                }
            }

            info.ZdoCount = zdoCount;
            WorldDbReader.ClusterBuilds(buildPieces, info.BuildClusters);
            return info;
        }

        // ------------------------------------------------------------------
        // _main.N.db2
        // ------------------------------------------------------------------

        private static bool ReadDb2(string path, WorldDbInfo info, Action<string> report)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            info.WorldVersion = br.ReadInt32();
            if (info.WorldVersion < WorldDbReader.ChunkedSaveVersion || info.WorldVersion > WorldDbReader.MaxWorldVersion)
            {
                report?.Invoke($"{Path.GetFileName(path)}: world version {info.WorldVersion} is outside the supported chunked range "
                    + $"{WorldDbReader.ChunkedSaveVersion}-{WorldDbReader.MaxWorldVersion}");
                return false;
            }

            info.NetTime = br.ReadDouble();
            if (double.IsNaN(info.NetTime) || info.NetTime < 0 || info.NetTime > 1e12)
            {
                report?.Invoke($"{Path.GetFileName(path)}: net time {info.NetTime} is out of range");
                return false;
            }

            int compressedLength = br.ReadInt32();
            if (compressedLength < 0 || compressedLength > 512 * 1024 * 1024)
            {
                report?.Invoke($"{Path.GetFileName(path)}: zone block length {compressedLength} is out of range");
                return false;
            }

            byte[] compressed = br.ReadBytes(compressedLength);
            if (compressed.Length != compressedLength)
            {
                report?.Invoke($"{Path.GetFileName(path)}: zone block is truncated");
                return false;
            }

            if (!ReadZoneSystem(Decompress(compressed), info, report))
            {
                return false;
            }

            // RandEventSystem. The brotli PersistentEventSystem blob that
            // follows carries nothing the map needs, so it is left alone.
            br.ReadSingle();                   // event timer
            info.EventName = br.ReadString();
            br.ReadSingle();                   // event time
            info.EventPosX = br.ReadSingle();
            br.ReadSingle();                   // event pos y
            info.EventPosZ = br.ReadSingle();
            return true;
        }

        private static bool ReadZoneSystem(byte[] raw, WorldDbInfo info, Action<string> report)
        {
            using var ms = new MemoryStream(raw, writable: false);
            using var br = new BinaryReader(ms);

            info.GeneratedZoneCount = br.ReadInt32();
            if (info.GeneratedZoneCount < 0 || info.GeneratedZoneCount > 10_000_000)
            {
                report?.Invoke($"zone count {info.GeneratedZoneCount} is out of range");
                return false;
            }
            // Vector2s: two int16 per zone (it was two int32 before 1.0).
            br.BaseStream.Seek((long)info.GeneratedZoneCount * 4, SeekOrigin.Current);

            br.ReadInt32(); // location version

            int keys = br.ReadInt32();
            if (keys < 0 || keys > 100_000)
            {
                report?.Invoke($"global key count {keys} is out of range");
                return false;
            }
            for (int i = 0; i < keys; i++)
            {
                info.GlobalKeys.Add(br.ReadString());
            }

            info.LocationsGenerated = br.ReadBoolean();

            int locations = br.ReadInt32();
            if (locations < 0 || locations > 10_000_000)
            {
                report?.Invoke($"location count {locations} is out of range");
                return false;
            }
            for (int i = 0; i < locations; i++)
            {
                var location = new DbLocation();
                location.Prefab = WorldLocationNames.Resolve(br.ReadInt32());
                location.X = br.ReadSingle();
                location.Y = br.ReadSingle();
                location.Z = br.ReadSingle();
                location.Placed = br.ReadBoolean();
                info.Locations.Add(location);
            }
            return true;
        }

        private static byte[] Decompress(byte[] input)
        {
            using var src = new MemoryStream(input, writable: false);
            using var gz = new GZipStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();
            gz.CopyTo(dst);
            return dst.ToArray();
        }

        // ------------------------------------------------------------------
        // _main.N.chunks
        // ------------------------------------------------------------------

        private readonly struct ChunkRecord
        {
            public readonly ushort Chunk;
            public readonly byte Size;
            public readonly uint Version;
            public readonly int ZdoCount;

            public ChunkRecord(ushort chunk, byte size, uint version, int zdoCount)
            {
                Chunk = chunk;
                Size = size;
                Version = version;
                ZdoCount = zdoCount;
            }

            public bool IsPortalChunk => Chunk == PortalChunkIndex && Size == PortalChunkSize;

            /// <summary>
            /// ChunkSaveMapping.GetChunkFilename (decomp_new :94198): the y byte
            /// is printed first, both bytes as lowercase two-digit hex, then the
            /// chunk size and the chunk's own version.
            /// </summary>
            public string FileName =>
                (Chunk >> 8).ToString("x2", CultureInfo.InvariantCulture) + "_"
                + (Chunk & 0xFF).ToString("x2", CultureInfo.InvariantCulture) + "__"
                + Size.ToString(CultureInfo.InvariantCulture) + "_"
                + Version.ToString(CultureInfo.InvariantCulture) + ".chunk";
        }

        /// <summary>
        /// The chunk file names one generation's committed index points at, or null when the
        /// index is missing or unreadable. Used by the pre-update snapshot so a copy carries
        /// exactly the chunk files the committed save refers to and none of the ones an
        /// in-flight save has already written for the generation after it.
        /// </summary>
        internal static HashSet<string> TryReadChunkFileNames(string worldDirectory, int generation)
        {
            try
            {
                var records = ReadChunkIndex(GenerationPath(worldDirectory, generation, ".chunks"), null);
                if (records == null) return null;

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var record in records) names.Add(record.FileName);
                return names;
            }
            catch
            {
                return null;
            }
        }

        private static List<ChunkRecord> ReadChunkIndex(string path, Action<string> report)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            int version = br.ReadUInt16();
            if (version < WorldDbReader.ChunkedSaveVersion || version > WorldDbReader.MaxWorldVersion)
            {
                report?.Invoke($"{Path.GetFileName(path)}: chunk index version {version} is not supported");
                return null;
            }

            br.ReadInt32(); // total ZDOs across every chunk the game tracked

            int count = br.ReadInt32();
            if (count < 0 || count > 1_000_000)
            {
                report?.Invoke($"{Path.GetFileName(path)}: chunk count {count} is out of range");
                return null;
            }

            var records = new List<ChunkRecord>(count);
            for (int i = 0; i < count; i++)
            {
                ushort chunk = br.ReadUInt16();
                byte size = br.ReadByte();
                uint chunkVersion = br.ReadUInt32();
                int zdos = br.ReadInt32();
                if (zdos < 0 || zdos > 50_000_000)
                {
                    report?.Invoke($"{Path.GetFileName(path)}: chunk {chunk} claims {zdos} objects");
                    return null;
                }
                records.Add(new ChunkRecord(chunk, size, chunkVersion, zdos));
            }
            return records;
        }

        // ------------------------------------------------------------------
        // <yy>_<xx>__<size>_<version>.chunk
        // ------------------------------------------------------------------

        private static void ReadChunkFile(string path, bool isPortalChunk, WorldDbInfo info, List<(float X, float Z)> buildPieces)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            int version = br.ReadInt16();
            if (version < WorldDbReader.ChunkedSaveVersion || version > WorldDbReader.MaxWorldVersion)
            {
                throw new InvalidDataException($"chunk version {version} is not supported");
            }

            int count = br.ReadInt32();
            if (count < 0 || count > 50_000_000)
            {
                throw new InvalidDataException($"chunk claims {count} objects");
            }

            for (int i = 0; i < count; i++)
            {
                ReadZdo(br, isPortalChunk, info, buildPieces);
            }
        }

        /// <summary>
        /// Walks one world-version-40 ZDO record. The sector field the old
        /// format carried is gone, the position can be a packed Vector2s, and
        /// the rotation is a packed 2 or 4 byte value instead of three floats
        /// (ZDO.Load, decomp_new :74443).
        /// </summary>
        private static void ReadZdo(BinaryReader br, bool isPortalChunk, WorldDbInfo info, List<(float X, float Z)> buildPieces)
        {
            ushort flags = br.ReadUInt16();

            float posX, posY, posZ;
            if ((flags & 0x2000) != 0) // SmallPosition
            {
                short packedX = br.ReadInt16();
                short packedZ = br.ReadInt16();
                posX = packedX;
                posY = 0f;
                posZ = packedZ;
            }
            else
            {
                posX = br.ReadSingle();
                posY = br.ReadSingle();
                posZ = br.ReadSingle();
            }

            int prefab = br.ReadInt32();

            if ((flags & 0x1000) != 0) // Rotation, ZPackage.ReadSmallRotation
            {
                ushort packed = br.ReadUInt16();
                if ((packed & 0x8000) == 0)
                {
                    br.ReadUInt16(); // full three-axis form spans two ushorts
                }
            }

            // Everything in the portal chunk is a portal whatever it is called,
            // which is also how the game decides (the portal prefab list is
            // Unity data, not something a save reader can know).
            bool isPortal = isPortalChunk || WorldDbReader.PortalPrefabHashes.Contains(prefab);
            bool isMapTable = prefab == WorldDbReader.HashCartographyTable;

            if ((flags & 0xFF) == 0)
            {
                if (isPortal)
                {
                    info.Portals.Add(new DbPortal { Tag = "", X = posX, Y = posY, Z = posZ });
                }
                return;
            }

            if ((flags & 0x0001) != 0) // connection
            {
                br.ReadByte();
                br.ReadInt32();
            }
            if ((flags & 0x0002) != 0) // floats
            {
                int n = WorldDbReader.ReadNumItems(br);
                br.BaseStream.Seek((long)n * 8, SeekOrigin.Current);
            }
            if ((flags & 0x0004) != 0) // Vector3s
            {
                int n = WorldDbReader.ReadNumItems(br);
                br.BaseStream.Seek((long)n * 16, SeekOrigin.Current);
            }
            if ((flags & 0x0008) != 0) // quaternions
            {
                int n = WorldDbReader.ReadNumItems(br);
                br.BaseStream.Seek((long)n * 20, SeekOrigin.Current);
            }
            if ((flags & 0x0010) != 0) // ints
            {
                int n = WorldDbReader.ReadNumItems(br);
                br.BaseStream.Seek((long)n * 8, SeekOrigin.Current);
            }

            bool isBuildPiece = false;
            if ((flags & 0x0020) != 0) // longs
            {
                int n = WorldDbReader.ReadNumItems(br);
                for (int i = 0; i < n; i++)
                {
                    int key = br.ReadInt32();
                    long value = br.ReadInt64();
                    if (key == WorldDbReader.HashCreator && value != 0)
                    {
                        isBuildPiece = true;
                    }
                }
            }

            string portalTag = null;
            if ((flags & 0x0040) != 0) // strings
            {
                int n = WorldDbReader.ReadNumItems(br);
                for (int i = 0; i < n; i++)
                {
                    int key = br.ReadInt32();
                    if (isPortal && key == WorldDbReader.HashTag)
                    {
                        portalTag = br.ReadString();
                    }
                    else
                    {
                        WorldDbReader.SkipString(br);
                    }
                }
            }

            byte[] mapData = null;
            if ((flags & 0x0080) != 0) // byte arrays
            {
                int n = WorldDbReader.ReadNumItems(br);
                for (int i = 0; i < n; i++)
                {
                    int key = br.ReadInt32();
                    int length = br.ReadInt32();
                    if (length < 0 || length > 128 * 1024 * 1024)
                    {
                        throw new InvalidDataException("byte array length out of range");
                    }
                    if (isMapTable && key == WorldDbReader.HashData)
                    {
                        mapData = br.ReadBytes(length);
                    }
                    else
                    {
                        br.BaseStream.Seek(length, SeekOrigin.Current);
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
    }
}
