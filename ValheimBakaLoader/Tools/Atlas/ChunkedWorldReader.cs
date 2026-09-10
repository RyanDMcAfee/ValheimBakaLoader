using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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

        /// <summary>
        /// Hard ceiling on the gzip block in a .db2. A real one is a few
        /// hundred kilobytes; the biggest world anyone has is nowhere near
        /// this, so anything above it is a corrupt length field.
        /// </summary>
        private const int MaxCompressedZoneBlockBytes = 256 * 1024 * 1024;

        /// <summary>
        /// Hard ceiling on what that block is allowed to expand to. Past this
        /// the file is treated as corrupt, never as a valid save.
        /// </summary>
        private const int MaxDecompressedZoneBlockBytes = 1024 * 1024 * 1024;

        /// <summary>Ceiling on a file read whole (.chunks, one .chunk), same reasoning.</summary>
        private const int MaxWholeFileBytes = 256 * 1024 * 1024;

        /// <summary>
        /// Read buffer for the .db2, which a BinaryReader walks field by field.
        /// The default 4 KB turns a 143 KB header into three dozen reads; 64 KB
        /// takes that to three and still sits under the 85 KB line above which
        /// an array goes to the large object heap, which matters because the
        /// Atlas re-reads this file every time it refreshes.
        /// </summary>
        private const int Db2BufferBytes = 64 * 1024;

        /// <summary>
        /// How many chunk files may be read at once. A world with a large
        /// explored area has thousands of small ones, and reading them one at a
        /// time leaves the disk idle between opens; this is capped so a big
        /// world cannot flood the thread pool the rest of the app shares.
        /// </summary>
        private static readonly int ChunkReadParallelism = Math.Min(Environment.ProcessorCount, 8);

        private static readonly Regex GenerationPattern =
            new Regex(@"^_main\.(\d+)\.fwl2$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Runs once a generation has been picked and before any of its files
        /// are opened. Null in the app, and the reader behaves exactly as it
        /// would without it. It exists because the one situation the re-read
        /// branch below is built for, a save landing while a read is already in
        /// flight, cannot be arranged from outside the reader: by the time a
        /// test has written the next generation, the reader would have picked
        /// that one to begin with. A test hands in a hook here, lands the save
        /// from inside it, and the real branch runs.
        /// </summary>
        internal static Action<string, int> GenerationPicked;

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
                int generation = -1;
                try
                {
                    if (!Directory.Exists(worldDirectory))
                    {
                        report?.Invoke($"chunked world folder not found: {worldDirectory}");
                        return null;
                    }

                    generation = FindCommittedGeneration(worldDirectory, report);
                    if (generation < 0) return null;
                    GenerationPicked?.Invoke(worldDirectory, generation);

                    var chunkNotes = new List<string>();
                    WorldDbInfo info = ReadGeneration(
                        worldDirectory, generation, report, chunkNotes, out IOException chunkVanished);
                    if (info == null) return null;

                    // A chunk file that would not open is either the commit of the
                    // next generation deleting this one's files, or damage, and the
                    // two want opposite answers. The game writes generation N+1's
                    // .ok marker BEFORE it deletes generation N (ZNet.SaveWorldThread,
                    // decomp_new :80502-80531), so re-scanning settles it with no
                    // waiting and no guessing.
                    if (chunkVanished != null
                        && ShouldReadAgain(generation, CommittedGenerationOrNone(worldDirectory), attempt))
                    {
                        // A save landed while this read was running. Throw away the
                        // half-old world, quietly, and read the one that is committed
                        // now. This is the ONLY case that reads twice.
                        continue;
                    }

                    if (chunkVanished != null)
                    {
                        report?.Invoke($"generation {generation} is still the newest committed save, so a chunk that "
                            + $"would not open is damage and not a save landing mid-read "
                            + $"({chunkVanished.GetType().Name}: {chunkVanished.Message})");
                    }

                    foreach (string note in chunkNotes)
                    {
                        report?.Invoke(note);
                    }
                    return info;
                }
                catch (IOException ex) when (attempt == 0)
                {
                    // The .db2 or the .chunks index itself would not open. There is
                    // no partial world to salvage from that, so the same question
                    // decides everything: a higher committed generation means a save
                    // landed and the read is worth repeating, the same generation
                    // means the save is corrupt and repeating it would only open the
                    // same broken file and fail identically.
                    if (!ShouldReadAgain(generation, CommittedGenerationOrNone(worldDirectory), attempt))
                    {
                        report?.Invoke($"chunked world read failed ({ex.GetType().Name}): {ex.Message}");
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    report?.Invoke($"chunked world read failed ({ex.GetType().Name}): {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// The one rule that decides whether a file the committed index pointed
        /// at, and which then would not open, is worth reading around a second
        /// time. Reading again only helps when a save landed underneath this
        /// one: the game writes generation N+1's .ok marker before it deletes
        /// generation N (ZNet.SaveWorldThread, decomp_new :80502-80531), so a
        /// higher committed generation than the one just read is proof of
        /// exactly that. The same generation still being newest means the file
        /// is damaged, and opening it again would fail in the same way; the
        /// reader says so instead of waiting and pretending otherwise. One
        /// re-read at most, so a folder that keeps rotating cannot spin here.
        /// </summary>
        internal static bool ShouldReadAgain(int generationRead, int committedNow, int attempt)
        {
            return attempt == 0 && committedNow > generationRead;
        }

        /// <summary>
        /// The committed generation, or -1 when the folder cannot even be
        /// listed. Used from the failure paths, where the folder being gone is
        /// one of the things that can have just happened and must not turn into
        /// a second exception thrown out of a catch block.
        /// </summary>
        private static int CommittedGenerationOrNone(string worldDirectory)
        {
            try
            {
                return FindCommittedGeneration(worldDirectory, null);
            }
            catch
            {
                return -1;
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

        /// <param name="chunkNotes">
        /// Collects one line per chunk that could not be read, instead of reporting them as they
        /// happen. The caller only knows whether this generation is still the newest one AFTER
        /// the read, and a world it is about to throw away because a save landed underneath it
        /// should not have filed complaints about it first.
        /// </param>
        /// <param name="chunkVanished">
        /// The first IO failure a chunk file raised, or null. It says "a file that the committed
        /// index points at would not open", which is what both a save landing mid-read and a
        /// damaged save look like from here; the caller tells them apart by re-scanning.
        /// </param>
        private static WorldDbInfo ReadGeneration(
            string worldDirectory,
            int generation,
            Action<string> report,
            List<string> chunkNotes,
            out IOException chunkVanished)
        {
            chunkVanished = null;
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

            int zdoCount = 0;
            foreach (ChunkRecord chunk in chunks)
            {
                zdoCount += chunk.ZdoCount;
            }
            info.ChunksTotal = chunks.Count;

            // Thousands of small files on a well explored world, so they are
            // read a few at a time instead of one after another. Each read
            // fills its own bucket and nothing touches the shared result until
            // the loop is done, so the portals, map tables and build sites come
            // out in chunk-index order no matter which thread finished first.
            var buckets = new ChunkPayload[chunks.Count];
            var failures = new string[chunks.Count];
            IOException vanished = null;

            var options = new ParallelOptions { MaxDegreeOfParallelism = ChunkReadParallelism };
            Parallel.For(0, chunks.Count, options, i =>
            {
                ChunkRecord chunk = chunks[i];
                string chunkPath = Path.Combine(worldDirectory, chunk.FileName);
                var payload = new ChunkPayload();

                try
                {
                    if (!File.Exists(chunkPath))
                    {
                        throw new FileNotFoundException("chunk file is not there", chunkPath);
                    }

                    ReadChunkFile(chunkPath, chunk.IsPortalChunk, payload);
                    buckets[i] = payload;
                }
                catch (Exception ex)
                {
                    // The rest of the world is still worth drawing, as long as the map says a
                    // piece of it is missing. A missing or busy file is also what a save landing
                    // mid-read looks like, so that one is handed back up as well and the caller
                    // decides whether a newer generation has since been committed.
                    if (ex is IOException io)
                    {
                        Interlocked.CompareExchange(ref vanished, io, null);
                    }
                    failures[i] = $"chunk {chunk.FileName} could not be read ({ex.GetType().Name}: {ex.Message}), skipping";
                }
            });

            chunkVanished = vanished;

            var buildPieces = new List<(float X, float Z)>();
            for (int i = 0; i < buckets.Length; i++)
            {
                if (failures[i] != null)
                {
                    info.ChunksSkipped++;
                    chunkNotes.Add(failures[i]);
                    continue;
                }

                ChunkPayload payload = buckets[i];
                if (payload == null) continue;

                info.Portals.AddRange(payload.Portals);
                info.MapTables.AddRange(payload.MapTables);
                buildPieces.AddRange(payload.BuildPieces);
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
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Db2BufferBytes, FileOptions.SequentialScan);
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

            // One int32 off the disk decides how big an array to allocate, so it
            // is checked against a hard ceiling AND against what is really left
            // in the file before anything is allocated. A corrupt length field
            // now costs nothing instead of half a gigabyte.
            int compressedLength = br.ReadInt32();
            if (compressedLength < 0 || compressedLength > MaxCompressedZoneBlockBytes)
            {
                report?.Invoke($"{Path.GetFileName(path)}: zone block length {compressedLength} is out of range "
                    + $"(the ceiling is {MaxCompressedZoneBlockBytes / (1024 * 1024)} MB)");
                return false;
            }

            long remaining = fs.Length - fs.Position;
            if (compressedLength > remaining)
            {
                report?.Invoke($"{Path.GetFileName(path)}: zone block claims {compressedLength} bytes but only "
                    + $"{remaining} are left in the file, so the save is truncated");
                return false;
            }

            byte[] compressed = br.ReadBytes(compressedLength);
            if (compressed.Length != compressedLength)
            {
                report?.Invoke($"{Path.GetFileName(path)}: zone block is truncated");
                return false;
            }

            byte[] zoneBlock = Decompress(compressed, Path.GetFileName(path), report);
            if (zoneBlock == null)
            {
                return false;
            }

            if (!ReadZoneSystem(zoneBlock, info, report))
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

        /// <summary>
        /// Gunzips the zone block, or null (with a reason handed to
        /// <paramref name="report"/>) when it expands past
        /// <see cref="MaxDecompressedZoneBlockBytes"/>. The compressed size is
        /// capped too, but that bounds nothing on its own: gzip will happily
        /// turn a few megabytes of a repeating byte into gigabytes, so the
        /// output is counted as it comes out and the read is abandoned the
        /// moment it goes past the ceiling. A block that big is not a Valheim
        /// save, so it is reported as corrupt rather than parsed.
        /// </summary>
        private static byte[] Decompress(byte[] input, string fileName, Action<string> report)
        {
            // A gzip member ends with ISIZE, the uncompressed length modulo 4 GB
            // (RFC 1952, section 2.3.1). A crafted file can lie about it, which
            // is what the counted copy below is for, but an honestly oversized
            // block is turned away here for the price of reading four bytes and
            // never gets a byte allocated for it.
            if (input.Length >= 4)
            {
                long declared = BitConverter.ToUInt32(input, input.Length - 4);
                if (declared > MaxDecompressedZoneBlockBytes)
                {
                    report?.Invoke($"{fileName}: the zone block says it expands to {declared} bytes, past the "
                        + $"{MaxDecompressedZoneBlockBytes / (1024 * 1024)} MB ceiling, so the save is corrupt");
                    return null;
                }
            }

            using var src = new MemoryStream(input, writable: false);
            using var gz = new GZipStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();

            byte[] buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                int read = gz.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                total += read;
                if (total > MaxDecompressedZoneBlockBytes)
                {
                    report?.Invoke($"{fileName}: the zone block expands past "
                        + $"{MaxDecompressedZoneBlockBytes / (1024 * 1024)} MB, so the save is corrupt");
                    return null;
                }

                dst.Write(buffer, 0, read);
            }

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
            using var br = new BinaryReader(new MemoryStream(ReadWholeFile(path), writable: false));

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

        /// <summary>
        /// What one chunk file contributed. Kept per chunk so the files can be
        /// read in parallel and merged afterwards in a fixed order.
        /// </summary>
        private sealed class ChunkPayload
        {
            public readonly List<DbPortal> Portals = new List<DbPortal>();
            public readonly List<DbMapTable> MapTables = new List<DbMapTable>();
            public readonly List<(float X, float Z)> BuildPieces = new List<(float X, float Z)>();
        }

        private static void ReadChunkFile(string path, bool isPortalChunk, ChunkPayload payload)
        {
            using var br = new BinaryReader(new MemoryStream(ReadWholeFile(path), writable: false));

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
                ReadZdo(br, isPortalChunk, payload);
            }
        }

        /// <summary>
        /// One open and one read per chunk file. The old shape used a
        /// BinaryReader straight off a default FileStream, which pulls a
        /// thousand-byte file through a 4 KB buffer in several syscalls; a
        /// chunk is small and is walked front to back exactly once, so it is
        /// cheaper to take the whole thing in one go and parse out of memory.
        /// FileShare.ReadWrite is kept because the game may be writing the
        /// generation next door while this runs.
        /// </summary>
        private static byte[] ReadWholeFile(string path)
        {
            // 4096 is deliberate, not a leftover default: the one Read below
            // asks for the whole file at once, and a request at least as big as
            // the buffer goes straight to the caller's array without touching
            // it. A megabyte-sized buffer here would only be an allocation per
            // file, and there are thousands of files.
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);

            long length = fs.Length;
            if (length > MaxWholeFileBytes)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} is {length} bytes, past the {MaxWholeFileBytes / (1024 * 1024)} MB ceiling");
            }

            byte[] raw = new byte[length];
            int offset = 0;
            while (offset < raw.Length)
            {
                int read = fs.Read(raw, offset, raw.Length - offset);
                if (read <= 0)
                {
                    throw new EndOfStreamException($"file ended after {offset} of {raw.Length} bytes");
                }
                offset += read;
            }
            return raw;
        }

        /// <summary>
        /// Walks one world-version-40 ZDO record. The sector field the old
        /// format carried is gone, the position can be a packed Vector2s, and
        /// the rotation is a packed 2 or 4 byte value instead of three floats
        /// (ZDO.Load, decomp_new :74443).
        /// </summary>
        private static void ReadZdo(BinaryReader br, bool isPortalChunk, ChunkPayload payload)
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
                    payload.Portals.Add(new DbPortal { Tag = "", X = posX, Y = posY, Z = posZ });
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
                payload.Portals.Add(new DbPortal { Tag = portalTag ?? "", X = posX, Y = posY, Z = posZ });
            }
            else if (isMapTable)
            {
                payload.MapTables.Add(new DbMapTable { X = posX, Y = posY, Z = posZ, Data = mapData });
            }
            else if (isBuildPiece)
            {
                payload.BuildPieces.Add((posX, posZ));
            }
        }
    }
}
