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
    /// The world uid, and the rewrite that gives a copy one of its own.
    /// <para>
    /// WHY THIS EXISTS. Duplicate copied a world's header byte for byte and changed only the
    /// name, so the copy carried the SOURCE's uid. The uid is how the game tells two worlds
    /// apart: a character file keys its map exploration and its pins on it, so the copy came
    /// up with the source's map already drawn on it and every pin either world gained after
    /// that appeared on both. Two worlds, one identity.
    /// </para>
    /// <para>
    /// These read and write bytes rather than files wherever they can, because the field is
    /// eight bytes at a place that MOVES with the length of the two names in front of it.
    /// A header built by hand with a one-letter name and one with a long one both have to
    /// land on the same field, and a test that only ever drove the real save folder would
    /// pass with the offset hard-coded for whatever names the fixture happened to use.
    /// </para>
    /// </summary>
    public class FwlUidRewriterTests
    {
        /// <summary>
        /// A header in the layout the game writes: the payload size, the world version, the
        /// two length-prefixed strings, the seed, the uid, and the tail.
        /// </summary>
        private static byte[] BuildHeader(int version, string worldName, string seedName, long uid)
        {
            using var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(version);
                w.Write(worldName);
                w.Write(seedName);
                w.Write(FwlWriter.GetStableHashCode(seedName));
                w.Write(uid);
                w.Write(2);        // worldGenVersion
                w.Write(false);    // needsDB
                w.Write(0);        // no starting keys
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

        // ------------------------------------------------------------------ finding the field

        [Fact]
        public void The_uid_is_read_back_out_of_a_header_it_was_written_into()
        {
            var header = BuildHeader(37, "Midgard", "seedy", 1234567890123L);

            Assert.True(FwlUidRewriter.TryReadUid(header, out var uid));
            Assert.Equal(1234567890123L, uid);
        }

        /// <summary>
        /// The field moves with the names in front of it, so the offset is walked rather than
        /// counted. Both layouts write the same fields in the same order, which is why one
        /// rewrite serves the pre-1.0 ".fwl" and the 1.0 ".fwl2" alike.
        /// </summary>
        [Theory]
        [InlineData("A", "s")]
        [InlineData("Midgard", "chunkyseed")]
        [InlineData("A world with a rather long name indeed", "sd1234567890")]
        [InlineData("Jotunheimr", "")]
        public void The_field_is_found_however_long_the_names_in_front_of_it_are(string name, string seed)
        {
            var header = BuildHeader(41, name, seed, -42L);

            Assert.True(FwlUidRewriter.TryReadUid(header, out var read));
            Assert.Equal(-42L, read);

            var rebuilt = FwlUidRewriter.Rebuild(header, 777L);
            Assert.NotNull(rebuilt);
            Assert.True(FwlUidRewriter.TryReadUid(rebuilt, out var after));
            Assert.Equal(777L, after);
        }

        /// <summary>
        /// The uid is fixed width, so nothing else in the file may move. Eight bytes differ
        /// and every other byte is the one it was, the tail included.
        /// </summary>
        [Fact]
        public void Nothing_but_the_eight_bytes_of_the_uid_changes()
        {
            // Two uids that share no byte at all, so "eight differ" is a statement about
            // the field's width rather than about the numbers that happened to be picked.
            var header = BuildHeader(37, "Midgard", "seedy", 0x0101010101010101L);
            var rebuilt = FwlUidRewriter.Rebuild(header, 0x0202020202020202L);

            Assert.Equal(header.Length, rebuilt.Length);
            var differing = Enumerable.Range(0, header.Length).Where(i => rebuilt[i] != header[i]).ToList();
            Assert.Equal(8, differing.Count);
            // And they are eight in a row: the field moved, nothing around it did.
            Assert.Equal(differing.First() + 7, differing.Last());

            // And the identity the game reads out of it is otherwise untouched.
            var before = ReadThrough(header);
            var after = ReadThrough(rebuilt);
            Assert.Equal(before.WorldName, after.WorldName);
            Assert.Equal(before.SeedName, after.SeedName);
            Assert.Equal(before.Seed, after.Seed);
            Assert.Equal(before.WorldVersion, after.WorldVersion);
            Assert.Equal(before.WorldGenVersion, after.WorldGenVersion);
            Assert.Equal(0x0202020202020202L, after.Uid);
        }

        /// <summary>FwlReader over a byte array, through a temp file, which is all it reads.</summary>
        private static FwlWorldInfo ReadThrough(byte[] header)
        {
            var path = Path.Combine(Path.GetTempPath(), "vbl-uid-" + Guid.NewGuid().ToString("N") + ".fwl");
            try
            {
                File.WriteAllBytes(path, header);
                return FwlReader.TryRead(path);
            }
            finally
            {
                try { File.Delete(path); } catch { /* temp */ }
            }
        }

        // ------------------------------------------------------------------ what it refuses

        [Fact]
        public void Bytes_that_are_not_a_header_are_refused_rather_than_guessed_at()
        {
            Assert.Null(FwlUidRewriter.Rebuild(null, 1));
            Assert.Null(FwlUidRewriter.Rebuild(Array.Empty<byte>(), 1));
            Assert.Null(FwlUidRewriter.Rebuild(new byte[11], 1));
            // A declared payload of zero is not a payload.
            Assert.Null(FwlUidRewriter.Rebuild(new byte[] { 0, 0, 0, 0, 37, 0, 0, 0, 1, 65, 0, 0 }, 1));
        }

        /// <summary>
        /// A header whose declared payload runs past the end of the file is one whose own
        /// size field is contradicted by the file it sits in, and rewriting it would mean
        /// measuring by a number that was never true.
        /// </summary>
        [Fact]
        public void A_payload_that_overshoots_the_file_is_refused()
        {
            var header = BuildHeader(37, "Midgard", "seedy", 5L);
            header[0] = (byte)(header[0] + 8);   // claim eight more bytes than there are

            Assert.Null(FwlUidRewriter.Rebuild(header, 9));
            Assert.False(FwlUidRewriter.TryReadUid(header, out _));
        }

        /// <summary>
        /// A header that parses as far as the seed name and then stops has NO uid in it to
        /// be sharing, so a copy of it is allowed to go ahead untouched. That is the one
        /// place the copy's rule is softer than the rewrite's, and it is deliberate: refusing
        /// the copy would lose a host their world over a field that is not there.
        /// </summary>
        [Fact]
        public void A_header_with_no_room_for_a_uid_is_left_alone_rather_than_refusing_the_copy()
        {
            // The payload is cut to end right after the seed, leaving no uid behind it.
            var full = BuildHeader(37, "M", "s", 5L);
            var cut = 4 + 4 + 2 + 2 + 4;                  // size, version, "M", "s", seed
            var payload = full.Skip(4).Take(cut - 4).ToArray();

            using var file = new MemoryStream();
            using (var w = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(payload.Length);
                w.Write(payload);
            }

            var header = file.ToArray();
            Assert.Null(FwlUidRewriter.Rebuild(header, 7));

            var path = Path.Combine(Path.GetTempPath(), "vbl-uid-" + Guid.NewGuid().ToString("N") + ".fwl");
            try
            {
                File.WriteAllBytes(path, header);

                // The strict rewrite says no, because there is nothing there to write.
                Assert.False(FwlUidRewriter.TryRewriteWorldUid(path, 7));
                // The copy's rule says yes, and leaves the file exactly as it was.
                Assert.True(FwlUidRewriter.TryGiveOwnUid(path, 7));
                Assert.Equal(header, File.ReadAllBytes(path));
            }
            finally
            {
                try { File.Delete(path); } catch { /* temp */ }
            }
        }

        /// <summary>
        /// A header whose uid is IN THE FILE but outside the payload the header declares is
        /// the shape the copy's softer rule must not wave through.
        /// <para>
        /// The rewrite refuses it, for the same reason it refuses any header it cannot
        /// measure: the eight bytes it would write are past the end of what the header says
        /// its own payload is. What the copy must not then do is answer "fine, carry on",
        /// because the bytes ARE there, they hold the SOURCE's uid, and the copy would go out
        /// wearing it. That is the one outcome the whole rewrite exists to stop, and it is a
        /// worse one than refusing the copy: a host who is refused still has both worlds.
        /// </para>
        /// <para>
        /// It is a different shape from the one above, where the file simply stops before the
        /// field. There is no uid in that one for the copy to be sharing; there is in this one.
        /// </para>
        /// </summary>
        [Fact]
        public void A_uid_the_payload_does_not_reach_but_the_file_holds_refuses_the_copy()
        {
            var header = BuildHeader(37, "Midgard", "seedy", 1234567890123L);

            // The same bytes, with the header's own size field pulled back so the payload it
            // declares ends before the uid does. Nothing is removed: the eight bytes holding
            // 1234567890123 are still in the file, which is the whole point.
            var declared = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), declared - 12);

            Assert.Null(FwlUidRewriter.Rebuild(header, 7));
            Assert.False(FwlUidRewriter.TryReadUid(header, out _));

            var folder = Path.Combine(Path.GetTempPath(), "vbl-uid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "Midgard.fwl");

            try
            {
                File.WriteAllBytes(path, header);

                Assert.False(FwlUidRewriter.TryRewriteWorldUid(path, 7));
                // And the copy's rule says no as well, which is the line this test is for.
                Assert.False(FwlUidRewriter.TryGiveOwnUid(path, 7));

                // Refused and untouched: the source's uid is still in there, unwritten over.
                Assert.Equal(header, File.ReadAllBytes(path));
                Assert.Single(Directory.GetFiles(folder));
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { /* temp */ }
            }
        }

        [Fact]
        public void A_file_that_is_not_there_is_a_no_for_both_rules()
        {
            var missing = Path.Combine(Path.GetTempPath(), "vbl-uid-missing-" + Guid.NewGuid().ToString("N") + ".fwl");

            Assert.False(FwlUidRewriter.TryRewriteWorldUid(missing, 1));
            Assert.False(FwlUidRewriter.TryGiveOwnUid(missing, 1));
            Assert.False(FwlUidRewriter.TryRewriteWorldUid(null, 1));
            Assert.False(FwlUidRewriter.TryRewriteWorldUid("   ", 1));
        }

        // ------------------------------------------------------------------ the new uid itself

        /// <summary>
        /// Never zero, because that is what FwlReader answers with for a header it could not
        /// follow as far as the uid: a real uid must not be mistakable for "there was nothing
        /// there". And not the same one twice, or every copy would share an identity with
        /// every other copy instead of with its source.
        /// </summary>
        [Fact]
        public void A_new_uid_is_never_zero_and_not_the_same_one_twice()
        {
            var seen = new System.Collections.Generic.HashSet<long>();

            for (var i = 0; i < 200; i++)
            {
                var uid = FwlUidRewriter.NewUid();
                Assert.NotEqual(0L, uid);
                seen.Add(uid);
            }

            // Two hundred draws out of the whole of int64: a repeat here is not chance.
            Assert.Equal(200, seen.Count);

            // And the WHOLE of int64, not the non-negative half of it. The game writes a
            // signed field and real worlds carry both signs; a draw that could only ever be
            // positive threw away a bit of the space for nothing. Two hundred draws with no
            // negative among them is not chance either.
            Assert.Contains(seen, uid => uid < 0);
            Assert.Contains(seen, uid => uid > 0);
        }

        // ------------------------------------------------- a header the game itself wrote

        /// <summary>
        /// "_main.1.fwl2" lifted out of a world a live Valheim 1.0 dedicated server wrote
        /// (worldVersion 41), the same capture the reader tests are held against.
        /// </summary>
        private static string RealFwl2Path =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "BakaLegacy_main.1.fwl2");

        /// <summary>
        /// The rewrite driven over a header the GAME wrote, rather than over one built here.
        /// <para>
        /// Everything above builds its own bytes, which is what makes the offset walk worth
        /// asserting: the field moves with the names in front of it. But a builder can agree
        /// with a reader about a layout they are both wrong about, and then every test passes
        /// and every real world is refused. This one starts from the file itself.
        /// </para>
        /// </summary>
        [Fact]
        public void A_real_captured_game_header_goes_through_the_rewrite_whole()
        {
            Assert.True(File.Exists(RealFwl2Path), "Missing test resource: " + RealFwl2Path);

            var original = File.ReadAllBytes(RealFwl2Path);

            Assert.True(FwlUidRewriter.TryReadUid(original, out var was));
            Assert.NotEqual(0L, was);

            var rebuilt = FwlUidRewriter.Rebuild(original, -8877665544332211L);
            Assert.NotNull(rebuilt);

            // Eight bytes in a row and not one more, in a file the game laid out.
            Assert.Equal(original.Length, rebuilt.Length);
            var differing = Enumerable.Range(0, original.Length)
                .Where(i => rebuilt[i] != original[i]).ToList();
            Assert.Equal(8, differing.Count);
            Assert.Equal(differing.First() + 7, differing.Last());

            // And the game's own fields come back out of it, the new uid included.
            var before = ReadThrough(original);
            var after = ReadThrough(rebuilt);

            Assert.Equal("BakaLegacy", after.WorldName);
            Assert.Equal("SeedSeed99", after.SeedName);
            Assert.Equal(before.Seed, after.Seed);
            Assert.Equal(41, after.WorldVersion);
            Assert.Equal(before.WorldGenVersion, after.WorldGenVersion);
            Assert.Equal(was, before.Uid);
            Assert.Equal(-8877665544332211L, after.Uid);
        }

        /// <summary>
        /// And the same capture through the rule a COPY follows, on disk, in a scratch folder
        /// of its own. The fixture itself is never written to.
        /// </summary>
        [Fact]
        public void A_real_captured_game_header_takes_a_new_uid_on_disk()
        {
            Assert.True(File.Exists(RealFwl2Path), "Missing test resource: " + RealFwl2Path);

            var folder = Path.Combine(Path.GetTempPath(), "vbl-uid-real-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "_main.1.fwl2");

            try
            {
                File.Copy(RealFwl2Path, path);
                var before = FwlReader.TryRead(path);

                var fresh = FwlUidRewriter.NewUid();
                Assert.True(FwlUidRewriter.TryGiveOwnUid(path, fresh));

                var after = FwlReader.TryRead(path);
                Assert.Equal(fresh, after.Uid);
                Assert.NotEqual(before.Uid, after.Uid);
                Assert.Equal(before.WorldName, after.WorldName);
                Assert.Equal(before.SeedName, after.SeedName);
                Assert.Equal(before.Seed, after.Seed);

                // Nothing left behind under the staging name.
                Assert.Single(Directory.GetFiles(folder));

                // And the capture in the test resources is exactly as it was: the copy in
                // the scratch folder was rewritten and the fixture beside it was not.
                Assert.True(FwlUidRewriter.TryReadUid(File.ReadAllBytes(RealFwl2Path), out var fixtureUid));
                Assert.Equal(before.Uid, fixtureUid);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { /* temp */ }
            }
        }

        /// <summary>
        /// The rewrite lands on disk, and the file it leaves is the rebuilt one rather than
        /// the staging name it was written under.
        /// </summary>
        [Fact]
        public void The_rewrite_replaces_the_file_and_leaves_no_staging_behind()
        {
            var folder = Path.Combine(Path.GetTempPath(), "vbl-uid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "Midgard.fwl");

            try
            {
                File.WriteAllBytes(path, BuildHeader(37, "Midgard", "seedy", 1234567890123L));

                Assert.True(FwlUidRewriter.TryRewriteWorldUid(path, -7L));

                Assert.Equal(-7L, FwlReader.TryRead(path).Uid);
                Assert.Equal("Midgard", FwlReader.TryRead(path).WorldName);
                Assert.Single(Directory.GetFiles(folder));
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { /* temp */ }
            }
        }
    }
}
