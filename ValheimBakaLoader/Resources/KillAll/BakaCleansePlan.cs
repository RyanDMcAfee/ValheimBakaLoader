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
            var text = new StringBuilder(CompletePrefix);
            text.Append(" ").Append(Counts(zdosCleared, containersRewritten, itemsCleared)).Append(".");
            text.Append(" Anything a player is carrying lives on their own machine and was not touched.");
            return text.ToString();
        }

        /// <summary>
        /// The three counts as one phrase, with no full stop on the end. It is written once
        /// because two lines carry it now: the one that says the cleanse finished, and the one
        /// that says it stopped part way because somebody connected. The page reads both with
        /// the same expression, so a second spelling of these words would be a reply the host
        /// can see and the window cannot read.
        /// </summary>
        internal static string Counts(int zdosCleared, int containersRewritten, int itemsCleared)
        {
            var text = new StringBuilder();
            text.Append(zdosCleared).Append(zdosCleared == 1 ? " world object cleared, " : " world objects cleared, ");
            text.Append(containersRewritten)
                .Append(containersRewritten == 1 ? " container rewritten, " : " containers rewritten, ");
            text.Append(itemsCleared).Append(itemsCleared == 1 ? " item cleared" : " items cleared");
            return text.ToString();
        }

        // ------------------------------------------------------------------
        //  The sliced sweep: what it says while it is running
        // ------------------------------------------------------------------

        /// <summary>
        /// The opening of the line a finished cleanse answers with. Both the page and the
        /// window read replies by their opening words, so the four openings below are named
        /// here rather than spelled out at each side.
        /// </summary>
        internal const string CompletePrefix = "Cleanse complete:";

        /// <summary>The opening of the line a cleanse answers with the moment it starts.</summary>
        internal const string StartedPrefix = "Cleanse started:";

        /// <summary>The opening of the status line while a sweep is still walking.</summary>
        internal const string RunningPrefix = "Cleanse running:";

        /// <summary>The opening of the line a sweep that gave up part way answers with.</summary>
        internal const string StoppedPrefix = "Cleanse stopped:";

        /// <summary>
        /// What a cleanse says the moment it starts, when it is too big to finish inside the
        /// answer.
        /// <para>
        /// RCON is why this line exists, exactly as it is why the kill sweep has one. The
        /// window's client gives up after a few seconds and Commander stops waiting before
        /// that, and until this was written a sweep over a long-lived world ran past both:
        /// the host was told the command had timed out while the cleanse went on and did
        /// every bit of its work. Worse, the whole pass held the main thread, so the server
        /// did not tick for as long as it took. The sweep now answers the moment it knows
        /// what it is about to walk, does the walk a few milliseconds a frame, and puts the
        /// result line in the server log.
        /// </para>
        /// </summary>
        internal static string Started(int total)
        {
            return StartedPrefix + " " + total + (total == 1 ? " object" : " objects") +
                   " to check. The counts follow in the server log when it finishes, and" +
                   " baka_cleanse_status says how far it has got.";
        }

        /// <summary>
        /// The answer to a second baka_cleanse while the first is still walking. Two sweeps
        /// over one snapshot would count every cleared mark twice and rewrite half the
        /// containers under each other.
        /// </summary>
        internal static string AlreadyRunning(int remaining)
        {
            return "Cleanse is already running: " + remaining +
                   (remaining == 1 ? " object" : " objects") +
                   " still to check. Wait for its result line before starting another.";
        }

        /// <summary>Nothing has been run since the server came up.</summary>
        internal static string StatusIdle()
        {
            return "Cleanse status: nothing is running. Run baka_cleanse on an empty server" +
                   " to clear the cheat marks a 1.0.9 to 1.1.2 spawn left behind.";
        }

        /// <summary>How far along the walk is, which is the whole of what a waiting host wants.</summary>
        internal static string StatusRunning(int checkedSoFar, int total)
        {
            return RunningPrefix + " " + checkedSoFar + " of " + total +
                   (total == 1 ? " object" : " objects") + " checked.";
        }

        /// <summary>
        /// What a sweep says when somebody connected while it was walking.
        /// <para>
        /// The start check refuses a cleanse while anybody is on the server, and for a sweep
        /// that takes minutes that check has to go on being asked: a container rewrites itself
        /// from the world record only when nobody has it open, so a chest somebody walked up
        /// to mid sweep would take the rewrite and have it written straight back over by their
        /// client. The counts it reached are real work that really happened, so they are
        /// reported rather than thrown away, and the line does not open with "Cleanse
        /// complete" because it is not one.
        /// </para>
        /// </summary>
        internal static string StoppedForPeers(
            int count, string names, int zdosCleared, int containersRewritten, int itemsCleared)
        {
            var text = new StringBuilder(StoppedPrefix);
            text.Append(" ").Append(count)
                .Append(count == 1 ? " player connected" : " players connected")
                .Append(" while it was running");
            if (!string.IsNullOrEmpty(names)) text.Append(" (").Append(names).Append(")");
            text.Append(". Up to that point: ")
                .Append(Counts(zdosCleared, containersRewritten, itemsCleared)).Append(".");
            text.Append(" Run it again when the server is empty.");
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

    /// <summary>
    /// One cleanse, from the moment its snapshot is taken to its result line: how far along
    /// the walk is, what it has cleared, and whether anything ended it early.
    /// <para>
    /// It names no Valheim type at all, which is the point: the walk it describes is the half
    /// that can be driven by a test, and the records it walks are handed to it by index.
    /// </para>
    /// </summary>
    internal sealed class CleanseRun
    {
        /// <summary>How many records the snapshot held when it was taken.</summary>
        internal int Total;

        /// <summary>How far along the snapshot the walk has got.</summary>
        internal int Index;

        internal int ZdosCleared;
        internal int ContainersRewritten;
        internal int ItemsCleared;

        /// <summary>True once the walk is over, however it ended.</summary>
        internal bool Finished;

        /// <summary>True when something ended it before it reached the end of the snapshot.</summary>
        internal bool Stopped;

        /// <summary>Who was on the server when it stopped, and how many of them.</summary>
        internal int PeerCount;

        internal string PeerNames = "";

        internal int Remaining
        {
            get { return Total - Index; }
        }
    }

    /// <summary>
    /// The walk itself, with no game in it.
    /// <para>
    /// A cleanse used to be one uninterrupted pass over every object in the world. On a
    /// long-lived world that pass outlasts the RCON client's patience, so the host was told
    /// the command had timed out while it carried on; and because it ran on the Unity main
    /// thread, the server did not tick for the whole of it. Anybody still connected would
    /// have been frozen, and anybody trying to connect would have been refused.
    /// </para>
    /// <para>
    /// So the pass is cut into slices with a budget per frame, exactly as the kill sweep
    /// beside it already is. The budget is asked AFTER each record rather than before one,
    /// so a slice always makes at least one record's worth of progress and a sweep can never
    /// stall however slow one record turns out to be.
    /// </para>
    /// </summary>
    internal static class CleanseWalk
    {
        /// <summary>
        /// How much of a frame one slice may spend. Eight milliseconds of a frame that is
        /// meant to last about thirty, which is what keeps the server ticking while the walk
        /// goes on.
        /// <para>
        /// Nothing here measures how long a sweep takes, and nothing can: the walk gets about
        /// eight milliseconds of work per frame, so a world of N records takes roughly N
        /// divided by however many records a slice gets through, in frames. How many that is
        /// depends on the records, and a container with a full items blob costs a great deal
        /// more than an empty tree. The point of the budget is the tick, not the total.
        /// </para>
        /// </summary>
        internal const long SliceMilliseconds = 8;

        /// <summary>
        /// One slice of the walk.
        /// </summary>
        /// <param name="run">The sweep in flight. Its counts and its position are moved here.</param>
        /// <param name="step">Handed the index of each record to work on.</param>
        /// <param name="budgetSpent">
        /// Asked after each record: true when this slice is over. Null walks the whole
        /// snapshot in one go, which is what a test that is not about slicing wants.
        /// </param>
        /// <param name="stopNow">
        /// Asked once at the top of every slice, before any record is touched: true when the
        /// sweep must end now. The caller is what records WHY, because the reason is about the
        /// world and this knows nothing about one.
        /// </param>
        /// <returns>True when the walk is over, however it ended.</returns>
        internal static bool Advance(
            CleanseRun run, Action<int> step, Func<bool> budgetSpent, Func<bool> stopNow)
        {
            if (run == null) return true;
            if (run.Finished) return true;

            // Before the slice rather than during it. Somebody who connected between two
            // slices must not have a container rewritten under them, and finding that out
            // half way through a slice would leave the counts saying otherwise.
            if (stopNow != null && stopNow())
            {
                run.Stopped = true;
                run.Finished = true;
                return true;
            }

            while (run.Index < run.Total)
            {
                if (step != null) step(run.Index);
                run.Index++;

                if (budgetSpent != null && budgetSpent()) break;
            }

            if (run.Index < run.Total) return false;

            run.Finished = true;
            return true;
        }
    }
}
