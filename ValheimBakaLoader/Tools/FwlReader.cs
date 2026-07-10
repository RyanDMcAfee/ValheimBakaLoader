using System;
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

                return new FwlWorldInfo
                {
                    WorldName = name,
                    SeedName = seedName,
                    Seed = seed,
                    WorldVersion = worldVersion,
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Looks for "{world}.fwl" under a save folder's worlds_local/ then worlds/
        /// subdirs (the same two the game and the rest of the app scan) and parses it.
        /// Returns null when the world has no .fwl anywhere under that folder.
        /// </summary>
        public static FwlWorldInfo TryReadWorld(string saveFolder, string worldName)
        {
            var path = FindWorldFwl(saveFolder, worldName);
            return path == null ? null : TryRead(path);
        }

        /// <summary>Full path of the world's .fwl under the save folder, or null.</summary>
        public static string FindWorldFwl(string saveFolder, string worldName)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(worldName)) return null;
            foreach (var sub in new[] { "worlds_local", "worlds" })
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
    /// HARD INVARIANT: never touches an existing world. If a .fwl for the world
    /// already exists under the save folder (worlds_local/ or worlds/), the write
    /// is refused - an existing world's seed must never be overwritten.
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

            // THE guard: an existing world's seed must never be overwritten.
            var existing = FwlReader.FindWorldFwl(saveFolder, worldName);
            if (existing != null)
                throw new InvalidOperationException(
                    $"World '{worldName}' already exists ({existing}) - the seed of an existing world can't be changed.");

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
}
