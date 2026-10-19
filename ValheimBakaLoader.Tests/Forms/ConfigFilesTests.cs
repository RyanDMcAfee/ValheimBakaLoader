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
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The Configs hall's files, 1.2.15 (issue 20). The hall used to list the top level of
    /// BepInEx/config and only its .cfg files, and refused any name with a slash in it. Mods keep
    /// folders and .yaml, .json and .md files there now, so the list walks down and the paths it
    /// hands the page are relative ones. A relative path from the page is a path that can try to
    /// walk out, so every read and write goes back through ConfigFiles.Resolve, and these tests
    /// are about that door: what it lets through, what it refuses, and that a save can never make
    /// a new file or reach outside the folder.
    /// <para>
    /// The first half drives ConfigFiles over a temp folder. The second drives the real RPC table
    /// (config.list, config.read, config.write and mods.configs) over a temp install, with the
    /// Form built without a handle the way ItemsSearchRpcTests builds it, so no window is shown.
    /// </para>
    /// </summary>
    public class ConfigFilesTests : IDisposable
    {
        private readonly string Root;
        private readonly string ConfigDir;
        private readonly string Outside;

        public ConfigFilesTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-config-files-" + Guid.NewGuid().ToString("N"));
            var serverDir = Path.Combine(Root, "server");
            ConfigDir = Path.Combine(serverDir, "BepInEx", "config");
            Outside = Path.Combine(Root, "outside");
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(Outside);
            File.WriteAllText(Path.Combine(serverDir, "valheim_server.exe"), "MZ");

            Write("BepInEx.cfg");
            Write("com.someModAuthor.ModName.DefaultSettings.cfg");
            Write("ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg");
            Write("ModNameData/com.someModAuthor.ModName.CustomList.yaml");
            Write("ModNameData/notes.md");
            Write("Other/one.yml");
            Write("Other/two.json");
            Write("Deep/a/b/deep.cfg");          // three folders down
            Write("Other/readme.txt");           // not a kind the hall shows
            Write("Other/plugin.dll");
            File.WriteAllText(Path.Combine(Outside, "evil.cfg"), "outside");
            File.WriteAllText(Path.Combine(Root, "server", "BepInEx", "outside.cfg"), "beside config");
        }

        public void Dispose()
        {
            try { DeleteTreeNoFollow(Root); } catch { /* temp folder, best effort */ }
        }

        private void Write(string relative, string text = null)
        {
            var full = Path.Combine(ConfigDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text ?? ("text of " + relative));
        }

        // ------------------------------------------------------------------ the list

        [Fact]
        public void The_list_walks_down_and_answers_relative_paths_with_forward_slashes()
        {
            var list = ConfigFiles.List(ConfigDir);
            Assert.Equal(new[]
            {
                "BepInEx.cfg",
                "com.someModAuthor.ModName.DefaultSettings.cfg",
                "Deep/a/b/deep.cfg",
                "ModNameData/com.someModAuthor.ModName.CustomList.yaml",
                "ModNameData/notes.md",
                "ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg",
                "Other/one.yml",
                "Other/two.json",
            }, list);
            Assert.DoesNotContain(list, f => f.Contains('\\'));
        }

        [Fact]
        public void The_list_shows_exactly_the_five_kinds_the_issue_named()
        {
            var list = ConfigFiles.List(ConfigDir);
            Assert.DoesNotContain("Other/readme.txt", list);
            Assert.DoesNotContain("Other/plugin.dll", list);
            foreach (var ext in new[] { ".cfg", ".yaml", ".yml", ".json", ".md" })
                Assert.Contains(list, f => f.EndsWith(ext, StringComparison.Ordinal));
        }

        [Fact]
        public void The_list_does_not_follow_a_junction_out_of_the_folder()
        {
            Assert.True(MakeJunction(Path.Combine(ConfigDir, "link"), Outside), "a directory junction could not be made in the temp folder");
            var list = ConfigFiles.List(ConfigDir);
            Assert.DoesNotContain(list, f => f.StartsWith("link/", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void A_missing_folder_lists_nothing()
        {
            Assert.Empty(ConfigFiles.List(Path.Combine(Root, "nowhere")));
            Assert.Empty(ConfigFiles.List(null));
        }

        // ------------------------------------------------------------------ the door

        [Theory]
        [InlineData("BepInEx.cfg")]
        [InlineData("ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg")]
        [InlineData("ModNameFolder\\com.someModAuthor.ModName.ExtraSettings.cfg")]
        [InlineData("ModNameData/com.someModAuthor.ModName.CustomList.yaml")]
        [InlineData("ModNameData/notes.md")]
        [InlineData("Other/one.yml")]
        [InlineData("Other/two.json")]
        [InlineData("Deep/a/b/deep.cfg")]
        public void Every_listed_kind_and_depth_resolves_inside_the_folder(string relative)
        {
            var full = ConfigFiles.Resolve(ConfigDir, relative);
            Assert.StartsWith(Path.GetFullPath(ConfigDir) + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(full));
        }

        [Theory]
        [InlineData("bepinex.CFG")]
        [InlineData("modnamefolder/COM.SOMEMODAUTHOR.MODNAME.EXTRASETTINGS.Cfg")]
        [InlineData("OTHER/Two.JSON")]
        public void Case_is_ignored_the_way_Windows_ignores_it(string relative)
        {
            Assert.True(File.Exists(ConfigFiles.Resolve(ConfigDir, relative)));
        }

        [Theory]
        [InlineData("../outside.cfg")]
        [InlineData("..\\outside.cfg")]
        [InlineData("Other/../../outside.cfg")]
        [InlineData("Other\\..\\..\\outside.cfg")]
        [InlineData("Other/../BepInEx.cfg")]
        [InlineData("./BepInEx.cfg")]
        [InlineData("Other//two.json")]
        [InlineData("../../outside/evil.cfg")]
        public void A_path_that_walks_is_refused(string relative)
        {
            Assert.Throws<ArgumentException>(() => ConfigFiles.Resolve(ConfigDir, relative));
        }

        [Fact]
        public void A_rooted_path_is_refused_whatever_shape_it_takes()
        {
            var evil = Path.Combine(Outside, "evil.cfg");
            foreach (var rooted in new[]
                     {
                         evil,                                   // C:\...\evil.cfg
                         "/" + "BepInEx.cfg",
                         "\\BepInEx.cfg",
                         "C:BepInEx.cfg",                        // drive relative
                         "\\\\server\\share\\evil.cfg",          // UNC
                         "//server/share/evil.cfg",
                         "\\\\?\\" + evil,                       // extended length
                     })
                Assert.Throws<ArgumentException>(() => ConfigFiles.Resolve(ConfigDir, rooted));
        }

        [Theory]
        [InlineData("Other/readme.txt")]
        [InlineData("Other/plugin.dll")]
        [InlineData("BepInEx.cfg.txt")]
        [InlineData("BepInEx")]
        public void A_kind_the_hall_does_not_show_is_refused(string relative)
        {
            Assert.Throws<ArgumentException>(() => ConfigFiles.Resolve(ConfigDir, relative));
        }

        [Fact]
        public void A_file_that_is_not_there_is_refused_rather_than_made()
        {
            Assert.Throws<FileNotFoundException>(() => ConfigFiles.Resolve(ConfigDir, "New.cfg"));
            Assert.Throws<FileNotFoundException>(() => ConfigFiles.Resolve(ConfigDir, "NewFolder/New.cfg"));
            Assert.False(File.Exists(Path.Combine(ConfigDir, "New.cfg")));
            Assert.False(Directory.Exists(Path.Combine(ConfigDir, "NewFolder")));
        }

        [Fact]
        public void A_junction_inside_the_folder_cannot_carry_a_path_out_of_it()
        {
            Assert.True(MakeJunction(Path.Combine(ConfigDir, "link"), Outside), "a directory junction could not be made in the temp folder");
            Assert.True(File.Exists(Path.Combine(ConfigDir, "link", "evil.cfg")), "the junction did not land");
            Assert.Throws<ArgumentException>(() => ConfigFiles.Resolve(ConfigDir, "link/evil.cfg"));
        }

        [Fact]
        public void A_config_folder_that_is_itself_a_junction_still_works()
        {
            // An isolated install can share its config folder through a junction, and that is the
            // folder the hall is about, so only the steps BELOW it are held against a path.
            var linked = Path.Combine(Root, "linked-config");
            Assert.True(MakeJunction(linked, ConfigDir), "a directory junction could not be made in the temp folder");
            Assert.True(File.Exists(ConfigFiles.Resolve(linked, "ModNameData/notes.md")));
            Assert.Contains("ModNameData/notes.md", ConfigFiles.List(linked));
        }

        // ------------------------------------------------------------------ the matcher

        [Fact]
        public void The_matcher_finds_the_issues_three_files_for_their_mod()
        {
            var files = ConfigFiles.MatchForMod(ConfigFiles.List(ConfigDir), "ModName", "someModAuthor", "ModName",
                new[] { "someModAuthor-ModName" });
            Assert.Equal(new[]
            {
                "com.someModAuthor.ModName.DefaultSettings.cfg",
                "ModNameData/com.someModAuthor.ModName.CustomList.yaml",
                "ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg",
            }, files);
        }

        [Fact]
        public void The_matcher_ignores_case_extension_and_the_three_separators()
        {
            var files = new[] { "shudnal.ExtraSlots.cfg", "extra_slots.yaml", "Extra-Slots Custom.json", "Other.cfg" };
            Assert.Equal(new[] { "Extra-Slots Custom.json", "extra_slots.yaml", "shudnal.ExtraSlots.cfg" },
                ConfigFiles.MatchForMod(files, "Extra Slots", "shudnal", null, null));
        }

        [Fact]
        public void The_author_and_name_pair_matches_on_its_own()
        {
            // A name too short to match alone still matches as part of its author.name pair.
            var files = new[] { "com.jere.ab.cfg", "lab.cfg" };
            Assert.Equal(new[] { "com.jere.ab.cfg" }, ConfigFiles.MatchForMod(files, "Ab", "Jere", null, null));
        }

        [Fact]
        public void The_manifest_name_matches_when_the_folder_name_differs()
        {
            var files = new[] { "WorldEditCommands.cfg", "server_devcommands.cfg" };
            Assert.Equal(new[] { "server_devcommands.cfg" },
                ConfigFiles.MatchForMod(files, "ServerDevcommands_Fork", "JereKuusela", "Server_devcommands", null));
        }

        [Fact]
        public void A_file_under_the_mods_own_package_folder_belongs_whatever_it_is_called()
        {
            var files = new[] { "someModAuthor-ModName/settings.json", "elsewhere/someModAuthor-ModName.txt.md", "settings.json" };
            Assert.Equal(new[] { "someModAuthor-ModName/settings.json" },
                ConfigFiles.MatchForMod(files, "Zzz", "Qqq", null, new[] { "someModAuthor-ModName" }));
        }

        [Fact]
        public void A_name_of_one_or_two_letters_matches_nothing_on_its_own()
        {
            Assert.Empty(ConfigFiles.MatchForMod(new[] { "ab.cfg", "cab.cfg" }, "Ab", null, null, null));
        }

        // ------------------------------------------------------------------ the RPCs

        [Fact]
        public async Task Config_list_over_the_real_table_answers_the_walk()
        {
            var (window, rpc) = BuildWindow(ExePath());
            using (window)
            {
                var list = JArray.Parse(JsonConvert.SerializeObject(await rpc["config.list"](new JObject())))
                    .Select(t => t.Value<string>()).ToList();
                Assert.Equal(ConfigFiles.List(ConfigDir), list);
            }
        }

        [Fact]
        public async Task Config_read_and_write_reach_a_file_in_a_subfolder()
        {
            var (window, rpc) = BuildWindow(ExePath());
            using (window)
            {
                const string rel = "ModNameData/com.someModAuthor.ModName.CustomList.yaml";
                Assert.Equal("text of " + rel, (string)await rpc["config.read"](JObject.FromObject(new { file = rel })));
                await rpc["config.write"](JObject.FromObject(new { file = rel, text = "edited: true" }));
                Assert.Equal("edited: true", File.ReadAllText(Path.Combine(ConfigDir, "ModNameData", "com.someModAuthor.ModName.CustomList.yaml")));
            }
        }

        [Fact]
        public async Task Config_write_cannot_make_a_file()
        {
            var (window, rpc) = BuildWindow(ExePath());
            using (window)
            {
                await Assert.ThrowsAsync<FileNotFoundException>(() =>
                    rpc["config.write"](JObject.FromObject(new { file = "Brand new.cfg", text = "x" })));
                await Assert.ThrowsAsync<FileNotFoundException>(() =>
                    rpc["config.write"](JObject.FromObject(new { file = "Fresh/Brand new.json", text = "x" })));
                Assert.False(File.Exists(Path.Combine(ConfigDir, "Brand new.cfg")));
                Assert.False(Directory.Exists(Path.Combine(ConfigDir, "Fresh")));
            }
        }

        [Theory]
        [InlineData("../outside.cfg")]
        [InlineData("..\\outside.cfg")]
        [InlineData("Other/../../outside.cfg")]
        public async Task Config_write_cannot_reach_outside_the_config_folder(string relative)
        {
            var beside = Path.Combine(Root, "server", "BepInEx", "outside.cfg");
            var (window, rpc) = BuildWindow(ExePath());
            using (window)
            {
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    rpc["config.write"](JObject.FromObject(new { file = relative, text = "pwned" })));
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    rpc["config.write"](JObject.FromObject(new { file = Path.Combine(Outside, "evil.cfg"), text = "pwned" })));
                Assert.Equal("beside config", File.ReadAllText(beside));
                Assert.Equal("outside", File.ReadAllText(Path.Combine(Outside, "evil.cfg")));
            }
        }

        [Fact]
        public async Task Config_write_cannot_follow_a_junction_out()
        {
            Assert.True(MakeJunction(Path.Combine(ConfigDir, "link"), Outside), "a directory junction could not be made in the temp folder");
            var (window, rpc) = BuildWindow(ExePath());
            using (window)
            {
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    rpc["config.write"](JObject.FromObject(new { file = "link/evil.cfg", text = "pwned" })));
                Assert.Equal("outside", File.ReadAllText(Path.Combine(Outside, "evil.cfg")));
            }
        }

        [Fact]
        public async Task Mods_configs_answers_the_files_that_carry_the_mods_name()
        {
            var plugins = Path.Combine(Root, "server", "BepInEx", "plugins", "someModAuthor-ModName");
            Directory.CreateDirectory(plugins);
            File.WriteAllText(Path.Combine(plugins, "ModName.dll"), "MZ");
            File.WriteAllText(Path.Combine(plugins, "manifest.json"),
                "{\"name\":\"ModName\",\"version_number\":\"1.0.0\",\"website_url\":\"\",\"description\":\"\",\"dependencies\":[]}");

            var (window, rpc) = BuildWindow(ExePath());
            using (window)
            {
                var reply = JObject.Parse(JsonConvert.SerializeObject(
                    await rpc["mods.configs"](JObject.FromObject(new { fullName = "someModAuthor-ModName" }))));
                Assert.Equal(new[]
                {
                    "com.someModAuthor.ModName.DefaultSettings.cfg",
                    "ModNameData/com.someModAuthor.ModName.CustomList.yaml",
                    "ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg",
                }, reply["files"]!.Select(t => t.Value<string>()).ToArray());

                // A question about another realm is answered with nothing, not with this one's files.
                var stale = JObject.Parse(JsonConvert.SerializeObject(
                    await rpc["mods.configs"](JObject.FromObject(new { fullName = "someModAuthor-ModName", profile = "Some Other Realm" }))));
                Assert.Empty(stale["files"]!);
                Assert.True(stale.Value<bool>("stale"));
            }
        }

        // ------------------------------------------------------------------ the chip's look

        [Fact]
        public void A_realms_look_round_trips_through_the_file_shape()
        {
            var prefs = new ServerPreferences { ProfileName = "Looked", LookRune = "fehu", LookColor = "moss" };
            var back = ServerPreferences.FromFile(JsonConvert.DeserializeObject<ServerPreferencesFile>(
                JsonConvert.SerializeObject(prefs.ToFile())));
            Assert.Equal("fehu", back.LookRune);
            Assert.Equal("moss", back.LookColor);

            var plain = ServerPreferences.FromFile(JsonConvert.DeserializeObject<ServerPreferencesFile>(
                JsonConvert.SerializeObject(new ServerPreferences { ProfileName = "Plain", LookRune = "  " }.ToFile())));
            Assert.Null(plain.LookRune);
            Assert.Null(plain.LookColor);
            Assert.DoesNotContain("lookRune\":\"", JsonConvert.SerializeObject(new ServerPreferences { LookRune = " " }.ToFile()));
        }

        [Fact]
        public async Task A_look_saved_through_profiles_save_reaches_the_chip_strip()
        {
            var (window, rpc) = BuildWindow(ExePath(), withProfile: "Looked");
            using (window)
            {
                var cur = JObject.FromObject(await rpc["profiles.get"](JObject.FromObject(new { name = "Looked" })));
                cur["LookRune"] = "raido";
                cur["LookColor"] = "cold";
                await rpc["profiles.save"](JObject.FromObject(new { name = "Looked", prefs = cur }));

                var strip = JArray.Parse(JsonConvert.SerializeObject(await rpc["servers.list"](new JObject())));
                var chip = strip.Single(t => t.Value<string>("name") == "Looked");
                Assert.Equal("raido", chip.Value<string>("lookRune"));
                Assert.Equal("cold", chip.Value<string>("lookColor"));

                // And a plain settings save, which fetches the profile and sends the whole of it
                // back, keeps the look it did not touch.
                var again = JObject.FromObject(await rpc["profiles.get"](JObject.FromObject(new { name = "Looked" })));
                again["Name"] = "Renamed in the form";
                await rpc["profiles.save"](JObject.FromObject(new { name = "Looked", prefs = again }));
                var after = JObject.FromObject(await rpc["profiles.get"](JObject.FromObject(new { name = "Looked" })));
                Assert.Equal("raido", after.Value<string>("LookRune"));
                Assert.Equal("cold", after.Value<string>("LookColor"));
            }
        }

        // ------------------------------------------------------------------ harness

        private string ExePath() => Path.Combine(Root, "server", "valheim_server.exe");

        /// <summary>
        /// A directory junction made the way Windows makes one for anybody (no administrator and
        /// no developer mode needed), through cmd with no window. Answers false when it could not
        /// be made, and every caller asserts it was: a junction is the case those tests are about,
        /// so one that quietly did not land would be a test proving nothing.
        /// </summary>
        private static bool MakeJunction(string link, string target)
        {
            try
            {
                var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using var p = Process.Start(start)!;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return Directory.Exists(link)
                    && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return false;
            }
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

        private static (BlendWindow, Dictionary<string, Func<JObject, Task<object>>>) BuildWindow(
            string exe, string withProfile = null)
        {
            var userPrefs = new RoundTrippingUserPrefs();
            ((IUserPreferencesProvider)userPrefs).Mutate(prefs =>
            {
                prefs.ServerExePath = exe;
                prefs.LogsFolderPath = MockUserPreferencesProvider.TestLogsFolder;
                if (withProfile != null)
                    prefs.Servers.Add(new ServerPreferences { ProfileName = withProfile, Name = withProfile, WorldName = withProfile });
            });

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
