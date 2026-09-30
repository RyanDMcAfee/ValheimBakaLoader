using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BakaLoaderUncheatPlan;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// BakaLoaderUncheat, the client companion: the sweep that takes the cheat mark off what a
    /// player's own character is carrying.
    /// </summary>
    /// <remarks>
    /// WHY IT EXISTS AT ALL. A player's inventory is not in the world. It lives in the character
    /// file on their own machine, which is why the server-side cleanse says out loud that it
    /// cannot reach it, and why nothing a host does on the server will ever take a mark off
    /// somebody's axe. The companion runs on the client instead.
    /// <para>
    /// WHAT THESE HOLD. The plan is pure, so the sweep, the stamp and the generation rule are all
    /// driven here over a synthetic inventory with no game installed. The half that touches
    /// Valheim is held by its source and by verify-plugins.ps1, which reads the built DLL against
    /// a real Managed folder; the one rule about it worth writing down here is that it does
    /// nothing on a dedicated server, because that is the road it must never take.
    /// </para>
    /// </remarks>
    public class ClientCompanionTests
    {
        // ------------------------------------------------------------------ the sweep

        /// <summary>
        /// A synthetic inventory: a marked axe, a clean shield, a marked helmet being worn, and a
        /// stack of wood with no mark on it. Only the two marked ones change, and nothing else
        /// about any of them is touched.
        /// </summary>
        [Fact]
        public void Only_the_marked_items_change()
        {
            var axe = new MarkedItem("AxeIron", cheated: true);
            var shield = new MarkedItem("ShieldWood", cheated: false);
            var helmet = new MarkedItem("HelmetBronze", cheated: true, equipped: true);
            var wood = new MarkedItem("Wood", cheated: false);

            var cleared = UncheatPlan.ClearAll(new[] { axe, shield, helmet, wood });

            Assert.Equal(2, cleared);
            Assert.False(axe.Cheated);
            Assert.False(helmet.Cheated);
            Assert.False(shield.Cheated);
            Assert.False(wood.Cheated);

            // Names and the worn flag are read and never written.
            Assert.Equal("HelmetBronze", helmet.Name);
            Assert.True(helmet.Equipped);
        }

        [Fact]
        public void A_clean_inventory_is_swept_and_nothing_changes()
        {
            var items = new[] { new MarkedItem("Wood", false), new MarkedItem("Stone", false) };
            Assert.Equal(0, UncheatPlan.ClearAll(items));
            Assert.All(items, item => Assert.False(item.Cheated));
        }

        [Fact]
        public void An_empty_inventory_and_a_missing_one_are_both_nothing_to_do()
        {
            Assert.Equal(0, UncheatPlan.ClearAll(new MarkedItem[0]));
            Assert.Equal(0, UncheatPlan.ClearAll(null));

            // A null in the middle of a list is a slot the game has not filled, not a crash.
            var axe = new MarkedItem("AxeIron", true);
            Assert.Equal(1, UncheatPlan.ClearAll(new[] { null, axe, null }));
            Assert.False(axe.Cheated);
        }

        [Fact]
        public void Sweeping_twice_clears_nothing_the_second_time()
        {
            var items = new[] { new MarkedItem("AxeIron", true), new MarkedItem("Wood", true) };

            Assert.Equal(2, UncheatPlan.ClearAll(items));
            Assert.Equal(0, UncheatPlan.ClearAll(items));
        }

        // ------------------------------------------------------------------ the stamp

        /// <summary>
        /// A character with no stamp has never been swept, so it is swept. Anything that is not a
        /// number is also a character that has never been swept by THIS plugin: customData is one
        /// dictionary shared by everything on the client, and a value under this key that is not a
        /// generation is somebody else's or a hand edit.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("yes")]
        [InlineData("v1")]
        [InlineData("1.0")]
        public void A_character_with_no_readable_stamp_is_swept(string stamp)
        {
            Assert.True(UncheatPlan.ShouldRun(stamp, generation: 1));
        }

        /// <summary>
        /// The generation rule. A stamp at or above the generation asked for is the sweep having
        /// already been here; below it, the host has asked for another pass.
        /// </summary>
        [Theory]
        [InlineData("1", 1, false)]
        [InlineData("2", 1, false)]
        [InlineData("1", 2, true)]
        [InlineData("1", 7, true)]
        [InlineData("6", 7, true)]
        [InlineData("7", 7, false)]
        [InlineData("0", 1, true)]
        public void Raising_the_generation_is_what_asks_for_another_pass(
            string stamp, int generation, bool runs)
        {
            Assert.Equal(runs, UncheatPlan.ShouldRun(stamp, generation));
        }

        [Fact]
        public void The_stamp_written_down_is_the_generation_it_ran_at_and_reads_back()
        {
            for (var generation = 1; generation < 6; generation++)
            {
                var stamp = UncheatPlan.Stamp(generation);
                Assert.Equal(generation.ToString(), stamp);

                // Written at N, it does not run again at N, and it does at N+1.
                Assert.False(UncheatPlan.ShouldRun(stamp, generation));
                Assert.True(UncheatPlan.ShouldRun(stamp, generation + 1));
            }
        }

        /// <summary>
        /// The key is named for the mod and versioned. customData is shared by everything on the
        /// client, so a key like "done" is a collision waiting for the next mod that thinks of it.
        /// </summary>
        [Fact]
        public void The_stamp_key_is_namespaced_and_versioned()
        {
            Assert.Equal("baka.uncheat.v1", UncheatPlan.StampKey);
            Assert.StartsWith("baka.", UncheatPlan.StampKey);
            Assert.Matches(@"\.v[0-9]+$", UncheatPlan.StampKey);
        }

        // ------------------------------------------------------------------ what it says

        /// <summary>
        /// Three different facts, three different sentences. "Cleared 0" and "did not look" are
        /// not the same thing, and a host reading a client log needs to know which one they have.
        /// </summary>
        [Fact]
        public void The_line_it_writes_says_which_of_the_three_things_happened()
        {
            var swept = UncheatPlan.Reply(cleared: 2, looked: 40, generation: 1);
            Assert.Contains("2 items", swept);
            Assert.Contains("40", swept);

            var clean = UncheatPlan.Reply(cleared: 0, looked: 40, generation: 1);
            Assert.Contains("none of them", clean);
            Assert.DoesNotContain("Cleared the cheat mark", clean);

            var nothing = UncheatPlan.Reply(cleared: 0, looked: 0, generation: 1);
            Assert.Contains("Nothing in this character's inventory", nothing);

            // The singular really is singular.
            Assert.Contains("1 item ", UncheatPlan.Reply(cleared: 1, looked: 9, generation: 1));

            // And the generation is on every one of them, because it is what decides whether the
            // host will ever see this line again.
            foreach (var line in new[] { swept, clean, nothing })
                Assert.Contains("generation 1", line);
        }

        [Fact]
        public void An_already_swept_character_is_told_how_to_ask_again()
        {
            var said = UncheatPlan.AlreadyDone("1");
            Assert.Contains("already swept", said);
            Assert.Contains("Generation", said);
            Assert.DoesNotContain("null", UncheatPlan.AlreadyDone(null));
        }

        // ------------------------------------------------------------------ the plugin itself

        /// <summary>
        /// It does nothing on a dedicated server. A server has no local character, and a sweep
        /// there would be a client mod rewriting something it has no business in.
        /// <para>
        /// The check is on the INSTANCE, because ZNet.IsDedicated is an instance method on this
        /// build of the game, and it is guarded with a null check because this runs on the road
        /// into the world where a throw would be in front of a character loading.
        /// </para>
        /// </summary>
        [Fact]
        public void The_plugin_does_nothing_on_a_dedicated_server()
        {
            var source = PluginSource();

            Assert.Contains("ZNet.instance != null && ZNet.instance.IsDedicated()", source);
            Assert.Contains("NothingToDoOnAServer", source);

            // And the guard is the FIRST thing the sweep does, before it reads a stamp or an
            // inventory: a guard after the work is not a guard.
            var sweepAt = source.IndexOf("private static void Sweep(Player player)", StringComparison.Ordinal);
            Assert.True(sweepAt > 0, "the plugin no longer has a Sweep");
            var body = source.Substring(sweepAt);

            var dedicatedAt = body.IndexOf("IsDedicated()", StringComparison.Ordinal);
            var stampAt = body.IndexOf("TryGetValue(UncheatPlan.StampKey", StringComparison.Ordinal);
            var inventoryAt = body.IndexOf("player.GetInventory()", StringComparison.Ordinal);

            Assert.True(dedicatedAt > 0 && stampAt > dedicatedAt,
                "the stamp is read before the dedicated-server guard");
            Assert.True(inventoryAt > dedicatedAt,
                "the inventory is read before the dedicated-server guard");
        }

        /// <summary>
        /// The local character and nobody else. Every other Player object on a client is a peer,
        /// and a peer's inventory is not this machine's to rewrite.
        /// </summary>
        [Fact]
        public void It_sweeps_the_local_character_and_no_peer()
        {
            var source = PluginSource();
            Assert.Contains("ReferenceEquals(player, Player.m_localPlayer)", source);
        }

        /// <summary>
        /// It runs after the character is LOADED. Awake and Start both happen before the
        /// inventory has been read off the character file, so a sweep at either would look at an
        /// empty list, clear nothing, and stamp the character as done for ever.
        /// </summary>
        [Fact]
        public void It_runs_after_the_character_is_loaded_and_only_once()
        {
            var source = PluginSource();

            Assert.Contains("[HarmonyPatch(typeof(Player), \"OnSpawned\")]", source);
            Assert.Contains("private static void Postfix(Player __instance)", source);
            Assert.DoesNotContain("\"Awake\")]", source);
            Assert.DoesNotContain("\"Start\")]", source);

            // The stamp is written on every road out of the sweep that looked, not only when
            // something was cleared: a character with nothing on it has been looked at.
            Assert.Contains("customData[UncheatPlan.StampKey] = UncheatPlan.Stamp(generation);", source);

            // And a throw in the postfix never reaches the game, because a postfix that throws on
            // the road into the world is a character that will not load.
            Assert.Contains("catch (Exception problem)", source);
        }

        /// <summary>
        /// What a Thunderstore package is: four files, flat, with a manifest that says the same
        /// version the plugin does.
        /// </summary>
        [Fact]
        public void The_release_asset_carries_the_four_files_thunderstore_reads()
        {
            var folder = UncheatFolder();

            foreach (var name in new[] { "BakaLoaderUncheat.cs", "BakaUncheatPlan.cs",
                                         "manifest.json", "README.md", "icon.png" })
                Assert.True(File.Exists(Path.Combine(folder, name)), name + " is missing from " + folder);

            var manifest = File.ReadAllText(Path.Combine(folder, "manifest.json"));
            Assert.Contains("\"name\": \"BakaLoaderUncheat\"", manifest);
            Assert.Contains("\"version_number\": \"1.0.0\"", manifest);
            Assert.Contains("BepInExPack_Valheim", manifest);

            // The plugin says the same version the manifest does. Two numbers for one thing drift
            // the first time only one of them is remembered.
            Assert.Contains("PluginVersion = \"1.0.0\"", PluginSource());

            // Thunderstore wants a 256x256 icon. The bytes say so in the PNG header: width and
            // height are two big-endian ints at offset 16.
            var png = File.ReadAllBytes(Path.Combine(folder, "icon.png"));
            Assert.True(png.Length > 24, "the icon is not a PNG");
            Assert.Equal(256, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
            Assert.Equal(256, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        }

        /// <summary>
        /// The build recipe knows about it, so a game update cannot leave it built against a
        /// different assembly set than the four plugins beside it.
        /// </summary>
        [Fact]
        public void The_one_build_recipe_builds_it_too()
        {
            var build = File.ReadAllText(Path.Combine(ResourcesFolder(), "build-plugins.ps1"));

            Assert.Contains("Dir = \"Uncheat\"", build);
            Assert.Contains("BakaLoaderUncheat.cs", build);
            Assert.Contains("BakaUncheatPlan.cs", build);
            Assert.Contains("Out = \"BakaLoaderUncheat.dll\"", build);

            // verify-plugins.ps1 reads every DLL in the folder it is pointed at, so nothing has to
            // be added there for the new one to be checked. This holds that to be true rather
            // than assumed, because the day it becomes a named list the new plugin drops out of
            // the gate in silence.
            var verify = File.ReadAllText(Path.Combine(ResourcesFolder(), "verify-plugins.ps1"));
            Assert.Contains("Get-ChildItem -LiteralPath $PluginDir -Filter *.dll -Recurse -File", verify);
        }

        /// <summary>
        /// It must never be copied into the app's own payload. The installer puts the plugins from
        /// Resources onto a SERVER, and this one belongs on a client: shipped in that folder it
        /// would be a client mod sitting in a server's plugins directory doing nothing, and
        /// nobody would know why it was there.
        /// </summary>
        [Fact]
        public void It_is_not_part_of_the_apps_own_payload()
        {
            var csproj = File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(ResourcesFolder()), "ValheimBakaLoader.csproj"));

            Assert.DoesNotContain("Content Include=\"Resources\\Uncheat", csproj);
            Assert.Contains("Compile Remove=\"Resources\\Uncheat\\BakaLoaderUncheat.cs\"", csproj);
        }

        // ------------------------------------------------------------------ plumbing

        private static string RepoRoot()
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at != null && !File.Exists(Path.Combine(at.FullName, "ValheimBakaLoader.sln")))
                at = at.Parent;

            Assert.NotNull(at);
            return at.FullName;
        }

        private static string ResourcesFolder() =>
            Path.Combine(RepoRoot(), "ValheimBakaLoader", "Resources");

        private static string UncheatFolder() => Path.Combine(ResourcesFolder(), "Uncheat");

        private static string PluginSource() =>
            File.ReadAllText(Path.Combine(UncheatFolder(), "BakaLoaderUncheat.cs"));
    }
}
