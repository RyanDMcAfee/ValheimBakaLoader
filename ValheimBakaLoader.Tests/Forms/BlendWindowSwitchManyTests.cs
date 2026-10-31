using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using ValheimBakaLoader.Tools.Models;
using ValheimBakaLoader.Tests.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// mods.switchMany (1.3.2, issue 33): the ticked rows of the Mods table switched off or on in
    /// one request, driven through the real RPC table over a temporary server tree with the real
    /// scanner (behind a counter) and the real ModSwitch. The Form is built without a handle, so
    /// no window is shown and no server is started; a running server is a session whose status
    /// alone is set.
    /// <para>
    /// What is proved, one fact per test: a server up refuses the whole batch before a rename; a
    /// locked file leaves only its own mod as it was; a dependency can be switched off under a
    /// dependent in either order and the rescan says the dependent needs it; a companion or a
    /// framework name answers notFound and the protected patcher answers protected and has no
    /// tick box; the busy flag is shared both ways with every other mod job; a mixed set comes
    /// back changed, skipped and notFound; a page a realm behind is refused; a server that comes
    /// up part way stops the rest; and fifty of sixty cost one folder scan.
    /// </para>
    /// </summary>
    public class BlendWindowSwitchManyTests : IDisposable
    {
        private const string Profile = "Batch Test";

        private readonly ITestOutputHelper Output;
        private readonly string Root;
        private readonly string Server;
        private readonly string Plugins;
        private readonly RoundTrippingUserPrefs UserPrefs = new();
        private readonly CountingScanner Scanner = new();
        private readonly BlendWindow Window;
        private readonly Dictionary<string, Func<JObject, Task<object>>> Rpc;

        public BlendWindowSwitchManyTests(ITestOutputHelper output)
        {
            Output = output;
            Root = Path.Combine(Path.GetTempPath(), "vbl-switchmany-" + Guid.NewGuid().ToString("N"));
            Server = Path.Combine(Root, "server");
            Plugins = Path.Combine(Server, "BepInEx", "plugins");
            Directory.CreateDirectory(Plugins);
            var exe = Path.Combine(Server, "valheim_server.exe");
            File.WriteAllText(exe, "MZ");

            ((IUserPreferencesProvider)UserPrefs).Mutate(prefs =>
            {
                prefs.ServerExePath = exe;
                prefs.LogsFolderPath = MockUserPreferencesProvider.TestLogsFolder;
                prefs.Servers.Add(new ServerPreferences { ProfileName = Profile, Name = "Batch", WorldName = "Batch", Password = "secret1" });
            });

            (Window, Rpc) = BuildWindow(UserPrefs, new Dictionary<Type, object> { [typeof(IModScanner)] = Scanner });
            typeof(BlendWindow).GetField("CurrentProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, Profile);
        }

        public void Dispose()
        {
            try { Sessions().Clear(); } catch { }
            try { Window?.Dispose(); } catch { }
            try { Directory.Delete(Root, recursive: true); } catch { /* temp folder, best effort */ }
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>The real scanner, counted, with a hook that runs inside the batch's one scan.</summary>
        private sealed class CountingScanner : IModScanner
        {
            private readonly ModScanner Real = new(new Mock<IApplicationLogger>().Object);
            public int Calls;
            public Action DuringFirst;

            public List<InstalledMod> ScanPlugins(string pluginsDirectory)
            {
                Calls++;
                var hook = DuringFirst;
                DuringFirst = null;
                hook?.Invoke();
                return Real.ScanPlugins(pluginsDirectory);
            }
        }

        /// <summary>A mod folder with a manifest and its plugin files, on or off.</summary>
        private string Mod(string fullName, bool off = false, string[] files = null, params string[] deps)
        {
            var dir = Path.Combine(Plugins, fullName);
            Directory.CreateDirectory(dir);
            var name = fullName.Substring(fullName.IndexOf('-') + 1);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonConvert.SerializeObject(new
            {
                name, version_number = "1.0.0", website_url = "", description = "a stand-in", dependencies = deps ?? Array.Empty<string>(),
            }));
            foreach (var file in files ?? new[] { name + ".dll" })
                File.WriteAllText(Path.Combine(dir, file + (off ? ModSwitch.OffSuffix : "")), "MZ " + file);
            return dir;
        }

        private List<string> Tree() =>
            Directory.GetFileSystemEntries(Server, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();

        private async Task<JObject> Call(string method, object args)
        {
            var json = JsonConvert.SerializeObject(await Rpc[method](JObject.FromObject(args)));
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        private Task<JObject> Batch(bool off, params string[] names) =>
            Call("mods.switchMany", new { profile = Profile, fullNames = names, off });

        private static JObject Result(JObject reply, string fullName) =>
            (JObject)reply["results"]!.Single(r => r.Value<string>("fullName") == fullName);

        private System.Collections.Concurrent.ConcurrentDictionary<string, ServerSession> Sessions()
            => (System.Collections.Concurrent.ConcurrentDictionary<string, ServerSession>)typeof(BlendWindow)
                .GetField("Sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);

        /// <summary>A session that reads as up. Its server is never built or started: only the status is set.</summary>
        private void PlantRunning(string profile)
        {
            var server = (ValheimServer)RuntimeHelpers.GetUninitializedObject(typeof(ValheimServer));
            typeof(ValheimServer).GetField("CurrentStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(server, ServerStatus.Running);
            Sessions()[profile] = new ServerSession(profile, server);
        }

        private bool Busy
        {
            get => (bool)typeof(BlendWindow).GetField("_modUpdateInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
            set => typeof(BlendWindow).GetField("_modUpdateInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, value);
        }

        /// <summary>One name from one of the scanner's own lists, so no folder name is written out here.</summary>
        private static string Listed(string field, Func<string, bool> also = null) =>
            ((IEnumerable<string>)typeof(ModScanner).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
                .First(n => also == null || also(n));

        private static bool IsOn(string dir) => Directory.GetFiles(dir, "*.dll").Any();

        // ------------------------------------------------------------------ 1. a server up

        [Fact]
        public async Task Fifty_mods_while_the_server_is_up_are_refused_whole_before_any_rename()
        {
            var names = Enumerable.Range(1, 50).Select(i => "Acme-Aurora" + i).ToArray();
            foreach (var n in names) Mod(n);
            PlantRunning(Profile);
            var before = Tree();

            var r = await Batch(true, names);

            Assert.False(r.Value<bool>("ok"));
            Assert.Equal("serverRunning", r.Value<string>("reason"));
            Assert.Equal(Profile, r["errorParams"]!.Value<string>("name"));
            Assert.Empty((JArray)r["results"]!);
            Assert.Equal(0, r.Value<int>("changed"));
            Assert.Equal(before, Tree());
            Assert.Equal(0, Scanner.Calls);
            Assert.False(Busy);
        }

        // ------------------------------------------------------------------ 2. one locked file

        [Fact]
        public async Task One_locked_file_among_ten_leaves_that_mod_as_it_was_and_switches_the_other_nine()
        {
            var names = Enumerable.Range(1, 9).Select(i => "Acme-Aurora" + i).ToList();
            foreach (var n in names) Mod(n);
            var locked = Mod("Acme-Borealis", files: new[] { "A_first.dll", "Z_second.dll" });
            names.Insert(4, "Acme-Borealis");

            JObject r;
            using (new FileStream(Path.Combine(locked, "Z_second.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
                r = await Batch(true, names.ToArray());

            Assert.True(r.Value<bool>("ok"), r.ToString());
            Assert.Equal(9, r.Value<int>("changed"));
            Assert.Equal(1, r.Value<int>("failed"));
            var bad = Result(r, "Acme-Borealis");
            Assert.False(bad.Value<bool>("ok"));
            Assert.Equal("failed", bad.Value<string>("reason"));
            Assert.False(string.IsNullOrWhiteSpace(bad["errorParams"]!.Value<string>("detail")));
            // Every file of the locked mod is back as it was: the first rename was put back.
            Assert.Equal(new[] { "A_first.dll", "Z_second.dll", "manifest.json" },
                Directory.GetFiles(locked).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal));
            foreach (var n in names.Where(n => n != "Acme-Borealis"))
            {
                Assert.True(Result(r, n).Value<bool>("ok"));
                Assert.False(IsOn(Path.Combine(Plugins, n)), n + " is still on");
            }
            Assert.False(Busy);
        }

        // ------------------------------------------------------------------ 3. a dependency off

        [Fact]
        public async Task A_dependency_switched_off_under_its_dependent_is_allowed_and_the_rescan_says_so_in_either_order()
        {
            Mod("Acme-Lib");
            Mod("Acme-App", deps: "Acme-Lib-1.0.0");
            Mod("Acme-Other");

            foreach (var order in new[] { new[] { "Acme-Lib", "Acme-Other" }, new[] { "Acme-Other", "Acme-Lib" } })
            {
                var r = await Batch(true, order);
                Assert.Equal(2, r.Value<int>("changed"));
                Assert.Equal(0, r.Value<int>("failed"));

                // The rescan the page asks for next, the two steps mods.scan runs for its rows.
                var mods = new ModScanner(new Mock<IApplicationLogger>().Object).ScanPlugins(Plugins);
                typeof(BlendWindow).GetMethod("MarkMissingDependencies", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(Window, new object[] { mods, Plugins });
                var rows = JArray.Parse(JsonConvert.SerializeObject(mods.Select(m => BlendWindow.BuildModDto(m)).ToList()));
                var app = rows.Single(x => x.Value<string>("FullName") == "Acme-App");
                Assert.False(app.Value<bool>("disabled"));
                var need = Assert.Single((JArray)app["missingDependencies"]!);
                Assert.Equal("Acme-Lib", need!.Value<string>("fullName"));
                Assert.Equal("off", need.Value<string>("state"));

                var back = await Batch(false, order);
                Assert.Equal(2, back.Value<int>("changed"));
            }
        }

        // ------------------------------------------------------------------ 4. what is never a row

        [Fact]
        public async Task A_companion_or_framework_name_is_notFound_and_the_protected_patcher_is_refused_and_has_no_box()
        {
            var companion = Listed("CompanionFolderNames");
            var framework = Listed("PluginsFrameworkFolderNames");
            var patcher = Listed("PatcherProtectedNames", n => !ModScanner.IsPluginsFrameworkFolder(n));
            foreach (var folder in new[] { companion, framework })
            {
                Directory.CreateDirectory(Path.Combine(Plugins, folder));
                File.WriteAllText(Path.Combine(Plugins, folder, folder + ".dll"), "MZ");
            }
            Mod(patcher);
            Mod("Acme-Aurora");
            var untouched = new[] { companion, framework, patcher }.SelectMany(f => Directory.GetFiles(Path.Combine(Plugins, f))).ToList();

            var r = await Batch(true, companion, framework, patcher, "Acme-Aurora");

            Assert.Equal("notFound", Result(r, companion).Value<string>("reason"));
            Assert.Equal("notFound", Result(r, framework).Value<string>("reason"));
            Assert.Equal("protected", Result(r, patcher).Value<string>("reason"));
            Assert.True(Result(r, "Acme-Aurora").Value<bool>("ok"));
            Assert.Equal(1, r.Value<int>("changed"));
            Assert.Equal(3, r.Value<int>("failed"));
            Assert.All(untouched, f => Assert.True(File.Exists(f), f + " was renamed"));

            // The scan never lists the first two, and the third reaches the page with no box.
            var rows = new ModScanner(new Mock<IApplicationLogger>().Object).ScanPlugins(Plugins)
                .ToDictionary(m => m.FullName, m => JObject.FromObject(BlendWindow.BuildModDto(m)));
            Assert.DoesNotContain(rows.Keys, k => k == companion || k == framework);
            Assert.False(rows[patcher].Value<bool>("switchable"));
            Assert.True(rows["Acme-Aurora"].Value<bool>("switchable"));
        }

        // ------------------------------------------------------------------ 6. one mod job at a time

        [Fact]
        public async Task A_batch_started_while_another_mod_job_holds_the_folder_is_busy_and_changes_nothing()
        {
            Mod("Acme-Aurora");
            var before = Tree();
            Busy = true;

            var r = await Batch(true, "Acme-Aurora");

            Assert.False(r.Value<bool>("ok"));
            Assert.Equal("busy", r.Value<string>("reason"));
            Assert.Equal(before, Tree());
            Assert.Equal(0, Scanner.Calls);
            Assert.True(Busy, "the batch put down a flag it never held");
        }

        [Fact]
        public async Task Every_other_mod_job_that_asks_while_the_batch_runs_is_told_busy()
        {
            Mod("Acme-Aurora");
            Mod("Acme-Borealis");
            var asked = new Dictionary<string, string>();
            string ReasonOf(Func<JObject, Task<object>> handler, object args)
            {
                try
                {
                    var o = JObject.FromObject(handler(JObject.FromObject(args)).GetAwaiter().GetResult());
                    return o.Value<string>("reason") ?? o.Value<string>("Reason");
                }
                catch (Exception e)
                {
                    return HostFacingException.IdOf(e) ?? e.Message;
                }
            }
            Scanner.DuringFirst = () =>
            {
                Assert.True(Busy, "the batch scans before it holds the flag");
                asked["install"] = ReasonOf(Rpc["mods.addFromUrl"], new { url = "https://thunderstore.io/c/valheim/p/Acme/Aurora/", profile = Profile });
                asked["remove"] = ReasonOf(Rpc["mods.remove"], new { fullName = "Acme-Borealis", profile = Profile });
                asked["switch"] = ReasonOf(Rpc["mods.disable"], new { fullName = "Acme-Borealis", profile = Profile });
                asked["batch"] = ReasonOf(Rpc["mods.switchMany"], new { fullNames = new[] { "Acme-Borealis" }, off = true, profile = Profile });
                asked["gale configs"] = ReasonOf(Rpc["gale.configs.apply"], new { profile = Profile, paths = new[] { "Acme.Aurora.cfg" } });
                asked["test copy"] = ReasonOf(Rpc["testcopy.pullMods"], new { profile = Profile, mods = new[] { "Acme-Borealis" } });
            };

            var r = await Batch(true, "Acme-Aurora");

            Assert.True(r.Value<bool>("ok"), r.ToString());
            Assert.Equal(1, r.Value<int>("changed"));
            Assert.Equal(new Dictionary<string, string>
            {
                ["install"] = "busy", ["remove"] = "mods.remove.busy", ["switch"] = "busy", ["batch"] = "busy",
                ["gale configs"] = "busy", ["test copy"] = "busy",
            }, asked);
            Assert.True(IsOn(Path.Combine(Plugins, "Acme-Borealis")), "a job told busy still changed something");
            Assert.True(Directory.Exists(Path.Combine(Plugins, "Acme-Borealis")));
            Assert.False(Busy);
        }

        // ------------------------------------------------------------------ 10. a realm behind

        [Fact]
        public async Task A_batch_from_a_page_a_realm_behind_is_refused_and_changes_nothing()
        {
            Mod("Acme-Aurora");
            var before = Tree();

            var r = await Call("mods.switchMany", new { profile = "Somewhere Else", fullNames = new[] { "Acme-Aurora" }, off = true });

            Assert.False(r.Value<bool>("ok"));
            Assert.Equal("realmChanged", r.Value<string>("reason"));
            Assert.Equal(before, Tree());
        }

        [Fact]
        public async Task A_server_that_comes_up_part_way_stops_the_rest_with_nothing_of_theirs_touched()
        {
            Mod("Acme-Aurora");
            Mod("Acme-Borealis");
            var before = Tree();
            Scanner.DuringFirst = () => PlantRunning(Profile);

            var r = await Batch(true, "Acme-Aurora", "Acme-Borealis");

            Assert.True(r.Value<bool>("ok"));
            Assert.Equal(0, r.Value<int>("changed"));
            Assert.Equal(2, r.Value<int>("failed"));
            Assert.All(r["results"]!, x => Assert.Equal("serverRunning", x.Value<string>("reason")));
            Assert.Equal(Profile, Result(r, "Acme-Aurora")["errorParams"]!.Value<string>("name"));
            Assert.Equal(before, Tree());
        }

        // ------------------------------------------------------------------ 11. a mixed set

        [Fact]
        public async Task A_mixed_set_comes_back_changed_skipped_and_notFound_and_the_batch_goes_on()
        {
            Mod("Acme-Aurora");
            Mod("Acme-Borealis", off: true);
            Mod("Acme-Cirrus");
            var gone = Mod("Acme-Gone");
            Directory.Delete(gone, recursive: true);

            var r = await Batch(true, "Acme-Aurora", "Acme-Borealis", "Acme-Gone", "Acme-Cirrus", "Acme-Aurora", "  ", null);

            Assert.True(r.Value<bool>("ok"));
            Assert.Equal(4, ((JArray)r["results"]!).Count);
            Assert.Equal(2, r.Value<int>("changed"));
            Assert.Equal(1, r.Value<int>("skipped"));
            Assert.Equal(1, r.Value<int>("failed"));
            var skipped = Result(r, "Acme-Borealis");
            Assert.True(skipped.Value<bool>("skipped"));
            Assert.Equal("alreadyOff", skipped.Value<string>("reason"));
            Assert.Equal("notFound", Result(r, "Acme-Gone").Value<string>("reason"));
            Assert.False(Result(r, "Acme-Gone").Value<bool>("skipped"));
            Assert.False(IsOn(Path.Combine(Plugins, "Acme-Cirrus")), "the batch stopped at the missing folder");
        }

        [Fact]
        public async Task A_call_without_a_direction_or_a_list_is_an_error_not_a_guess()
        {
            Mod("Acme-Aurora");
            await Assert.ThrowsAsync<ArgumentException>(() => Rpc["mods.switchMany"](JObject.FromObject(new { profile = Profile, fullNames = new[] { "Acme-Aurora" } })));
            await Assert.ThrowsAsync<ArgumentException>(() => Rpc["mods.switchMany"](JObject.FromObject(new { profile = Profile, off = "yes", fullNames = new[] { "Acme-Aurora" } })));
            await Assert.ThrowsAsync<ArgumentException>(() => Rpc["mods.switchMany"](JObject.FromObject(new { profile = Profile, off = true })));
            Assert.True(IsOn(Path.Combine(Plugins, "Acme-Aurora")));
        }

        // ------------------------------------------------------------------ 12. the cost

        [Fact]
        public async Task Fifty_of_sixty_in_one_batch_is_one_folder_scan_and_the_time_is_recorded()
        {
            var names = Enumerable.Range(1, 60).Select(i => "Acme-Aurora" + i.ToString("00")).ToArray();
            foreach (var n in names) Mod(n);
            var fifty = names.Take(50).ToArray();

            // Before: one mods.disable per mod, and the folder read each one's rescan makes.
            var clock = Stopwatch.StartNew();
            foreach (var n in fifty)
            {
                var one = await Call("mods.disable", new { profile = Profile, fullName = n });
                Assert.True(one.Value<bool>("ok"), one.ToString());
                Scanner.ScanPlugins(Plugins);
            }
            var single = clock.Elapsed;
            Assert.Equal(100, Scanner.Calls);
            foreach (var n in fifty) Assert.True((await Call("mods.enable", new { profile = Profile, fullName = n })).Value<bool>("ok"));

            // After: one request, and the one rescan the page makes after it.
            Scanner.Calls = 0;
            clock.Restart();
            var r = await Batch(true, fifty);
            Scanner.ScanPlugins(Plugins);
            var batch = clock.Elapsed;

            Assert.Equal(50, r.Value<int>("changed"));
            Assert.Equal(2, Scanner.Calls);
            Assert.All(fifty, n => Assert.False(IsOn(Path.Combine(Plugins, n))));
            Assert.All(names.Skip(50), n => Assert.True(IsOn(Path.Combine(Plugins, n))));
            Output.WriteLine($"SWITCHMANY-TIME 50 of 60: single calls {single.TotalMilliseconds:0} ms, one batch {batch.TotalMilliseconds:0} ms");
        }

        // ------------------------------------------------------------------ the harness (BlendWindowGaleTests' own)

        private static (BlendWindow, Dictionary<string, Func<JObject, Task<object>>>) BuildWindow(
            IUserPreferencesProvider userPrefs, Dictionary<Type, object> extra)
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
                [typeof(IModScanner)] = new ModScanner(appLogger),
                [typeof(IApplicationLogger)] = appLogger,
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
