using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The character file a test copy writes (issue 20), proved by a round trip.
    /// <para>
    /// Every file here is built by <see cref="GameLayout"/>, a writer of its own that follows
    /// PlayerProfile.SavePlayerToDisk in the 0.221 game field by field (and, for the older
    /// versions, the shapes LoadPlayerFromDisk still reads), so the reader is held to the game's
    /// layout rather than to itself. The writer notes where it put the name. The copy is then
    /// read back and compared byte for byte: the name changed, the hash made again over the new
    /// block, and every other byte the source's.
    /// </para>
    /// <para>
    /// The character folders are temp folders. Nothing under LocalLow is read or written.
    /// </para>
    /// </summary>
    public class CharacterFileTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "vbl-chars-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp folder, best effort */ }
            GC.SuppressFinalize(this);
        }

        /// <summary>A .fch in the game's own layout, and where in the profile block the name went.</summary>
        internal sealed class GameLayout
        {
            public byte[] File;
            public byte[] Data;
            public int NameStart;
            public int NameEnd;

            private static void Table(BinaryWriter w, params (string Key, float Value)[] rows)
            {
                w.Write(rows.Length);
                foreach (var (key, value) in rows) { w.Write(key); w.Write(value); }
            }

            /// <summary>
            /// Writes a profile of <paramref name="version"/>. 47 and 46 take SavePlayerToDisk's
            /// own order; 45 to 38 the Stats2 shape; 37 to 28 the four counters; 27 none.
            /// </summary>
            public static GameLayout Build(string name, int version = 47, int worlds = 2, bool mapData = true,
                bool playerData = true, long playerId = 1234567890123456789L)
            {
                using var block = new MemoryStream();
                int nameStart, nameEnd;
                using (var w = new BinaryWriter(block, Encoding.UTF8, leaveOpen: true))
                {
                    w.Write(version);
                    if (version >= 46 || version == 44)
                    {
                        w.Write(205);                               // stat count
                        w.Write(10);                                // one stat block per difficulty
                        for (var i = 0; i < 10; i++)
                        {
                            for (var j = 0; j < 205; j++) w.Write(i * 1000f + j);
                            Table(w, ("Midgard", 3600f), ("Ragnarok", 12f));
                            Table(w, ("defeated_eikthyr 1", 3600f));
                            Table(w, ("spawn", 1f), ("pos", 2f));
                            w.Write(5);
                            for (var k = 0; k < 5; k++) Table(w, ("$enemy_greyling", k + 1f));
                            Table(w, ("Wood", 99f));
                            Table(w, ("Club", 1f));
                            Table(w, ("Pickable_Mushroom", 4f));
                            Table(w, ("Raspberry", 7f));
                            Table(w, ("piece_workbench", 1f), ("woodwall", 30f));
                        }
                    }
                    else if (version >= 38)
                    {
                        w.Write(205);
                        for (var j = 0; j < 205; j++) w.Write((float)j);
                    }
                    else if (version >= 28)
                    {
                        w.Write(10); w.Write(2); w.Write(30); w.Write(400);
                    }

                    if (version >= 40) w.Write(false);              // first spawn

                    w.Write(worlds);
                    for (var i = 0; i < worlds; i++)
                    {
                        w.Write(7000000000000L + i);                // world uid
                        w.Write(i % 2 == 0); w.Write(1f); w.Write(2f); w.Write(3f);
                        w.Write(true); w.Write(4f); w.Write(5f); w.Write(6f);
                        if (version >= 30) { w.Write(false); w.Write(7f); w.Write(8f); w.Write(9f); }
                        w.Write(10f); w.Write(11f); w.Write(12f);
                        if (version >= 29)
                        {
                            w.Write(mapData);
                            if (mapData)
                            {
                                var map = Enumerable.Range(0, 4096).Select(b => (byte)(b * 31 + i)).ToArray();
                                w.Write(map.Length);
                                w.Write(map);
                            }
                        }
                    }

                    w.Flush();
                    nameStart = (int)block.Position;
                    w.Write(name);
                    w.Flush();
                    nameEnd = (int)block.Position;
                    w.Write(playerId);
                    w.Write("aBcDeFgHiJ");                          // start seed

                    if (version >= 38)
                    {
                        w.Write(false);                             // used cheats
                        w.Write(1760000000L);                       // date created
                        if (version < 46 && version != 44)
                        {
                            Table(w, ("Midgard", 3600f));
                            Table(w, ("defeated_eikthyr 1", 3600f));
                            Table(w);
                            if (version >= 42)
                            {
                                Table(w, ("$enemy_neck", 3f));
                                Table(w, ("Stone", 20f));
                                Table(w);
                            }
                        }
                    }

                    w.Write(playerData);
                    if (playerData)
                    {
                        var data = Enumerable.Range(0, 900).Select(b => (byte)(255 - b % 251)).ToArray();
                        w.Write(data.Length);
                        w.Write(data);
                    }
                }

                var profile = block.ToArray();
                using var file = new MemoryStream();
                using (var w = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
                {
                    var hash = SHA512.Create().ComputeHash(profile);
                    w.Write(profile.Length);
                    w.Write(profile);
                    w.Write(hash.Length);
                    w.Write(hash);
                }
                return new GameLayout { File = file.ToArray(), Data = profile, NameStart = nameStart, NameEnd = nameEnd };
            }
        }

        /// <summary>The profile block and the hash out of a whole file, the way LoadPlayerDataFromDisk splits it.</summary>
        private static (byte[] Data, byte[] Hash) Split(byte[] file)
        {
            using var r = new BinaryReader(new MemoryStream(file));
            var data = r.ReadBytes(r.ReadInt32());
            var hash = r.ReadBytes(r.ReadInt32());
            Assert.Equal(file.Length, (int)r.BaseStream.Position);
            return (data, hash);
        }

        [Theory]
        [InlineData(47)]
        [InlineData(46)]
        [InlineData(45)]
        [InlineData(44)]
        [InlineData(42)]
        [InlineData(40)]
        [InlineData(38)]
        [InlineData(33)]
        [InlineData(29)]
        [InlineData(27)]
        public void A_renamed_copy_differs_from_its_source_only_in_the_name_and_the_hash(int version)
        {
            var source = GameLayout.Build("Ragnar", version);

            Assert.True(CharacterFile.TryRead(source.File, out var info, out var problem), "the source did not read: " + problem);
            Assert.Equal("Ragnar", info.Name);
            Assert.Equal(version, info.Version);
            Assert.Equal(1234567890123456789L, info.PlayerId);
            Assert.True(info.HashMatches);

            var copy = CharacterFile.Rename(info, "Ragnar_bakaT");

            // Read back with the same reader: the new name, the same id, a true hash.
            Assert.True(CharacterFile.TryRead(copy, out var back, out problem), "the copy did not read: " + problem);
            Assert.Equal("Ragnar_bakaT", back.Name);
            Assert.Equal(info.PlayerId, back.PlayerId);
            Assert.Equal(version, back.Version);
            Assert.True(back.HashMatches, "the copy's hash is not the SHA-512 of its profile");

            // And byte for byte: the head up to the name and the tail after it are the source's.
            var (data, hash) = Split(copy);
            var nameBytes = Prefixed("Ragnar_bakaT");
            Assert.Equal(source.Data.Length - (source.NameEnd - source.NameStart) + nameBytes.Length, data.Length);
            Assert.Equal(source.Data.Take(source.NameStart), data.Take(source.NameStart));
            Assert.Equal(nameBytes, data.Skip(source.NameStart).Take(nameBytes.Length));
            Assert.Equal(source.Data.Skip(source.NameEnd), data.Skip(source.NameStart + nameBytes.Length));
            Assert.Equal(SHA512.Create().ComputeHash(data), hash);
            Assert.NotEqual(Split(source.File).Hash, hash);
        }

        [Fact]
        public void The_shapes_with_nothing_optional_in_them_read_too()
        {
            foreach (var version in new[] { 47, 38, 28 })
            {
                var bare = GameLayout.Build("Ylva", version, worlds: 0, mapData: false, playerData: false);
                Assert.True(CharacterFile.TryRead(bare.File, out var info, out _), "version " + version + " with nothing optional did not read");
                Assert.Equal("Ylva", info.Name);
                var noMap = GameLayout.Build("Ylva", version, worlds: 3, mapData: false);
                Assert.True(CharacterFile.TryRead(noMap.File, out _, out _));
            }
        }

        /// <summary>
        /// A name whose length takes two bytes in front of it, and one that is not ASCII. The
        /// rename has to move the tail, not just overwrite the name in place.
        /// </summary>
        [Fact]
        public void A_name_of_another_length_and_another_alphabet_moves_the_tail_with_it()
        {
            var source = GameLayout.Build("Åsa");
            Assert.True(CharacterFile.TryRead(source.File, out var info, out _));
            Assert.Equal("Åsa", info.Name);

            var longName = "Åsa" + new string('x', 140) + CharacterCopy.Suffix;
            var copy = CharacterFile.Rename(info, longName);
            Assert.True(CharacterFile.TryRead(copy, out var back, out _));
            Assert.Equal(longName, back.Name);
            var (data, _) = Split(copy);
            Assert.Equal(source.Data.Skip(source.NameEnd), data.Skip(data.Length - (source.Data.Length - source.NameEnd)));
        }

        [Fact]
        public void A_file_the_reader_cannot_walk_to_its_end_is_refused_rather_than_guessed_at()
        {
            var good = GameLayout.Build("Ragnar").File;

            Assert.False(CharacterFile.TryRead(Array.Empty<byte>(), out _, out var p));
            Assert.Equal(CharacterFile.NotACharacter, p);
            Assert.False(CharacterFile.TryRead(Encoding.UTF8.GetBytes("this is not a character"), out _, out _));
            Assert.False(CharacterFile.TryRead(good.Take(good.Length - 1).ToArray(), out _, out _), "a cut hash read");
            Assert.False(CharacterFile.TryRead(good.Concat(new byte[] { 0 }).ToArray(), out _, out _), "a trailing byte read");

            // A profile one byte longer than the walk, with the lengths kept true: the walk ends
            // short of the block, so the place the name was found in is not trusted.
            var (data, _) = Split(good);
            Assert.False(CharacterFile.TryRead(Wrap(data.Concat(new byte[] { 1 }).ToArray()), out _, out _));
            // A world count that runs past the end.
            var lying = (byte[])data.Clone();
            var worldsAt = IndexOfWorldCount(GameLayout.Build("Ragnar"));
            BitConverter.GetBytes(int.MaxValue).CopyTo(lying, worldsAt);
            Assert.False(CharacterFile.TryRead(Wrap(lying), out _, out _));

            // A version the game does not load, either side.
            foreach (var version in new[] { 26, 48, -1 })
            {
                var other = (byte[])data.Clone();
                BitConverter.GetBytes(version).CopyTo(other, 0);
                Assert.False(CharacterFile.TryRead(Wrap(other), out _, out p));
                Assert.Equal(CharacterFile.UnknownVersion, p);
            }
        }

        [Fact]
        public void The_copy_is_named_the_way_the_game_names_a_character_file()
        {
            Assert.Equal("ragnar_bakat.fch", CharacterFile.FileNameFor("Ragnar_bakaT"));
            Assert.Equal("åsa_bakat.fch", CharacterFile.FileNameFor("Åsa_bakaT"));
        }

        // ------------------------------------------------------------------ the copier

        private string Local => Path.Combine(Root, "characters_local");

        private string Plant(string file, GameLayout layout, string folder = "characters_local")
        {
            var dir = Path.Combine(Root, folder);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, file);
            File.WriteAllBytes(path, layout.File);
            return path;
        }

        [Fact]
        public void The_list_is_the_characters_and_not_the_games_backups_of_them()
        {
            Plant("ragnar.fch", GameLayout.Build("Ragnar"));
            Plant("ylva.fch", GameLayout.Build("Ylva"), "characters");
            Plant("ragnar_backup_auto-20261020120000.fch", GameLayout.Build("Ragnar"));
            File.WriteAllBytes(Path.Combine(Local, "ragnar.fch.old"), GameLayout.Build("Ragnar").File);
            File.WriteAllText(Path.Combine(Local, "broken.fch"), "not a character");

            var list = CharacterCopy.List(Root);

            Assert.Equal(new[] { "broken.fch", "ragnar.fch", "ylva.fch" }, list.Select(c => c.File).OrderBy(f => f).ToArray());
            var ragnar = list.Single(c => c.File == "ragnar.fch");
            Assert.Equal("characters_local", ragnar.Folder);
            Assert.Equal("Ragnar", ragnar.Name);
            Assert.Equal("Ragnar_bakaT", ragnar.CopyName);
            Assert.Equal("ragnar_bakat.fch", ragnar.CopyFile);
            Assert.False(ragnar.CopyExists);
            Assert.Null(ragnar.Problem);
            Assert.Equal("characters", list.Single(c => c.File == "ylva.fch").Folder);
            var broken = list.Single(c => c.File == "broken.fch");
            Assert.Null(broken.Name);
            Assert.Equal(CharacterFile.NotACharacter, broken.Problem);
            Assert.Empty(CharacterCopy.List(Path.Combine(Root, "nowhere")));
        }

        [Fact]
        public void A_copy_is_a_new_file_beside_the_source_and_the_source_is_never_touched()
        {
            var source = Plant("ragnar.fch", GameLayout.Build("Ragnar"));
            var before = File.ReadAllBytes(source);
            var stamp = File.GetLastWriteTimeUtc(source);

            var result = CharacterCopy.Copy(Root, "characters_local", "ragnar.fch");

            Assert.Equal("Ragnar_bakaT", result.Name);
            Assert.Equal("Ragnar", result.SourceName);
            Assert.Equal("ragnar_bakat.fch", result.File);
            Assert.Equal(Path.Combine(Local, "ragnar_bakat.fch"), result.Path);
            Assert.Equal(before, File.ReadAllBytes(source));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(source));
            Assert.True(CharacterFile.TryRead(File.ReadAllBytes(result.Path), out var copy, out _));
            Assert.Equal("Ragnar_bakaT", copy.Name);

            // The list now says the copy is there.
            Assert.True(CharacterCopy.List(Root).Single(c => c.File == "ragnar.fch").CopyExists);
        }

        [Fact]
        public void A_copy_that_is_already_there_is_refused_and_left_exactly_as_it_was()
        {
            Plant("ragnar.fch", GameLayout.Build("Ragnar"));
            var mine = Plant("ragnar_bakat.fch", GameLayout.Build("Ragnar_bakaT", playerId: 42));
            var before = File.ReadAllBytes(mine);

            var refusal = Assert.Throws<HostFacingException>(() => CharacterCopy.Copy(Root, "characters_local", "ragnar.fch"));

            Assert.Equal("characters.copy.exists", refusal.MessageId);
            Assert.Equal("ragnar_bakat.fch", refusal.Params["file"]);
            Assert.Contains("never writes over a character", refusal.Message);
            Assert.Equal(before, File.ReadAllBytes(mine));
        }

        [Fact]
        public void Every_other_refusal_is_named_and_writes_nothing()
        {
            Plant("ragnar.fch", GameLayout.Build("Ragnar"));
            File.WriteAllText(Path.Combine(Local, "broken.fch"), "not a character");
            var old = GameLayout.Build("Old");
            BitConverter.GetBytes(26).CopyTo(old.File, 4);
            File.WriteAllBytes(Path.Combine(Local, "old.fch"), old.File);
            var files = Directory.GetFiles(Local).OrderBy(f => f).ToArray();

            foreach (var (folder, file, id) in new[]
            {
                ("worlds_local", "ragnar.fch", "characters.copy.badRef"),
                ("characters_local", "..\\ragnar.fch", "characters.copy.badRef"),
                ("characters_local", "../ragnar.fch", "characters.copy.badRef"),
                ("characters_local", "ragnar.fch.old", "characters.copy.badRef"),
                ("characters_local", "ragnar_backup_auto-20261020120000.fch", "characters.copy.badRef"),
                ("characters_local", "", "characters.copy.badRef"),
                ("characters_local", "gone.fch", "characters.copy.missing"),
                ("characters", "ragnar.fch", "characters.copy.missing"),
                ("characters_local", "broken.fch", "characters.copy.unreadable"),
                ("characters_local", "old.fch", "characters.copy.unknownVersion"),
            })
            {
                var refusal = Assert.Throws<HostFacingException>(() => CharacterCopy.Copy(Root, folder, file));
                Assert.Equal(id, refusal.MessageId);
            }

            Assert.Equal(files, Directory.GetFiles(Local).OrderBy(f => f).ToArray());
            Assert.False(Directory.Exists(Path.Combine(Root, "characters")));
        }

        private static byte[] Prefixed(string s)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write(s);
            return ms.ToArray();
        }

        private static byte[] Wrap(byte[] data)
        {
            var hash = SHA512.Create().ComputeHash(data);
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(data.Length); w.Write(data); w.Write(hash.Length); w.Write(hash);
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>Where the world count sits: a build with no worlds puts the name right after it.</summary>
        private static int IndexOfWorldCount(GameLayout layout)
        {
            var empty = GameLayout.Build("Ragnar", worlds: 0);
            return empty.NameStart - 4;
        }
    }
}
