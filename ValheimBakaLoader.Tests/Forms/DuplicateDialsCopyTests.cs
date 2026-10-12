using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Game;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// A duplicate that copies its world, and a dial the host set back to Normal on purpose.
    /// <para>
    /// The defect this closes: the Duplicate dialog sent only the dials that were moved, and the
    /// host side merged only the keys it received over what the copy's own header had just
    /// brought in. So a Casual source stayed Casual whatever the host chose, and Normal could
    /// not be chosen at all. The dialog now sends all five with Normal spelled out, and these
    /// tests take that payload through the same steps servers.create takes, in its order, on a
    /// real world copied between two temp save folders: the copy, the first meeting that reads
    /// the copy's header, then <see cref="BlendWindow.ApplyForgeWorldChoices"/>.
    /// </para>
    /// <para>
    /// Every world here is built byte for byte under the test runner's temp folder. No save
    /// folder of the host's is opened, copied or written.
    /// </para>
    /// </summary>
    public class DuplicateDialsCopyTests : IDisposable
    {
        private readonly string Root =
            Path.Combine(Path.GetTempPath(), "vbl-dupdials-" + Guid.NewGuid().ToString("N"));

        private string SourceFolder => Path.Combine(Root, "source");
        private string CopyFolder => Path.Combine(Root, "copy");

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
            GC.SuppressFinalize(this);
        }

        /// <summary>The keys the game writes into a world for Death penalty Casual and Combat Hard.</summary>
        private static readonly string[] CasualAndHard =
        {
            "deathkeepequip", "skillreductionrate 15",
            "playerdamage 85", "enemydamage 150", "enemyspeedsize 110", "enemyleveluprate 120",
        };

        private static byte[] Fwl(string worldName, IReadOnlyList<string> keys)
        {
            using var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(37);                                  // world version, legacy .fwl
                w.Write(worldName);
                w.Write("seedy");
                w.Write(FwlWriter.GetStableHashCode("seedy"));
                w.Write(1234567890123L);                      // uid
                w.Write(2);                                   // worldGenVersion
                w.Write(true);                                // needsDB
                w.Write(keys.Count);
                foreach (var key in keys) w.Write(key);
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

        private static byte[] DbHeader()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(37);
                w.Write(3600.0);
                w.Write(new byte[32]);
            }
            return ms.ToArray();
        }

        /// <summary>
        /// The source world, copied into a second save folder under a new name, and the first
        /// meeting run on the copy: everything servers.create does before the dials are written.
        /// </summary>
        private WorldKeyImportTests.InMemoryWorlds CopyAndMeet(string copyName)
        {
            var dir = Path.Combine(SourceFolder, "worlds_local");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "Midgard.fwl"), Fwl("Midgard", CasualAndHard));
            File.WriteAllBytes(Path.Combine(dir, "Midgard.db"), DbHeader());
            Directory.CreateDirectory(CopyFolder);

            var source = WorldStore.Find(SourceFolder, "Midgard");
            Assert.NotNull(source);
            WorldStore.CopyWorldAs(source, copyName, CopyFolder);

            var worlds = new WorldKeyImportTests.InMemoryWorlds();
            var outcome = WorldKeyImportStep.Run(
                worlds, () => FwlReader.TryReadWorldStartingKeys(CopyFolder, copyName), copyName);

            // The copy carries the source's difficulty, and the first meeting reads it.
            Assert.Equal(WorldKeyImportKind.Imported, outcome.Kind);
            Assert.Equal("casual", worlds.LoadPreferences(copyName).Modifiers["deathpenalty"]);
            Assert.Equal("hard", worlds.LoadPreferences(copyName).Modifiers["combat"]);
            return worlds;
        }

        /// <summary>What the Duplicate dialog sends with the source read: all five dials.</summary>
        private static JObject AllFive(string combat, string deathpenalty) => new JObject
        {
            ["combat"] = combat,
            ["deathpenalty"] = deathpenalty,
            ["resources"] = "normal",
            ["raids"] = "normal",
            ["portals"] = "normal",
        };

        private static WorldPreferences Forge(WorldKeyImportTests.InMemoryWorlds worlds, string world, JObject dials)
            => BlendWindow.ApplyForgeWorldChoices(worlds, world,
                BlendWindow.ParseWorldModifiers(dials), BlendWindow.ParseExplicitNormalDials(dials), null);

        [Fact]
        public void An_explicit_normal_lands_in_the_copys_world_prefs()
        {
            var worlds = CopyAndMeet("Midgard2");

            var stored = Forge(worlds, "Midgard2", AllFive("hard", "normal"));

            Assert.NotNull(stored);
            var after = worlds.LoadPreferences("Midgard2");
            Assert.False(after.Modifiers.ContainsKey("deathpenalty"),
                "Death penalty was set back to Normal and the copy still carries " +
                (after.Modifiers.TryGetValue("deathpenalty", out var v) ? v : "?"));
            Assert.Equal("hard", after.Modifiers["combat"]);
            Assert.Single(after.Modifiers);
            Assert.Null(after.Preset);
            // The Casual keys were taken by the dial at the first meeting, so nothing of it is
            // left among the carried keys to be written back by the next start either.
            Assert.DoesNotContain("deathkeepequip", after.Keys);
        }

        [Fact]
        public void A_dial_kept_on_the_source_value_stays_as_the_copy_has_it()
        {
            var worlds = CopyAndMeet("Midgard2");

            Forge(worlds, "Midgard2", AllFive("hard", "casual"));

            var after = worlds.LoadPreferences("Midgard2");
            Assert.Equal("casual", after.Modifiers["deathpenalty"]);
            Assert.Equal("hard", after.Modifiers["combat"]);
        }

        /// <summary>
        /// The other half of the contract, and the reason Normal is spelled out rather than sent
        /// blank: a dial the page said nothing about is not a statement about it. A brand new
        /// realm, or a duplicate whose source dials could not be read, still sends only what was
        /// moved, and the copy keeps its own difficulty for the rest.
        /// </summary>
        [Fact]
        public void Silence_about_a_dial_still_leaves_the_copy_as_it_was()
        {
            var worlds = CopyAndMeet("Midgard2");

            Forge(worlds, "Midgard2", new JObject { ["combat"] = "veryhard", ["deathpenalty"] = "" });

            var after = worlds.LoadPreferences("Midgard2");
            Assert.Equal("casual", after.Modifiers["deathpenalty"]);
            Assert.Equal("veryhard", after.Modifiers["combat"]);
        }

        [Fact]
        public void Only_the_word_normal_is_a_choice_and_only_for_a_real_dial()
        {
            var normals = BlendWindow.ParseExplicitNormalDials(new JObject
            {
                ["combat"] = "normal",
                ["deathpenalty"] = "",
                ["raids"] = null,
                ["portals"] = "hard",
            });

            Assert.Equal(new[] { "combat" }, normals.ToArray());
            Assert.Throws<ArgumentException>(() =>
                BlendWindow.ParseExplicitNormalDials(new JObject { ["weather"] = "normal" }));
            Assert.Empty(BlendWindow.ParseExplicitNormalDials(null));
        }

        /// <summary>
        /// The source world met where it lies, the way worldgen.get and servers.create meet it:
        /// the entry a copy kept under the source's own world name would share.
        /// </summary>
        private WorldKeyImportTests.InMemoryWorlds MeetSource()
        {
            var dir = Path.Combine(SourceFolder, "worlds_local");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "Midgard.fwl"), Fwl("Midgard", CasualAndHard));
            File.WriteAllBytes(Path.Combine(dir, "Midgard.db"), DbHeader());
            var worlds = new WorldKeyImportTests.InMemoryWorlds();
            var outcome = WorldKeyImportStep.Run(
                worlds, () => FwlReader.TryReadWorldStartingKeys(SourceFolder, "Midgard"), "Midgard");
            Assert.Equal(WorldKeyImportKind.Imported, outcome.Kind);
            return worlds;
        }

        private static List<string> SharedChanges(WorldKeyImportTests.InMemoryWorlds worlds, JObject dials)
            => BlendWindow.SharedEntryDialChanges(worlds.LoadPreferences("Midgard"),
                BlendWindow.ParseWorldModifiers(dials), BlendWindow.ParseExplicitNormalDials(dials), null);

        /// <summary>
        /// The path that needs no typing in the World box: a new realm named after the source
        /// world, World left blank, so servers.create names the copy's world after the realm and
        /// lands on the source's own world name. Death penalty moved from Casual to Normal used to
        /// be dropped there without a word. It is now named as a change to the shared entry, which
        /// is what servers.create refuses on.
        /// </summary>
        [Fact]
        public void A_blank_world_box_under_the_source_world_name_names_the_normal_it_cannot_keep()
        {
            var worlds = MeetSource();
            var world = InstallIsolationService.MakeSafeName("Midgard");
            Assert.Equal("Midgard", world);

            Assert.Equal(new[] { "deathpenalty" }, SharedChanges(worlds, AllFive("hard", "normal")).ToArray());
            // A moved dial on the same path changes the source as well, and is named too.
            Assert.Equal(new[] { "combat" }, SharedChanges(worlds, AllFive("veryhard", "casual")).ToArray());
        }

        [Fact]
        public void Every_dial_on_the_source_value_changes_nothing_on_the_shared_entry()
        {
            var worlds = MeetSource();

            Assert.Empty(SharedChanges(worlds, AllFive("hard", "casual")));
            Assert.Empty(SharedChanges(worlds, new JObject()));
            // A dial the page said nothing about is not a change either.
            Assert.Empty(SharedChanges(worlds, new JObject { ["combat"] = "hard", ["deathpenalty"] = "" }));
        }

        [Fact]
        public void A_preset_entry_is_changed_by_any_dial_and_a_switch_list_counts()
        {
            var preset = new WorldPreferences { WorldName = "Midgard", Preset = "hard" };
            Assert.Equal(new[] { "raids" }, BlendWindow.SharedEntryDialChanges(preset,
                new Dictionary<string, string>(), new HashSet<string> { "raids" }, null).ToArray());
            Assert.Empty(BlendWindow.SharedEntryDialChanges(preset,
                new Dictionary<string, string>(), new HashSet<string>(), null));

            var plain = new WorldPreferences { WorldName = "Midgard" };
            Assert.Empty(BlendWindow.SharedEntryDialChanges(plain,
                new Dictionary<string, string>(), new HashSet<string> { "raids" }, null));
            Assert.Equal(new[] { "switches" }, BlendWindow.SharedEntryDialChanges(plain,
                new Dictionary<string, string>(), new HashSet<string>(), new HashSet<string> { "nomap" }).ToArray());
            Assert.Empty(BlendWindow.SharedEntryDialChanges(null,
                new Dictionary<string, string>(), new HashSet<string> { "raids" }, null));
        }

        /// <summary>
        /// servers.create asks before it makes anything, and refuses by name rather than
        /// clearing the Normals and carrying on.
        /// </summary>
        [Fact]
        public void Servers_create_refuses_a_shared_entry_change_before_it_copies()
        {
            var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"));
            var from = bridge.IndexOf("RegisterRpc(\"servers.create\"", StringComparison.Ordinal);
            var to = bridge.IndexOf("RegisterRpc(", from + 10, StringComparison.Ordinal);
            var handler = bridge.Substring(from, to - from);

            var refuse = handler.IndexOf("\"servers.create.copySameWorldDials\"", StringComparison.Ordinal);
            var copy = handler.IndexOf("WorldStore.CopyWorldAs(", StringComparison.Ordinal);
            var provision = handler.IndexOf("InstallIsolation.ProvisionInstall(", StringComparison.Ordinal);
            Assert.True(refuse > 0, "servers.create no longer refuses a shared entry change");
            Assert.True(refuse < copy && refuse < provision, "the refusal comes after something was made");
            Assert.Contains("SharedEntryDialChanges(", handler);
            Assert.DoesNotContain("worldNormals.Clear()", handler);
        }

        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs")))
                    return dir.FullName;
            throw new InvalidOperationException("could not find the repository root");
        }

        [Fact]
        public void A_forge_that_chose_nothing_writes_nothing()
        {
            var worlds = new WorldKeyImportTests.InMemoryWorlds();

            Assert.Null(BlendWindow.ApplyForgeWorldChoices(worlds, "Fresh",
                new Dictionary<string, string>(), new HashSet<string>(), null));
            Assert.Null(worlds.LoadPreferences("Fresh"));
        }
    }
}
