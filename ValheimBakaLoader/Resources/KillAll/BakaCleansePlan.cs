// BakaLoader: the words baka_cleanse answers with, and the counting behind them.
//
// WHY THIS FILE CARRIES NO FENCE
// It names no Valheim type at all, so BakaLoader's own project compiles it like any other
// file and the test suite reads it directly. Its fenced companion BakaCleanseSweep.cs is the
// half that touches the world, and that half is compiled only by Resources\build-plugins.ps1.
// The split is the same one BakaKillAllPlan.cs and BakaKillAllSweep.cs already keep, for the
// same reason: a reply a host reads is worth a test, and a test cannot load assembly_valheim.
//
// WHAT THE COMMAND IS FOR
// From 1.0.9 to 1.1.2 BakaLoader's own plugins marked every spawn with the game's cheated
// flag, on purpose, because that is what the vanilla spawn console command does. The game
// then spreads that mark on its own: a cheated item merged into a stack marks the whole
// stack, crafting with a cheated ingredient marks what comes out, a cheated weapon marks
// what it kills and what that drops, and a piece built from cheated materials marks the
// piece. New spawns have been clean since 1.2.0, and nothing in the game ever takes a mark
// back off, so a host who handed somebody a replacement axe in 1.1.x still has a world full
// of marked things. This is the way back.

using System;
using System.IO;
using System.Text;

namespace BakaLoaderKillAll
{
    /// <summary>
    /// The cheated bit inside a saved item, cleared without disturbing a single other byte.
    ///
    /// <para>
    /// WHY THIS IS NOT Inventory.Load AND Inventory.Save. That round trip looks like the
    /// obvious way to do it and it loses items. Inventory.Load hands every record to
    /// AddItem(int prefabHash, ...), which begins with ObjectDB.instance.GetItemPrefab(hash)
    /// and, when that answers null, logs a line and DROPS the item (decompiled
    /// assembly_valheim, Inventory.AddItem at 68695-68706). A chest holding an item from a
    /// mod the host has since removed would come back out of that round trip empty, and the
    /// cleanse would have been the thing that emptied it. There is no version of "clear a
    /// flag" that is worth a chest.
    /// </para>
    ///
    /// <para>
    /// So the blob is walked instead and one byte is written. The flag is the LAST byte of
    /// each item record and it holds nothing else: ItemDrop.ItemData.Save at 69362-69364
    /// writes <c>num2 |= (m_cheated ? 1 : 0); pkg.Write((byte)num2);</c> and
    /// ItemDrop.ItemData.Load at 69396-69397 reads <c>b2 = pkg.ReadByte();
    /// m_cheated = (b2 &amp; 1) != 0;</c>. Every other field is found by reading past it, so
    /// every other field comes out of this byte for byte identical by construction.
    /// </para>
    ///
    /// <para>
    /// THE FIELD LIST, in the order ItemDrop.ItemData.Save writes it for this build
    /// (decompiled assembly_valheim 69311-69365), which is the list this walks:
    /// <list type="number">
    /// <item>int, durability times a hundred</item>
    /// <item>byte, grid x</item>
    /// <item>byte, grid y</item>
    /// <item>byte, world level</item>
    /// <item>byte, flags: 1 picked up, 2 equipped, 4 quality is not 1, 8 stack is not 1,
    /// 16 variant is not 0, 32 crafter (BOTH the id and the name), 64 a drop prefab,
    /// 128 custom data</item>
    /// <item>ushort quality, only with flag 4</item>
    /// <item>ushort stack, only with flag 8</item>
    /// <item>int variant, only with flag 16</item>
    /// <item>long crafter id, only with flag 32</item>
    /// <item>string crafter name, only with flag 32</item>
    /// <item>int drop prefab hash, only with flag 64</item>
    /// <item>a count and that many key and value strings, only with flag 128</item>
    /// <item>byte, the cheated flags, always, and bit 0 is the mark</item>
    /// </list>
    /// The last byte is only written from item data version 109 (ChunksNCheats) and from the
    /// one older version that also carried it, 107 (AbandonedDN); see the gate at 69394.
    /// </para>
    ///
    /// <para>
    /// The numbers and strings are read the way ZPackage writes them, because ZPackage is a
    /// BinaryWriter over a MemoryStream and nothing more (decompiled ZPackage 82964-83370):
    /// little endian for the numbers, and a string is BinaryWriter's own seven-bit length in
    /// front of its UTF-8 bytes. The one shape of its own is the custom-data count, which is
    /// one byte under 128 and two bytes above it (WriteNumItems at 83173).
    /// </para>
    /// </summary>
    internal static class CleanseBlob
    {
        /// <summary>The item data version this build writes. Version.Item.ChunksNCheats.</summary>
        internal const int ItemVersionChunksNCheats = 109;

