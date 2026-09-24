using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BakaLoaderKillAll;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The one byte baka_cleanse writes into a saved item, and the whole of what it must not
    /// touch around it.
    /// <para>
    /// The blobs below are written here the way ItemDrop.ItemData.Save writes them in the
    /// build this release was made against (decompiled assembly_valheim 69311-69365), field
    /// for field and in order, over a BinaryWriter, because that is all ZPackage is. No
    /// capture was taken off a live world: a chest's blob is a chest's contents, and the one
    /// live world on this machine belongs to somebody who is playing on it.
    /// </para>
    /// <para>
    /// The strong assertion in every test is the same one: the output differs from the input
    /// in exactly the positions of the cheated bytes and nowhere else. That is what a round
    /// trip through Inventory.Load and Inventory.Save could not promise, and why the cleanse
    /// does not use one: Inventory.AddItem drops an item whose prefab ObjectDB does not know
    /// (68695-68706), so a chest holding an item from a mod the host removed would come back
    /// empty.
    /// </para>
    /// </summary>
    public class CleanseBlobTests
    {
        // --------------------------------------------------------------- the writer

        /// <summary>One item, written exactly as ItemDrop.ItemData.Save writes it.</summary>
        private static void WriteItem(
            BinaryWriter w,
            float durability = 87.5f,
            byte gridX = 3, byte gridY = 1, byte worldLevel = 2,
            bool pickedUp = true, bool equipped = false,
            int quality = 1, int stack = 1, int variant = 0,
            long crafterId = 0, string crafterName = "",
            int? dropPrefab = null,
            Dictionary<string, string> customData = null,
            bool cheated = false,
            bool withCheatedByte = true)
        {
            customData = customData ?? new Dictionary<string, string>();

            var flags = 0;
            flags |= pickedUp ? 1 : 0;
            flags |= equipped ? 2 : 0;
            flags |= quality != 1 ? 4 : 0;
            flags |= stack != 1 ? 8 : 0;
            flags |= variant != 0 ? 16 : 0;
            flags |= crafterId != 0 ? 32 : 0;
            flags |= dropPrefab != null ? 64 : 0;
            flags |= customData.Count != 0 ? 128 : 0;

            w.Write((int)(durability * 100f));
            w.Write(gridX);
            w.Write(gridY);
            w.Write(worldLevel);
            w.Write((byte)flags);

            if ((flags & 0x04) != 0) w.Write((ushort)quality);
            if ((flags & 0x08) != 0) w.Write((ushort)stack);
            if ((flags & 0x10) != 0) w.Write(variant);
            if ((flags & 0x20) != 0)
            {
                w.Write(crafterId);
                w.Write(crafterName);
            }
            if ((flags & 0x40) != 0) w.Write(dropPrefab.Value);
            if ((flags & 0x80) != 0) WriteNumItems(w, customData.Count);
            foreach (var pair in customData)
            {
                w.Write(pair.Key);
                w.Write(pair.Value);
            }

            if (withCheatedByte) w.Write((byte)(cheated ? 1 : 0));
        }

        /// <summary>ZPackage.WriteNumItems, at 83173: one byte under 128, two above it.</summary>
        private static void WriteNumItems(BinaryWriter w, int count)
        {
            if (count < 128) { w.Write((byte)count); return; }
            w.Write((byte)((uint)(count >> 8) | 0x80u));
            w.Write((byte)count);
        }

        /// <summary>A container's blob: Inventory.Save at 68479-68487.</summary>
        private static byte[] Inventory(int version, params Action<BinaryWriter>[] items)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                w.Write(version);
                w.Write((ushort)items.Length);
                foreach (var item in items) item(w);
            }
            return stream.ToArray();
        }

        /// <summary>One item on the ground: ItemDrop.SaveToZDO at 70622-70642, byte version.</summary>
        private static byte[] OneItem(int version, Action<BinaryWriter> item)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                w.Write((byte)version);
                item(w);
            }
            return stream.ToArray();
        }

        /// <summary>Which byte positions differ between two buffers of the same length.</summary>
        private static List<int> Differences(byte[] before, byte[] after)
        {
            Assert.Equal(before.Length, after.Length);
            var at = new List<int>();
            for (var i = 0; i < before.Length; i++) if (before[i] != after[i]) at.Add(i);
            return at;
        }

        // --------------------------------------------------------------- the container

        [Fact]
        public void A_marked_item_in_a_chest_loses_the_mark_and_nothing_else_moves()
        {
            var blob = Inventory(CleanseBlob.ItemVersionChunksNCheats,
                w => WriteItem(w, cheated: true, stack: 17, quality: 3, variant: 4,
                               crafterId: 987654321L, crafterName: "Bjorn the Red",
                               dropPrefab: 12345,
                               customData: new Dictionary<string, string> { { "mod.key", "value" } }));

            byte[] rewritten;
            int cleared;
            Assert.True(CleanseBlob.ClearInventory(blob, out rewritten, out cleared));
            Assert.Equal(1, cleared);

            // Exactly one byte moved, it is the last one, and it moved from 1 to 0.
            var moved = Differences(blob, rewritten);
            Assert.Single(moved);
            Assert.Equal(blob.Length - 1, moved[0]);
            Assert.Equal(1, blob[moved[0]]);
            Assert.Equal(0, rewritten[moved[0]]);
        }

        [Fact]
        public void Only_the_mark_bit_is_taken_out_of_the_flags_byte()
        {
            // The game writes only bit 0 today, but the byte is a flags byte and the rule is
            // about bit 0 rather than about the byte.
            var blob = Inventory(CleanseBlob.ItemVersionChunksNCheats, w => WriteItem(w));
            blob[blob.Length - 1] = 0x83;   // the mark, and two flags this build does not write

            byte[] rewritten;
            int cleared;
            Assert.True(CleanseBlob.ClearInventory(blob, out rewritten, out cleared));
            Assert.Equal(1, cleared);
            Assert.Equal(0x82, rewritten[rewritten.Length - 1]);
        }

        [Fact]
        public void A_chest_of_several_items_loses_only_the_marks()
        {
            var blob = Inventory(CleanseBlob.ItemVersionChunksNCheats,
                w => WriteItem(w, cheated: false, stack: 3),
                w => WriteItem(w, cheated: true, crafterId: 1L, crafterName: "Ymir"),
                w => WriteItem(w, cheated: false, customData: new Dictionary<string, string> { { "a", "b" }, { "c", "d" } }),
                w => WriteItem(w, cheated: true, quality: 4, dropPrefab: 99));

            byte[] rewritten;
            int cleared;
            Assert.True(CleanseBlob.ClearInventory(blob, out rewritten, out cleared));

            Assert.Equal(2, cleared);
            var moved = Differences(blob, rewritten);
            Assert.Equal(2, moved.Count);
            foreach (var at in moved)
            {
                Assert.Equal(1, blob[at]);
                Assert.Equal(0, rewritten[at]);
            }
        }

        [Fact]
        public void A_chest_with_nothing_marked_comes_back_byte_for_byte_and_counts_nothing()
        {
            var blob = Inventory(CleanseBlob.ItemVersionChunksNCheats,
                w => WriteItem(w), w => WriteItem(w, stack: 42));

            byte[] rewritten;
            int cleared;
            Assert.True(CleanseBlob.ClearInventory(blob, out rewritten, out cleared));
            Assert.Equal(0, cleared);
            Assert.Equal(blob, rewritten);
        }

        [Fact]
        public void An_empty_chest_is_read_and_left_alone()
        {
            var blob = Inventory(CleanseBlob.ItemVersionChunksNCheats);

            byte[] rewritten;
            int cleared;
            Assert.True(CleanseBlob.ClearInventory(blob, out rewritten, out cleared));
            Assert.Equal(0, cleared);
            Assert.Equal(blob, rewritten);
        }

        /// <summary>
        /// A custom-data count of 128 or more is two bytes rather than one. Reading it as one
        /// would put every field after it out of step and the walk would run off the end,
        /// which is a refusal rather than a wrong answer, but it is worth proving it does not
        /// happen at all.
        /// </summary>
        [Fact]
        public void A_long_custom_data_list_is_walked_with_the_two_byte_count()
        {
            var custom = new Dictionary<string, string>();
            for (var i = 0; i < 200; i++) custom["key" + i] = "value" + i;

            var blob = Inventory(CleanseBlob.ItemVersionChunksNCheats,
                w => WriteItem(w, cheated: true, customData: custom));

            byte[] rewritten;
            int cleared;
            Assert.True(CleanseBlob.ClearInventory(blob, out rewritten, out cleared));
            Assert.Equal(1, cleared);
            Assert.Single(Differences(blob, rewritten));
        }

        /// <summary>
        /// A name outside the first 128 characters is written as UTF-8 behind a seven-bit
        /// length, and a walker that counted characters would lose its place inside it.
        /// </summary>
        [Fact]
        public void A_crafter_name_in_another_alphabet_does_not_move_the_walk()
        {
            var blob = Inventory(CleanseBlob.ItemVersionChunksNCheats,
                w => WriteItem(w, cheated: true, crafterId: 7L, crafterName: "Ωμέγα Σίγμα"),
                w => WriteItem(w, cheated: false, crafterId: 8L, crafterName: "ビョルン"));

            byte[] rewritten;
            int cleared;
            Assert.True(CleanseBlob.ClearInventory(blob, out rewritten, out cleared));
            Assert.Equal(1, cleared);
            Assert.Single(Differences(blob, rewritten));
        }

        // --------------------------------------------------------------- the versions

        /// <summary>
        /// Version 108 wrote no cheated byte at all (the gate at 69394), so there is nothing
        /// to clear and the blob comes back exactly as it went in.
        /// </summary>
        [Fact]
        public void A_version_that_carried_no_mark_is_read_and_left_alone()
        {
            var blob = Inventory(CleanseBlob.ItemVersionSmaller,
                w => WriteItem(w, withCheatedByte: false),
                w => WriteItem(w, withCheatedByte: false, stack: 5));

            byte[] rewritten;
            int cleared;
            Assert.True(CleanseBlob.ClearInventory(blob, out rewritten, out cleared));
            Assert.Equal(0, cleared);
            Assert.Equal(blob, rewritten);
        }

        [Fact]
        public void The_two_versions_that_carry_the_mark_are_the_two_the_game_names()
        {
            Assert.True(CleanseBlob.CarriesCheatedByte(CleanseBlob.ItemVersionChunksNCheats));
            Assert.True(CleanseBlob.CarriesCheatedByte(CleanseBlob.ItemVersionAbandonedDn));
            Assert.True(CleanseBlob.CarriesCheatedByte(CleanseBlob.ItemVersionChunksNCheats + 1));
            Assert.False(CleanseBlob.CarriesCheatedByte(CleanseBlob.ItemVersionSmaller));
            Assert.False(CleanseBlob.CarriesCheatedByte(106));
        }

        /// <summary>
        /// Anything older than the short framing is another method again in the game
        /// (Inventory.LoadOld), so it is refused and the container is left exactly as it is.
        /// Leaving a chest alone is always an answer; guessing at its bytes is not.
        /// </summary>
        [Fact]
        public void An_inventory_older_than_the_short_framing_is_refused_and_untouched()
        {
            var blob = Inventory(106, w => WriteItem(w, withCheatedByte: false));

            byte[] rewritten;
            int cleared;
            Assert.False(CleanseBlob.ClearInventory(blob, out rewritten, out cleared));
            Assert.Equal(0, cleared);
            Assert.Same(blob, rewritten);
        }

        // --------------------------------------------------------------- the refusals

        [Theory]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 1, 2, 3 })]
        public void A_blob_too_short_to_be_one_is_refused(byte[] blob)
        {
            byte[] rewritten;
            int cleared;
            Assert.False(CleanseBlob.ClearInventory(blob, out rewritten, out cleared));
            Assert.Equal(0, cleared);
        }

        [Fact]
        public void A_blob_that_is_null_is_refused_rather_than_thrown_over()
        {
            byte[] rewritten;
            int cleared;
            Assert.False(CleanseBlob.ClearInventory(null, out rewritten, out cleared));
            Assert.False(CleanseBlob.ClearItemData(null, out rewritten, out cleared));
        }

        [Fact]
        public void A_blob_that_runs_out_half_way_through_is_refused_and_nothing_is_written()
        {
            var whole = Inventory(CleanseBlob.ItemVersionChunksNCheats,
                w => WriteItem(w, cheated: true),
                w => WriteItem(w, cheated: true));
            var cut = new byte[whole.Length - 4];
            Array.Copy(whole, cut, cut.Length);
            var before = (byte[])cut.Clone();

            byte[] rewritten;
            int cleared;
            Assert.False(CleanseBlob.ClearInventory(cut, out rewritten, out cleared));
            Assert.Equal(0, cleared);
            // And the caller's own buffer is untouched, so a refusal cannot half-write a chest.
            Assert.Equal(before, cut);
        }

        [Fact]
        public void A_blob_with_bytes_left_over_at_the_end_is_refused()
        {
            var whole = Inventory(CleanseBlob.ItemVersionChunksNCheats, w => WriteItem(w, cheated: true));
            var padded = new byte[whole.Length + 3];
            Array.Copy(whole, padded, whole.Length);

            byte[] rewritten;
            int cleared;
            Assert.False(CleanseBlob.ClearInventory(padded, out rewritten, out cleared));

            // Nothing is written when this is refused, so nothing was cleared. The count is an
            // out parameter a caller adds to a running total, and this path walked the records
            // first and reported what it had got through before it gave up: the sweep told the
            // host it had emptied a chest it had left exactly as it was.
            Assert.Equal(0, cleared);
        }

        /// <summary>
        /// The other refusal that is not an exception: the records read cleanly and the last one
        /// ends where its cheated byte should have been. Same rule, and it needs its own test
        /// because it is reached with a running count already in hand.
        /// </summary>
        [Fact]
        public void A_record_that_ends_where_its_last_byte_should_be_reports_nothing_cleared()
        {
            var whole = Inventory(CleanseBlob.ItemVersionChunksNCheats,
                w => WriteItem(w, cheated: true),
                w => WriteItem(w, cheated: true));

            // One byte short: the first item is a mark this would have cleared, and the second
            // record runs out exactly at the byte that says whether it is marked.
            var cut = new byte[whole.Length - 1];
            Array.Copy(whole, cut, cut.Length);
            var before = (byte[])cut.Clone();

            byte[] rewritten;
            int cleared;
            Assert.False(CleanseBlob.ClearInventory(cut, out rewritten, out cleared));
            Assert.Equal(0, cleared);
            Assert.Equal(before, cut);
        }

        // --------------------------------------------------------------- one item alone

        [Fact]
        public void An_item_on_the_ground_loses_its_mark_and_nothing_else_moves()
        {
            var blob = OneItem(CleanseBlob.ItemVersionChunksNCheats,
                w => WriteItem(w, cheated: true, quality: 2, crafterId: 5L, crafterName: "Freya"));

            byte[] rewritten;
            int cleared;
            Assert.True(CleanseBlob.ClearItemData(blob, out rewritten, out cleared));
            Assert.Equal(1, cleared);

            var moved = Differences(blob, rewritten);
            Assert.Single(moved);
            Assert.Equal(blob.Length - 1, moved[0]);
        }

        [Fact]
        public void An_item_on_the_ground_that_is_not_marked_comes_back_byte_for_byte()
        {
            var blob = OneItem(CleanseBlob.ItemVersionChunksNCheats, w => WriteItem(w));

            byte[] rewritten;
            int cleared;
            Assert.True(CleanseBlob.ClearItemData(blob, out rewritten, out cleared));
            Assert.Equal(0, cleared);
            Assert.Equal(blob, rewritten);
        }

        [Fact]
        public void The_game_ignores_a_two_byte_item_blob_and_so_does_this()
        {
            byte[] rewritten;
            int cleared;
            Assert.False(CleanseBlob.ClearItemData(new byte[] { 109, 0 }, out rewritten, out cleared));
        }

        // --------------------------------------------------------------- the words

        [Fact]
        public void The_refusal_names_who_is_still_on_the_server()
        {
            var said = CleansePlan.Connected(2, "Bjorn, Freya");

            Assert.Contains("2 players are still connected", said);
            Assert.Contains("Bjorn, Freya", said);
            Assert.StartsWith("Error: ", said);

            // One player is one player, not "1 players".
            Assert.Contains("1 player is still connected", CleansePlan.Connected(1, "Bjorn"));
        }

        [Fact]
        public void The_result_line_counts_in_the_singular_and_the_plural()
        {
            Assert.Contains("1 world object cleared", CleansePlan.Reply(1, 1, 1));
            Assert.Contains("1 container rewritten", CleansePlan.Reply(1, 1, 1));
            Assert.Contains("1 item cleared", CleansePlan.Reply(1, 1, 1));

            Assert.Contains("0 world objects cleared", CleansePlan.Reply(0, 0, 0));
            Assert.Contains("2 containers rewritten", CleansePlan.Reply(3, 2, 9));
            Assert.Contains("9 items cleared", CleansePlan.Reply(3, 2, 9));
        }

        /// <summary>
        /// Every answer says the one thing the command cannot do, because a player's own
        /// inventory is in their character file and not in the world.
        /// </summary>
        [Fact]
        public void Every_answer_says_what_it_could_not_reach()
        {
            foreach (var said in new[] { CleansePlan.Reply(1, 1, 1), CleansePlan.NothingToDo() })
                Assert.Contains("player is carrying", said);
        }
    }
}
