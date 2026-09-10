using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The five companion plugins compile against the game's own assemblies, which only exist on a
    /// machine that has the dedicated server installed. They are deliberately not part of this
    /// solution, so nothing here can call into them. What can still be held to account is their
    /// source and their build scripts, and that is what these do: each one pins a rule that was
    /// wrong once, and each one fails against the source as it stood before it was fixed.
    /// </summary>
    public class CompanionPluginSourceTests
    {
        // Spelled in two halves on purpose: this very file is one of the files the reference
        // scan below reads, and a whole-name literal here would match itself every time.
        private static readonly string RemovedProjectFile = "BakaLoaderItemIndexer" + ".csproj";

        private static string RepoRoot([CallerFilePath] string here = "")
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(here) ?? ".");
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ValheimBakaLoader.sln")))
                dir = dir.Parent;

            Assert.True(dir != null, "could not find ValheimBakaLoader.sln above " + here);
            return dir.FullName;
        }

        private static string PathIn(params string[] parts) => Path.Combine(RepoRoot(), Path.Combine(parts));

        private static string Read(params string[] parts)
        {
            var path = PathIn(parts);
            Assert.True(File.Exists(path), path + " is missing");
            return File.ReadAllText(path);
        }

        private static string Commander =>
            Read("ValheimBakaLoader", "Resources", "Commander", "BakaLoaderCommander.cs");

        private static string SpawnHelper =>
            Read("ValheimBakaLoader", "Resources", "SpawnHelper", "BakaLoaderSpawnHelper.cs");

        private static string KillAll =>
            Read("ValheimBakaLoader", "Resources", "KillAll", "BakaKillAll.cs");

        private static string MaxPlayers =>
            Read("ValheimBakaLoader", "Resources", "MaxPlayers", "BakaLoaderMaxPlayers.cs");

        private static string Indexer => Read("BakaLoaderItemIndexer", "Plugin.cs");

        private static string BuildScript => Read("ValheimBakaLoader", "Resources", "build-plugins.ps1");

        private static string VerifyScript => Read("ValheimBakaLoader", "Resources", "verify-plugins.ps1");

        // ---- P8: the admission-cap transpiler must check what it is about to rewrite ----

        /// <summary>
        /// The transpiler took the first sbyte constant after the GetNrOfPlayers() call and
        /// rewrote it sight unseen. A game update that slipped another sbyte in between would
        /// have had it change the wrong number while still logging a clean patch.
        /// </summary>
        [Fact]
        public void MaxPlayersTranspiler_ChecksTheOperandAgainstTheVanillaCapBeforeRewritingIt()
        {
            var src = MaxPlayers;

            Assert.Contains("VanillaAdmissionCap = 10", src);
            Assert.Contains("IsVanillaAdmissionCap(codes[j].operand)", src);

            // The guard has to sit BEFORE the rewrite, or it guards nothing.
            var guard = src.IndexOf("IsVanillaAdmissionCap(codes[j].operand)", StringComparison.Ordinal);
            var rewrite = src.IndexOf("codes[j].operand = MaxPlayers.Value", StringComparison.Ordinal);
            Assert.True(guard >= 0 && rewrite >= 0 && guard < rewrite,
                "the operand check must run before the rewrite");
        }

        /// <summary>
        /// Every other check in verify-plugins.ps1 asks whether a NAME still resolves. A
        /// hand-written transpiler assumes a SHAPE, and no check covered that.
        /// </summary>
        [Fact]
        public void VerifyScript_ChecksTheConstantsTheTranspilersRewrite()
        {
            var src = VerifyScript;

            Assert.Contains("RPC_PeerInfo", src);
            Assert.Contains("GetNrOfPlayers", src);
            Assert.Contains("Find-SbyteConstant", src);
            Assert.Contains("ZPlayFabMatchmaking", src);
        }

        /// <summary>
        /// The gate has to ask its question the way the plugin asks it. ReplaceLobbyCap walks
        /// past any other sbyte and rewrites the first one carrying 11, while the gate took the
        /// first sbyte of any value and then demanded it be 11. Today's IL happens to agree, but
        /// CreateAndJoinNetwork already carries a second sbyte (15, the peer connectivity
        /// options), so a reordering in a game update would have failed a release the plugin
        /// still patches correctly, and a cap that moved off 11 behind an earlier 11 would have
        /// passed while the plugin rewrote the wrong constant.
        /// </summary>
        [Fact]
        public void VerifyScript_LooksForTheLobbyCapByValueTheWayThePluginDoes()
        {
            var src = VerifyScript;

            Assert.Contains("function Find-SbyteConstantWithValue", src);

            var lobby = src.IndexOf("ZPlayFabMatchmaking.{0} lobby cap", StringComparison.Ordinal);
            Assert.True(lobby > 0, "the lobby cap check is gone");

            // The search that feeds that report has to be the value-matching one.
            var block = src.Substring(0, lobby);
            var byValue = block.LastIndexOf("Find-SbyteConstantWithValue $lins", StringComparison.Ordinal);
            var byPosition = block.LastIndexOf("Find-SbyteConstant $lins", StringComparison.Ordinal);
            Assert.True(byValue > byPosition,
                "the lobby cap is still located by position rather than by value");

            // And the admission half keeps the position search, because that is the rule ITS
            // transpiler follows.
            Assert.Contains("Find-SbyteConstant $ins ($callAt + 1)", src);
        }

        // ---- P8 again: a refused admission patch must not leave the advertised caps raised ----

        /// <summary>
        /// Fail-closed stopped at the admission patch. The FejdStartup postfix still raised the
        /// Steam and PlayFab capacity, so a server whose admission rewrite was refused advertised
        /// the configured number and then turned those joiners away at the door.
        /// </summary>
        [Fact]
        public void MaxPlayers_BackendCapsAreOnlyRaisedWhenAdmissionWas()
        {
            var src = MaxPlayers;

            Assert.Contains("AdmissionCapRaised = true;", src);
            Assert.Contains("if (!AdmissionCapRaised)", src);

            // The flag is set by the admission transpiler and read by the backend postfix, and
            // the read has to come before the switch that installs the backend patches.
            var set = src.IndexOf("AdmissionCapRaised = true;", StringComparison.Ordinal);
            var read = src.IndexOf("if (!AdmissionCapRaised)", StringComparison.Ordinal);
            var backendSwitch = src.IndexOf("switch (ZNet.m_onlineBackend)", StringComparison.Ordinal);
            Assert.True(set < read && read < backendSwitch,
                "the guard must sit between the admission rewrite and the backend patches");
        }

        /// <summary>
        /// Both PlayFab methods embed 11, and in both it is vanilla's 10 plus the slot the server
        /// itself takes: CreateLobbyRequest lists the server as owner and first member. Passing
        /// the bare configured number to CreateLobby left the lobby one seat short, so the last
        /// player on a full-size crossplay server could never get in.
        /// </summary>
        [Fact]
        public void MaxPlayers_BothPlayFabCapsKeepTheReservedSlot()
        {
            var src = MaxPlayers;

            Assert.Contains("ReplaceLobbyCap(instructions, MaxPlayers.Value + 1, \"CreateLobby\")", src);
            Assert.Contains("ReplaceLobbyCap(instructions, MaxPlayers.Value + 1, \"CreateAndJoinNetwork\")", src);
            Assert.DoesNotContain("ReplaceLobbyCap(instructions, MaxPlayers.Value, ", src);
        }

        // ---- P4 and P5: one build recipe, and it builds all five ----

        [Fact]
        public void BuildScript_BuildsTheItemIndexerUnlessItIsExplicitlySkipped()
        {
            var src = BuildScript;

            Assert.Contains("[switch] $SkipItemIndexer", src);

            var skip = src.IndexOf("if ($SkipItemIndexer)", StringComparison.Ordinal);
            var entry = src.IndexOf("Dir = \"ItemIndexer\"", StringComparison.Ordinal);
            Assert.True(skip >= 0, "build-plugins.ps1 needs a way to opt out of the item indexer");
            Assert.True(entry > skip, "the item indexer must be added on the default path, not behind an opt-in");
            Assert.Contains("} else {", src.Substring(skip, entry - skip));
        }

        [Fact]
        public void ItemIndexer_HasOneBuildRecipeAndItIsBuildPluginsPs1()
        {
            Assert.False(File.Exists(PathIn("BakaLoaderItemIndexer", RemovedProjectFile)),
                "the second build recipe for BakaLoaderItemIndexer.dll is back");

            var bat = Read("BakaLoaderItemIndexer", "build-plugin.bat");
            Assert.Contains("build-plugins.ps1", bat);
            Assert.DoesNotContain("dotnet build", bat);
        }

        /// <summary>
        /// Removing a build recipe is only safe if nothing still points at it. This is the proof,
        /// re-run every time rather than taken on trust from the day it was removed.
        /// </summary>
        [Fact]
        public void NothingInTheRepoStillReferencesTheRemovedItemIndexerProject()
        {
            var offenders = SourceFiles()
                .Where(f => File.ReadAllText(f).Contains(RemovedProjectFile))
                .ToList();

            Assert.True(offenders.Count == 0,
                "still referenced by: " + string.Join(", ", offenders));
        }

        private static IEnumerable<string> SourceFiles()
        {
            var root = RepoRoot();
            var wanted = new[] { ".cs", ".ps1", ".bat", ".csproj", ".sln", ".md", ".json", ".yml" };
            var searched = new[]
            {
                "ValheimBakaLoader", "ValheimBakaLoader.Tests", "ValheimBakaLoader.Tools",
                "ValheimBakaLoader.Controls", "BakaLoaderItemIndexer", "docs",
            };

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
                if (wanted.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    yield return file;

            foreach (var name in searched)
            {
                var dir = Path.Combine(root, name);
                if (!Directory.Exists(dir)) continue;

                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (!wanted.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;

                    var relative = file.Substring(root.Length).Replace('\\', '/');
                    if (relative.Contains("/bin/") || relative.Contains("/obj/")) continue;

                    yield return file;
                }
            }
        }

        // ---- P9: a conjured object has to be marked the way the game marks its own ----

        [Theory]
        [InlineData("Commander")]
        [InlineData("SpawnHelper")]
        public void SpawnCommands_MarkEveryObjectTheWayVanillaSpawnDoes(string plugin)
        {
            var src = plugin == "Commander" ? Commander : SpawnHelper;

            Assert.Contains("ZDOVars.s_cheated", src);
            Assert.Contains("ItemDrop.OnCreateNew", src);
            Assert.Contains("PlayerProfile.s_bypassCheatChecks", src);

            // It has to run on the object that was just made, not somewhere unrelated.
            var instantiate = src.IndexOf("Object.Instantiate(prefab", StringComparison.Ordinal);
            var mark = src.IndexOf("MarkAsSpawnedIn(obj)", StringComparison.Ordinal);
            Assert.True(instantiate >= 0 && mark > instantiate,
                "the marking must follow the instantiation of the spawned object");
        }

        // ---- P10: killall is for hostiles, and a training post is not one ----

        [Theory]
        [InlineData("Commander")]
        [InlineData("KillAll")]
        public void KillAll_SparesPlayerBuiltTrainingPosts(string plugin)
        {
            var src = plugin == "Commander" ? Commander : KillAll;
            Assert.Contains("Character.Faction.TrainingDummy", src);
        }

        /// <summary>
        /// Two plugins answer the same command name and BakaLoader's button promises one thing
        /// for both: "players, pets and allies spared". Commander answers it over its own RCON,
        /// BakaKillAll registers the in-game console command, and BakaKillAll used to spare only
        /// players and training posts. An operator typing baka_killall at the server console lost
        /// every tamed wolf, boar and lox on the server, and every dvergr ally with them.
        /// </summary>
        [Theory]
        [InlineData("Commander")]
        [InlineData("KillAll")]
        public void KillAll_SparesTamedPetsAndTheFriendlyFactions(string plugin)
        {
            var src = plugin == "Commander" ? Commander : KillAll;

            Assert.Contains("c.IsTamed()", src);
            Assert.Contains("Character.Faction.Players", src);
            Assert.Contains("Character.Faction.AnimalsVeg", src);
            Assert.Contains("Character.Faction.Dverger", src);
            Assert.Contains("Character.Faction.PlayerSpawned", src);
        }

        // ---- P11: a trinket is equipment, and equipment is what keeps its quality ----

        [Fact]
        public void ItemIndexer_ClassifiesTrinketsAsEquipment()
        {
            var src = Indexer;

            var trinket = src.IndexOf("case ItemDrop.ItemData.ItemType.Trinket:", StringComparison.Ordinal);
            Assert.True(trinket >= 0, "Trinket is missing from ClassifyItem, so it falls through to plain Item");

            var next = src.IndexOf("return \"", trinket, StringComparison.Ordinal);
            Assert.True(next > trinket, "the Trinket case leads nowhere");
            Assert.StartsWith("return \"Equipment\";", src.Substring(next));
        }

        /// <summary>
        /// The Trinket fix argued from ItemData.IsEquipable, which was read straight out of the
        /// shipped assembly: it returns true for Tool, OneHandedWeapon, TwoHandedWeapon,
        /// TwoHandedWeaponLeft, Bow, Shield, Helmet, Chest, Legs, Shoulder, Ammo, Torch, Utility
        /// and Trinket. Utility and Ammo were the two the list still left out, and Utility is the
        /// one that costs something: the caller keeps a quality value only for "Equipment", so a
        /// utility item with qualities lost the picker's quality box for exactly the reason the
        /// Trinket case was written.
        /// </summary>
        [Theory]
        [InlineData("Utility")]
        [InlineData("Ammo")]
        public void ItemIndexer_ClassifiesTheRestOfTheEquipableTypesAsEquipment(string type)
        {
            var src = Indexer;

            var at = src.IndexOf("case ItemDrop.ItemData.ItemType." + type + ":", StringComparison.Ordinal);
            Assert.True(at >= 0, type + " is missing from ClassifyItem, so it falls through to plain Item");

            var next = src.IndexOf("return \"", at, StringComparison.Ordinal);
            Assert.True(next > at, "the " + type + " case leads nowhere");
            Assert.StartsWith("return \"Equipment\";", src.Substring(next));
        }
    }
}
