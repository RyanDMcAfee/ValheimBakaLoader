using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    /// The test copy papercuts of issue 27 (1.3.0, H2), driven through the real RPC table over a
    /// fake base install in a temp tree, the way TestCopyRealmTests makes its realms.
    /// <para>
    /// What is proved: a test copy is named after its source's in-game name made safe for a
    /// folder, and its own in-game name is the same; it keeps the source's in-game name at copy
    /// time beside the profile name, through a rename of the source; a change of case is a
    /// rename like any other and the copies follow it; a test copy is made with an install and a
    /// save folder of its own whatever the switches said, and a settings save that would take
    /// either away is refused by name with nothing written; a copy on its own install reads as
    /// one while a copy sharing an install never does; and the rename and save replies carry the
    /// in-game name the title bar shows.
    /// </para>
    /// <para>
    /// Nothing here touches a real install, a real save folder, LocalLow or the registry. The
    /// window is never shown and has no handle.
    /// </para>
    /// </summary>
    public class TestCopyPapercutTests : IDisposable
    {
        private const string Home = "Home";
        private const string Source = "Second Sunset";

        private static readonly string[] AnswerOnlyKeys =
            { "EffectiveServerExePath", "ServerExePathSource", "EffectiveSaveDataFolderPath", "SaveDataFolderPathSource" };

        private readonly string Root;
        private readonly string BaseExe;
        private readonly string SaveRoot;
        private readonly RoundTrippingUserPrefs UserPrefs = new();
        private readonly BlendWindow Window;
        private readonly Dictionary<string, Func<JObject, Task<object>>> Rpc;

        public TestCopyPapercutTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-tcpaper-" + Guid.NewGuid().ToString("N"));

            var baseDir = Path.Combine(Root, "common", "Valheim dedicated server");
            Directory.CreateDirectory(Path.Combine(baseDir, "valheim_server_Data"));
            File.WriteAllText(Path.Combine(baseDir, "valheim_server_Data", "globalgamemanagers"), "data");
            Directory.CreateDirectory(Path.Combine(baseDir, "BepInEx", "core"));
            File.WriteAllText(Path.Combine(baseDir, "BepInEx", "core", "BepInEx.dll"), "core");
            Directory.CreateDirectory(Path.Combine(baseDir, "BepInEx", "plugins", "BaseMod"));
            File.WriteAllText(Path.Combine(baseDir, "BepInEx", "plugins", "BaseMod", "BaseMod.dll"), "base mod");
            Directory.CreateDirectory(Path.Combine(baseDir, "BepInEx", "config"));
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

            var isolation = new InstallIsolationService(new Mock<IApplicationLogger>().Object);
            (Window, Rpc) = BuildWindow(UserPrefs, isolation);
            Window.CharacterSaveRootOverride = Path.Combine(Root, "client");
        }

        public void Dispose()
        {
            try { Sessions().Clear(); } catch { }
            try { Window?.Dispose(); } catch { }
            try { DeleteTreeNoFollow(Root); } catch { /* temp folder, best effort */ }
        }

        // ------------------------------------------------------------------ the realms

        /// <summary>An isolated source with an in-game name of the host's choosing, left with Home on screen.</summary>
        private async Task<ServerPreferences> MakeSource(string inGameName = null, string profile = Source)
        {
            await Call("profiles.get", new { name = Home });
            Assert.IsType<ServerPreferences>(await Call("servers.create", new
            {
                name = profile, world = "Sunset", password = "sunset99", @public = false,
                isolateInstall = true, seedMods = true, isolateSaveFolder = true,
            }));
            var source = ServerPrefs().LoadPreferences(profile);
            if (inGameName != null)
            {
                source.Name = inGameName;
                ServerPrefs().SavePreferences(source);
            }
            await Call("profiles.get", new { name = Home });
            return ServerPrefs().LoadPreferences(profile);
        }

        private async Task<ServerPreferences> MakeCopy(string name, string of = Source,
            bool isolateInstall = true, bool isolateSaveFolder = true)
            => Assert.IsType<ServerPreferences>(await Call("servers.create", new
            {
                name,
                password = "sunset99",
                @public = false,
                isolateInstall,
                seedMods = true,
                isolateSaveFolder,
                testCopyOf = of,
            }));

        // ------------------------------------------------------------------ 1. the name

        [Fact]
        public async Task A_test_copy_is_named_after_its_sources_in_game_name_made_safe_for_a_folder()
        {
            await MakeSource("Baka's Hall!");

            // The page sent nothing: the host offers the name, built on the in-game name.
            var first = await MakeCopy("");
            Assert.Equal("Bakas Hall_bakaT", first.ProfileName);
            Assert.Equal("Bakas Hall_bakaT", first.Name);
            Assert.Equal(Source, first.TestCopyOf);
            Assert.Equal("Bakas Hall_bakaT", Path.GetFileName(Path.GetDirectoryName(first.ServerExePath)));
            Assert.Equal("Bakas Hall_bakaT", Path.GetFileName(first.SaveDataFolderPath));

            // Taken, in any case: the next number, as the dialog offers it.
            var second = await MakeCopy(null);
            Assert.Equal("Bakas Hall_bakaT2", second.ProfileName);
            Assert.Equal("Bakas Hall_bakaT2", second.Name);

            // What the dialog offered comes through exactly as it was.
            var offered = await MakeCopy("Bakas Hall_bakaT3");
            Assert.Equal("Bakas Hall_bakaT3", offered.ProfileName);

            // A typed name is held to the same rule.
            var typed = await MakeCopy("  Proving #3? ");
            Assert.Equal("Proving 3", typed.ProfileName);
            Assert.Equal("Proving 3", typed.Name);
            Assert.NotNull(ServerPrefs().LoadPreferences("Proving 3"));

            // And a typed name another realm has, in another case, is refused before anything is made.
            var stores = ServerPrefs().LoadPreferences().Count();
            var refusal = await Assert.ThrowsAsync<HostFacingException>(() => MakeCopy("bakas hall_BAKAT"));
            Assert.Equal("servers.create.nameTaken", refusal.MessageId);
            Assert.Equal("bakas hall_BAKAT", refusal.Params["name"]);
            Assert.Equal(stores, ServerPrefs().LoadPreferences().Count());
        }

        /// <summary>
        /// The tester's own case: the base realm keyed Default, known in game as Baka Gaijin, with
        /// no install or save folder of its own. Before 1.3.0 its copy was "Default_bakaT". A realm
        /// with no in-game name at all still gets its profile name.
        /// </summary>
        [Fact]
        public async Task A_copy_of_the_base_realm_named_Default_takes_the_in_game_name_and_a_nameless_one_the_profile_name()
        {
            ServerPrefs().SavePreferences(new ServerPreferences
            {
                ProfileName = "Default", Name = "Baka Gaijin", WorldName = "Gaijin", Password = "gaijin1",
            });

            var copy = await MakeCopy("", of: "Default");
            Assert.Equal("Baka Gaijin_bakaT", copy.ProfileName);
            Assert.Equal("Baka Gaijin_bakaT", copy.Name);
            Assert.Equal("Default", copy.TestCopyOf);
            Assert.Equal("Baka Gaijin", copy.TestCopyOfName);
            Assert.True(copy.IsolatedInstall);
            Assert.True(InstallIsolationService.IsManagedInstallExe(copy.ServerExePath), "the copy of the base realm runs on the base install");
            Assert.False(string.IsNullOrWhiteSpace(copy.SaveDataFolderPath));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(copy.ServerExePath)!, "BepInEx", "plugins", "BaseMod", "BaseMod.dll")),
                "the copy of the base realm was not seeded from the base install");

            var nameless = ServerPrefs().LoadPreferences("Default");
            nameless.Name = null;
            ServerPrefs().SavePreferences(nameless);
            var fallback = await MakeCopy("", of: "Default");
            Assert.Equal("Default_bakaT", fallback.ProfileName);
            Assert.Null(fallback.TestCopyOfName);
        }

        // ------------------------------------------------------------------ 2. the source line

        [Fact]
        public async Task The_copy_keeps_its_sources_in_game_name_beside_the_profile_name_through_a_rename()
        {
            await MakeSource("Baka Gaijin");
            var copy = await MakeCopy("");
            Assert.Equal("Baka Gaijin_bakaT", copy.ProfileName);
            Assert.Equal("Baka Gaijin", copy.TestCopyOfName);

            // On the reply (above), on disk under a key of its own, on the strip and on profiles.get.
            Assert.Contains("\"testCopyOfName\":\"Baka Gaijin\"", JsonConvert.SerializeObject(ServerPrefs().LoadPreferences(copy.ProfileName).ToFile()));
            Assert.Equal("Baka Gaijin", ListRow(copy.ProfileName).Value<string>("testCopyOfName"));
            Assert.Null(ListRow(Source).Value<string>("testCopyOfName"));
            var got = (JObject)await Call("profiles.get", new { name = copy.ProfileName });
            Assert.Equal("Baka Gaijin", got.Value<string>("TestCopyOfName"));
            Assert.Equal(Source, got.Value<string>("TestCopyOf"));

            // The source renamed: the profile name follows, the in-game name at copy time stays.
            await Call("profiles.rename", new { name = Source, newName = "Third Sunset" });
            Assert.Equal("Third Sunset", ListRow(copy.ProfileName).Value<string>("testCopyOf"));
            Assert.Equal("Baka Gaijin", ListRow(copy.ProfileName).Value<string>("testCopyOfName"));

            // And its in-game name changed in Settings: still the name at copy time.
            await SaveForm("Third Sunset", f => f["Name"] = "Gaijin Reborn");
            Assert.Equal("Gaijin Reborn", ServerPrefs().LoadPreferences("Third Sunset").Name);
            Assert.Equal("Baka Gaijin", ServerPrefs().LoadPreferences(copy.ProfileName).TestCopyOfName);

            // A settings save of the copy that drops or changes it keeps what is on disk.
            await SaveForm(copy.ProfileName, f => f.Remove("TestCopyOfName"));
            Assert.Equal("Baka Gaijin", ServerPrefs().LoadPreferences(copy.ProfileName).TestCopyOfName);
            await SaveForm(copy.ProfileName, f => f["TestCopyOfName"] = "Someone Else");
            Assert.Equal("Baka Gaijin", ServerPrefs().LoadPreferences(copy.ProfileName).TestCopyOfName);

            // A plain duplicate of the copy is no test copy and names no source.
            await Call("profiles.get", new { name = copy.ProfileName });
            var dup = Assert.IsType<ServerPreferences>(await Call("servers.create", new
            {
                name = "Plain", password = "plain123", isolateInstall = true, isolateSaveFolder = true,
            }));
            Assert.Null(dup.TestCopyOf);
            Assert.Null(dup.TestCopyOfName);
            Assert.DoesNotContain("testCopyOfName\":\"", JsonConvert.SerializeObject(ServerPrefs().LoadPreferences("Plain").ToFile()));
        }

        // ------------------------------------------------------------------ 3. a change of case

        [Fact]
        public async Task A_change_of_case_is_a_rename_and_the_copies_follow_it()
        {
            await MakeSource();
            var copy = await MakeCopy("");
            Assert.Equal("Second Sunset_bakaT", copy.ProfileName);
            // The source takes its mods from its copy, so the follow has to move as well.
            var source = ServerPrefs().LoadPreferences(Source);
            source.FollowTestCopy = copy.ProfileName;
            ServerPrefs().SavePreferences(source);

            var reply = (JObject)await Call("profiles.rename", new { name = Source, newName = "second sunset" });

            Assert.Equal("second sunset", reply.Value<string>("ProfileName"));
            Assert.Null(ServerPrefs().LoadPreferences(Source));
            var renamed = ServerPrefs().LoadPreferences("second sunset");
            Assert.NotNull(renamed);
            Assert.Single(ServerPrefs().LoadPreferences(), p => string.Equals(p.ProfileName, Source, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(copy.ProfileName, renamed.FollowTestCopy);

            // The copy follows its source to the new case and is not told its source is gone.
            var left = ServerPrefs().LoadPreferences(copy.ProfileName);
            Assert.Equal("second sunset", left.TestCopyOf);
            Assert.False(left.TestSourceGone, "a change of case told the copy its source was gone");
            Assert.False(ListRow(copy.ProfileName).Value<bool>("testSourceGone"));

            // The copy's own change of case: the follow moves with it.
            await Call("profiles.rename", new { name = copy.ProfileName, newName = "SECOND SUNSET_BAKAT" });
            Assert.Equal("SECOND SUNSET_BAKAT", ServerPrefs().LoadPreferences("second sunset").FollowTestCopy);
            Assert.Equal("second sunset", ServerPrefs().LoadPreferences("SECOND SUNSET_BAKAT").TestCopyOf);
        }

        [Fact]
        public async Task A_change_of_case_is_still_refused_while_running_and_over_another_realms_name()
        {
            await MakeSource();

            PlantRunning(Source);
            var running = await Assert.ThrowsAsync<HostFacingException>(() =>
                Call("profiles.rename", new { name = Source, newName = "SECOND SUNSET" }));
            Assert.Equal("profiles.rename.serverRunning", running.MessageId);
            Assert.NotNull(ServerPrefs().LoadPreferences(Source));
            Sessions().Clear();

            // Another realm's name in another case is still taken.
            var taken = await Assert.ThrowsAsync<HostFacingException>(() =>
                Call("profiles.rename", new { name = Source, newName = "HOME" }));
            Assert.Equal("profiles.rename.nameTaken", taken.MessageId);
            Assert.NotNull(ServerPrefs().LoadPreferences(Source));
            Assert.NotNull(ServerPrefs().LoadPreferences(Home));
        }

        // ------------------------------------------------------------------ 4. kept apart

        [Fact]
        public async Task A_test_copy_is_made_on_an_install_and_a_save_folder_of_its_own_whatever_the_switches_say()
        {
            var source = await MakeSource();

            var copy = await MakeCopy("", isolateInstall: false, isolateSaveFolder: false);

            Assert.True(copy.IsolatedInstall);
            Assert.True(InstallIsolationService.IsManagedInstallExe(copy.ServerExePath));
            Assert.NotEqual(Path.GetDirectoryName(source.ServerExePath), Path.GetDirectoryName(copy.ServerExePath), StringComparer.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(copy.SaveDataFolderPath), "the test copy was put in the shared save folder");
            Assert.NotEqual(source.SaveDataFolderPath, copy.SaveDataFolderPath, StringComparer.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(copy.SaveDataFolderPath));

            // A realm that is not a test copy still gets what its switches say.
            await Call("profiles.get", new { name = Home });
            var plain = Assert.IsType<ServerPreferences>(await Call("servers.create", new
            {
                name = "Shared", password = "shared1", isolateInstall = false, isolateSaveFolder = false,
            }));
            Assert.False(plain.IsolatedInstall);
            Assert.Null(plain.SaveDataFolderPath);
        }

        [Fact]
        public async Task A_settings_save_cannot_take_a_test_copy_off_its_own_install_or_save_folder()
        {
            var source = await MakeSource();
            var copy = await MakeCopy("");
            var before = StoredJson(copy.ProfileName);

            async Task Refused(string part, Action<JObject> edit)
            {
                var refusal = await Assert.ThrowsAsync<HostFacingException>(() => SaveForm(copy.ProfileName, edit));
                Assert.Equal("servers.save.testCopyIsolated", refusal.MessageId);
                Assert.Equal(copy.ProfileName, refusal.Params["name"]);
                Assert.Equal(part, refusal.Params["part"]);
                Assert.Equal(before, StoredJson(copy.ProfileName));
            }

            // The save folder box cleared (the shared folder), typed as the shared folder, or
            // pointed at the source's worlds.
            await Refused("saveFolder", f => f["SaveDataFolderPath"] = "");
            await Refused("saveFolder", f => f["SaveDataFolderPath"] = SaveRoot);
            await Refused("saveFolder", f => f["SaveDataFolderPath"] = source.SaveDataFolderPath);
            // The server path cleared (the base install), pointed at the source's install, or the
            // flag switched off under it.
            await Refused("install", f => f["ServerExePath"] = "");
            await Refused("install", f => f["ServerExePath"] = BaseExe);
            await Refused("install", f => f["ServerExePath"] = source.ServerExePath);
            await Refused("install", f => f["IsolatedInstall"] = false);
            await Refused("both", f => { f["ServerExePath"] = ""; f["SaveDataFolderPath"] = ""; });

            // A save that keeps both, even one moving the worlds to a folder of their own, is fine.
            var elsewhere = Path.Combine(Root, "private-worlds");
            var reply = (JObject)await SaveForm(copy.ProfileName, f => { f["SaveDataFolderPath"] = elsewhere; f["Port"] = 2470; });
            Assert.Equal(elsewhere, ServerPrefs().LoadPreferences(copy.ProfileName).SaveDataFolderPath);
            Assert.Equal(2470, ServerPrefs().LoadPreferences(copy.ProfileName).Port);
            Assert.True(reply.Value<bool>("IsolatedInstall"));

            // The same boxes cleared on a realm that is no test copy save as sent.
            await SaveForm(Source, f => { f["ServerExePath"] = ""; f["SaveDataFolderPath"] = ""; });
            var plain = ServerPrefs().LoadPreferences(Source);
            Assert.True(string.IsNullOrEmpty(plain.ServerExePath), "a realm that is no test copy was held to its install");
            Assert.True(string.IsNullOrEmpty(plain.SaveDataFolderPath), "a realm that is no test copy was held to its save folder");
        }

        /// <summary>
        /// The flag follows the install. A copy on its own install whose flag a hand edit turned
        /// off reads as isolated and is saved as isolated. A copy that shares its source's install
        /// (made before 1.3.0 with the install switch off from an isolated source) never does,
        /// because a delete of the copy with its files would then take the source's install.
        /// </summary>
        [Fact]
        public async Task A_copy_on_its_own_install_reads_as_one_and_a_copy_sharing_an_install_never_does()
        {
            var source = await MakeSource();
            var copy = await MakeCopy("");

            var edited = ServerPrefs().LoadPreferences(copy.ProfileName);
            edited.IsolatedInstall = false;
            ServerPrefs().SavePreferences(edited);
            Assert.False(ServerPrefs().LoadPreferences(copy.ProfileName).IsolatedInstall);

            var got = (JObject)await Call("profiles.get", new { name = copy.ProfileName });
            Assert.True(got.Value<bool>("IsolatedInstall"), "a copy on its own install read as sharing one");
            await SaveForm(copy.ProfileName, f => { });
            Assert.True(ServerPrefs().LoadPreferences(copy.ProfileName).IsolatedInstall, "the save did not hold the flag on");

            // The copy made the old way: on its source's install, with its worlds in the shared folder.
            ServerPrefs().SavePreferences(new ServerPreferences
            {
                ProfileName = "Old Copy", Name = "Old Copy", WorldName = "Old", Password = "oldcopy1",
                TestCopyOf = Source, IsolatedInstall = false,
                ServerExePath = source.ServerExePath, SaveDataFolderPath = null,
            });
            var old = (JObject)await Call("profiles.get", new { name = "Old Copy" });
            Assert.False(old.Value<bool>("IsolatedInstall"), "a copy on its source's install was called its own");
            var info = JObject.FromObject(await Call("profiles.deleteInfo", new { name = "Old Copy" }));
            Assert.False(info.Value<bool>("hasIsolatedInstall"), "a delete of the old copy would take the source's install");

            // And a save of it is not refused for what it already was.
            await SaveForm("Old Copy", f => f["Port"] = 2480);
            var saved = ServerPrefs().LoadPreferences("Old Copy");
            Assert.Equal(2480, saved.Port);
            Assert.False(saved.IsolatedInstall);
            Assert.True(File.Exists(source.ServerExePath));
        }

        // ------------------------------------------------------------------ 5. the title bar's name

        [Fact]
        public async Task A_rename_carries_the_in_game_name_only_when_the_two_were_one_and_the_replies_say_it()
        {
            await MakeSource();
            Assert.Equal(Source, ServerPrefs().LoadPreferences(Source).Name);

            // One name: the in-game name goes with the rename, and the reply says both.
            var reply = (JObject)await Call("profiles.rename", new { name = Source, newName = "Third Sunset" });
            Assert.Equal("Third Sunset", reply.Value<string>("ProfileName"));
            Assert.Equal("Third Sunset", reply.Value<string>("Name"));
            Assert.Equal("Third Sunset", ServerPrefs().LoadPreferences("Third Sunset").Name);
            Assert.Equal("Third Sunset", ((JObject)await Call("profiles.get", new { name = "Third Sunset" })).Value<string>("Name"));

            // Two names: Default known in game as Baka Gaijin keeps Baka Gaijin.
            ServerPrefs().SavePreferences(new ServerPreferences { ProfileName = "Default", Name = "Baka Gaijin", Password = "gaijin1" });
            var kept = (JObject)await Call("profiles.rename", new { name = "Default", newName = "Home Base" });
            Assert.Equal("Home Base", kept.Value<string>("ProfileName"));
            Assert.Equal("Baka Gaijin", kept.Value<string>("Name"));
            Assert.Equal("Baka Gaijin", ServerPrefs().LoadPreferences("Home Base").Name);

            // No in-game name at all: the new name becomes it.
            ServerPrefs().SavePreferences(new ServerPreferences { ProfileName = "Nameless", Name = null, Password = "nameless1" });
            var named = (JObject)await Call("profiles.rename", new { name = "Nameless", newName = "Named" });
            Assert.Equal("Named", named.Value<string>("Name"));

            // The settings save answers with the in-game name it stored.
            var saved = (JObject)await SaveForm("Third Sunset", f => f["Name"] = "Sunset Hall");
            Assert.Equal("Sunset Hall", saved.Value<string>("Name"));
            Assert.Equal("Third Sunset", saved.Value<string>("ProfileName"));
        }

        // ------------------------------------------------------------------ the harness

        /// <summary>The Settings form's save: the profile fetched fresh, edited, the answer-only keys dropped, sent back whole.</summary>
        private async Task<object> SaveForm(string name, Action<JObject> edit)
        {
            var cur = JObject.Parse(JsonConvert.SerializeObject(await Call("profiles.get", new { name })));
            foreach (var key in AnswerOnlyKeys) cur.Remove(key);
            edit(cur);
            return await Call("profiles.save", new { name, prefs = cur });
        }

        private string StoredJson(string name)
        {
            var stored = ServerPrefs().LoadPreferences(name).ToFile();
            stored.LastSaved = null;
            return JsonConvert.SerializeObject(stored);
        }

        private IServerPreferencesProvider ServerPrefs()
            => (IServerPreferencesProvider)typeof(BlendWindow)
                .GetField("ServerPrefsProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);

        private System.Collections.Concurrent.ConcurrentDictionary<string, ServerSession> Sessions()
            => (System.Collections.Concurrent.ConcurrentDictionary<string, ServerSession>)typeof(BlendWindow)
                .GetField("Sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);

        /// <summary>A session that reads as up. Its server is never built or started: only the status the guard reads is set.</summary>
        private void PlantRunning(string profile)
        {
            var server = (ValheimServer)RuntimeHelpers.GetUninitializedObject(typeof(ValheimServer));
            typeof(ValheimServer).GetField("CurrentStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(server, ServerStatus.Running);
            Sessions()[profile] = new ServerSession(profile, server);
        }

        private JObject ListRow(string name)
        {
            var list = JArray.Parse(JsonConvert.SerializeObject(Rpc["servers.list"](new JObject()).GetAwaiter().GetResult()));
            return (JObject)list.Single(r => string.Equals(r.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<object> Call(string method, object args) => await Rpc[method](JObject.FromObject(args));

        private static (BlendWindow, Dictionary<string, Func<JObject, Task<object>>>) BuildWindow(
            IUserPreferencesProvider userPrefs, IInstallIsolationService isolation)
        {
            var window = (BlendWindow)RuntimeHelpers.GetUninitializedObject(typeof(BlendWindow));
            typeof(Form).GetConstructor(Type.EmptyTypes)!.Invoke(window, null);

            var serilog = new Mock<Serilog.ILogger>().Object;
            var appLogger = new Mock<IApplicationLogger>().Object;

            var known = new Dictionary<Type, object>
            {
                [typeof(IUserPreferencesProvider)] = userPrefs,
                [typeof(IServerPreferencesProvider)] = new ServerPreferencesProvider(userPrefs, serilog),
                [typeof(IWorldPreferencesProvider)] = new WorldPreferencesProvider(userPrefs, serilog),
                [typeof(IInstallIsolationService)] = isolation,
                [typeof(IApplicationLogger)] = appLogger,
                [typeof(Serilog.ILogger)] = serilog,
            };

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
