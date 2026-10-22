using System;
using System.Collections;
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
using Xunit.Abstractions;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// DELETE WITH FILES, driven through the real RPC handlers (issue 17, Truncated 2026-10-06).
    /// <para>
    /// A host duplicated a realm, deleted the copy with its files switch on, and found the
    /// realm's save folder gone but its install folder still standing under
    /// .bakaloader-instances, with no error in the Saga; a realm made again with the same name
    /// then got "-2" on its install folder. These tests make a realm the way servers.create
    /// makes one, over a fake base install in a temp tree, run the saves the page runs after a
    /// create, delete it with its files, and look at the disk.
    /// </para>
    /// <para>
    /// Nothing here touches a real install, a real save folder, LocalLow or the registry: the
    /// base install, the instances root and the save folders all live in one temp folder that
    /// is removed afterwards. The window is never shown and has no handle, so nothing it posts
    /// goes anywhere.
    /// </para>
    /// </summary>
    public class ProfileDeleteFilesTests : IDisposable
    {
        private readonly ITestOutputHelper Out;
        private readonly string Root;
        private readonly string BaseExe;
        private readonly string SaveRoot;
        private readonly RoundTrippingUserPrefs UserPrefs = new();
        private readonly HookedIsolation Hook;
        private readonly BlendWindow Window;
        private readonly Dictionary<string, Func<JObject, Task<object>>> Rpc;

        public ProfileDeleteFilesTests(ITestOutputHelper output)
        {
            Out = output;
            Root = Path.Combine(Path.GetTempPath(), "vbl-delete-files-" + Guid.NewGuid().ToString("N"));

            // A base install shaped like the real one: the exe and its loose files, a game data
            // folder that gets junctioned, and a BepInEx with a runtime folder, plugins and config.
            var baseDir = Path.Combine(Root, "common", "Valheim dedicated server");
            Directory.CreateDirectory(Path.Combine(baseDir, "valheim_server_Data"));
            File.WriteAllText(Path.Combine(baseDir, "valheim_server_Data", "globalgamemanagers"), "data");
            Directory.CreateDirectory(Path.Combine(baseDir, "BepInEx", "core"));
            File.WriteAllText(Path.Combine(baseDir, "BepInEx", "core", "BepInEx.dll"), "core");
            Directory.CreateDirectory(Path.Combine(baseDir, "BepInEx", "plugins", "SomeMod"));
            File.WriteAllText(Path.Combine(baseDir, "BepInEx", "plugins", "SomeMod", "SomeMod.dll"), "mod");
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
                prefs.Servers.Add(new ServerPreferences
                {
                    ProfileName = Properties.Resources.DefaultServerProfileName,
                    Name = "Home",
                    WorldName = "Home",
                    Password = "secret1",
                });
            });

            Hook = new HookedIsolation(new InstallIsolationService(new Mock<IApplicationLogger>().Object));
            (Window, Rpc) = BuildWindow(UserPrefs, Hook);
        }

        public void Dispose()
        {
            try { DeleteTreeNoFollow(Root); } catch { /* temp folder, best effort */ }
        }

        // ------------------------------------------------------------------ the repro

        /// <summary>
        /// The realm on screen, which is the one a host deletes right after Duplicate. The save
        /// folder guard compared the realm's folder with "the base save folder" read through the
        /// profile on screen, which for the realm on screen IS its own folder, so the folder was
        /// called the base, left alone, and the delete said it had worked.
        /// </summary>
        [Fact]
        public async Task A_duplicated_realm_deleted_with_its_files_while_on_screen_leaves_no_folder_behind()
        {
            var created = await CreateDuplicate("Dup");
            var installDir = Path.GetDirectoryName(created.ServerExePath);
            var saveFolder = created.SaveDataFolderPath;
            Out.WriteLine($"install: {installDir}");
            Out.WriteLine($"saves:   {saveFolder}");
            Assert.True(Directory.Exists(installDir));
            Assert.True(Directory.Exists(saveFolder));

            await RunThePageAfterACreate("Dup");

            var info = JObject.FromObject(await Call("profiles.deleteInfo", new { name = "Dup" }));
            Out.WriteLine("deleteInfo: " + info.ToString(Formatting.None));

            await Call("profiles.delete", new { name = "Dup", deleteFiles = true });
            await RunThePollers();

            ReportLeftovers(installDir, saveFolder);
            Assert.False(Directory.Exists(saveFolder), "the realm's save folder survived the delete");
            Assert.False(Directory.Exists(installDir), "the realm's install folder survived the delete");

            // And a realm made again under the same name gets the plain folders, not "-2".
            var again = await CreateDuplicate("Dup");
            Assert.Equal(installDir, Path.GetDirectoryName(again.ServerExePath), ignoreCase: true);
            Assert.Equal(saveFolder, again.SaveDataFolderPath, ignoreCase: true);
        }

        /// <summary>The same delete pressed while another realm is on screen.</summary>
        [Fact]
        public async Task A_duplicate_of_a_duplicate_deleted_from_another_realm_leaves_no_folder_behind()
        {
            await CreateDuplicate("Src");
            await RunThePageAfterACreate("Src");
            var dup = await CreateFrom("Src", "Dup");
            await RunThePageAfterACreate("Dup");
            await Call("profiles.get", new { name = "Src" });

            var installDir = Path.GetDirectoryName(dup.ServerExePath);
            await Call("profiles.delete", new { name = "Dup", deleteFiles = true });
            await RunThePollers();

            ReportLeftovers(installDir, dup.SaveDataFolderPath);
            Assert.False(Directory.Exists(dup.SaveDataFolderPath));
            Assert.False(Directory.Exists(installDir));
            Assert.Equal(installDir, Path.GetDirectoryName((await CreateFrom("Src", "Dup")).ServerExePath), ignoreCase: true);
        }

        /// <summary>
        /// The page keeps polling the realm on screen while the delete is out. The polls are run
        /// at the moment the files have just gone, which is where a poll that lands during the
        /// delete would run, and none of them may put a folder back.
        /// </summary>
        [Fact]
        public async Task A_poll_that_lands_while_the_files_are_going_puts_no_folder_back()
        {
            var created = await CreateDuplicate("Dup");
            await RunThePageAfterACreate("Dup");
            var installDir = Path.GetDirectoryName(created.ServerExePath);

            Hook.AfterDelete = () => RunThePollers().GetAwaiter().GetResult();
            await Call("profiles.delete", new { name = "Dup", deleteFiles = true });

            ReportLeftovers(installDir, created.SaveDataFolderPath);
            Assert.False(Directory.Exists(installDir));
            Assert.False(Directory.Exists(created.SaveDataFolderPath));
        }

        /// <summary>
        /// The realm's session is let go of BEFORE its files are touched, so nothing it holds
        /// can be inside the folders while they are deleted.
        /// </summary>
        [Fact]
        public async Task The_session_is_disposed_before_the_files_are_deleted()
        {
            var created = await CreateDuplicate("Dup");
            await RunThePageAfterACreate("Dup");
            PlantSession("Dup");

            bool? sessionStillThere = null;
            Hook.BeforeDelete = () => sessionStillThere = Sessions().ContainsKey("Dup");
            await Call("profiles.delete", new { name = "Dup", deleteFiles = true });

            Assert.False(sessionStillThere, "the session was still registered while its install was being deleted");
            Assert.False(Directory.Exists(Path.GetDirectoryName(created.ServerExePath)));
        }

        /// <summary>
        /// A delete that leaves a folder standing, for whatever reason, is a refusal with the
        /// folder named, never a success. The realm stays, so the host can delete it again.
        /// </summary>
        [Fact]
        public async Task A_folder_that_survives_the_delete_is_a_refusal_and_the_realm_is_kept()
        {
            var created = await CreateDuplicate("Dup");
            await RunThePageAfterACreate("Dup");
            var installDir = Path.GetDirectoryName(created.ServerExePath);

            // Something puts a folder back the moment the delete is done with it.
            Hook.AfterDelete = () => Directory.CreateDirectory(Path.Combine(installDir, "BepInEx", "config"));

            var refusal = await Assert.ThrowsAsync<HostFacingException>(
                () => Call("profiles.delete", new { name = "Dup", deleteFiles = true }));
            Out.WriteLine(refusal.Message);

            // The save folder did go, so the refusal says so as well as naming the install.
            Assert.Equal("profiles.delete.filesSurvivedPartly", refusal.MessageId);
            Assert.Contains(installDir, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(created.SaveDataFolderPath, (string)refusal.Params["removed"], ignoreCase: true);
            Assert.NotNull(((IServerPreferencesProvider)new ServerPreferencesProvider(UserPrefs, new Mock<Serilog.ILogger>().Object))
                .LoadPreferences("Dup"));

            // Pressed again once nothing is putting it back, the delete goes through.
            Hook.AfterDelete = null;
            await Call("profiles.delete", new { name = "Dup", deleteFiles = true });
            Assert.False(Directory.Exists(installDir));
        }

        /// <summary>
        /// The install goes and the save folder is held open. The realm is kept, but it is no
        /// longer whole, so the refusal names the folder that was removed as well as the one
        /// still standing. Pressed again once nothing holds the save folder, the delete finishes.
        /// </summary>
        [Fact]
        public async Task A_delete_that_removes_the_install_but_not_the_save_folder_names_both()
        {
            var created = await CreateDuplicate("Dup");
            await RunThePageAfterACreate("Dup");
            var installDir = Path.GetDirectoryName(created.ServerExePath);
            var saveFolder = created.SaveDataFolderPath;
            Assert.True(Directory.Exists(saveFolder));

            // Something holds a file open in the save folder the moment the install has gone.
            FileStream held = null;
            Hook.AfterDelete = () => held = new FileStream(Path.Combine(saveFolder, "held.lock"),
                FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            HostFacingException refusal;
            try
            {
                refusal = await Assert.ThrowsAsync<HostFacingException>(
                    () => Call("profiles.delete", new { name = "Dup", deleteFiles = true }));
            }
            finally { held?.Dispose(); }
            Out.WriteLine(refusal.Message);

            Assert.Equal("profiles.delete.filesSurvivedPartly", refusal.MessageId);
            Assert.False(Directory.Exists(installDir));
            Assert.Equal(installDir, (string)refusal.Params["removed"], ignoreCase: true);
            Assert.Equal(saveFolder, (string)refusal.Params["folders"], ignoreCase: true);
            Assert.Contains(installDir, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(saveFolder, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(((IServerPreferencesProvider)new ServerPreferencesProvider(UserPrefs, new Mock<Serilog.ILogger>().Object))
                .LoadPreferences("Dup"));

            Hook.AfterDelete = null;
            await Call("profiles.delete", new { name = "Dup", deleteFiles = true });
            Assert.False(Directory.Exists(saveFolder));
            Assert.Null(((IServerPreferencesProvider)new ServerPreferencesProvider(UserPrefs, new Mock<Serilog.ILogger>().Object))
                .LoadPreferences("Dup"));
        }

        /// <summary>
        /// Nothing went at all: the install is put back and the save folder is held open. The
        /// refusal names both folders and says nothing was removed, because nothing was.
        /// </summary>
        [Fact]
        public async Task A_delete_that_removes_nothing_names_every_folder_still_standing()
        {
            var created = await CreateDuplicate("Dup");
            await RunThePageAfterACreate("Dup");
            var installDir = Path.GetDirectoryName(created.ServerExePath);
            var saveFolder = created.SaveDataFolderPath;

            FileStream held = null;
            Hook.AfterDelete = () =>
            {
                Directory.CreateDirectory(Path.Combine(installDir, "BepInEx", "config"));
                held = new FileStream(Path.Combine(saveFolder, "held.lock"),
                    FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            };

            HostFacingException refusal;
            try
            {
                refusal = await Assert.ThrowsAsync<HostFacingException>(
                    () => Call("profiles.delete", new { name = "Dup", deleteFiles = true }));
            }
            finally { held?.Dispose(); }
            Out.WriteLine(refusal.Message);

            Assert.Equal("profiles.delete.filesSurvived", refusal.MessageId);
            Assert.False(refusal.Params.ContainsKey("removed"));
            Assert.Contains(installDir, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(saveFolder, refusal.Message, StringComparison.OrdinalIgnoreCase);

            Hook.AfterDelete = null;
            await Call("profiles.delete", new { name = "Dup", deleteFiles = true });
            Assert.False(Directory.Exists(installDir));
            Assert.False(Directory.Exists(saveFolder));
        }

        // ------------------------------------------------------------------ the flow

        /// <summary>Duplicate: the page switches to the source, then founds the copy.</summary>
        private async Task<ServerPreferences> CreateDuplicate(string name)
        {
            await Call("profiles.get", new { name = Properties.Resources.DefaultServerProfileName });
            var created = await Call("servers.create", new
            {
                name,
                password = "secret2",
                isolateInstall = true,
                seedMods = true,
                isolateSaveFolder = true,
            });
            return Assert.IsType<ServerPreferences>(created);
        }

        /// <summary>
        /// What the page does once a create has landed: switch to the new realm, draw the World
        /// hall, and save it the way Save Config does, with the WHOLE profile it was handed back
        /// plus the form's fields (app.js saveCfgBtn), as JSON both ways like the real wire.
        /// </summary>
        private async Task RunThePageAfterACreate(string name)
        {
            var cur = Wire(await Call("profiles.get", new { name }));
            await RunThePollers();
            await Call("worldgen.get", new { world = cur.Value<string>("WorldName") });

            var prefs = (JObject)cur.DeepClone();
            prefs["Name"] = cur["Name"];
            prefs["ServerExePath"] = cur.Value<string>("ServerExePath") ?? "";
            prefs["SaveDataFolderPath"] = cur.Value<string>("SaveDataFolderPath") ?? "";
            prefs.Remove("EffectiveServerExePath");
            prefs.Remove("ServerExePathSource");
            prefs.Remove("EffectiveSaveDataFolderPath");
            prefs.Remove("SaveDataFolderPathSource");
            await Call("profiles.save", new { name, prefs });

            await Call("userprefs.save", new { prefs = new { CheckForUpdates = false } });
            await RunThePollers();
        }

        private async Task RunThePollers()
        {
            foreach (var poll in new[] { "mods.count", "maxplayers.get", "servers.list" })
            {
                try { await Call(poll, new { }); }
                catch (Exception e) { Out.WriteLine($"{poll}: {e.GetType().Name}: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------------ the harness

        private async Task<ServerPreferences> CreateFrom(string source, string name)
        {
            await Call("profiles.get", new { name = source });
            var created = await Call("servers.create", new
            {
                name,
                password = "secret2",
                isolateInstall = true,
                seedMods = true,
                isolateSaveFolder = true,
            });
            return Assert.IsType<ServerPreferences>(created);
        }

        private void ReportLeftovers(params string[] folders)
        {
            foreach (var folder in folders)
            {
                Out.WriteLine($"after delete: {folder} exists={Directory.Exists(folder)}");
                if (!Directory.Exists(folder)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
                    Out.WriteLine("  left: " + entry);
            }
        }

        private System.Collections.Concurrent.ConcurrentDictionary<string, ServerSession> Sessions()
            => (System.Collections.Concurrent.ConcurrentDictionary<string, ServerSession>)typeof(BlendWindow)
                .GetField("Sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);

        /// <summary>
        /// A stopped session for a realm, the shape the window keeps one in once the realm has
        /// been looked at. Its server is never started; only what Dispose touches is given.
        /// </summary>
        private void PlantSession(string profile)
        {
            var server = (ValheimServer)RuntimeHelpers.GetUninitializedObject(typeof(ValheimServer));
            Fill(server, typeof(object), new Dictionary<Type, object>(), interfacesOnly: true);
            Sessions()[profile] = new ServerSession(profile, server);
        }

        private static void Fill(object target, Type stopAt, Dictionary<Type, object> known, bool interfacesOnly)
        {
            for (var type = target.GetType(); type != null && type != stopAt; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (field.GetValue(target) != null) continue;
                    // A server object gets its services and its own collections, never a
                    // process or anything else that would reach outside the test.
                    if (interfacesOnly && !field.FieldType.IsInterface
                        && !(field.FieldType.Namespace ?? "").StartsWith("System.Collections", StringComparison.Ordinal))
                        continue;
                    var value = DefaultFor(field.FieldType, known);
                    if (value != null) field.SetValue(target, value);
                }
            }
        }

        private async Task<object> Call(string method, object args)
        {
            var p = JObject.FromObject(args);
            return await Rpc[method](p);
        }

        /// <summary>An RPC reply as the page holds it: serialised and read back.</summary>
        private static JObject Wire(object reply) => JObject.Parse(JsonConvert.SerializeObject(reply));

        /// <summary>
        /// A BlendWindow with its bridge wired over this test's providers and nothing else: the
        /// Form is constructed (so it has no handle and posts nothing), every interface the
        /// bridge holds is a loose mock, and the RPC table is the real one.
        /// </summary>
        private static (BlendWindow, Dictionary<string, Func<JObject, Task<object>>>) BuildWindow(
            IUserPreferencesProvider userPrefs, IInstallIsolationService isolation)
        {
            var window = (BlendWindow)RuntimeHelpers.GetUninitializedObject(typeof(BlendWindow));
            // No Form under the bridge any more: the headless host stands in for the window.
            window.Ui = new HeadlessHostUi(Serilog.Log.Logger);

            var serilog = new Mock<Serilog.ILogger>().Object;
            var appLogger = new Mock<IApplicationLogger>().Object;
            var serverPrefs = new ServerPreferencesProvider(userPrefs, serilog);
            var worldPrefs = new WorldPreferencesProvider(userPrefs, serilog);

            var known = new Dictionary<Type, object>
            {
                [typeof(IUserPreferencesProvider)] = userPrefs,
                [typeof(IServerPreferencesProvider)] = serverPrefs,
                [typeof(IWorldPreferencesProvider)] = worldPrefs,
                [typeof(IInstallIsolationService)] = isolation,
                [typeof(IApplicationLogger)] = appLogger,
                [typeof(Serilog.ILogger)] = serilog,
            };

            Fill(window, typeof(object), known, interfacesOnly: false);

            typeof(BlendWindow).GetMethod("RegisterRpcHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);

            var table = (Dictionary<string, Func<JObject, Task<object>>>)typeof(BlendWindow)
                .GetField("RpcHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            return (window, table);
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

        /// <summary>
        /// The real isolation service with two moments a test can stand in: just before the
        /// install is deleted and just after.
        /// </summary>
        private sealed class HookedIsolation : IInstallIsolationService
        {
            private readonly IInstallIsolationService Real;
            public Action BeforeDelete;
            public Action AfterDelete;

            public HookedIsolation(IInstallIsolationService real) => Real = real;

            public IsolatedInstallResult ProvisionInstall(string baseExePath, string profileName, bool seedPluginsFromBase,
                string seedSourceBepInExDir = null)
                => Real.ProvisionInstall(baseExePath, profileName, seedPluginsFromBase, seedSourceBepInExDir);

            public bool IsManagedInstall(string installDir) => Real.IsManagedInstall(installDir);

            public void DeleteInstall(string installDir)
            {
                BeforeDelete?.Invoke();
                Real.DeleteInstall(installDir);
                AfterDelete?.Invoke();
            }

            public IReadOnlyList<string> ManagedInstallDirectories(string baseExePath) => Real.ManagedInstallDirectories(baseExePath);

            public bool EnsureSharedBepInEx(string baseExePath, string installDir) => Real.EnsureSharedBepInEx(baseExePath, installDir);
        }

        /// <summary>
        /// userprefs.json kept in memory but round-tripped through its file shape on every load
        /// and save, the way the disk does it, so a field a writer forgets is really lost.
        /// </summary>
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
