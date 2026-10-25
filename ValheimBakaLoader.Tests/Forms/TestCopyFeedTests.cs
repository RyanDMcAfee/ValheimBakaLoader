using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// Merge-back through the real RPC table (1.3.0, H1 item 3): a source realm follows one of
    /// its own test copies with testcopy.follow, the Gale check, configs and apply read the
    /// copy's folders on this PC, and testcopy.pullMods copies the named mod folders back,
    /// refusing while the source runs. Also the Steam Cloud rows of characters.list and the
    /// follow's care across a settings save, a rename and a delete of the copy.
    /// <para>
    /// A fake base install in a temp tree, the way TestCopyRealmTests makes its realms. Gale's
    /// client is a loose mock that must never be asked anything. Nothing here touches a real
    /// install, a real save folder, Steam, LocalLow or the registry, and no window is shown.
    /// </para>
    /// </summary>
    public class TestCopyFeedTests : IDisposable
    {
        private const string Home = "Home";
        private const string Source = "Second Sunset";
        private const string Copy = "Second Sunset_bakaT";

        private readonly string Root;
        private readonly string BaseExe;
        private readonly string SaveRoot;
        private readonly RoundTrippingUserPrefs UserPrefs = new();
        private readonly BlendWindow Window;
        private readonly Dictionary<string, Func<JObject, Task<object>>> Rpc;
        private readonly Mock<IGaleSyncClient> Gale = new(MockBehavior.Strict);

        public TestCopyFeedTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-tcfeedrpc-" + Guid.NewGuid().ToString("N"));

            var baseDir = Path.Combine(Root, "common", "Valheim dedicated server");
            Directory.CreateDirectory(Path.Combine(baseDir, "valheim_server_Data"));
            File.WriteAllText(Path.Combine(baseDir, "valheim_server_Data", "globalgamemanagers"), "data");
            Directory.CreateDirectory(Path.Combine(baseDir, "BepInEx", "core"));
            File.WriteAllText(Path.Combine(baseDir, "BepInEx", "core", "BepInEx.dll"), "core");
            Directory.CreateDirectory(Path.Combine(baseDir, "BepInEx", "plugins", "BaseMod"));
            File.WriteAllText(Path.Combine(baseDir, "BepInEx", "plugins", "BaseMod", "BaseMod.dll"), "base mod");
            Directory.CreateDirectory(Path.Combine(baseDir, "BepInEx", "patchers"));
            Directory.CreateDirectory(Path.Combine(baseDir, "BepInEx", "config"));
            File.WriteAllText(Path.Combine(baseDir, "BepInEx", "config", "BepInEx.cfg"), "[Logging]");
            BaseExe = Path.Combine(baseDir, "valheim_server.exe");
            File.WriteAllText(BaseExe, "MZ");
            File.WriteAllText(Path.Combine(baseDir, "winhttp.dll"), "doorstop");

            SaveRoot = Path.Combine(Root, "saves");
            Directory.CreateDirectory(Path.Combine(SaveRoot, "worlds_local"));

            ((IUserPreferencesProvider)UserPrefs).Mutate(prefs =>
            {
                prefs.ServerExePath = BaseExe;
                prefs.SaveDataFolderPath = SaveRoot;
                prefs.LogsFolderPath = MockUserPreferencesProvider.TestLogsFolder;
                prefs.Servers.Add(new ServerPreferences { ProfileName = Home, Name = Home, WorldName = Home, Password = "secret1" });
            });

            var logger = new Mock<IApplicationLogger>().Object;
            (Window, Rpc) = BuildWindow(UserPrefs, new Dictionary<Type, object>
            {
                [typeof(IInstallIsolationService)] = new InstallIsolationService(logger),
                [typeof(IModScanner)] = new ModScanner(logger),
                [typeof(IGaleSyncClient)] = Gale.Object,
            });
            Window.CharacterSaveRootOverride = Path.Combine(Root, "client");
        }

        public void Dispose()
        {
            try { Sessions().Clear(); } catch { }
            try { Window?.Dispose(); } catch { }
            try { DeleteTreeNoFollow(Root); } catch { /* temp folder, best effort */ }
        }

        // ------------------------------------------------------------------ the realms

        private string SourceBep;
        private string CopyBep;

        /// <summary>The source, isolated and seeded, with a mod of its own; then its test copy, seeded from it.</summary>
        private async Task MakePair()
        {
            await Call("profiles.get", new { name = Home });
            var source = Assert.IsType<ServerPreferences>(await Call("servers.create", new
            {
                name = Source, world = "Sunset", password = "sunset99", @public = false,
                isolateInstall = true, seedMods = true, isolateSaveFolder = true,
            }));
            SourceBep = Path.Combine(Path.GetDirectoryName(source.ServerExePath)!, "BepInEx");
            Mod(SourceBep, "SourceOnly", null, "the source's own build");
            File.WriteAllText(Path.Combine(SourceBep, "config", "source.mod.cfg"), "[Source]\nkey = 1\n");

            await Call("profiles.get", new { name = Home });
            var copy = Assert.IsType<ServerPreferences>(await Call("servers.create", new
            {
                name = Copy, world = "Sunset_bakaT", password = "sunset99", @public = false,
                isolateInstall = true, seedMods = true, isolateSaveFolder = true, testCopyOf = Source,
            }));
            CopyBep = Path.Combine(Path.GetDirectoryName(copy.ServerExePath)!, "BepInEx");
            Assert.NotEqual(SourceBep, CopyBep);
            Assert.True(File.Exists(Path.Combine(CopyBep, "plugins", "SourceOnly", "SourceOnly.dll")), "the copy was not seeded from the source");
        }

        private static void Mod(string bep, string folder, string version, string dllText, string dllName = null, string under = "plugins")
        {
            var dir = Path.Combine(bep, under, folder);
            Directory.CreateDirectory(dir);
            if (version != null)
            {
                var name = folder.Contains('-') ? folder.Substring(folder.IndexOf('-') + 1) : folder;
                File.WriteAllText(Path.Combine(dir, "manifest.json"),
                    "{\"name\":\"" + name + "\",\"version_number\":\"" + version + "\",\"website_url\":\"\",\"description\":\"\",\"dependencies\":[]}");
            }
            var dll = Path.Combine(dir, dllName ?? (folder + ".dll"));
            // A new file every time, never a write into one that may be shared.
            if (File.Exists(dll)) File.Delete(dll);
            File.WriteAllText(dll, dllText);
        }

        /// <summary>The copy, worked on by the host: a new mod, a bumped one, a rebuilt one and a switched off one; and a mod the source has alone.</summary>
        private void WorkOnTheCopy()
        {
            Mod(CopyBep, "Someone-NewMod", "1.0.0", "new mod");
            Mod(CopyBep, "Someone-NewMod", null, "new mod patcher", dllName: "NewModPatch.dll", under: "patchers");
            Mod(SourceBep, "Someone-Bumped", "1.0.0", "bumped old");
            Mod(CopyBep, "Someone-Bumped", "1.1.0", "bumped new");
            Mod(CopyBep, "SourceOnly", null, "rebuilt on the copy");
            Mod(SourceBep, "Someone-Off", "2.0.0", "off mod");
            Mod(CopyBep, "Someone-Off", "2.0.0", "off mod", dllName: "Someone-Off.dll" + ModSwitch.OffSuffix);
            Mod(SourceBep, "Someone-Kept", "1.0.0", "the source's alone");
        }

        private async Task<JObject> Follow(string copy = Copy)
            => await CallJson("testcopy.follow", new { profile = Source, copy });

        // ------------------------------------------------------------------ the follow

        [Fact]
        public async Task The_follow_round_trips_through_the_profile_file_and_a_settings_save_keeps_it()
        {
            await MakePair();

            // Any case reaches the copy; the copy's own spelling is stored.
            var state = await Follow("second sunset_BAKAT");
            Assert.True(state.Value<bool>("following"));
            Assert.Equal("testcopy", state.Value<string>("feed"));
            Assert.Equal(Copy, state.Value<string>("testCopy"));
            Assert.Equal(JTokenType.Null, state["id"]!.Type);
            Assert.Equal(Copy, ServerPrefs().LoadPreferences(Source).FollowTestCopy);
            Assert.Contains("\"followTestCopy\":\"" + Copy + "\"", UserPrefs.Raw);

            // The settings form has no box for it: a save that comes back without it keeps it.
            var cur = JObject.Parse(JsonConvert.SerializeObject(await Call("profiles.get", new { name = Source })));
            cur.Remove("FollowTestCopy");
            cur["FollowTestCopyUnticked"] = new JArray("Someone-Fake");
            foreach (var key in new[] { "EffectiveServerExePath", "ServerExePathSource", "EffectiveSaveDataFolderPath", "SaveDataFolderPathSource" })
                cur.Remove(key);
            await Call("profiles.save", new { name = Source, prefs = cur });
            Assert.Equal(Copy, ServerPrefs().LoadPreferences(Source).FollowTestCopy);
            Assert.Empty(ServerPrefs().LoadPreferences(Source).FollowTestCopyUnticked);

            // A new realm made from this one does not take its follow.
            Assert.Null(BlendWindow.SeedNewRealm(ServerPrefs().LoadPreferences(Source)).FollowTestCopy);

            var off = await Follow(null);
            Assert.False(off.Value<bool>("following"));
            Assert.Equal(JTokenType.Null, off["feed"]!.Type);
            Assert.Null(ServerPrefs().LoadPreferences(Source).FollowTestCopy);
            var onDisk = JObject.Parse(UserPrefs.Raw)["servers"]!.Single(s => s.Value<string>("profileName") == Source);
            Assert.Equal(JTokenType.Null, onDisk["followTestCopy"]!.Type);
            Assert.Equal(JTokenType.Null, onDisk["followTestCopyUnticked"]!.Type);
        }

        [Fact]
        public async Task A_realm_that_is_not_its_test_copy_is_refused_before_anything_is_stored()
        {
            await MakePair();

            var notACopy = await Assert.ThrowsAsync<HostFacingException>(() => Follow(Home));
            Assert.Equal("testcopy.follow.notACopy", notACopy.MessageId);
            var nowhere = await Assert.ThrowsAsync<HostFacingException>(() => Follow("Nowhere"));
            Assert.Equal("testcopy.follow.notACopy", nowhere.MessageId);
            // A copy cannot follow itself, nor the source its own name.
            var self = await Assert.ThrowsAsync<HostFacingException>(() =>
                CallJson("testcopy.follow", new { profile = Copy, copy = Copy }));
            Assert.Equal("testcopy.follow.notACopy", self.MessageId);
            var noRealm = await Assert.ThrowsAsync<HostFacingException>(() =>
                CallJson("testcopy.follow", new { profile = "Nowhere", copy = Copy }));
            Assert.Equal("testcopy.follow.noSuchRealm", noRealm.MessageId);

            Assert.Null(ServerPrefs().LoadPreferences(Source).FollowTestCopy);
        }

        [Fact]
        public async Task A_rename_of_the_copy_moves_the_follow_and_a_delete_of_the_copy_lets_it_go()
        {
            await MakePair();
            await Follow();

            await Call("profiles.rename", new { name = Copy, newName = "Proving Ground" });
            Assert.Equal("Proving Ground", ServerPrefs().LoadPreferences(Source).FollowTestCopy);
            var state = await CallJson("gale.get", new { profile = Source });
            Assert.Equal("Proving Ground", state.Value<string>("testCopy"));

            await Call("profiles.get", new { name = Home });
            await Call("profiles.delete", new { name = "Proving Ground", deleteFiles = false });
            Assert.Null(ServerPrefs().LoadPreferences(Source).FollowTestCopy);
            Assert.False((await CallJson("gale.get", new { profile = Source })).Value<bool>("following"));
        }

        [Fact]
        public async Task Pasting_a_Gale_code_lets_go_of_the_copy_and_letting_go_of_the_copy_keeps_the_code()
        {
            await MakePair();
            var source = ServerPrefs().LoadPreferences(Source);
            source.GaleProfileId = "ab12CD";
            ServerPrefs().SavePreferences(source);

            var following = await Follow();
            Assert.Equal("testcopy", following.Value<string>("feed"));
            Assert.Equal("ab12CD", following.Value<string>("id"));

            var released = await Follow(null);
            Assert.Equal("gale", released.Value<string>("feed"));
            Assert.Equal("ab12CD", ServerPrefs().LoadPreferences(Source).GaleProfileId);

            await Follow();
            var set = await CallJson("gale.set", new { profile = Source, id = "ab12CD" });
            Assert.True(set.Value<bool>("ok"));
            Assert.Null(ServerPrefs().LoadPreferences(Source).FollowTestCopy);
            Assert.Equal("gale", set["state"]!.Value<string>("feed"));
        }

        // ------------------------------------------------------------------ the check

        [Fact]
        public async Task The_Gale_check_reads_the_copy_and_never_asks_Gale()
        {
            await MakePair();
            WorkOnTheCopy();
            await Follow();

            var state = await CallJson("gale.check", new { profile = Source });
            var check = (JObject)state["check"]!;
            Assert.True(check.Value<bool>("ok"), check.ToString());
            var kinds = check["rows"]!.ToDictionary(r => r.Value<string>("name")!, r => r.Value<string>("kind"));

            Assert.Equal("add", kinds["Someone-NewMod"]);
            Assert.Equal("update", kinds["Someone-Bumped"]);
            Assert.Equal("changed", kinds["SourceOnly"]);
            Assert.Equal("changed", kinds["Someone-Off"]);
            Assert.Equal("same", kinds["BaseMod"]);
            Assert.Equal("notInProfile", kinds["Someone-Kept"]);
            Assert.Equal(1, check.Value<int>("addCount"));
            Assert.Equal(3, check.Value<int>("updateCount"));
            Assert.Equal(2, check.Value<int>("changedCount"));
            Assert.Equal(1, check.Value<int>("onlyHereCount"));
            Assert.True(check.Value<bool>("due"));
            Assert.StartsWith(TestCopyGaleFeed.StampPrefix, check.Value<string>("updatedAt"));

            // An untick is the copy's own list, and a dismissal stamps the copy's own approval.
            var unticked = await CallJson("gale.untick", new { profile = Source, unticked = new[] { "SourceOnly" } });
            Assert.Equal("changed", unticked["state"]!["check"]!["rows"]!.Single(r => r.Value<string>("name") == "SourceOnly").Value<string>("untickedFrom"));
            Assert.Equal(new[] { "SourceOnly" }, ServerPrefs().LoadPreferences(Source).FollowTestCopyUnticked);
            Assert.Empty(ServerPrefs().LoadPreferences(Source).GaleUnticked);

            var dismissed = await CallJson("gale.dismiss", new { profile = Source, seq = check.Value<long>("seq") });
            Assert.True(dismissed.Value<bool>("ok"));
            Assert.Equal(check.Value<string>("updatedAt"), ServerPrefs().LoadPreferences(Source).FollowTestCopyLastApproved);
            Assert.Null(ServerPrefs().LoadPreferences(Source).GaleLastUpdatedAt);
            Assert.False(dismissed["state"]!["check"]!.Value<bool>("due"));

            Gale.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task A_copy_that_lost_its_source_mark_or_shares_the_install_is_a_status_line_not_a_comparison()
        {
            await MakePair();
            await Follow();
            var copy = ServerPrefs().LoadPreferences(Copy);
            copy.TestSourceGone = true;
            ServerPrefs().SavePreferences(copy);

            var check = (JObject)(await CallJson("gale.check", new { profile = Source }))["check"]!;
            Assert.False(check.Value<bool>("ok"));
            Assert.Equal(TestCopyGaleFeed.NotACopySentence, check.Value<string>("statusId"));
            Assert.Empty(check["rows"]!);

            copy.TestSourceGone = false;
            copy.ServerExePath = ServerPrefs().LoadPreferences(Source).ServerExePath;
            ServerPrefs().SavePreferences(copy);
            check = (JObject)(await CallJson("gale.check", new { profile = Source }))["check"]!;
            Assert.Equal(TestCopyGaleFeed.SharesInstallSentence, check.Value<string>("statusId"));

            Gale.VerifyNoOtherCalls();
        }

        // ------------------------------------------------------------------ the configs

        [Fact]
        public async Task The_configs_come_from_the_copy_and_the_apply_writes_them_with_a_backup()
        {
            await MakePair();
            File.WriteAllText(Path.Combine(CopyBep, "config", "source.mod.cfg"), "[Source]\nkey = 2\n");
            File.WriteAllText(Path.Combine(CopyBep, "config", "new.mod.cfg"), "[New]\n");
            File.WriteAllText(Path.Combine(CopyBep, "config", "com.baka.commander.cfg"), "[Rcon]\nPort = 25576\n");
            await Follow();
            await Call("profiles.get", new { name = Source });
            await CallJson("gale.check", new { profile = Source });

            var configs = await CallJson("gale.configs", new { profile = Source });
            Assert.Equal(Copy, configs.Value<string>("testCopy"));
            var files = configs["configs"]!["files"]!.ToDictionary(f => f.Value<string>("path")!, f => f.Value<string>("state"));
            Assert.Equal("changed", files["source.mod.cfg"]);
            Assert.Equal("added", files["new.mod.cfg"]);
            Assert.Equal("same", files["BepInEx.cfg"]);
            Assert.False(files.ContainsKey("com.baka.commander.cfg"), "a per realm setting was offered");

            var applied = await CallJson("gale.configs.apply", new { profile = Source, paths = new[] { "new.mod.cfg" } });
            Assert.True(applied.Value<bool>("ok"), applied.ToString());
            Assert.Equal("[New]\n", File.ReadAllText(Path.Combine(SourceBep, "config", "new.mod.cfg")));
            Assert.Equal("[Source]\nkey = 1\n", File.ReadAllText(Path.Combine(SourceBep, "config", "source.mod.cfg")));
            Assert.Equal(new[] { "source.mod.cfg" }, ServerPrefs().LoadPreferences(Source).FollowTestCopyConfigUnticked);
            Assert.Empty(ServerPrefs().LoadPreferences(Source).GaleConfigUnticked);

            applied = await CallJson("gale.configs.apply", new { profile = Source, paths = new[] { "source.mod.cfg" } });
            Assert.True(applied.Value<bool>("ok"), applied.ToString());
            Assert.Equal("[Source]\nkey = 2\n", File.ReadAllText(Path.Combine(SourceBep, "config", "source.mod.cfg")));
            var backup = Directory.GetFiles(Path.Combine(SourceBep, BlendWindow.GaleConfigBackupFolder), "source.mod.cfg", SearchOption.AllDirectories);
            Assert.Equal("[Source]\nkey = 1\n", File.ReadAllText(Assert.Single(backup)));
            Assert.False(File.Exists(Path.Combine(SourceBep, "config", "com.baka.commander.cfg")));

            Gale.VerifyNoOtherCalls();
        }

        // ------------------------------------------------------------------ the pull

        [Fact]
        public async Task The_pull_copies_the_named_mods_moves_the_old_aside_and_touches_nothing_else()
        {
            await MakePair();
            WorkOnTheCopy();
            await Follow();
            await Call("profiles.get", new { name = Source });
            await CallJson("gale.check", new { profile = Source });

            var keptBefore = File.ReadAllText(Path.Combine(SourceBep, "plugins", "Someone-Kept", "Someone-Kept.dll"));
            var pulled = await CallJson("testcopy.pullMods", new { profile = Source, mods = new[] { "SourceOnly", "Someone-NewMod", "Someone-Off", "Someone-Bumped" } });

            Assert.True(pulled.Value<bool>("ok"), pulled.ToString());
            Assert.Equal(new[] { "SourceOnly", "Someone-NewMod", "Someone-Off", "Someone-Bumped" }, pulled["pulled"]!.Values<string>().ToArray());
            Assert.Equal("BepInEx/" + BlendWindow.TestCopyModBackupFolder, pulled.Value<string>("backup"));

            Assert.Equal("rebuilt on the copy", File.ReadAllText(Path.Combine(SourceBep, "plugins", "SourceOnly", "SourceOnly.dll")));
            Assert.Equal("new mod", File.ReadAllText(Path.Combine(SourceBep, "plugins", "Someone-NewMod", "Someone-NewMod.dll")));
            Assert.Equal("new mod patcher", File.ReadAllText(Path.Combine(SourceBep, "patchers", "Someone-NewMod", "NewModPatch.dll")));
            Assert.Contains("1.1.0", File.ReadAllText(Path.Combine(SourceBep, "plugins", "Someone-Bumped", "manifest.json")));
            // The switch travels: the mod is off on the source now, as it was on the copy.
            Assert.True(File.Exists(Path.Combine(SourceBep, "plugins", "Someone-Off", "Someone-Off.dll" + ModSwitch.OffSuffix)));
            Assert.False(File.Exists(Path.Combine(SourceBep, "plugins", "Someone-Off", "Someone-Off.dll")));

            // What the source had of each mod is kept in the backups, not thrown away.
            var backups = Path.Combine(SourceBep, BlendWindow.TestCopyModBackupFolder);
            Assert.Equal("the source's own build", File.ReadAllText(
                Assert.Single(Directory.GetFiles(Path.Combine(backups, "SourceOnly"), "SourceOnly.dll", SearchOption.AllDirectories))));
            Assert.Equal("bumped old", File.ReadAllText(
                Assert.Single(Directory.GetFiles(Path.Combine(backups, "Someone-Bumped"), "Someone-Bumped.dll", SearchOption.AllDirectories))));

            // Mods not named are exactly as they were, and nothing was left half way.
            Assert.Equal(keptBefore, File.ReadAllText(Path.Combine(SourceBep, "plugins", "Someone-Kept", "Someone-Kept.dll")));
            Assert.True(File.Exists(Path.Combine(SourceBep, "plugins", "BaseMod", "BaseMod.dll")));
            Assert.Empty(Directory.GetDirectories(SourceBep, ".bakaloader-pull-*"));
            // The copy is read only to the pull.
            Assert.Equal("rebuilt on the copy", File.ReadAllText(Path.Combine(CopyBep, "plugins", "SourceOnly", "SourceOnly.dll")));

            // The state that came back was read off the disk after the pull.
            var check = (JObject)pulled["state"]!["check"]!;
            var kinds = check["rows"]!.ToDictionary(r => r.Value<string>("name")!, r => r.Value<string>("kind"));
            Assert.All(new[] { "SourceOnly", "Someone-NewMod", "Someone-Off", "Someone-Bumped" }, n => Assert.Equal("same", kinds[n]));
            Assert.Equal("notInProfile", kinds["Someone-Kept"]);
            Assert.Equal(0, check.Value<int>("addCount") + check.Value<int>("updateCount"));

            Gale.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task The_pull_refuses_while_the_source_runs_and_on_every_other_hold_before_writing()
        {
            await MakePair();
            WorkOnTheCopy();

            // Not following anything yet.
            await Call("profiles.get", new { name = Source });
            var refused = await CallJson("testcopy.pullMods", new { profile = Source, mods = new[] { "SourceOnly" } });
            Assert.Equal("notFollowing", refused.Value<string>("Reason"));

            await Follow();

            PlantRunning(Source);
            refused = await CallJson("testcopy.pullMods", new { profile = Source, mods = new[] { "SourceOnly" } });
            Assert.False(refused.Value<bool>("ok"));
            Assert.Equal("serverRunning", refused.Value<string>("Reason"));
            Assert.Equal(Source, refused["ErrorParams"]!.Value<string>("name"));
            Sessions().Clear();

            refused = await CallJson("testcopy.pullMods", new { profile = Source, mods = new[] { "SourceOnly", "Nope" } });
            Assert.Equal("notInCopy", refused.Value<string>("Reason"));
            Assert.Equal("Nope", refused["ErrorParams"]!.Value<string>("mod"));

            await Call("profiles.get", new { name = Home });
            refused = await CallJson("testcopy.pullMods", new { profile = Source, mods = new[] { "SourceOnly" } });
            Assert.Equal("realmChanged", refused.Value<string>("Reason"));

            // None of it wrote a thing.
            Assert.Equal("the source's own build", File.ReadAllText(Path.Combine(SourceBep, "plugins", "SourceOnly", "SourceOnly.dll")));
            Assert.False(Directory.Exists(Path.Combine(SourceBep, BlendWindow.TestCopyModBackupFolder)));

            // A copy whose source mark was lost is refused by name too.
            await Call("profiles.get", new { name = Source });
            var copy = ServerPrefs().LoadPreferences(Copy);
            copy.TestSourceGone = true;
            ServerPrefs().SavePreferences(copy);
            refused = await CallJson("testcopy.pullMods", new { profile = Source, mods = new[] { "SourceOnly" } });
            Assert.Equal("notACopy", refused.Value<string>("Reason"));

            // An empty list is nothing to do, and says so.
            copy.TestSourceGone = false;
            ServerPrefs().SavePreferences(copy);
            var nothing = await CallJson("testcopy.pullMods", new { profile = Source, mods = Array.Empty<string>() });
            Assert.True(nothing.Value<bool>("ok"));
            Assert.Empty(nothing["pulled"]!);
        }

        // ------------------------------------------------------------------ the character rows

        [Fact]
        public async Task The_character_list_carries_where_each_one_is_kept_and_a_cloud_copy_lands_locally()
        {
            var steam = Path.Combine(Root, "Steam");
            Window.CharacterSteamRootOverride = steam;
            var cloud = Path.Combine(steam, "userdata", "44957764", "892970", "remote", "characters");
            Directory.CreateDirectory(cloud);
            File.WriteAllBytes(Path.Combine(cloud, "astrid.fch"), CharacterFileTests.GameLayout.Build("Astrid").File);
            var local = Path.Combine(Root, "client", "characters_local");
            Directory.CreateDirectory(local);
            File.WriteAllBytes(Path.Combine(local, "ragnar.fch"), CharacterFileTests.GameLayout.Build("Ragnar").File);

            var list = JArray.Parse(JsonConvert.SerializeObject(await Call("characters.list", new { })));
            Assert.Equal(new[] { "copyExists", "copyFile", "copyName", "file", "folder", "name", "problem", "source" },
                ((JObject)list[0]).Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            var astrid = list.Single(r => r.Value<string>("name") == "Astrid");
            Assert.Equal("cloud", astrid.Value<string>("source"));
            Assert.Equal("cloud:44957764", astrid.Value<string>("folder"));
            Assert.Equal("local", list.Single(r => r.Value<string>("name") == "Ragnar").Value<string>("source"));

            var copied = JObject.Parse(JsonConvert.SerializeObject(
                await Call("characters.copy", new { folder = "cloud:44957764", file = "astrid.fch" })));
            Assert.Equal("characters_local", copied.Value<string>("folder"));
            Assert.Equal("local", copied.Value<string>("source"));
            Assert.Equal("astrid_bakat.fch", copied.Value<string>("file"));
            Assert.True(File.Exists(Path.Combine(local, "astrid_bakat.fch")));
            Assert.Equal(new[] { "astrid.fch" }, Directory.GetFiles(cloud).Select(Path.GetFileName).ToArray());
        }

        [Fact]
        public async Task A_test_that_points_the_save_folder_somewhere_reads_no_Steam_folder_at_all()
        {
            // The harness set only the save folder: the real PC's Steam Cloud characters must
            // never be listed, and a cloud folder is not a place a copy can come from.
            Assert.Empty(JArray.Parse(JsonConvert.SerializeObject(await Call("characters.list", new { }))));
            var refusal = await Assert.ThrowsAsync<HostFacingException>(() =>
                Call("characters.copy", new { folder = "cloud:44957764", file = "astrid.fch" }));
            Assert.Equal("characters.copy.badRef", refusal.MessageId);
        }

        // ------------------------------------------------------------------ the harness

        private IServerPreferencesProvider ServerPrefs()
            => (IServerPreferencesProvider)typeof(BlendWindow)
                .GetField("ServerPrefsProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);

        private System.Collections.Concurrent.ConcurrentDictionary<string, ServerSession> Sessions()
            => (System.Collections.Concurrent.ConcurrentDictionary<string, ServerSession>)typeof(BlendWindow)
                .GetField("Sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);

        /// <summary>A session that reads as up. Its server is never built or started: only the status the hold reads is set.</summary>
        private void PlantRunning(string profile)
        {
            var server = (ValheimServer)RuntimeHelpers.GetUninitializedObject(typeof(ValheimServer));
            typeof(ValheimServer).GetField("CurrentStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(server, ServerStatus.Running);
            Sessions()[profile] = new ServerSession(profile, server);
        }

        private async Task<object> Call(string method, object args) => await Rpc[method](JObject.FromObject(args));

        private async Task<JObject> CallJson(string method, object args)
        {
            var json = JsonConvert.SerializeObject(await Call(method, args));
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        private static (BlendWindow, Dictionary<string, Func<JObject, Task<object>>>) BuildWindow(
            IUserPreferencesProvider userPrefs, Dictionary<Type, object> extra)
        {
            var window = (BlendWindow)RuntimeHelpers.GetUninitializedObject(typeof(BlendWindow));
            typeof(Form).GetConstructor(Type.EmptyTypes)!.Invoke(window, null);

            var serilog = new Mock<Serilog.ILogger>().Object;
            var known = new Dictionary<Type, object>
            {
                [typeof(IUserPreferencesProvider)] = userPrefs,
                [typeof(IServerPreferencesProvider)] = new ServerPreferencesProvider(userPrefs, serilog),
                [typeof(IWorldPreferencesProvider)] = new WorldPreferencesProvider(userPrefs, serilog),
                [typeof(IApplicationLogger)] = new Mock<IApplicationLogger>().Object,
                [typeof(Serilog.ILogger)] = serilog,
            };
            foreach (var pair in extra) known[pair.Key] = pair.Value;

            Fill(window, typeof(Form), known);

            typeof(BlendWindow).GetMethod("RegisterRpcHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);

            var table = (Dictionary<string, Func<JObject, Task<object>>>)typeof(BlendWindow)
                .GetField("RpcHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            return (window, table);
        }

        private static void Fill(object target, Type stopAt, Dictionary<Type, object> known)
        {
            for (var type = target.GetType(); type != null && type != stopAt; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (field.GetValue(target) != null) continue;
                    var value = DefaultFor(field.FieldType, known);
                    if (value != null) field.SetValue(target, value);
                }
            }
        }

        private static object DefaultFor(Type t, Dictionary<Type, object> known)
        {
            if (known.TryGetValue(t, out var k)) return k;
            if (t.IsValueType || t == typeof(string)) return null;

            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                if (def == typeof(System.Collections.Concurrent.ConcurrentDictionary<,>)
                    && t.GetGenericArguments()[0] == typeof(string))
                    return Activator.CreateInstance(t, StringComparer.OrdinalIgnoreCase);
                if (def == typeof(Dictionary<,>) && t.GetGenericArguments()[0] == typeof(string))
                    return Activator.CreateInstance(t, StringComparer.OrdinalIgnoreCase);
                if (def == typeof(HashSet<>) && t.GetGenericArguments()[0] == typeof(string))
                    return Activator.CreateInstance(t, StringComparer.OrdinalIgnoreCase);
            }

            if (t == typeof(object)) return new object();

            if (t.IsInterface)
            {
                var mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(t))!;
                mock.DefaultValue = DefaultValue.Empty;
                return mock.Object;
            }

            if (!t.IsAbstract && t.Namespace != null && t.Namespace.StartsWith("System", StringComparison.Ordinal)
                && t.GetConstructor(Type.EmptyTypes) != null)
                return Activator.CreateInstance(t);

            return null;
        }

        /// <summary>Removes the temp tree without ever following a junction out of it.</summary>
        private static void DeleteTreeNoFollow(string dir)
        {
            var di = new DirectoryInfo(dir);
            if (!di.Exists) return;
            if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) { Directory.Delete(dir); return; }
            foreach (var sub in di.GetDirectories()) DeleteTreeNoFollow(sub.FullName);
            foreach (var file in di.GetFiles()) { file.Attributes = FileAttributes.Normal; file.Delete(); }
            Directory.Delete(dir);
        }

        /// <summary>userprefs.json kept in memory but round-tripped through its file shape, the way the disk does it.</summary>
        private sealed class RoundTrippingUserPrefs : IUserPreferencesProvider
        {
            private string Stored = JsonConvert.SerializeObject(UserPreferences.GetDefault().ToFile());

            public string Raw => Stored;

            public event EventHandler<UserPreferences> PreferencesSaved;

            public UserPreferences LoadPreferences()
                => UserPreferences.FromFile(JsonConvert.DeserializeObject<UserPreferencesFile>(Stored));

            public void SavePreferences(UserPreferences preferences)
            {
                Stored = JsonConvert.SerializeObject(preferences.ToFile());
                PreferencesSaved?.Invoke(this, preferences);
            }
        }
    }
}