        /// <summary>The first version with the short inventory framing. Version.Item.Smaller.</summary>
        internal const int ItemVersionSmaller = 108;

        /// <summary>The one older version that also carried the flag. Version.Item.AbandonedDN.</summary>
        internal const int ItemVersionAbandonedDn = 107;

        /// <summary>Whether an item record of this version ends with the cheated byte.</summary>
        internal static bool CarriesCheatedByte(int version) =>
            version >= ItemVersionChunksNCheats || version == ItemVersionAbandonedDn;

        /// <summary>
        /// A container's items blob: an int version, a ushort count, and that many item
        /// records (Inventory.Save at 68479-68487).
        /// <para>
        /// Answers false and changes nothing for a blob this cannot read all the way to its
        /// last byte, which includes every inventory written before version 108, whose
        /// framing is another method again (Inventory.LoadOld). Leaving a container alone is
        /// always an answer; guessing at its bytes is not.
        /// </para>
        /// </summary>
        internal static bool ClearInventory(byte[] blob, out byte[] rewritten, out int cleared)
        {
            rewritten = blob;
            cleared = 0;
            if (blob == null || blob.Length < 6) return false;

            var working = (byte[])blob.Clone();

            try
            {
                using (var stream = new MemoryStream(working, writable: false))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false)))
                {
                    var version = reader.ReadInt32();
                    if (version < ItemVersionSmaller) return false;

                    var carries = CarriesCheatedByte(version);
                    int count = reader.ReadUInt16();

                    for (var i = 0; i < count; i++)
                    {
                        // Nothing is rewritten when this gives up, so nothing was cleared. The
                        // count is an out parameter a caller adds to a running total, and a
                        // refused blob that still reported the records it got through before
                        // giving up made the sweep tell the host it had emptied things it had
                        // left exactly as they were.
                        if (!ClearOneRecord(reader, working, carries, ref cleared))
                        {
                            cleared = 0;
                            return false;
                        }
                    }

                    // Every byte accounted for, or this did not understand the blob after all.
                    if (stream.Position != working.Length)
                    {
                        cleared = 0;
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                cleared = 0;
                return false;
            }

            rewritten = working;
            return true;
        }

