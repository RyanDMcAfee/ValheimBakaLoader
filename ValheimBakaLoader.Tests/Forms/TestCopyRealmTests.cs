using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
    /// Make a test copy (issue 20), driven through the real RPC table over a fake base install
    /// in a temp tree, the way ProfileDeleteFilesTests drives servers.create.
    /// <para>
    /// What is proved: a test copy is made by servers.create with testCopyOf, named by any case;
    /// it is never listed whatever the payload says, on create, on every settings save and on
    /// every start; its install is seeded from the SOURCE's BepInEx (plugins, patchers and
    /// config) even while another realm is on screen; its world is the source's, copied under
    /// the new name; servers.list carries the mark; a rename of the source moves the mark with
    /// it; a delete of the source leaves the copy standing and says the source is gone; a source
    /// that is not there is refused before anything is made; and the character step lists and
    /// copies through characters.list and characters.copy, refusing a copy that is already there.
    /// </para>
    /// <para>
    /// Nothing here touches a real install, a real save folder, LocalLow or the registry. The
    /// window is never shown and has no handle.
    /// </para>
    /// </summary>
    public class TestCopyRealmTests : IDisposable
    {
        private const string Home = "Home";
        private const string Source = "Second Sunset";
        private const string Copy = "Second Sunset_bakaT";

        private readonly string Root;
        private readonly string BaseExe;
        private readonly string SaveRoot;
        private readonly string Characters;
        private readonly RoundTrippingUserPrefs UserPrefs = new();
        private readonly BlendWindow Window;
        private readonly Dictionary<string, Func<JObject, Task<object>>> Rpc;

        public TestCopyRealmTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-testcopy-" + Guid.NewGuid().ToString("N"));

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
            Characters = Path.Combine(Root, "client");

            ((IUserPreferencesProvider)UserPrefs).Mutate(prefs =>
            {
                prefs.ServerExePath = BaseExe;
                prefs.SaveDataFolderPath = SaveRoot;
                prefs.LogsFolderPath = MockUserPreferencesProvider.TestLogsFolder;
                prefs.Servers.Add(new ServerPreferences { ProfileName = Home, Name = Home, WorldName = Home, Password = "secret1" });
            });

            var isolation = new InstallIsolationService(new Mock<IApplicationLogger>().Object);
            (Window, Rpc) = BuildWindow(UserPrefs, isolation);
            Window.CharacterSaveRootOverride = Characters;
        }

        public void Dispose()
        {
            try { Window?.Dispose(); } catch { }
            try { DeleteTreeNoFollow(Root); } catch { /* temp folder, best effort */ }
        }

        // ------------------------------------------------------------------ the realm

        /// <summary>
        /// The source: an isolated realm with a mod, a patcher and a config of its own that the
        /// base install does not have, and a world in its own save folder. Left with Home on
        /// screen, so a copy seeded from "the active realm" would get none of it.
        /// </summary>
        private async Task<ServerPreferences> MakeSource(bool listed = true)
        {
            await Call("profiles.get", new { name = Home });
            var source = Assert.IsType<ServerPreferences>(await Call("servers.create", new
            {
                name = Source,
                world = "Sunset",
                password = "sunset99",
                @public = listed,
                isolateInstall = true,
                seedMods = true,
                isolateSaveFolder = true,
            }));
            var bep = Path.Combine(Path.GetDirectoryName(source.ServerExePath)!, "BepInEx");
            Directory.CreateDirectory(Path.Combine(bep, "plugins", "SourceOnly"));
            File.WriteAllText(Path.Combine(bep, "plugins", "SourceOnly", "SourceOnly.dll"), "only the source runs this");
            Directory.CreateDirectory(Path.Combine(bep, "patchers"));
            File.WriteAllText(Path.Combine(bep, "patchers", "SourcePatch.dll"), "a patcher of the source");
            File.WriteAllText(Path.Combine(bep, "config", "source.mod.cfg"), "[Source]\nkey = value");

            var worlds = Path.Combine(source.SaveDataFolderPath, "worlds_local");
            Directory.CreateDirectory(worlds);
            File.WriteAllBytes(Path.Combine(worlds, "Sunset.fwl"), Fwl("Sunset"));
            File.WriteAllBytes(Path.Combine(worlds, "Sunset.db"), DbHeader());

            await Call("profiles.get", new { name = Home });
            Assert.Equal(Home, ActiveProfile());
            return source;
        }

        private async Task<ServerPreferences> MakeTestCopy(string testCopyOf = Source, bool askListed = true)
            => Assert.IsType<ServerPreferences>(await Call("servers.create", new
            {
                name = Copy,
                world = "Sunset_bakaT",
                password = "sunset99",
                // The page sends false. Whatever arrives, a test copy is not listed.
                @public = askListed,
                isolateInstall = true,
                seedMods = true,
                isolateSaveFolder = true,
                testCopyOf,
                copyWorldFrom = new { profile = Source, world = "Sunset" },
            }));

        [Fact]
        public async Task A_test_copy_is_the_source_seeded_by_name_and_never_listed()
        {
            var source = await MakeSource();
            Assert.True(source.Public);

            var copy = await MakeTestCopy(testCopyOf: "second SUNSET");

            Assert.Equal(Source, copy.TestCopyOf);
            Assert.False(copy.Public, "the test copy went up listed because the payload asked");
            Assert.True(copy.IsolatedInstall);
            Assert.Equal("sunset99", copy.Password);
            Assert.NotEqual(source.SaveDataFolderPath, copy.SaveDataFolderPath, StringComparer.OrdinalIgnoreCase);

            // Its install carries what the SOURCE runs, read off the source's install by name
            // while Home was on screen: the mod, the patcher and the config.
            var bep = Path.Combine(Path.GetDirectoryName(copy.ServerExePath)!, "BepInEx");
            Assert.True(File.Exists(Path.Combine(bep, "plugins", "SourceOnly", "SourceOnly.dll")), "the source's own mod is not in the copy");
            Assert.True(File.Exists(Path.Combine(bep, "patchers", "SourcePatch.dll")), "the source's patcher is not in the copy");
            Assert.Equal("[Source]\nkey = value", File.ReadAllText(Path.Combine(bep, "config", "source.mod.cfg")));
            Assert.NotEqual(Path.GetDirectoryName(source.ServerExePath), Path.GetDirectoryName(copy.ServerExePath), StringComparer.OrdinalIgnoreCase);

            // And its world is the source's, under the new name, in its own folder.
            Assert.NotNull(WorldStore.Find(copy.SaveDataFolderPath, "Sunset_bakaT"));
            Assert.NotNull(WorldStore.Find(source.SaveDataFolderPath, "Sunset"));

            // Stored as it was made, and the file key is testCopyOf.
            var stored = ServerPrefs().LoadPreferences(Copy);
            Assert.Equal(Source, stored.TestCopyOf);
            Assert.False(stored.Public);
            Assert.Contains("\"testCopyOf\":\"Second Sunset\"", JsonConvert.SerializeObject(stored.ToFile()));
            Assert.Null(ServerPrefs().LoadPreferences(Source).TestCopyOf);
            Assert.DoesNotContain("testCopyOf", JsonConvert.SerializeObject(ServerPrefs().LoadPreferences(Home).ToFile()).Replace("\"testCopyOf\":null", ""));
        }

        [Fact]
        public async Task The_strip_marks_the_copy_and_a_rename_of_the_source_carries_the_mark()
        {
            await MakeSource();
            await MakeTestCopy();

            var row = ListRow(Copy);
            Assert.Equal(Source, row.Value<string>("testCopyOf"));
            Assert.False(row.Value<bool>("testSourceGone"));
            Assert.Null(ListRow(Source).Value<string>("testCopyOf"));
            Assert.False(ListRow(Source).Value<bool>("testSourceGone"));

            await Call("profiles.rename", new { name = Source, newName = "Third Sunset" });

            Assert.Equal("Third Sunset", ServerPrefs().LoadPreferences(Copy).TestCopyOf);
            Assert.Equal("Third Sunset", ListRow(Copy).Value<string>("testCopyOf"));
            Assert.False(ListRow(Copy).Value<bool>("testSourceGone"));

            // A rename of the copy itself keeps what it is a copy of.
            await Call("profiles.rename", new { name = Copy, newName = "Proving" });
            Assert.Equal("Third Sunset", ServerPrefs().LoadPreferences("Proving").TestCopyOf);
        }

        [Fact]
        public async Task Deleting_the_source_leaves_the_copy_standing_with_its_source_gone()
        {
            await MakeSource();
            var copy = await MakeTestCopy();

            await Call("profiles.delete", new { name = Source, deleteFiles = true });

            Assert.Null(ServerPrefs().LoadPreferences(Source));
            var left = ServerPrefs().LoadPreferences(Copy);
            Assert.NotNull(left);
            Assert.Equal(Source, left.TestCopyOf);
            Assert.True(File.Exists(copy.ServerExePath), "the copy's install went with its source");
            Assert.NotNull(WorldStore.Find(copy.SaveDataFolderPath, "Sunset_bakaT"));
            var row = ListRow(Copy);
            Assert.True(row.Value<bool>("testSourceGone"));
            Assert.Equal(Source, row.Value<string>("testCopyOf"));
        }

        /// <summary>
        /// The settings form has no box for the mark and the listing switch is the form's own:
        /// a save that sends Public on, with the mark missing, keeps the mark and the listing off.
        /// And a start of a hand edited profile is not listed either.
        /// </summary>
        [Fact]
        public async Task A_settings_save_and_a_start_both_keep_a_test_copy_unlisted()
        {
            await MakeSource();
            await MakeTestCopy();

            var cur = JObject.Parse(JsonConvert.SerializeObject(await Call("profiles.get", new { name = Copy })));
            cur["Public"] = true;
            cur.Remove("TestCopyOf");
            foreach (var key in new[] { "EffectiveServerExePath", "ServerExePathSource", "EffectiveSaveDataFolderPath", "SaveDataFolderPathSource" })
                cur.Remove(key);
            await Call("profiles.save", new { name = Copy, prefs = cur });

            var stored = ServerPrefs().LoadPreferences(Copy);
            Assert.Equal(Source, stored.TestCopyOf);
            Assert.False(stored.Public, "a settings save listed the test copy");

            // A realm that is not a test copy saves its listing as sent.
            var src = JObject.Parse(JsonConvert.SerializeObject(await Call("profiles.get", new { name = Source })));
            src["Public"] = false;
            foreach (var key in new[] { "EffectiveServerExePath", "ServerExePathSource", "EffectiveSaveDataFolderPath", "SaveDataFolderPathSource" })
                src.Remove(key);
            await Call("profiles.save", new { name = Source, prefs = src });
            Assert.False(ServerPrefs().LoadPreferences(Source).Public);
            src["Public"] = true;
            await Call("profiles.save", new { name = Source, prefs = src });
            Assert.True(ServerPrefs().LoadPreferences(Source).Public);
            Assert.Null(ServerPrefs().LoadPreferences(Source).TestCopyOf);

            // A hand edit that turned Public on is still started with -public 0.
            stored.Public = true;
            Assert.False(ValheimServerOptions.FromPreferences(stored, new UserPreferences(), null).Public);
            stored.TestCopyOf = null;
            Assert.True(ValheimServerOptions.FromPreferences(stored, new UserPreferences(), null).Public);
        }

        [Fact]
        public async Task A_source_that_is_not_there_is_refused_before_anything_is_made()
        {
            var source = await MakeSource();
            var instancesRoot = Path.GetDirectoryName(Path.GetDirectoryName(source.ServerExePath))!;
            var instances = Directory.GetDirectories(instancesRoot).Length;

            var refusal = await Assert.ThrowsAsync<HostFacingException>(() => MakeTestCopy(testCopyOf: "Nowhere"));

            Assert.Equal("servers.create.testCopySourceMissing", refusal.MessageId);
            Assert.Equal("Nowhere", refusal.Params["source"]);
            Assert.Null(ServerPrefs().LoadPreferences(Copy));
            Assert.Equal(instances, Directory.GetDirectories(instancesRoot).Length);
            Assert.False(Directory.Exists(Path.Combine(SaveRoot, "servers", Copy)));
        }

        /// <summary>A duplicate made the plain way from a test copy is not a test copy.</summary>
        [Fact]
        public async Task A_plain_duplicate_of_a_test_copy_is_a_realm_of_its_own()
        {
            await MakeSource();
            await MakeTestCopy();
            await Call("profiles.get", new { name = Copy });

            var dup = Assert.IsType<ServerPreferences>(await Call("servers.create", new
            {
                name = "Plain",
                password = "plain123",
                @public = true,
                isolateInstall = false,
                isolateSaveFolder = true,
            }));

            Assert.Null(dup.TestCopyOf);
            Assert.True(dup.Public);
        }

        // ------------------------------------------------------------------ the character

        [Fact]
        public async Task The_character_step_lists_and_copies_and_never_writes_over_a_character()
        {
            var local = Path.Combine(Characters, "characters_local");
            Directory.CreateDirectory(local);
            var source = CharacterFileTests.GameLayout.Build("Ragnar");
            File.WriteAllBytes(Path.Combine(local, "ragnar.fch"), source.File);

            var list = JArray.Parse(JsonConvert.SerializeObject(await Call("characters.list", new { })));
            var row = Assert.Single(list);
            Assert.Equal("characters_local", row.Value<string>("folder"));
            Assert.Equal("ragnar.fch", row.Value<string>("file"));
            Assert.Equal("Ragnar", row.Value<string>("name"));
            Assert.Equal("Ragnar_bakaT", row.Value<string>("copyName"));
            Assert.Equal("ragnar_bakat.fch", row.Value<string>("copyFile"));
            Assert.False(row.Value<bool>("copyExists"));

            var copied = JObject.Parse(JsonConvert.SerializeObject(
                await Call("characters.copy", new { folder = "characters_local", file = "ragnar.fch" })));
            Assert.Equal("Ragnar_bakaT", copied.Value<string>("name"));
            Assert.Equal("Ragnar", copied.Value<string>("sourceName"));
            Assert.Equal("ragnar_bakat.fch", copied.Value<string>("file"));
            Assert.Equal(source.File, File.ReadAllBytes(Path.Combine(local, "ragnar.fch")));
            var written = File.ReadAllBytes(Path.Combine(local, "ragnar_bakat.fch"));
            Assert.True(CharacterFile.TryRead(written, out var info, out _));
            Assert.Equal("Ragnar_bakaT", info.Name);

            var listed = JArray.Parse(JsonConvert.SerializeObject(await Call("characters.list", new { })));
            Assert.True(listed.Single(r => r.Value<string>("file") == "ragnar.fch").Value<bool>("copyExists"));

            var refusal = await Assert.ThrowsAsync<HostFacingException>(() =>
                Call("characters.copy", new { folder = "characters_local", file = "ragnar.fch" }));
            Assert.Equal("characters.copy.exists", refusal.MessageId);
            Assert.Equal(written, File.ReadAllBytes(Path.Combine(local, "ragnar_bakat.fch")));
        }

        // ------------------------------------------------------------------ the harness

        private IServerPreferencesProvider ServerPrefs()
            => (IServerPreferencesProvider)typeof(BlendWindow)
                .GetField("ServerPrefsProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);

        private string ActiveProfile()
            => (string)typeof(BlendWindow).GetField("CurrentProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);

        private JObject ListRow(string name)
        {
            var list = JArray.Parse(JsonConvert.SerializeObject(Rpc["servers.list"](new JObject()).GetAwaiter().GetResult()));
            return (JObject)list.Single(r => string.Equals(r.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<object> Call(string method, object args) => await Rpc[method](JObject.FromObject(args));

        private static byte[] Fwl(string worldName)
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
                w.Write(0);                                   // no keys
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
