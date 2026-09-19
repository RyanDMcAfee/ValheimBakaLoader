using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The world name stored INSIDE a save header, rewritten.
    /// <para>
    /// The name is the third field of a header the game wrote with a .NET BinaryWriter: a
    /// 7 bit encoded BYTE COUNT and then that many UTF-8 bytes. A new name of another
    /// length moves every byte behind it and changes the payload size the file opens
    /// with, so the whole point of these tests is the part that is easy to get wrong and
    /// impossible to see: that NOTHING else in the file moved, byte for byte.
    /// </para>
    /// <para>
    /// The headers here are built by the test in the layout both formats share, which is
    /// the layout <see cref="FwlReader"/> documents and Fwl2ReaderTests holds against a
    /// header a live Valheim 1.0 server really wrote. No save folder is touched.
    /// </para>
    /// </summary>
    public class FwlNameRewriterTests : IDisposable
    {
        private readonly string Scratch =
            Path.Combine(Path.GetTempPath(), "vbl-fwlrename-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Scratch, recursive: true); } catch { /* best effort */ }
        }

        private string Scratched(string name)
        {
            Directory.CreateDirectory(Scratch);
            return Path.Combine(Scratch, name);
        }

        /// <summary>
        /// A header in the shared layout: int32 payload size, int32 version, world name,
        /// seed name, int32 seed, int64 uid, int32 worldGenVersion, then a tail this reader
        /// never parses and this rewriter must never disturb.
        /// </summary>
        private static byte[] BuildHeader(int version, string worldName, string seedName,
            long uid = 1234567890123L, int worldGenVersion = 2)
        {
            using var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(version);
                w.Write(worldName);
                w.Write(seedName);
                w.Write(FwlWriter.GetStableHashCode(seedName));
                w.Write(uid);
                w.Write(worldGenVersion);
                w.Write(false);                      // needsDB
                w.Write(3);                          // three world modifier keys
                w.Write("deathdeleteunequipped");
                w.Write("skillreductionrate");
                w.Write("enemyleveluprate");
                w.Write(new byte[] { 7, 7, 7, 7 });  // trailing bytes nothing here reads
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

        // ------------------------------------------------------------------ round trip

        /// <summary>
        /// The whole promise in one assertion: a header with the name swapped is byte for
        /// byte the header the game would have written under that name in the first place.
        /// Anything that moved, dropped or resized a field behind the name fails here.
        /// </summary>
        [Theory]
        [InlineData(37, "Midgard", "Copy of Midgard")]      // longer
        [InlineData(37, "Midgard", "M")]                    // shorter
        [InlineData(37, "Midgard", "Midgard")]              // the same length
        [InlineData(41, "BakaLegacy", "BakaLegacy backup")] // a 1.0 header
        public void A_rewritten_header_is_the_header_the_game_would_have_written(
            int version, string from, string to)
        {
            var original = BuildHeader(version, from, "SeedSeed99");
            var expected = BuildHeader(version, to, "SeedSeed99");

            Assert.Equal(expected, FwlNameRewriter.Rebuild(original, to));
        }

        /// <summary>
        /// And the fields the rewrite is not about survive a read: the seed the world was
        /// generated from above all, which is the one thing about a world that can never be
        /// changed and the one thing a copy has to keep.
        /// </summary>
        [Theory]
        [InlineData(37, ".fwl", WorldFormat.Legacy)]
        [InlineData(41, ".fwl2", WorldFormat.Chunked)]
        public void The_seed_and_the_uid_come_back_unchanged_beside_the_new_name(
            int version, string extension, WorldFormat format)
        {
            var path = Scratched("_main.1" + extension);
            File.WriteAllBytes(path, BuildHeader(version, "Midgard", "SeedSeed99", uid: 8877665544332211L));

            var before = FwlReader.TryRead(path);
            Assert.NotNull(before);

            Assert.True(FwlNameRewriter.TryRewriteWorldName(path, "Second Midgard"));

            var after = FwlReader.TryRead(path);
            Assert.NotNull(after);
            Assert.Equal("Second Midgard", after.WorldName);
            Assert.Equal(before.SeedName, after.SeedName);
            Assert.Equal(before.Seed, after.Seed);
            Assert.Equal(before.Uid, after.Uid);
            Assert.Equal(before.WorldVersion, after.WorldVersion);
            Assert.Equal(before.WorldGenVersion, after.WorldGenVersion);
            Assert.Equal(format, after.Format);
        }

        /// <summary>
        /// The leading int32 is the payload size, which is the file's own length minus the
        /// four bytes that hold it. A rewrite that moved the bytes and left the number is a
        /// file the game reads the wrong length of.
        /// </summary>
        [Fact]
        public void The_payload_size_follows_the_new_name()
        {
            var path = Scratched("Midgard.fwl");
            File.WriteAllBytes(path, BuildHeader(37, "Midgard", "SeedSeed99"));

            Assert.True(FwlNameRewriter.TryRewriteWorldName(path, "A very much longer world name"));

            using var reader = new BinaryReader(File.OpenRead(path));
            Assert.Equal(new FileInfo(path).Length - 4, reader.ReadInt32());
        }

        /// <summary>
        /// The length prefix counts BYTES, not characters, so a name outside ASCII is the
        /// case a character count gets wrong in a way that only shows up on somebody else's
        /// machine.
        /// </summary>
        [Fact]
        public void A_name_outside_ascii_is_measured_in_bytes()
        {
            var path = Scratched("Midgard.fwl");
            File.WriteAllBytes(path, BuildHeader(37, "Midgard", "SeedSeed99"));

            Assert.True(FwlNameRewriter.TryRewriteWorldName(path, "バカの世界"));

            var read = FwlReader.TryRead(path);
            Assert.NotNull(read);
            Assert.Equal("バカの世界", read.WorldName);
            Assert.Equal("SeedSeed99", read.SeedName);
        }

        /// <summary>
        /// Past 127 bytes the length prefix takes a second byte, which moves everything
        /// behind it by one more than the name itself grew. Below that boundary a rewrite
        /// that forgot the prefix could still pass.
        /// </summary>
        [Fact]
        public void A_name_past_the_one_byte_length_prefix_still_round_trips()
        {
            var longName = new string('w', 200);
            var original = BuildHeader(37, "Midgard", "SeedSeed99");

            var wide = FwlNameRewriter.Rebuild(original, longName);
            Assert.Equal(BuildHeader(37, longName, "SeedSeed99"), wide);

            // And back down again, which is the same boundary crossed the other way.
            Assert.Equal(original, FwlNameRewriter.Rebuild(wide, "Midgard"));
        }

        // ------------------------------------------------------------------ refusals

        /// <summary>
        /// A file that is not a header is answered with false and left exactly as it was. A
        /// rewrite that guessed would leave a world that cannot be opened and no way back.
        /// </summary>
        [Fact]
        public void A_file_that_is_not_a_header_is_left_alone()
        {
            var path = Scratched("Broken.fwl");
            var rubbish = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 };
            File.WriteAllBytes(path, rubbish);

            Assert.False(FwlNameRewriter.TryRewriteWorldName(path, "Anything"));
            Assert.Equal(rubbish, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".renaming"));
        }

        [Fact]
        public void A_header_that_is_not_there_is_answered_with_false()
        {
            Assert.False(FwlNameRewriter.TryRewriteWorldName(Scratched("Gone.fwl"), "Anything"));
            Assert.False(FwlNameRewriter.TryRewriteWorldName(null, "Anything"));
            Assert.False(FwlNameRewriter.TryRewriteWorldName("   ", "Anything"));
        }

        [Fact]
        public void A_name_that_is_nothing_is_refused()
        {
            var original = BuildHeader(37, "Midgard", "SeedSeed99");
            Assert.Null(FwlNameRewriter.Rebuild(original, null));
            Assert.Null(FwlNameRewriter.Rebuild(null, "Anything"));
        }

        /// <summary>
        /// The leading size has to be the file's own. A file that was truncated part way
        /// through a write still opens with the size it MEANT to be, and rewriting it would
        /// move a tail that is not all there.
        /// </summary>
        [Fact]
        public void A_header_whose_size_disagrees_with_its_length_is_refused()
        {
            var original = BuildHeader(37, "Midgard", "SeedSeed99");
            var truncated = original.Take(original.Length - 6).ToArray();

            Assert.Null(FwlNameRewriter.Rebuild(truncated, "Anything"));
        }

        /// <summary>
        /// A world version outside any plausible range says the four bytes behind the size
        /// are not a version, so what follows them is not a name either.
        /// </summary>
        [Fact]
        public void A_header_with_an_implausible_version_is_refused()
        {
            var original = BuildHeader(37, "Midgard", "SeedSeed99");
            original[4] = 0xFF;
            original[5] = 0xFF;
            original[6] = 0xFF;
            original[7] = 0x7F;

            Assert.Null(FwlNameRewriter.Rebuild(original, "Anything"));
        }

        /// <summary>
        /// The seed name has to parse as well. Without that second read, any file whose
        /// ninth byte happened to be a small number would be rewritten as though the bytes
        /// behind it were a world name.
        /// </summary>
        [Fact]
        public void A_header_whose_seed_name_does_not_parse_is_refused()
        {
            // int32 size, int32 version, a name that claims every remaining byte, and so
            // nothing left for a seed name to sit in.
            using var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(37);
                w.Write("Midgard");
            }
            var bytes = payload.ToArray();

            using var file = new MemoryStream();
            using (var w = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(bytes.Length);
                w.Write(bytes);
            }

            Assert.Null(FwlNameRewriter.Rebuild(file.ToArray(), "Anything"));
        }

        /// <summary>
        /// A name of nothing is refused the way a null one is. The bytes would rebuild
        /// and parse and the file would open, and what would come back out of it is a
        /// world with no name at all: a header the caller cannot have meant to ask for.
        /// The product never asks, because both callers gate on the name first, which is
        /// exactly why the byte level entry point has to answer for itself.
        /// </summary>
        [Fact]
        public void A_name_of_nothing_is_refused_the_way_a_null_one_is()
        {
            var original = BuildHeader(37, "Midgard", "seedy");

            Assert.Null(FwlNameRewriter.Rebuild(original, null));
            Assert.Null(FwlNameRewriter.Rebuild(original, string.Empty));

            // And the file level call refuses it too, leaving the file exactly as it was.
            var path = Scratched("Midgard.fwl");
            File.WriteAllBytes(path, original);
            Assert.False(FwlNameRewriter.TryRewriteWorldName(path, string.Empty));
            Assert.Equal(original, File.ReadAllBytes(path));
        }

        /// <summary>
        /// A length prefix is five bytes of somebody else's data, and it is free to spell a
        /// byte count of two billion. Added to the offset it starts at, in int, that count
        /// wraps past zero: the bound that is supposed to stop it then reads as a number
        /// smaller than the file, passes, and the offset goes back to the reader to index
        /// with. The answer to a header this cannot read is null, the way it is for every
        /// other shape it cannot read. A throw here reaches the copy through a call that
        /// sits outside its own catch.
        /// </summary>
        [Fact]
        public void A_name_length_that_wraps_an_int_is_refused_rather_than_thrown()
        {
            using var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(37);
                // int.MaxValue, spelled the way a BinaryWriter spells a length.
                w.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07 });
                w.Write(new byte[20]);
            }
            var bytes = payload.ToArray();

            using var file = new MemoryStream();
            using (var w = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(bytes.Length);
                w.Write(bytes);
            }
            var header = file.ToArray();

            Assert.Null(FwlNameRewriter.Rebuild(header, "NewName"));

            // And the file level entry point answers false and leaves the bytes alone,
            // rather than throwing out of a copy that is holding staged files.
            var path = Scratched("Wrapped.fwl");
            File.WriteAllBytes(path, header);
            Assert.False(FwlNameRewriter.TryRewriteWorldName(path, "NewName"));
            Assert.Equal(header, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".renaming"));
        }

        // ------------------------------------------------------------------ the payload

        /// <summary>
        /// The leading size measures the PAYLOAD, and a file is allowed to carry bytes behind
        /// that payload rather than end exactly on it. What sits past the payload is carried
        /// across untouched, because nothing here knows what it is.
        /// </summary>
        [Fact]
        public void A_header_carrying_bytes_past_its_payload_is_rewritten_and_keeps_them()
        {
            var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
            var original = BuildHeader(37, "Midgard", "SeedSeed99").Concat(junk).ToArray();

            var rebuilt = FwlNameRewriter.Rebuild(original, "Second Midgard");
            Assert.NotNull(rebuilt);
            Assert.Equal(BuildHeader(37, "Second Midgard", "SeedSeed99").Concat(junk).ToArray(), rebuilt);

            // And through the file, which is the way the copy reaches it.
            var path = Scratched("Trailing.fwl");
            File.WriteAllBytes(path, original);
            Assert.True(FwlNameRewriter.TryRewriteWorldName(path, "Second Midgard"));

            var read = FwlReader.TryRead(path);
            Assert.NotNull(read);
            Assert.Equal("Second Midgard", read.WorldName);
            Assert.Equal("SeedSeed99", read.SeedName);
            Assert.Equal(junk, File.ReadAllBytes(path).TakeLast(junk.Length).ToArray());
        }

        /// <summary>The same header with another number written into its leading size field.</summary>
        private static byte[] WithDeclaredPayloadSize(byte[] header, int size)
        {
            var copy = (byte[])header.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(0, 4), size);
            return copy;
        }

        /// <summary>
        /// The band where the rewrite is stricter than the copy's own pre-check, pinned rather
        /// than wished away. The pre-check reader, FwlReader.TryRead, turns a leading size down
        /// only when it is past the file's LENGTH; this turns it down when it is past the
        /// file's PAYLOAD, four bytes shorter. So a header declaring one, two, three or four
        /// bytes more payload than the file can hold is read for its name and its seed, passes
        /// the copy's pre-check, and is then refused by the rewrite.
        /// <para>
        /// That is the intended answer, not a gap to close: a tail measured past the last byte
        /// there is cannot be carried across, and a header rebuilt around a size that was never
        /// true is a world that will not open. What matters is that it fails CLOSED, which is
        /// what the assertions below hold it to: the file is byte for byte as it was and no
        /// staging file is left behind.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void A_size_overshooting_the_payload_passes_the_precheck_and_is_refused_by_the_rewrite(int overshoot)
        {
            var original = WithDeclaredPayloadSize(
                BuildHeader(37, "Midgard", "SeedSeed99"),
                BuildHeader(37, "Midgard", "SeedSeed99").Length - 4 + overshoot);

            var path = Scratched("Overshoot" + overshoot + ".fwl");
            File.WriteAllBytes(path, original);

            // The copy's pre-check reads it, which is what lets the copy get as far as the
            // rewrite at all.
            var precheck = FwlReader.TryRead(path);
            Assert.NotNull(precheck);
            Assert.Equal("Midgard", precheck.WorldName);
            Assert.Equal("SeedSeed99", precheck.SeedName);

            // And the rewrite says no, leaving the file exactly as it found it.
            Assert.Null(FwlNameRewriter.Rebuild(original, "Second Midgard"));
            Assert.False(FwlNameRewriter.TryRewriteWorldName(path, "Second Midgard"));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".renaming"));
        }

        /// <summary>
        /// One band further out, where the two readers agree again: a size five bytes past the
        /// payload is past the file's length as well, so the copy's pre-check turns it down too
        /// and the copy is answered "could not be read" before a byte moves. The world is still
        /// LISTED and is still offered a copy, because the list names a world by its file and
        /// never opens its header; only the answer the host gets changes.
        /// </summary>
        [Fact]
        public void A_size_past_the_files_length_is_refused_by_both()
        {
            var original = BuildHeader(37, "Midgard", "SeedSeed99");
            var overshot = WithDeclaredPayloadSize(original, original.Length + 1);

            var path = Scratched("PastLength.fwl");
            File.WriteAllBytes(path, overshot);

            Assert.Null(FwlReader.TryRead(path));
            Assert.Null(FwlNameRewriter.Rebuild(overshot, "Second Midgard"));
        }

        /// <summary>
        /// The other side of the same difference. Every read here is bounded by the payload the
        /// header declares, while the pre-check reader takes the name and the seed name straight
        /// out of the file. A header declaring a payload too small to hold its own name passes
        /// the pre-check and is refused, for the same reason and with the same nothing left
        /// behind.
        /// </summary>
        [Fact]
        public void A_payload_too_small_to_hold_the_name_passes_the_precheck_and_is_refused_by_the_rewrite()
        {
            // Eight bytes of payload: the version and four bytes more, which stops inside
            // the name however short the name is.
            var original = WithDeclaredPayloadSize(BuildHeader(37, "Midgard", "SeedSeed99"), 8);

            var path = Scratched("ShortPayload.fwl");
            File.WriteAllBytes(path, original);

            var precheck = FwlReader.TryRead(path);
            Assert.NotNull(precheck);
            Assert.Equal("Midgard", precheck.WorldName);

            Assert.Null(FwlNameRewriter.Rebuild(original, "Second Midgard"));
            Assert.False(FwlNameRewriter.TryRewriteWorldName(path, "Second Midgard"));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".renaming"));
        }

        /// <summary>A header too short to hold a size and a version at all.</summary>
        [Fact]
        public void A_header_shorter_than_its_own_first_two_fields_is_refused()
        {
            Assert.Null(FwlNameRewriter.Rebuild(new byte[] { 1, 2, 3, 4 }, "Anything"));
            Assert.Null(FwlNameRewriter.Rebuild(Array.Empty<byte>(), "Anything"));
        }

        /// <summary>
        /// The words around this rewrite, held to the same bar as the code. Explaining how
        /// strict the rewrite is means naming what it is stricter THAN, and twice that has
        /// been written down as the world list, which never opens a header: what is four bytes
        /// more forgiving is FwlReader.TryRead, the reader WorldStore.CopyWorldAs calls as the
        /// copy's pre-check. The behaviour was right both times and the sentence was wrong both
        /// times, and a sentence cannot fail a suite on its own.
        /// <para>
        /// So it fails here. The comparison has to be spelled against the pre-check, and the
        /// phrasings that made the claim before are turned away by name. This is cheap and it
        /// is narrow on purpose: it pins the one sentence that has drifted twice, rather than
        /// trying to read English in general.
        /// </para>
        /// </summary>
        [Fact]
        public void The_rewrites_strictness_is_explained_against_the_copys_precheck_not_the_world_list()
        {
            var source = AppSourceTree.Files()["FwlReader.cs"];

            // Named, so a reader can go and look at the thing being compared against.
            Assert.Contains("the copy's own pre-check", source);
            Assert.Contains("WorldStore.CopyWorldAs", source);

            // And the list is described as what it is, rather than as a reader of headers.
            Assert.Contains("which never opens a header at all", source);

            foreach (var claim in new[]
                     {
                         "reader the world list answers from",
                         "stricter than the world list",
                         "more forgiving than the world list",
                         "the list's reader",
                         "the world list will show",
                         "the list will show",
                     })
            {
                Assert.False(source.Contains(claim, StringComparison.OrdinalIgnoreCase),
                    "FwlReader.cs says \"" + claim + "\". The world list never opens a header: "
                    + "WorldStore Enumerate, Find and GetWorldNames name a world by its file or "
                    + "its directory. What the rewrite is stricter than is FwlReader.TryRead, the "
                    + "copy's pre-check. Compare against that instead.");
            }
        }
    }
}
