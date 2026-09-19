using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The words for the sentences that never reach the page: the restart countdown a player
    /// reads in the middle of their screen, the Discord post a community reads in a channel,
    /// and the note appended to both when mods are about to be updated.
    /// <para>
    /// The interface has a catalog of its own and reads it in the browser. These sentences
    /// cannot: they are written by the server side and handed to RCON or to a webhook, and
    /// nobody is reading a browser when they arrive. So this is the same catalog, read on this
    /// side, and it is the SAME FILE: the host strings live in WebUI/i18n/en.json under
    /// <c>host.</c> and the csproj embeds that file as a resource, so one catalog holds
    /// everything and a translator is handed one document rather than two.
    /// </para>
    /// <para>
    /// And it is a different language from the interface's, on purpose. The person running the
    /// server and the people playing on it are not always reading the same one, so
    /// <see cref="UserPreferences.PlayerMessageLanguage"/> picks this one: "same" follows the
    /// interface, and anything else is a language of its own. The English embedded in the app
    /// is the floor under every one of them, so a pack that is missing a line still says that
    /// line, in English, rather than saying an id. Missing covers both shapes a pack goes
    /// short in: a key it never carried, and a key it carries with nothing inside it, which
    /// is what a half filled translation file looks like.
    /// </para>
    /// </summary>
    public sealed class HostCatalog
    {
        /// <summary>The prefix every id this catalog answers for is spelled under.</summary>
        public const string Prefix = "host.";

        /// <summary>Where the embedded English lives inside the assembly.</summary>
        internal const string EnglishResourceName = "ValheimBakaLoader.WebUI.i18n.en.json";

        private static readonly Regex Slot = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

        private readonly IReadOnlyDictionary<string, JObject> Entries;

        private HostCatalog(string code, IReadOnlyDictionary<string, JObject> entries)
        {
            Code = code;
            Entries = entries;
        }

        /// <summary>The language this catalog answers in.</summary>
        public string Code { get; }

        /// <summary>
        /// The English that ships inside the app. Never null, never replaced, and the floor
        /// under every other catalog: a pack that does not carry a line is answered from here.
        /// </summary>
        public static HostCatalog EmbeddedEnglish { get; } = ReadEmbeddedEnglish();

        /// <summary>
        /// The catalog the host-facing sentences are being written in right now. English until
        /// something sets it, which is what a machine with no pack installed stays on.
        /// </summary>
        public static HostCatalog Current { get; private set; } = EmbeddedEnglish;

        /// <summary>
        /// How many lines the English catalog holds in total, host strings and interface
        /// strings alike. It is what a pack's own count is compared against to say how many
        /// lines are still being read in English.
        /// </summary>
        public static int EnglishKeyCount { get; private set; }

        /// <summary>Starts writing the host-facing sentences in this catalog.</summary>
        public static void Use(HostCatalog catalog) => Current = catalog ?? EmbeddedEnglish;

        /// <summary>The sentence for an id, in whatever language the players are being written in.</summary>
        public static string T(string id, params (string Name, object Value)[] args) =>
            Current.Say(id, args);

        /// <summary>
        /// The catalog for a language, read out of the pack installed for it, with the
        /// embedded English behind it. Answers the English itself for English, for a language
        /// with no pack on disk, and for a pack that cannot be read: this is a lookup, and a
        /// lookup that throws would take a countdown announcement with it.
        /// </summary>
        /// <param name="languagesRoot">The folder installed packs live in.</param>
        /// <param name="code">The language wanted, already resolved from the preferences.</param>
        /// <param name="version">The pack version to read, or null for the newest on disk.</param>
        public static HostCatalog Load(string languagesRoot, string code, string version)
        {
            var normalized = LanguageCodes.Normalize(code);
            if (normalized == null || LanguageCodes.IsEnglish(normalized)) return EmbeddedEnglish;
            if (string.IsNullOrWhiteSpace(languagesRoot) || string.IsNullOrWhiteSpace(version))
                return EmbeddedEnglish;

            try
            {
                var strings = Path.Combine(languagesRoot, normalized, version, "strings.json");
                if (!File.Exists(strings)) return EmbeddedEnglish;

                var read = ReadHostKeys(File.ReadAllText(strings, Encoding.UTF8));
                return read.Count == 0 ? EmbeddedEnglish : new HostCatalog(normalized, read);
            }
            catch (Exception)
            {
                return EmbeddedEnglish;
            }
        }

        /// <summary>
        /// Which language the people on the server are written to in, given the two
        /// preferences. "same" is the interface's language; anything else is its own, and a
        /// spelling nothing answers to falls back to the interface's rather than to nothing.
        /// </summary>
        public static string EffectiveCode(string interfaceLanguage, string playerMessageLanguage)
        {
            var asked = playerMessageLanguage?.Trim();
            var forInterface = LanguageCodes.Normalize(interfaceLanguage) ?? LanguageCodes.English;

            if (string.IsNullOrEmpty(asked) ||
                string.Equals(asked, "same", StringComparison.OrdinalIgnoreCase))
            {
                return forInterface;
            }

            return LanguageCodes.Normalize(asked) ?? forInterface;
        }

        /// <summary>
        /// The sentence for an id. A slot with no value behind it is left standing rather than
        /// blanked, exactly as the page's own lookup leaves it: an empty gap in a sentence
        /// reads as a wording mistake somebody lives with, and a literal <c>{count}</c> on
        /// screen gets reported the same day.
        /// </summary>
        public string Say(string id, params (string Name, object Value)[] args)
        {
            if (string.IsNullOrWhiteSpace(id)) return string.Empty;

            var text = Words(id, args);
            return text == null ? id : Fill(text, args);
        }

        /// <summary>True when this catalog, or the English behind it, has words for this id.</summary>
        public bool Has(string id) => !string.IsNullOrWhiteSpace(id) && Words(id, null) != null;

        /// <summary>
        /// The words for an id, or null when nothing on either side has any.
        /// <para>
        /// The English is asked SECOND, not only when the pack has never heard of the id. A
        /// pack is the untrusted half of this exchange: it is a file downloaded from a
        /// release page, and a half filled translation file carries the id with nothing
        /// inside it. Falling back on a missing KEY alone would answer that with the id
        /// itself, and the id is what then goes into a Discord post or an RCON broadcast.
        /// An entry the pack carries but cannot answer from is the same thing as one it
        /// never carried, and it is answered the same way.
        /// </para>
        /// <para>
        /// The language handed to <see cref="Resolve"/> is the language of the words being
        /// read rather than of the catalog reading them, because a line that fell through to
        /// the English was written for English plural rules.
        /// </para>
        /// </summary>
        private string Words(string id, (string Name, object Value)[] args)
        {
            if (Entries.TryGetValue(id, out var mine) && mine != null)
            {
                var text = Resolve(mine, Code, args);
                if (text != null) return text;
            }

            if (ReferenceEquals(this, EmbeddedEnglish)) return null;

            return EmbeddedEnglish.Entries.TryGetValue(id, out var english) && english != null
                ? Resolve(english, LanguageCodes.English, args)
                : null;
        }

        /// <summary>
        /// The value inside an entry: a pack's own translation when it carries one, and the
        /// English lore otherwise. The plain register is deliberately not consulted. It is the
        /// host's own switch for the words in their interface, and the people on the server
        /// never see that switch or the interface it belongs to.
        /// </summary>
        private static string Resolve(JObject entry, string language, (string Name, object Value)[] args)
        {
            var value = entry["translation"] ?? entry["lore"];
            if (value == null || value.Type == JTokenType.Null) return null;

            if (value.Type == JTokenType.Object)
            {
                var slot = (string)entry["plural"];
                var counted = CountFor(slot, args);
                var forms = (JObject)value;
                var picked = forms[PluralCategory(language, counted)] ?? forms["other"];
                return (string)picked;
            }

            return (string)value;
        }

        private static double CountFor(string slot, (string Name, object Value)[] args)
        {
            if (args == null) return double.NaN;

            foreach (var (name, value) in args)
            {
                var wanted = string.Equals(name, "pluralValue", StringComparison.Ordinal) ||
                             (!string.IsNullOrEmpty(slot) && string.Equals(name, slot, StringComparison.Ordinal));
                if (!wanted || value == null) continue;

                try
                {
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    return double.NaN;
                }
            }

            return double.NaN;
        }

        /// <summary>
        /// The CLDR plural category for a number in a language. The browser asks
        /// Intl.PluralRules for this and there is no Intl on this side, so the rules for the
        /// five languages the product is planned in are written out. They are the published
        /// CLDR rules for integers, which is every count a host-facing sentence carries: a
        /// countdown announces whole minutes and a post counts whole mods.
        /// </summary>
        internal static string PluralCategory(string code, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "other";

            var language = LanguageCodes.Normalize(code) ?? LanguageCodes.English;

            // Japanese and both Chinese scripts have one form for every count.
            if (language is "ja" or "zh-Hans" or "zh-Hant") return "other";

            var whole = Math.Abs(value) == Math.Floor(Math.Abs(value));
            var n = (long)Math.Abs(value);

            if (string.Equals(language, "ru", StringComparison.Ordinal))
            {
                if (!whole) return "other";

                var last = n % 10;
                var lastTwo = n % 100;

                if (last == 1 && lastTwo != 11) return "one";
                if (last >= 2 && last <= 4 && (lastTwo < 12 || lastTwo > 14)) return "few";
                return "many";
            }

            return whole && n == 1 ? "one" : "other";
        }

        private static string Fill(string text, (string Name, object Value)[] args)
        {
            if (args == null || args.Length == 0 || text.IndexOf('{') < 0) return text;

            return Slot.Replace(text, match =>
            {
                var name = match.Groups[1].Value;
                foreach (var (slot, value) in args)
                {
                    if (string.Equals(slot, name, StringComparison.Ordinal))
                        return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
                }

                return match.Value;
            });
        }

        private static HostCatalog ReadEmbeddedEnglish()
        {
            try
            {
                using var stream = typeof(HostCatalog).Assembly.GetManifestResourceStream(EnglishResourceName);
                if (stream == null) return new HostCatalog(LanguageCodes.English, new Dictionary<string, JObject>(StringComparer.Ordinal));

                using var reader = new StreamReader(stream, Encoding.UTF8);
                return new HostCatalog(LanguageCodes.English, ReadHostKeys(reader.ReadToEnd(), countAll: true));
            }
            catch (Exception)
            {
                // A catalog that will not load must never be what stops the app opening. Every
                // id then answers with itself, which is ugly and is still a running server.
                return new HostCatalog(LanguageCodes.English, new Dictionary<string, JObject>(StringComparer.Ordinal));
            }
        }

        /// <summary>
        /// The host half of a catalog file. Only the host ids are kept: the interface's
        /// thousand and a half entries are the browser's to hold, and this side has no use
        /// for them beyond counting them.
        /// </summary>
        private static Dictionary<string, JObject> ReadHostKeys(string json, bool countAll = false)
        {
            var kept = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var keys = JObject.Parse(json)["keys"] as JObject;
            if (keys == null) return kept;

            if (countAll) EnglishKeyCount = keys.Count;

            foreach (var pair in keys)
            {
                if (!pair.Key.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                if (pair.Value is JObject entry) kept[pair.Key] = entry;
            }

            return kept;
        }
    }
}
