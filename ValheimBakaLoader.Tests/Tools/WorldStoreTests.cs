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
        // The 14-character stamp the pre-1.0 game used, still on this machine's own auto layers.
        [InlineData("Midgard_backup_auto-20220620101500", "Midgard", WorldBackupKind.Auto)]
        // The game separates the six date and time groups with any number of hyphens, so all of
        // these are the same stamp to it and all of them are layers, not worlds.
        [InlineData("Midgard_backup_2026-09-09-18-00-00", "Midgard", WorldBackupKind.Legacy)]
        [InlineData("Midgard_backup_auto-2026-09-09-180000", "Midgard", WorldBackupKind.Auto)]
        [InlineData("Midgard_backup_cloud-20260909180000", "Midgard", WorldBackupKind.Cloud)]
        // The pattern is not anchored either: trailing text after the stamp is still a layer.
        [InlineData("Midgard_backup_auto-20260909-180000 (copy)", "Midgard", WorldBackupKind.Auto)]
        // A world of its own whose name ends in "_backup" keeps its own layers.
        [InlineData("Midgard_backup_backup_20260909-180000", "Midgard_backup", WorldBackupKind.Legacy)]
        // A world name that itself ends in an underscore word must not be mangled.
        [InlineData("My_Great_World_backup_auto-20260909-180000", "My_Great_World", WorldBackupKind.Auto)]
        // A stamped name with NO "_backup_" marker is the game's rolling save: it belongs to
        // the name trimmed at the LAST underscore and the game never opens it as a world.
        [InlineData("Midgard_20220620-101500", "Midgard", WorldBackupKind.Other)]
        [InlineData("Midgard_20220620101500", "Midgard", WorldBackupKind.Other)]
        // The marker is in the name but not at the second-to-last underscore, so the game reads
        // no marker at all and trims at the last one instead.
        [InlineData("My_backup_World_20260909-180000", "My_backup_World", WorldBackupKind.Other)]
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
        // A stamp but no underscore at all: the game has nothing to trim at, so it is a save.
        [InlineData("20220620-101500")]
        // The marker is there but the numbers are not a date the game can read, so it reads no
        // stamp at all and treats the name as an ordinary save name.
        [InlineData("Midgard_backup_20261340-000000")]
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
        [InlineData("Final Sunset_backup_auto-20260722065046.fwl", "Final Sunset")]
        [InlineData("Midgard_backup_2026-09-09-17-51-56.fwl", "Midgard")]
        // The rolling shape: no marker, so the game trims at the last underscore and files it
        // under "Midgard" as a backup file it will never elect as the world.
        [InlineData("Midgard_20220620-101500.fwl", "Midgard")]
        [InlineData("Midgard_20220620-101500.db", "Midgard")]
        public void IsBackupFileName_CatchesEveryLegacyLayerShape(string file, string world)
        {
            Assert.True(WorldStore.IsBackupFileName(file, out var owner));
            Assert.Equal(world, owner);
        }

        [Theory]
        [InlineData("Midgard.fwl")]
        [InlineData("Midgard.db")]
        [InlineData("Midgard2.fwl")]
        // An underscore and a number that is not a date the game can read is just a name.
        [InlineData("Midgard_2.fwl")]
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

        // ------------------------------------------------------- the game's own naming (W-06)

        [Fact]
        public void A_layer_the_game_would_recognise_is_never_listed_as_a_world()
        {
            var dir = WorldsDir();
            MakeLegacyWorld("Midgard");

            // Hyphens between every date and time group. The game reads this as a layer of
            // "Midgard"; a shape that only allowed one hyphen in a fixed spot read it as a world.
            File.WriteAllBytes(Path.Combine(dir, "Midgard_backup_2026-09-09-17-51-56.fwl"), BuildFwl(37, "Midgard", "s"));
            File.WriteAllBytes(Path.Combine(dir, "Midgard_backup_2026-09-09-17-51-56.db"), BuildDbHeader(37, 1800));

            // A directory layer with the same lenient stamp, and one with text after the stamp.
            MakeGeneration(Path.Combine(dir, "Midgard_backup_auto-2026-09-09-18-00-00"), 2, committed: true, worldName: "Midgard");
            MakeGeneration(Path.Combine(dir, "Midgard_backup_auto-20260909-190000 (copy)"), 2, committed: true, worldName: "Midgard");

            Assert.Equal(new[] { "Midgard" }, WorldStore.GetWorldNames(SaveFolder));

            var layers = WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Midgard");
            Assert.Equal(
                new[]
                {
                    "Midgard_backup_2026-09-09-17-51-56.fwl",
                    "Midgard_backup_auto-2026-09-09-18-00-00",
                    "Midgard_backup_auto-20260909-190000 (copy)",
                },
                layers.Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

            Assert.Equal(
                new DateTime(2026, 9, 9, 17, 51, 56),
                layers.Single(l => l.Name.EndsWith(".fwl")).Stamp);
        }

        [Fact]
        public void The_live_save_folder_classifies_exactly_the_way_the_game_names_it()
        {
            // The real worlds_local on this machine, name for name: one legacy world still on the
            // pre-1.0 stamp convention for its own auto layers, two orphaned backup sets whose
            // worlds are long gone, and the client's map render caches.
            var dir = WorldsDir();

            MakeLegacyWorld("Final Sunset");
            File.WriteAllBytes(Path.Combine(dir, "Final Sunset.fwl.old"), BuildFwl(37, "Final Sunset", "s"));
            File.WriteAllBytes(Path.Combine(dir, "Final Sunset.db.old"), BuildDbHeader(37, 1800));

            var autoStamps = new[] { "20260722065046", "20260722165442", "20260819074545" };
            foreach (var stamp in autoStamps)
            {
                File.WriteAllBytes(Path.Combine(dir, "Final Sunset_backup_auto-" + stamp + ".fwl"), BuildFwl(37, "Final Sunset", "s"));
                File.WriteAllBytes(Path.Combine(dir, "Final Sunset_backup_auto-" + stamp + ".db"), BuildDbHeader(37, 1800));
            }

            File.WriteAllBytes(Path.Combine(dir, "Final Sunset_backup_restore-20260607-024757.fwl"), BuildFwl(37, "Final Sunset", "s"));
            File.WriteAllBytes(Path.Combine(dir, "Final Sunset_backup_restore-20260607-024757.db"), BuildDbHeader(37, 1800));

            File.WriteAllBytes(Path.Combine(dir, "Azula.fwl.old"), BuildFwl(37, "Azula", "s"));
            File.WriteAllBytes(Path.Combine(dir, "Azula_backup_20231120-133010.fwl"), BuildFwl(37, "Azula", "s"));
            File.WriteAllBytes(Path.Combine(dir, "Azula_backup_20231120-133010.db"), BuildDbHeader(37, 1800));

            foreach (var cache in new[] { "Azula_forestMaskTexCache", "Azula_heightTexCache", "Azula_mapTexCache" })
                File.WriteAllBytes(Path.Combine(dir, cache), new byte[] { 1, 2, 3, 4 });

            File.WriteAllBytes(Path.Combine(dir, "NewAge_backup_20251214-171043.fwl"), BuildFwl(37, "NewAge", "s"));
            File.WriteAllBytes(Path.Combine(dir, "NewAge_backup_20251214-171043.db"), BuildDbHeader(37, 1800));

            // Exactly one live world. Every layer, and every client cache file, stays out of it.
            Assert.Equal(new[] { "Final Sunset" }, WorldStore.GetWorldNames(SaveFolder));

            var sunset = WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Final Sunset");
            Assert.Equal(5, sunset.Count);
            Assert.Equal(WorldBackupKind.Old, sunset.Single(l => l.Name == "Final Sunset.fwl.old").Kind);
            Assert.Equal(WorldBackupKind.Restore, sunset.Single(l => l.Name == "Final Sunset_backup_restore-20260607-024757.fwl").Kind);
            Assert.All(
                sunset.Where(l => l.Name.Contains("_backup_auto-")),
                l => Assert.Equal(WorldBackupKind.Auto, l.Kind));
            Assert.All(sunset, l => Assert.True(l.HasDb));

            // The pre-1.0 stamp with no hyphen at all still reads as a real date.
            Assert.Equal(
                new DateTime(2026, 7, 22, 6, 50, 46),
                sunset.Single(l => l.Name == "Final Sunset_backup_auto-20260722065046.fwl").Stamp);

            var azula = WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Azula");
            Assert.Equal(
                new[] { "Azula.fwl.old", "Azula_backup_20231120-133010.fwl" },
                azula.Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(WorldBackupKind.Old, azula.Single(l => l.Name == "Azula.fwl.old").Kind);
            Assert.Equal(WorldBackupKind.Legacy, azula.Single(l => l.Name.Contains("_backup_")).Kind);

            // The client's map render caches are not worlds, layers, or anything else here.
            Assert.DoesNotContain(azula, l => l.Name.EndsWith("TexCache"));

            var newAge = Assert.Single(WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "NewAge"));
            Assert.Equal("NewAge_backup_20251214-171043.fwl", newAge.Name);
            Assert.Equal(WorldBackupKind.Legacy, newAge.Kind);
            Assert.Equal(new DateTime(2025, 12, 14, 17, 10, 43), newAge.Stamp);
        }

        // ------------------------------- a rolling save is a LAYER, never a world (W-07)

        [Fact]
        public void A_bare_stamped_save_is_a_layer_of_the_trimmed_world_and_never_a_world()
        {
            var dir = WorldsDir();

            // The live world, plus the leftover a 2022 migration renamed aside with no marker.
            MakeLegacyWorld("Midgard");
            MakeLegacyWorld("Midgard_20220620-101500");
            MakeGeneration(Path.Combine(dir, "Realm_20260909180000"), 1, committed: true, worldName: "Realm_20260909180000");

            // The game reads a stamp out of both names, finds no "_backup_" at the second to
            // last underscore, calls them rolling saves and files them under the name trimmed
            // at the LAST underscore. It never elects a rolling file as a world, so a world
            // list holding one would offer a save the game itself refuses to open, and a
            // server launched at it would generate a brand new realm under that name.
            Assert.Equal(new[] { "Midgard" }, WorldStore.GetWorldNames(SaveFolder));
            Assert.Null(WorldStore.Find(SaveFolder, "Midgard_20220620-101500"));
            Assert.Null(WorldStore.Find(SaveFolder, "Realm_20260909180000"));

            // They are layers of the trimmed name, which is what makes them reachable in the
            // Barrow rather than lost between the two lists.
            var midgard = WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Midgard");
            var rolling = Assert.Single(midgard, l => l.Name == "Midgard_20220620-101500.fwl");
            Assert.Equal(WorldBackupKind.Other, rolling.Kind);
            Assert.Equal(new DateTime(2022, 6, 20, 10, 15, 0), rolling.Stamp);
            Assert.True(rolling.HasDb);
            Assert.False(rolling.IsDamaged);

            var realm = Assert.Single(WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Realm"));
            Assert.Equal("Realm_20260909180000", realm.Name);
            Assert.True(realm.IsDirectory);
            Assert.Equal(WorldBackupKind.Other, realm.Kind);

            // "Realm" has no live world at all, so the whole set is reachable only through the
            // owner less scan.
            var orphan = Assert.Single(WorldStore.EnumerateOrphanBackups(SaveFolder), o => o.WorldName == "Realm");
            Assert.Equal("worlds_local", orphan.Sub);
            Assert.Single(orphan.Layers);
        }

        [Fact]
        public void The_preupdate_pass_names_a_rolling_save_instead_of_letting_it_vanish()
        {
            var dir = WorldsDir();
            MakeLegacyWorld("Midgard");
            MakeLegacyWorld("Midgard_20220620-101500");

            var when = new DateTime(2026, 9, 9, 18, 42, 7, DateTimeKind.Local);
            var result = WorldStore.SnapshotAllPreUpdate(SaveFolder, when);

            // The live world is copied aside.
            Assert.True(result.Ok, result.Error);
            Assert.Equal(new[] { "Midgard_backup_preupdate-20260909-184207" }, result.Copied);
            Assert.True(File.Exists(Path.Combine(dir, "Midgard_backup_preupdate-20260909-184207.fwl")));

            // The rolling save is not a world and is not copied as one, but the pass says so
            // rather than leaving it out of the world list and the snapshot list at once.
            var named = Assert.Single(result.Skipped, s => s.StartsWith("Midgard_20220620-101500.fwl"));
            Assert.Contains("rolling save", named);
            Assert.Contains("'Midgard'", named);
        }

        // ----------------------------------------------- the cache key the game uses (W-08)

        [Fact]
        public void The_biome_cache_is_keyed_on_the_name_stored_inside_the_world_file()
        {
            // What a copied or renamed world looks like: the folder says one thing, the name the
            // game carries in the file says another, and the cache file is named after the file.
            var dir = WorldsDir();
            File.WriteAllBytes(Path.Combine(dir, "CopiedRealm.fwl"), BuildFwl(37, "Midgard", "seedy"));
            File.WriteAllBytes(Path.Combine(dir, "CopiedRealm.db"), BuildDbHeader(37, 3600));

            var cacheDir = Path.Combine(SaveFolder, "cache");
            Directory.CreateDirectory(cacheDir);
            var realCache = Path.Combine(cacheDir, "Midgard_biomedatacache.bin");
            File.WriteAllBytes(realCache, new byte[] { 1, 2, 3 });

            var world = WorldStore.Find(SaveFolder, "CopiedRealm");
            Assert.Equal("Midgard", WorldStore.BiomeCacheKey(world));

            // A copy takes the cache under the name the game will ask for at the destination.
            var dest = Path.Combine(SaveFolder, "adopted");
            WorldStore.CopyWorld(world, dest);
            Assert.True(File.Exists(Path.Combine(dest, "cache", "Midgard_biomedatacache.bin")));

            // And a delete takes the real cache with it instead of leaving a 2048x2048 grid
            // behind for the next world that happens to store the same name.
            var deleted = WorldStore.DeleteWorld(SaveFolder, "CopiedRealm");
            Assert.False(File.Exists(realCache));
            Assert.Contains("Midgard_biomedatacache.bin", deleted);
        }

        [Fact]
        public void The_biome_cache_key_falls_back_to_the_name_on_disk()
        {
            var dir = WorldsDir();
            File.WriteAllBytes(Path.Combine(dir, "Unreadable.fwl"), new byte[] { 9, 9, 9, 9 });
            File.WriteAllBytes(Path.Combine(dir, "Unreadable.db"), BuildDbHeader(37, 3600));

            var world = WorldStore.Find(SaveFolder, "Unreadable");
            Assert.NotNull(world);
            Assert.Equal("Unreadable", WorldStore.BiomeCacheKey(world));
        }

        // ------------------------------------------- one destination, one world (W-10)

        [Fact]
        public void CopyWorld_refuses_a_destination_that_already_has_that_world()
        {
            MakeGeneration(MakeChunkedWorld("NewRealm"), 2, committed: true);

            var dest = Path.Combine(SaveFolder, "adopted");
            var destWorlds = Path.Combine(dest, "worlds_local");
            var destWorld = Path.Combine(destWorlds, "NewRealm");
            MakeGeneration(destWorld, 7, committed: true, worldName: "NewRealm");
            File.WriteAllBytes(Path.Combine(destWorld, "07_07__0_41.chunk"), new byte[16]);

            var refused = Assert.Throws<InvalidOperationException>(
                () => WorldStore.CopyWorld(WorldStore.Find(SaveFolder, "NewRealm"), dest));
            Assert.Contains("NewRealm", refused.Message);

            // Nothing was merged in: the world that was there is exactly as it was.
            Assert.Equal(7, WorldStore.Find(dest, "NewRealm").SaveNumber);
            Assert.True(File.Exists(Path.Combine(destWorld, "07_07__0_41.chunk")));
            Assert.False(File.Exists(Path.Combine(destWorld, "_main.2.fwl2")));
        }

        [Fact]
        public void CopyWorld_with_overwrite_replaces_the_whole_destination_directory()
        {
            MakeGeneration(MakeChunkedWorld("NewRealm"), 2, committed: true);

            var dest = Path.Combine(SaveFolder, "adopted");
            var destWorlds = Path.Combine(dest, "worlds_local");
            var destWorld = Path.Combine(destWorlds, "NewRealm");
            MakeGeneration(destWorld, 7, committed: true, worldName: "NewRealm");
            File.WriteAllBytes(Path.Combine(destWorld, "07_07__0_41.chunk"), new byte[16]);
            // A same-named legacy pair that the directory was hiding.
            File.WriteAllBytes(Path.Combine(destWorlds, "NewRealm.fwl"), BuildFwl(37, "NewRealm", "s"));
            File.WriteAllBytes(Path.Combine(destWorlds, "NewRealm.db"), BuildDbHeader(37, 1800));

            WorldStore.CopyWorld(WorldStore.Find(SaveFolder, "NewRealm"), dest, overwrite: true);

            var copied = WorldStore.Find(dest, "NewRealm");
            Assert.Equal(2, copied.SaveNumber);
            Assert.False(File.Exists(Path.Combine(destWorld, "_main.7.fwl2")));
            Assert.False(File.Exists(Path.Combine(destWorld, "07_07__0_41.chunk")));
            Assert.False(File.Exists(Path.Combine(destWorlds, "NewRealm.fwl")));

            // The staged swap leaves nothing behind beside the world either.
            Assert.Equal(
                new[] { "NewRealm" },
                Directory.GetDirectories(destWorlds).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        }

        [Fact]
        public void CopyWorld_refuses_a_legacy_pair_that_is_already_at_the_destination()
        {
            MakeLegacyWorld("OldRealm");

            var dest = Path.Combine(SaveFolder, "adopted");
            var destWorlds = Path.Combine(dest, "worlds_local");
            Directory.CreateDirectory(destWorlds);
            File.WriteAllBytes(Path.Combine(destWorlds, "OldRealm.fwl"), BuildFwl(37, "OldRealm", "other"));
            File.WriteAllBytes(Path.Combine(destWorlds, "OldRealm.fwl.old"), BuildFwl(37, "OldRealm", "other"));

            Assert.Throws<InvalidOperationException>(
                () => WorldStore.CopyWorld(WorldStore.Find(SaveFolder, "OldRealm"), dest));
            Assert.Equal("other", FwlReader.TryRead(Path.Combine(destWorlds, "OldRealm.fwl")).SeedName);

            WorldStore.CopyWorld(WorldStore.Find(SaveFolder, "OldRealm"), dest, overwrite: true);

            Assert.Equal("seedy", FwlReader.TryRead(Path.Combine(destWorlds, "OldRealm.fwl")).SeedName);
            // The ".fwl.old" the source has no answer for went with the world it belonged to.
            Assert.False(File.Exists(Path.Combine(destWorlds, "OldRealm.fwl.old")));
        }

        // --------------------------------------------- no world comes back from the dead (W-11)

        [Fact]
        public void DeleteWorld_takes_the_legacy_pair_a_chunked_world_was_hiding()
        {
            var dir = WorldsDir();
            MakeLegacyWorld("Both", seedName: "the-old-one");
            MakeGeneration(Path.Combine(dir, "Both"), 1, committed: true);

            // Only the 1.0 world is listed while both are there.
            var only = Assert.Single(WorldStore.Enumerate(SaveFolder));
            Assert.Equal(WorldFormat.Chunked, only.Format);

            var deleted = WorldStore.DeleteWorld(SaveFolder, "Both");

            Assert.Contains("Both", deleted);
            Assert.Contains("Both.fwl", deleted);
            Assert.Contains("Both.db", deleted);
            Assert.False(File.Exists(Path.Combine(dir, "Both.fwl")));

            // Nothing rises again on the next read.
            Assert.Empty(WorldStore.Enumerate(SaveFolder));
        }

        // --------------------------------------- half a layer is still a layer (W-14)

        [Fact]
        public void An_orphan_backup_db_is_listed_as_damaged_and_goes_with_the_world()
        {
            var dir = WorldsDir();
            MakeLegacyWorld("Midgard");

            // The ".db" of a layer whose ".fwl" is gone: a whole world's worth of bytes that no
            // screen could show and no delete could reach.
            var orphan = Path.Combine(dir, "Midgard_backup_20260909-175156.db");
            File.WriteAllBytes(orphan, BuildDbHeader(37, 1800));

            var layer = Assert.Single(WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Midgard"));
            Assert.Equal("Midgard_backup_20260909-175156.db", layer.Name);
            Assert.True(layer.IsDamaged);
            Assert.False(layer.IsCommitted);
            Assert.Null(layer.MetaPath);
            Assert.True(layer.HasDb);
            Assert.Equal(WorldBackupKind.Legacy, layer.Kind);
            Assert.True(layer.SizeBytes > 0);

            // A restore must never be pointed at it: there is no ".fwl" to restore from, and
            // copying the ".db" into the live world's metadata file would break the world.
            Assert.Null(WorldStore.ResolveBackupLayer(
                SaveFolder, "worlds_local", "Midgard", "Midgard_backup_20260909-175156.db"));
            Assert.NotNull(WorldStore.ResolveBackupLayer(
                SaveFolder, "worlds_local", "Midgard", "Midgard_backup_20260909-175156.db", allowDamaged: true));

            var deleted = WorldStore.DeleteWorld(SaveFolder, "Midgard");
            Assert.Contains("Midgard_backup_20260909-175156.db", deleted);
            Assert.False(File.Exists(orphan));
        }

        [Fact]
        public void DeleteWorld_that_keeps_the_layers_keeps_the_old_pair_a_chunked_world_was_hiding()
        {
            var dir = WorldsDir();
            MakeLegacyWorld("Both");
            File.WriteAllBytes(Path.Combine(dir, "Both.fwl.old"), BuildFwl(37, "Both", "s"));
            File.WriteAllBytes(Path.Combine(dir, "Both.db.old"), BuildDbHeader(37, 1800));
            MakeGeneration(Path.Combine(dir, "Both"), 1, committed: true);

            var deleted = WorldStore.DeleteWorld(SaveFolder, "Both", includeBackups: false);

            // The shadowed LIVE pair goes, because it would come back as a world of its own.
            Assert.Contains("Both.fwl", deleted);
            Assert.False(File.Exists(Path.Combine(dir, "Both.fwl")));

            // The ".old" pair is a layer, and the caller asked for the layers to be left alone.
            Assert.True(File.Exists(Path.Combine(dir, "Both.fwl.old")));
            Assert.DoesNotContain("Both.fwl.old", deleted);
            Assert.Empty(WorldStore.Enumerate(SaveFolder));
        }

        [Fact]
        public void A_complete_layer_is_still_keyed_on_its_fwl_and_never_listed_twice()
        {
            var dir = WorldsDir();
            MakeLegacyWorld("Midgard");
            File.WriteAllBytes(Path.Combine(dir, "Midgard_backup_20260909-175156.fwl"), BuildFwl(37, "Midgard", "s"));
            File.WriteAllBytes(Path.Combine(dir, "Midgard_backup_20260909-175156.db"), BuildDbHeader(37, 1800));

            var layer = Assert.Single(WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Midgard"));
            Assert.Equal("Midgard_backup_20260909-175156.fwl", layer.Name);
            Assert.False(layer.IsDamaged);
            Assert.True(layer.HasDb);
        }

        [Fact]
        public void A_layer_that_lost_its_db_is_damaged_in_the_same_way_the_lone_db_is()
        {
            var dir = WorldsDir();
            MakeLegacyWorld("Midgard");

            // The ".db" of this layer was deleted to save space, so the ".fwl" is all that is
            // left of it. Restoring it would copy an old seed and uid over the live world's
            // metadata and leave the current database beside it, describing another save.
            File.WriteAllBytes(Path.Combine(dir, "Midgard_backup_auto-20260722065046.fwl"), BuildFwl(37, "Midgard", "s"));

            var layer = Assert.Single(WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Midgard"));
            Assert.Equal("Midgard_backup_auto-20260722065046.fwl", layer.Name);
            Assert.True(layer.IsDamaged);
            Assert.False(layer.HasDb);
            Assert.True(layer.SizeBytes > 0);

            // Listed and deletable, never restorable: exactly what the lone ".db" gets.
            Assert.Null(WorldStore.ResolveBackupLayer(
                SaveFolder, "worlds_local", "Midgard", "Midgard_backup_auto-20260722065046.fwl"));
            Assert.NotNull(WorldStore.ResolveBackupLayer(
                SaveFolder, "worlds_local", "Midgard", "Midgard_backup_auto-20260722065046.fwl", allowDamaged: true));
        }

        // ------------------------------------- backup sets with no world left (W-14)

        [Fact]
        public void EnumerateOrphanBackups_finds_the_sets_whose_world_is_gone_in_both_subfolders()
        {
            var local = WorldsDir();
            var legacy = WorldsDir("worlds");

            // A live world with layers of its own: never an orphan.
            MakeLegacyWorld("Final Sunset");
            File.WriteAllBytes(Path.Combine(local, "Final Sunset_backup_auto-20260722065046.fwl"), BuildFwl(37, "Final Sunset", "s"));
            File.WriteAllBytes(Path.Combine(local, "Final Sunset_backup_auto-20260722065046.db"), BuildDbHeader(37, 1800));

            // Two sets in worlds_local whose worlds are long gone.
            File.WriteAllBytes(Path.Combine(local, "Azula.fwl.old"), BuildFwl(37, "Azula", "s"));
            File.WriteAllBytes(Path.Combine(local, "Azula_backup_20231120-133010.fwl"), BuildFwl(37, "Azula", "s"));
            File.WriteAllBytes(Path.Combine(local, "Azula_backup_20231120-133010.db"), BuildDbHeader(37, 1800));
            File.WriteAllBytes(Path.Combine(local, "NewAge_backup_20251214-171043.fwl"), BuildFwl(37, "NewAge", "s"));
            File.WriteAllBytes(Path.Combine(local, "NewAge_backup_20251214-171043.db"), BuildDbHeader(37, 1800));

            // And one in the sibling "worlds" folder.
            File.WriteAllBytes(Path.Combine(legacy, "bruvs_backup_20240418-153951.fwl"), BuildFwl(37, "bruvs", "s"));
            File.WriteAllBytes(Path.Combine(legacy, "bruvs_backup_20240418-153951.db"), BuildDbHeader(37, 1800));

            var orphans = WorldStore.EnumerateOrphanBackups(SaveFolder);

            Assert.Equal(
                new[] { "Azula", "NewAge", "bruvs" },
                orphans.Select(o => o.WorldName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.DoesNotContain(orphans, o => o.WorldName == "Final Sunset");

            var azula = orphans.Single(o => o.WorldName == "Azula");
            Assert.Equal("worlds_local", azula.Sub);
            Assert.Equal(
                new[] { "Azula.fwl.old", "Azula_backup_20231120-133010.fwl" },
                azula.Layers.Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.True(azula.SizeBytes > 0);

            Assert.Equal("worlds", orphans.Single(o => o.WorldName == "bruvs").Sub);

            // Every layer of an owner less set still resolves, so a restore or a delete can be
            // pointed at it exactly the way a live world's layer is.
            Assert.NotNull(WorldStore.ResolveBackupLayer(
                SaveFolder, "worlds_local", "NewAge", "NewAge_backup_20251214-171043.fwl"));
        }

        [Fact]
        public void A_backup_set_stops_being_an_orphan_the_moment_its_world_is_back()
        {
            var dir = WorldsDir();
            File.WriteAllBytes(Path.Combine(dir, "Azula_backup_20231120-133010.fwl"), BuildFwl(37, "Azula", "s"));
            File.WriteAllBytes(Path.Combine(dir, "Azula_backup_20231120-133010.db"), BuildDbHeader(37, 1800));

            Assert.Single(WorldStore.EnumerateOrphanBackups(SaveFolder));

            // A world in the OTHER subfolder still owns its layers, so the set is not owner less.
            MakeLegacyWorld("Azula", sub: "worlds");
            Assert.Empty(WorldStore.EnumerateOrphanBackups(SaveFolder));
        }

        // ------------------------------------ a copy never takes the destination first (W-10)

        [Fact]
        public void CopyWorld_that_cannot_finish_a_legacy_pair_leaves_the_destination_world_whole()
        {
            MakeLegacyWorld("Realm", seedName: "the-new-one");

            var dest = Path.Combine(SaveFolder, "adopted");
            var destWorlds = Path.Combine(dest, "worlds_local");
            Directory.CreateDirectory(destWorlds);
            File.WriteAllBytes(Path.Combine(destWorlds, "Realm.fwl"), BuildFwl(37, "Realm", "the-old-one"));
            File.WriteAllBytes(Path.Combine(destWorlds, "Realm.db"), BuildDbHeader(37, 1800));

            var source = WorldStore.Find(SaveFolder, "Realm");

            // The ".fwl" copies, then the ".db" cannot be read: a full disk, a cloud sync handle
            // or a lock all land here. Before the staged copy, the destination pair had already
            // been deleted by this point and there was no way back.
            using (var _ = new FileStream(
                Path.Combine(WorldsDir(), "Realm.db"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failed = Assert.Throws<InvalidOperationException>(
                    () => WorldStore.CopyWorld(source, dest, overwrite: true));
                Assert.Contains("nothing in the destination folder was changed", failed.Message);
            }

            // The world that was there is exactly as it was, and no half copy is beside it.
            Assert.Equal("the-old-one", FwlReader.TryRead(Path.Combine(destWorlds, "Realm.fwl")).SeedName);
            Assert.True(File.Exists(Path.Combine(destWorlds, "Realm.db")));
            Assert.Equal(
                new[] { "Realm.db", "Realm.fwl" },
                Directory.GetFiles(destWorlds).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void CopyWorld_refuses_the_save_folder_the_world_already_lives_in()
        {
            MakeLegacyWorld("Realm");
            MakeGeneration(Path.Combine(WorldsDir(), "Chunky"), 2, committed: true);

            // Same folder in, same folder out: with overwrite this used to delete the source
            // pair and then copy it from the files it had just deleted.
            foreach (var name in new[] { "Realm", "Chunky" })
            {
                var world = WorldStore.Find(SaveFolder, name);
                var refused = Assert.Throws<InvalidOperationException>(
                    () => WorldStore.CopyWorld(world, SaveFolder, overwrite: true));
                Assert.Contains(name, refused.Message);
            }

            Assert.True(File.Exists(Path.Combine(WorldsDir(), "Realm.fwl")));
            Assert.True(File.Exists(Path.Combine(WorldsDir(), "Realm.db")));
            Assert.Equal(
                new[] { "Chunky", "Realm" },
                WorldStore.GetWorldNames(SaveFolder).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void CopyWorld_without_overwrite_refuses_a_destination_folder_rather_than_deleting_it()
        {
            MakeGeneration(MakeChunkedWorld("Realm"), 2, committed: true);

            // A destination directory of that name that holds no finished save: the ".fwl2" was
            // lost to a cloud sync conflict, so nothing claims the name through the world list,
            // but the database and the chunk files are still all there.
            var dest = Path.Combine(SaveFolder, "adopted");
            var destWorld = Path.Combine(dest, "worlds_local", "Realm");
            Directory.CreateDirectory(destWorld);
            File.WriteAllBytes(Path.Combine(destWorld, "_main.3.db2"), BuildDbHeader(41, 1800));
            File.WriteAllBytes(Path.Combine(destWorld, "00_00__0_41.chunk"), new byte[64]);

            var refused = Assert.Throws<InvalidOperationException>(
                () => WorldStore.CopyWorld(WorldStore.Find(SaveFolder, "Realm"), dest));
            Assert.Contains("Realm", refused.Message);

            // A call that did not ask to replace anything never deletes a tree.
            Assert.True(File.Exists(Path.Combine(destWorld, "_main.3.db2")));
            Assert.True(File.Exists(Path.Combine(destWorld, "00_00__0_41.chunk")));
            Assert.False(File.Exists(Path.Combine(destWorld, "_main.2.fwl2")));
        }

        // ------------------------------ a delete reaches both spellings of the name (W-11)

        [Fact]
        public void DeleteWorld_takes_the_same_named_world_hiding_in_the_sibling_subfolder()
        {
            var local = WorldsDir();
            var legacy = WorldsDir("worlds");

            // The live world, and a stale 2023 era pair of the same name in the other spelling
            // of the worlds folder. The dedupe hides the second one for exactly as long as the
            // first is there, so no list has ever shown it.
            MakeLegacyWorld("Final Sunset", seedName: "the-live-one");
            MakeLegacyWorld("Final Sunset", sub: "worlds", seedName: "the-stale-one");
            File.WriteAllBytes(Path.Combine(legacy, "Final Sunset_backup_20231202-002515.fwl"), BuildFwl(37, "Final Sunset", "s"));

            var only = Assert.Single(WorldStore.Enumerate(SaveFolder));
            Assert.Equal("worlds_local", only.Sub);

            WorldStore.DeleteWorld(SaveFolder, "Final Sunset");

            // Nothing rises again on the next read, in either spelling.
            Assert.Empty(WorldStore.Enumerate(SaveFolder));
            Assert.False(File.Exists(Path.Combine(local, "Final Sunset.fwl")));
            Assert.False(File.Exists(Path.Combine(legacy, "Final Sunset.fwl")));
            Assert.False(File.Exists(Path.Combine(legacy, "Final Sunset_backup_20231202-002515.fwl")));
        }

        [Fact]
        public void DeleteWorld_takes_a_shadowed_chunked_directory_in_the_sibling_subfolder()
        {
            var local = WorldsDir();
            MakeGeneration(Path.Combine(local, "Realm"), 2, committed: true);
            MakeGeneration(Path.Combine(WorldsDir("worlds"), "Realm"), 1, committed: true);

            Assert.Single(WorldStore.Enumerate(SaveFolder));

            WorldStore.DeleteWorld(SaveFolder, "Realm");

            Assert.Empty(WorldStore.Enumerate(SaveFolder));
            Assert.False(Directory.Exists(Path.Combine(WorldsDir("worlds"), "Realm")));
        }
    }
}