        /// <summary>
        /// The blob one item on the ground, on an armour stand or on an item stand carries:
        /// a BYTE version and one record (ItemDrop.SaveToZDO at 70622-70642).
        /// </summary>
        internal static bool ClearItemData(byte[] blob, out byte[] rewritten, out int cleared)
        {
            rewritten = blob;
            cleared = 0;
            // The game's own reader ignores anything this short (LoadFromZDO at 70614).
            if (blob == null || blob.Length <= 2) return false;

            var working = (byte[])blob.Clone();

            try
            {
                using (var stream = new MemoryStream(working, writable: false))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false)))
                {
                    var version = reader.ReadByte();
                    if (!ClearOneRecord(reader, working, CarriesCheatedByte(version), ref cleared)) return false;
                    if (stream.Position != working.Length) return false;
                }
            }
            catch (Exception)
            {
                cleared = 0;
                return false;
            }

            rewritten = working;
            return true;
        }

        /// <summary>
        /// Reads past one item record and, when it ends with the cheated byte and that byte
        /// has the mark in it, takes the mark out of the buffer in place. Only bit 0 is
        /// touched: the rest of that byte is whatever the game put there.
        /// </summary>
        private static bool ClearOneRecord(BinaryReader reader, byte[] buffer, bool carriesCheatedByte, ref int cleared)
        {
            reader.ReadInt32();                 // durability
            reader.ReadByte();                  // grid x
            reader.ReadByte();                  // grid y
            reader.ReadByte();                  // world level

            var flags = reader.ReadByte();
            if ((flags & 0x04) != 0) reader.ReadUInt16();   // quality
            if ((flags & 0x08) != 0) reader.ReadUInt16();   // stack
            if ((flags & 0x10) != 0) reader.ReadInt32();    // variant
            if ((flags & 0x20) != 0)
            {
                reader.ReadInt64();                          // crafter id
                reader.ReadString();                         // crafter name
            }
            if ((flags & 0x40) != 0) reader.ReadInt32();    // drop prefab hash

            var custom = 0;
            if ((flags & 0x80) != 0)
            {
                // WriteNumItems: one byte under 128, otherwise the high bit marks a second.
                int first = reader.ReadByte();
                custom = (first & 0x80) != 0 ? ((first & 0x7F) << 8) | reader.ReadByte() : first;
            }

            for (var i = 0; i < custom; i++)
            {
                reader.ReadString();
                reader.ReadString();
            }

            if (!carriesCheatedByte) return true;

            var at = (int)reader.BaseStream.Position;
            if (at >= buffer.Length) return false;
            var cheated = reader.ReadByte();
            if ((cheated & 1) == 0) return true;

            buffer[at] = (byte)(cheated & ~1);
            cleared++;
            return true;
        }
    }


    /// <summary>
    /// What one cleanse came to, and the sentences it is said in. Pure: the sweep hands it
    /// numbers and it hands back a line.
    /// </summary>
    internal static class CleansePlan
    {
        /// <summary>
        /// What the command answers when somebody is still connected. The count is in it, and
        /// so are the names, because "wait for the server to be empty" is not an instruction a
        /// host can act on without knowing who is still on it.
        /// <para>
        /// The refusal exists because of what the cleanse does to a container: it reads the
        /// items blob off the ZDO, rewrites it and writes it back. A container somebody has
        /// open refuses to reload itself while they are in it, so the rewrite would be
        /// overwritten by whatever their client saves next. And a player's own inventory is
        /// not in the world at all; it lives in their character file on their own machine.
        /// </para>
        /// </summary>
        internal static string Connected(int count, string names)
        {
            var text = new StringBuilder("Error: ");
            text.Append(count).Append(count == 1 ? " player is still connected" : " players are still connected");
            if (!string.IsNullOrEmpty(names)) text.Append(" (").Append(names).Append(")");
            text.Append(". Cheat marks can only be cleared on an empty server.");
            return text.ToString();
        }

        /// <summary>The result line, with the three counts a host can check the work against.</summary>
        internal static string Reply(int zdosCleared, int containersRewritten, int itemsCleared)
        {
            var text = new StringBuilder("Cleanse complete: ");
            text.Append(zdosCleared).Append(zdosCleared == 1 ? " world object cleared, " : " world objects cleared, ");
            text.Append(containersRewritten)
                .Append(containersRewritten == 1 ? " container rewritten, " : " containers rewritten, ");
            text.Append(itemsCleared).Append(itemsCleared == 1 ? " item cleared." : " items cleared.");
            text.Append(" Anything a player is carrying lives on their own machine and was not touched.");
            return text.ToString();
        }

        /// <summary>Nothing in the world carried a mark, which is a result rather than a failure.</summary>
        internal static string NothingToDo()
        {
            return "Cleanse complete: nothing in this world carries a cheat mark."
                + " Anything a player is carrying lives on their own machine and was not touched.";
        }

        /// <summary>The world is not up yet, said the same way every other command says it.</summary>
        internal const string NotReady = "Error: server not ready (world still loading)";

        /// <summary>The world's object index could not be read, so nothing was touched.</summary>
        internal const string NoIndex =
            "Error: this build of Valheim keeps its world objects somewhere the cleanse cannot read,"
            + " so nothing was touched. The plugin needs rebuilding against it.";
    }
}
