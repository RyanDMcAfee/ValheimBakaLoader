using System;
using System.IO;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The Barrow's two dangerous edges, on real files. A layer that has lost the world file it
    /// would be restored FROM must never reach the restore path, because the legacy restore
    /// copies the layer's own file over the live world's ".fwl" and would write database bytes
    /// into the world's metadata. It must still reach the delete path: clearing it is the only
    /// thing left to do with it, and it can be as large as the world itself. Adopting a world
    /// into a folder that already holds one of that name is the other edge.
    /// </summary>
    public class BlendWindowBarrowTests : IDisposable
    {
        private readonly string Root;
        private readonly string SaveFolder;
        private readonly string WorldsDir;

        private const string Sub = "worlds_local";
        private const string World = "Midgard";
        private const string DamagedLayer = "Midgard_backup_20260910-140506.db";
        private const string HealthyLayer = "Midgard_backup_20260910-150607.fwl";

        public BlendWindowBarrowTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-barrow-" + Guid.NewGuid().ToString("N"));
            SaveFolder = Path.Combine(Root, "saves");
            WorldsDir = Path.Combine(SaveFolder, Sub);
            Directory.CreateDirectory(WorldsDir);

            // A ".db" whose ".fwl" partner is gone: half a layer, nothing to restore from.
            File.WriteAllText(Path.Combine(WorldsDir, DamagedLayer), "database bytes");

            // And a whole one beside it, so the refusal is about this layer and not the folder.
            File.WriteAllText(Path.Combine(WorldsDir, HealthyLayer), "world file");
            File.WriteAllText(Path.Combine(WorldsDir, "Midgard_backup_20260910-150607.db"), "database bytes");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void A_layer_that_lost_its_world_file_is_refused_by_name_rather_than_restored()
        {
            var refused = Assert.Throws<ArgumentException>(
                () => BlendWindow.ResolveBackupLayerOrRefuse(SaveFolder, Sub, World, DamagedLayer, allowDamaged: false));

            Assert.Contains(DamagedLayer, refused.Message);
            Assert.Contains("has lost the world file that went with it", refused.Message);

            // Not the stale-reference sentence: the file is sitting right there.
            Assert.DoesNotContain("no longer exists", refused.Message);
        }

        [Fact]
        public void The_same_layer_is_still_reachable_for_a_delete()
        {
            var layer = BlendWindow.ResolveBackupLayerOrRefuse(SaveFolder, Sub, World, DamagedLayer, allowDamaged: true);

            Assert.NotNull(layer);
            Assert.True(layer.IsDamaged);
            Assert.Equal(DamagedLayer, layer.Name);
        }

        [Fact]
        public void A_whole_layer_resolves_either_way()
        {
            Assert.NotNull(BlendWindow.ResolveBackupLayerOrRefuse(SaveFolder, Sub, World, HealthyLayer, allowDamaged: false));
            Assert.NotNull(BlendWindow.ResolveBackupLayerOrRefuse(SaveFolder, Sub, World, HealthyLayer, allowDamaged: true));
        }

        [Fact]
        public void A_name_that_was_never_a_layer_of_this_world_still_reads_as_a_bad_reference()
        {
            var refused = Assert.Throws<ArgumentException>(
                () => BlendWindow.ResolveBackupLayerOrRefuse(SaveFolder, Sub, World, "Asgard_backup_20260910-140506.fwl", allowDamaged: false));

            Assert.Contains("Not a backup layer of that world", refused.Message);
        }

        [Fact]
        public void The_overview_tells_the_interface_which_layers_are_damaged()
        {
            var layers = WorldStore.EnumerateBackups(SaveFolder, Sub, World);

            var damaged = JObject.FromObject(
                BlendWindow.BuildBackupLayerDto(FindLayer(layers, DamagedLayer), day: null));
            var whole = JObject.FromObject(
                BlendWindow.BuildBackupLayerDto(FindLayer(layers, HealthyLayer), day: 42));

            Assert.True(damaged.Value<bool>("damaged"), "the damaged layer went out without its flag");
            Assert.False(whole.Value<bool>("damaged"));

            // The fields the Barrow already read must still be there beside the new one.
            Assert.Equal(DamagedLayer, damaged.Value<string>("file"));
            Assert.Equal(42, whole.Value<int>("day"));
        }

        [Fact]
        public void Adopting_into_a_folder_that_already_holds_that_world_is_refused_in_plain_words()
        {
            // A source world to adopt.
            var sourceSave = Path.Combine(Root, "source");
            var sourceWorlds = Path.Combine(sourceSave, Sub);
            Directory.CreateDirectory(sourceWorlds);
            File.WriteAllText(Path.Combine(sourceWorlds, World + ".fwl"), "world file");
            File.WriteAllText(Path.Combine(sourceWorlds, World + ".db"), "database bytes");

            // A destination that already has a world of that name, which is what a removed
            // profile leaves behind. Copying onto it would mix two save histories together.
            var destSave = Path.Combine(Root, "dest");
            var destWorlds = Path.Combine(destSave, Sub);
            Directory.CreateDirectory(destWorlds);
            File.WriteAllText(Path.Combine(destWorlds, World + ".fwl"), "somebody else's world");
            File.WriteAllText(Path.Combine(destWorlds, World + ".db"), "somebody else's database");

            var refused = Assert.Throws<InvalidOperationException>(
                () => BlendWindow.CopyWorldFiles(sourceSave, Sub, World, destSave));

            Assert.Contains(World, refused.Message);
            Assert.Contains("nothing was adopted", refused.Message);

            // And it really did nothing: the world that was there is untouched.
            Assert.Equal("somebody else's world", File.ReadAllText(Path.Combine(destWorlds, World + ".fwl")));
        }

        private static WorldBackupInfo FindLayer(System.Collections.Generic.IEnumerable<WorldBackupInfo> layers, string name)
        {
            foreach (var layer in layers)
            {
                if (string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase)) return layer;
            }

            throw new InvalidOperationException($"the test fixture did not produce a layer called '{name}'");
        }
    }
}
