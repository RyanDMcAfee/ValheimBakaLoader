using System.IO;
using System.Text;
using System.Threading.Tasks;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// What survives the trip down the RCON socket and back.
    /// <para>
    /// Both ends wrote and read packet bodies as ASCII until 1.1.0, and ASCII has no room
    /// for any letter past the first 128: every one of them came out as a question mark.
    /// That was not a cosmetic loss. A player name in Cyrillic, Japanese or Chinese came
    /// back from playerlist as "???", and the name BakaLoader then sent back in a spawn, a
    /// tp or a kick was the question marks, which match nobody. Broadcasts and the restart
    /// countdown went out the same way and reached the world unreadable.
    /// </para>
    /// <para>
    /// These drive the app's own packet writer and reader over a plain memory stream, so
    /// nothing here opens a socket or needs a server. Each one fails against the encoding
    /// as it stood before the fix.
    /// </para>
    /// </summary>
    public class RconUtf8Tests
    {
        private const int Exec = 2;

        private static async Task<RconClient.RconPacket> RoundTrip(int id, int type, string body)
        {
            var wire = new MemoryStream();
            await RconClient.SendPacketAsync(wire, id, type, body);
            wire.Position = 0;
            return await RconClient.ReadPacketAsync(wire);
        }

        [Theory]
        [InlineData("Смитикс")]                          // Cyrillic
        [InlineData("スミティックス")]                    // Japanese
        [InlineData("鍛冶屋")]                            // Chinese
        [InlineData("Смитикс と 鍛冶屋 and Van Hoenhiem")] // all three beside plain English
        [InlineData("kick Смитикс")]
        [InlineData("broadcast center Сервер перезапустится через 5 минут")]
        public async Task A_body_in_any_alphabet_comes_back_exactly_as_it_went_in(string body)
        {
            var packet = await RoundTrip(7, Exec, body);

            Assert.NotNull(packet);
            Assert.Equal(body, packet.Body);
            Assert.Equal(7, packet.Id);
            Assert.Equal(Exec, packet.Type);
            // The thing the old encoding actually did.
            Assert.DoesNotContain("?", packet.Body);
        }

        /// <summary>
        /// The frame's length field counts BYTES. A letter outside the first 128 is two or
        /// more of them, so a length worked out from the character count would leave the
        /// reader on the other end short and every packet after it misaligned.
        /// </summary>
        [Fact]
        public async Task The_length_field_counts_bytes_and_not_letters()
        {
            const string body = "鍛冶屋";          // three characters, nine UTF-8 bytes
            var expectedBytes = Encoding.UTF8.GetByteCount(body);
            Assert.Equal(9, expectedBytes);
            Assert.Equal(3, body.Length);

            var wire = new MemoryStream();
            await RconClient.SendPacketAsync(wire, 1, Exec, body);
            var frame = wire.ToArray();

            var length = System.BitConverter.ToInt32(frame, 0);

            // id(4) + type(4) + body + body terminator + packet terminator
            Assert.Equal(4 + 4 + expectedBytes + 2, length);
            Assert.Equal(4 + length, frame.Length);
        }

        /// <summary>
        /// Plain English is byte for byte what ASCII wrote, so every server and every
        /// command that worked before this change still works exactly the same.
        /// </summary>
        [Fact]
        public async Task Plain_english_is_written_byte_for_byte_the_way_it_always_was()
        {
            const string body = "playerlist";

            var wire = new MemoryStream();
            await RconClient.SendPacketAsync(wire, 3, Exec, body);
            var frame = wire.ToArray();

            var written = new byte[body.Length];
            System.Buffer.BlockCopy(frame, 12, written, 0, body.Length);

            Assert.Equal(Encoding.ASCII.GetBytes(body), written);
        }

        [Fact]
        public async Task An_empty_body_still_round_trips()
        {
            var packet = await RoundTrip(9, Exec, "");

            Assert.NotNull(packet);
            Assert.Equal("", packet.Body);
            Assert.Equal(9, packet.Id);
        }

        /// <summary>
        /// A body written with no byte-order mark. A BOM at the front of every packet would
        /// be three stray bytes the other end reads as part of the command text.
        /// </summary>
        [Fact]
        public async Task Nothing_writes_a_byte_order_mark()
        {
            var wire = new MemoryStream();
            await RconClient.SendPacketAsync(wire, 1, Exec, "save");
            var frame = wire.ToArray();

            Assert.Equal((byte)'s', frame[12]);
            Assert.Equal(0, RconClient.BodyEncoding.GetPreamble().Length);
        }

        /// <summary>
        /// A gate on the app's own source: the two body lines are the ones that were wrong,
        /// and a line that puts ASCII back on either of them is what this catches.
        /// </summary>
        [Fact]
        public void The_apps_rcon_body_paths_carry_no_ascii_encoding()
        {
            var source = AppSourceTree.Read("ValheimBakaLoader", "Tools", "RconClient.cs");
            var code = WithoutComments(source);

            Assert.DoesNotContain("Encoding.ASCII", code);
            Assert.Contains("new UTF8Encoding(false)", code);
            Assert.Contains("BodyEncoding.GetBytes(body", code);
            Assert.Contains("BodyEncoding.GetString(payload", code);
        }

        // ---- the split that must never cut a letter in half ----

        /// <summary>
        /// Commander's own chunk walk, copied here letter for letter. The plugin compiles
        /// against the game's assemblies and cannot be called from this solution, so the
        /// algorithm is driven here and the gate below pins the copy to the shipped source:
        /// change one without the other and that gate fails.
        /// </summary>
        private static int SafeChunkLength(byte[] bytes, int offset, int wanted)
        {
            if (wanted <= 0) return 0;
            if (offset + wanted >= bytes.Length) return wanted;

            var len = wanted < 4 ? 4 : wanted;
            if (offset + len >= bytes.Length) return bytes.Length - offset;

            var walked = 0;
            while (len > 1 && walked < 3 && (bytes[offset + len] & 0xC0) == 0x80)
            {
                len--;
                walked++;
            }

            return len;
        }

        /// <summary>
        /// Every chunk has to stand on its own: the client decodes each packet as it arrives,
        /// so a cut inside a letter hands it half of one and the letter is lost on both
        /// sides of the seam. Driven at the shipped cap and at caps far below it, over text
        /// that mixes one, two, three and four byte letters, because the walk that holds at
        /// 4000 is exactly the walk a smaller cap leans on hardest.
        /// </summary>
        [Theory]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(7)]
        [InlineData(37)]
        [InlineData(4000)]
        [InlineData(2)]     // below the longest letter: the chunk grows rather than cutting one
        [InlineData(3)]
        public void No_chunk_of_a_long_answer_ever_ends_inside_a_letter(int cap)
        {
            var text = new StringBuilder();
            for (var i = 0; i < 2000; i++)
            {
                text.Append("a");          // one byte
                text.Append("ж");          // two
                text.Append("屋");         // three
                text.Append("\U0001F480"); // four
            }

            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            var strict = new UTF8Encoding(false, false);
            var rebuilt = new MemoryStream();

            var offset = 0;
            var chunks = 0;
            while (offset < bytes.Length)
            {
                var len = SafeChunkLength(bytes, offset, System.Math.Min(cap, bytes.Length - offset));
                Assert.True(len > 0, "the walk asked for a chunk of nothing at " + offset);

                var piece = strict.GetString(bytes, offset, len);
                Assert.DoesNotContain("�", piece);   // what a cut letter decodes to

                rebuilt.Write(bytes, offset, len);
                offset += len;
                chunks++;
            }

            Assert.True(chunks > 1, "the text has to be long enough to be split at all");
            Assert.Equal(bytes, rebuilt.ToArray());
        }

        /// <summary>
        /// The copy above is only worth anything while it is the same walk the plugin ships.
        /// The floor is the line that matters: a cap under four bytes cannot hold the longest
        /// letter, so the chunk grows to four rather than cutting one. It was a floor of one
        /// before, which at a cap of two or three returned a length that landed mid-letter.
        /// </summary>
        [Fact]
        public void The_copy_of_the_walk_matches_the_one_the_plugin_ships()
        {
            var src = WithoutComments(
                AppSourceTree.Read("ValheimBakaLoader", "Resources", "Commander", "BakaLoaderCommander.cs"));

            Assert.Contains("var len = wanted < 4 ? 4 : wanted;", src);
            Assert.Contains("if (offset + len >= bytes.Length) return bytes.Length - offset;", src);
            Assert.Contains("while (len > 1 && walked < 3 && (bytes[offset + len] & 0xC0) == 0x80)", src);
        }

        /// <summary>
        /// Whole-line comments out, because the paragraphs above the fix name the encoding
        /// they replaced and a rule about the code would otherwise trip on the explanation.
        /// </summary>
        private static string WithoutComments(string src) =>
            string.Join("\n", System.Linq.Enumerable.Where(
                src.Split('\n'), line => !line.TrimStart().StartsWith("//", System.StringComparison.Ordinal)));
    }
}
