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
    /// items.search, driven through the real RPC table over a catalog written to a temp
    /// BepInEx folder. The RPC used to filter the catalog in file order and take the first
    /// rows, so a typed "Iron" could lose the plain Iron off the end of the list. No window is
    /// shown: the Form is built without a handle, the way BlendWindowGaleTests builds one.
    /// </summary>
    public class ItemsSearchRpcTests : IDisposable
    {
        private readonly string Root;
        private readonly string BepInExDir;
        private readonly RoundTrippingUserPrefs UserPrefs = new();
        private readonly ItemCatalog Catalog = new(new Mock<IApplicationLogger>().Object);
        private readonly BlendWindow Window;
        private readonly Dictionary<string, Func<JObject, Task<object>>> Rpc;

        public ItemsSearchRpcTests()
        {
            Root = Path.Combine(Path.GetTempPath(), "vbl-items-search-" + Guid.NewGuid().ToString("N"));
            var serverDir = Path.Combine(Root, "server");
            BepInExDir = Path.Combine(serverDir, "BepInEx");
            Directory.CreateDirectory(BepInExDir);
            var exe = Path.Combine(serverDir, "valheim_server.exe");
            File.WriteAllText(exe, "MZ");

            // Written out of order, with the exact name last, so file order and ranked order differ.
            var items = new (string Prefab, string Name)[]
            {
                ("Wood", "Wood"),
                ("ScrapMetal", "Scrap iron"),
                ("IronOre", "IronOre"),
                ("ArrowIron", "Ironhead arrow"),
                ("ArmorIronChest", "Iron Armor"),
                ("Iron", "Iron"),
            };
            File.WriteAllText(Path.Combine(BepInExDir, "items.json"), "[" + string.Join(",", items.Select(i =>
                "{\"prefab\":\"" + i.Prefab + "\",\"name\":\"" + i.Name + "\",\"category\":\"Item\"}")) + "]");

            ((IUserPreferencesProvider)UserPrefs).Mutate(prefs =>
            {
                prefs.ServerExePath = exe;
                prefs.LogsFolderPath = MockUserPreferencesProvider.TestLogsFolder;
            });

            (Window, Rpc) = BuildWindow(UserPrefs, Catalog);
        }

        public void Dispose()
        {
            try { Window?.Dispose(); } catch { }
            try { Directory.Delete(Root, recursive: true); } catch { /* temp folder, best effort */ }
        }

        private async Task<JObject> Search(object args)
            => JObject.Parse(JsonConvert.SerializeObject(await Rpc["items.search"](JObject.FromObject(args))));

        private static string[] Labels(JObject reply)
            => reply["results"]!.Select(r => r.Value<string>("Label")).ToArray();

        [Fact]
        public async Task A_typed_name_comes_back_exact_first_then_starts_with_then_contains()
        {
            var reply = await Search(new { query = "Iron" });

            Assert.Equal(new[] { "Iron", "Iron Armor", "Ironhead arrow", "IronOre", "Scrap iron" }, Labels(reply));
            Assert.Equal(Path.Combine(BepInExDir, "items.json"), reply.Value<string>("loadedFrom"));
        }

        /// <summary>
        /// The regression: with the cut taken in file order, a limit of one answered with
        /// whichever iron-something the file listed first. The exact name sits last in the file.
        /// </summary>
        [Fact]
        public async Task The_limit_is_applied_after_the_ranking()
        {
            Assert.Equal(new[] { "Iron" }, Labels(await Search(new { query = "Iron", limit = 1 })));
            Assert.Equal(new[] { "Iron", "Iron Armor", "Ironhead arrow" }, Labels(await Search(new { query = "iron", limit = 3 })));
        }

        [Fact]
        public async Task A_blank_query_is_every_entry_alphabetical_by_label()
        {
            Assert.Equal(
                new[] { "Iron", "Iron Armor", "Ironhead arrow", "IronOre", "Scrap iron", "Wood" },
                Labels(await Search(new { query = "" })));
            Assert.Equal(new[] { "Iron", "Iron Armor" }, Labels(await Search(new { query = "  ", limit = 2 })));
        }

        // ------------------------------------------------------------------ harness

        private static (BlendWindow, Dictionary<string, Func<JObject, Task<object>>>) BuildWindow(
            IUserPreferencesProvider userPrefs, ItemCatalog catalog)
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
                [typeof(ItemCatalog)] = catalog,
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
