using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ValheimBakaLoader.Service.Catalog;

/// <summary>
/// The service's own reader of the app's catalog, for the few sentences it writes before
/// the page's scripts exist: the login page. English comes from the en.json it serves,
/// so the words live in one file with everything else and the copy gate holds them to the
/// same rules; another language comes from the pack installed for it, the same
/// lang/&lt;code&gt;/&lt;version&gt;/strings.json the page reads, with English behind it.
/// The call shape is the one the catalog gate recognises, which is why the name matches
/// the app's class even though this is a separate, smaller thing.
/// </summary>
public sealed class HostCatalog
{
    private static readonly Regex Slot = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly HostCatalog Empty = new("en", new Dictionary<string, JObject>(), null);

    private readonly Dictionary<string, JObject> _entries;
    private readonly HostCatalog? _english;

    public static HostCatalog Current { get; private set; } = Empty;

    public string Code { get; }

    private HostCatalog(string code, Dictionary<string, JObject> entries, HostCatalog? english)
    {
        Code = code;
        _entries = entries;
        _english = english;
    }

    public static void Use(HostCatalog catalog) => Current = catalog ?? Empty;

    /// <summary>The sentence for an id in the language in use, with its named values filled in.</summary>
    public static string T(string id, params (string Name, object? Value)[] args) => Current.Say(id, args);

    /// <summary>
    /// Reads English out of the served WebUI folder and, for another language, the newest
    /// pack installed for it. A missing or damaged file answers English, and missing
    /// English answers the id: the login page must come up whatever is on disk.
    /// </summary>
    public static HostCatalog Load(string webUiDir, string langDir, string? code)
    {
        var english = ReadFile(Path.Combine(webUiDir, "i18n", "en.json"));
        var englishCatalog = new HostCatalog("en", english, null);

        var wanted = (code ?? "en").Trim().ToLowerInvariant();
        if (wanted.Length == 0 || wanted == "en") return englishCatalog;

        var pack = NewestPack(Path.Combine(langDir, wanted));
        if (pack == null) return englishCatalog;

        var entries = ReadFile(pack);
        return entries.Count == 0 ? englishCatalog : new HostCatalog(wanted, entries, englishCatalog);
    }

    public string Say(string id, params (string Name, object? Value)[] args)
    {
        var text = Words(id);
        if (text == null) return id;
        return Slot.Replace(text, m =>
        {
            foreach (var (name, value) in args)
                if (name == m.Groups[1].Value) return value?.ToString() ?? "";
            return m.Value;
        });
    }

    public bool Has(string id) => Words(id) != null;

    private string? Words(string id)
    {
        if (_entries.TryGetValue(id, out var mine))
        {
            var text = Resolve(mine);
            if (text != null) return text;
        }
        return _english?.Words(id);
    }

    private static string? Resolve(JObject entry)
    {
        var value = entry["translation"] ?? entry["lore"];
        if (value == null || value.Type == JTokenType.Null) return null;
        // A plural object has no place on the login page; its "other" form is the sane read.
        if (value.Type == JTokenType.Object) return value["other"]?.ToString();
        return value.ToString();
    }

    private static Dictionary<string, JObject> ReadFile(string path)
    {
        var kept = new Dictionary<string, JObject>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return kept;
            var keys = JObject.Parse(File.ReadAllText(path, Encoding.UTF8))["keys"] as JObject;
            if (keys == null) return kept;
            foreach (var pair in keys)
                if (pair.Value is JObject entry) kept[pair.Key] = entry;
        }
        catch (Exception)
        {
            kept.Clear();
        }
        return kept;
    }

    private static string? NewestPack(string languageDir)
    {
        if (!Directory.Exists(languageDir)) return null;
        string? best = null;
        Version? bestVersion = null;
        foreach (var dir in Directory.GetDirectories(languageDir))
        {
            var strings = Path.Combine(dir, "strings.json");
            if (!File.Exists(strings)) continue;
            if (!Version.TryParse(Path.GetFileName(dir), out var version)) continue;
            if (bestVersion != null && version <= bestVersion) continue;
            best = strings;
            bestVersion = version;
        }
        return best;
    }
}
