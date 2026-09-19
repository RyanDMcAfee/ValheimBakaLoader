using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

        /// <summary>
        /// The pure half of baka_killall. It lives beside the KillAll plugin, it is compiled
        /// into both companion DLLs, and it is also globbed into BakaLoader itself, which is
        /// how KillAllPlanTests can call it for real instead of reading it as text.
        /// </summary>
        private static string KillAllPlanSource =>
            Read("ValheimBakaLoader", "Resources", "KillAll", "BakaKillAllPlan.cs");

        /// <summary>The half that touches the game, which only the plugin compiler ever sees.</summary>
        private static string KillAllSweepSource =>
            Read("ValheimBakaLoader", "Resources", "KillAll", "BakaKillAllSweep.cs");

        private static string SourceNamed(string file)
        {
            switch (file)
            {
                case "Commander": return Commander;
                case "KillAll": return KillAll;
                case "Sweep": return KillAllSweepSource;
                case "Plan": return KillAllPlanSource;
                default: throw new ArgumentOutOfRangeException(nameof(file), file, "no such companion source");
            }
        }

        private static string MaxPlayers =>
            Read("ValheimBakaLoader", "Resources", "MaxPlayers", "BakaLoaderMaxPlayers.cs");

        private static string Indexer => Read("BakaLoaderItemIndexer", "Plugin.cs");

        /// <summary>
        /// The source with its whole-line comments taken out, for rules about what the compiled
        /// code may do. The plugin headers explain the bugs they were written against, so a rule
        /// that forbids a name would otherwise be tripped by the paragraph explaining why.
        /// </summary>
        private static string WithoutComments(string src) =>
            string.Join("\n", src.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

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
            Assert.Contains("CheatChecksBypassed()", src);

            // It has to run on the object that was just made, not somewhere unrelated.
            var instantiate = src.IndexOf("Object.Instantiate(prefab", StringComparison.Ordinal);
            var mark = src.IndexOf("MarkAsSpawnedIn(obj)", StringComparison.Ordinal);
            Assert.True(instantiate >= 0 && mark > instantiate,
                "the marking must follow the instantiation of the spawned object");
        }

        // ---- 1.0.9: the game turned a field into a property and took spawning with it ----

        /// <summary>
        /// Valheim 1.0.12 turned PlayerProfile.s_bypassCheatChecks from a static field into a
        /// static property. A compiled field read is an ldsfld, and Mono raises the resulting
        /// MissingFieldException when it JITs the method holding the read, which is the CALLER,
        /// outside MarkAsSpawnedIn's own try/catch. The spawn loop died after the first object:
        /// one item on the ground, no level, no quality. Nothing in either plugin may name that
        /// member directly again; the lookup has to ask the live assembly what shape it is.
        /// </summary>
        [Theory]
        [InlineData("Commander")]
        [InlineData("SpawnHelper")]
        public void SpawnCommands_ReadTheCheatBypassThroughReflectionNotADirectReference(string plugin)
        {
            var src = plugin == "Commander" ? Commander : SpawnHelper;

            // Comments are free to name the member - explaining the bug is the point of them.
            // What must not survive anywhere is CODE that reads it, because that is the ldsfld.
            Assert.DoesNotContain("PlayerProfile" + ".s_bypassCheatChecks", WithoutComments(src));

            Assert.Contains("typeof(PlayerProfile).GetProperty(\"s_bypassCheatChecks\"", src);
            Assert.Contains("typeof(PlayerProfile).GetField(\"s_bypassCheatChecks\"", src);
            Assert.Contains("BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static", src);

            // Property first: that is the shape the game has now, and a field of the same name
            // would only ever be an older build.
            var property = src.IndexOf("GetProperty(\"s_bypassCheatChecks\"", StringComparison.Ordinal);
            var field = src.IndexOf("GetField(\"s_bypassCheatChecks\"", StringComparison.Ordinal);
            Assert.True(property < field, "the property lookup has to come before the field one");

            // Absent or unreadable means cheated, which is what vanilla does with the bypass off.
            var helper = src.IndexOf("private static bool CheatChecksBypassed()", StringComparison.Ordinal);
            Assert.True(helper > 0, "the cached reflection helper is gone");
            Assert.Contains("return false;", src.Substring(helper));
        }

        // ---- 1.0.9: item quality and stacking, which never worked at all ----

        /// <summary>
        /// The spawn loop only ever called Character.SetLevel, so the quality the picker
        /// collected for every tool, weapon and piece of armour was carried all the way to the
        /// server and then dropped. The game's own spawn command sets full durability and calls
        /// ItemDrop.SetQuality, and quality has to be set FIRST because maximum durability grows
        /// with it.
        /// </summary>
        [Theory]
        [InlineData("Commander")]
        [InlineData("SpawnHelper")]
        public void SpawnCommands_GiveItemsTheirQualityAndFullDurability(string plugin)
        {
            var src = plugin == "Commander" ? Commander : SpawnHelper;

            Assert.Contains("item.SetQuality(applied)", src);
            Assert.Contains("data.m_durability = data.GetMaxDurability();", src);
            Assert.Contains("data.m_shared.m_maxQuality", src);

            var quality = src.IndexOf("item.SetQuality(applied)", StringComparison.Ordinal);
            var durability = src.IndexOf("data.m_durability = data.GetMaxDurability();", StringComparison.Ordinal);
            Assert.True(quality < durability,
                "quality must be set before durability, or the item arrives at its quality 1 maximum");
        }

        /// <summary>
        /// A stack of six meads is one pile, not six drops on the floor. The loop counts objects
        /// and units separately, and the mutated item data has to reach the ZDO or nothing the
        /// loop just did survives the first client that reads the drop.
        /// </summary>
        [Theory]
        [InlineData("Commander")]
        [InlineData("SpawnHelper")]
        public void SpawnCommands_SpawnStackableItemsAsStacks(string plugin)
        {
            var src = plugin == "Commander" ? Commander : SpawnHelper;

            Assert.Contains("MaxStackSizeOf(prefab)", src);
            Assert.Contains("Mathf.CeilToInt(", src);
            Assert.Contains("data.m_stack = Mathf.Clamp(stack, 1, maxStack);", src);

            // Only the owner may write a ZDO, and SaveToZDO is the public path the game's own
            // private Save() takes.
            Assert.Contains("view.IsValid() && view.IsOwner()", src);
            Assert.Contains("ItemDrop.SaveToZDO(data, view.GetZDO(), -1)", src);

            var mutate = src.IndexOf("data.m_stack = Mathf.Clamp(stack, 1, maxStack);", StringComparison.Ordinal);
            var save = src.IndexOf("ItemDrop.SaveToZDO(data, view.GetZDO(), -1)", StringComparison.Ordinal);
            Assert.True(mutate < save, "the save has to follow the mutation it is meant to persist");
        }

        /// <summary>
        /// Most things in the game have no upgrade track: a mead, a pile of wood, a trophy. The
        /// first pass clamped every requested quality to 1 for those and then reported "at
        /// quality 1", which is chatter about a property the item does not have, and a write with
        /// nothing behind it. Nothing is set and nothing is said unless the item really upgrades,
        /// and a request that overshoots the ceiling says it was met as far as the item allows
        /// rather than quietly reporting a smaller number than the host typed.
        /// </summary>
        [Theory]
        [InlineData("Commander")]
        [InlineData("SpawnHelper")]
        public void SpawnCommands_SayNothingAboutQualityOnAnItemThatHasNone(string plugin)
        {
            var src = plugin == "Commander" ? Commander : SpawnHelper;
            var code = WithoutComments(src);

            // The guard sits between reading the item's ceiling and setting anything on it, so
            // a one-quality item is never written to at all.
            var ceiling = code.IndexOf("Mathf.Max(1, data.m_shared.m_maxQuality)", StringComparison.Ordinal);
            var guard = code.IndexOf("if (maxQuality > 1)", StringComparison.Ordinal);
            var set = code.IndexOf("item.SetQuality(applied)", StringComparison.Ordinal);

            Assert.True(ceiling > 0, "the item's own quality ceiling is no longer read");
            Assert.True(guard > ceiling, "the guard has to follow the ceiling it reads");
            Assert.True(set > guard, "SetQuality has to sit inside the guard, not before it");

            // Nothing applied means nothing said: the report only names a quality when one was.
            Assert.Contains("if (quality > 0)", code);
            Assert.Contains("(the most this item allows)", code);
        }

        // ---- P10: killall is for hostiles, and a training post is not one ----
        //
        // The rule itself moved into KillAllPlan when the sweep was rewritten, and it is
        // exercised for real in KillAllPlanTests rather than looked for as a literal. What is
        // still only a literal is the naming across from the game's own enum to this plugin's
        // copy of it, because only the plugin compiler ever sees that file.

        /// <summary>
        /// Two plugins answer the same command name and BakaLoader's button promises one thing
        /// for both: "players, pets and allies spared". Commander answers it over its own RCON,
        /// BakaKillAll registers the in-game console command, and BakaKillAll used to spare only
        /// players and training posts. An operator typing baka_killall at the server console lost
        /// every tamed wolf, boar and lox on the server, and every dvergr ally with them. The
        /// cure was one sweep compiled into both DLLs, which is the arrangement this pins.
        /// </summary>
        [Theory]
        [InlineData("Commander")]
        [InlineData("KillAll")]
        public void KillAll_BothPluginsAreBuiltFromTheOneSweep(string plugin)
        {
            var script = BuildScript;
            var entry = script.IndexOf("Dir = \"" + plugin + "\"", StringComparison.Ordinal);
            Assert.True(entry >= 0, "build-plugins.ps1 no longer builds " + plugin);

            // Up to the next plugin entry, so a shared file listed for one is not read as
            // listed for the other.
            var next = script.IndexOf("Dir = \"", entry + 8, StringComparison.Ordinal);
            var block = next > entry ? script.Substring(entry, next - entry) : script.Substring(entry);

            Assert.Contains("BakaKillAllPlan.cs", block);
            Assert.Contains("BakaKillAllSweep.cs", block);
        }

        /// <summary>
        /// Every faction the game has must be named across into the plugin's own enum. A
        /// faction missing from the switch arrives as Unknown, which is spared, so a forgotten
        /// hostile silently survives every sweep.
        /// </summary>
        [Theory]
        [InlineData("Players")]
        [InlineData("AnimalsVeg")]
        [InlineData("ForestMonsters")]
        [InlineData("Undead")]
        [InlineData("Demon")]
        [InlineData("MountainMonsters")]
        [InlineData("SeaMonsters")]
        [InlineData("PlainsMonsters")]
        [InlineData("Boss")]
        [InlineData("MistlandsMonsters")]
        [InlineData("Dverger")]
        [InlineData("PlayerSpawned")]
        [InlineData("TrainingDummy")]
        [InlineData("DeepNorth")]
        public void KillAll_NamesEveryGameFactionAcrossToItsOwn(string faction)
        {
            Assert.Contains("case Character.Faction." + faction + ": return KillAllFaction." + faction + ";",
                KillAllSweepSource);
        }

        /// <summary>
        /// THE 2026-09-19 BUG. Over RCON with four players online and hostiles standing next to
        /// them, baka_killall answered "0 hostiles slain, 162 spared" and a summoned Eikthyr was
        /// still alive. Character.GetAllCharacters() holds only what THIS process instantiated,
        /// and on a dedicated server the creatures around players are instantiated and owned by
        /// those players' clients, so the server's list has none of them in it. Neither the
        /// sweep nor either plugin may go back to asking that question.
        /// </summary>
        [Theory]
        [InlineData("Commander")]
        [InlineData("KillAll")]
        [InlineData("Sweep")]
        public void KillAll_NeverAsksThisProcessWhichCreaturesExist(string file)
        {
            var code = WithoutComments(SourceNamed(file));

            Assert.DoesNotContain("GetAllCharacters", code);
            Assert.DoesNotContain("FindObjectsOfTypeAll<Character>", code);
            Assert.DoesNotContain("FindObjectsOfType<MonoBehaviour>", code);
        }

        /// <summary>
        /// The damage has to reach the creature's OWNER. Character.RPC_Damage returns at once
        /// unless the process running it owns the object, so a hit sent anywhere else is a
        /// count with nothing behind it.
        /// </summary>
        [Fact]
        public void KillAll_SendsTheDamageToWhoeverOwnsTheCreature()
        {
            var code = WithoutComments(KillAllSweepSource);

            Assert.Contains("var owner = zdo.GetOwner();", code);
            Assert.Contains("InvokeRoutedRPC(owner, zdo.m_uid, \"RPC_Damage\", hit)", code);

            // The owner is read before the hit is built and sent, not guessed at afterwards.
            var owner = code.IndexOf("var owner = zdo.GetOwner();", StringComparison.Ordinal);
            var send = code.IndexOf("InvokeRoutedRPC(owner", StringComparison.Ordinal);
            Assert.True(owner >= 0 && send > owner, "the owner has to be resolved before the hit goes out");
        }

        /// <summary>
        /// A creature nobody has loaded has no owner and cannot be damaged by anybody. It is
        /// counted out of reach and LEFT ALONE. Destroying its record instead would drop no
        /// loot, and for a boss it would leave the world's active-boss counter stuck.
        /// </summary>
        [Fact]
        public void KillAll_NeverDestroysAWorldRecord()
        {
            var code = WithoutComments(KillAllSweepSource);

            Assert.DoesNotContain("DestroyZDO", code);
            Assert.DoesNotContain(".Destroy(", code);
            Assert.Contains("unreachable++", code);
        }

        /// <summary>
        /// There is no Character to ask whether a creature was tamed, so the fact is read from
        /// the world record itself, out of the very key Character.Awake reads it from. Losing
        /// this read loses every pet on the server.
        /// </summary>
        [Fact]
        public void KillAll_ReadsTamedFromTheWorldRecord()
        {
            Assert.Contains("zdo.GetBool(ZDOVars.s_tamed, false)", WithoutComments(KillAllSweepSource));
        }

        /// <summary>
        /// The sweep names Valheim's types and BakaLoader has no game assemblies to resolve
        /// them against, yet the file sits under Resources where the app project's source glob
        /// picks it up. The fence is what keeps the app building, and build-plugins.ps1 is the
        /// only thing that lifts it.
        /// </summary>
        [Fact]
        public void KillAll_TheGameHalfIsFencedOffFromTheAppBuild()
        {
            var sweep = KillAllSweepSource;

            Assert.StartsWith("#if VALHEIM_PLUGIN", sweep, StringComparison.Ordinal);
            Assert.EndsWith("#endif", sweep.TrimEnd(), StringComparison.Ordinal);
            Assert.Contains("/define:VALHEIM_PLUGIN", BuildScript);
        }

        /// <summary>
        /// And the pure half must stay pure, because the app project compiles it for real. One
        /// using of UnityEngine here and BakaLoader itself stops building, with the failure
        /// landing on whoever next touches the app rather than on whoever wrote the line.
        /// </summary>
        [Fact]
        public void KillAll_ThePureHalfNamesNoGameType()
        {
            var plan = KillAllPlanSource;

            Assert.DoesNotContain("#if", plan);

            foreach (var forbidden in new[]
                     {
                         "using UnityEngine", "using BepInEx", "using HarmonyLib",
                         "Character.", "ZDO", "ZNet", "HitData", "Vector3", "GameObject",
                     })
                Assert.False(WithoutComments(plan).Contains(forbidden),
                    "BakaKillAllPlan.cs names " + forbidden + ", which the app project cannot compile");
        }

        /// <summary>
        /// The reply's first words are the contract with anything that reads them, and the
        /// three counts are the whole point of the rewrite.
        /// </summary>
        [Fact]
        public void KillAll_TheReplyKeepsItsOpeningWordsAndCarriesTheReachCount()
        {
            var plan = KillAllPlanSource;

            Assert.Contains("\"KillAll complete: \"", plan);
            Assert.Contains("\" out of reach, \"", plan);
            Assert.Contains("\" spared (players, pets & allies)\"", plan);
        }

        /// <summary>A rewritten plugin that ships under the old version number is a plugin nobody replaces.</summary>
        [Theory]
        [InlineData("Commander", "1.4.0")]
        [InlineData("KillAll", "1.6.0")]
        public void KillAll_BothPluginsAnnounceTheirNewVersion(string plugin, string version)
        {
            Assert.Contains("PluginVersion = \"" + version + "\"", SourceNamed(plugin));
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

        // ---- P12: the RCON body is UTF-8 on both ends, and a chunk never cuts a letter ----

        /// <summary>
        /// Commander wrote and read packet bodies as ASCII, which has no room for any letter
        /// past the first 128: a player name in Cyrillic, Japanese or Chinese reached
        /// BakaLoader as question marks, and the name BakaLoader sent back in a spawn, a tp or
        /// a kick was the question marks, which match nobody. A line that puts ASCII back on
        /// either body path is what this catches.
        /// </summary>
        [Fact]
        public void Commander_ReadsAndWritesPacketBodiesAsUtf8()
        {
            var src = WithoutComments(Commander);

            Assert.DoesNotContain("Encoding.ASCII", src);
            Assert.Contains("new UTF8Encoding(false)", src);
            Assert.Contains("BodyEncoding.GetString(payload", src);
            Assert.Contains("BodyEncoding.GetBytes(body", src);
            Assert.Contains("BodyEncoding.GetBytes(response", src);
        }

        /// <summary>
        /// Long answers are split on bytes, because the frame's length field is bytes. A cut
        /// landing inside a multi-byte letter would hand the client half of it at the end of
        /// one packet and half at the start of the next, and both halves read as damage. The
        /// split walks back to the last whole letter, and the bytes are passed on rather than
        /// decoded and re-encoded around the seam.
        /// </summary>
        [Fact]
        public void Commander_SplitsLongAnswersWithoutCuttingALetterInHalf()
        {
            var src = WithoutComments(Commander);

            Assert.Contains("private static int SafeChunkLength(", src);
            // 10xxxxxx is a continuation byte: a chunk must never start with one.
            Assert.Contains("& 0xC0) == 0x80", src);

            var writeResponse = src.IndexOf("private static void WriteResponse(", StringComparison.Ordinal);
            Assert.True(writeResponse > 0, "WriteResponse is gone");
            var body = src.Substring(writeResponse, Math.Min(900, src.Length - writeResponse));

            Assert.Contains("SafeChunkLength(bytes, offset,", body);
            // The old split decoded each chunk back to text before writing it, which is the
            // step that could not survive a seam inside a letter.
            Assert.DoesNotContain("GetString(bytes, offset", body);
        }

        /// <summary>
        /// The UTF-8 fix shipped in Commander 1.3.1, and a fixed plugin that goes out under an
        /// older number is a plugin a host never gets. Pinned as a floor rather than as an
        /// exact string: later work raises this number (the ZDO sweep took it to 1.4.0), and a
        /// test that demands one exact version turns every honest bump into a red build.
        /// </summary>
        [Fact]
        public void Commander_CarriesAtLeastTheVersionTheUtf8FixShippedIn()
        {
            Assert.True(PluginVersionOf(Commander) >= new Version("1.3.1"),
                "Commander is published as " + PluginVersionOf(Commander) + ", below the 1.3.1 the UTF-8 fix shipped in");
        }

        /// <summary>The version a companion plugin publishes to BepInEx, read out of its source.</summary>
        private static Version PluginVersionOf(string src)
        {
            var match = Regex.Match(src, "PluginVersion\\s*=\\s*\"([0-9]+(?:\\.[0-9]+)+)\"");
            Assert.True(match.Success, "the plugin no longer declares a PluginVersion");
            return new Version(match.Groups[1].Value);
        }
    }
}
