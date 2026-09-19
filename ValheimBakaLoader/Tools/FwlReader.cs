using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// World identity parsed from a Valheim .fwl world-metadata file.
    /// </summary>
    public class FwlWorldInfo
    {
        public string WorldName { get; init; }

        /// <summary>The human-typed seed string (what map/seed tools take as input).</summary>
        public string SeedName { get; init; }

        /// <summary>Valheim's stable hash of <see cref="SeedName"/> (the numeric seed).</summary>
        public int Seed { get; init; }

        public int WorldVersion { get; init; }

        /// <summary>The world's unique id (the game's own uid field).</summary>
        public long Uid { get; init; }

        /// <summary>Worldgen algorithm version the save was generated with (2 in both 0.2x and 1.0).</summary>
        public int WorldGenVersion { get; init; }

        /// <summary>On-disk shape the identity was read from.</summary>
        public WorldFormat Format { get; init; }
    }

    /// <summary>
    /// Minimal reader for the Valheim .fwl binary header. Layout (written with a
    /// .NET BinaryWriter, verified byte-for-byte against a live worldVersion-37 file):
    ///
    ///   int32  payload size (file length - 4)
    ///   int32  worldVersion
    ///   string worldName   (7-bit length-prefixed)
    ///   string seedName    (7-bit length-prefixed)
    ///   int32  seed        (GetStableHashCode of seedName)
    ///   int64  uid
    ///   int32  worldGenVersion
    ///   ...    trailing fields vary by version (needsDB flag, world modifiers) - not read
    ///
    /// Only the fields through <c>seed</c> are needed, so trailing layout changes
    /// across game versions cannot break this parser.
    /// </summary>
    public static class FwlReader
    {
        /// <summary>
        /// Reads world identity from a .fwl file. Returns null if the file is
        /// missing, locked, or not a plausible .fwl.
        /// </summary>
        public static FwlWorldInfo TryRead(string fwlPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fwlPath) || !File.Exists(fwlPath)) return null;

                // FileShare.ReadWrite: the game rewrites the .fwl while running and we
                // must never contend with the live server's handle.
                using var fs = new FileStream(fwlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var br = new BinaryReader(fs, Encoding.UTF8);

                var payloadSize = br.ReadInt32();
                if (payloadSize <= 0 || payloadSize > fs.Length) return null;

                var worldVersion = br.ReadInt32();
                if (worldVersion <= 0 || worldVersion > 10_000) return null;

                var name = br.ReadString();
                var seedName = br.ReadString();
                var seed = br.ReadInt32();

                if (string.IsNullOrEmpty(name)) return null;

                // uid + worldGenVersion sit right behind the seed in BOTH layouts. They are
                // read defensively: a truncated tail must never cost us the seed we came for.
                long uid = 0;
                var worldGenVersion = 0;
                try
                {
                    uid = br.ReadInt64();
                    worldGenVersion = br.ReadInt32();
                }
                catch (EndOfStreamException) { /* older/short file: identity above is still good */ }

                return new FwlWorldInfo
                {
                    WorldName = name,
                    SeedName = seedName,
                    Seed = seed,
                    WorldVersion = worldVersion,
                    Uid = uid,
                    WorldGenVersion = worldGenVersion,
                    Format = IsFwl2Path(fwlPath) ? WorldFormat.Chunked : WorldFormat.Legacy,
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True for a Valheim 1.0 "_main.{N}.fwl2" metadata file.</summary>
        public static bool IsFwl2Path(string path)
            => !string.IsNullOrWhiteSpace(path)
               && path.EndsWith(".fwl2", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// World identity for a named world under a save folder, in EITHER save format:
        /// the pre-1.0 "{world}.fwl" or the committed "_main.{N}.fwl2" inside a 1.0 world
        /// directory. Returns null when no such world exists under that folder.
        /// </summary>
        public static FwlWorldInfo TryReadWorld(string saveFolder, string worldName)
        {
            var path = FindWorldMeta(saveFolder, worldName);
            return path == null ? null : TryRead(path);
        }

        /// <summary>
        /// Full path of the world's metadata file under the save folder, whichever format it
        /// is in ("{world}.fwl", or the world directory's committed "_main.{N}.fwl2"). Null
        /// when the world does not exist.
        /// </summary>
        public static string FindWorldMeta(string saveFolder, string worldName)
            => WorldStore.Find(saveFolder, worldName)?.MetaPath;

        /// <summary>
        /// Full path of the world's LEGACY .fwl under the save folder, or null. Deliberately
        /// blind to 1.0 world directories: callers that pair the .fwl with a sibling ".db" by
        /// extension only work on the legacy layout. Use <see cref="FindWorldMeta"/> for a
        /// format-agnostic lookup.
        /// </summary>
        public static string FindWorldFwl(string saveFolder, string worldName)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(worldName)) return null;
            foreach (var sub in WorldStore.WorldSubfolders)
            {
                var candidate = Path.Combine(saveFolder, sub, worldName + ".fwl");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    /// <summary>
    /// Creates the .fwl for a BRAND-NEW world so a chosen seed takes effect: the
    /// dedicated server has no -seed argument, but on first launch it adopts a
    /// pre-existing .fwl instead of generating one. Written in the verified
    /// worldVersion-37 layout (see <see cref="FwlReader"/>).
    ///
    /// Valheim 1.0 still accepts this v37 layout and converts the world to its own
    /// directory format on the first save, so BakaLoader never hand-writes a .fwl2.
    ///
    /// HARD INVARIANT: never touches an existing world. If the world already exists
    /// under the save folder in EITHER format (a legacy "{world}.fwl", or a 1.0 world
    /// directory), the write is refused - an existing world's seed must never change.
    /// The check reads the filesystem rather than the world list, so a world the list
    /// deliberately leaves out still holds its name.
    /// </summary>
    public static class FwlWriter
    {
        private const int WorldVersion = 37;      // verified against a live 37 file
        private const int WorldGenVersion = 2;

        private static readonly Random Rng = new();

        /// <summary>
        /// Valheim's StableHashCode over the seed string - this is the numeric seed
        /// the game derives from the typed seed text. Validated against a live world
        /// ("yBvEFPKD9S" -> 649688311).
        /// </summary>
        public static int GetStableHashCode(string s)
        {
            unchecked
            {
                var num = 5381;
                var num2 = num;
                for (var i = 0; i < s.Length && s[i] != '\0'; i += 2)
                {
                    num = ((num << 5) + num) ^ s[i];
                    if (i == s.Length - 1 || s[i + 1] == '\0') break;
                    num2 = ((num2 << 5) + num2) ^ s[i + 1];
                }
                return num + num2 * 1566083941;
            }
        }

        /// <summary>A random human-style seed like the game generates (10 alphanumerics).</summary>
        public static string RandomSeedName()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var buf = new char[10];
            for (var i = 0; i < buf.Length; i++) buf[i] = chars[Rng.Next(chars.Length)];
            return new string(buf);
        }

        /// <summary>
        /// Writes worlds_local/{world}.fwl under the save folder with the given seed.
        /// Throws if a .fwl for the world already exists anywhere under the save folder
        /// (seed of a created world is immutable) or on invalid input.
        /// </summary>
        public static FwlWorldInfo WriteNewWorld(string saveFolder, string worldName, string seedName)
        {
            if (string.IsNullOrWhiteSpace(saveFolder))
                throw new ArgumentException("A save folder is required.", nameof(saveFolder));
            if (string.IsNullOrWhiteSpace(worldName))
                throw new ArgumentException("A world name is required.", nameof(worldName));
            if (worldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("The world name contains characters that can't be used in a file name.", nameof(worldName));
            if (!WorldStore.IsSafeReferenceToken(worldName))
                throw new ArgumentException("That world name is not one Windows can keep as a folder name.", nameof(worldName));

            // THE guard: an existing world's seed must never be overwritten. A Valheim 1.0
            // world is a DIRECTORY, so a name check that only looked for "{world}.fwl" would
            // happily write a second, conflicting seed beside a live chunked world.
            //
            // The world LIST is not enough on its own to answer this. It leaves names out on
            // purpose, backup-shaped ones above all, so a live world whose folder happens to
            // carry the game's backup marker is invisible to it while being perfectly real on
            // disk. The raw filesystem probe is what closes that: it answers "is this name
            // taken" for every name, including the ones the list refuses to return.
            var existingWorld = WorldStore.Find(saveFolder, worldName);
            var existingPath = existingWorld?.Folder ?? WorldStore.FindWorldFilesOnDisk(saveFolder, worldName);
            if (existingPath != null)
                throw new InvalidOperationException(
                    $"World '{worldName}' already exists ({existingPath}), and the seed of an existing world can't be changed.");

            seedName = (seedName ?? "").Trim();
            if (seedName.Length == 0) seedName = RandomSeedName();
            var seed = GetStableHashCode(seedName);

            var dir = Path.Combine(saveFolder, "worlds_local");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, worldName + ".fwl");

            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(WorldVersion);
                bw.Write(worldName);
                bw.Write(seedName);
                bw.Write(seed);
                bw.Write(((long)Rng.Next() << 32) | (uint)Rng.Next());  // world uid
                bw.Write(WorldGenVersion);
                bw.Write(false);   // needsDB
                bw.Write(0);       // no start-with world modifier keys
            }
            var payload = ms.ToArray();

            // CreateNew (not Create): a second existence guard at the filesystem level,
            // atomic against a concurrent creator.
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                bw.Write(payload.Length);
                bw.Write(payload);
            }

            return new FwlWorldInfo
            {
                WorldName = worldName,
                SeedName = seedName,
                Seed = seed,
                WorldVersion = WorldVersion,
            };
        }
    }

    /// <summary>
    /// Rewrites the world name stored INSIDE a .fwl or a .fwl2 header, so a copy made
    /// under a new file name is a real rename rather than the same world wearing two
    /// names at once.
    /// <para>
    /// The name is the third field of the header and the game writes it with a .NET
    /// BinaryWriter, which means a 7 bit encoded BYTE COUNT followed by that many UTF-8
    /// bytes. A longer or shorter name moves every byte behind it and changes the payload
    /// size the file opens with, so the file is rebuilt rather than patched in place: the
    /// four bytes of worldVersion, the new name, and the whole tail exactly as it was.
    /// Nothing behind the name is parsed, which is what keeps this working across game
    /// versions that add fields to the end.
    /// </para>
    /// <para>
    /// It refuses rather than guesses. A file whose leading size claims more bytes than the
    /// file holds, whose worldVersion is not plausible, or whose name and seed name do not
    /// both parse as length prefixed strings inside that payload is left untouched and
    /// answered with false, because a header written half way is a world that will not open.
    /// </para>
    /// <para>
    /// That is STRICTER than the copy's own pre-check, and it is worth knowing by how much.
    /// The pre-check is <see cref="FwlReader.TryRead"/>, which WorldStore.CopyWorldAs asks
    /// before a byte moves. It only wants the fields at the front, so it holds the leading size
    /// against the file's LENGTH and reads the name and the seed name straight out of the file.
    /// This holds the size against the file's PAYLOAD, which is four bytes shorter, and holds
    /// both of those strings inside the payload the header declares. So two bands of header
    /// pass that pre-check and are still not renamed: one whose declared payload overshoots the
    /// file by one, two, three or four bytes, and one whose declared payload is too small to
    /// hold the name or the seed name the file really carries. Both are headers whose own size
    /// field is contradicted by the file it sits in, and rebuilding either one would mean
    /// measuring the tail by a number that was never true: past the last byte there is in the
    /// first case, and short of the name in the second. A header in either band stops the copy
    /// before anything takes the new name, and the staging it was working in goes with it,
    /// rather than landing a world whose header disagrees with the folder it sits in.
    /// </para>
    /// <para>
    /// Neither band says anything about the world LIST, which never opens a header at all.
    /// WorldStore.Enumerate, Find and GetWorldNames name a world by the file or the directory
    /// it sits in, so a world whose header will not parse is listed like any other and is
    /// offered a copy like any other. What such a host meets is the pre-check's own "could not
    /// be read", with the save folder untouched.
    /// </para>
    /// </summary>
    public static class FwlNameRewriter
    {
        /// <summary>The suffix a rebuilt header is written under before it takes its place.</summary>
        private const string WritingSuffix = ".renaming";

        /// <summary>
        /// Writes the header at <paramref name="metaPath"/> back with <paramref name="newName"/>
        /// as the world name it stores. False when the header could not be read, in which case
        /// the file on disk is exactly as it was.
        /// </summary>
        public static bool TryRewriteWorldName(string metaPath, string newName)
        {
            if (string.IsNullOrWhiteSpace(metaPath)) return false;
            if (string.IsNullOrEmpty(newName)) return false;

            byte[] original;
            try
            {
                if (!File.Exists(metaPath)) return false;
                original = File.ReadAllBytes(metaPath);
            }
            catch { return false; }

            var rebuilt = Rebuild(original, newName);
            if (rebuilt == null) return false;

            var staging = metaPath + WritingSuffix;
            try
            {
                File.WriteAllBytes(staging, rebuilt);
                File.Move(staging, metaPath, overwrite: true);
                return true;
            }
            catch
            {
                try { if (File.Exists(staging)) File.Delete(staging); } catch { /* best effort */ }
                return false;
            }
        }

        /// <summary>
        /// The bytes of a header with its world name swapped, or null when the bytes are not
        /// a header this can read. Public so the rewrite can be held against a file built by
        /// hand, byte for byte, without going near a save folder.
        /// </summary>
        public static byte[] Rebuild(byte[] header, string newName)
        {
            // An empty name is refused beside a null one. A header rebuilt around a
            // zero length name parses and opens, and is a world with no name at all:
            // returning it would be answering a question nobody can have meant to ask.
            if (header == null || string.IsNullOrEmpty(newName)) return null;

            // int32 payload size, int32 worldVersion, then the name: the shortest header
            // that could hold all three is twelve bytes.
            if (header.Length < 12) return null;

            // The leading size measures the PAYLOAD, and a file is allowed to carry bytes
            // past that payload rather than end exactly on it: those are kept untouched,
            // because nothing here knows what they are. The size itself has to fit INSIDE
            // the file, which is four bytes stricter than the copy's pre-check reader
            // (FwlReader.TryRead), and everything below is bounded by where the payload ends
            // rather than by where the file does. A header whose size overshoots the file by
            // one to four bytes is therefore one the pre-check will pass and this will refuse:
            // a tail measured past the last byte there is cannot be carried across, and a
            // header rebuilt around a size that was never true is a world that will not open.
            var payloadSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
            if (payloadSize <= 0 || payloadSize > header.Length - 4) return null;
            var payloadEnds = 4 + payloadSize;

            var worldVersion = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (worldVersion <= 0 || worldVersion > 10_000) return null;

            if (!TryReadLength(header, 8, payloadEnds, out var nameBytes, out var namePrefix)) return null;
            // Added up in long on purpose. A length prefix is free to encode a byte count
            // near int.MaxValue, and in int that sum wraps past zero: the bound underneath
            // would then be measuring a negative offset, pass it, and hand it straight back
            // to the reader to index with. A header this cannot read is refused, and
            // refusing rather than throwing is the whole promise of this type.
            var nameEnds = 8L + namePrefix + nameBytes;
            if (nameEnds > payloadEnds) return null;
            var nameAt = (int)nameEnds;

            // The seed name has to parse too. Without that check any file whose ninth byte
            // happens to be a small number would be rewritten as though it were a world.
            if (!TryReadLength(header, nameAt, payloadEnds, out var seedBytes, out var seedPrefix)) return null;
            if (nameEnds + seedPrefix + seedBytes > payloadEnds) return null;

            var written = Encoding.UTF8.GetBytes(newName);
            var lengthPrefix = Encode7Bit(written.Length);
            var tailLength = payloadEnds - nameAt;       // the rest of the payload
            var trailing = header.Length - payloadEnds;  // and whatever the file carries past it

            var payload = new byte[4 + lengthPrefix.Length + written.Length + tailLength];
            Buffer.BlockCopy(header, 4, payload, 0, 4);                       // worldVersion
            Buffer.BlockCopy(lengthPrefix, 0, payload, 4, lengthPrefix.Length);
            Buffer.BlockCopy(written, 0, payload, 4 + lengthPrefix.Length, written.Length);
            Buffer.BlockCopy(header, nameAt, payload, 4 + lengthPrefix.Length + written.Length, tailLength);

            var rebuilt = new byte[4 + payload.Length + trailing];
            BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(0, 4), payload.Length);
            Buffer.BlockCopy(payload, 0, rebuilt, 4, payload.Length);
            if (trailing > 0) Buffer.BlockCopy(header, payloadEnds, rebuilt, 4 + payload.Length, trailing);
            return rebuilt;
        }

        /// <summary>
        /// The 7 bit encoded byte count a BinaryWriter puts in front of a string: seven bits
        /// of the number per byte, the top bit saying another byte follows. Five bytes is the
        /// most an int32 can take, and anything longer is not a length. Reading stops at
        /// <paramref name="limit"/>, which is where the payload ends rather than where the
        /// file does, so a prefix is never read out of bytes the header never claimed.
        /// </summary>
        private static bool TryReadLength(byte[] header, int at, int limit, out int length, out int prefixLength)
        {
            length = 0;
            prefixLength = 0;
            if (at < 0) return false;

            var shift = 0;
            for (var step = 0; step < 5; step++)
            {
                if (at + step >= limit) return false;
                var b = header[at + step];
                length |= (b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0)
                {
                    prefixLength = step + 1;
                    return length >= 0;
                }
            }
            return false;
        }

        /// <summary>The same encoding, written out.</summary>
        private static byte[] Encode7Bit(int value)
        {
            var bytes = new System.Collections.Generic.List<byte>(5);
            var left = (uint)value;
            while (left >= 0x80)
            {
                bytes.Add((byte)(left | 0x80));
                left >>= 7;
            }
            bytes.Add((byte)left);
            return bytes.ToArray();
        }
    }
}
