using System;
using System.IO;
using System.Linq;
using System.Text;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Covers the world model across BOTH save formats: the pre-1.0 "{name}.fwl" + "{name}.db"
    /// pair and the Valheim 1.0 world DIRECTORY of "_main.{N}.fwl2/.db2/.chunks/.ok". The tree
    /// each test builds mirrors what a converted install actually looks like on disk.
    /// </summary>
    public class WorldStoreTests : IDisposable
    {
        private readonly string SaveFolder =
            Path.Combine(Path.GetTempPath(), "vbl-worldstore-tests-" + Guid.NewGuid().ToString("N"));

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

        /// <summary>A pre-1.0 world: "{name}.fwl" plus, unless suppressed, "{name}.db".</summary>
        private void MakeLegacyWorld(string name, string sub = "worlds_local", string seedName = "seedy", bool withDb = true)
        {
            var dir = WorldsDir(sub);
            File.WriteAllBytes(Path.Combine(dir, name + ".fwl"), BuildFwl(37, name, seedName));
            if (withDb) File.WriteAllBytes(Path.Combine(dir, name + ".db"), BuildDbHeader(37, 3600));
        }

        /// <summary>
        /// One "_main.{N}.*" generation inside a world directory. committed=false writes only the
        /// .fwl2, which is exactly what a world that was created but never saved looks like.
        /// </summary>
        private string MakeGeneration(string worldDir, int number, bool committed, string worldName = null,
            string seedName = "chunkyseed", double netTime = 5400)
        {
            Directory.CreateDirectory(worldDir);
            worldName ??= Path.GetFileName(worldDir);

            File.WriteAllBytes(Path.Combine(worldDir, $"_main.{number}.fwl2"), BuildFwl(41, worldName, seedName));
            if (committed)
            {
                File.WriteAllBytes(Path.Combine(worldDir, $"_main.{number}.db2"), BuildDbHeader(41, netTime));
                File.WriteAllBytes(Path.Combine(worldDir, $"_main.{number}.chunks"), new byte[] { 41, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
                // The .ok marker is 4 bytes of int32 41 and is written LAST: it is the commit.
                File.WriteAllBytes(Path.Combine(worldDir, $"_main.{number}.ok"), BitConverter.GetBytes(41));
                File.WriteAllBytes(Path.Combine(worldDir, "00_00__0_41.chunk"), new byte[64]);
            }
            return worldDir;
        }

        private string MakeChunkedWorld(string name, string sub = "worlds_local")
            => Path.Combine(WorldsDir(sub), name);

        /// <summary>
        /// A .fwl/.fwl2 header in the layout both formats share: int32 payload size, int32
        /// version, world name, seed name, int32 seed, int64 uid, int32 worldGenVersion.
        /// </summary>
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
                w.Write(2);              // worldGenVersion
                w.Write(false);          // needsDB
                w.Write(0);              // no global keys
                w.Write(0);              // no player history
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

        /// <summary>The first twelve bytes of a .db/.db2: int32 version then a double netTime.</summary>
        private static byte[] BuildDbHeader(int version, double netTime)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(version);
                w.Write(netTime);
                w.Write(new byte[32]);   // filler so the file is not suspiciously short
            }
            return ms.ToArray();
        }

        // ------------------------------------------------------------------ enumeration

        [Fact]
        public void Enumerate_ReturnsBothFormatsAndNeverABackup()
        {
            var dir = WorldsDir();

            // A pre-1.0 world that was never converted.
            MakeLegacyWorld("OldRealm");

            // A converted world: two generations, only the higher one committed.
            var chunked = MakeChunkedWorld("NewRealm");
            MakeGeneration(chunked, 4, committed: true, netTime: 9000);
            File.WriteAllBytes(Path.Combine(chunked, "_main.5.fwl2"), BuildFwl(41, "NewRealm", "chunkyseed"));

            // The originals the game renamed aside when it converted "NewRealm".
            File.WriteAllBytes(Path.Combine(dir, "NewRealm_backup_20260909-175156.fwl"), BuildFwl(37, "NewRealm", "chunkyseed"));
            File.WriteAllBytes(Path.Combine(dir, "NewRealm_backup_20260909-175156.db"), BuildDbHeader(37, 8000));

            // An automatic snapshot directory.
            MakeGeneration(Path.Combine(dir, "NewRealm_backup_auto-20260909-180000"), 4, committed: true, worldName: "NewRealm");

            // Created but never saved: a lone "_main.0.fwl2".
            var unsaved = MakeChunkedWorld("Unsaved");
            MakeGeneration(unsaved, 0, committed: false, worldName: "Unsaved");

            var worlds = WorldStore.Enumerate(SaveFolder);

            Assert.Equal(
                new[] { "NewRealm", "OldRealm", "Unsaved" },
                worlds.Select(w => w.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

            var old = worlds.Single(w => w.Name == "OldRealm");
            Assert.Equal(WorldFormat.Legacy, old.Format);
            Assert.Equal("Legacy", old.FormatLabel);
            Assert.Null(old.SaveNumber);
            Assert.True(old.IsCommitted);
            Assert.EndsWith("OldRealm.fwl", old.MetaPath);
            Assert.EndsWith("OldRealm.db", old.DbPath);

            var live = worlds.Single(w => w.Name == "NewRealm");
            Assert.Equal(WorldFormat.Chunked, live.Format);
            Assert.Equal("1.0", live.FormatLabel);
            // Generation 5 has only a .fwl2, so the live generation is still the committed 4.
            Assert.Equal(4, live.SaveNumber);
            Assert.True(live.IsCommitted);
            Assert.EndsWith("_main.4.fwl2", live.MetaPath);
            Assert.EndsWith("_main.4.db2", live.DbPath);
            Assert.Equal(Path.Combine(dir, "NewRealm"), live.Folder);

            var never = worlds.Single(w => w.Name == "Unsaved");
            Assert.Equal(WorldFormat.Chunked, never.Format);
            Assert.False(never.IsCommitted);
            Assert.Equal(0, never.SaveNumber);
            Assert.Null(never.DbPath);
        }

        [Fact]
        public void Enumerate_DedupesByNameWithWorldsLocalWinning()
        {
            MakeLegacyWorld("Shared", sub: "worlds", seedName: "in-worlds");
            MakeLegacyWorld("Shared", sub: "worlds_local", seedName: "in-worlds-local");

            var worlds = WorldStore.Enumerate(SaveFolder);
            var only = Assert.Single(worlds);
            Assert.Equal("Shared", only.Name);
            Assert.Equal("worlds_local", only.Sub);
        }

        [Fact]
        public void Enumerate_ChunkedWorldSupersedesASameNamedLegacyLeftover()
        {
            var dir = WorldsDir();
            MakeLegacyWorld("Both");
            MakeGeneration(Path.Combine(dir, "Both"), 1, committed: true);

            var only = Assert.Single(WorldStore.Enumerate(SaveFolder));
            Assert.Equal(WorldFormat.Chunked, only.Format);
        }

        [Fact]
        public void Find_ResolvesEitherFormatAndExistsAgreesWithIt()
        {
            MakeLegacyWorld("OldRealm");
            MakeGeneration(MakeChunkedWorld("NewRealm"), 2, committed: true);

            Assert.Equal(WorldFormat.Legacy, WorldStore.Find(SaveFolder, "OldRealm").Format);
            Assert.Equal(WorldFormat.Chunked, WorldStore.Find(SaveFolder, "NewRealm").Format);
            Assert.True(WorldStore.Exists(SaveFolder, "NewRealm"));
            Assert.False(WorldStore.Exists(SaveFolder, "Nowhere"));
            Assert.Null(WorldStore.Find(SaveFolder, "Nowhere"));
        }

        [Fact]
        public void Find_IgnoresABackupDirectoryEvenWhenAskedForItByName()
        {
            var dir = WorldsDir();
            MakeGeneration(Path.Combine(dir, "Realm_backup_auto-20260909-180000"), 1, committed: true, worldName: "Realm");

            Assert.Null(WorldStore.Find(SaveFolder, "Realm_backup_auto-20260909-180000"));
            Assert.Empty(WorldStore.Enumerate(SaveFolder));
        }

        [Fact]
        public void Enumerate_SurvivesAMissingSaveFolder()
        {
            Assert.Empty(WorldStore.Enumerate(Path.Combine(SaveFolder, "does-not-exist")));
            Assert.Empty(WorldStore.Enumerate(null));
            Assert.Empty(WorldStore.GetWorldNames(SaveFolder));
        }

        // ------------------------------------------------------------------ backup classification

        [Theory]
        // Directory layers the 1.0 game writes.
        [InlineData("Midgard_backup_auto-20260909-180000", "Midgard", WorldBackupKind.Auto)]
        [InlineData("Midgard_backup_restore-20260909-180000", "Midgard", WorldBackupKind.Restore)]
        [InlineData("Midgard_backup_cloud-20260909-180000", "Midgard", WorldBackupKind.Cloud)]
        // The untouched pre-conversion originals: "_backup_" with no infix.
        [InlineData("Midgard_backup_20260909-175156", "Midgard", WorldBackupKind.Legacy)]
        // The 14-character stamp the pre-1.0 game used.
        [InlineData("Midgard_backup_auto-20220620101500", "Midgard", WorldBackupKind.Auto)]
        [InlineData("Midgard_20220620-101500", "Midgard", WorldBackupKind.Other)]
        // A world name that itself ends in an underscore word must not be mangled.
        [InlineData("My_Great_World_backup_auto-20260909-180000", "My_Great_World", WorldBackupKind.Auto)]
        public void TryParseBackupName_ClassifiesEveryShape(string name, string world, WorldBackupKind kind)
        {
            Assert.True(WorldStore.TryParseBackupName(name, out var owner, out var parsed, out var stamp));
            Assert.Equal(world, owner);
            Assert.Equal(kind, parsed);
            Assert.NotNull(stamp);
        }

        [Theory]
        [InlineData("Midgard")]
        [InlineData("Midgard2")]
        [InlineData("worlds_local")]
        [InlineData("Realm_backup_notatimestamp")]
        [InlineData("")]
        public void TryParseBackupName_LeavesRealWorldNamesAlone(string name)
        {
            Assert.False(WorldStore.TryParseBackupName(name, out _, out _, out _));
            Assert.False(WorldStore.IsBackupDirectoryName(name));
        }

        [Theory]
        [InlineData("Midgard_backup_auto-20260909-180000.fwl", "Midgard")]
        [InlineData("Midgard_backup_20260909-175156.db", "Midgard")]
        [InlineData("Midgard.fwl.old", "Midgard")]
        [InlineData("Midgard.db.old", "Midgard")]
        [InlineData("Midgard_20220620-101500.fwl", "Midgard")]
        public void IsBackupFileName_CatchesEveryLegacyLayerShape(string file, string world)
        {
            Assert.True(WorldStore.IsBackupFileName(file, out var owner));
            Assert.Equal(world, owner);
        }

        [Theory]
        [InlineData("Midgard.fwl")]
        [InlineData("Midgard.db")]
        [InlineData("Midgard2.fwl")]
        public void IsBackupFileName_LeavesTheLivePairAlone(string file)
        {
            Assert.False(WorldStore.IsBackupFileName(file, out _));
        }

        [Fact]
        public void EnumerateBackups_FindsFileAndDirectoryLayersOfTheRightWorldOnly()
        {
            var dir = WorldsDir();
            MakeLegacyWorld("Midgard");
            MakeLegacyWorld("Midgard2");   // the near-miss neighbour that must never be swallowed

            // The pre-1.0 originals kept when the game converted the world.
            File.WriteAllBytes(Path.Combine(dir, "Midgard_backup_20260909-175156.fwl"), BuildFwl(37, "Midgard", "s"));
            File.WriteAllBytes(Path.Combine(dir, "Midgard_backup_20260909-175156.db"), BuildDbHeader(37, 1800));
            // The last-known-good pair.
            File.WriteAllBytes(Path.Combine(dir, "Midgard.fwl.old"), BuildFwl(37, "Midgard", "s"));
            File.WriteAllBytes(Path.Combine(dir, "Midgard.db.old"), BuildDbHeader(37, 1800));
            // Directory layers.
            MakeGeneration(Path.Combine(dir, "Midgard_backup_auto-20260909-180000"), 3, committed: true, worldName: "Midgard", netTime: 7200);
            MakeGeneration(Path.Combine(dir, "Midgard_backup_restore-20260909-190000"), 3, committed: false, worldName: "Midgard");
            // A neighbour's layer, which must stay with the neighbour.
            MakeGeneration(Path.Combine(dir, "Midgard2_backup_auto-20260909-180000"), 1, committed: true, worldName: "Midgard2");

            var layers = WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Midgard");

            Assert.Equal(4, layers.Count);
            Assert.All(layers, l => Assert.Equal("Midgard", l.World));

            var auto = layers.Single(l => l.Kind == WorldBackupKind.Auto);
            Assert.True(auto.IsDirectory);
            Assert.True(auto.IsCommitted);
            Assert.Equal(3, auto.SaveNumber);
            Assert.EndsWith("_main.3.db2", auto.DbPath);
            Assert.True(auto.SizeBytes > 0);

            var restore = layers.Single(l => l.Kind == WorldBackupKind.Restore);
            Assert.True(restore.IsDirectory);
            Assert.False(restore.IsCommitted);   // no .ok yet, so nothing may be copied out of it
            Assert.Null(restore.DbPath);

            var original = layers.Single(l => l.Kind == WorldBackupKind.Legacy);
            Assert.False(original.IsDirectory);
            Assert.True(original.HasDb);
            Assert.Equal("Midgard_backup_20260909-175156.fwl", original.Name);

            var lastGood = layers.Single(l => l.Kind == WorldBackupKind.Old);
            Assert.Equal("Midgard.fwl.old", lastGood.Name);
            Assert.EndsWith("Midgard.db.old", lastGood.DbPath);
        }

        [Fact]
        public void EnumerateBackups_NeverReturnsTheLiveWorld()
        {
            MakeLegacyWorld("Midgard");
            MakeGeneration(MakeChunkedWorld("NewRealm"), 1, committed: true);

            Assert.Empty(WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Midgard"));
            Assert.Empty(WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "NewRealm"));
        }

        [Fact]
        public void KindToken_MapsEveryKindToItsWireName()
        {
            Assert.Equal("auto", WorldStore.KindToken(WorldBackupKind.Auto));
            Assert.Equal("restore", WorldStore.KindToken(WorldBackupKind.Restore));
            Assert.Equal("cloud", WorldStore.KindToken(WorldBackupKind.Cloud));
            Assert.Equal("legacy", WorldStore.KindToken(WorldBackupKind.Legacy));
            Assert.Equal("old", WorldStore.KindToken(WorldBackupKind.Old));
            Assert.Equal("other", WorldStore.KindToken(WorldBackupKind.Other));
        }

        // ------------------------------------------------------------------ reference safety

        [Theory]
        [InlineData("../../evil")]
        [InlineData("..\\..\\evil")]
        [InlineData("sub/Midgard_backup_auto-20260909-180000")]
        [InlineData("sub\\Midgard_backup_auto-20260909-180000")]
        [InlineData("C:\\Windows\\System32")]
        [InlineData("..")]
        [InlineData("Midgard_backup_*")]
        [InlineData("")]
        [InlineData(null)]
        // Windows resolves "." and strips a trailing dot or space on its way to a path, so each
        // of these names one thing and lands on another - for a delete, on the whole folder.
        [InlineData(".")]
        [InlineData("Midgard.")]
        [InlineData("Midgard ")]
        [InlineData(" Midgard")]
        [InlineData("Midgard\t")]
        // Reserved device names never reach a folder at all.
        [InlineData("NUL")]
        [InlineData("con")]
        [InlineData("COM1")]
        [InlineData("LPT9.fwl")]
        public void IsSafeReferenceToken_RefusesAnythingThatCouldLeaveTheWorldsFolder(string token)
        {
            Assert.False(WorldStore.IsSafeReferenceToken(token));
        }

        [Fact]
        public void Find_CannotBeTalkedIntoReturningTheWorldsFolderItself()
        {
            // A loose generation directly in worlds_local is what a mis-extracted backup leaves,
            // and it used to make "." look like a world whose folder was the worlds folder.
            MakeGeneration(WorldsDir(), 1, committed: true, worldName: "Loose");
            MakeLegacyWorld("Alpha");

            Assert.Null(WorldStore.Find(SaveFolder, "."));
            Assert.Null(WorldStore.Find(SaveFolder, ".."));
            Assert.Null(WorldStore.Find(SaveFolder, "Alpha "));
            Assert.Null(WorldStore.Find(SaveFolder, "Alpha."));

            // The real world is still perfectly findable.
            Assert.Equal("Alpha", WorldStore.Find(SaveFolder, "Alpha")?.Name);
        }

        [Theory]
        [InlineData("Midgard")]
        [InlineData("Midgard_backup_auto-20260909-180000")]
        [InlineData("Midgard.fwl.old")]
        [InlineData("My Great World")]
        public void IsSafeReferenceToken_AcceptsAPlainName(string token)
        {
            Assert.True(WorldStore.IsSafeReferenceToken(token));
        }

        [Fact]
        public void ResolveBackupLayer_RefusesTraversalAndTheLiveWorldAndAStrangersLayer()
        {
            var dir = WorldsDir();
            MakeLegacyWorld("Midgard");
            MakeGeneration(MakeChunkedWorld("NewRealm"), 1, committed: true);
            MakeGeneration(Path.Combine(dir, "Midgard_backup_auto-20260909-180000"), 1, committed: true, worldName: "Midgard");
            MakeGeneration(Path.Combine(dir, "NewRealm_backup_auto-20260909-180000"), 1, committed: true, worldName: "NewRealm");

            // The one legitimate reference resolves.
            var ok = WorldStore.ResolveBackupLayer(SaveFolder, "worlds_local", "Midgard", "Midgard_backup_auto-20260909-180000");
            Assert.NotNull(ok);
            Assert.Equal(WorldBackupKind.Auto, ok.Kind);

            // Traversal, in either separator, and an absolute path.
            Assert.Null(WorldStore.ResolveBackupLayer(SaveFolder, "worlds_local", "Midgard", "../Midgard.fwl"));
            Assert.Null(WorldStore.ResolveBackupLayer(SaveFolder, "worlds_local", "Midgard", "..\\..\\Midgard.fwl"));
            Assert.Null(WorldStore.ResolveBackupLayer(SaveFolder, "worlds_local", "Midgard", "worlds\\Midgard.fwl"));
            Assert.Null(WorldStore.ResolveBackupLayer(SaveFolder, "worlds_local", "Midgard", @"C:\Windows\System32"));
            Assert.Null(WorldStore.ResolveBackupLayer(SaveFolder, "worlds_local", "../Midgard", "Midgard_backup_auto-20260909-180000"));

            // The LIVE world can never be named, in either format.
            Assert.Null(WorldStore.ResolveBackupLayer(SaveFolder, "worlds_local", "Midgard", "Midgard.fwl"));
            Assert.Null(WorldStore.ResolveBackupLayer(SaveFolder, "worlds_local", "NewRealm", "NewRealm"));

            // Another world's layer stays with that world.
            Assert.Null(WorldStore.ResolveBackupLayer(SaveFolder, "worlds_local", "Midgard", "NewRealm_backup_auto-20260909-180000"));

            // Only the two real world subfolders are addressable.
            Assert.Null(WorldStore.ResolveBackupLayer(SaveFolder, "characters_local", "Midgard", "Midgard_backup_auto-20260909-180000"));
            Assert.Null(WorldStore.ResolveBackupLayer(SaveFolder, "../worlds_local", "Midgard", "Midgard_backup_auto-20260909-180000"));
        }

        [Fact]
        public void IsBackupShapedFor_TellsABadReferenceApartFromAVanishedOne()
        {
            Assert.True(WorldStore.IsBackupShapedFor("Midgard_backup_auto-20260909-180000", "Midgard"));
            Assert.True(WorldStore.IsBackupShapedFor("Midgard.fwl.old", "Midgard"));
            Assert.False(WorldStore.IsBackupShapedFor("Midgard.fwl", "Midgard"));
            Assert.False(WorldStore.IsBackupShapedFor("Midgard2_backup_auto-20260909-180000", "Midgard"));
        }

        // ------------------------------------------------------------------ copy, delete, day

        [Fact]
        public void CopyWorld_CopiesAChunkedDirectoryAndItsBiomeCacheWithoutTouchingTheSource()
        {
            var chunked = MakeChunkedWorld("NewRealm");
            MakeGeneration(chunked, 2, committed: true);
            var cacheDir = Path.Combine(SaveFolder, "cache");
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(Path.Combine(cacheDir, "NewRealm_biomedatacache.bin"), new byte[] { 1, 2, 3 });

            var dest = Path.Combine(SaveFolder, "adopted");
            var source = WorldStore.Find(SaveFolder, "NewRealm");
            WorldStore.CopyWorld(source, dest);

            var copied = WorldStore.Find(dest, "NewRealm");
            Assert.NotNull(copied);
            Assert.Equal(WorldFormat.Chunked, copied.Format);
            Assert.Equal(2, copied.SaveNumber);
            Assert.True(File.Exists(Path.Combine(dest, "cache", "NewRealm_biomedatacache.bin")));

            // The original is left exactly where it was.
            Assert.True(Directory.Exists(chunked));
            Assert.NotNull(WorldStore.Find(SaveFolder, "NewRealm"));
        }

        [Fact]
        public void CopyWorld_CopiesALegacyPairIncludingItsOldSiblings()
        {
            var dir = WorldsDir();
            MakeLegacyWorld("OldRealm");
            File.WriteAllBytes(Path.Combine(dir, "OldRealm.fwl.old"), BuildFwl(37, "OldRealm", "seedy"));
            File.WriteAllBytes(Path.Combine(dir, "OldRealm.db.old"), BuildDbHeader(37, 1800));

            var dest = Path.Combine(SaveFolder, "adopted");
            WorldStore.CopyWorld(WorldStore.Find(SaveFolder, "OldRealm"), dest);

            var destWorlds = Path.Combine(dest, "worlds_local");
            Assert.True(File.Exists(Path.Combine(destWorlds, "OldRealm.fwl")));
            Assert.True(File.Exists(Path.Combine(destWorlds, "OldRealm.db")));
            Assert.True(File.Exists(Path.Combine(destWorlds, "OldRealm.fwl.old")));
            Assert.True(File.Exists(Path.Combine(destWorlds, "OldRealm.db.old")));
        }

        [Fact]
        public void DeleteWorld_TakesTheLayersAndTheBiomeCacheWithIt()
        {
            var dir = WorldsDir();
            var chunked = MakeChunkedWorld("NewRealm");
            MakeGeneration(chunked, 1, committed: true);
            MakeGeneration(Path.Combine(dir, "NewRealm_backup_auto-20260909-180000"), 1, committed: true, worldName: "NewRealm");
            File.WriteAllBytes(Path.Combine(dir, "NewRealm_backup_20260909-175156.fwl"), BuildFwl(37, "NewRealm", "s"));
            File.WriteAllBytes(Path.Combine(dir, "NewRealm_backup_20260909-175156.db"), BuildDbHeader(37, 1800));

            var cacheDir = Path.Combine(SaveFolder, "cache");
            Directory.CreateDirectory(cacheDir);
            var cache = Path.Combine(cacheDir, "NewRealm_biomedatacache.bin");
            File.WriteAllBytes(cache, new byte[] { 1 });

            // A neighbour that must be left completely alone.
            MakeLegacyWorld("Untouched");

            var deleted = WorldStore.DeleteWorld(SaveFolder, "NewRealm");

            Assert.False(Directory.Exists(chunked));
            Assert.False(Directory.Exists(Path.Combine(dir, "NewRealm_backup_auto-20260909-180000")));
            Assert.False(File.Exists(Path.Combine(dir, "NewRealm_backup_20260909-175156.fwl")));
            Assert.False(File.Exists(cache));
            Assert.Contains("NewRealm_biomedatacache.bin", deleted);
            Assert.NotNull(WorldStore.Find(SaveFolder, "Untouched"));
        }

        [Fact]
        public void ReplaceDirectoryContents_LeavesNoStaleChunkFileBehind()
        {
            var live = MakeChunkedWorld("NewRealm");
            MakeGeneration(live, 9, committed: true);
            File.WriteAllBytes(Path.Combine(live, "07_07__0_41.chunk"), new byte[16]);

            var layer = Path.Combine(WorldsDir(), "NewRealm_backup_auto-20260909-180000");
            MakeGeneration(layer, 3, committed: true, worldName: "NewRealm");

            WorldStore.ReplaceDirectoryContents(layer, live);

            Assert.False(File.Exists(Path.Combine(live, "_main.9.fwl2")));
            Assert.False(File.Exists(Path.Combine(live, "07_07__0_41.chunk")));
            Assert.Equal(3, WorldStore.Find(SaveFolder, "NewRealm").SaveNumber);

            // The staging and scratch directories are gone, so the Barrow does not list them.
            Assert.Equal(
                new[] { "NewRealm", "NewRealm_backup_auto-20260909-180000" },
                Directory.GetDirectories(WorldsDir()).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        }

        [Fact]
        public void ReplaceDirectoryContents_LeavesTheLiveWorldAloneWhenTheCopyIsNotAWorld()
        {
            var live = MakeChunkedWorld("NewRealm");
            MakeGeneration(live, 9, committed: true);

            // A layer whose newest generation was never committed. Emptying the live world and
            // then copying this in would leave a world with no finished save in it at all.
            var layer = Path.Combine(WorldsDir(), "NewRealm_backup_auto-20260909-180000");
            MakeGeneration(layer, 3, committed: false, worldName: "NewRealm");

            var failure = Assert.ThrowsAny<Exception>(
                () => WorldStore.ReplaceDirectoryContents(layer, live, "NewRealm_backup_restore-20260909-181500"));

            // The message has to name the way back, because that is all the host has.
            Assert.Contains("NewRealm_backup_restore-20260909-181500", failure.Message);

            var untouched = WorldStore.Find(SaveFolder, "NewRealm");
            Assert.NotNull(untouched);
            Assert.Equal(9, untouched.SaveNumber);
            Assert.True(File.Exists(Path.Combine(live, "_main.9.ok")));

            // No half-finished staging directory is left lying beside the world either.
            Assert.Equal(
                new[] { "NewRealm", "NewRealm_backup_auto-20260909-180000" },
                Directory.GetDirectories(WorldsDir()).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        }

        // ------------------------------------------------------------- one name, one world

        [Fact]
        public void Enumerate_And_Find_Agree_When_The_Same_Name_Sits_In_Both_Subfolders()
        {
            // The exact tree the two rules disagreed on: Enumerate deduped format first (the
            // chunked world in "worlds" wins) while Find deduped subfolder first (the legacy
            // pair in "worlds_local" wins), so the row the host read and the world a restore
            // wrote to were two different worlds.
            MakeLegacyWorld("Shared");
            MakeGeneration(MakeChunkedWorld("Shared", "worlds"), 7, committed: true);

            var listed = Assert.Single(WorldStore.Enumerate(SaveFolder).Where(w => w.Name == "Shared"));
            var found = WorldStore.Find(SaveFolder, "Shared");

            Assert.NotNull(found);
            Assert.Equal(listed.Folder, found.Folder);
            Assert.Equal(listed.Sub, found.Sub);
            Assert.Equal(WorldFormat.Chunked, found.Format);
            Assert.Equal("worlds", found.Sub);
        }

        [Fact]
        public void FindIn_Stays_In_The_Subfolder_It_Was_Asked_About()
        {
            MakeLegacyWorld("Shared");
            MakeGeneration(MakeChunkedWorld("Shared", "worlds"), 7, committed: true);

            var local = WorldStore.FindIn(SaveFolder, "worlds_local", "Shared");
            var worlds = WorldStore.FindIn(SaveFolder, "worlds", "Shared");

            Assert.NotNull(local);
            Assert.NotNull(worlds);
            Assert.Equal(WorldFormat.Legacy, local.Format);
            Assert.Equal("worlds_local", local.Sub);
            Assert.Equal(WorldFormat.Chunked, worlds.Format);
            Assert.Equal("worlds", worlds.Sub);
            Assert.NotEqual(local.Folder, worlds.Folder);

            // A world that is only in one of them is not invented in the other.
            MakeLegacyWorld("OnlyLocal");
            Assert.NotNull(WorldStore.FindIn(SaveFolder, "worlds_local", "OnlyLocal"));
            Assert.Null(WorldStore.FindIn(SaveFolder, "worlds", "OnlyLocal"));
        }

        [Fact]
        public void TryReadWorldDay_ReadsTheSameHeaderInBothFormats()
        {
            var dir = WorldsDir();
            var legacyDb = Path.Combine(dir, "OldRealm.db");
            File.WriteAllBytes(legacyDb, BuildDbHeader(37, 5400));      // 3 days
            var chunkedDb = Path.Combine(dir, "NewRealm.db2");
            File.WriteAllBytes(chunkedDb, BuildDbHeader(41, 5400));

            Assert.Equal(3, WorldStore.TryReadWorldDay(legacyDb));
            Assert.Equal(3, WorldStore.TryReadWorldDay(chunkedDb));
            Assert.Null(WorldStore.TryReadWorldDay(null));
            Assert.Null(WorldStore.TryReadWorldDay(Path.Combine(dir, "nope.db")));

            var garbage = Path.Combine(dir, "garbage.db2");
            File.WriteAllBytes(garbage, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0 });
            Assert.Null(WorldStore.TryReadWorldDay(garbage));
        }

        [Fact]
        public void EnsureSaveFolderLayout_CreatesWorldsLocalAndTheCacheSibling()
        {
            var folder = Path.Combine(SaveFolder, "fresh");
            WorldStore.EnsureSaveFolderLayout(folder);

            Assert.True(Directory.Exists(Path.Combine(folder, "worlds_local")));
            Assert.True(Directory.Exists(Path.Combine(folder, "cache")));
        }

        [Fact]
        public void BiomeCachePath_PointsAtTheFileTheGameWrites()
        {
            Assert.Equal(
                Path.Combine(SaveFolder, "cache", "Midgard_biomedatacache.bin"),
                WorldStore.BiomeCachePath(SaveFolder, "Midgard"));
        }
    }
}
