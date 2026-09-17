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

        [Fact]
        public void Commander_CarriesTheVersionTheUtf8FixShippedIn()
        {
            Assert.Contains("PluginVersion = \"1.3.1\"", Commander);
        }
    }
}
