using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The three places a realm's own facts were answered with another realm's.
    /// </summary>
    /// <remarks>
    /// In the DiagnosticSink collection because one rule here names
    /// ReadWorldSaveWithDiagnostics, and the gate that keeps every class which can trip that
    /// one static sink off the others reads the source rather than what the class does. Nothing
    /// here installs a sink; sharing the collection costs a dozen fast tests their parallelism
    /// and keeps the gate's own algorithm the thing that decides.
    /// </remarks>
    [Collection("DiagnosticSink")]
    public class RealmParityTests : BaseTest, IDisposable
    {
        private readonly string Root = Path.Combine(
            Path.GetTempPath(), "bakaloader-parity-" + Guid.NewGuid().ToString("N"));

        public RealmParityTests() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        // ------------------------------------------------------------------ the item catalog

        private string RealmWithCatalog(string name, params string[] prefabs)
        {
            var bep = Path.Combine(Root, name, "BepInEx");
            Directory.CreateDirectory(bep);

            var rows = prefabs.Select(p =>
                "{\"prefab\":\"" + p + "\",\"name\":\"" + p + "\",\"category\":\"Resource\"}");
            File.WriteAllText(Path.Combine(bep, "items.json"), "[" + string.Join(",", rows) + "]");
            return bep;
        }

        private string RealmWithNoCatalog(string name)
        {
            var bep = Path.Combine(Root, name, "BepInEx");
            Directory.CreateDirectory(bep);
            return bep;
        }

        /// <summary>
        /// The missing-file branch used to reload the bundled list only when NOTHING was loaded,
        /// which is true on a cold start and false on every realm switch. So a realm with no
        /// items.json of its own kept the previous realm's catalog: the picker went on offering
        /// the other realm's modded prefabs, the spawn validator accepted them because they were
        /// in Entries, and items.search handed the page the OTHER realm's install path as the
        /// place it had read them from.
        /// </summary>
        [Fact]
        public void A_realm_with_no_catalog_does_not_keep_the_previous_realms()
        {
            var catalog = new ItemCatalog(GetService<IApplicationLogger>());

            var withMods = RealmWithCatalog("alpha", "RealmAOnlyBlade", "Wood");
            catalog.EnsureLoaded(withMods);

            Assert.True(catalog.IsLiveCatalog);
            Assert.Contains(catalog.Entries, e => e.PrefabName == "RealmAOnlyBlade");

            // Over to a realm that has never started, so it has written no catalog of its own.
            catalog.EnsureLoaded(RealmWithNoCatalog("beta"));

            Assert.False(catalog.IsLiveCatalog);
            Assert.DoesNotContain(catalog.Entries, e => e.PrefabName == "RealmAOnlyBlade");
            Assert.DoesNotContain(withMods, catalog.LoadedFrom ?? "");
        }

        [Fact]
        public void A_realm_with_its_own_catalog_still_gets_its_own()
        {
            var catalog = new ItemCatalog(GetService<IApplicationLogger>());

            catalog.EnsureLoaded(RealmWithCatalog("one", "OneBlade"));
            Assert.Contains(catalog.Entries, e => e.PrefabName == "OneBlade");

            catalog.EnsureLoaded(RealmWithCatalog("two", "TwoBlade"));
            Assert.Contains(catalog.Entries, e => e.PrefabName == "TwoBlade");
            Assert.DoesNotContain(catalog.Entries, e => e.PrefabName == "OneBlade");
        }

        /// <summary>
        /// The spawn validator reads the catalog and never asked for this realm's one. The
        /// picker's own search did, which is why the two could disagree.
        /// </summary>
        [Fact]
        public void The_spawn_validator_loads_this_realms_catalog_before_it_judges_a_prefab()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            var spawn = bridge.IndexOf("RegisterRpc(\"players.spawn\"", StringComparison.Ordinal);
            Assert.True(spawn > 0);

            var lookup = bridge.IndexOf("ItemCatalog.Entries.FirstOrDefault", spawn, StringComparison.Ordinal);
            var ensure = bridge.IndexOf("EnsureItemCatalogLoaded();", spawn, StringComparison.Ordinal);

            Assert.True(ensure > 0 && ensure < lookup,
                "players.spawn judges a prefab against whatever catalog happens to be in memory");
        }

        // ------------------------------------------------------------------ the Barrow's owner

        private static ServerPreferences Profile(string name, string world, string saveFolder) =>
            new() { ProfileName = name, WorldName = world, SaveDataFolderPath = saveFolder };

        /// <summary>
        /// The Default realm carries no save folder of its own by design, and the overview's
        /// owner lookup compared that blank against the folder it was walking, which is always
        /// false. So every one of the Default realm's worlds drew as unclaimed, a live one never
        /// showed the raiding dot, and the Delete control was offered for a world worlds.delete
        /// refuses the moment the host has typed its name into the confirm box. An added realm
        /// was fine; the realm every host starts on was not.
        /// </summary>
        [Fact]
        public void The_default_realm_owns_its_worlds_the_way_an_added_realm_does()
        {
            var userSave = @"C:\Users\Someone\AppData\LocalLow\IronGate\Valheim";
            var alphaSave = @"C:\Valheim\servers\Alpha";

            var profiles = new List<ServerPreferences>
            {
                Profile("Default", "Midgard", null),
                Profile("Alpha", "Frostreach", alphaSave),
            };

            // What the delete gate has always enforced.
            Assert.Equal("Default",
                BlendWindow.ProfileSelectingWorld(profiles, "Midgard", userSave, userSave)?.ProfileName);
            Assert.Equal("Alpha",
                BlendWindow.ProfileSelectingWorld(profiles, "Frostreach", alphaSave, userSave)?.ProfileName);

            // And a world nobody claims is still nobody's.
            Assert.Null(BlendWindow.ProfileSelectingWorld(profiles, "Orphan", userSave, userSave));
        }

        [Fact]
        public void The_barrow_asks_the_same_question_the_delete_gate_answers()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            var overview = bridge.IndexOf("RegisterRpc(\"backups.overview\"", StringComparison.Ordinal);
            Assert.True(overview > 0);

            var chunk = bridge.Substring(overview, Math.Min(4000, bridge.Length - overview));
            Assert.Contains("ProfileSelectingWorld(profiles, world, saveFolder, userSave)", chunk);
        }

        /// <summary>
        /// The same blank-folder blindness on the third sibling. A realm with no folder of its
        /// own could never be named as the one still using the shared folder, so deleting an
        /// isolated realm's files could take the shared worlds with them with nothing said.
        /// </summary>
        [Fact]
        public void A_realm_on_the_shared_folder_is_named_as_sharing_it()
        {
            var userSave = @"C:\Users\Someone\AppData\LocalLow\IronGate\Valheim";

            var profiles = new List<ServerPreferences>
            {
                Profile("Default", "Midgard", null),
                Profile("Beta", "Beta", userSave),
            };

            Assert.Equal("Default",
                BlendWindow.ProfileSharingSaveFolder(profiles, "Beta", userSave, userSave)?.ProfileName);

            // And with no app-wide folder to hand, exactly the old behaviour.
            Assert.Null(BlendWindow.ProfileSharingSaveFolder(profiles, "Beta", userSave));
        }

        // ------------------------------------------------------- the new realm's save folder

        /// <summary>
        /// "Own save folder" off means the SHARED folder, and that is the only other thing it
        /// can honestly mean. The new realm is seeded from the ACTIVE realm's preferences, so
        /// leaving the field alone handed it whatever private folder that realm had: found a
        /// realm from an isolated one with the switch off and the new realm wrote its worlds
        /// into the other realm's folder, while the note under the boxes said its worlds never
        /// mix and neither realm could afterwards be deleted with its files.
        /// </summary>
        [Fact]
        public void A_new_realm_with_the_switch_off_takes_the_shared_folder_not_the_active_realms()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            var create = bridge.IndexOf("RegisterRpc(\"servers.create\"", StringComparison.Ordinal);
            Assert.True(create > 0);

            var chunk = bridge.Substring(create, Math.Min(9000, bridge.Length - create));
            Assert.Contains("created.SaveDataFolderPath = MakeIsolatedSaveFolder(name);", chunk);
            Assert.Contains("created.SaveDataFolderPath = null;", chunk);
        }

        /// <summary>
        /// And the live warning now fires on a shared save folder whether or not the two realms
        /// also share a world name. It used to need BOTH, so a realm pointed at another realm's
        /// private folder said nothing at all.
        /// </summary>
        [Fact]
        public void A_shared_save_folder_is_warned_about_on_its_own()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            var check = bridge.IndexOf("RegisterRpc(\"servers.checkCollision\"", StringComparison.Ordinal);
            Assert.True(check > 0);

            var chunk = bridge.Substring(check, Math.Min(4000, bridge.Length - check));
            Assert.Contains("kind = \"saveFolder\"", chunk);
            // The app-wide folder is left out: sharing THAT is the documented default.
            Assert.Contains("!SamePath(save, userSave ?? \"\")", chunk);
        }

        // ------------------------------------------------- the integrity read in front of a Start

        /// <summary>
        /// The check parses EVERY chunk file of a world, and it sits on the road in front of a
        /// Start. It had no ceiling, so a large world on a slow disk held the button; and its
        /// answer was worked out again on every ask although nothing about it can change while
        /// the bytes do not. It has a clock now and a memo keyed on the file itself, so a server
        /// run that writes to the save is read again and nothing else is.
        /// </summary>
        [Fact]
        public void The_integrity_read_carries_a_clock_and_is_remembered_per_file()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
            var at = bridge.IndexOf("RegisterRpc(\"worlds.integrity\"", StringComparison.Ordinal);
            Assert.True(at > 0);

            var chunk = bridge.Substring(at, Math.Min(4000, bridge.Length - at));
            Assert.Contains("Task.Delay(WorldIntegrityBudget)", chunk);
            Assert.Contains("_worldIntegrityMemo", chunk);
            Assert.Contains("WorldIntegrityStamp(dbPath)", chunk);

            // The parse still runs off the UI thread, which it always did.
            Assert.Contains("Task.Run(() => ReadWorldSaveWithDiagnostics", chunk);
        }

        [Fact]
        public void The_memo_key_moves_when_the_save_is_written_to()
        {
            var method = typeof(ValheimBakaLoader.Forms.BlendWindow).GetMethod(
                "WorldIntegrityStamp",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var path = Path.Combine(Root, "world.db");
            File.WriteAllBytes(path, new byte[16]);
            var first = (string)method.Invoke(null, new object[] { path });

            // Same bytes, same answer, so the expensive parse is not repeated.
            Assert.Equal(first, (string)method.Invoke(null, new object[] { path }));

            // A server run wrote to it, so the answer has to be worked out again.
            File.WriteAllBytes(path, new byte[32]);
            Assert.NotEqual(first, (string)method.Invoke(null, new object[] { path }));

            // A file nobody can read is never remembered, because remembering it would settle
            // on an answer that was never read.
            var missing = Path.Combine(Root, "not-there.db");
            Assert.NotEqual(
                (string)method.Invoke(null, new object[] { missing }),
                (string)method.Invoke(null, new object[] { missing }));
        }

        /// <summary>The page drops what it read about the previous realm's worlds on a switch.</summary>
        [Fact]
        public void The_page_forgets_the_previous_realms_integrity_answers()
        {
            var page = AppSourceTree.Web("app.js");
            Assert.Contains("for(const key in WORLD_INTEGRITY_READ) delete WORLD_INTEGRITY_READ[key];", page);
        }

        // ------------------------------------------------------- the Directories note

        /// <summary>
        /// Two independent facts need two independent sentences. One AND decided a note that
        /// speaks about both, and the two switches in the forge can be set independently: a
        /// realm with its own install and the shared save folder was told it "uses the shared
        /// install", directly under a line reading its own install path.
        /// </summary>
        [Fact]
        public void The_directories_note_speaks_about_the_install_and_the_saves_separately()
        {
            var page = AppSourceTree.Web("app.js");

            Assert.Contains("world.dir.install_note.own", page);
            Assert.Contains("world.dir.install_note.shared", page);
            Assert.Contains("world.dir.save_note.own", page);
            Assert.Contains("world.dir.save_note.shared", page);

            var catalog = AppSourceTree.Web("i18n/en.json");
            foreach (var id in new[]
                     {
                         "world.dir.install_note.own", "world.dir.install_note.shared",
                         "world.dir.save_note.own", "world.dir.save_note.shared",
                     })
            {
                Assert.Contains("\"" + id + "\"", catalog);
            }
        }
    }
}
