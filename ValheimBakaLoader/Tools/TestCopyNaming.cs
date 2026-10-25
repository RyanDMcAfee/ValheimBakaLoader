using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The name a test copy is made under (1.3.0, issue 27).
    /// <para>
    /// Before 1.3.0 a copy was offered under its source's PROFILE name, so a realm the host knows
    /// as "Baka Gaijin" in game and on the title bar came back as "Default_bakaT", a name the host
    /// had never read anywhere but the chip. The copy is now offered under the source's in-game
    /// name, made safe for a folder because the realm name becomes the copy's install and save
    /// folder names: letters of any script with their accents, decimal digits, space, '_' and '-'
    /// are kept, everything else is dropped, and the ends are trimmed. A source whose in-game name
    /// keeps nothing falls back to its profile name, made safe the same way.
    /// </para>
    /// <para>
    /// The page offers the same name in its dialog (testCopyName in app.js). Both sides have to
    /// agree to the character, or the dialog would open on a name the host then changes, so the
    /// rule is written out for the page in one line: NFC first, then drop every character that is
    /// not <c>\p{L}</c>, <c>\p{M}</c>, <c>\p{Nd}</c>, space, '_' or '-', then trim. A taken name
    /// gets 2, 3 and on after the suffix, compared without regard to case against every stored
    /// realm, archived ones included, which is the comparison servers.create refuses a clash by.
    /// </para>
    /// </summary>
    public static class TestCopyNaming
    {
        /// <summary>The suffix a test copy's realm, world and character are made under.</summary>
        public const string Suffix = CharacterCopy.Suffix;

        /// <summary>
        /// <paramref name="name"/> with only what a folder name can hold without surprises:
        /// letters (any script, with their combining marks), decimal digits, space, '_' and
        /// '-'. Normalised to NFC first, so an accent typed as a separate mark stays on its letter
        /// rather than being read as two characters. Never null.
        /// </summary>
        public static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            string composed;
            try { composed = name.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { composed = name; } // a lone surrogate cannot be normalised; it is dropped below

            var kept = new StringBuilder(composed.Length);
            foreach (var rune in composed.EnumerateRunes())
            {
                if (Keeps(rune)) kept.Append(rune.ToString());
            }
            return kept.ToString().Trim(' ');
        }

        private static bool Keeps(Rune rune)
        {
            if (rune.Value == ' ' || rune.Value == '_' || rune.Value == '-') return true;
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.DecimalDigitNumber:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// What a test copy's name is built on: the source's in-game name made safe, or its
        /// profile name made safe when the in-game name keeps nothing (a fresh realm has none).
        /// Can be empty only when both keep nothing, and then the copy is the suffix alone.
        /// </summary>
        public static string BaseFor(string sourceName, string sourceProfile)
        {
            var fromName = Clean(sourceName);
            return fromName.Length > 0 ? fromName : Clean(sourceProfile);
        }

        /// <summary>
        /// The name a test copy of a realm is offered under: its base and the suffix, or the
        /// suffix with 2, 3 and on when a stored realm already has that name in any case. The
        /// count has no ceiling, so the answer is always a free name.
        /// </summary>
        public static string Propose(string sourceName, string sourceProfile, IEnumerable<string> taken)
        {
            var used = new HashSet<string>(
                (taken ?? Enumerable.Empty<string>()).Where(n => n != null).Select(n => n.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var first = BaseFor(sourceName, sourceProfile) + Suffix;
            if (!used.Contains(first)) return first;
            for (var n = 2; ; n++)
            {
                var next = first + n.ToString(CultureInfo.InvariantCulture);
                if (!used.Contains(next)) return next;
            }
        }

        /// <summary>
        /// The name servers.create makes a test copy under. The name the page sent, held to the
        /// same rule the page offers names by (so a name the dialog offered comes through as it
        /// was); or, when nothing is left of it, the name the host would have offered. A name that
        /// is taken is not renumbered here: the host typed or accepted it, and the clash is refused
        /// by name rather than quietly turned into another.
        /// </summary>
        public static string NameFor(string requested, string sourceName, string sourceProfile, IEnumerable<string> taken)
        {
            var cleaned = Clean(requested);
            return cleaned.Length > 0 ? cleaned : Propose(sourceName, sourceProfile, taken);
        }
    }
}
