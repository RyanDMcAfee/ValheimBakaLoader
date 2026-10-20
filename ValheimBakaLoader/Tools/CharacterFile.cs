using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ValheimBakaLoader.Tools
{
    /// <summary>What a Valheim character file says about itself, as far as a copy needs.</summary>
    public sealed class CharacterFileInfo
    {
        /// <summary>The profile version the game wrote it with.</summary>
        public int Version { get; init; }

        /// <summary>The character's name as the game shows it.</summary>
        public string Name { get; init; }

        /// <summary>The id the game builds the player's identity from. A copy keeps it.</summary>
        public long PlayerId { get; init; }

        /// <summary>Whether the SHA-512 stored after the profile matches the profile.</summary>
        public bool HashMatches { get; init; }

        internal byte[] Data { get; init; }
        internal int NameStart { get; init; }
        internal int NameEnd { get; init; }
    }

    /// <summary>
    /// Reads and renames a Valheim character file (.fch), in the layout the game itself
    /// writes and reads: PlayerProfile.SavePlayerToDisk and LoadPlayerFromDisk in
    /// assembly_valheim.
    /// <para>
    /// The file is the profile as one block of bytes with its length in front of it, then a
    /// SHA-512 of that block with its length in front of it too. The profile is ZPackage
    /// writes: little endian numbers, and strings with a 7 bit length in front, which is what
    /// BinaryReader and BinaryWriter speak. The name sits in the middle, after the stats and
    /// the per world data and before the player id, so it cannot be found by looking for it.
    /// The reader walks every field the game would, in the game's order, for every profile
    /// version the game still loads (27 to 47), and only trusts the place it found the name
    /// in when the walk ends exactly at the end of the block. A file it cannot walk that way is
    /// refused rather than guessed at.
    /// </para>
    /// <para>
    /// A rename changes the name and nothing else. Every byte before and after it is kept, the
    /// player id included, so whatever the character built still answers to it. The hash is
    /// made again over the new block: the game reads past it without checking, but it writes a
    /// true one, so a copy carries a true one as well.
    /// </para>
    /// </summary>
    public static class CharacterFile
    {
        /// <summary>The oldest profile version the game still loads (Version.Player.OldestForwardCompatible).</summary>
        public const int OldestVersion = 27;

        /// <summary>The newest profile version this reader knows (Version.Player.CheatCleanSlate).</summary>
        public const int NewestVersion = 47;

        /// <summary>A character with a lot of explored map is a few MB. Anything past this is not one.</summary>
        public const long MaxBytes = 64L * 1024 * 1024;

        /// <summary>The file is not a character file this reader can walk.</summary>
        public const string NotACharacter = "notACharacter";

        /// <summary>The file names a profile version outside the ones the game loads.</summary>
        public const string UnknownVersion = "unknownVersion";

        /// <summary>
        /// Reads a whole .fch. Answers false with <paramref name="problem"/> set to
        /// <see cref="NotACharacter"/> or <see cref="UnknownVersion"/> when it cannot be walked.
        /// </summary>
        public static bool TryRead(byte[] file, out CharacterFileInfo info, out string problem)
        {
            info = null;
            problem = NotACharacter;
            if (file == null || file.Length < 8 || file.Length > MaxBytes) return false;

            var dataLength = BitConverter.ToInt32(file, 0);
            if (dataLength < 4 || dataLength > file.Length - 8) return false;
            var hashLength = BitConverter.ToInt32(file, 4 + dataLength);
            if (hashLength < 0 || 8L + dataLength + hashLength != file.Length) return false;

            var data = new byte[dataLength];
            Buffer.BlockCopy(file, 4, data, 0, dataLength);
            var hash = new byte[hashLength];
            Buffer.BlockCopy(file, 8 + dataLength, hash, 0, hashLength);

            var version = BitConverter.ToInt32(data, 0);
            if (version < OldestVersion || version > NewestVersion)
            {
                problem = UnknownVersion;
                return false;
            }

            try
            {
                using var stream = new MemoryStream(data, writable: false);
                using var r = new BinaryReader(stream, Encoding.UTF8);
                var walk = new Walker(r, data.Length);
                walk.Skip(4);                                   // the version, read above

                // The stats, in the three shapes the game has had.
                if (version >= 46 || version == 44)             // DeepNorth, and AbandonedDN
                {
                    var statCount = walk.Count(4);
                    var difficulties = walk.Count(statCount * 4L + 4);
                    for (var i = 0; i < difficulties; i++)
                    {
                        walk.Skip(statCount * 4L);
                        walk.Table();                           // known worlds
                        walk.Table();                           // known world keys
                        walk.Table();                           // known commands
                        var enemyTables = walk.Count(4);
                        for (var n = 0; n < enemyTables; n++) walk.Table();
                        walk.Table();                           // item pickups
                        walk.Table();                           // item crafts
                        walk.Table();                           // pickables
                        walk.Table();                           // food eaten
                        walk.Table();                           // pieces placed
                    }
                }
                else if (version >= 38)                         // Stats2
                {
                    walk.Skip(walk.Count(4) * 4L);
                }
                else if (version >= 28)                         // Stats: four ints
                {
                    walk.Skip(16);
                }

                if (version >= 40) walk.Bool();                 // first spawn

                var worlds = walk.Count(8 + 1 + 12 + 1 + 12 + 12);
                for (var i = 0; i < worlds; i++)
                {
                    walk.Skip(8);                               // the world's uid
                    walk.Bool(); walk.Skip(12);                 // custom spawn point
                    walk.Bool(); walk.Skip(12);                 // logout point
                    if (version >= 30) { walk.Bool(); walk.Skip(12); } // death point
                    walk.Skip(12);                              // home point
                    if (version >= 29 && walk.Bool()) walk.ByteArray(); // explored map
                }

                var nameStart = (int)stream.Position;
                var name = walk.String();
                var nameEnd = (int)stream.Position;
                walk.Need(8);
                var playerId = r.ReadInt64();
                walk.String();                                  // start seed

                if (version >= 38)
                {
                    walk.Bool();                                // used cheats
                    walk.Skip(8);                               // date created
                    if (version < 46 && version != 44)
                    {
                        walk.Table();
                        walk.Table();
                        walk.Table();
                        if (version >= 42)                      // CallToArms
                        {
                            walk.Table();
                            walk.Table();
                            walk.Table();
                        }
                    }
                }

                if (walk.Bool()) walk.ByteArray();              // the player's own data

                if (stream.Position != data.Length) return false;
                if (string.IsNullOrEmpty(name)) return false;

                bool matches;
                using (var sha = SHA512.Create())
                    matches = hash.Length == 64 && sha.ComputeHash(data).SequenceEqual(hash);

                info = new CharacterFileInfo
                {
                    Version = version,
                    Name = name,
                    PlayerId = playerId,
                    HashMatches = matches,
                    Data = data,
                    NameStart = nameStart,
                    NameEnd = nameEnd,
                };
                problem = null;
                return true;
            }
            catch (Exception e) when (e is EndOfStreamException || e is InvalidDataException
                                      || e is FormatException || e is ArgumentException || e is IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// The same file with the name changed to <paramref name="newName"/> and the hash made
        /// again. Every other byte of the profile is the source's.
        /// </summary>
        public static byte[] Rename(CharacterFileInfo info, string newName)
        {
            if (info?.Data == null) throw new ArgumentNullException(nameof(info));
            if (string.IsNullOrEmpty(newName)) throw new ArgumentException("A name is required.", nameof(newName));

            byte[] data;
            using (var block = new MemoryStream())
            {
                block.Write(info.Data, 0, info.NameStart);
                using (var w = new BinaryWriter(block, Encoding.UTF8, leaveOpen: true)) w.Write(newName);
                block.Write(info.Data, info.NameEnd, info.Data.Length - info.NameEnd);
                data = block.ToArray();
            }

            byte[] hash;
            using (var sha = SHA512.Create()) hash = sha.ComputeHash(data);

            using var file = new MemoryStream();
            using (var w = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(data.Length);
                w.Write(data);
                w.Write(hash.Length);
                w.Write(hash);
            }
            return file.ToArray();
        }

        /// <summary>
        /// The file name the game gives a character of this name: the name in lower case, the
        /// way FejdStartup.OnNewCharacterDone names it, with .fch after it.
        /// </summary>
        public static string FileNameFor(string name) => (name ?? string.Empty).ToLowerInvariant() + ".fch";

        /// <summary>Reads with a bound on every count, so a damaged file fails fast rather than allocating.</summary>
        private sealed class Walker
        {
            private readonly BinaryReader R;
            private readonly long Length;

            public Walker(BinaryReader r, long length) { R = r; Length = length; }

            private long Left => Length - R.BaseStream.Position;

            public void Need(long bytes)
            {
                if (bytes > Left) throw new EndOfStreamException();
            }

            public bool Bool()
            {
                Need(1);
                var b = R.ReadByte();
                if (b > 1) throw new InvalidDataException("not a bool");
                return b == 1;
            }

            /// <summary>A count whose entries take at least <paramref name="leastEach"/> bytes each.</summary>
            public int Count(long leastEach)
            {
                Need(4);
                var n = R.ReadInt32();
                if (n < 0 || n * Math.Max(1L, leastEach) > Left) throw new InvalidDataException("count past the end");
                return n;
            }

            public void Skip(long bytes)
            {
                if (bytes < 0) throw new InvalidDataException("negative length");
                Need(bytes);
                R.BaseStream.Seek(bytes, SeekOrigin.Current);
            }

            public string String() => R.ReadString();

            public void ByteArray() => Skip(Count(1));

            /// <summary>A string to float table, the shape every stat list after the first is saved in.</summary>
            public void Table()
            {
                var n = Count(1 + 4);
                for (var i = 0; i < n; i++)
                {
                    String();
                    Skip(4);
                }
            }
        }
    }
}
