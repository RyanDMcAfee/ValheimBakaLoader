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
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// Add from link with a zip on this PC (1.3.2, issue 32), driven through the real RPC table:
    /// the real path rule, the real reader, the real installer (behind a spy, so a test can say it
    /// was never reached) over a temporary server tree. The Form is built without a handle, so no
    /// window is shown, and shell.pickFile is never asked for a zip here because that opens one.
    /// <para>
    /// What is proved: the first call asks and writes nothing, the second installs what is on the
    /// disk at that moment; a running server refuses both, for a new mod and an installed one (D2);
    /// nothing is counted (D3); a realm switch, a half quoted or a relative path each say their own
    /// reason; the question says what it replaces, a same version, a step back, a Hexium copy and a
    /// switched off copy; every link shape is still a link; mods.checkOne leaves a zip copy alone;
    /// the Gale mark for that build comes off; the dependency offer is handed back.
    /// </para>
    /// </summary>
    public class BlendWindowLocalZipTests : IDisposable
    {
        private const string Profile = "Zip Test";

        private readonly string Root;
        private readonly string Server;
        private readonly string Plugins;
        private readonly string Downloads;
        private readonly RoundTrippingUserPrefs UserPrefs = new();
        private readonly BlendWindow Window;
        private readonly Dictionary<string, Func<JObject, Task<object>>> Rpc;
        private readonly Mock<IModUpdateService> ModUpdates = new();
        private readonly Mock<IBepInExService> BepInEx = new();
        private readonly Mock<IThunderstoreClient> Thunderstore = new();
        private readonly Mock<IAnalyticsService> Analytics = new();
        private readonly Mock<IHeartbeatService> Heartbeat = new();
        private readonly Mock<ICommandTally> Tally = new();
        private readonly ModUpdateService RealInstaller;
        private readonly HashSet<string> TempBefore;

        public BlendWindowLocalZipTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-zipadd-" + Guid.NewGuid().ToString("N"));
            Server = Path.Combine(Root, "server");
            Plugins = Path.Combine(Server, "BepInEx", "plugins");
            Downloads = Path.Combine(Root, "Zoë's downloads");
            Directory.CreateDirectory(Plugins);
            Directory.CreateDirectory(Downloads);
            var exe = Path.Combine(Server, "valheim_server.exe");
            File.WriteAllText(exe, "MZ");

            ((IUserPreferencesProvider)UserPrefs).Mutate(prefs =>
            {
                prefs.ServerExePath = exe;
                prefs.LogsFolderPath = MockUserPreferencesProvider.TestLogsFolder;
                prefs.Servers.Add(new ServerPreferences { ProfileName = Profile, Name = "Zip", WorldName = "Zip", Password = "secret1" });
            });

            BepInEx.Setup(b => b.Status(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<BepInExProfileInstall>>()))
                .Returns(new BepInExStatus { Installed = true });

            // The installer is the real one; the mock in front only lets a test see whether it was
            // reached. Its Thunderstore client and HTTP provider are strict: a zip asks neither.
            RealInstaller = new ModUpdateService(new Mock<IThunderstoreClient>(MockBehavior.Strict).Object,
                new Mock<IHttpClientProvider>(MockBehavior.Strict).Object, Mock.Of<IApplicationLogger>());
            ModUpdates.Setup(m => m.InstallFromLocalZipAsync(It.IsAny<LocalPackageFacts>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns((LocalPackageFacts f, string copy, string dir) => RealInstaller.InstallFromLocalZipAsync(f, copy, dir));
            ModUpdates.Setup(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()))
                .ReturnsAsync((ThunderstoreModReference r, string _) => new ModInstallResult
                    { Installed = true, Owner = r.Owner, Name = r.Name, Version = r.Version ?? "1.0.0", Source = "thunderstore" });

            (Window, Rpc) = BuildWindow(UserPrefs, new Dictionary<Type, object>
            {
                [typeof(IModUpdateService)] = ModUpdates.Object,
                [typeof(IBepInExService)] = BepInEx.Object,
                [typeof(IThunderstoreClient)] = Thunderstore.Object,
                [typeof(IAnalyticsService)] = Analytics.Object,
                [typeof(IHeartbeatService)] = Heartbeat.Object,
                [typeof(ICommandTally)] = Tally.Object,
            });
            typeof(BlendWindow).GetField("CurrentProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, Profile);
            Heartbeat.Invocations.Clear();
            TempBefore = LocalTemps().ToHashSet();
        }

        public void Dispose()
        {
            try { Sessions().Clear(); } catch { }
            try { Window?.Dispose(); } catch { }
            try { Directory.Delete(Root, recursive: true); } catch { /* temp folder, best effort */ }
        }

        // ------------------------------------------------------------------ plumbing

        private static IEnumerable<string> LocalTemps() => Directory.GetFiles(Path.GetTempPath(), "bakaloader-local-*.zip");

        private string Zip(string fileName, string name = "Aurora", string version = "1.4.0", params string[] deps)
        {
            var path = Path.Combine(Downloads, fileName);
            File.WriteAllBytes(path, LocalPackageReaderTests.Zip(new[]
            {
                ("manifest.json", LocalPackageReaderTests.Manifest(name, version, deps)),
                ("plugins/" + name + ".dll", "the plugin " + version),
            }));
            return path;
        }

        private void InstallHand(string fullName, string version, string source = null, bool off = false)
        {
            var dir = Path.Combine(Plugins, fullName);
            Directory.CreateDirectory(dir);
            var name = fullName.Substring(fullName.IndexOf('-') + 1);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), LocalPackageReaderTests.Manifest(name, version));
            File.WriteAllText(Path.Combine(dir, name + ".dll" + (off ? ModSwitch.OffSuffix : "")), "old " + version);
            if (source != null)
                ModSourceMarkerFile.Write(dir, new ModSourceMarker
                {
                    Schema = ModSourceMarkerFile.CurrentSchema, Writer = "BakaLoader test", Source = source,
                    Owner = fullName.Split('-')[0], Name = name, Version = version, InstalledUtc = DateTime.UtcNow,
                });
        }

        private List<string> ServerTree() =>
            Directory.GetFileSystemEntries(Server, "*", SearchOption.AllDirectories)
                .Select(f => f + "|" + (File.Exists(f) ? File.ReadAllText(f) : "dir")).OrderBy(f => f).ToList();

        private async Task<JObject> Call(string method, object args)
        {
            var json = JsonConvert.SerializeObject(await Rpc[method](JObject.FromObject(args)));
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        private Task<JObject> Ask(string path, string profile = Profile) => Call("mods.addFromUrl", new { url = path, profile });

        private Task<JObject> Accept(string path, string profile = Profile) => Call("mods.addFromUrl", new { url = path, profile, accept = true });

        private void NeverInstalled() =>
            ModUpdates.Verify(m => m.InstallFromLocalZipAsync(It.IsAny<LocalPackageFacts>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        private void NothingCounted()
        {
            Analytics.Verify(a => a.Record(It.IsAny<AnalyticsEvent>()), Times.Never);
            Assert.Empty(Heartbeat.Invocations);
            Tally.Verify(t => t.Count(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        /// <summary>A call the way the window's dispatcher makes it: the press is counted first, then the handler runs.</summary>
        private async Task<JObject> Dispatch(object args)
        {
            var parameters = JObject.FromObject(args);
            typeof(BlendWindow).GetMethod("CountCommand", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Window, new object[] { "mods.addFromUrl", parameters });
            return await Call("mods.addFromUrl", args);
        }

        private void NoTempLeft() => Assert.Empty(LocalTemps().Where(t => !TempBefore.Contains(t)));

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

        private ServerPreferences Stored() => new ServerPreferencesProvider(UserPrefs, new Mock<Serilog.ILogger>().Object).LoadPreferences(Profile);

        // ------------------------------------------------------------------ two steps

        [Fact]
        public async Task The_first_call_asks_and_writes_nothing_and_the_second_installs_without_counting_it()
        {
            var path = Zip("Acme-Aurora-1.4.0.zip");
            var before = ServerTree();

            var asked = await Ask("\"" + path + "\"\r\n");

            Assert.True(asked.Value<bool>("NeedsLocalConsent"));
            Assert.False(asked.Value<bool>("Installed"));
            Assert.Equal("Acme", asked.Value<string>("Owner"));
            Assert.Equal("Aurora", asked.Value<string>("Name"));
            Assert.Equal("1.4.0", asked.Value<string>("Version"));
            Assert.Equal("Acme-Aurora-1.4.0.zip", asked.Value<string>("FileName"));
            Assert.Equal("fileName", asked.Value<string>("OwnerFrom"));
            Assert.True(asked.Value<long>("Bytes") > 0);
            Assert.Equal(JTokenType.Null, asked["wouldReplace"]!.Type);
            Assert.Equal("local", asked.Value<string>("source"));
            Assert.Equal(before, ServerTree());
            NeverInstalled();
            NoTempLeft();

            var done = await Accept(path);

            Assert.True(done.Value<bool>("Installed"), done.Value<string>("Error"));
            Assert.False(done.Value<bool>("Replaced"));
            Assert.Equal("local", done.Value<string>("source"));
            var folder = Path.Combine(Plugins, "Acme-Aurora");
            Assert.True(File.Exists(Path.Combine(folder, "Aurora.dll")));
            Assert.Equal("local", ModSourceMarkerFile.ReadTrustedSource(folder, "1.4.0"));
            NothingCounted();
            NoTempLeft();
        }

        [Fact]
        public async Task Neither_call_of_a_zip_install_is_counted_while_a_link_still_is()
        {
            Assert.True(UserPrefs.LoadPreferences().ShareAnonymousStats);
            var path = Zip("Acme-Aurora-1.4.0.zip");

            Assert.True((await Dispatch(new { url = path, profile = Profile })).Value<bool>("NeedsLocalConsent"));
            Assert.True((await Dispatch(new { url = "\"" + path + "\"", profile = Profile, accept = true })).Value<bool>("Installed"));
            NothingCounted();

            // The control: a link through the same dispatch is one press, as it always was.
            await Dispatch(new { url = "https://thunderstore.io/c/valheim/p/Acme/Aurora/v/1.4.0/" });
            Tally.Verify(t => t.Count("mods.addFromUrl", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task A_cancel_is_the_first_call_alone_and_leaves_no_trace()
        {
            InstallHand("Acme-Aurora", "1.3.0");
            var before = ServerTree();

            var asked = await Ask(Zip("Acme-Aurora-1.4.0.zip"));

            Assert.True(asked.Value<bool>("NeedsLocalConsent"));
            Assert.Equal(before, ServerTree());
            NeverInstalled();
            NothingCounted();
            NoTempLeft();
        }

        [Fact]
        public async Task The_second_call_reads_the_file_again_so_a_swap_or_a_delete_in_between_is_seen()
        {
            var path = Zip("Acme-Aurora-1.4.0.zip");
            Assert.True((await Ask(path)).Value<bool>("NeedsLocalConsent"));

            // Swapped for a newer build: the install is what is on the disk now.
            Zip("Acme-Aurora-1.4.0.zip", version: "1.4.1");
            var swapped = await Accept(path);
            Assert.True(swapped.Value<bool>("Installed"), swapped.Value<string>("Error"));
            Assert.Equal("1.4.1", swapped.Value<string>("Version"));

            // Swapped for something that is not a package: refused, the copy in place untouched.
            var before = ServerTree();
            File.WriteAllText(path, "not a zip");
            var broken = await Accept(path);
            Assert.False(broken.Value<bool>("Installed"));
            Assert.Equal("local.notArchive", broken.Value<string>("Reason"));
            Assert.Equal(before, ServerTree());

            File.Delete(path);
            var gone = await Accept(path);
            Assert.Equal("local.missing", gone.Value<string>("Reason"));
            Assert.Equal(path, gone["ErrorParams"]!.Value<string>("path"));
            NoTempLeft();
        }

        // ------------------------------------------------------------------ D2: the server is up

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task A_running_server_refuses_the_zip_for_a_new_mod_and_an_installed_one(bool installed, bool accept)
        {
            if (installed) InstallHand("Acme-Aurora", "1.3.0");
            PlantRunning(Profile);
            var before = ServerTree();

            var path = Zip("Acme-Aurora-1.4.0.zip");
            var a = accept ? await Accept(path) : await Ask(path);

            Assert.False(a.Value<bool>("Installed"));
            Assert.Null(a["NeedsLocalConsent"]);
            Assert.Equal("local.serverRunning", a.Value<string>("Reason"));
            Assert.Equal(Profile, a["ErrorParams"]!.Value<string>("name"));
            Assert.Equal(before, ServerTree());
            NeverInstalled();
            NoTempLeft();
        }

        // ------------------------------------------------------------------ the other refusals

        [Fact]
        public async Task A_realm_switch_between_paste_and_answer_installs_nothing()
        {
            var path = Zip("Acme-Aurora-1.4.0.zip");
            Assert.Equal("realmChanged", (await Ask(path, "Some Other Realm")).Value<string>("Reason"));
            Assert.Equal("realmChanged", (await Accept(path, "Some Other Realm")).Value<string>("Reason"));
            NeverInstalled();
        }

        [Theory]
        [InlineData("\"C:/Users/me/Downloads/Acme-Aurora-1.4.0.zip", "local.unbalancedQuote")]
        [InlineData("Acme-Aurora-1.4.0.zip", "local.notAbsolute")]
        [InlineData("%USERPROFILE%/Downloads/Acme-Aurora-1.4.0.zip", "local.notAbsolute")]
        public async Task A_half_quoted_or_relative_path_says_so_and_reads_nothing(string pasted, string reason)
        {
            var a = await Accept(pasted);
            Assert.Equal(reason, a.Value<string>("Reason"));
            Assert.NotNull(a["ErrorParams"]!.Value<string>("path"));
            NeverInstalled();
        }

        [Fact]
        public async Task With_no_BepInEx_and_no_leave_to_install_it_the_question_is_not_asked()
        {
            BepInEx.Setup(b => b.Status(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<BepInExProfileInstall>>()))
                .Returns(new BepInExStatus { Installed = false });
            ((IUserPreferencesProvider)UserPrefs).Mutate(prefs => { prefs.BepInExMaintained = false; prefs.BepInExMaintenanceAsked = true; return true; });

            var a = await Ask(Zip("Acme-Aurora-1.4.0.zip"));
            Assert.Equal("noBepInEx", a.Value<string>("Reason"));
            NeverInstalled();
        }

        // ------------------------------------------------------------------ what the question says

        [Theory]
        [InlineData("1.4.0", null, false, true, false)]
        [InlineData("1.5.0", null, false, false, true)]
        [InlineData("1.3.0", "hexium", false, false, false)]
        [InlineData("1.3.0", null, true, false, false)]
        public async Task The_question_says_what_it_replaces(string installedVersion, string source, bool off, bool same, bool downgrade)
        {
            InstallHand("Acme-Aurora", installedVersion, source, off);

            var asked = await Ask(Zip("Aurora.zip"));

            Assert.True(asked.Value<bool>("NeedsLocalConsent"), asked.Value<string>("Error"));
            Assert.Equal("installed", asked.Value<string>("OwnerFrom"));
            var replace = (JObject)asked["wouldReplace"]!;
            Assert.Equal(installedVersion, replace.Value<string>("installedVersion"));
            Assert.Equal(source, replace.Value<string>("source"));
            Assert.Equal(off, replace.Value<bool>("disabled"));
            Assert.Equal(same, replace.Value<bool>("same"));
            Assert.Equal(downgrade, replace.Value<bool>("downgrade"));
        }

        [Fact]
        public async Task A_switched_off_copy_stays_off_after_the_zip_replaces_it()
        {
            InstallHand("Acme-Aurora", "1.3.0", "hexium", off: true);

            var done = await Accept(Zip("Acme-Aurora-1.4.0.zip"));

            Assert.True(done.Value<bool>("Installed"), done.Value<string>("Error"));
            Assert.True(done.Value<bool>("Replaced"));
            var folder = Path.Combine(Plugins, "Acme-Aurora");
            Assert.True(ModSwitch.IsDisabled(folder, Path.Combine(Server, "BepInEx", "patchers", "Acme-Aurora")));
            Assert.Equal("local", ModSourceMarkerFile.ReadTrustedSource(folder, "1.4.0"));
        }

        // ------------------------------------------------------------------ links stay links

        [Theory]
        [InlineData("https://thunderstore.io/c/valheim/p/Acme/Aurora/v/1.4.0/")]
        [InlineData("https://thunderstore.io/package/download/Acme/Aurora/1.4.0/")]
        [InlineData("ror2mm://v1/install/thunderstore.io/Acme/Aurora/1.4.0/")]
        [InlineData("https://hexium.gg/mods/acme/aurora")]
        [InlineData("https://example.com/files/Acme-Aurora-1.4.0.zip")]
        public async Task Every_link_shape_is_still_a_link(string url)
        {
            var a = await Call("mods.addFromUrl", new { url });

            Assert.Null(a["NeedsLocalConsent"]);
            Assert.False((a.Value<string>("Reason") ?? "").StartsWith("local.", StringComparison.Ordinal), a.Value<string>("Reason"));
            Assert.NotEqual("local", a.Value<string>("source"));
            NeverInstalled();
        }

        [Fact]
        public async Task A_Thunderstore_link_still_reaches_the_Thunderstore_install()
        {
            var a = await Call("mods.addFromUrl", new { url = "https://thunderstore.io/c/valheim/p/Acme/Aurora/v/1.4.0/" });
            Assert.True(a.Value<bool>("Installed"), a.Value<string>("Error"));
            ModUpdates.Verify(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()), Times.Once);
            NeverInstalled();
        }

        // ------------------------------------------------------------------ after the install

        [Fact]
        public async Task Checking_a_zip_copy_reports_no_update_and_no_swap()
        {
            Assert.True((await Accept(Zip("Acme-Aurora-1.4.0-beta.1.zip", version: "1.4.0-beta.1"))).Value<bool>("Installed"));
            Thunderstore.Setup(t => t.LookupLiveAsync("Acme", "Aurora")).ReturnsAsync(new ThunderstoreLiveLookup
            {
                Answered = true,
                Package = new ThunderstorePackage { Namespace = "Acme", Name = "Aurora", Latest = new ThunderstorePackageVersion { VersionNumber = "1.4.0" } },
            });

            var c = await Call("mods.checkOne", new { author = "Acme", name = "Aurora" });

            Assert.True(c.Value<bool>("ok"));
            Assert.Equal("1.4.0", c.Value<string>("latestVersion"));
            Assert.False(c.Value<bool>("updateAvailable"));
            Assert.False(c.Value<bool>("thunderstoreNewer"));
            Assert.False(c.Value<bool>("held"));
        }

        [Fact]
        public async Task The_Gale_mark_for_that_build_comes_off()
        {
            ((IUserPreferencesProvider)UserPrefs).Mutate(prefs =>
            {
                var s = prefs.Servers.First(x => x.ProfileName == Profile);
                s.GaleProfileId = "ab12CD";
                s.GaleNotAvailable = new List<string> { "Acme-Aurora@1.4.0", "Acme-Other@2.0.0" };
                return true;
            });

            Assert.True((await Accept(Zip("Acme-Aurora-1.4.0.zip"))).Value<bool>("Installed"));

            Assert.Equal(new[] { "Acme-Other@2.0.0" }, Stored().GaleNotAvailable);
        }

        [Fact]
        public async Task What_the_zip_says_it_needs_and_the_server_lacks_is_handed_back()
        {
            var done = await Accept(Zip("Acme-Aurora-1.4.0.zip", deps: "Acme-Needed-1.0.0"));

            Assert.True(done.Value<bool>("Installed"), done.Value<string>("Error"));
            var missing = (JArray)done["missingDependencies"]!;
            Assert.Contains(missing, m => m.Value<string>("fullName") == "Acme-Needed");
        }

        // ------------------------------------------------------------------ Browse

        [Fact]
        public void The_zip_picker_opens_where_Windows_last_left_it()
        {
            var start = typeof(BlendWindow).GetMethod("PickerStartFolder", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Null(start.Invoke(Window, new object[] { PathCheck.KindZip }));
        }

        [Fact]
        public async Task An_unknown_picker_kind_is_refused_before_any_dialog()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Rpc["shell.pickFile"](JObject.FromObject(new { kind = "zips" })));
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
