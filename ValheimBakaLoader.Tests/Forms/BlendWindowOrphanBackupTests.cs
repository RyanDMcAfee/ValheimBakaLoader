using System;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// Backup layers whose world is gone. The Barrow asked for layers world by world, working
    /// from the list of LIVE worlds, so a set left behind by a world that has since been deleted
    /// or renamed was never asked for at all: nothing on the machine showed it, and the disk it
    /// holds could not be seen or reclaimed from the app. A real save folder can easily be
    /// carrying a hundred megabytes of these.
    /// <para>
    /// The scan itself is <see cref="WorldStore.EnumerateOrphanBackups"/>, which is also what
    /// decides what counts as a layer everywhere else. The bridge used to carry a second scan of
    /// its own, and the two did not agree: that one gathered the live names from ONE worlds
    /// subfolder, so a world kept in "worlds" did not claim its layers sitting in "worlds_local"
    /// and they were offered as orphans of a world that is right there. These drive the
    /// classifier the Barrow now reads, and the restore and delete paths that its rows call.
    /// </para>
    /// </summary>
    public class BlendWindowOrphanBackupTests : IDisposable
    {
        private const string Sub = "worlds_local";
        private const string OtherSub = "worlds";

        // Real names off a host's machine. The set with a layer AND its database is the one the
        // restore proof unearths, because bringing a world back needs both halves.
        private const string AzulaLayer = "Azula_backup_20231120-133010";

        private readonly string Root;
        private readonly string SaveFolder;
        private readonly string WorldsDir;
        private readonly string OtherWorldsDir;

        public BlendWindowOrphanBackupTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-orphan-" + Guid.NewGuid().ToString("N"));
            SaveFolder = Path.Combine(Root, "saves");
            WorldsDir = Path.Combine(SaveFolder, Sub);
            OtherWorldsDir = Path.Combine(SaveFolder, OtherSub);
            Directory.CreateDirectory(WorldsDir);
            Directory.CreateDirectory(OtherWorldsDir);

            // A live world with a layer of its own, which must stay out of the orphan list.
            Write(Sub, "Midgard.fwl", "midgard world file");
            Write(Sub, "Midgard.db", "midgard database");
            Write(Sub, "Midgard_backup_20260910-150607.fwl");
            Write(Sub, "Midgard_backup_20260910-150607.db");

            // A world that lives in the OTHER worlds subfolder, with a layer over here. The
            // world is on disk, so this is not an orphan set however far from it the layer sits.
            Write(OtherSub, "Farum.fwl", "farum world file");
            Write(OtherSub, "Farum.db", "farum database");
            Write(Sub, "Farum_backup_20250101-101010.fwl");
            Write(Sub, "Farum_backup_20250101-101010.db");

            // Names taken from a real machine: a world that is no longer anywhere on disk, and
            // the game's own pre-conversion pair left behind by another one.
            Write(Sub, "NewAge_backup_20251214-171043.fwl");
            Write(Sub, "NewAge_backup_20251214-171043.db");
            Write(Sub, "Azula.fwl.old");
            Write(Sub, "Azula.db.old");

            // The whole layer the restore proof brings back. Distinct bytes on both halves so
            // the assert can say WHICH file landed where.
            Write(Sub, AzulaLayer + ".fwl", "azula world file");
            Write(Sub, AzulaLayer + ".db", "azula database");

            // What the game calls a rolling save: a stamp and no "_backup_" marker. The game
            // files it under the trimmed name and never opens it as a world of its own, so it
            // is a LAYER of a world that is not here rather than a world in its own right.
            Write(Sub, "Ymir_20260101-000000.fwl");
            Write(Sub, "Ymir_20260101-000000.db");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
            GC.SuppressFinalize(this);
        }

        // ---------------------------------------------------------------- the scan

        [Fact]
        public void Layers_whose_world_is_gone_are_found()
        {
            var sets = WorldStore.EnumerateOrphanBackups(SaveFolder);

            Assert.Equal(
                new[] { "Azula", "NewAge", "Ymir" },
                sets.Select(s => s.WorldName).OrderBy(n => n, StringComparer.Ordinal).ToArray());

            var newAge = sets.Single(s => s.WorldName == "NewAge");
            Assert.Equal("NewAge_backup_20251214-171043.fwl", Assert.Single(newAge.Layers).Name);
            Assert.Equal(Sub, newAge.Sub);

            // The pre-conversion pair and the whole layer are both layers of the same gone world.
            var azula = sets.Single(s => s.WorldName == "Azula");
            Assert.Contains(azula.Layers, l => l.Kind == WorldBackupKind.Old);
            Assert.Contains(azula.Layers, l => l.Name == AzulaLayer + ".fwl");
        }

        [Fact]
        public void A_rolling_save_is_a_layer_of_the_world_it_names_and_never_a_world()
        {
            var ymir = Assert.Single(WorldStore.EnumerateOrphanBackups(SaveFolder), s => s.WorldName == "Ymir");

            Assert.Equal("Ymir_20260101-000000.fwl", Assert.Single(ymir.Layers).Name);

            // And the world lists never offer it as something to run or adopt.
            Assert.DoesNotContain(
                WorldStore.EnumerateIn(SaveFolder, Sub),
                w => w.Name.StartsWith("Ymir", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void A_world_that_is_still_on_disk_is_never_called_an_orphan()
        {
            var sets = WorldStore.EnumerateOrphanBackups(SaveFolder);

            Assert.DoesNotContain(sets, s => string.Equals(s.WorldName, "Midgard", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void A_world_in_the_other_worlds_subfolder_still_claims_its_layers()
        {
            // The bridge's own scan looked for the live owner in ONE subfolder, so Farum's layer
            // over here read as a set belonging to nobody while Farum sat in "worlds" all along.
            var sets = WorldStore.EnumerateOrphanBackups(SaveFolder);

            Assert.DoesNotContain(sets, s => string.Equals(s.WorldName, "Farum", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void The_set_carries_the_disk_it_holds()
        {
            var azula = Assert.Single(WorldStore.EnumerateOrphanBackups(SaveFolder), s => s.WorldName == "Azula");

            // What the Barrow row shows as backupBytes, and the reason the set is worth a screen.
            Assert.Equal(azula.Layers.Sum(l => l.SizeBytes), azula.SizeBytes);
            Assert.True(azula.SizeBytes > 0);
        }

        [Fact]
        public void Every_orphan_layer_can_be_reached_by_the_restore_and_delete_paths()
        {
            // The Barrow draws a RESTORE and a delete on every one of these rows, and both go
            // back through the reference check. A row whose name could not survive that round
            // trip would be two dead buttons.
            foreach (var set in WorldStore.EnumerateOrphanBackups(SaveFolder))
            {
                foreach (var layer in set.Layers)
                {
                    var resolved = BlendWindow.ResolveBackupLayerOrRefuse(
                        SaveFolder, set.Sub, set.WorldName, layer.Name, allowDamaged: true);

                    Assert.Equal(layer.Name, resolved.Name);
                }
            }
        }

        [Fact]
        public void An_empty_folder_and_a_folder_that_is_not_there_answer_with_nothing()
        {
            Assert.Empty(WorldStore.EnumerateOrphanBackups(Path.Combine(Root, "no-such-folder")));
            Assert.Empty(WorldStore.EnumerateOrphanBackups(null));
            Assert.Empty(WorldStore.EnumerateOrphanBackups("   "));
        }

        // ---------------------------------------------------------------- restore and delete

        [Fact]
        public void Unearthing_an_orphan_layer_brings_the_world_back()
        {
            // Nothing named Azula is on disk as a world before this runs.
            Assert.Null(WorldStore.FindIn(SaveFolder, Sub, "Azula"));

            var layer = BlendWindow.ResolveBackupLayerOrRefuse(
                SaveFolder, Sub, "Azula", AzulaLayer + ".fwl", allowDamaged: false);

            var (snapshot, restoredDb) = BlendWindow.RestoreBackupLayer(SaveFolder, Sub, "Azula", layer);

            // There was no live world to copy aside, so there is no safety layer to report.
            Assert.Null(snapshot);
            Assert.True(restoredDb);

            // The world is a world again, with both halves and the layer's own bytes in them.
            Assert.NotNull(WorldStore.FindIn(SaveFolder, Sub, "Azula"));
            Assert.Equal("azula world file", File.ReadAllText(Path.Combine(WorldsDir, "Azula.fwl")));
            Assert.Equal("azula database", File.ReadAllText(Path.Combine(WorldsDir, "Azula.db")));

            // And it stops being an orphan set: its layers now have an owner on disk again.
            Assert.DoesNotContain(
                WorldStore.EnumerateOrphanBackups(SaveFolder),
                s => string.Equals(s.WorldName, "Azula", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Unearthing_over_a_live_world_still_takes_the_safety_copy_first()
        {
            // The other half of the same helper: a world that IS there is copied aside before
            // anything is written over it, so the orphan path is the exception and not the rule.
            var layer = BlendWindow.ResolveBackupLayerOrRefuse(
                SaveFolder, Sub, "Midgard", "Midgard_backup_20260910-150607.fwl", allowDamaged: false);

            var (snapshot, _) = BlendWindow.RestoreBackupLayer(SaveFolder, Sub, "Midgard", layer);

            Assert.NotNull(snapshot);
            Assert.Equal("midgard world file", File.ReadAllText(Path.Combine(WorldsDir, snapshot + ".fwl")));
            Assert.Equal("midgard database", File.ReadAllText(Path.Combine(WorldsDir, snapshot + ".db")));
        }

        [Fact]
        public void An_orphan_layer_can_be_cleared_to_take_its_disk_back()
        {
            // What backups.delete does with the reference the row carries.
            var layer = BlendWindow.ResolveBackupLayerOrRefuse(
                SaveFolder, Sub, "NewAge", "NewAge_backup_20251214-171043.fwl", allowDamaged: true);

            var deleted = WorldStore.DeleteBackup(layer);

            Assert.NotEmpty(deleted);
            Assert.False(File.Exists(Path.Combine(WorldsDir, "NewAge_backup_20251214-171043.fwl")));
            Assert.False(File.Exists(Path.Combine(WorldsDir, "NewAge_backup_20251214-171043.db")));
            Assert.DoesNotContain(
                WorldStore.EnumerateOrphanBackups(SaveFolder),
                s => string.Equals(s.WorldName, "NewAge", StringComparison.OrdinalIgnoreCase));
        }

        private void Write(string sub, string name, string contents = "bytes")
            => File.WriteAllText(Path.Combine(SaveFolder, sub, name), contents);
    }
}
