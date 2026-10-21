using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
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
    /// The Gale RPCs (the seven of the follow, and from 1.2.16 the configs list and its apply and
    /// the server-only list's FullName and off state), and the two holds the dialog's install puts on mods.addFromUrl, driven through the real RPC table over a fake HTTP handler. Nothing
    /// here reaches gale.kesomannen.com, and no window is shown: the Form is constructed without
    /// a handle, the way ProfileDeleteFilesTests builds one.
    /// <para>
    /// What is proved: the row is due only when there is something to install AND the profile's
    /// updatedAt moved since the last approval or dismissal; a 400, a timeout and a realm with no
    /// id never make it due; a realm with no id never sends a request; the trip runs off the
    /// calling thread; a refused id is refused before anything is sent or stored; unticks move
    /// the counts; Forget clears the three fields; after a walk that stopped short the counts are
    /// made again off the disk without a trip; and the add refuses a running server and a realm
    /// that changed, by running the real RPC, while a plain paste is exactly as it was.
    /// </para>
    /// </summary>
    public class BlendWindowGaleTests : IDisposable
    {
        private const string Profile = "Gale Test";
        private const string Code = "ab12CD";
        private const string Stamp1 = "2026-10-06T12:34:56.789123+00:00";
        private const string Stamp2 = "2026-10-07T08:00:00.000001+00:00";

        private readonly string Root;
        private readonly string Plugins;
        private readonly RoundTrippingUserPrefs UserPrefs = new();
        private readonly ScriptedHandler Handler = new();
        private readonly GaleSyncClient Client;
        private readonly BlendWindow Window;
        private readonly Dictionary<string, Func<JObject, Task<object>>> Rpc;
        private readonly Mock<IModUpdateService> ModUpdates = new();
        private readonly Mock<IBepInExService> BepInEx = new();
        private readonly Mock<IThunderstoreClient> Thunderstore = new();
        private readonly Mock<IHexiumClient> Hexium = new();

        public BlendWindowGaleTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-gale-" + Guid.NewGuid().ToString("N"));
            var serverDir = Path.Combine(Root, "server");
            Plugins = Path.Combine(serverDir, "BepInEx", "plugins");
            Directory.CreateDirectory(Plugins);
            var exe = Path.Combine(serverDir, "valheim_server.exe");
            File.WriteAllText(exe, "MZ");

            // One mod already on the server, at the version the profile pins.
            Install("ValheimModding-Jotunn", "2.20.1");

            ((IUserPreferencesProvider)UserPrefs).Mutate(prefs =>
            {
                prefs.ServerExePath = exe;
                prefs.LogsFolderPath = MockUserPreferencesProvider.TestLogsFolder;
                prefs.Servers.Add(new ServerPreferences { ProfileName = Profile, Name = "Gale", WorldName = "Gale", Password = "secret1" });
            });

            Client = new GaleSyncClient(new HandlerProvider(Handler)) { Bound = TimeSpan.FromSeconds(5) };
            // The add's own services: BepInEx reads as installed, so a paste goes straight to the
            // install, and the install is a mock that records the call and touches nothing.
            BepInEx.Setup(b => b.Status(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<BepInExProfileInstall>>()))
                .Returns(new BepInExStatus { Installed = true });
            ModUpdates.Setup(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()))
                .ReturnsAsync((ThunderstoreModReference r, string _) => new ModInstallResult
                    { Installed = true, Owner = r.Owner, Name = r.Name, Version = r.Version });

            (Window, Rpc) = BuildWindow(UserPrefs, Client, new Dictionary<Type, object>
            {
                [typeof(IModUpdateService)] = ModUpdates.Object,
                [typeof(IBepInExService)] = BepInEx.Object,
                // Loose, and answering nothing unless a test says otherwise: the Gale tests never
                // reach it, and the dependency tests below give it the answers they need.
                [typeof(IThunderstoreClient)] = Thunderstore.Object,
                [typeof(IHexiumClient)] = Hexium.Object,
            });
            typeof(BlendWindow).GetField("CurrentProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, Profile);
        }

        public void Dispose()
        {
            // A planted session's server was never built; it is not the window's to dispose.
            try { Sessions().Clear(); } catch { }
            try { Window?.Dispose(); } catch { }
            try { Directory.Delete(Root, recursive: true); } catch { /* temp folder, best effort */ }
        }

        private void Install(string fullName, string version)
        {
            var dir = Path.Combine(Plugins, fullName);
            Directory.CreateDirectory(dir);
            var name = fullName.Substring(fullName.IndexOf('-') + 1);
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"name\":\"" + name + "\",\"version_number\":\"" + version + "\",\"website_url\":\"\",\"description\":\"\",\"dependencies\":[]}");
        }

        private static string Meta(string stamp, params (string Name, bool Enabled, int Major, int Minor, int Patch)[] mods)
        {
            var lines = mods.Select(m =>
                "{\"name\":\"" + m.Name + "\",\"enabled\":" + (m.Enabled ? "true" : "false")
                + ",\"version\":{\"major\":" + m.Major + ",\"minor\":" + m.Minor + ",\"patch\":" + m.Patch + "}}");
            return "{\"id\":\"" + Code + "\",\"updatedAt\":\"" + stamp + "\",\"manifest\":{\"profileName\":\"p\",\"mods\":["
                + string.Join(",", lines) + "]}}";
        }

        /// <summary>The profile with one mod to add, one the server already has, and one switched off.</summary>
        private static string WorkMeta(string stamp) => Meta(stamp,
            ("Azumatt-AzuCraftyBoxes", true, 1, 8, 4),
            ("ValheimModding-Jotunn", true, 2, 20, 1),
            ("RandyKnapp-EpicLoot", false, 0, 11, 0));

        /// <summary>
        /// An RPC reply as the page holds it: serialised and read back. Read with dates left as
        /// text, because the page's JSON.parse never turns a string into a date and Newtonsoft's
        /// default would, which is a difference in this reader and not in the reply.
        /// </summary>
        private async Task<JObject> Call(string method, object args)
        {
            var json = JsonConvert.SerializeObject(await Rpc[method](JObject.FromObject(args)));
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        private Task<JObject> Check() => Call("gale.check", new { profile = Profile });

        private async Task Follow()
        {
            var set = await Call("gale.set", new { profile = Profile, id = Code });
            Assert.True(set.Value<bool>("ok"));
        }

        private ServerPreferences Stored() => new ServerPreferencesProvider(UserPrefs, new Mock<Serilog.ILogger>().Object).LoadPreferences(Profile);

        // ------------------------------------------------------------------ the row

        [Fact]
        public async Task The_row_is_due_when_there_is_work_and_the_profile_moved()
        {
            Handler.Body = WorkMeta(Stamp1);
            await Follow();

            var state = await Check();
            var check = (JObject)state["check"];

            Assert.True(check.Value<bool>("ok"));
            Assert.True(check.Value<bool>("due"));
            Assert.Equal(1, check.Value<int>("addCount"));
            Assert.Equal(0, check.Value<int>("updateCount"));
            Assert.Equal(Stamp1, check.Value<string>("updatedAt"));
            Assert.Equal(new[] { "https://gale.kesomannen.com/api/profile/" + Code + "/meta" }, Handler.Requests);

            // The rows the dialog lists, and the disabled one for its note. Nothing is a removal.
            var kinds = check["rows"]!.Select(r => (r.Value<string>("name"), r.Value<string>("kind"))).ToList();
            Assert.Contains(("Azumatt-AzuCraftyBoxes", "add"), kinds);
            Assert.Contains(("ValheimModding-Jotunn", "same"), kinds);
            Assert.Contains(("RandyKnapp-EpicLoot", "disabled"), kinds);
        }

        [Fact]
        public async Task A_dismissal_holds_until_updatedAt_moves()
        {
            Handler.Body = WorkMeta(Stamp1);
            await Follow();
            var first = (JObject)(await Check())["check"];
            Assert.True(first.Value<bool>("due"));

            var dismissed = await Call("gale.dismiss", new { profile = Profile, seq = first.Value<long>("seq") });
            Assert.True(dismissed.Value<bool>("ok"));
            Assert.False(dismissed["state"]!["check"]!.Value<bool>("due"));
            // The service's text, byte for byte, never a date written back out.
            Assert.Equal(Stamp1, Stored().GaleLastUpdatedAt);

            // The same profile again: still work to do, but it has not moved, so no row.
            var again = (JObject)(await Check())["check"];
            Assert.Equal(1, again.Value<int>("addCount"));
            Assert.False(again.Value<bool>("due"));

            // The profile moves: the row is due again.
            Handler.Body = WorkMeta(Stamp2);
            var moved = (JObject)(await Check())["check"];
            Assert.True(moved.Value<bool>("due"));
        }

        [Fact]
        public async Task A_moved_profile_with_nothing_to_install_raises_no_row()
        {
            Handler.Body = Meta(Stamp2, ("ValheimModding-Jotunn", true, 2, 20, 1), ("RandyKnapp-EpicLoot", false, 0, 11, 0));
            await Follow();

            var check = (JObject)(await Check())["check"];

            Assert.True(check.Value<bool>("ok"));
            Assert.Equal(0, check.Value<int>("addCount") + check.Value<int>("updateCount"));
            Assert.False(check.Value<bool>("due"));
        }

        [Fact]
        public async Task A_stale_dismissal_stores_nothing()
        {
            Handler.Body = WorkMeta(Stamp1);
            await Follow();
            var first = (JObject)(await Check())["check"];
            Handler.Body = WorkMeta(Stamp2);
            await Check();

            // The host waved away the row they were shown, from the FIRST check. A newer check
            // has landed since, so its stamp is not the one they saw and nothing is stored.
            var dismissed = await Call("gale.dismiss", new { profile = Profile, seq = first.Value<long>("seq") });

            Assert.False(dismissed.Value<bool>("ok"));
            Assert.Null(Stored().GaleLastUpdatedAt);
        }

        [Fact]
        public async Task Unticked_names_leave_the_counts_and_stay_unticked()
        {
            Handler.Body = Meta(Stamp1, ("Azumatt-AzuCraftyBoxes", true, 1, 8, 4), ("ValheimModding-Jotunn", true, 2, 21, 0));
            await Follow();
            var check = (JObject)(await Check())["check"];
            Assert.Equal(1, check.Value<int>("addCount"));
            Assert.Equal(1, check.Value<int>("updateCount"));

            var after = await Call("gale.untick", new { profile = Profile, unticked = new[] { "Azumatt-AzuCraftyBoxes" }, ticked = new[] { "ValheimModding-Jotunn" } });
            var c = (JObject)after["state"]!["check"];
            Assert.Equal(0, c.Value<int>("addCount"));
            Assert.Equal(1, c.Value<int>("updateCount"));
            var row = c["rows"]!.First(r => r.Value<string>("name") == "Azumatt-AzuCraftyBoxes");
            Assert.Equal("unticked", row.Value<string>("kind"));
            Assert.Equal("add", row.Value<string>("untickedFrom"));
            Assert.Equal(new[] { "Azumatt-AzuCraftyBoxes" }, Stored().GaleUnticked);

            // Everything unticked: nothing to install, so no row, whatever the stamp says.
            var none = await Call("gale.untick", new { profile = Profile, unticked = new[] { "ValheimModding-Jotunn" }, ticked = Array.Empty<string>() });
            Assert.False(none["state"]!["check"]!.Value<bool>("due"));
        }

        [Fact]
        public async Task After_a_walk_that_stopped_short_the_counts_follow_the_disk_and_Gale_is_not_asked()
        {
            Handler.Body = Meta(Stamp1,
                ("Azumatt-AzuCraftyBoxes", true, 1, 8, 4),
                ("shudnal-ExtraSlots", true, 1, 0, 0),
                ("ValheimModding-Jotunn", true, 2, 21, 0));
            await Follow();
            var first = (JObject)(await Check())["check"];
            Assert.Equal(2, first.Value<int>("addCount"));
            Assert.Equal(1, first.Value<int>("updateCount"));
            var asked = Handler.Count;

            // Two of the three went in; the third failed. No stamp is taken.
            Install("Azumatt-AzuCraftyBoxes", "1.8.4");
            Install("ValheimModding-Jotunn", "2.21.0");

            // gale.get alone still holds the scan from check time: the stale state the page
            // drew from before the rescan existed.
            var before = (JObject)(await Call("gale.get", new { profile = Profile }))["check"];
            Assert.Equal(2, before.Value<int>("addCount"));

            var r = await Call("gale.rescan", new { profile = Profile });
            Assert.True(r.Value<bool>("ok"));
            var c = (JObject)r["state"]!["check"];

            Assert.Equal(1, c.Value<int>("addCount"));
            Assert.Equal(0, c.Value<int>("updateCount"));
            Assert.Equal(first.Value<long>("seq"), c.Value<long>("seq"));
            Assert.Equal(first.Value<string>("at"), c.Value<string>("at"));
            // Still due: the row stays for the one that is left.
            Assert.True(c.Value<bool>("due"));
            var kinds = c["rows"]!.ToDictionary(x => x.Value<string>("name"), x => x.Value<string>("kind"));
            Assert.Equal("same", kinds["Azumatt-AzuCraftyBoxes"]);
            Assert.Equal("same", kinds["ValheimModding-Jotunn"]);
            Assert.Equal("add", kinds["shudnal-ExtraSlots"]);
            Assert.Null(Stored().GaleLastUpdatedAt);
            Assert.Equal(asked, Handler.Count);

            // The seq it kept is still the one a dismissal names.
            var dismissed = await Call("gale.dismiss", new { profile = Profile, seq = c.Value<long>("seq") });
            Assert.True(dismissed.Value<bool>("ok"));
            Assert.Equal(asked, Handler.Count);
        }

        [Fact]
        public async Task A_rescan_with_no_check_held_does_nothing_and_asks_nothing()
        {
            await Follow();
            var r = await Call("gale.rescan", new { profile = Profile });
            Assert.False(r.Value<bool>("ok"));
            Assert.Equal(JTokenType.Null, r["state"]!["check"]!.Type);
            Assert.Equal(0, Handler.Count);
        }

        // ------------------------------------------------------------------ the add's two holds

        private const string PinnedUrl = "https://thunderstore.io/c/valheim/p/Azumatt/AzuCraftyBoxes/v/1.8.4/";

        private System.Collections.Concurrent.ConcurrentDictionary<string, ServerSession> Sessions()
            => (System.Collections.Concurrent.ConcurrentDictionary<string, ServerSession>)typeof(BlendWindow)
                .GetField("Sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);

        /// <summary>
        /// A session that reads as up, on this test's own server exe. Its server is never built or
        /// started: only the status the add's hold reads is set, so nothing runs and nothing opens.
        /// </summary>
        private void PlantRunning(string profile)
        {
            var server = (ValheimServer)RuntimeHelpers.GetUninitializedObject(typeof(ValheimServer));
            typeof(ValheimServer).GetField("CurrentStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(server, ServerStatus.Running);
            Assert.Equal(ServerStatus.Running, server.Status);
            Sessions()[profile] = new ServerSession(profile, server);
        }

        [Fact]
        public async Task The_add_refuses_a_running_server_when_the_dialog_asks_for_a_stopped_one()
        {
            PlantRunning(Profile);

            var a = await Call("mods.addFromUrl", new { url = PinnedUrl, profile = Profile, requireStopped = true });

            Assert.False(a.Value<bool>("Installed"));
            Assert.Equal("serverRunning", a.Value<string>("Reason"));
            Assert.Equal(Profile, a["ErrorParams"]!.Value<string>("name"));
            ModUpdates.Verify(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task The_add_refuses_when_the_realm_on_screen_changed()
        {
            var a = await Call("mods.addFromUrl", new { url = PinnedUrl, profile = "Some Other Realm", requireStopped = true });

            Assert.False(a.Value<bool>("Installed"));
            Assert.Equal("realmChanged", a.Value<string>("Reason"));
            ModUpdates.Verify(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task A_plain_paste_is_unchanged_and_installs_with_a_server_up()
        {
            // A paste sends neither key, so neither hold applies: the Mods hall's own add is
            // exactly what it was before the dialog existed.
            PlantRunning(Profile);

            var a = await Call("mods.addFromUrl", new { url = PinnedUrl });

            Assert.True(a.Value<bool>("Installed"));
            Assert.Null(a.Value<string>("Reason"));
            ModUpdates.Verify(m => m.InstallFromThunderstoreAsync(
                It.Is<ThunderstoreModReference>(r => r.Owner == "Azumatt" && r.Name == "AzuCraftyBoxes" && r.Version == "1.8.4"),
                It.Is<string>(d => string.Equals(Path.GetFullPath(d), Path.GetFullPath(Plugins), StringComparison.OrdinalIgnoreCase))),
                Times.Once);
        }

        [Fact]
        public async Task The_dialogs_add_goes_through_on_a_stopped_server_in_the_right_realm()
        {
            var a = await Call("mods.addFromUrl", new { url = PinnedUrl, profile = Profile, requireStopped = true });

            Assert.True(a.Value<bool>("Installed"));
            ModUpdates.Verify(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()), Times.Once);
        }

        // ------------------------------------------------------------------ 1.2.16: what the mod needs

        private const string ForgeUrl = "https://thunderstore.io/c/valheim/p/Someone/Someone_ModA/";

        /// <summary>A plugin folder with a manifest that lists these dependencies.</summary>
        private void InstallNeeding(string fullName, string version, params string[] needs)
        {
            var dir = Path.Combine(Plugins, fullName);
            Directory.CreateDirectory(dir);
            var name = fullName.Substring(fullName.IndexOf('-') + 1);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonConvert.SerializeObject(new
            {
                name, version_number = version, website_url = "", description = "", dependencies = needs,
            }));
        }

        /// <summary>The install mock answering with what the installed build said it needs, and writing its folder.</summary>
        private void InstallAnswers(params string[] needs)
        {
            ModUpdates.Setup(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()))
                .ReturnsAsync((ThunderstoreModReference r, string _) =>
                {
                    InstallNeeding($"{r.Owner}-{r.Name}", r.Version ?? "1.0.0", needs);
                    return new ModInstallResult
                        { Installed = true, Owner = r.Owner, Name = r.Name, Version = r.Version ?? "1.0.0", Dependencies = needs };
                });
        }

        private static ThunderstoreLiveLookup LatestNeeding(string owner, string name, params string[] needs) => new()
        {
            Answered = true,
            Package = new ThunderstorePackage
            {
                Namespace = owner,
                Name = name,
                Latest = new ThunderstorePackageVersion { VersionNumber = "1.0.2", Dependencies = needs.ToList() },
            },
        };

        [Fact]
        public async Task A_link_install_hands_back_the_missing_dependency_and_the_one_behind_it()
        {
            InstallAnswers("denikson-BepInExPack_Valheim-5.4.2350", "Someone-Someone_CoreLib-1.0.2");
            Thunderstore.Setup(t => t.LookupLiveAsync("Someone", "Someone_CoreLib"))
                .ReturnsAsync(LatestNeeding("Someone", "Someone_CoreLib", "denikson-BepInExPack_Valheim-5.4.2350", "ValheimModding-Jotunn-2.25.0"));

            var a = await Call("mods.addFromUrl", new { url = ForgeUrl });

            Assert.True(a.Value<bool>("Installed"));
            var needs = (JArray)a["missingDependencies"]!;
            Assert.Equal(2, needs.Count);
            Assert.Equal("Someone-Someone_CoreLib", needs[0]!.Value<string>("fullName"));
            Assert.Equal("Someone_CoreLib", needs[0]!.Value<string>("name"));
            Assert.Equal("missing", needs[0]!.Value<string>("state"));
            Assert.Equal("1.0.2", needs[0]!.Value<string>("minVersion"));
            Assert.Equal("Someone-Someone_ModA", needs[0]!.Value<string>("requiredBy"));
            // Jotunn is on the server at 2.20.1, and the core asks for 2.25.0.
            Assert.Equal("ValheimModding-Jotunn", needs[1]!.Value<string>("fullName"));
            Assert.Equal("older", needs[1]!.Value<string>("state"));
            Assert.Equal("2.20.1", needs[1]!.Value<string>("installedVersion"));
            Assert.Equal("Someone-Someone_CoreLib", needs[1]!.Value<string>("requiredBy"));
            // One install, the pasted one: the host side offers, it never fetches on its own.
            ModUpdates.Verify(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task A_link_install_with_everything_there_carries_an_empty_list()
        {
            InstallNeeding("Someone-Someone_CoreLib", "1.0.5");
            InstallAnswers("Someone-Someone_CoreLib-1.0.2", "ValheimModding-Jotunn-2.20.0");

            var a = await Call("mods.addFromUrl", new { url = ForgeUrl });

            Assert.True(a.Value<bool>("Installed"));
            Assert.Equal(JTokenType.Array, a["missingDependencies"]!.Type);
            Assert.Empty(a["missingDependencies"]!);
            // Everything was on disk, so the site was never asked.
            Thunderstore.Verify(t => t.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task The_BepInEx_pack_counts_as_there_when_BepInEx_is_installed()
        {
            InstallAnswers("denikson-BepInExPack_Valheim-5.4.2350");

            var a = await Call("mods.addFromUrl", new { url = ForgeUrl });

            Assert.True(a.Value<bool>("Installed"));
            Assert.Empty(a["missingDependencies"]!);
            Thunderstore.Verify(t => t.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task A_refused_add_has_the_same_shape_with_nothing_in_it()
        {
            var a = await Call("mods.addFromUrl", new { url = "not a link at all" });
            Assert.False(a.Value<bool>("Installed"));
            Assert.Equal("badUrl", a.Value<string>("Reason"));
            Assert.Empty(a["missingDependencies"]!);
        }

        [Fact]
        public async Task The_Gale_walks_add_is_one_install_whatever_the_mod_needs()
        {
            // The page's Gale walk ignores the field; the host side must not act on it either.
            InstallAnswers("Someone-Someone_CoreLib-1.0.2");

            var a = await Call("mods.addFromUrl", new { url = PinnedUrl, profile = Profile, requireStopped = true });

            Assert.True(a.Value<bool>("Installed"));
            Assert.Single(a["missingDependencies"]!);
            ModUpdates.Verify(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()), Times.Once);
            ModUpdates.Verify(m => m.InstallFromThunderstoreAsync(
                It.Is<ThunderstoreModReference>(r => r.Owner == "Someone"), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void The_scan_row_says_what_it_needs_from_the_disk_alone()
        {
            InstallNeeding("Someone-Someone_ModA", "1.0.0", "denikson-BepInExPack_Valheim-5.4.2350", "Someone-Someone_CoreLib-1.0.2");
            Directory.CreateDirectory(Path.Combine(Plugins, "Someone-HandCopy"));
            File.WriteAllText(Path.Combine(Plugins, "Someone-HandCopy", "HandCopy.dll"), "MZ");

            // The scan RPC itself reaches both sites and the Hexium step, which this window was
            // not built with, so the two steps it runs for the rows are driven as it runs them:
            // the same scanner, then MarkMissingDependencies, then BuildModDto, through JSON.
            var mods = new ModScanner(new Mock<IApplicationLogger>().Object).ScanPlugins(Plugins);
            typeof(BlendWindow).GetMethod("MarkMissingDependencies", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Window, new object[] { mods, Plugins });
            var rows = JArray.Parse(JsonConvert.SerializeObject(mods.Select(m => BlendWindow.BuildModDto(m)).ToList()));
            var forge = rows.Single(r => r.Value<string>("FullName") == "Someone-Someone_ModA");
            var need = Assert.Single((JArray)forge["missingDependencies"]!);
            Assert.Equal("Someone-Someone_CoreLib", need!.Value<string>("fullName"));
            Assert.Equal("missing", need.Value<string>("state"));
            Assert.Empty(rows.Single(r => r.Value<string>("FullName") == "Someone-HandCopy")["missingDependencies"]!);
            Assert.Empty(rows.Single(r => r.Value<string>("FullName") == "ValheimModding-Jotunn")["missingDependencies"]!);
            // No site was asked about a dependency.
            Assert.Empty(Thunderstore.Invocations);

            // And the scan RPC runs the step before it builds its rows.
            var bridge = AppSourceTree.Lf(AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"));
            var scanRpc = bridge.Substring(bridge.IndexOf("RegisterRpc(\"mods.scan\"", StringComparison.Ordinal));
            scanRpc = scanRpc.Substring(0, scanRpc.IndexOf("RegisterRpc(\"mods.checkOne\"", StringComparison.Ordinal));
            Assert.True(scanRpc.IndexOf("MarkMissingDependencies(mods, pluginsDir);", StringComparison.Ordinal)
                < scanRpc.IndexOf("BuildModDto(m, gameUpdatedUtc)", StringComparison.Ordinal));
            Assert.Contains("MarkMissingDependencies(mods, pluginsDir);", scanRpc);
        }

        [Fact]
        public async Task Hexiums_consent_marks_each_dependency_against_the_disk()
        {
            ((IUserPreferencesProvider)UserPrefs).Mutate(prefs => prefs.UseHexiumSource = true);
            // A window built without its constructor has no consent gate; the real one is put in.
            typeof(BlendWindow).GetField("HexiumConsent", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Window, new HexiumConsentGate());
            InstallNeeding("Azumatt-AzuCraftyBoxes", "1.8.4");
            // Switched off: its plugin file is named .dll.old and there is no .dll beside it.
            File.WriteAllText(Path.Combine(Plugins, "Azumatt-AzuCraftyBoxes", "AzuCraftyBoxes.dll.old"), "MZ");
            Hexium.Setup(h => h.LookupAsync("JereKuusela-WorldEditCommands")).ReturnsAsync(new HexiumLookup
            {
                IndexAvailable = true,
                Package = new HexiumPackage
                {
                    Owner = "JereKuusela",
                    Name = "WorldEditCommands",
                    Versions = new List<HexiumPackageVersion>
                    {
                        new()
                        {
                            VersionNumber = "1.67.0",
                            DownloadUrl = "https://hexium.gg/download/1",
                            Dependencies = new List<string>
                            {
                                "denikson-BepInExPack_Valheim-5.4.2350", "Someone-Someone_CoreLib-1.0.2",
                                "ValheimModding-Jotunn-2.25.0", "Azumatt-AzuCraftyBoxes-1.8.0",
                            },
                        },
                    },
                },
            });

            var r = await Call("mods.hexiumPrepare", new { owner = "JereKuusela", name = "WorldEditCommands", version = "1.67.0" });

            Assert.True(r.Value<bool>("NeedsConsent"), r.ToString());
            Assert.Equal(4, ((JArray)r["Dependencies"]!).Count);
            var marks = ((JArray)r["DependencyMarks"]!).Select(m => (m!.Value<string>("fullName"), m.Value<string>("state"))).ToArray();
            Assert.Equal(new[]
            {
                ("Someone-Someone_CoreLib", "missing"),
                ("ValheimModding-Jotunn", "older"),
                ("Azumatt-AzuCraftyBoxes", "off"),
            }, marks);
        }

        // ------------------------------------------------------------------ failures are a line

        [Fact]
        public async Task A_400_is_a_status_line_and_never_a_row()
        {
            Handler.Status = HttpStatusCode.BadRequest;
            Handler.Body = "Invalid URL";
            await Follow();

            var check = (JObject)(await Check())["check"];

            Assert.False(check.Value<bool>("ok"));
            Assert.False(check.Value<bool>("due"));
            Assert.Equal("mods.gale.check.bad_request", check.Value<string>("statusId"));
            Assert.Empty(check["rows"]!);
        }

        // ------------------------------------------------------------------ another game

        /// <summary>The work profile with manifest.community set, or left out when null.</summary>
        private static string WorkMetaFor(string community, string stamp)
        {
            var meta = JObject.Parse(WorkMeta(stamp));
            if (community != null) ((JObject)meta["manifest"]!)["community"] = community;
            return meta.ToString(Formatting.None);
        }

        [Theory]
        [InlineData("valheim")]
        [InlineData("VALHEIM")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public async Task A_Valheim_profile_or_an_old_one_with_no_game_is_compared(string community)
        {
            Handler.Body = WorkMetaFor(community, Stamp1);
            await Follow();

            var check = (JObject)(await Check())["check"];

            Assert.True(check.Value<bool>("ok"));
            Assert.True(check.Value<bool>("due"));
            Assert.Null(check.Value<string>("statusId"));
            Assert.Contains(check["rows"]!, r => r.Value<string>("name") == "Azumatt-AzuCraftyBoxes");
        }

        [Theory]
        [InlineData("atlyss")]
        [InlineData("bonelab")]
        public async Task A_profile_for_another_game_is_a_status_line_and_never_a_row(string community)
        {
            Handler.Body = WorkMetaFor(community, Stamp1);
            await Follow();

            var state = await Check();
            var check = (JObject)state["check"];

            Assert.False(check.Value<bool>("ok"));
            Assert.False(check.Value<bool>("due"));
            Assert.Equal("mods.gale.check.other_game", check.Value<string>("statusId"));
            Assert.Equal(community, check.Value<string>("statusGame"));
            Assert.Equal(0, check.Value<int>("addCount"));
            Assert.Equal(0, check.Value<int>("updateCount"));
            Assert.Empty(check["rows"]!);

            // Waving away a refusal stores nothing: the row it never raised cannot be dismissed.
            var dismiss = await Call("gale.dismiss", new { profile = Profile, seq = check.Value<long>("seq") });
            Assert.False(dismiss.Value<bool>("ok"));
            Assert.Null(Stored().GaleLastUpdatedAt);
        }

        [Fact]
        public void The_other_game_sentence_is_in_the_catalog_with_its_slot()
        {
            var en = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WebUI", "i18n", "en.json")));
            var entry = en.Descendants().OfType<JProperty>().First(p => p.Name == "mods.gale.check.other_game").Value;
            Assert.Equal("That profile is for {game}, not Valheim.", entry.Value<string>("lore"));
        }

        [Fact]
        public async Task A_timeout_is_a_status_line_and_never_a_row()
        {
            Handler.Stall = true;
            Client.Bound = TimeSpan.FromMilliseconds(300);
            await Follow();

            var check = (JObject)(await Check())["check"];

            Assert.False(check.Value<bool>("ok"));
            Assert.False(check.Value<bool>("due"));
            Assert.Equal("mods.gale.check.timeout", check.Value<string>("statusId"));
        }

        [Fact]
        public async Task A_realm_with_no_id_asks_nothing()
        {
            Handler.Body = WorkMeta(Stamp1);

            var state = await Check();

            Assert.False(state.Value<bool>("following"));
            Assert.Equal(JTokenType.Null, state["check"]!.Type);
            Assert.Equal(0, Handler.Count);
        }

        [Fact]
        public async Task A_bad_id_is_refused_before_anything_is_sent_or_stored()
        {
            var set = await Call("gale.set", new { profile = Profile, id = "../../etc" });

            Assert.False(set.Value<bool>("ok"));
            Assert.Equal("mods.gale.id.bad", set.Value<string>("sentenceId"));
            Assert.Null(Stored().GaleProfileId);

            await Check();
            Assert.Equal(0, Handler.Count);
        }

        [Fact]
        public async Task The_trip_runs_off_the_calling_thread_and_the_call_does_not_wait_on_it()
        {
            Handler.Body = WorkMeta(Stamp1);
            Handler.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Follow();

            var pending = Check();
            // The handler is held, so the call cannot have finished; it came back as a task
            // rather than holding the caller until Gale answered.
            Assert.False(pending.IsCompleted);

            Handler.Gate.SetResult(true);
            var check = (JObject)(await pending)["check"];

            Assert.True(check.Value<bool>("ok"));
            Assert.True(Handler.RanOnPoolThread);
        }

        [Fact]
        public async Task Forget_clears_the_id_the_stamp_and_the_unticks()
        {
            Handler.Body = WorkMeta(Stamp1);
            await Follow();
            var first = (JObject)(await Check())["check"];
            await Call("gale.dismiss", new { profile = Profile, seq = first.Value<long>("seq") });
            await Call("gale.untick", new { profile = Profile, unticked = new[] { "Azumatt-AzuCraftyBoxes" } });

            var state = await Call("gale.forget", new { profile = Profile });

            Assert.False(state.Value<bool>("following"));
            var stored = Stored();
            Assert.Null(stored.GaleProfileId);
            Assert.Null(stored.GaleLastUpdatedAt);
            Assert.Empty(stored.GaleUnticked);
        }

        [Fact]
        public async Task A_new_id_drops_the_old_profiles_stamp_and_unticks()
        {
            Handler.Body = WorkMeta(Stamp1);
            await Follow();
            var first = (JObject)(await Check())["check"];
            await Call("gale.dismiss", new { profile = Profile, seq = first.Value<long>("seq") });
            await Call("gale.untick", new { profile = Profile, unticked = new[] { "Azumatt-AzuCraftyBoxes" } });

            var set = await Call("gale.set", new { profile = Profile, id = "ZZ99yy" });

            Assert.True(set.Value<bool>("ok"));
            var stored = Stored();
            Assert.Equal("ZZ99yy", stored.GaleProfileId);
            Assert.Null(stored.GaleLastUpdatedAt);
            Assert.Empty(stored.GaleUnticked);
            Assert.Equal(JTokenType.Null, set["state"]!["check"]!.Type);
        }

        // ------------------------------------------------------------------ BepInEx first (SPEC L)

        /// <summary>The profile as the isolated run had it: the pack first, then one mod to add.</summary>
        private static string PackMeta(string stamp) => Meta(stamp,
            ("denikson-BepInExPack_Valheim", true, 5, 4, 2333),
            ("Azumatt-AzuCraftyBoxes", true, 1, 8, 4),
            ("ValheimModding-Jotunn", true, 2, 20, 1));

        /// <summary>
        /// A server with no BepInEx until the BepInEx write runs, and a record of the order the
        /// two writes happened in. The write is a mock: nothing is downloaded or unpacked.
        /// </summary>
        private List<string> NoBepInExUntilWritten()
        {
            // The one write slot every BepInEx write takes, which the window's own constructor
            // makes and an uninitialized window does not have.
            typeof(BlendWindow).GetField("_bepInExWriteSlot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Window, new SemaphoreSlim(1, 1));
            var order = new List<string>();
            var there = false;
            BepInEx.Setup(b => b.Status(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<BepInExProfileInstall>>()))
                .Returns(() => new BepInExStatus { Installed = there });
            BepInEx.Setup(b => b.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<BepInExProfileInstall>>(),
                    It.IsAny<IProgress<BepInExProgress>>(), It.IsAny<CancellationToken>(), It.IsAny<BepInExWriteOptions>()))
                .ReturnsAsync(() =>
                {
                    order.Add("bepinex");
                    there = true;
                    return new BepInExInstallResult { Installed = true, Version = "5.4.2333", Package = "denikson-BepInExPack_Valheim" };
                });
            ModUpdates.Setup(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()))
                .ReturnsAsync((ThunderstoreModReference r, string _) =>
                {
                    order.Add(r.Owner + "-" + r.Name);
                    return new ModInstallResult { Installed = true, Owner = r.Owner, Name = r.Name, Version = r.Version };
                });
            return order;
        }

        [Fact]
        public async Task With_no_BepInEx_and_the_pack_in_the_profile_BepInEx_goes_in_before_the_first_mod()
        {
            var order = NoBepInExUntilWritten();
            Handler.Body = PackMeta(Stamp1);
            await Follow();

            // What the dialog reads: BepInEx is missing and the profile carries the pack.
            var check = (JObject)(await Check())["check"];
            Assert.True(check["bepinex"]!.Value<bool>("missing"));
            Assert.True(check["bepinex"]!.Value<bool>("inProfile"));
            Assert.Equal("framework", check["rows"]!.First(r => r.Value<string>("name") == "denikson-BepInExPack_Valheim").Value<string>("kind"));

            // The bug as the isolated run met it: the Mods hall's add alone stops on noBepInEx.
            var bare = await Call("mods.addFromUrl", new { url = PinnedUrl, profile = Profile, requireStopped = true });
            Assert.Equal("noBepInEx", bare.Value<string>("Reason"));
            Assert.Empty(order);

            // The dialog's walk: BepInEx first, then the mod.
            var bep = await Call("gale.bepinex", new { profile = Profile });
            Assert.True(bep.Value<bool>("ok"));
            Assert.True(bep.Value<bool>("wrote"));
            var a = await Call("mods.addFromUrl", new { url = PinnedUrl, profile = Profile, requireStopped = true });
            Assert.True(a.Value<bool>("Installed"));
            Assert.Equal(new[] { "bepinex", "Azumatt-AzuCraftyBoxes" }, order);
            BepInEx.Verify(b => b.InstallAsync(It.IsAny<string>(), null, It.IsAny<IEnumerable<BepInExProfileInstall>>(),
                It.IsAny<IProgress<BepInExProgress>>(), It.IsAny<CancellationToken>(), It.IsAny<BepInExWriteOptions>()), Times.Once);

            // The approval answered the open question yes, the way the condition bar's Yes does.
            var prefs = UserPrefs.LoadPreferences();
            Assert.True(prefs.BepInExMaintenanceAsked);
            Assert.True(prefs.BepInExMaintained);

            // Once it is in, a second ask writes nothing.
            var again = await Call("gale.bepinex", new { profile = Profile });
            Assert.True(again.Value<bool>("ok"));
            Assert.False(again.Value<bool>("wrote"));
            Assert.Equal(new[] { "bepinex", "Azumatt-AzuCraftyBoxes" }, order);
        }

        [Fact]
        public async Task A_No_the_host_gave_earlier_stands_and_the_approved_write_still_goes_in()
        {
            var order = NoBepInExUntilWritten();
            ((IUserPreferencesProvider)UserPrefs).Mutate(prefs =>
            {
                prefs.BepInExMaintained = false;
                prefs.BepInExMaintenanceAsked = true;
                return true;
            });
            Handler.Body = PackMeta(Stamp1);
            await Follow();
            await Check();

            var bep = await Call("gale.bepinex", new { profile = Profile });

            Assert.True(bep.Value<bool>("wrote"));
            Assert.Equal(new[] { "bepinex" }, order);
            var prefs = UserPrefs.LoadPreferences();
            Assert.True(prefs.BepInExMaintenanceAsked);
            Assert.False(prefs.BepInExMaintained);
        }

        [Fact]
        public async Task With_no_BepInEx_and_no_pack_in_the_profile_nothing_is_written()
        {
            var order = NoBepInExUntilWritten();
            Handler.Body = WorkMeta(Stamp1);
            await Follow();

            var check = (JObject)(await Check())["check"];
            Assert.True(check["bepinex"]!.Value<bool>("missing"));
            Assert.False(check["bepinex"]!.Value<bool>("inProfile"));

            var bep = await Call("gale.bepinex", new { profile = Profile });
            Assert.False(bep.Value<bool>("ok"));
            Assert.Equal("bepinexNotInProfile", bep.Value<string>("Reason"));
            Assert.Empty(order);
            Assert.False(UserPrefs.LoadPreferences().BepInExMaintenanceAsked);
        }

        [Fact]
        public async Task The_BepInEx_write_refuses_a_running_server_and_another_realm()
        {
            var order = NoBepInExUntilWritten();
            Handler.Body = PackMeta(Stamp1);
            await Follow();
            await Check();

            Assert.Equal("realmChanged", (await Call("gale.bepinex", new { profile = "Some Other Realm" })).Value<string>("Reason"));
            PlantRunning(Profile);
            Assert.Equal("serverRunning", (await Call("gale.bepinex", new { profile = Profile })).Value<string>("Reason"));
            Assert.Empty(order);
        }

        [Fact]
        public async Task A_server_with_BepInEx_is_not_said_to_be_missing_it()
        {
            Handler.Body = PackMeta(Stamp1);
            await Follow();

            var check = (JObject)(await Check())["check"];
            Assert.False(check["bepinex"]!.Value<bool>("missing"));
            Assert.True(check["bepinex"]!.Value<bool>("inProfile"));
        }

        // ------------------------------------------------------------------ not available (SPEC M)

        [Fact]
        public async Task A_pinned_build_Thunderstore_no_longer_has_is_marked_and_stops_counting()
        {
            ModUpdates.Setup(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()))
                .ReturnsAsync((ThunderstoreModReference r, string _) => new ModInstallResult
                {
                    Installed = false, Owner = r.Owner, Name = r.Name, Version = r.Version,
                    Reason = ModInstallResult.NotAvailableReason, Error = "gone",
                });
            Handler.Body = WorkMeta(Stamp1);
            await Follow();
            var before = (JObject)(await Check())["check"];
            Assert.Equal(1, before.Value<int>("addCount"));
            Assert.True(before.Value<bool>("due"));

            var a = await Call("mods.addFromUrl", new { url = PinnedUrl, profile = Profile, requireStopped = true });
            Assert.False(a.Value<bool>("Installed"));
            Assert.Equal("notAvailable", a.Value<string>("Reason"));
            Assert.Equal("Azumatt-AzuCraftyBoxes", a["ErrorParams"]!.Value<string>("mod"));
            Assert.Equal("1.8.4", a["ErrorParams"]!.Value<string>("version"));
            Assert.Equal(new[] { "Azumatt-AzuCraftyBoxes@1.8.4" }, Stored().GaleNotAvailable);

            // The next review: the row carries the mark, and it is no longer work.
            var after = (JObject)(await Call("gale.get", new { profile = Profile }))["check"];
            var row = after["rows"]!.First(r => r.Value<string>("name") == "Azumatt-AzuCraftyBoxes");
            Assert.True(row.Value<bool>("notAvailable"));
            Assert.Equal("add", row.Value<string>("kind"));
            Assert.Equal(0, after.Value<int>("addCount"));
            Assert.False(after.Value<bool>("due"));

            // A plain paste of the same build says the same reason and marks nothing.
            var pasted = await Call("mods.addFromUrl", new { url = PinnedUrl });
            Assert.Equal("notAvailable", pasted.Value<string>("Reason"));

            // A build that does go in later takes its mark off.
            ModUpdates.Setup(m => m.InstallFromThunderstoreAsync(It.IsAny<ThunderstoreModReference>(), It.IsAny<string>()))
                .ReturnsAsync((ThunderstoreModReference r, string _) => new ModInstallResult { Installed = true, Owner = r.Owner, Name = r.Name, Version = r.Version });
            await Call("mods.addFromUrl", new { url = PinnedUrl, profile = Profile, requireStopped = true });
            Assert.Empty(Stored().GaleNotAvailable);
        }

        [Fact]
        public async Task A_new_id_drops_the_not_available_marks()
        {
            Handler.Body = WorkMeta(Stamp1);
            await Follow();
            ((IUserPreferencesProvider)UserPrefs).Mutate(prefs =>
            {
                prefs.Servers.First(s => s.ProfileName == Profile).GaleNotAvailable = new List<string> { "Azumatt-AzuCraftyBoxes@1.8.4" };
                return true;
            });
            Assert.Single(Stored().GaleNotAvailable);

            await Call("gale.set", new { profile = Profile, id = "ZZ99yy" });
            Assert.Empty(Stored().GaleNotAvailable);
        }

        // ------------------------------------------------------------------ the profile's configs (1.2.16)

        private string ConfigDir => Path.Combine(Path.GetDirectoryName(Plugins)!, "config");
        private string BackupRoot => Path.Combine(Path.GetDirectoryName(Plugins)!, BlendWindow.GaleConfigBackupFolder);

        private void RealmConfig(string rel, string text)
        {
            var full = Path.Combine(ConfigDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Encoding.UTF8.GetBytes(text));
        }

        private string RealmText(string rel) =>
            Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(ConfigDir, rel.Replace('/', Path.DirectorySeparatorChar))));

        /// <summary>
        /// The realm holds a changed Jotunn config (BepInEx wrote it with CRLF), a BepInEx.cfg the
        /// profile has with LF only, a config the profile does not carry, and no Azumatt config.
        /// </summary>
        private async Task FollowWithConfigs()
        {
            RealmConfig("Jotunn/jotunn.cfg", "[General]\r\nSpeed = 1\r\n");
            RealmConfig("BepInEx.cfg", "[Logging]\r\nEnabled = true\r\n");
            RealmConfig("mine.cfg", "host = mine\r\n");
            RealmConfig("second.cfg", "b = 1\r\n");
            Handler.ZipBody = GaleProfileConfigsTests.Zip(
                ("export.r2x", "profileName: p"),
                ("config/Azumatt.AzuCraftyBoxes.yml", "range: 20\n"),
                ("config/Jotunn/jotunn.cfg", "[General]\nSpeed = 2\n"),
                ("config/BepInEx.cfg", "[Logging]\nEnabled = true"),
                ("config/second.cfg", "b = 2\n"));
            Handler.Body = WorkMeta(Stamp1);
            await Follow();
            await Check();
        }

        private Task<JObject> Configs(bool refresh = false) => Call("gale.configs", new { profile = Profile, refresh });

        private Task<JObject> Apply(params string[] paths) => Call("gale.configs.apply", new { profile = Profile, paths });

        private static Dictionary<string, JToken> FilesOf(JObject state) =>
            state["configs"]!["files"]!.ToDictionary(f => f.Value<string>("path")!, f => f);

        [Fact]
        public async Task The_configs_are_listed_against_the_realm_with_their_ticks_and_previews()
        {
            await FollowWithConfigs();
            Assert.DoesNotContain(Handler.Requests, r => !r.EndsWith("/meta", StringComparison.Ordinal));

            var state = await Configs();
            var c = (JObject)state["configs"]!;
            Assert.True(c.Value<bool>("ok"));
            Assert.Equal((1, 2, 1), (c.Value<int>("added"), c.Value<int>("changed"), c.Value<int>("same")));
            var files = FilesOf(state);
            Assert.Equal("added", files["Azumatt.AzuCraftyBoxes.yml"].Value<string>("state"));
            Assert.Equal("changed", files["Jotunn/jotunn.cfg"].Value<string>("state"));
            // CRLF against LF, and no newline at the end, is the same file.
            Assert.Equal("same", files["BepInEx.cfg"].Value<string>("state"));
            Assert.False(files["BepInEx.cfg"].Value<bool>("ticked"));
            Assert.True(files["Jotunn/jotunn.cfg"].Value<bool>("ticked"));
            Assert.Equal("BepInEx/config/Jotunn/jotunn.cfg", files["Jotunn/jotunn.cfg"].Value<string>("realmPath"));
            var preview = files["Jotunn/jotunn.cfg"]["preview"]!.Select(l => l.Value<string>("k") + l.Value<string>("t")).ToList();
            Assert.Contains("-Speed = 1", preview);
            Assert.Contains("+Speed = 2", preview);
            Assert.False(files.ContainsKey("mine.cfg"), "a config the profile does not carry is listed");

            // One zip fetch, to the service, and opening the part again fetches nothing more.
            Assert.Single(Handler.Requests, r => r == "https://gale.kesomannen.com/api/profile/" + Code);
            await Configs();
            Assert.Single(Handler.Requests, r => !r.EndsWith("/meta", StringComparison.Ordinal));
            await Configs(refresh: true);
            Assert.Equal(2, Handler.Requests.Count(r => !r.EndsWith("/meta", StringComparison.Ordinal)));

            // The box learns there is config news, for its Review link.
            var box = await Call("gale.get", new { profile = Profile });
            Assert.Equal(3, box["check"]!.Value<int>("configChanges"));
        }

        [Fact]
        public async Task Before_the_configs_part_is_opened_the_box_does_not_know_and_the_check_fetches_no_zip()
        {
            await FollowWithConfigs();
            var box = await Call("gale.get", new { profile = Profile });
            Assert.Equal(JTokenType.Null, box["check"]!["configChanges"]!.Type);
            await Check();
            Assert.All(Handler.Requests, r => Assert.EndsWith("/meta", r));
        }

        [Fact]
        public async Task Apply_writes_only_the_ticked_files_byte_for_byte_backs_up_what_it_replaces_and_remembers_the_unticked()
        {
            await FollowWithConfigs();
            await Configs();

            var applied = await Apply("Azumatt.AzuCraftyBoxes.yml", "Jotunn/jotunn.cfg");

            Assert.True(applied.Value<bool>("ok"), applied.ToString());
            Assert.Equal(2, applied.Value<int>("written"));
            Assert.Equal((1, 1), (applied.Value<int>("added"), applied.Value<int>("replaced")));

            // The profile's bytes exactly: LF as the profile has them, not the realm's CRLF.
            Assert.Equal("[General]\nSpeed = 2\n", RealmText("Jotunn/jotunn.cfg"));
            Assert.Equal("range: 20\n", RealmText("Azumatt.AzuCraftyBoxes.yml"));
            // Not ticked, not touched; not in the profile, not touched; the same, not written.
            Assert.Equal("b = 1\r\n", RealmText("second.cfg"));
            Assert.Equal("host = mine\r\n", RealmText("mine.cfg"));
            Assert.Equal("[Logging]\r\nEnabled = true\r\n", RealmText("BepInEx.cfg"));

            // The replaced file, as it was, under one stamped folder; nothing for the new one.
            var stamps = Directory.GetDirectories(BackupRoot);
            var stamp = Assert.Single(stamps);
            Assert.Matches(@"^\d{8}-\d{6}$", Path.GetFileName(stamp));
            Assert.Equal("BepInEx/" + BlendWindow.GaleConfigBackupFolder + "/" + Path.GetFileName(stamp), applied.Value<string>("backup"));
            Assert.Equal("[General]\r\nSpeed = 1\r\n", File.ReadAllText(Path.Combine(stamp, "Jotunn", "jotunn.cfg")));
            Assert.Single(Directory.GetFiles(stamp, "*", SearchOption.AllDirectories));

            // The one offered and left unticked is remembered, and shown unticked next time.
            Assert.Equal(new[] { "second.cfg" }, Stored().GaleConfigUnticked);
            var after = FilesOf(await Configs());
            Assert.False(after["second.cfg"].Value<bool>("ticked"));
            Assert.Equal("same", after["Jotunn/jotunn.cfg"].Value<string>("state"));

            // Ticking it in a later apply takes it off the list and writes it, with a second backup folder.
            var again = await Apply("second.cfg");
            Assert.True(again.Value<bool>("ok"));
            Assert.Equal("b = 2\n", RealmText("second.cfg"));
            Assert.Empty(Stored().GaleConfigUnticked);
            Assert.Equal(2, Directory.GetDirectories(BackupRoot).Length);
        }

        [Fact]
        public async Task Apply_with_nothing_ticked_writes_nothing_and_stores_the_unticks()
        {
            await FollowWithConfigs();
            await Configs();
            var applied = await Apply();
            Assert.True(applied.Value<bool>("ok"));
            Assert.Equal(0, applied.Value<int>("written"));
            Assert.Equal(new[] { "Azumatt.AzuCraftyBoxes.yml", "Jotunn/jotunn.cfg", "second.cfg" }, Stored().GaleConfigUnticked.OrderBy(x => x, StringComparer.Ordinal));
            Assert.False(File.Exists(Path.Combine(ConfigDir, "Azumatt.AzuCraftyBoxes.yml")));
            Assert.False(Directory.Exists(BackupRoot));
        }

        [Fact]
        public async Task Apply_refuses_a_running_server_another_realm_and_configs_it_has_not_loaded()
        {
            await FollowWithConfigs();

            var unloaded = await Apply("Jotunn/jotunn.cfg");
            Assert.Equal("stale", unloaded.Value<string>("Reason"));

            await Configs();
            PlantRunning(Profile);
            var held = await Apply("Jotunn/jotunn.cfg");
            Assert.False(held.Value<bool>("ok"));
            Assert.Equal("serverRunning", held.Value<string>("Reason"));
            Assert.Equal(Profile, held["ErrorParams"]!.Value<string>("name"));
            Assert.Equal(Profile, (await Configs())["heldBy"]!.Value<string>());
            Sessions().Clear();

            var other = await Call("gale.configs.apply", new { profile = "Some Other Realm", paths = new[] { "Jotunn/jotunn.cfg" } });
            Assert.Equal("realmChanged", other.Value<string>("Reason"));

            Assert.Equal("[General]\r\nSpeed = 1\r\n", RealmText("Jotunn/jotunn.cfg"));
            Assert.Empty(Stored().GaleConfigUnticked);
            Assert.False(Directory.Exists(BackupRoot));
        }

        [Fact]
        public async Task Apply_refuses_a_path_the_profile_does_not_carry_and_one_that_leads_out_through_a_junction()
        {
            var outside = Path.Combine(Root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "x.cfg"), "theirs");
            await FollowWithConfigs();
            InstallIsolationService.CreateJunctionOrThrow(Path.Combine(ConfigDir, "link"), outside);
            Handler.ZipBody = GaleProfileConfigsTests.Zip(("config/link/x.cfg", "ours"), ("config/second.cfg", "b = 2\n"));
            var listed = await Configs(refresh: true);
            Assert.False(FilesOf(listed).ContainsKey("link/x.cfg"), "a file behind a junction is offered");
            Assert.Equal(1, listed["configs"]!.Value<int>("skipped"));

            foreach (var path in new[] { "../evil.cfg", "mine.cfg", "C:\\evil.cfg" })
            {
                var r = await Apply(path, "second.cfg");
                Assert.Equal("notInProfile", r.Value<string>("Reason"));
            }
            var through = await Apply("link/x.cfg", "second.cfg");
            Assert.Equal("unsafePath", through.Value<string>("Reason"));
            Assert.Equal("link/x.cfg", through["ErrorParams"]!.Value<string>("file"));

            // Refused whole: not even the good one went in.
            Assert.Equal("theirs", File.ReadAllText(Path.Combine(outside, "x.cfg")));
            Assert.Equal("b = 1\r\n", RealmText("second.cfg"));
            Assert.False(Directory.Exists(Path.Combine(Root, "evil.cfg")));
        }

        [Fact]
        public async Task A_bad_zip_is_one_sentence_and_nothing_to_apply()
        {
            await FollowWithConfigs();
            Handler.ZipBody = GaleProfileConfigsTests.Zip(("config/../../evil.cfg", "x"));
            var state = await Configs(refresh: true);
            Assert.False(state["configs"]!.Value<bool>("ok"));
            Assert.Equal(GaleProfileConfigs.UnsafeSentence, state["configs"]!.Value<string>("statusId"));
            Assert.Equal("stale", (await Apply("second.cfg")).Value<string>("Reason"));

            Handler.ZipStatus = HttpStatusCode.NotFound;
            var gone = await Configs(refresh: true);
            Assert.Equal("mods.gale.configs.fetch.not_found", gone["configs"]!.Value<string>("statusId"));
        }

        [Fact]
        public async Task Forget_and_a_new_code_clear_the_config_unticks_and_the_fetched_files()
        {
            await FollowWithConfigs();
            await Configs();
            await Apply();
            Assert.NotEmpty(Stored().GaleConfigUnticked);

            await Call("gale.forget", new { profile = Profile });
            Assert.Empty(Stored().GaleConfigUnticked);
            Assert.Equal(JTokenType.Null, (await Configs())["configs"]!.Type);
            Assert.Equal(BlendWindow.GaleConfigsNotFollowingSentence, (await Configs()).Value<string>("statusId"));

            await Follow();
            await Check();
            await Configs();
            await Apply();
            Assert.NotEmpty(Stored().GaleConfigUnticked);
            var set = await Call("gale.set", new { profile = Profile, id = "zz99ZZ" });
            Assert.True(set.Value<bool>("ok"));
            Assert.Empty(Stored().GaleConfigUnticked);
        }

        [Fact]
        public void A_new_realm_does_not_carry_the_config_unticks_and_an_empty_list_is_written_as_nothing()
        {
            var basePrefs = new ServerPreferences { ProfileName = "Base", GaleConfigUnticked = new List<string> { "a.cfg", " A.cfg ", "" } };
            Assert.Equal(new[] { "a.cfg" }, basePrefs.ToFile().GaleConfigUnticked);
            Assert.Equal(new[] { "a.cfg" }, ServerPreferences.FromFile(basePrefs.ToFile()).GaleConfigUnticked);
            Assert.Empty(BlendWindow.SeedNewRealm(basePrefs).GaleConfigUnticked);
            Assert.Null(new ServerPreferences().ToFile().GaleConfigUnticked);
            Assert.Empty(ServerPreferences.FromFile(new ServerPreferencesFile()).GaleConfigUnticked);
            // A settings save that comes back with no Gale fields keeps what is on disk.
            Assert.Equal(new[] { "a.cfg" }, new ServerPreferences().KeepGaleFollowFrom(basePrefs).GaleConfigUnticked);
        }

        // ------------------------------------------------------------------ on this server only (1.2.16)

        [Fact]
        public async Task Server_only_mods_carry_the_scans_FullName_and_their_off_state_and_the_switch_finds_them_by_it()
        {
            // A folder with no author: the diff names it LooseMod, the scan's FullName is -LooseMod.
            var loose = Path.Combine(Plugins, "LooseMod");
            Directory.CreateDirectory(loose);
            File.WriteAllText(Path.Combine(loose, "LooseMod.dll"), "MZ");
            Install("Someone-OnlyHere", "3.0.0");
            File.WriteAllText(Path.Combine(Plugins, "Someone-OnlyHere", "OnlyHere.dll"), "MZ");

            Handler.Body = WorkMeta(Stamp1);
            await Follow();
            var check = (JObject)(await Check())["check"]!;

            Assert.Equal(2, check.Value<int>("onlyHereCount"));
            var rows = check["rows"]!.Where(r => r.Value<string>("kind") == "notInProfile").ToDictionary(r => r.Value<string>("name")!);
            Assert.Equal(new[] { "LooseMod", "Someone-OnlyHere" }, rows.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal("-LooseMod", rows["LooseMod"].Value<string>("fullName"));
            Assert.Equal("Someone-OnlyHere", rows["Someone-OnlyHere"].Value<string>("fullName"));
            Assert.False(rows["LooseMod"].Value<bool>("off"));
            // Rows the server does not have carry no FullName.
            Assert.Null(check["rows"]!.Single(r => r.Value<string>("name") == "Azumatt-AzuCraftyBoxes").Value<string>("fullName"));

            // The switch the list sends, through the real mods.disable, by the FullName the row carried.
            var off = await Call("mods.disable", new { profile = Profile, fullName = rows["LooseMod"].Value<string>("fullName") });
            Assert.True(off.Value<bool>("ok"), off.ToString());
            Assert.True(File.Exists(Path.Combine(loose, "LooseMod.dll.old")));

            var rescanned = await Call("gale.rescan", new { profile = Profile });
            var after = rescanned["state"]!["check"]!["rows"]!.Single(r => r.Value<string>("name") == "LooseMod");
            Assert.True(after.Value<bool>("off"), "the list does not show the mod as off after the switch");
            Assert.Equal("notInProfile", after.Value<string>("kind"));
            // Nothing was removed and Gale was not asked again.
            Assert.True(Directory.Exists(loose));
            Assert.Single(Handler.Requests);
        }

        [Fact]
        public async Task The_framework_row_is_not_a_server_only_mod()
        {
            Handler.Body = Meta(Stamp1, ("denikson-BepInExPack_Valheim", true, 5, 4, 2202), ("ValheimModding-Jotunn", true, 2, 20, 1));
            await Follow();
            var check = (JObject)(await Check())["check"]!;
            Assert.Equal(0, check.Value<int>("onlyHereCount"));
            Assert.DoesNotContain(check["rows"]!, r => r.Value<string>("kind") == "notInProfile");
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>A handler that answers from a script, can be held at a gate, or can stall past the bound.</summary>
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            public HttpStatusCode Status = HttpStatusCode.OK;
            public string Body = "";
            public bool Stall;
            public TaskCompletionSource<bool> Gate;
            public bool RanOnPoolThread;
            /// <summary>What the profile's own address answers: its zip, straight from the service.</summary>
            public byte[] ZipBody = Array.Empty<byte>();
            public HttpStatusCode ZipStatus = HttpStatusCode.OK;
            public List<string> Requests { get; } = new();
            public int Count => Requests.Count;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                lock (Requests) Requests.Add(request.RequestUri?.ToString() ?? "");
                Assert.Equal(GaleSyncClient.Host, request.RequestUri?.Host);
                RanOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
                if (Gate != null) await Gate.Task;
                if (Stall) await Task.Delay(TimeSpan.FromSeconds(30), token);
                if (!(request.RequestUri?.AbsolutePath ?? "").EndsWith("/meta", StringComparison.Ordinal))
                    return new HttpResponseMessage(ZipStatus) { Content = new ByteArrayContent(ZipBody ?? Array.Empty<byte>()) };
                return new HttpResponseMessage(Status) { Content = new StringContent(Body ?? "", Encoding.UTF8, "application/json") };
            }
        }

        private sealed class HandlerProvider : IHttpClientProvider
        {
            private readonly HttpMessageHandler Handler;
            public HandlerProvider(HttpMessageHandler handler) => Handler = handler;
            public HttpClient CreateClient() => new(Handler, disposeHandler: false);
        }

        /// <summary>
        /// A BlendWindow with its bridge wired over this test's providers: the Form is constructed
        /// (so it has no handle, shows nothing and posts nothing), the mod scanner is the real one,
        /// the Gale client is the real one over the fake handler, every other interface is a loose
        /// mock, and the RPC table is the real one.
        /// </summary>
        private static (BlendWindow, Dictionary<string, Func<JObject, Task<object>>>) BuildWindow(
            IUserPreferencesProvider userPrefs, IGaleSyncClient gale, Dictionary<Type, object> extra)
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
                [typeof(IGaleSyncClient)] = gale,
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
