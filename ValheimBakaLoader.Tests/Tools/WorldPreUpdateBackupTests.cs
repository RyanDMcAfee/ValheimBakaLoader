using System;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The snapshot the launch guard takes before it lets a server come up on a newer build,
    /// and the Barrow's ability to see it afterwards. Valheim 1.0 upgrades a world the first
    /// time it saves it and there is no way back, so this copy is the only way back.
    /// </summary>
    public class WorldPreUpdateBackupTests : IDisposable
    {
        private readonly string SaveFolder =
            Path.Combine(Path.GetTempPath(), "vbl-preupdate-tests-" + Guid.NewGuid().ToString("N"));

        private static readonly DateTime Stamp = new(2026, 9, 9, 18, 42, 7, DateTimeKind.Local);
        private const string StampText = "20260909-184207";

        public void Dispose()
        {
            try { Directory.Delete(SaveFolder, recursive: true); } catch { /* best effort */ }
        }

        // ------------------------------------------------------------------ the classifier

        [Theory]
        [InlineData("Midgard_backup_preupdate-20260909-184207", "Midgard")]
        [InlineData("Final Sunset_backup_preupdate-20260909-184207", "Final Sunset")]
        public void A_preupdate_layer_is_recognised_as_a_backup_of_its_world(string name, string world)
        {
            Assert.True(WorldStore.TryParseBackupName(name, out var owner, out var kind, out var stamp));
            Assert.Equal(world, owner);
            Assert.Equal(WorldBackupKind.PreUpdate, kind);
            Assert.Equal(new DateTime(2026, 9, 9, 18, 42, 7), stamp);

            Assert.True(WorldStore.IsBackupDirectoryName(name));
            Assert.True(WorldStore.IsBackupFileName(name + ".fwl", out var fileOwner));
            Assert.Equal(world, fileOwner);
            Assert.Equal("preupdate", WorldStore.KindToken(WorldBackupKind.PreUpdate));
        }

        [Fact]
        public void The_layer_name_follows_the_games_own_backup_shape()
        {
            Assert.Equal(
                "Midgard_backup_preupdate-" + StampText,
                WorldStore.PreUpdateLayerName("Midgard", Stamp));
        }

        // ------------------------------------------------------------------ legacy worlds

        [Fact]
        public void A_legacy_world_is_copied_to_a_preupdate_pair_the_Barrow_can_list()
        {
            MakeLegacyWorld("Midgard");

            var result = WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp);

            Assert.True(result.Ok);
            Assert.Equal(new[] { "Midgard_backup_preupdate-" + StampText }, result.Copied);
            Assert.True(result.Bytes > 0);

            var dir = WorldsDir();
            Assert.True(File.Exists(Path.Combine(dir, "Midgard_backup_preupdate-" + StampText + ".fwl")));
            Assert.True(File.Exists(Path.Combine(dir, "Midgard_backup_preupdate-" + StampText + ".db")));

            // The live world is untouched, and the copy shows up as a layer rather than a world.
            Assert.Equal(new[] { "Midgard" }, WorldStore.GetWorldNames(SaveFolder));

            var layer = Assert.Single(WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Midgard"));
            Assert.Equal(WorldBackupKind.PreUpdate, layer.Kind);
            Assert.False(layer.IsDirectory);
            Assert.True(layer.HasDb);
            Assert.True(layer.IsCommitted);
        }

        [Fact]
        public void A_preupdate_layer_can_be_resolved_for_a_restore()
        {
            MakeLegacyWorld("Midgard");
            WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp);

            var name = "Midgard_backup_preupdate-" + StampText + ".fwl";
            var layer = WorldStore.ResolveBackupLayer(SaveFolder, "worlds_local", "Midgard", name);

            Assert.NotNull(layer);
            Assert.Equal(WorldBackupKind.PreUpdate, layer.Kind);
            Assert.True(WorldStore.IsBackupShapedFor(name, "Midgard"));
        }

        // ------------------------------------------------------------------ chunked worlds

        [Fact]
        public void A_chunked_world_is_copied_as_a_directory_holding_the_committed_generation()
        {
            var world = Path.Combine(WorldsDir(), "Chunky");
            MakeGeneration(world, 3, committed: true);
            // A newer generation that was never committed: the game would delete it, and a
            // restore of it would hand back a save with no .ok marker.
            File.WriteAllBytes(Path.Combine(world, "_main.4.fwl2"), new byte[16]);

            var result = WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp);
            Assert.True(result.Ok);

            var layerDir = Path.Combine(WorldsDir(), "Chunky_backup_preupdate-" + StampText);
            Assert.True(Directory.Exists(layerDir));
            Assert.True(File.Exists(Path.Combine(layerDir, "_main.3.fwl2")));
            Assert.True(File.Exists(Path.Combine(layerDir, "_main.3.db2")));
            Assert.True(File.Exists(Path.Combine(layerDir, "_main.3.chunks")));
            Assert.True(File.Exists(Path.Combine(layerDir, "_main.3.ok")));
            Assert.True(File.Exists(Path.Combine(layerDir, "00_00__0_41.chunk")));
            Assert.False(File.Exists(Path.Combine(layerDir, "_main.4.fwl2")));

            // The layer is a backup of Chunky, not a second world called Chunky_backup_....
            Assert.Equal(new[] { "Chunky" }, WorldStore.GetWorldNames(SaveFolder));

            var layer = Assert.Single(WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Chunky"));
            Assert.Equal(WorldBackupKind.PreUpdate, layer.Kind);
            Assert.True(layer.IsDirectory);
            Assert.True(layer.IsCommitted);
            Assert.Equal(3, layer.SaveNumber);
        }

        [Fact]
        public void A_chunked_world_with_no_finished_save_is_skipped_rather_than_half_copied()
        {
            var world = Path.Combine(WorldsDir(), "NeverSaved");
            MakeGeneration(world, 0, committed: false);
            // A world that DOES copy, so the pass as a whole still protects something.
            MakeLegacyWorld("Midgard");

            var result = WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp);

            Assert.True(result.Ok, result.Error);   // one world with nothing in it is not a failure
            Assert.Equal(new[] { "Midgard_backup_preupdate-" + StampText }, result.Copied);
            Assert.Single(result.Skipped);
            Assert.Contains("NeverSaved", result.Skipped[0]);
            Assert.False(Directory.Exists(Path.Combine(WorldsDir(), "NeverSaved_backup_preupdate-" + StampText)));
        }

        [Fact]
        public void A_world_with_no_finished_save_is_not_counted_as_a_world_that_failed_to_copy()
        {
            // The 1.0 server writes "_main.0.fwl2" the moment it creates a world and only lands
            // the ".db2"/".chunks"/".ok" at the first world save, so a crash or a force kill
            // inside that first save interval leaves a save folder in exactly this state.
            // There is nothing in it an upgrade could take away, so there is nothing to protect
            // and no reason to refuse the launch that asked to be protected.
            MakeGeneration(Path.Combine(WorldsDir(), "NeverSaved"), 0, committed: false);

            var result = WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp);

            Assert.True(result.Ok, result.Error);
            Assert.Null(result.Error);
            Assert.Empty(result.Copied);

            // Not protected is not the same as silently gone: the pass names it.
            var skipped = Assert.Single(result.Skipped);
            Assert.Contains("NeverSaved", skipped);
            Assert.Contains("no finished save", skipped);
        }

        [Fact]
        public void A_folder_where_nothing_has_a_finished_save_is_a_clean_pass_that_names_them_all()
        {
            MakeGeneration(Path.Combine(WorldsDir(), "NeverSaved"), 0, committed: false);
            MakeGeneration(Path.Combine(WorldsDir(), "AlsoNeverSaved"), 0, committed: false);

            var result = WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp);

            Assert.True(result.Ok, result.Error);
            Assert.Empty(result.Copied);
            Assert.Equal(2, result.Skipped.Count);
        }

        [Fact]
        public void A_world_that_HAS_a_finished_save_and_cannot_be_copied_stops_the_pass()
        {
            // This is the case the zero copy guard is actually for: a world with a whole save
            // in it that the snapshot could not put anywhere. Waving the launch through here
            // starts a newer build on a world with nothing behind it.
            MakeLegacyWorld("Midgard");

            using var _ = new FileStream(
                Path.Combine(WorldsDir(), "Midgard.fwl"), FileMode.Open, FileAccess.Read, FileShare.None);

            var result = WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp);

            Assert.False(result.Ok);
            Assert.Empty(result.Copied);
            Assert.Contains("Midgard", result.Error);
        }

        [Fact]
        public void Only_the_chunk_files_the_finished_save_names_travel_with_it()
        {
            var world = Path.Combine(WorldsDir(), "Chunky");
            MakeGeneration(world, 3, committed: true);

            // A chunk file an in-flight save has already written for the NEXT generation. The
            // committed index does not name it, so it is not part of the world being copied and
            // pairing it with this index would produce a layer that is neither generation.
            File.WriteAllBytes(Path.Combine(world, "01_02__0_42.chunk"), new byte[64]);

            Assert.True(WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp).Ok);

            var layerDir = Path.Combine(WorldsDir(), "Chunky_backup_preupdate-" + StampText);
            Assert.True(File.Exists(Path.Combine(layerDir, ChunkName)));
            Assert.False(File.Exists(Path.Combine(layerDir, "01_02__0_42.chunk")));
        }

        [Fact]
        public void A_world_the_caller_refuses_stops_the_whole_pass_rather_than_being_skipped()
        {
            MakeLegacyWorld("Midgard");
            MakeGeneration(Path.Combine(WorldsDir(), "Chunky"), 1, committed: true);

            var result = WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp,
                refuse: w => w.Name == "Chunky" ? "'Raids' is running it right now." : null);

            // A world that cannot be captured is not a world you may launch past: the caller
            // asked for every world to be safe, so a refusal is an error, never a quiet skip.
            Assert.False(result.Ok);
            Assert.Contains("Chunky", result.Error);
            Assert.Contains("Raids", result.Error);
            Assert.Empty(result.Skipped);
            Assert.False(Directory.Exists(Path.Combine(WorldsDir(), "Chunky_backup_preupdate-" + StampText)));
        }

        [Fact]
        public void A_finished_save_with_an_unreadable_chunk_index_is_a_failure_not_a_partial_copy()
        {
            var world = Path.Combine(WorldsDir(), "Chunky");
            MakeGeneration(world, 2, committed: true);
            File.WriteAllBytes(Path.Combine(world, "_main.2.chunks"), new byte[] { 1, 2, 3 });

            var result = WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp);

            Assert.False(result.Ok);
            Assert.Contains("Chunky", result.Error);
        }

        [Fact]
        public void Every_world_in_the_save_folder_is_copied_in_one_pass()
        {
            MakeLegacyWorld("Midgard");
            MakeLegacyWorld("Trialgrounds");
            MakeGeneration(Path.Combine(WorldsDir(), "Chunky"), 1, committed: true);

            var result = WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp);

            Assert.True(result.Ok);
            Assert.Equal(3, result.Copied.Count);
            Assert.All(result.Copied, n => Assert.Contains("_backup_preupdate-", n));
            Assert.True(result.Bytes > 0);
        }

        [Fact]
        public void A_save_folder_that_is_not_there_reports_an_error_rather_than_throwing()
        {
            var result = WorldStore.SnapshotAllPreUpdate("   ");
            Assert.False(result.Ok);
            Assert.NotNull(result.Error);

            // A folder that simply has no worlds in it is a clean, empty pass: there was nothing
            // that needed protecting, which is not the same as protecting nothing.
            var empty = WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp);
            Assert.True(empty.Ok, empty.Error);
            Assert.Empty(empty.Copied);
            Assert.Empty(empty.Skipped);
        }

        [Fact]
        public void Snapshotting_twice_in_the_same_second_overwrites_rather_than_failing()
        {
            MakeLegacyWorld("Midgard");

            Assert.True(WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp).Ok);
            Assert.True(WorldStore.SnapshotAllPreUpdate(SaveFolder, Stamp).Ok);

            // Still one layer, because the second pass wrote over the first.
            Assert.Single(WorldStore.EnumerateBackups(SaveFolder, "worlds_local", "Midgard"));
        }

        // ------------------------------------------------------------------ tree builders

        private string WorldsDir(string sub = "worlds_local")
        {
            var dir = Path.Combine(SaveFolder, sub);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void MakeLegacyWorld(string name)
        {
            var dir = WorldsDir();
            File.WriteAllBytes(Path.Combine(dir, name + ".fwl"), BuildFwl(37, name));
            File.WriteAllBytes(Path.Combine(dir, name + ".db"), BuildDbHeader(37));
        }

        private static void MakeGeneration(string worldDir, int number, bool committed)
        {
            Directory.CreateDirectory(worldDir);
            var worldName = Path.GetFileName(worldDir);

            File.WriteAllBytes(Path.Combine(worldDir, $"_main.{number}.fwl2"), BuildFwl(41, worldName));
            if (!committed) return;

            File.WriteAllBytes(Path.Combine(worldDir, $"_main.{number}.db2"), BuildDbHeader(41));
            File.WriteAllBytes(Path.Combine(worldDir, $"_main.{number}.chunks"), BuildChunkIndex(ChunkName));
            File.WriteAllBytes(Path.Combine(worldDir, $"_main.{number}.ok"), BitConverter.GetBytes(41));
            File.WriteAllBytes(Path.Combine(worldDir, ChunkName), new byte[64]);
        }

        /// <summary>The one chunk file the generations built here declare in their index.</summary>
        private const string ChunkName = "00_00__0_41.chunk";

        /// <summary>
        /// A "_main.N.chunks" index: uint16 version, int32 total objects, int32 record count,
        /// then one record per chunk (uint16 chunk, byte size, uint32 chunk version, int32
        /// objects). The file name a record stands for is built from those three numbers, so an
        /// index that names one chunk is the only thing that makes that .chunk file part of the
        /// finished save.
        /// </summary>
        private static byte[] BuildChunkIndex(params string[] chunkFileNames)
        {
            using var body = new MemoryStream();
            using var w = new BinaryWriter(body);

            w.Write((ushort)41);
            w.Write(0);                        // total objects across every chunk
            w.Write(chunkFileNames.Length);

            foreach (var name in chunkFileNames)
            {
                // "<yy>_<xx>__<size>_<version>.chunk"
                var stem = Path.GetFileNameWithoutExtension(name);
                var halves = stem.Split(new[] { "__" }, StringSplitOptions.None);
                var position = halves[0].Split('_');
                var shape = halves[1].Split('_');

                var high = Convert.ToUInt16(position[0], 16);
                var low = Convert.ToUInt16(position[1], 16);

                w.Write((ushort)((high << 8) | low));
                w.Write(byte.Parse(shape[0]));
                w.Write(uint.Parse(shape[1]));
                w.Write(0);                    // objects in this chunk
            }

            w.Flush();
            return body.ToArray();
        }

        /// <summary>
        /// A .fwl/.fwl2 header in the layout both formats share: int32 payload size, int32
        /// version, world name, seed name, int32 seed, int64 uid, int32 worldGenVersion.
        /// </summary>
        private static byte[] BuildFwl(int version, string worldName)
        {
            using var body = new MemoryStream();
            using var w = new BinaryWriter(body, System.Text.Encoding.UTF8);
            w.Write(version);
            w.Write(worldName);
            w.Write("seedy");
            w.Write(12345);
            w.Write(9876543210L);
            w.Write(2);
            w.Flush();

            var payload = body.ToArray();
            return BitConverter.GetBytes(payload.Length).Concat(payload).ToArray();
        }

        /// <summary>int32 world version then a double netTime in seconds - the same in .db and .db2.</summary>
        private static byte[] BuildDbHeader(int version)
            => BitConverter.GetBytes(version).Concat(BitConverter.GetBytes(5400d)).ToArray();
    }
}
