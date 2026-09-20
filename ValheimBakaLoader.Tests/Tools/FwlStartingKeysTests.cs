using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Reading the world-modifier starting keys out of a world's own header, in both save
    /// formats. Nothing here was possible in 1.2.0: the reader stopped at worldGenVersion and
    /// the keys sat four fields further on, unread.
    /// <para>
    /// Every header here is built byte for byte in a temp folder from the layout
    /// <c>World.SaveWorldFWLData</c> writes, plus the one real capture the test resources
    /// already hold. No world under a save folder is opened, copied or written.
    /// </para>
    /// </summary>
    public class FwlStartingKeysTests : IDisposable
    {
        private readonly string Root =
            Path.Combine(Path.GetTempPath(), "vbl-fwlkeys-" + Guid.NewGuid().ToString("N"));

        public FwlStartingKeysTests() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
            GC.SuppressFinalize(this);
        }

        /// <summary>The real "_main.1.fwl2" a live Valheim 1.0 server wrote (worldVersion 41).</summary>
        private static string RealFwl2Path =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "BakaLegacy_main.1.fwl2");

        // ------------------------------------------------------------------ the legacy .fwl

        [Fact]
        public void A_legacy_fwl_hands_back_the_keys_it_carries()
        {
            var path = WriteHeader("Midgard", 37, new[] { "nobuildcost", "resourcerate 150" });

            Assert.Equal(new[] { "nobuildcost", "resourcerate 150" }, FwlReader.TryReadStartingKeys(path));
        }

        [Fact]
        public void A_legacy_fwl_with_no_keys_answers_empty_rather_than_nothing()
        {
            var path = WriteHeader("Midgard", 37, Array.Empty<string>());

            var keys = FwlReader.TryReadStartingKeys(path);

            Assert.NotNull(keys);           // "there are none" is an answer
            Assert.Empty(keys);
        }

        /// <summary>
        /// A header from before the game stored keys at all. There is nothing there to miss, so
        /// it reads as a world with no modifiers rather than as a failure.
        /// </summary>
        [Fact]
        public void A_header_older_than_the_keys_field_reads_as_a_world_with_none()
        {
            var path = WriteHeader("Elder", 29, Array.Empty<string>(), writeNeedsDb: false, writeKeys: false);

            var keys = FwlReader.TryReadStartingKeys(path);

            Assert.NotNull(keys);
            Assert.Empty(keys);
        }

        // ------------------------------------------------------------------ the 1.0 .fwl2

        [Fact]
        public void A_committed_fwl2_hands_back_the_keys_it_carries()
        {
            var path = WriteHeader("Midgard", 41, new[] { "nomap", "carryweightrate 150" },
                fileName: "_main.1.fwl2", playerHistory: true);

            Assert.Equal(new[] { "nomap", "carryweightrate 150" }, FwlReader.TryReadStartingKeys(path));
        }

        /// <summary>
        /// The one capture taken from a live 1.0 server. It carries no starting keys, which is
        /// what a world nobody has touched the modifier screen on really looks like, and is the
        /// case that must import nothing and say nothing.
        /// </summary>
        [Fact]
        public void The_real_capture_reads_and_carries_no_keys()
        {
            Assert.True(File.Exists(RealFwl2Path), "missing test resource: " + RealFwl2Path);

            var keys = FwlReader.TryReadStartingKeys(RealFwl2Path);

            Assert.NotNull(keys);
            Assert.Empty(keys);
        }

        /// <summary>
        /// Picked the way every other reader here picks it: the committed generation inside the
        /// world directory, not the uncommitted one beside it.
        /// </summary>
        [Fact]
        public void A_named_world_is_found_in_either_format()
        {
            var legacyFolder = Path.Combine(Root, "legacy");
            Directory.CreateDirectory(Path.Combine(legacyFolder, "worlds_local"));
            WriteHeader("Midgard", 37, new[] { "passivemobs" },
                folder: Path.Combine(legacyFolder, "worlds_local"), fileName: "Midgard.fwl");

            Assert.Equal(new[] { "passivemobs" },
                FwlReader.TryReadWorldStartingKeys(legacyFolder, "Midgard"));

            var chunkedFolder = Path.Combine(Root, "chunked");
            var worldDir = Path.Combine(chunkedFolder, "worlds_local", "Ashland");
            Directory.CreateDirectory(worldDir);
            WriteHeader("Ashland", 41, new[] { "fire" },
                folder: worldDir, fileName: "_main.1.fwl2", playerHistory: true);
            File.WriteAllBytes(Path.Combine(worldDir, "_main.1.db2"), new byte[16]);
            File.WriteAllBytes(Path.Combine(worldDir, "_main.1.ok"), BitConverter.GetBytes(41));

            Assert.Equal(new[] { "fire" }, FwlReader.TryReadWorldStartingKeys(chunkedFolder, "Ashland"));
        }

        [Fact]
        public void A_world_that_is_not_there_answers_nothing_at_all()
        {
            Assert.Null(FwlReader.TryReadWorldStartingKeys(Root, "NoSuchWorld"));
            Assert.Null(FwlReader.TryReadStartingKeys(Path.Combine(Root, "missing.fwl")));
            Assert.Null(FwlReader.TryReadStartingKeys(null));
        }

        // ------------------------------------------------------------------ headers that will not read

        [Fact]
        public void A_header_cut_off_in_the_middle_of_its_keys_answers_nothing()
        {
            var whole = File.ReadAllBytes(WriteHeader("Midgard", 37, new[] { "nobuildcost", "nomap" }));
            var cut = Path.Combine(Root, "cut.fwl");
            File.WriteAllBytes(cut, whole[..(whole.Length - 6)]);

            Assert.Null(FwlReader.TryReadStartingKeys(cut));
        }

        [Fact]
        public void A_file_that_is_not_a_header_answers_nothing()
        {
            var junk = Path.Combine(Root, "junk.fwl");
            File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Assert.Null(FwlReader.TryReadStartingKeys(junk));
        }

        /// <summary>
        /// A count is not a promise: a header cut off mid write can claim any number of keys at
        /// all, and allocating on the strength of one is how a truncated file turns into an out
        /// of memory rather than a null.
        /// </summary>
        [Fact]
        public void A_key_count_no_file_could_hold_answers_nothing()
        {
            var path = Path.Combine(Root, "silly.fwl");
            var payload = Payload("Midgard", 37, Array.Empty<string>(), writeNeedsDb: true, writeKeys: false);
            using (var ms = new MemoryStream())
            {
                using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    bw.Write(payload.Length + 4);
                    bw.Write(payload);
                    bw.Write(int.MaxValue);          // the key count
                }
                File.WriteAllBytes(path, ms.ToArray());
            }

            Assert.Null(FwlReader.TryReadStartingKeys(path));
        }

        /// <summary>
        /// The live server holds this file open and rewrites it on every save, so the read has
        /// to share. A handle open for writing here stands in for that server.
        /// </summary>
        [Fact]
        public void The_read_shares_with_a_server_that_has_the_file_open()
        {
            var path = WriteHeader("Midgard", 37, new[] { "nomap" });

            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            Assert.Equal(new[] { "nomap" }, FwlReader.TryReadStartingKeys(path));
        }

        /// <summary>The identity read still works on a header this one walks all the way through.</summary>
        [Fact]
        public void Reading_the_keys_does_not_disturb_the_identity_read()
        {
            var path = WriteHeader("Midgard", 37, new[] { "nomap" });

            var info = FwlReader.TryRead(path);

            Assert.NotNull(info);
            Assert.Equal("Midgard", info.WorldName);
            Assert.Equal(37, info.WorldVersion);
        }

        // ------------------------------------------------------------------ building a header

        /// <summary>
        /// One header, written the way <c>World.SaveWorldFWLData</c> writes it: an int32 payload
        /// size, then the payload. Synthetic on purpose, so no real world file is ever copied
        /// into the repository.
        /// </summary>
        private string WriteHeader(
            string world, int version, IReadOnlyList<string> keys,
            string folder = null, string fileName = null,
            bool writeNeedsDb = true, bool writeKeys = true, bool playerHistory = false)
        {
            var dir = folder ?? Root;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName ?? (world + ".fwl"));

            var payload = Payload(world, version, keys, writeNeedsDb, writeKeys, playerHistory);
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(payload.Length);
                bw.Write(payload);
            }

            File.WriteAllBytes(path, ms.ToArray());
            return path;
        }

        private static byte[] Payload(
            string world, int version, IReadOnlyList<string> keys,
            bool writeNeedsDb = true, bool writeKeys = true, bool playerHistory = false)
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(version);
                bw.Write(world);
                bw.Write("SeedSeed99");
                bw.Write(123456789);              // seed
                bw.Write(987654321L);             // uid
                bw.Write(2);                      // worldGenVersion
                if (writeNeedsDb) bw.Write(true); // needsDB, from world version 30
                if (writeKeys)
                {
                    bw.Write(keys.Count);
                    foreach (var key in keys) bw.Write(key);
                }
                if (playerHistory) bw.Write(0);   // the player-history block, from world version 41
            }

            return ms.ToArray();
        }
    }
}
