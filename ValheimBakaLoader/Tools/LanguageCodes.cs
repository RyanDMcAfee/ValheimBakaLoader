using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// One language BakaLoader knows how to show itself in.
    /// </summary>
    public sealed class LanguageCode
    {
        /// <summary>
        /// The one spelling used everywhere: the saved preference, the folder on disk, the
        /// code in a manifest, the segment in a pack URL and the html lang attribute. Asset
        /// file names are lower case, which is a property of the file name and never of this.
        /// </summary>
        public string Code { get; init; }

        /// <summary>What the language calls itself. This is what the menu lists.</summary>
        public string NativeName { get; init; }

        /// <summary>What English calls it, for logs and for the accessible label.</summary>
        public string EnglishName { get; init; }

        /// <summary>
        /// The BCP 47 tag handed to Intl for dates, numbers and plural categories. It is
        /// spelled the same as <see cref="Code"/> today and is kept separate because the
        /// two answer different questions, and one of them may grow a region subtag.
        /// </summary>
        public string Bcp47 { get; init; }

        /// <summary>
        /// Which script the pack's faces cover: latin, cyrillic, jp, sc or tc. The page
        /// keys its own stacks on the language itself (app.css, html[data-lang=...]);
        /// this names the shape of the fonts a pack for this language carries, which is
        /// what the packaging step subsets against and what two languages can share.
        /// </summary>
        public string FontStackKey { get; init; }

        /// <summary>True for the language that ships inside the app and is never downloaded.</summary>
        public bool BuiltIn => LanguageCodes.IsEnglish(Code);
    }

    /// <summary>
    /// The languages the app knows about without asking anybody. The list is compiled in on
    /// purpose: the globe menu draws instantly and completely on a machine with no connection,
    /// and a manifest only ever refines a row that is already there with a byte count and a
    /// translation count.
    /// <para>
    /// The pseudo locale "xx" is deliberately absent. It is a test fixture, not a language:
    /// every sentence bracketed and padded so a surface that missed the switch shows up on
    /// sight. It is reachable from the mock preview and from the suite, and a host has no use
    /// for it, so it is never listed and never offered.
    /// </para>
    /// </summary>
    public static class LanguageCodes
    {
        /// <summary>The language that ships inside the app.</summary>
        public const string English = "en";

        /// <summary>The pseudo locale, named here only so nothing has to spell it inline.</summary>
        public const string Pseudo = "xx";

        private static readonly LanguageCode[] All =
        {
            new()
            {
                Code = "en",
                NativeName = "English",
                EnglishName = "English",
                Bcp47 = "en",
                FontStackKey = "latin",
            },
            // The native names are written as escapes rather than as the letters themselves so
            // this file stays plain ASCII on disk. A source file with no byte order mark can
            // be read in the machine's own code page, and a name that only renders correctly
            // on the machine it was typed on is the one thing a language list cannot afford.
            new()
            {
                Code = "ru",
                NativeName = "\u0420\u0443\u0441\u0441\u043a\u0438\u0439",   // Russkiy
                EnglishName = "Russian",
                Bcp47 = "ru",
                FontStackKey = "cyrillic",
            },
            new()
            {
                Code = "ja",
                NativeName = "\u65e5\u672c\u8a9e",                           // Nihongo
                EnglishName = "Japanese",
                Bcp47 = "ja",
                FontStackKey = "jp",
            },
            new()
            {
                Code = "zh-Hans",
                NativeName = "\u7b80\u4f53\u4e2d\u6587",                     // Simplified Chinese
                EnglishName = "Simplified Chinese",
                Bcp47 = "zh-Hans",
                FontStackKey = "sc",
            },
            new()
            {
                Code = "zh-Hant",
                NativeName = "\u7e41\u9ad4\u4e2d\u6587",                     // Traditional Chinese
                EnglishName = "Traditional Chinese",
                Bcp47 = "zh-Hant",
                FontStackKey = "tc",
            },
        };

        /// <summary>Every language the menu lists, in the order it lists them.</summary>
        public static IReadOnlyList<LanguageCode> Known => All;

        /// <summary>True when this is the language that ships inside the app.</summary>
        public static bool IsEnglish(string code) =>
            string.Equals(code?.Trim(), English, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The one canonical spelling of a code, or null when nothing on the list answers to
        /// it. Windows folder names are case preserving and not case sensitive, and a manifest
        /// cut by hand can say "zh-hans", so a code is matched without regard to case and
        /// always handed back in the spelling the rest of the app uses.
        /// </summary>
        public static string Normalize(string code)
        {
            var trimmed = code?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;

            return All.FirstOrDefault(l =>
                string.Equals(l.Code, trimmed, StringComparison.OrdinalIgnoreCase))?.Code;
        }

        /// <summary>The entry for a code, or null when it is not one of ours.</summary>
        public static LanguageCode Find(string code)
        {
            var normalized = Normalize(code);
            return normalized == null
                ? null
                : All.First(l => string.Equals(l.Code, normalized, StringComparison.Ordinal));
        }

        /// <summary>True when the code is one the app knows.</summary>
        public static bool IsKnown(string code) => Normalize(code) != null;
    }
}
