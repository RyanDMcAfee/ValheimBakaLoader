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
    /// Copying a world beside itself under a new name, in both save formats.
    /// <para>
    /// The thing that makes this more than a file copy is the header: the game carries the
    /// world's own name inside the .fwl or the .fwl2 and builds the biome cache file name
    /// out of it, so a copy that kept the source's name would be two worlds answering to
    /// one. Every test here checks the copy's header as well as its files, and checks that
    /// the source came out of it untouched.
    /// </para>
    /// </summary>
    public class WorldStoreCopyAsTests : IDisposable
    {
        private readonly string SaveFolder =
            Path.Combine(Path.GetTempPath(), "vbl-copyas-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(SaveFolder, recursive: true); } catch { /* best effort */ }
        }

        // ------------------------------------------------------------------ tree builders

        private string WorldsDir(string sub = "worlds_local")
        {
            var dir = Path.Combine(SaveFolder, sub);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static byte[] BuildFwl(int version, string worldName, string seedName)
        {
            using var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(version);
                w.Write(worldName);
                w.Write(seedName);
                w.Write(FwlWriter.GetStableHashCode(seedName));
                w.Write(1234567890123L);
                w.Write(2);
                w.Write(false);
                w.Write(0);
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

        private static byte[] BuildDbHeader(int version, double netTime)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(version);
                w.Write(netTime);
                w.Write(new byte[32]);
            }
            return ms.ToArray();
        }

        private void MakeLegacyWorld(string name, string sub = "worlds_local", string seedName = "seedy")
        {
            var dir = WorldsDir(sub);
            File.WriteAllBytes(Path.Combine(dir, name + ".fwl"), BuildFwl(37, name, seedName));
            File.WriteAllBytes(Path.Combine(dir, name + ".db"), BuildDbHeader(37, 3600));
        }

        private string MakeChunkedWorld(string name, string sub = "worlds_local",
            string seedName = "chunkyseed", int number = 1)
        {
            var dir = Path.Combine(WorldsDir(sub), name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"_main.{number}.fwl2"), BuildFwl(41, name, seedName));
            File.WriteAllBytes(Path.Combine(dir, $"_main.{number}.db2"), BuildDbHeader(41, 5400));
            File.WriteAllBytes(Path.Combine(dir, $"_main.{number}.chunks"), new byte[] { 41, 0, 0, 0 });
            File.WriteAllBytes(Path.Combine(dir, $"_main.{number}.ok"), BitConverter.GetBytes(41));
            File.WriteAllBytes(Path.Combine(dir, "00_00__0_41.chunk"), new byte[64]);
            return dir;
        }

        private static string StoredName(string metaPath) => FwlReader.TryRead(metaPath)?.WorldName;

        // ------------------------------------------------------------------ the name rules

        /// <summary>
        /// The rules a new world name is held to, which are the rules a reference is held
        /// to plus a length. The page says the same thing to a host who is typing, and a
        /// test in the web suite holds the two numbers together.
        /// </summary>
        [Theory]
        [InlineData("Midgard", null)]
        [InlineData("Copy of Midgard", null)]
        [InlineData("world.1", null)]
        [InlineData(null, "required")]
        [InlineData("", "required")]
        [InlineData("   ", "required")]
        [InlineData("a/b", "badCharacters")]
        [InlineData("a\\b", "badCharacters")]
        [InlineData("..", "badCharacters")]
        [InlineData("../escape", "badCharacters")]
        [InlineData("what?", "badCharacters")]
        [InlineData("NUL", "badCharacters")]
        [InlineData("nul.fwl", "badCharacters")]
        [InlineData("trailing.", "badCharacters")]
        // Padding is trimmed before the name is judged, the way the window trims it before
        // it sends it, so what is left is what is held to the rule.
        [InlineData("trailing ", null)]
        [InlineData(" leading", null)]
        [InlineData("  both  ", null)]
        [InlineData(" NUL ", "badCharacters")]
        [InlineData(" a/b ", "badCharacters")]
        public void What_is_wrong_with_a_world_name(string name, string expected)
        {
            Assert.Equal(expected, WorldStore.WorldNameProblem(name));
        }

        /// <summary>
        /// The rule and the window answer the same question, so the name the rule passed is
        /// the name that lands: a copy asked for with padding around it is saved under the
        /// trimmed name rather than under a folder name with a space on the end, which is a
        /// name Windows itself will not keep.
        /// </summary>
        [Fact]
        public void A_target_name_with_padding_lands_under_the_trimmed_name()
        {
            MakeLegacyWorld("Legacy");

            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Legacy"), "  Padded  ");

            Assert.True(File.Exists(Path.Combine(WorldsDir(), "Padded.fwl")));
            Assert.Equal("Padded", StoredName(Path.Combine(WorldsDir(), "Padded.fwl")));
            Assert.NotNull(WorldStore.Find(SaveFolder, "Padded"));
        }

        [Fact]
        public void A_name_past_the_limit_is_too_long_rather_than_merely_wrong()
        {
            Assert.Equal(64, WorldStore.WorldNameMaxLength);
            Assert.Null(WorldStore.WorldNameProblem(new string('w', WorldStore.WorldNameMaxLength)));
            Assert.Equal("tooLong",
                WorldStore.WorldNameProblem(new string('w', WorldStore.WorldNameMaxLength + 1)));

            // The length is the trimmed name's, which is the name that lands. A name at the
            // limit with a space either side is not over it, and the window measures it the
            // same way.
            Assert.Null(WorldStore.WorldNameProblem(" " + new string('w', WorldStore.WorldNameMaxLength) + " "));
            Assert.Equal("tooLong",
                WorldStore.WorldNameProblem(" " + new string('w', WorldStore.WorldNameMaxLength + 1) + " "));
        }

        // ------------------------------------------------------------------ the legacy copy

        [Fact]
        public void A_legacy_world_is_copied_under_the_new_name_and_carries_it_inside()
        {
            MakeLegacyWorld("Midgard");
            var dir = WorldsDir();

            var landed = WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard");

            Assert.Equal(
                Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(landed).TrimEnd(Path.DirectorySeparatorChar));

            // Both halves of the pair landed, under the new name.
            Assert.True(File.Exists(Path.Combine(dir, "Second Midgard.fwl")));
            Assert.True(File.Exists(Path.Combine(dir, "Second Midgard.db")));

            // And the copy's header names the copy, not the world it came from.
            Assert.Equal("Second Midgard", StoredName(Path.Combine(dir, "Second Midgard.fwl")));

            // The database travelled whole.
            Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "Midgard.db")),
                         File.ReadAllBytes(Path.Combine(dir, "Second Midgard.db")));

            // The seed is the one thing a copy must keep: it is what the world IS.
            var source = FwlReader.TryRead(Path.Combine(dir, "Midgard.fwl"));
            var copy = FwlReader.TryRead(Path.Combine(dir, "Second Midgard.fwl"));
            Assert.Equal(source.SeedName, copy.SeedName);
            Assert.Equal(source.Seed, copy.Seed);

            // And the uid is the one thing it must NOT keep. It is how the game tells two
            // worlds apart: a character file keys its map exploration and its pins on it, so
            // a copy carrying the source's uid comes up with the source's map already drawn
            // and shares every pin either world ever gets. Until 1.2.4 it did.
            Assert.NotEqual(source.Uid, copy.Uid);
            Assert.NotEqual(0L, copy.Uid);

            // The world version and the worldgen version are identity too, and they travel.
            Assert.Equal(source.WorldVersion, copy.WorldVersion);
            Assert.Equal(source.WorldGenVersion, copy.WorldGenVersion);
        }

        /// <summary>
        /// The 1.0 layout, and the same rule. A world directory can hold several committed
        /// generations of ONE world, so every "_main.{N}.fwl2" in the copy takes the SAME new
        /// uid: they are saves of one world, not several worlds.
        /// </summary>
        [Fact]
        public void A_chunked_copy_gets_one_world_uid_of_its_own_across_every_generation()
        {
            var dir = MakeChunkedWorld("Midgard");
            // A second committed generation beside the first, which is what a world that has
            // been saved more than once looks like on disk.
            File.WriteAllBytes(Path.Combine(dir, "_main.2.fwl2"), BuildFwl(41, "Midgard", "chunkyseed"));
            File.WriteAllBytes(Path.Combine(dir, "_main.2.db2"), BuildDbHeader(41, 1900));
            File.WriteAllText(Path.Combine(dir, "_main.2.ok"), "");

            var landed = WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard");

            var source = FwlReader.TryRead(Path.Combine(dir, "_main.1.fwl2"));
            var first = FwlReader.TryRead(Path.Combine(landed, "_main.1.fwl2"));
            var second = FwlReader.TryRead(Path.Combine(landed, "_main.2.fwl2"));

            // The copy reads back as itself: the new name, a new uid, the same seed.
            Assert.Equal("Second Midgard", first.WorldName);
            Assert.Equal("Second Midgard", second.WorldName);
            Assert.Equal(source.SeedName, first.SeedName);
            Assert.Equal(source.Seed, first.Seed);

            Assert.NotEqual(source.Uid, first.Uid);
            Assert.NotEqual(0L, first.Uid);
            // One world, so one uid across its generations.
            Assert.Equal(first.Uid, second.Uid);

            // And the source keeps its own, byte for byte.
            Assert.Equal(1234567890123L, source.Uid);
            Assert.Equal("Midgard", source.WorldName);
        }

        /// <summary>
        /// The source is not touched by the uid rewrite either. The rewrite happens on the
        /// staging copy, before anything takes the new name, so a refusal leaves the source
        /// exactly as it was and so does a success.
        /// </summary>
        [Fact]
        public void A_chunked_copy_leaves_the_source_generation_byte_identical()
        {
            var dir = MakeChunkedWorld("Midgard");
            var before = File.ReadAllBytes(Path.Combine(dir, "_main.1.fwl2"));

            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard");

            Assert.Equal(before, File.ReadAllBytes(Path.Combine(dir, "_main.1.fwl2")));
        }

        /// <summary>
        /// The realm Duplicate path: the copy lands in ANOTHER save folder, under whatever
        /// name the new realm calls its own, and it gets a world uid of its own there too.
        /// <para>
        /// This is the overload the wiki sentence is actually about. "Duplicate a server and
        /// take the world with it" is the one place a host ends up with two live realms on
        /// one map, and it is the one place two worlds sharing a uid is worst: both realms
        /// are running, both are being played, and every client that opens either one is
        /// writing its map and its pins into the same slot of its own character file. The
        /// other overload is reached through COPY AS and is tested above; neither may be the
        /// one that was missed.
        /// </para>
        /// <para>
        /// Keeping the SAME name is allowed here, and it is the case worth driving: a world
        /// of one name in a save folder of its own is not the same world twice, so a name
        /// that has not changed is the shape in which nothing but the uid tells them apart.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("Midgard")]              // the realm form's default: the same name
        [InlineData("Second Midgard")]
        public void A_copy_into_another_save_folder_gets_its_own_world_uid(string targetName)
        {
            var dir = MakeChunkedWorld("Midgard");
            var sourceBytes = File.ReadAllBytes(Path.Combine(dir, "_main.1.fwl2"));
            var sourceRead = FwlReader.TryRead(Path.Combine(dir, "_main.1.fwl2"));

            // The new realm's own save folder, which is what a duplicated server is given.
            var destSaveFolder = Path.Combine(SaveFolder, "realm-two");
            Directory.CreateDirectory(destSaveFolder);

            var landed = WorldStore.CopyWorldAs(
                WorldStore.Find(SaveFolder, "Midgard"), targetName, destSaveFolder);

            // It landed in the DESTINATION folder and not beside the source.
            Assert.StartsWith(destSaveFolder, landed, StringComparison.OrdinalIgnoreCase);

            var copy = FwlReader.TryRead(Path.Combine(landed, "_main.1.fwl2"));
            Assert.Equal(targetName, copy.WorldName);

            // The same map, a different world.
            Assert.Equal(sourceRead.SeedName, copy.SeedName);
            Assert.Equal(sourceRead.Seed, copy.Seed);
            Assert.NotEqual(sourceRead.Uid, copy.Uid);
            Assert.NotEqual(0L, copy.Uid);

            // And the realm it was taken from is untouched, byte for byte.
            Assert.Equal(sourceBytes, File.ReadAllBytes(Path.Combine(dir, "_main.1.fwl2")));
        }

        /// <summary>
        /// The pre-1.0 pair through the same overload, because the two formats take two
        /// different code paths through CopyWorldAs and only one of them is a directory walk.
        /// </summary>
        [Fact]
        public void A_legacy_copy_into_another_save_folder_gets_its_own_world_uid()
        {
            MakeLegacyWorld("Midgard");
            var source = Path.Combine(WorldsDir(), "Midgard.fwl");
            var sourceBytes = File.ReadAllBytes(source);
            var sourceRead = FwlReader.TryRead(source);

            var destSaveFolder = Path.Combine(SaveFolder, "realm-two");
            Directory.CreateDirectory(destSaveFolder);

            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Midgard", destSaveFolder);

            var copy = FwlReader.TryRead(
                Path.Combine(destSaveFolder, "worlds_local", "Midgard.fwl"));

            Assert.Equal("Midgard", copy.WorldName);
            Assert.Equal(sourceRead.Seed, copy.Seed);
            Assert.NotEqual(sourceRead.Uid, copy.Uid);
            Assert.NotEqual(0L, copy.Uid);

            Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        }

        /// <summary>
        /// Two copies of one world are two worlds, not one world twice. Nothing about the
        /// fix works if the new uid is drawn once and reused.
        /// </summary>
        [Fact]
        public void Two_copies_of_one_world_do_not_share_a_uid_with_each_other()
        {
            MakeLegacyWorld("Midgard");
            var dir = WorldsDir();

            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Copy One");
            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Copy Two");

            var one = FwlReader.TryRead(Path.Combine(dir, "Copy One.fwl"));
            var two = FwlReader.TryRead(Path.Combine(dir, "Copy Two.fwl"));

            Assert.NotEqual(one.Uid, two.Uid);
            Assert.NotEqual(0L, one.Uid);
            Assert.NotEqual(0L, two.Uid);
            // Both are still the same world to LOOK at, which is what a duplicate is for.
            Assert.Equal(one.SeedName, two.SeedName);
        }

        [Fact]
        public void The_source_of_a_legacy_copy_is_not_touched()
        {
            MakeLegacyWorld("Midgard");
            var dir = WorldsDir();
            var fwlBefore = File.ReadAllBytes(Path.Combine(dir, "Midgard.fwl"));
            var dbBefore = File.ReadAllBytes(Path.Combine(dir, "Midgard.db"));

            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard");

            Assert.Equal(fwlBefore, File.ReadAllBytes(Path.Combine(dir, "Midgard.fwl")));
            Assert.Equal(dbBefore, File.ReadAllBytes(Path.Combine(dir, "Midgard.db")));
            Assert.Equal("Midgard", StoredName(Path.Combine(dir, "Midgard.fwl")));
        }

        /// <summary>
        /// A backup layer belongs to the world that is staying. Copying them would double
        /// the disk a copy costs and would offer the host a restore of somebody else's save
        /// history under this world's name.
        /// </summary>
        [Fact]
        public void The_backup_layers_of_the_source_stay_with_the_source()
        {
            MakeLegacyWorld("Midgard");
            var dir = WorldsDir();
            File.WriteAllBytes(Path.Combine(dir, "Midgard_backup_20260101-120000.fwl"),
                BuildFwl(37, "Midgard", "seedy"));
            File.WriteAllBytes(Path.Combine(dir, "Midgard_backup_20260101-120000.db"),
                BuildDbHeader(37, 1800));
            File.WriteAllBytes(Path.Combine(dir, "Midgard.fwl.old"), BuildFwl(37, "Midgard", "seedy"));
            File.WriteAllBytes(Path.Combine(dir, "Midgard.db.old"), BuildDbHeader(37, 1800));

            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard");

            Assert.True(File.Exists(Path.Combine(dir, "Midgard_backup_20260101-120000.fwl")));
            Assert.True(File.Exists(Path.Combine(dir, "Midgard.fwl.old")));

            Assert.False(File.Exists(Path.Combine(dir, "Second Midgard_backup_20260101-120000.fwl")));
            Assert.False(File.Exists(Path.Combine(dir, "Second Midgard.fwl.old")));
            Assert.False(File.Exists(Path.Combine(dir, "Second Midgard.db.old")));
        }

        // ------------------------------------------------------------------ the 1.0 copy

        [Fact]
        public void A_chunked_world_is_copied_as_a_whole_directory_that_names_itself()
        {
            MakeChunkedWorld("Midgard");

            var landed = WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard");

            Assert.Equal(Path.Combine(WorldsDir(), "Second Midgard"), landed);
            Assert.True(Directory.Exists(landed));

            foreach (var name in new[] { "_main.1.fwl2", "_main.1.db2", "_main.1.chunks",
                                         "_main.1.ok", "00_00__0_41.chunk" })
                Assert.True(File.Exists(Path.Combine(landed, name)), name + " did not travel");

            Assert.Equal("Second Midgard", StoredName(Path.Combine(landed, "_main.1.fwl2")));
            Assert.Equal("Midgard", StoredName(Path.Combine(WorldsDir(), "Midgard", "_main.1.fwl2")));

            // Nothing staged is left lying beside it.
            Assert.Empty(Directory.EnumerateDirectories(WorldsDir())
                .Where(d => Path.GetFileName(d).Contains(".copying-")));
        }

        /// <summary>
        /// A 1.0 world directory can hold more than one generation while the game is busy
        /// rolling one over. Every header in the copy names the copy, or a later save would
        /// hand the game back the name it came from.
        /// </summary>
        [Fact]
        public void Every_generation_in_a_chunked_copy_names_the_copy()
        {
            var dir = MakeChunkedWorld("Midgard", number: 4);
            File.WriteAllBytes(Path.Combine(dir, "_main.5.fwl2"), BuildFwl(41, "Midgard", "chunkyseed"));

            var landed = WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard");

            Assert.Equal("Second Midgard", StoredName(Path.Combine(landed, "_main.4.fwl2")));
            Assert.Equal("Second Midgard", StoredName(Path.Combine(landed, "_main.5.fwl2")));
        }

        /// <summary>
        /// A generation the game was in the middle of writing does not cost the host their
        /// world.
        /// <para>
        /// The precheck reads the COMMITTED generation, and it read fine, so the copy went
        /// ahead. The rewrite then walked the whole staged tree and refused the lot over an
        /// extra "_main.5.fwl2" with no .ok beside it, which is a generation the game has not
        /// finished writing and will discard itself. The committed world is what was being
        /// copied and it is what lands; the torn file comes across untouched and uncounted.
        /// </para>
        /// </summary>
        [Fact]
        public void A_torn_generation_beside_a_committed_one_does_not_stop_the_copy()
        {
            var dir = MakeChunkedWorld("Midgard", number: 4);

            // Half a header and no commit marker: exactly what is on disk if the game is
            // interrupted part way through a save.
            File.WriteAllBytes(Path.Combine(dir, "_main.5.fwl2"), new byte[] { 9, 9, 9, 9, 1, 2 });
            Assert.False(File.Exists(Path.Combine(dir, "_main.5.ok")));

            var landed = WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard");

            Assert.Equal("Second Midgard", StoredName(Path.Combine(landed, "_main.4.fwl2")));

            // The torn one is still there, still torn, and it is not what the copy is read by.
            Assert.True(File.Exists(Path.Combine(landed, "_main.5.fwl2")));
            Assert.Null(StoredName(Path.Combine(landed, "_main.5.fwl2")));

            // The source came out of it untouched, as it does in every test here.
            Assert.Equal("Midgard", StoredName(Path.Combine(dir, "_main.4.fwl2")));
        }

        /// <summary>
        /// The other side of the same rule: a COMMITTED generation that will not rewrite is
        /// still a refusal, because that one is a generation the game really will read.
        /// </summary>
        [Fact]
        public void A_committed_generation_that_will_not_rewrite_still_refuses_the_copy()
        {
            var dir = MakeChunkedWorld("Midgard", number: 4);

            File.WriteAllBytes(Path.Combine(dir, "_main.5.fwl2"), new byte[] { 9, 9, 9, 9, 1, 2 });
            File.WriteAllBytes(Path.Combine(dir, "_main.5.ok"), BitConverter.GetBytes(41));

            var refused = Assert.Throws<HostFacingException>(() =>
                WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard"));

            Assert.Equal("worlds.copyUnreadable", refused.MessageId);
            Assert.False(Directory.Exists(Path.Combine(WorldsDir(), "Second Midgard")));
        }

        /// <summary>
        /// The copy is a world the moment it is there: it is what the world list answers
        /// with, which is what the World field on the Settings hall paints from.
        /// </summary>
        [Fact]
        public void The_copy_is_in_the_world_list_beside_the_world_it_came_from()
        {
            MakeChunkedWorld("Midgard");
            MakeLegacyWorld("Trialgrounds");

            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard");
            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Trialgrounds"), "Proving 2");

            var names = WorldStore.GetWorldNames(SaveFolder);
            Assert.Contains("Midgard", names);
            Assert.Contains("Second Midgard", names);
            Assert.Contains("Trialgrounds", names);
            Assert.Contains("Proving 2", names);

            var copy = WorldStore.Find(SaveFolder, "Second Midgard");
            Assert.NotNull(copy);
            Assert.Equal(WorldFormat.Chunked, copy.Format);
            Assert.True(copy.IsCommitted);
        }

        /// <summary>
        /// A copy made in the second worlds folder stays in the second worlds folder. A copy
        /// that quietly hopped to worlds_local would be a world the game reads instead of
        /// the one beside it the next time both were there.
        /// </summary>
        [Fact]
        public void A_copy_lands_in_the_same_worlds_folder_the_source_sits_in()
        {
            MakeLegacyWorld("Midgard", sub: "worlds");

            WorldStore.CopyWorldAs(WorldStore.FindIn(SaveFolder, "worlds", "Midgard"), "Second Midgard");

            Assert.True(File.Exists(Path.Combine(SaveFolder, "worlds", "Second Midgard.fwl")));
            Assert.False(File.Exists(Path.Combine(SaveFolder, "worlds_local", "Second Midgard.fwl")));
        }

        // ------------------------------------------------------------------ the biome cache

        [Fact]
        public void The_biome_cache_travels_under_the_name_the_copy_now_stores()
        {
            MakeLegacyWorld("Midgard");
            var cacheDir = Path.Combine(SaveFolder, "cache");
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(Path.Combine(cacheDir, "Midgard_biomedatacache.bin"), new byte[] { 1, 2, 3 });

            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard");

            Assert.True(File.Exists(Path.Combine(cacheDir, "Second Midgard_biomedatacache.bin")));
            Assert.True(File.Exists(Path.Combine(cacheDir, "Midgard_biomedatacache.bin")));
        }

        // ------------------------------------------------------------------ refusals

        [Fact]
        public void A_name_already_in_the_save_folder_is_refused()
        {
            MakeLegacyWorld("Midgard");
            MakeChunkedWorld("Trialgrounds");

            var refused = Assert.Throws<HostFacingException>(
                () => WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Trialgrounds"));
            Assert.Equal("worlds.copyTargetExists", refused.MessageId);

            // Nothing of the copy was started.
            Assert.False(File.Exists(Path.Combine(WorldsDir(), "Trialgrounds.fwl")));
        }

        [Fact]
        public void A_name_taken_in_the_other_worlds_folder_is_refused_too()
        {
            MakeLegacyWorld("Midgard");
            MakeLegacyWorld("Second Midgard", sub: "worlds");

            var refused = Assert.Throws<HostFacingException>(
                () => WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard"));
            Assert.Equal("worlds.copyTargetExists", refused.MessageId);
        }

        /// <summary>
        /// A folder the world list refuses to return still holds its name: a backup shaped
        /// one, and one holding nothing the list would call a world. A copy landing on
        /// either would mix two saves into one folder.
        /// </summary>
        [Fact]
        public void A_name_held_by_a_folder_the_world_list_never_shows_is_refused()
        {
            MakeLegacyWorld("Midgard");
            Directory.CreateDirectory(Path.Combine(WorldsDir(), "Second Midgard"));
            File.WriteAllBytes(Path.Combine(WorldsDir(), "Second Midgard", "stray.txt"), new byte[] { 1 });

            var refused = Assert.Throws<HostFacingException>(
                () => WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard"));
            Assert.Equal("worlds.copyTargetExists", refused.MessageId);
        }

        [Fact]
        public void A_world_cannot_be_copied_onto_itself()
        {
            MakeLegacyWorld("Midgard");

            var refused = Assert.Throws<HostFacingException>(
                () => WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "midgard"));
            Assert.Equal("worlds.copyTargetExists", refused.MessageId);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("a/b")]
        [InlineData("..")]
        [InlineData("NUL")]
        public void A_name_no_world_can_be_saved_under_is_refused(string target)
        {
            MakeLegacyWorld("Midgard");

            var refused = Assert.Throws<HostFacingException>(
                () => WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), target));
            Assert.Equal("worlds.copyBadTargetRef", refused.MessageId);
        }

        /// <summary>
        /// A header that cannot be read cannot be rewritten, and a copy that kept the
        /// source's name inside it is the thing this whole path exists to avoid. The
        /// refusal has to leave the worlds folder exactly as it found it.
        /// </summary>
        [Fact]
        public void A_world_whose_header_cannot_be_read_is_refused_and_leaves_nothing_behind()
        {
            var dir = WorldsDir();
            File.WriteAllBytes(Path.Combine(dir, "Unreadable.fwl"), new byte[] { 9, 9, 9, 9 });
            File.WriteAllBytes(Path.Combine(dir, "Unreadable.db"), BuildDbHeader(37, 3600));

            var before = Directory.GetFileSystemEntries(dir).OrderBy(x => x, StringComparer.Ordinal).ToList();

            var refused = Assert.Throws<HostFacingException>(
                () => WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Unreadable"), "Second"));
            Assert.Equal("worlds.copyUnreadable", refused.MessageId);

            Assert.Equal(before, Directory.GetFileSystemEntries(dir).OrderBy(x => x, StringComparer.Ordinal).ToList());
        }

        /// <summary>
        /// The same for a 1.0 world: the directory copy is staged under a name of its own
        /// and thrown away whole, so a refusal never leaves half a world standing.
        /// </summary>
        [Fact]
        public void A_chunked_world_whose_header_cannot_be_read_leaves_no_staged_folder()
        {
            var dir = MakeChunkedWorld("Midgard");
            var world = WorldStore.Find(SaveFolder, "Midgard");
            Assert.NotNull(world);

            // Broken AFTER the world was found, which is what a file going bad under the
            // app looks like: the copy has to find it on its way past and stop there.
            File.WriteAllBytes(Path.Combine(dir, "_main.1.fwl2"), new byte[] { 9, 9, 9, 9 });

            var refused = Assert.Throws<HostFacingException>(
                () => WorldStore.CopyWorldAs(world, "Second Midgard"));
            Assert.Equal("worlds.copyUnreadable", refused.MessageId);

            Assert.False(Directory.Exists(Path.Combine(WorldsDir(), "Second Midgard")));
            Assert.Empty(Directory.EnumerateDirectories(WorldsDir())
                .Where(d => Path.GetFileName(d).Contains(".copying-")));
        }

        [Fact]
        public void A_world_that_is_nothing_is_refused_before_anything_else()
        {
            Assert.Throws<ArgumentNullException>(() => WorldStore.CopyWorldAs(null, "Second"));
        }

        /// <summary>
        /// The last thing a pre-1.0 copy does is move two staged files onto their real names,
        /// and the second one can still fail there: something is holding the name that the
        /// occupancy check found free, a directory is standing where the ".db" wants to be, a
        /// drive filled up. What had already landed used to stay: a ".fwl" with no ".db" is
        /// half of one world wearing another world's name, and the world list reads it as a
        /// world, so the host is left looking at a save that cannot be opened and was never
        /// asked for. It is taken back out, and what the page hears is a sentence it owns.
        /// </summary>
        [Fact]
        public void A_legacy_copy_whose_last_move_cannot_land_leaves_no_half_world_behind()
        {
            MakeLegacyWorld("Legacy");

            // A directory standing exactly where the copy's ".db" has to land. Every check in
            // front of the move is asking File.Exists, which a directory does not answer to,
            // so this is a name the copy believes is free right up to the move itself.
            Directory.CreateDirectory(Path.Combine(WorldsDir(), "Twin.db"));

            var refused = Assert.Throws<HostFacingException>(
                () => WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Legacy"), "Twin"));
            Assert.Equal("worlds.copyFailed", refused.MessageId);

            // No half world in the list, no half world on disk, and no staging left over.
            Assert.Equal(new[] { "Legacy" }, WorldStore.Enumerate(SaveFolder).Select(w => w.Name).ToArray());
            Assert.False(File.Exists(Path.Combine(WorldsDir(), "Twin.fwl")));
            Assert.Empty(Directory.EnumerateFiles(WorldsDir())
                .Where(f => Path.GetFileName(f).Contains(".copying-")));

            // And the world it was copied from is untouched.
            Assert.NotNull(WorldStore.Find(SaveFolder, "Legacy"));
            Assert.Equal("Legacy", StoredName(Path.Combine(WorldsDir(), "Legacy.fwl")));
        }

        /// <summary>
        /// A header carrying bytes past a payload it measures correctly is a header every
        /// reader here takes, so a world in that shape lists AND copies, and what sits past
        /// the payload comes across with it.
        /// </summary>
        [Fact]
        public void A_world_whose_header_carries_bytes_past_its_payload_still_copies()
        {
            var dir = WorldsDir();
            File.WriteAllBytes(Path.Combine(dir, "Ragnar.fwl"),
                BuildFwl(37, "Ragnar", "sd12345678").Concat(new byte[13]).ToArray());
            File.WriteAllBytes(Path.Combine(dir, "Ragnar.db"), BuildDbHeader(37, 3600));

            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Ragnar"), "Ragnar 2");

            Assert.Equal("Ragnar 2", StoredName(Path.Combine(dir, "Ragnar 2.fwl")));
            Assert.True(File.Exists(Path.Combine(dir, "Ragnar 2.db")));
            Assert.Equal("Ragnar", StoredName(Path.Combine(dir, "Ragnar.fwl")));

            // The seed is what makes the copy the same world, and it came across.
            Assert.Equal("sd12345678", FwlReader.TryRead(Path.Combine(dir, "Ragnar 2.fwl")).SeedName);
        }

        /// <summary>
        /// The one band where the copy's own pre-check is more forgiving than the rewrite,
        /// driven end to end so what a host would actually get is written down. A header whose
        /// leading size claims one, two, three or four bytes more payload than the file holds
        /// is past the file's PAYLOAD but not past its LENGTH, so the pre-check reads it and
        /// the copy gets as far as the rewrite, which then refuses it. The world list is not
        /// part of that difference: it never opens a header, so it shows this world exactly as
        /// it shows one whose header is perfect.
        /// <para>
        /// That refusal is the right answer for a header whose size field is wrong about its
        /// own file, and the point of this test is that it lands CLOSED: the sentence the host
        /// gets is one the page owns, the source is untouched, nothing new is in the worlds
        /// folder, and no staging is left standing.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void A_world_whose_header_overshoots_its_file_lists_and_is_refused_with_nothing_moved(int overshoot)
        {
            var dir = WorldsDir();
            var header = BuildFwl(37, "Over", "sd12345678");
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), header.Length - 4 + overshoot);
            File.WriteAllBytes(Path.Combine(dir, "Over.fwl"), header);
            File.WriteAllBytes(Path.Combine(dir, "Over.db"), BuildDbHeader(37, 3600));

            // The list shows it, which is how a host reaches the copy control at all.
            var world = WorldStore.Find(SaveFolder, "Over");
            Assert.NotNull(world);
            Assert.Equal("Over", FwlReader.TryRead(world.MetaPath)?.WorldName);

            var before = Directory.GetFileSystemEntries(dir).OrderBy(x => x, StringComparer.Ordinal).ToList();

            var refused = Assert.Throws<HostFacingException>(() => WorldStore.CopyWorldAs(world, "Over 2"));
            Assert.Equal("worlds.copyUnreadable", refused.MessageId);

            // Nothing landed, nothing was staged, and the source is byte for byte as it was.
            Assert.Equal(before, Directory.GetFileSystemEntries(dir).OrderBy(x => x, StringComparer.Ordinal).ToList());
            Assert.Equal(header, File.ReadAllBytes(Path.Combine(dir, "Over.fwl")));
        }

        /// <summary>
        /// The world list does not open headers, and this is here to keep that written down in
        /// something that can fail. Enumerate, GetWorldNames and Find name a world by the file
        /// or the directory it sits in, so a world whose header is unreadable by any measure is
        /// listed like any other, is offered a copy like any other, and meets the copy's own
        /// pre-check rather than a missing row.
        /// <para>
        /// Twice now a comment around the rewrite has said the opposite, that an unreadable
        /// header keeps a world out of the list and so out of reach of a copy. Both times the
        /// words were wrong and the behaviour was right. A comment cannot fail a suite, so the
        /// fact the comments lean on is asserted here instead: every shape of unreadable header
        /// is LISTED and is answered with "worlds.copyUnreadable", not with silence.
        /// </para>
        /// </summary>
        [Theory]
        // A legacy .fwl holding nothing the reader can use.
        [InlineData("Junk")]
        // A leading size one byte past the file's own length, the band past the overshoot band.
        [InlineData("Past")]
        public void An_unreadable_header_is_still_listed_and_is_refused_by_the_copys_precheck(string name)
        {
            var dir = WorldsDir();
            var bytes = name == "Past"
                ? PastTheLength(BuildFwl(37, name, "sd12345678"))
                : new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 };
            File.WriteAllBytes(Path.Combine(dir, name + ".fwl"), bytes);
            File.WriteAllBytes(Path.Combine(dir, name + ".db"), BuildDbHeader(37, 3600));

            // Unreadable by the pre-check's own reader, which is the strictly weaker of the two.
            var world = WorldStore.Find(SaveFolder, name);
            Assert.NotNull(world);
            Assert.Null(FwlReader.TryRead(world.MetaPath));

            // And listed all the same, by every door onto the list.
            Assert.Contains(name, WorldStore.GetWorldNames(SaveFolder));
            Assert.Contains(WorldStore.Enumerate(SaveFolder), w => w.Name == name);
            Assert.True(WorldStore.Exists(SaveFolder, name));

            var before = Directory.GetFileSystemEntries(dir).OrderBy(x => x, StringComparer.Ordinal).ToList();

            var refused = Assert.Throws<HostFacingException>(() => WorldStore.CopyWorldAs(world, name + " 2"));
            Assert.Equal("worlds.copyUnreadable", refused.MessageId);

            Assert.Equal(before, Directory.GetFileSystemEntries(dir).OrderBy(x => x, StringComparer.Ordinal).ToList());
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(dir, name + ".fwl")));
        }

        /// <summary>The same header with a leading size one byte past the file's length.</summary>
        private static byte[] PastTheLength(byte[] header)
        {
            var copy = (byte[])header.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(0, 4), copy.Length + 1);
            return copy;
        }

        /// <summary>
        /// The 1.0 shape of the same thing: a committed "_main.N.fwl2" full of garbage. The
        /// directory is what names the world, so the world is listed, and the refusal is the
        /// pre-check's again.
        /// </summary>
        [Fact]
        public void An_unreadable_chunked_header_is_still_listed_and_is_refused_by_the_copys_precheck()
        {
            var dir = MakeChunkedWorld("JunkChunked");
            File.WriteAllBytes(Path.Combine(dir, "_main.1.fwl2"), new byte[16]);

            var world = WorldStore.Find(SaveFolder, "JunkChunked");
            Assert.NotNull(world);
            Assert.Null(FwlReader.TryRead(world.MetaPath));
            Assert.Contains("JunkChunked", WorldStore.GetWorldNames(SaveFolder));

            var worldsDir = WorldsDir();
            var before = Directory.GetFileSystemEntries(worldsDir).OrderBy(x => x, StringComparer.Ordinal).ToList();

            var refused = Assert.Throws<HostFacingException>(() => WorldStore.CopyWorldAs(world, "JunkChunked 2"));
            Assert.Equal("worlds.copyUnreadable", refused.MessageId);

            Assert.Equal(before, Directory.GetFileSystemEntries(worldsDir).OrderBy(x => x, StringComparer.Ordinal).ToList());
        }

        // ------------------------------------------------------ the whole copied tree

        /// <summary>
        /// Every header in the copied tree is rewritten, not only the ones lying at the
        /// top of it. A "_main.{N}.fwl2" carried across in a subfolder and left alone
        /// would still be naming the world it came from, and the count of rewrites would
        /// not notice, because the top level files alone are enough to satisfy it.
        /// </summary>
        [Fact]
        public void A_header_in_a_subfolder_of_the_world_is_rewritten_too()
        {
            var dir = MakeChunkedWorld("Nest");
            var inner = Path.Combine(dir, "inner");
            Directory.CreateDirectory(inner);
            File.WriteAllBytes(Path.Combine(inner, "_main.9.fwl2"), BuildFwl(41, "Nest", "chunkyseed"));

            WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Nest"), "Nest2");

            var copied = Path.Combine(WorldsDir(), "Nest2");
            Assert.Equal("Nest2", StoredName(Path.Combine(copied, "_main.1.fwl2")));
            Assert.Equal("Nest2", StoredName(Path.Combine(copied, "inner", "_main.9.fwl2")));

            // And the world it came from is exactly as it was, at both depths.
            Assert.Equal("Nest", StoredName(Path.Combine(dir, "_main.1.fwl2")));
            Assert.Equal("Nest", StoredName(Path.Combine(inner, "_main.9.fwl2")));
        }

        // ------------------------------------------------------ the staging folder

        /// <summary>
        /// A staged copy is not a world. Every thrown refusal cleans its staging up, so
        /// the only thing that can leave one standing is the process being killed between
        /// the last file landing and the rename. What is in it at that moment is a whole
        /// generation whose header still names the world it was copied FROM, so listing it
        /// would be the two worlds under one name this whole feature exists to prevent.
        /// </summary>
        [Fact]
        public void A_staged_copy_left_behind_by_a_kill_is_not_listed_as_a_world()
        {
            MakeChunkedWorld("Midgard");

            // Exactly what a kill in that window leaves: the finished tree, still under
            // the staging name, with the source's name inside its header.
            var staged = Path.Combine(WorldsDir(), "Second Midgard.copying-20260918-101500");
            Directory.CreateDirectory(staged);
            File.WriteAllBytes(Path.Combine(staged, "_main.1.fwl2"), BuildFwl(41, "Midgard", "chunkyseed"));
            File.WriteAllBytes(Path.Combine(staged, "_main.1.db2"), BuildDbHeader(41, 5400));

            var names = WorldStore.Enumerate(SaveFolder).Select(w => w.Name).ToList();
            Assert.Equal(new[] { "Midgard" }, names);
            Assert.Null(WorldStore.Find(SaveFolder, "Second Midgard"));
        }

        /// <summary>
        /// The new uid resets the PLAYER side of the map, which lives in a character file
        /// keyed by the world uid. It does not reset the world side: what anybody wrote on
        /// a cartography table is inside the world save, and the world save is copied byte
        /// for byte. So the Atlas on the copy shows the same recorded ground and the same
        /// table pins as the world it came from, and the first player to read that table in
        /// game gets that ground back.
        /// <para>
        /// This is the fact the wording on both surfaces has to match, so it is pinned here
        /// rather than left to a sentence.
        /// </para>
        /// </summary>
        [Fact]
        public void The_world_save_itself_lands_on_the_copy_byte_for_byte()
        {
            var dir = MakeChunkedWorld("Midgard");
            var save = File.ReadAllBytes(Path.Combine(dir, "_main.1.db2"));
            var chunks = File.ReadAllBytes(Path.Combine(dir, "_main.1.chunks"));
            var tile = File.ReadAllBytes(Path.Combine(dir, "00_00__0_41.chunk"));

            var landed = WorldStore.CopyWorldAs(WorldStore.Find(SaveFolder, "Midgard"), "Second Midgard");

            Assert.Equal(save, File.ReadAllBytes(Path.Combine(landed, "_main.1.db2")));
            Assert.Equal(chunks, File.ReadAllBytes(Path.Combine(landed, "_main.1.chunks")));
            Assert.Equal(tile, File.ReadAllBytes(Path.Combine(landed, "00_00__0_41.chunk")));

            // Only the header moved, which is the whole of what this change touches.
            Assert.NotEqual(
                File.ReadAllBytes(Path.Combine(dir, "_main.1.fwl2")),
                File.ReadAllBytes(Path.Combine(landed, "_main.1.fwl2")));
        }

        /// <summary>
        /// COPY AS runs the same CopyWorldAs the Duplicate form runs, so the same world id
        /// is written on a surface the wiki recommends as a safety net: copy the world,
        /// point a realm at the copy, let people loose on that one. On 1.2.3 that kept
        /// everyone's map; from 1.2.4 it does not, and the dialog has to say so BEFORE
        /// Confirm rather than after.
        /// <para>
        /// It has to say both halves. "The map starts unexplored" on its own is not true of
        /// the product: the table record travels with the copy and the Atlas draws it.
        /// </para>
        /// </summary>
        [Fact]
        public void The_copy_dialog_says_what_the_copy_does_not_bring_with_it()
        {
            var catalog = AppSourceTree.Read(
                "ValheimBakaLoader", "WebUI", "i18n", "en.json");
            Assert.Contains("\"world.copy.note.map\"", catalog);

            var note = Newtonsoft.Json.Linq.JObject
                .Parse(catalog)["keys"]["world.copy.note.map"]["lore"].ToString();

            // The half a host loses.
            Assert.Contains("character file", note, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("blank", note, StringComparison.OrdinalIgnoreCase);
            // The half they keep, which is the half the first wording left out.
            Assert.Contains("cartography table", note, StringComparison.OrdinalIgnoreCase);

            // And the dialog hands it in, so the sentence is on the surface rather than
            // only in the catalog.
            var page = AppSourceTree.Web("app.js");
            Assert.Contains("()=>T(\"world.copy.note.map\")", page);
            Assert.Contains("function promptModal(title,placeholder,onOk,check,note){", page);
        }

        /// <summary>
        /// The marker is matched with its stamp, so a world a host really did name after
        /// one is still a world. Nothing stops a folder being called that.
        /// </summary>
        [Fact]
        public void A_world_whose_own_name_carries_the_marker_is_still_a_world()
        {
            MakeChunkedWorld("Old.copying-notes");

            var found = WorldStore.Find(SaveFolder, "Old.copying-notes");
            Assert.NotNull(found);
            Assert.Equal("Old.copying-notes", found.Name);
        }
    }
}
